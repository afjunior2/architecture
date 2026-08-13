using FluxoDeCaixa.Lancamentos.Application;
using FluxoDeCaixa.Lancamentos.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FluxoDeCaixa.Lancamentos.Infrastructure.Persistencia;

public sealed class ArmazenamentoDeLancamentos(LancamentosDbContext db, IRelogio relogio) : IArmazenamentoDeLancamentos
{
    public async Task<Guid?> ObterPorChaveDeIdempotenciaAsync(Guid merchantId, string chave, CancellationToken ct)
    {
        var registro = await db.Idempotencia.AsNoTracking()
            .FirstOrDefaultAsync(i => i.MerchantId == merchantId && i.Chave == chave, ct);
        return registro?.LancamentoId;
    }

    public Task<Lancamento?> ObterPorIdAsync(Guid id, CancellationToken ct) =>
        db.Lancamentos.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<bool> SalvarAsync(
        Lancamento lancamento, MensagemDeOutbox outbox, string chaveIdempotencia, CancellationToken ct)
    {
        db.Lancamentos.Add(lancamento);
        db.Outbox.Add(new OutboxRegistro
        {
            Id = outbox.Id,
            TipoEvento = outbox.TipoEvento,
            Payload = outbox.Payload,
            OcorridoEm = outbox.OcorridoEm
        });
        db.Idempotencia.Add(new IdempotenciaRegistro
        {
            MerchantId = lancamento.MerchantId,
            Chave = chaveIdempotencia,
            LancamentoId = lancamento.Id,
            CriadoEm = relogio.Agora
        });

        try
        {
            // SaveChanges dentro de uma única transação implícita: lançamento, outbox
            // e idempotência entram juntos ou não entram. Não existe dual-write aqui.
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Corrida entre requisições com a mesma chave de idempotência.
            // A restrição do banco é a garantia; o if antes dela é só o caminho rápido.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
