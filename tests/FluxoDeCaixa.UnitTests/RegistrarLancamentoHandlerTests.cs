using System.Text.Json;
using FluentAssertions;
using FluxoDeCaixa.Contracts;
using FluxoDeCaixa.Lancamentos.Application;
using FluxoDeCaixa.Lancamentos.Domain;
using Xunit;

namespace FluxoDeCaixa.UnitTests;

/// <summary>Dublê em memória da porta de persistência, com o mesmo contrato de corrida.</summary>
public sealed class ArmazenamentoEmMemoria : IArmazenamentoDeLancamentos
{
    public readonly Dictionary<(Guid, string), Guid> Idempotencia = [];
    public readonly Dictionary<Guid, Lancamento> Lancamentos = [];
    public readonly List<MensagemDeOutbox> Outbox = [];
    public bool SimularCorridaNaProximaGravacao;

    public Task<Guid?> ObterPorChaveDeIdempotenciaAsync(Guid merchantId, string chave, CancellationToken ct) =>
        Task.FromResult(Idempotencia.TryGetValue((merchantId, chave), out var id) ? id : (Guid?)null);

    public Task<Lancamento?> ObterPorIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Lancamentos.GetValueOrDefault(id));

    public Task<bool> SalvarAsync(Lancamento l, MensagemDeOutbox outbox, string chave, CancellationToken ct)
    {
        if (SimularCorridaNaProximaGravacao || Idempotencia.ContainsKey((l.MerchantId, chave)))
            return Task.FromResult(false);

        Lancamentos[l.Id] = l;
        Outbox.Add(outbox);
        Idempotencia[(l.MerchantId, chave)] = l.Id;
        return Task.FromResult(true);
    }
}

public class RegistrarLancamentoHandlerTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly RelogioFixo Relogio = new(new DateOnly(2026, 08, 13));

    private static RegistrarLancamentoComando Comando(string chave = "chave-1", decimal valor = 100m) =>
        new(Merchant, "CREDITO", valor, "Venda", null, chave, "trace-abc");

    [Fact]
    public async Task Grava_lancamento_e_outbox_juntos()
    {
        var armazenamento = new ArmazenamentoEmMemoria();
        var handler = new RegistrarLancamentoHandler(armazenamento, Relogio);

        var resposta = await handler.ExecutarAsync(Comando(), CancellationToken.None);

        resposta.Duplicado.Should().BeFalse();
        armazenamento.Lancamentos.Should().HaveCount(1);
        armazenamento.Outbox.Should().HaveCount(1, "a intenção de publicar nasce junto com o lançamento");
    }

    [Fact]
    public async Task Evento_serializado_cumpre_o_contrato_publicado()
    {
        var armazenamento = new ArmazenamentoEmMemoria();
        var handler = new RegistrarLancamentoHandler(armazenamento, Relogio);

        await handler.ExecutarAsync(Comando(), CancellationToken.None);

        var payload = armazenamento.Outbox.Single().Payload;
        var evento = JsonSerializer.Deserialize<LancamentoRegistradoV1>(
            payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Validação leve de schema: os campos que o consumidor depende precisam existir.
        evento.IdEvento.Should().NotBeEmpty();
        evento.IdLancamento.Should().NotBeEmpty();
        evento.MerchantId.Should().Be(Merchant);
        evento.Tipo.Should().Be(1);
        evento.Valor.Should().Be(100m);
        evento.Data.Should().Be(Relogio.Hoje);
        evento.IdCorrelacao.Should().Be("trace-abc");
    }

    [Fact]
    public async Task Segunda_requisicao_com_mesma_chave_devolve_o_mesmo_lancamento_sem_novo_evento()
    {
        var armazenamento = new ArmazenamentoEmMemoria();
        var handler = new RegistrarLancamentoHandler(armazenamento, Relogio);

        var primeira = await handler.ExecutarAsync(Comando(), CancellationToken.None);
        var segunda = await handler.ExecutarAsync(Comando(), CancellationToken.None);

        segunda.Duplicado.Should().BeTrue();
        segunda.Id.Should().Be(primeira.Id, "mesma chave, mesma resposta lógica");
        armazenamento.Lancamentos.Should().HaveCount(1);
        armazenamento.Outbox.Should().HaveCount(1, "requisição repetida não pode gerar evento financeiro novo");
    }

    [Fact]
    public async Task Corrida_entre_requisicoes_com_mesma_chave_resolve_para_o_vencedor()
    {
        var armazenamento = new ArmazenamentoEmMemoria();
        var handler = new RegistrarLancamentoHandler(armazenamento, Relogio);

        var vencedora = await handler.ExecutarAsync(Comando(), CancellationToken.None);

        // A segunda perde o INSERT (violação de unicidade) e precisa responder com o original.
        armazenamento.SimularCorridaNaProximaGravacao = true;
        armazenamento.Idempotencia.Remove((Merchant, "chave-1"));
        var tarefa = async () =>
        {
            armazenamento.Idempotencia[(Merchant, "chave-1")] = vencedora.Id; // vencedora comitou no meio do caminho
            return await handler.ExecutarAsync(Comando(), CancellationToken.None);
        };

        var perdedora = await tarefa();
        perdedora.Duplicado.Should().BeTrue();
        perdedora.Id.Should().Be(vencedora.Id);
    }

    [Fact]
    public async Task Tipo_invalido_e_rejeitado_antes_de_qualquer_gravacao()
    {
        var armazenamento = new ArmazenamentoEmMemoria();
        var handler = new RegistrarLancamentoHandler(armazenamento, Relogio);
        var comando = Comando() with { Tipo = "TRANSFERENCIA" };

        var acao = () => handler.ExecutarAsync(comando, CancellationToken.None);

        await acao.Should().ThrowAsync<DominioInvalidoException>();
        armazenamento.Lancamentos.Should().BeEmpty();
        armazenamento.Outbox.Should().BeEmpty();
    }
}
