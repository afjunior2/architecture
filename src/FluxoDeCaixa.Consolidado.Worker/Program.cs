using FluxoDeCaixa.Consolidado.Infrastructure.Mensageria;
using FluxoDeCaixa.Consolidado.Infrastructure.Observabilidade;
using FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Formatting.Compact;

DateOnlyTypeHandler.Registrar();

// O worker é um host web mínimo: o HTTP existe só para health checks e métricas.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithProperty("servico", "consolidado-worker")
    .WriteTo.Console(new CompactJsonFormatter()));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres não configurada.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ProjecaoDeConsolidado>();
builder.Services.Configure<RabbitMqOpcoes>(builder.Configuration.GetSection(RabbitMqOpcoes.Secao));
builder.Services.AddHostedService<ConsumidorDeLancamentos>();
builder.Services.AddHealthChecks();

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter(MetricasDeConsolidado.NomeDoMeter)
        .AddPrometheusExporter());

var app = builder.Build();

await EsquemaDoConsolidado.GarantirAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

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

app.Run();

public partial class Program { } // exposto para os testes de integração

namespace FluxoDeCaixa.Consolidado.Worker
{
    /// <summary>Âncora de assembly para WebApplicationFactory.</summary>
    public sealed class MarcadorDoWorker { }
}
