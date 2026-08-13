namespace FluxoDeCaixa.Lancamentos.Infrastructure.Mensageria;

public sealed class RabbitMqOpcoes
{
    public const string Secao = "RabbitMq";
    public string Host { get; set; } = "localhost";
    public int Porta { get; set; } = 5672;
    public string Usuario { get; set; } = "guest";
    public string Senha { get; set; } = "guest";
    public string Exchange { get; set; } = "fluxo-caixa";

    /// <summary>
    /// Fila principal do consumidor. O publicador também a declara (operação idempotente)
    /// para que mensagens fiquem retidas mesmo que o consumidor nunca tenha subido.
    /// A fila durável é parte do contrato de entrega, não detalhe do consumidor.
    /// </summary>
    public string FilaDoConsumidor { get; set; } = "consolidado.projecao";
}
