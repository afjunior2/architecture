namespace FluxoDeCaixa.Lancamentos.Domain;

/// <summary>
/// Violação de regra de negócio na entrada. A API traduz para 422 com o código estável,
/// que faz parte do contrato (clientes podem tratar por código, não por mensagem).
/// </summary>
public sealed class DominioInvalidoException(string codigo, string mensagem) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
}

public static class ErrosDeDominio
{
    public const string ValorInvalido = "valor_invalido";
    public const string ValorComMaisDeDuasCasas = "valor_com_mais_de_duas_casas";
    public const string TipoInvalido = "tipo_invalido";
    public const string DataFutura = "data_futura";
    public const string DescricaoMuitoLonga = "descricao_muito_longa";
    public const string MerchantObrigatorio = "merchant_obrigatorio";
}
