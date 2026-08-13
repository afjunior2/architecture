using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace FluxoDeCaixa.IntegrationTests;

/// <summary>
/// Os dois testes que provam o requisito central do desafio: o registro de lançamentos
/// não depende da disponibilidade do consolidado nem da infraestrutura de mensageria.
/// </summary>
[Collection("ambiente")]
public class IndisponibilidadeTests(AmbienteFixture ambiente)
{
    private static HttpRequestMessage Requisicao(Guid merchant, string chave, string tipo, decimal valor)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/lancamentos")
        {
            Content = JsonContent.Create(new { tipo, valor })
        };
        req.Headers.Add("X-Merchant-Id", merchant.ToString());
        req.Headers.Add("Idempotency-Key", chave);
        return req;
    }

    [Fact]
    public async Task Worker_indisponivel_nao_afeta_lancamentos_e_o_consolidado_converge_depois()
    {
        var merchant = Guid.NewGuid();
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        // Fase 1: worker fora do ar. Só a API de Lançamentos (com outbox) e a de Consolidado.
        await using var apiLancamentos = ambiente.CriarApiDeLancamentos(outboxHabilitada: true);
        await using var apiConsolidado = ambiente.CriarApiDeConsolidado();
        using var clienteLancamentos = apiLancamentos.CreateClient();
        using var clienteConsolidado = apiConsolidado.CreateClient();

        for (var i = 0; i < 8; i++)
        {
            var r = await clienteLancamentos.SendAsync(Requisicao(merchant, $"indisp-{i}", "CREDITO", 100.00m));
            r.StatusCode.Should().Be(HttpStatusCode.Created,
                "o registro de lançamentos não pode depender do consolidado estar de pé");
        }
        for (var i = 0; i < 3; i++)
        {
            var r = await clienteLancamentos.SendAsync(Requisicao(merchant, $"indisp-deb-{i}", "DEBITO", 50.00m));
            r.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // A outbox drena para a fila durável mesmo sem consumidor: nada se perde.
        await AmbienteFixture.AguardarAsync(async () =>
            await ambiente.ConsultarAsync<int>(
                """
                SELECT COUNT(*) FROM lancamentos.outbox
                WHERE publicado_em IS NULL AND payload->>'merchantId' = @m
                """, new { m = merchant.ToString() }) == 0,
            TimeSpan.FromSeconds(30), "a outbox deveria ter sido publicada para a fila durável");

        // O consolidado ainda não viu nada: consulta responde zeros, não erro.
        var antes = await clienteConsolidado.SendAsync(ConsultaConsolidado(merchant, hoje));
        antes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LerSaldo(antes)).Quantidade.Should().Be(0);

        // Fase 2: worker volta. O backlog retido na fila é processado e o valor converge.
        await using var worker = ambiente.CriarWorker();
        using var _ = worker.CreateClient(); // materializa o host e inicia o consumidor

        await AmbienteFixture.AguardarAsync(async () =>
        {
            var resposta = await clienteConsolidado.SendAsync(ConsultaConsolidado(merchant, hoje));
            var corpo = await LerSaldo(resposta);
            return corpo.Quantidade == 11;
        }, TimeSpan.FromSeconds(60), "o backlog deveria convergir após o worker voltar");

        var final = await LerSaldo(await clienteConsolidado.SendAsync(ConsultaConsolidado(merchant, hoje)));
        final.TotalCreditos.Should().Be(800.00m);
        final.TotalDebitos.Should().Be(150.00m);
        final.Saldo.Should().Be(650.00m, "nenhum lançamento aceito durante a indisponibilidade pode ser perdido");
    }

    [Fact]
    public async Task Broker_indisponivel_nao_afeta_lancamentos_e_a_outbox_drena_quando_ele_volta()
    {
        var merchant = Guid.NewGuid();

        // Broker fora do ar de verdade.
        await ambiente.Rabbit.StopAsync();
        try
        {
            await using var api = ambiente.CriarApiDeLancamentos(outboxHabilitada: true);
            using var cliente = api.CreateClient();

            for (var i = 0; i < 5; i++)
            {
                var r = await cliente.SendAsync(Requisicao(merchant, $"broker-off-{i}", "CREDITO", 20.00m));
                r.StatusCode.Should().Be(HttpStatusCode.Created,
                    "a resposta ao cliente não depende do broker; a outbox retém a intenção de publicar");
            }

            (await ambiente.ConsultarAsync<int>(
                """
                SELECT COUNT(*) FROM lancamentos.outbox
                WHERE publicado_em IS NULL AND payload->>'merchantId' = @m
                """, new { m = merchant.ToString() }))
                .Should().Be(5, "com o broker fora, as mensagens permanecem pendentes na outbox");

            // Broker volta: o publicador drena sozinho, sem intervenção.
            await ambiente.Rabbit.StartAsync();

            await AmbienteFixture.AguardarAsync(async () =>
                await ambiente.ConsultarAsync<int>(
                    """
                    SELECT COUNT(*) FROM lancamentos.outbox
                    WHERE publicado_em IS NULL AND payload->>'merchantId' = @m
                    """, new { m = merchant.ToString() }) == 0,
                TimeSpan.FromSeconds(60), "a outbox deveria drenar após o broker voltar");
        }
        finally
        {
            // Garante o broker de pé para os demais testes, mesmo se este falhar.
            if (ambiente.Rabbit.State != DotNet.Testcontainers.Containers.TestcontainersStates.Running)
                await ambiente.Rabbit.StartAsync();
        }
    }

    private static HttpRequestMessage ConsultaConsolidado(Guid merchant, string data)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/consolidado/{data}");
        req.Headers.Add("X-Merchant-Id", merchant.ToString());
        return req;
    }

    private static async Task<RespostaConsolidado> LerSaldo(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<RespostaConsolidado>())!;

    private sealed record RespostaConsolidado(
        Guid MerchantId, DateOnly Data, decimal TotalCreditos, decimal TotalDebitos,
        decimal Saldo, int QuantidadeLancamentos, DateTimeOffset? AtualizadoEm, string Consistencia)
    {
        public int Quantidade => QuantidadeLancamentos;
    }
}
