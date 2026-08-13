using FluxoDeCaixa.Lancamentos.Domain;
using Microsoft.EntityFrameworkCore;

namespace FluxoDeCaixa.Lancamentos.Infrastructure.Persistencia;

/// <summary>Linha da outbox como tabela. O payload é opaco para a infraestrutura.</summary>
public sealed class OutboxRegistro
{
    public Guid Id { get; set; }
    public string TipoEvento { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OcorridoEm { get; set; }
    public DateTimeOffset? PublicadoEm { get; set; }
    public int Tentativas { get; set; }
}

/// <summary>Chave de idempotência escopada por merchant. Chave global vazaria resposta entre tenants.</summary>
public sealed class IdempotenciaRegistro
{
    public Guid MerchantId { get; set; }
    public string Chave { get; set; } = string.Empty;
    public Guid LancamentoId { get; set; }
    public DateTimeOffset CriadoEm { get; set; }
}

public sealed class LancamentosDbContext(DbContextOptions<LancamentosDbContext> options) : DbContext(options)
{
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();
    public DbSet<OutboxRegistro> Outbox => Set<OutboxRegistro>();
    public DbSet<IdempotenciaRegistro> Idempotencia => Set<IdempotenciaRegistro>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasDefaultSchema("lancamentos");

        mb.Entity<Lancamento>(e =>
        {
            e.ToTable("lancamentos", t =>
            {
                // Segunda linha de defesa: a invariante também vale no banco.
                t.HasCheckConstraint("ck_lancamentos_valor_positivo", "valor > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.MerchantId).HasColumnName("merchant_id");
            e.Property(x => x.Tipo).HasColumnName("tipo").HasConversion<short>();
            e.Property(x => x.Valor).HasColumnName("valor").HasColumnType("numeric(18,2)");
            e.Property(x => x.Descricao).HasColumnName("descricao").HasMaxLength(200);
            e.Property(x => x.Data).HasColumnName("data");
            e.Property(x => x.RegistradoEm).HasColumnName("registrado_em");
            e.HasIndex(x => new { x.MerchantId, x.Data }).HasDatabaseName("ix_lancamentos_merchant_data");
        });

        mb.Entity<OutboxRegistro>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TipoEvento).HasColumnName("tipo_evento").HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.OcorridoEm).HasColumnName("ocorrido_em");
            e.Property(x => x.PublicadoEm).HasColumnName("publicado_em");
            e.Property(x => x.Tentativas).HasColumnName("tentativas");
            // Índice parcial: o polling varre só o pendente, com custo constante
            // mesmo com milhões de linhas já publicadas.
            e.HasIndex(x => x.OcorridoEm)
                .HasDatabaseName("ix_outbox_pendentes")
                .HasFilter("publicado_em IS NULL");
        });

        mb.Entity<IdempotenciaRegistro>(e =>
        {
            e.ToTable("idempotencia");
            e.HasKey(x => new { x.MerchantId, x.Chave });
            e.Property(x => x.MerchantId).HasColumnName("merchant_id");
            e.Property(x => x.Chave).HasColumnName("chave").HasMaxLength(100);
            e.Property(x => x.LancamentoId).HasColumnName("lancamento_id");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
        });
    }
}
