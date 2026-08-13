namespace FluxoDeCaixa.Lancamentos.Domain;

/// <summary>
/// Tempo é dependência externa. Injetá-lo torna a regra "data não pode ser futura"
/// testável sem manipular o relógio da máquina.
/// </summary>
public interface IRelogio
{
    DateTimeOffset Agora { get; }
    DateOnly Hoje { get; }
}

public sealed class RelogioDoSistema : IRelogio
{
    public DateTimeOffset Agora => DateTimeOffset.UtcNow;
    public DateOnly Hoje => DateOnly.FromDateTime(DateTime.UtcNow);
}
