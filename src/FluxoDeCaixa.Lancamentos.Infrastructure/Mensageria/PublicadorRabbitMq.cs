using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FluxoDeCaixa.Lancamentos.Infrastructure.Mensageria;

public interface IPublicadorDeMensagens
{
    /// <summary>Publica com confirmação do broker. Lança exceção se a confirmação não vier.</summary>
    Task PublicarAsync(string routingKey, string payload, Guid idEvento, CancellationToken ct);
}

/// <summary>
/// Conexão preguiçosa e reaproveitada. Publisher confirms ligado: o broker confirma a
/// persistência da mensagem antes de marcarmos a linha da outbox como publicada.
/// Se o broker estiver fora, a exceção sobe, a transação do publicador aborta e as
/// linhas continuam pendentes. Nada é perdido, só atrasado.
/// </summary>
public sealed class PublicadorRabbitMq(IOptions<RabbitMqOpcoes> opcoes) : IPublicadorDeMensagens, IDisposable
{
    private readonly RabbitMqOpcoes _opcoes = opcoes.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _conexao;
    private IModel? _canal;

    public async Task PublicarAsync(string routingKey, string payload, Guid idEvento, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var canal = ObterCanal();
            var props = canal.CreateBasicProperties();
            props.Persistent = true;
            props.MessageId = idEvento.ToString();
            props.ContentType = "application/json";

            canal.BasicPublish(_opcoes.Exchange, routingKey, mandatory: false, props,
                Encoding.UTF8.GetBytes(payload));

            // Bloqueia até o broker confirmar a persistência (ou lança).
            canal.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Canal em estado indeterminado após falha: descarta e reconecta no próximo ciclo.
            DescartarCanal();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private IModel ObterCanal()
    {
        if (_canal is { IsOpen: true }) return _canal;

        if (_conexao is not { IsOpen: true })
        {
            var factory = new ConnectionFactory
            {
                HostName = _opcoes.Host,
                Port = _opcoes.Porta,
                UserName = _opcoes.Usuario,
                Password = _opcoes.Senha
            };
            _conexao = factory.CreateConnection("lancamentos-outbox-publisher");
        }

        _canal = _conexao.CreateModel();
        _canal.ConfirmSelect();

        _canal.ExchangeDeclare(_opcoes.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

        // Sem a fila durável declarada, mensagem publicada com o consumidor fora do ar
        // seria descartada pelo exchange. Declarar dos dois lados é idempotente e
        // garante retenção do backlog durante a indisponibilidade do consolidado.
        _canal.QueueDeclare(_opcoes.FilaDoConsumidor, durable: true, exclusive: false, autoDelete: false);
        _canal.QueueBind(_opcoes.FilaDoConsumidor, _opcoes.Exchange, "lancamento.registrado");

        return _canal;
    }

    private void DescartarCanal()
    {
        try { _canal?.Dispose(); } catch { /* melhor esforço */ }
        try { _conexao?.Dispose(); } catch { /* melhor esforço */ }
        _canal = null;
        _conexao = null;
    }

    public void Dispose()
    {
        DescartarCanal();
        _lock.Dispose();
    }
}
