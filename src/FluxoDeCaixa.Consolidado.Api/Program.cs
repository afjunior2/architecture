using System.Diagnostics;
using FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

DateOnlyTypeHandler.Registrar();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithProperty("servico", "consolidado-api")
    .WriteTo.Console(new CompactJsonFormatter()));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres não configurada.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ConsultaDeConsolidado>();
builder.Services.AddHealthChecks();

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddPrometheusExporter());

var app = builder.Build();

// A API também garante o schema: ela pode subir antes do worker e não deve
// falhar consulta de dia sem movimento por tabela inexistente.
await EsquemaDoConsolidado.GarantirAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.UseSerilogRequestLogging();
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapGet("/health/ready", async (NpgsqlDataSource ds, CancellationToken ct) =>
{
    try
    {
        await using var cmd = ds.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "Healthy" });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "Unhealthy" }, statusCode: 503);
    }
});

app.MapGet("/api/v1/consolidado/{data}", async (
    string data,
    [FromHeader(Name = "X-Merchant-Id")] Guid? merchantId,
    ConsultaDeConsolidado consulta,
    CancellationToken ct) =>
{
    if (merchantId is null || merchantId == Guid.Empty)
        return Results.Problem(statusCode: 400, title: "header_obrigatorio",
            detail: "O header X-Merchant-Id é obrigatório.");

    if (!DateOnly.TryParseExact(data, "yyyy-MM-dd", out var dataConsulta))
        return Results.Problem(statusCode: 400, title: "data_invalida",
            detail: "A data deve estar no formato yyyy-MM-dd.");

    var consolidado = await consulta.ObterAsync(merchantId.Value, dataConsulta, ct);

    // atualizadoEm e consistencia expõem o frescor: consistência eventual não
    // documentada na resposta é consistência eventual escondida do usuário.
    return Results.Ok(new
    {
        merchantId = consolidado.MerchantId,
        data = consolidado.Data,
        totalCreditos = consolidado.TotalCreditos,
        totalDebitos = consolidado.TotalDebitos,
        saldo = consolidado.Saldo,
        quantidadeLancamentos = consolidado.Quantidade,
        atualizadoEm = consolidado.AtualizadoEm,
        consistencia = "eventual",
        traceId = Activity.Current?.TraceId.ToString()
    });
});

app.Run();

public partial class Program { } // exposto para WebApplicationFactory nos testes de integração

namespace FluxoDeCaixa.Consolidado.Api
{
    /// <summary>Âncora de assembly para WebApplicationFactory.</summary>
    public sealed class MarcadorDaApiDeConsolidado { }
}
