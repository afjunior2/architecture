using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FluxoDeCaixa.IntegrationTests;

[Collection("ambiente")]
public class OutboxEIdempotenciaTests(AmbienteFixture ambiente)
{
    private static HttpRequestMessage Requisicao(Guid merchant, string chave, string tipo = "CREDITO", decimal valor = 100.50m)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/lancamentos")
        {
            Content = JsonContent.Create(new { tipo, valor, descricao = "teste" })
        };
        req.Headers.Add("X-Merchant-Id", merchant.ToString());
        req.Headers.Add("Idempotency-Key", chave);
        return req;
    }

    [Fact]
    public async Task Lancamento_e_outbox_sao_gravados_na_mesma_transacao()
    {
        // Outbox publisher desligado: queremos observar a linha pendente.
        await using var api = ambiente.CriarApiDeLancamentos(outboxHabilitada: false);
        using var client = api.CreateClient();
        var merchant = Guid.NewGuid();

        var resposta = await client.SendAsync(Requisicao(merchant, "chave-outbox-1"));

        resposta.StatusCode.Should().Be(HttpStatusCode.Created);
        var corpo = await resposta.Content.ReadFromJsonAsync<RespostaLancamento>();

        var lancamentos = await ambiente.ConsultarAsync<int>(
            "SELECT COUNT(*) FROM lancamentos.lancamentos WHERE merchant_id = @merchant", new { merchant });
        var outboxPendentes = await ambiente.ConsultarAsync<int>(
            """
            SELECT COUNT(*) FROM lancamentos.outbox o
            WHERE o.publicado_em IS NULL AND o.payload->>'idLancamento' = @id
            """, new { id = corpo!.Id.ToString() });

        lancamentos.Should().Be(1);
        outboxPendentes.Should().Be(1, "a intenção de publicação nasce na mesma transação do lançamento");
    }

    [Fact]
    public async Task Requisicoes_com_a_mesma_chave_de_idempotencia_geram_um_unico_lancamento()
    {
        await using var api = ambiente.CriarApiDeLancamentos(outboxHabilitada: false);
        using var client = api.CreateClient();
        var merchant = Guid.NewGuid();

        var primeira = await client.SendAsync(Requisicao(merchant, "mesma-chave"));
        var segunda = await client.SendAsync(Requisicao(merchant, "mesma-chave"));

        primeira.StatusCode.Should().Be(HttpStatusCode.Created);
        segunda.StatusCode.Should().Be(HttpStatusCode.OK, "repetição devolve a mesma resposta lógica, não um erro");

        var idPrimeira = (await primeira.Content.ReadFromJsonAsync<RespostaLancamento>())!.Id;
        var idSegunda = (await segunda.Content.ReadFromJsonAsync<RespostaLancamento>())!.Id;
        idSegunda.Should().Be(idPrimeira);

        (await ambiente.ConsultarAsync<int>(
            "SELECT COUNT(*) FROM lancamentos.lancamentos WHERE merchant_id = @merchant", new { merchant }))
            .Should().Be(1);
        (await ambiente.ConsultarAsync<int>(
            "SELECT COUNT(*) FROM lancamentos.outbox WHERE payload->>'merchantId' = @m", new { m = merchant.ToString() }))
            .Should().Be(1, "nenhum evento financeiro duplicado pode nascer de retry do cliente");
    }

    [Fact]
    public async Task Mesma_chave_em_merchants_diferentes_nao_colide()
    {
        await using var api = ambiente.CriarApiDeLancamentos(outboxHabilitada: false);
        using var client = api.CreateClient();
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();

        var a = await client.SendAsync(Requisicao(merchantA, "chave-compartilhada", valor: 10.00m));
        var b = await client.SendAsync(Requisicao(merchantB, "chave-compartilhada", valor: 999.00m));

        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created,
            "a chave é escopada por merchant; chave global vazaria resposta entre tenants");

        var valorB = (await b.Content.ReadFromJsonAsync<RespostaLancamento>())!.Valor;
        valorB.Should().Be(999.00m);
    }

    [Fact]
    public async Task Valor_invalido_retorna_422_com_codigo_estavel()
    {
        await using var api = ambiente.CriarApiDeLancamentos(outboxHabilitada: false);
        using var client = api.CreateClient();

        var resposta = await client.SendAsync(Requisicao(Guid.NewGuid(), "chave-invalida", valor: -5m));

        resposta.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var corpo = await resposta.Content.ReadAsStringAsync();
        corpo.Should().Contain("valor_invalido");
    }

    private sealed record RespostaLancamento(Guid Id, Guid MerchantId, string Tipo, decimal Valor);
}
