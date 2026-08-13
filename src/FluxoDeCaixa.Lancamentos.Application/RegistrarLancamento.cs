using System.Text.Json;
using FluxoDeCaixa.Contracts;
using FluxoDeCaixa.Lancamentos.Domain;

namespace FluxoDeCaixa.Lancamentos.Application;

public sealed record RegistrarLancamentoComando(
    Guid MerchantId,
    string Tipo,
    decimal Valor,
    string? Descricao,
    DateOnly? Data,
    string ChaveIdempotencia,
    string? IdCorrelacao);

public sealed record LancamentoRegistradoResposta(
    Guid Id,
    Guid MerchantId,
    string Tipo,
    decimal Valor,
    string Descricao,
    DateOnly Data,
    DateTimeOffset RegistradoEm,
    bool Duplicado);

/// <summary>
/// Caso de uso central do lado de escrita. A decisão importante está na ordem:
/// o evento é serializado e gravado na outbox dentro da mesma transação do lançamento.
/// A resposta ao cliente não depende do broker em nenhum momento.
/// </summary>
public sealed class RegistrarLancamentoHandler(IArmazenamentoDeLancamentos armazenamento, IRelogio relogio)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LancamentoRegistradoResposta> ExecutarAsync(RegistrarLancamentoComando comando, CancellationToken ct)
    {
        // Caminho rápido: chave já usada por este merchant devolve a resposta original.
        var idExistente = await armazenamento.ObterPorChaveDeIdempotenciaAsync(
            comando.MerchantId, comando.ChaveIdempotencia, ct);
        if (idExistente is not null)
            return await RespostaExistenteAsync(idExistente.Value, ct);

        var tipo = ConverterTipo(comando.Tipo);
        var lancamento = Lancamento.Registrar(
            comando.MerchantId, tipo, comando.Valor, comando.Descricao, comando.Data, relogio);

        var evento = new LancamentoRegistradoV1
        {
            IdEvento = GuidV7.Novo(),
            IdCorrelacao = comando.IdCorrelacao,
            OcorridoEm = lancamento.RegistradoEm,
            IdLancamento = lancamento.Id,
            MerchantId = lancamento.MerchantId,
            Tipo = (int)lancamento.Tipo,
            Valor = lancamento.Valor,
            Data = lancamento.Data,
            RegistradoEm = lancamento.RegistradoEm
        };

        var outbox = new MensagemDeOutbox(
            evento.IdEvento,
            LancamentoRegistradoV1.TipoEvento,
            JsonSerializer.Serialize(evento, JsonOptions),
            evento.OcorridoEm);

        var gravou = await armazenamento.SalvarAsync(lancamento, outbox, comando.ChaveIdempotencia, ct);
        if (!gravou)
        {
            // Corrida: outra requisição com a mesma chave ganhou o INSERT.
            var idOriginal = await armazenamento.ObterPorChaveDeIdempotenciaAsync(
                comando.MerchantId, comando.ChaveIdempotencia, ct)
                ?? throw new InvalidOperationException("Chave de idempotência em estado inconsistente.");
            return await RespostaExistenteAsync(idOriginal, ct);
        }

        return Mapear(lancamento, duplicado: false);
    }

    private async Task<LancamentoRegistradoResposta> RespostaExistenteAsync(Guid id, CancellationToken ct)
    {
        var original = await armazenamento.ObterPorIdAsync(id, ct)
            ?? throw new InvalidOperationException($"Lançamento {id} referenciado pela idempotência não encontrado.");
        return Mapear(original, duplicado: true);
    }

    private static LancamentoRegistradoResposta Mapear(Lancamento l, bool duplicado) => new(
        l.Id, l.MerchantId, l.Tipo.ToString().ToUpperInvariant(), l.Valor, l.Descricao,
        l.Data, l.RegistradoEm, duplicado);

    private static TipoLancamento ConverterTipo(string tipo) => tipo.Trim().ToUpperInvariant() switch
    {
        "CREDITO" => TipoLancamento.Credito,
        "DEBITO" => TipoLancamento.Debito,
        _ => throw new DominioInvalidoException(ErrosDeDominio.TipoInvalido,
            "O tipo do lançamento deve ser CREDITO ou DEBITO.")
    };
}
