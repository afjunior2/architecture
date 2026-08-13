using Dapper;
using Npgsql;

namespace FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;

public sealed record ConsolidadoDiario(
    Guid MerchantId,
    DateOnly Data,
    decimal TotalCreditos,
    decimal TotalDebitos,
    decimal Saldo,
    int Quantidade,
    DateTimeOffset? AtualizadoEm);

public sealed class ConsultaDeConsolidado(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Dia sem movimento retorna zeros, não erro: "não houve movimento" é resposta
    /// de negócio legítima. AtualizadoEm nulo indica que nenhum evento foi projetado
    /// para esta chave ainda.
    /// </summary>
    public async Task<ConsolidadoDiario> ObterAsync(Guid merchantId, DateOnly data, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var linha = await conn.QuerySingleOrDefaultAsync<LinhaConsolidado>(new CommandDefinition("""
            SELECT total_creditos AS TotalCreditos,
                   total_debitos  AS TotalDebitos,
                   saldo          AS Saldo,
                   quantidade     AS Quantidade,
                   atualizado_em  AS AtualizadoEm
            FROM consolidado.consolidado_diario
            WHERE merchant_id = @merchantId AND data = @data
            """, new { merchantId, data }, cancellationToken: ct));

        return linha is null
            ? new ConsolidadoDiario(merchantId, data, 0, 0, 0, 0, null)
            : new ConsolidadoDiario(merchantId, data, linha.TotalCreditos, linha.TotalDebitos,
                linha.Saldo, linha.Quantidade, linha.AtualizadoEm);
    }

    private sealed record LinhaConsolidado(
        decimal TotalCreditos, decimal TotalDebitos, decimal Saldo, int Quantidade, DateTime AtualizadoEm);
}
