using Npgsql;

namespace FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;

/// <summary>
/// DDL idempotente executado no boot (ambiente local e testes). Em produção, migração
/// é etapa do pipeline. O Consolidado não usa EF de propósito: este lado não tem regra
/// de negócio a proteger com modelo rico, é SQL direto sobre uma projeção.
/// </summary>
public static class EsquemaDoConsolidado
{
    public static async Task GarantirAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        // Advisory lock: a API e o worker executam este DDL no boot e podem subir juntos.
        // CREATE TABLE IF NOT EXISTS não protege contra corrida; o lock serializa.
        // xact_lock (não o de sessão): libera só no commit, senão a segunda sessão destrava
        // e tenta criar a mesma tabela antes do commit da primeira ficar visível, colidindo
        // em pg_type (unique constraint em nome de tipo).
        const string ddl = """
            SELECT pg_advisory_xact_lock(4202);

            CREATE SCHEMA IF NOT EXISTS consolidado;

            -- Cópia local dos fatos recebidos por evento. É o insumo do recálculo:
            -- sem ela, recalcular exigiria consultar o serviço de Lançamentos,
            -- criando exatamente o acoplamento que a arquitetura evita.
            CREATE TABLE IF NOT EXISTS consolidado.lancamentos_recebidos (
                id_lancamento uuid PRIMARY KEY,
                merchant_id   uuid NOT NULL,
                data          date NOT NULL,
                tipo          smallint NOT NULL,
                valor         numeric(18,2) NOT NULL,
                recebido_em   timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_lancamentos_recebidos_chave
                ON consolidado.lancamentos_recebidos (merchant_id, data);

            CREATE TABLE IF NOT EXISTS consolidado.consolidado_diario (
                merchant_id    uuid NOT NULL,
                data           date NOT NULL,
                total_creditos numeric(18,2) NOT NULL,
                total_debitos  numeric(18,2) NOT NULL,
                saldo          numeric(18,2) NOT NULL,
                quantidade     int NOT NULL,
                atualizado_em  timestamptz NOT NULL,
                PRIMARY KEY (merchant_id, data)
            );

            -- Deduplicação do consumidor (idempotent receiver). At-least-once garante
            -- que duplicata vai chegar; esta tabela garante que ela não tem efeito.
            CREATE TABLE IF NOT EXISTS consolidado.eventos_processados (
                id_evento     uuid PRIMARY KEY,
                processado_em timestamptz NOT NULL DEFAULT now()
            );
            """;

        await using var cmd = dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
