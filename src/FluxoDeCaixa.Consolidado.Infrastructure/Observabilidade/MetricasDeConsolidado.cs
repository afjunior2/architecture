using System.Diagnostics.Metrics;

namespace FluxoDeCaixa.Consolidado.Infrastructure.Observabilidade;

/// <summary>
/// As métricas respondem às duas perguntas operacionais deste serviço:
/// o consolidado está atrasado? Quanto? O contador de duplicados maior que zero
/// é sinal de saúde, não de problema: prova que a deduplicação está sendo exercitada.
/// </summary>
public static class MetricasDeConsolidado
{
    public const string NomeDoMeter = "FluxoDeCaixa.Consolidado";
    private static readonly Meter Meter = new(NomeDoMeter);

    public static readonly Counter<long> EventosProcessados =
        Meter.CreateCounter<long>("consolidado_events_processed_total");

    public static readonly Counter<long> EventosComFalha =
        Meter.CreateCounter<long>("consolidado_events_failed_total");

    public static readonly Counter<long> EventosDuplicados =
        Meter.CreateCounter<long>("consolidado_events_duplicated_total");

    public static readonly Histogram<double> LagDeProcessamento =
        Meter.CreateHistogram<double>("consolidado_processing_lag_seconds");
}
