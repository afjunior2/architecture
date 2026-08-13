using FluxoDeCaixa.Lancamentos.Domain;

namespace FluxoDeCaixa.Lancamentos.Application;

/// <summary>
/// Mensagem pendente de publicação, gravada na mesma transação do lançamento.
/// A serialização acontece na aplicação; a infraestrutura só persiste e publica bytes.
/// </summary>
public sealed record MensagemDeOutbox(Guid Id, string TipoEvento, string Payload, DateTimeOffset OcorridoEm);

/// <summary>
/// Porta de persistência do lado de escrita. A implementação garante que lançamento,
/// outbox e registro de idempotência entram na mesma transação local.
/// </summary>
public interface IArmazenamentoDeLancamentos
{
    /// <summary>Retorna o id do lançamento já registrado com esta chave, ou null.</summary>
    Task<Guid?> ObterPorChaveDeIdempotenciaAsync(Guid merchantId, string chave, CancellationToken ct);

    Task<Lancamento?> ObterPorIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Persiste tudo em uma transação. Retorna false se a chave de idempotência já
    /// existia (corrida entre requisições concorrentes com a mesma chave): nesse caso
    /// nada foi gravado e o chamador deve responder com o lançamento original.
    /// </summary>
    Task<bool> SalvarAsync(Lancamento lancamento, MensagemDeOutbox outbox, string chaveIdempotencia, CancellationToken ct);
}
