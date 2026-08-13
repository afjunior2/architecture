using FluxoDeCaixa.Contracts;
using FluxoDeCaixa.Lancamentos.Infrastructure.Observabilidade;
using FluxoDeCaixa.Lancamentos.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxoDeCaixa.Lancamentos.Infrastructure.Mensageria;

/// <summary>
/// Polling publisher da outbox. Roda dentro da própria API como hosted service:
/// para o volume atual, um processo separado seria um contêiner a mais sem ganho.
///
/// FOR UPDATE SKIP LOCKED permite mais de uma réplica da API sem publicação duplicada
/// e sem leader election. A ordem de publicação não é garantida entre réplicas, e não
/// precisa ser: a projeção do consolidado é indiferente à ordem (ADR-004).
///
/// Falha na publicação aborta a transação: as linhas continuam pendentes e serão
/// tentadas de novo no próximo ciclo. A semântica resultante é at-least-once.
/// </summary>
public sealed class PublicadorDeOutbox(
    IServiceScopeFactory scopes,
    IPublicadorDeMensagens publicador,
    ILogger<PublicadorDeOutbox> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromMilliseconds(500);
    private const int TamanhoDoLote = 100;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Publicador de outbox iniciado. Intervalo {IntervaloMs} ms, lote {Lote}.",
            Intervalo.TotalMilliseconds, TamanhoDoLote);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var publicadas = await PublicarLoteAsync(ct);
                if (publicadas == 0)
                    await Task.Delay(Intervalo, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MetricasDeLancamentos.FalhasDePublicacao.Add(1);
                logger.LogWarning(ex, "Falha ao publicar lote da outbox. Linhas permanecem pendentes.");
                await Task.Delay(Intervalo, ct);
            }
        }
    }

    private async Task<int> PublicarLoteAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LancamentosDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pendentes = await db.Outbox
            .FromSqlRaw("""
                SELECT * FROM lancamentos.outbox
                WHERE publicado_em IS NULL
                ORDER BY ocorrido_em
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
                """, TamanhoDoLote)
            .ToListAsync(ct);

        // Backlog real, não o tamanho do lote: com o broker fora e milhares de linhas
        // acumuladas, é este número que o alerta precisa enxergar. O índice parcial
        // em publicado_em IS NULL mantém o COUNT barato.
        var totalPendente = await db.Outbox.CountAsync(o => o.PublicadoEm == null, ct);
        MetricasDeLancamentos.OutboxPendentes = totalPendente;
        MetricasDeLancamentos.OutboxMensagemMaisAntigaSegundos = pendentes.Count == 0
            ? 0
            : (DateTimeOffset.UtcNow - pendentes.Min(p => p.OcorridoEm)).TotalSeconds;

        if (pendentes.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        foreach (var mensagem in pendentes)
        {
            await publicador.PublicarAsync(
                LancamentoRegistradoV1.RoutingKey, mensagem.Payload, mensagem.Id, ct);
            mensagem.PublicadoEm = DateTimeOffset.UtcNow;
            mensagem.Tentativas++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Outbox: {Quantidade} mensagens publicadas.", pendentes.Count);
        return pendentes.Count;
    }
}
