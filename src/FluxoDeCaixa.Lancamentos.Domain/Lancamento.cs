namespace FluxoDeCaixa.Lancamentos.Domain;

/// <summary>
/// Raiz de agregado. A fronteira é o próprio lançamento: não existe invariante que
/// exija dois lançamentos consistentes entre si, então cada escrita é uma transação
/// curta e sem contenção. O saldo não pertence a este agregado, é projeção do Consolidado.
///
/// O lançamento é imutável depois de criado (sem setter público, sem update no banco).
/// Correção de erro entra como evolução futura via estorno compensatório, nunca como edição.
/// </summary>
public sealed class Lancamento
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public TipoLancamento Tipo { get; private set; }
    public decimal Valor { get; private set; }
    public string Descricao { get; private set; } = string.Empty;

    /// <summary>Data à qual o lançamento pertence economicamente (competência), não a data do registro.</summary>
    public DateOnly Data { get; private set; }

    public DateTimeOffset RegistradoEm { get; private set; }

    private Lancamento() { } // EF Core

    /// <summary>Único caminho de criação. Não existe instância inválida deste tipo.</summary>
    public static Lancamento Registrar(
        Guid merchantId,
        TipoLancamento tipo,
        decimal valor,
        string? descricao,
        DateOnly? data,
        IRelogio relogio)
    {
        if (merchantId == Guid.Empty)
            throw new DominioInvalidoException(ErrosDeDominio.MerchantObrigatorio,
                "O identificador do merchant é obrigatório.");

        if (!Enum.IsDefined(tipo))
            throw new DominioInvalidoException(ErrosDeDominio.TipoInvalido,
                "O tipo do lançamento deve ser CREDITO ou DEBITO.");

        if (valor <= 0)
            throw new DominioInvalidoException(ErrosDeDominio.ValorInvalido,
                "O valor do lançamento deve ser maior que zero. A direção do dinheiro é expressa pelo tipo, não pelo sinal.");

        if (decimal.Round(valor, 2) != valor)
            throw new DominioInvalidoException(ErrosDeDominio.ValorComMaisDeDuasCasas,
                "O valor do lançamento deve ter no máximo duas casas decimais.");

        var dataEfetiva = data ?? relogio.Hoje;
        if (dataEfetiva > relogio.Hoje)
            throw new DominioInvalidoException(ErrosDeDominio.DataFutura,
                "Não é possível registrar lançamento com data futura.");

        var descricaoEfetiva = descricao?.Trim() ?? string.Empty;
        if (descricaoEfetiva.Length > 200)
            throw new DominioInvalidoException(ErrosDeDominio.DescricaoMuitoLonga,
                "A descrição deve ter no máximo 200 caracteres.");

        return new Lancamento
        {
            Id = GuidV7.Novo(),
            MerchantId = merchantId,
            Tipo = tipo,
            Valor = valor,
            Descricao = descricaoEfetiva,
            Data = dataEfetiva,
            RegistradoEm = relogio.Agora
        };
    }
}
