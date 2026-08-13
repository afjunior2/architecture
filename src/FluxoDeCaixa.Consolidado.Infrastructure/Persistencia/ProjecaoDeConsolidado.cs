using Dapper;
using FluxoDeCaixa.Contracts;
using Npgsql;

namespace FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;

public enum ResultadoDaProjecao
{
    Processado,
    Duplicado
}

/// <summary>
/// Processa um evento em uma única transação: deduplicação, cópia local e recálculo.
///
/// A projeção recalcula em vez de acumular (saldo += valor). O custo é uma agregação
/// sobre os lançamentos de um merchant em um dia, dezenas de linhas com índice.
/// O ganho: duplicata, reordenação e lançamento retroativo viram não-problemas,
/// e reprojetar nunca piora o estado. Ver ADR-004.
/// </summary>
public sealed class ProjecaoDeConsolidado(NpgsqlDataSource dataSource)
{
    public async Task<ResultadoDaProjecao> ProcessarAsync(LancamentoRegistradoV1 evento, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Dedup e efeito na MESMA transação. Separados, uma falha entre eles
        // perderia o evento (marcado como processado sem efeito) ou o duplicaria.
        var inserido = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO consolidado.eventos_processados (id_evento)
            VALUES (@IdEvento)
            ON CONFLICT (id_evento) DO NOTHING
            """, new { evento.IdEvento }, tx, cancellationToken: ct));

        if (inserido == 0)
        {
            await tx.RollbackAsync(ct);
            return ResultadoDaProjecao.Duplicado;
        }

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO consolidado.lancamentos_recebidos (id_lancamento, merchant_id, data, tipo, valor)
            VALUES (@IdLancamento, @MerchantId, @Data, @Tipo, @Valor)
            ON CONFLICT (id_lancamento) DO NOTHING
            """,
            new { evento.IdLancamento, evento.MerchantId, evento.Data, evento.Tipo, evento.Valor },
            tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO consolidado.consolidado_diario
                (merchant_id, data, total_creditos, total_debitos, saldo, quantidade, atualizado_em)
            SELECT merchant_id, data,
                   COALESCE(SUM(valor) FILTER (WHERE tipo = 1), 0),
                   COALESCE(SUM(valor) FILTER (WHERE tipo = 2), 0),
                   COALESCE(SUM(valor) FILTER (WHERE tipo = 1), 0) - COALESCE(SUM(valor) FILTER (WHERE tipo = 2), 0),
                   COUNT(*), now()
            FROM consolidado.lancamentos_recebidos
            WHERE merchant_id = @MerchantId AND data = @Data
            GROUP BY merchant_id, data
            ON CONFLICT (merchant_id, data) DO UPDATE SET
                total_creditos = EXCLUDED.total_creditos,
                total_debitos  = EXCLUDED.total_debitos,
                saldo          = EXCLUDED.saldo,
                quantidade     = EXCLUDED.quantidade,
                atualizado_em  = EXCLUDED.atualizado_em
            """,
            new { evento.MerchantId, evento.Data },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return ResultadoDaProjecao.Processado;
    }
}
