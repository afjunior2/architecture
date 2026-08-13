using FluentAssertions;
using FluxoDeCaixa.Lancamentos.Domain;
using Xunit;

namespace FluxoDeCaixa.UnitTests;

/// <summary>Relógio fixo: as regras de data ficam determinísticas.</summary>
public sealed class RelogioFixo(DateOnly hoje) : IRelogio
{
    public DateTimeOffset Agora { get; } = new(hoje.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    public DateOnly Hoje { get; } = hoje;
}

public class LancamentoTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly RelogioFixo Relogio = new(new DateOnly(2026, 08, 13));

    [Fact]
    public void Registra_credito_valido()
    {
        var l = Lancamento.Registrar(Merchant, TipoLancamento.Credito, 150.50m, "Venda balcão", null, Relogio);

        l.Id.Should().NotBeEmpty();
        l.MerchantId.Should().Be(Merchant);
        l.Tipo.Should().Be(TipoLancamento.Credito);
        l.Valor.Should().Be(150.50m);
        l.Data.Should().Be(Relogio.Hoje, "sem data informada, o lançamento pertence ao dia corrente");
        l.RegistradoEm.Should().Be(Relogio.Agora);
    }

    [Fact]
    public void Registra_debito_valido_com_data_retroativa()
    {
        var ontem = Relogio.Hoje.AddDays(-1);

        var l = Lancamento.Registrar(Merchant, TipoLancamento.Debito, 99.99m, null, ontem, Relogio);

        l.Tipo.Should().Be(TipoLancamento.Debito);
        l.Data.Should().Be(ontem, "fechamento de caixa do dia anterior é caso real");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-1000)]
    public void Rejeita_valor_zero_ou_negativo(decimal valor)
    {
        var acao = () => Lancamento.Registrar(Merchant, TipoLancamento.Credito, valor, null, null, Relogio);

        acao.Should().Throw<DominioInvalidoException>()
            .Which.Codigo.Should().Be(ErrosDeDominio.ValorInvalido);
    }

    [Fact]
    public void Rejeita_valor_com_mais_de_duas_casas_decimais()
    {
        var acao = () => Lancamento.Registrar(Merchant, TipoLancamento.Credito, 10.999m, null, null, Relogio);

        acao.Should().Throw<DominioInvalidoException>()
            .Which.Codigo.Should().Be(ErrosDeDominio.ValorComMaisDeDuasCasas);
    }

    [Fact]
    public void Rejeita_data_futura()
    {
        var amanha = Relogio.Hoje.AddDays(1);

        var acao = () => Lancamento.Registrar(Merchant, TipoLancamento.Debito, 10m, null, amanha, Relogio);

        acao.Should().Throw<DominioInvalidoException>()
            .Which.Codigo.Should().Be(ErrosDeDominio.DataFutura);
    }

    [Fact]
    public void Rejeita_merchant_vazio()
    {
        var acao = () => Lancamento.Registrar(Guid.Empty, TipoLancamento.Credito, 10m, null, null, Relogio);

        acao.Should().Throw<DominioInvalidoException>()
            .Which.Codigo.Should().Be(ErrosDeDominio.MerchantObrigatorio);
    }

    [Fact]
    public void Rejeita_tipo_indefinido()
    {
        var acao = () => Lancamento.Registrar(Merchant, (TipoLancamento)99, 10m, null, null, Relogio);

        acao.Should().Throw<DominioInvalidoException>()
            .Which.Codigo.Should().Be(ErrosDeDominio.TipoInvalido);
    }

    [Fact]
    public void Rejeita_descricao_acima_de_200_caracteres()
    {
        var descricao = new string('x', 201);

        var acao = () => Lancamento.Registrar(Merchant, TipoLancamento.Credito, 10m, descricao, null, Relogio);

        acao.Should().Throw<DominioInvalidoException>()
            .Which.Codigo.Should().Be(ErrosDeDominio.DescricaoMuitoLonga);
    }

    [Fact]
    public void Ids_gerados_sao_uuid_v7_ordenados_no_tempo()
    {
        var antes = GuidV7.Novo(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var depois = GuidV7.Novo(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        string.Compare(antes.ToString(), depois.ToString(), StringComparison.Ordinal)
            .Should().BeNegative("uuid v7 preserva ordem temporal, o que evita fragmentação do índice");
    }
}
