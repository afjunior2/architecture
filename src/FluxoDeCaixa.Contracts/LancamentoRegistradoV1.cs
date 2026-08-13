namespace FluxoDeCaixa.Contracts;

/// <summary>
/// Contrato do evento de integração. É a linguagem publicada entre Lançamentos e Consolidado.
/// Evolução compatível (campo novo opcional) mantém a versão. Evolução incompatível publica
/// lancamento.registrado.v2 em paralelo durante a janela de convivência.
/// O evento carrega o estado completo (event-carried state transfer): o consumidor não
/// precisa consultar o serviço de origem para projetar.
/// </summary>
public sealed record LancamentoRegistradoV1
{
    public const string TipoEvento = "lancamento.registrado";
    public const int Versao = 1;
    public const string RoutingKey = "lancamento.registrado";

    /// <summary>Identidade do evento. Chave de deduplicação no consumidor.</summary>
    public required Guid IdEvento { get; init; }

    /// <summary>Propagado da requisição HTTP original para rastreio ponta a ponta.</summary>
    public string? IdCorrelacao { get; init; }

    public required DateTimeOffset OcorridoEm { get; init; }

    public required Guid IdLancamento { get; init; }
    public required Guid MerchantId { get; init; }

    /// <summary>1 = Crédito, 2 = Débito.</summary>
    public required int Tipo { get; init; }

    public required decimal Valor { get; init; }

    /// <summary>Data à qual o lançamento pertence. É a chave de agrupamento do consolidado.</summary>
    public required DateOnly Data { get; init; }

    public required DateTimeOffset RegistradoEm { get; init; }
}
