using System.Diagnostics;
using FluxoDeCaixa.Lancamentos.Api;
using FluxoDeCaixa.Lancamentos.Application;
using FluxoDeCaixa.Lancamentos.Domain;
using FluxoDeCaixa.Lancamentos.Infrastructure.Mensageria;
using FluxoDeCaixa.Lancamentos.Infrastructure.Observabilidade;
using FluxoDeCaixa.Lancamentos.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados em JSON. Sem valor monetário, sem descrição, sem dado sensível.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithProperty("servico", "lancamentos-api")
    .WriteTo.Console(new CompactJsonFormatter()));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres não configurada.");

builder.Services.AddDbContext<LancamentosDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddSingleton<IRelogio, RelogioDoSistema>();
builder.Services.AddScoped<IArmazenamentoDeLancamentos, ArmazenamentoDeLancamentos>();
builder.Services.AddScoped<RegistrarLancamentoHandler>();

builder.Services.Configure<RabbitMqOpcoes>(builder.Configuration.GetSection(RabbitMqOpcoes.Secao));
builder.Services.AddSingleton<IPublicadorDeMensagens, PublicadorRabbitMq>();
if (!builder.Configuration.GetValue("Outbox:Desabilitado", false))
    builder.Services.AddHostedService<PublicadorDeOutbox>();

builder.Services.AddHealthChecks();

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddSource("FluxoDeCaixa.*")
        .AddOtlpExporterSeConfigurado(builder.Configuration))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddMeter(MetricasDeLancamentos.NomeDoMeter)
        .AddPrometheusExporter());

var app = builder.Build();

// Cria o schema no primeiro boot. Conveniência de ambiente local e de teste;
// em produção a migração é etapa do pipeline, nunca do startup (ver docs/operations-security.md).
// Não usamos EnsureCreated: ele decide por "o banco tem alguma tabela?", e num banco
// compartilhado com o schema do consolidado isso pula a criação. Verificamos a nossa
// tabela e criamos sob advisory lock para tolerar boots concorrentes.
if (!app.Configuration.GetValue("Persistencia:PularCriacao", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LancamentosDbContext>();
    var conexao = db.Database.GetDbConnection();
    await conexao.OpenAsync();
    await using (var cmd = conexao.CreateCommand())
    {
        cmd.CommandText = "SELECT pg_advisory_lock(4201)";
        await cmd.ExecuteNonQueryAsync();
    }
    try
    {
        bool tabelaExiste;
        await using (var cmd = conexao.CreateCommand())
        {
            cmd.CommandText = "SELECT to_regclass('lancamentos.lancamentos') IS NOT NULL";
            tabelaExiste = (bool)(await cmd.ExecuteScalarAsync())!;
        }
        if (!tabelaExiste)
        {
            var criador = db.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
            await criador.CreateTablesAsync();
        }
    }
    finally
    {
        await using var cmd = conexao.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(4201)";
        await cmd.ExecuteNonQueryAsync();
    }
}

app.UseSerilogRequestLogging();
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapGet("/health/ready", async (LancamentosDbContext db, CancellationToken ct) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        return Results.Ok(new { status = "Healthy" });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "Unhealthy" }, statusCode: 503);
    }
});

app.MapPost("/api/v1/lancamentos", async (
    [FromHeader(Name = "X-Merchant-Id")] Guid? merchantId,
    [FromHeader(Name = "Idempotency-Key")] string? chaveIdempotencia,
    [FromBody] RegistrarLancamentoRequisicao corpo,
    RegistrarLancamentoHandler handler,
    HttpContext http,
    CancellationToken ct) =>
{
    // MerchantId vem de header porque autenticação está fora do escopo do MVP.
    // Em produção ele é derivado do token, nunca de entrada controlada pelo cliente
    // (ver docs/operations-security.md).
    if (merchantId is null || merchantId == Guid.Empty)
        return Resultados.Problema(400, "header_obrigatorio", "O header X-Merchant-Id é obrigatório.");
    if (string.IsNullOrWhiteSpace(chaveIdempotencia) || chaveIdempotencia.Length > 100)
        return Resultados.Problema(400, "idempotency_key_obrigatoria",
            "O header Idempotency-Key é obrigatório e deve ter até 100 caracteres.");

    try
    {
        var resposta = await handler.ExecutarAsync(new RegistrarLancamentoComando(
            merchantId.Value, corpo.Tipo ?? string.Empty, corpo.Valor, corpo.Descricao, corpo.Data,
            chaveIdempotencia, Activity.Current?.TraceId.ToString()), ct);

        if (!resposta.Duplicado)
            MetricasDeLancamentos.LancamentosCriados.Add(1, new KeyValuePair<string, object?>("tipo", resposta.Tipo));

        // Requisição repetida devolve a mesma resposta lógica, com o mesmo id.
        return resposta.Duplicado
            ? Results.Ok(resposta)
            : Results.Created($"/api/v1/lancamentos/{resposta.Id}", resposta);
    }
    catch (DominioInvalidoException ex)
    {
        MetricasDeLancamentos.LancamentosRejeitados.Add(1,
            new KeyValuePair<string, object?>("codigo", ex.Codigo));
        return Resultados.Problema(422, ex.Codigo, ex.Message);
    }
});

app.MapGet("/api/v1/lancamentos/{id:guid}", async (
    Guid id,
    [FromHeader(Name = "X-Merchant-Id")] Guid? merchantId,
    IArmazenamentoDeLancamentos armazenamento,
    CancellationToken ct) =>
{
    if (merchantId is null || merchantId == Guid.Empty)
        return Resultados.Problema(400, "header_obrigatorio", "O header X-Merchant-Id é obrigatório.");

    var lancamento = await armazenamento.ObterPorIdAsync(id, ct);

    // 404 também quando o lançamento é de outro merchant: revelar a existência
    // de recurso alheio já é vazamento de informação.
    if (lancamento is null || lancamento.MerchantId != merchantId)
        return Results.NotFound();

    return Results.Ok(new LancamentoRegistradoResposta(
        lancamento.Id, lancamento.MerchantId, lancamento.Tipo.ToString().ToUpperInvariant(),
        lancamento.Valor, lancamento.Descricao, lancamento.Data, lancamento.RegistradoEm, Duplicado: false));
});

app.Run();

public partial class Program { } // exposto para WebApplicationFactory nos testes de integração

namespace FluxoDeCaixa.Lancamentos.Api
{
    /// <summary>Âncora de assembly para WebApplicationFactory (evita ambiguidade entre os Programs).</summary>
    public sealed class MarcadorDaApiDeLancamentos { }

    public sealed record RegistrarLancamentoRequisicao(string? Tipo, decimal Valor, string? Descricao, DateOnly? Data);

    public static class Resultados
    {
        public static IResult Problema(int status, string codigo, string detalhe) =>
            Results.Problem(statusCode: status, title: codigo, detail: detalhe,
                extensions: new Dictionary<string, object?>
                {
                    ["codigo"] = codigo,
                    ["traceId"] = Activity.Current?.TraceId.ToString()
                });
    }

    public static class OtlpExtensao
    {
        /// <summary>Exporta traces via OTLP apenas se o endpoint estiver configurado por ambiente.</summary>
        public static TracerProviderBuilder AddOtlpExporterSeConfigurado(
            this TracerProviderBuilder builder, IConfiguration config)
        {
            if (!string.IsNullOrEmpty(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                builder.AddOtlpExporter();
            return builder;
        }
    }
}
