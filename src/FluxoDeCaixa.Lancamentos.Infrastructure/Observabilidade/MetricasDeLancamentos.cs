using System.Diagnostics.Metrics;

namespace FluxoDeCaixa.Lancamentos.Infrastructure.Observabilidade;

/// <summary>
/// Métricas do serviço de Lançamentos. As duas observáveis de outbox respondem
/// à pergunta operacional que importa: existe backlog e há quanto tempo?
/// </summary>
public static class MetricasDeLancamentos
{
    public const string NomeDoMeter = "FluxoDeCaixa.Lancamentos";
    private static readonly Meter Meter = new(NomeDoMeter);

    public static readonly Counter<long> LancamentosCriados =
        Meter.CreateCounter<long>("lancamentos_created_total");

    public static readonly Counter<long> LancamentosRejeitados =
        Meter.CreateCounter<long>("lancamentos_failed_total");

    public static readonly Counter<long> FalhasDePublicacao =
        Meter.CreateCounter<long>("outbox_publish_failures_total");

    internal static double OutboxPendentes;
    internal static double OutboxMensagemMaisAntigaSegundos;

    static MetricasDeLancamentos()
    {
        Meter.CreateObservableGauge("outbox_pending_total", () => OutboxPendentes);
        Meter.CreateObservableGauge("outbox_oldest_message_seconds", () => OutboxMensagemMaisAntigaSegundos);
    }
}
