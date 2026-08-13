using FluentAssertions;
using FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;
using FluxoDeCaixa.Contracts;
using FluxoDeCaixa.Lancamentos.Domain;
using Npgsql;
using Xunit;

namespace FluxoDeCaixa.IntegrationTests;

[Collection("ambiente")]
public class ProjecaoIdempotenteTests(AmbienteFixture ambiente)
{
    private static LancamentoRegistradoV1 Evento(Guid merchant, decimal valor, int tipo = 1) => new()
    {
        IdEvento = GuidV7.Novo(),
        OcorridoEm = DateTimeOffset.UtcNow,
        IdLancamento = GuidV7.Novo(),
        MerchantId = merchant,
        Tipo = tipo,
        Valor = valor,
        Data = new DateOnly(2026, 08, 13),
        RegistradoEm = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Evento_entregue_duas_vezes_tem_efeito_unico()
    {
        // Simula o cenário de redelivery: o consumidor processou, falhou antes do ACK
        // e o broker reentregou. A deduplicação por id do evento impede efeito duplo.
        await using var ds = NpgsqlDataSource.Create(ambiente.ConnString);
        await EsquemaDoConsolidado.GarantirAsync(ds);
        var projecao = new ProjecaoDeConsolidado(ds);
        var merchant = Guid.NewGuid();
        var evento = Evento(merchant, 100.00m);

        var primeira = await projecao.ProcessarAsync(evento, CancellationToken.None);
        var segunda = await projecao.ProcessarAsync(evento, CancellationToken.None);

        primeira.Should().Be(ResultadoDaProjecao.Processado);
        segunda.Should().Be(ResultadoDaProjecao.Duplicado);

        var creditos = await ambiente.ConsultarAsync<decimal>(
            "SELECT total_creditos FROM consolidado.consolidado_diario WHERE merchant_id = @merchant",
            new { merchant });
        creditos.Should().Be(100.00m, "o saldo não pode duplicar por reentrega do broker");
    }

    [Fact]
    public async Task Recalculo_e_indiferente_a_ordem_e_a_lancamento_retroativo()
    {
        await using var ds = NpgsqlDataSource.Create(ambiente.ConnString);
        await EsquemaDoConsolidado.GarantirAsync(ds);
        var projecao = new ProjecaoDeConsolidado(ds);
        var merchant = Guid.NewGuid();

        // Chegam fora de ordem: crédito de hoje, depois débito retroativo do mesmo dia.
        await projecao.ProcessarAsync(Evento(merchant, 500.00m, tipo: 1), CancellationToken.None);
        await projecao.ProcessarAsync(Evento(merchant, 120.50m, tipo: 2), CancellationToken.None);
        await projecao.ProcessarAsync(Evento(merchant, 200.00m, tipo: 1), CancellationToken.None);

        var saldo = await ambiente.ConsultarAsync<decimal>(
            "SELECT saldo FROM consolidado.consolidado_diario WHERE merchant_id = @merchant", new { merchant });
        var quantidade = await ambiente.ConsultarAsync<int>(
            "SELECT quantidade FROM consolidado.consolidado_diario WHERE merchant_id = @merchant", new { merchant });

        saldo.Should().Be(579.50m);
        quantidade.Should().Be(3);
    }
}
