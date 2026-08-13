using System.Text.Json;
using FluxoDeCaixa.Consolidado.Infrastructure.Observabilidade;
using FluxoDeCaixa.Consolidado.Infrastructure.Persistencia;
using FluxoDeCaixa.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FluxoDeCaixa.Consolidado.Infrastructure.Mensageria;

public sealed class RabbitMqOpcoes
{
    public const string Secao = "RabbitMq";
    public string Host { get; set; } = "localhost";
    public int Porta { get; set; } = 5672;
    public string Usuario { get; set; } = "guest";
    public string Senha { get; set; } = "guest";
    public string Exchange { get; set; } = "fluxo-caixa";
    public string Fila { get; set; } = "consolidado.projecao";
    public ushort Prefetch { get; set; } = 25;
    public int MaximoDeTentativas { get; set; } = 5;
}

/// <summary>
/// Consumidor da fila de projeção. Entrega é at-least-once, então o processamento é
/// idempotente por construção (dedup por id do evento, na mesma transação do efeito).
///
/// Retry: nack com requeue devolveria a mensagem imediatamente à cabeça da fila,
/// criando um hot loop que queima as tentativas em milissegundos. Em vez disso,
/// falha transitória vai para uma fila de espera com TTL e dead-letter de volta
/// para a fila principal. Depois do limite de tentativas, DLQ e alerta.
/// Mensagem malformada (poison) vai direto para a DLQ, sem retry.
/// </summary>
public sealed class ConsumidorDeLancamentos(
    ProjecaoDeConsolidado projecao,
    IOptions<RabbitMqOpcoes> opcoes,
    ILogger<ConsumidorDeLancamentos> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqOpcoes _o = opcoes.Value;
    private IConnection? _conexao;
    private IModel? _canal;

    private string FilaDeRetry => $"{_o.Fila}.retry";
    private string FilaDlq => $"{_o.Fila}.dlq";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Sai da thread de startup antes de qualquer chamada bloqueante.
        await Task.Yield();

        // Espera o broker com backoff: no compose, o worker pode subir antes do RabbitMQ.
        var tentativa = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Conectar();
                tentativa = 0;
                logger.LogInformation("Consumindo fila {Fila} (prefetch {Prefetch}).", _o.Fila, _o.Prefetch);
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                tentativa++;
                var espera = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(tentativa, 5))));
                logger.LogWarning(ex, "Broker indisponível (tentativa {Tentativa}). Nova tentativa em {Espera}s.",
                    tentativa, espera.TotalSeconds);
                await Task.Delay(espera, ct);
            }
        }
    }

    private void Conectar()
    {
        var factory = new ConnectionFactory
        {
            HostName = _o.Host,
            Port = _o.Porta,
            UserName = _o.Usuario,
            Password = _o.Senha,
            DispatchConsumersAsync = true,
            // Reconexão e redeclaração automáticas se o broker reiniciar no meio do caminho.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        _conexao = factory.CreateConnection("consolidado-worker");
        _canal = _conexao.CreateModel();
        // Confirms também aqui: o Encaminhar reposta a mensagem (retry/DLQ) antes do ack
        // da original. Sem confirmação do broker, uma queda entre o publish e o ack
        // perderia o evento. Com ela, falha no encaminhamento deixa a original sem ack
        // e o redelivery natural acontece (a dedup absorve a duplicata).
        _canal.ConfirmSelect();

        DeclararTopologia(_canal);
        _canal.BasicQos(0, _o.Prefetch, global: false);

        var consumer = new AsyncEventingBasicConsumer(_canal);
        consumer.Received += (_, ea) => TratarMensagemAsync(ea);
        _canal.BasicConsume(_o.Fila, autoAck: false, consumer);
    }

    private void DeclararTopologia(IModel canal)
    {
        canal.ExchangeDeclare(_o.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

        canal.QueueDeclare(_o.Fila, durable: true, exclusive: false, autoDelete: false);
        canal.QueueBind(_o.Fila, _o.Exchange, LancamentoRegistradoV1.RoutingKey);

        // Fila de espera: mensagens dormem o TTL e voltam para a fila principal
        // pelo dead-letter-exchange (exchange default roteia pelo nome da fila).
        canal.QueueDeclare(FilaDeRetry, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-message-ttl"] = 5000,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = _o.Fila
            });

        canal.QueueDeclare(FilaDlq, durable: true, exclusive: false, autoDelete: false);
    }

    private async Task TratarMensagemAsync(BasicDeliverEventArgs ea)
    {
        var canal = _canal!;
        LancamentoRegistradoV1? evento;
        try
        {
            evento = JsonSerializer.Deserialize<LancamentoRegistradoV1>(ea.Body.Span, JsonOptions);
            if (evento is null || evento.IdEvento == Guid.Empty)
                throw new JsonException("Evento sem IdEvento.");
        }
        catch (JsonException ex)
        {
            // Poison message: retry não conserta JSON inválido. DLQ direto, a fila não trava.
            logger.LogError(ex, "Mensagem malformada enviada para a DLQ.");
            MetricasDeConsolidado.EventosComFalha.Add(1, new KeyValuePair<string, object?>("motivo", "poison"));
            try
            {
                Encaminhar(canal, FilaDlq, ea);
                canal.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception encEx)
            {
                logger.LogWarning(encEx, "Falha ao encaminhar para a DLQ; mensagem será reentregue.");
            }
            return;
        }

        try
        {
            var resultado = await projecao.ProcessarAsync(evento, CancellationToken.None);
            canal.BasicAck(ea.DeliveryTag, multiple: false);

            if (resultado == ResultadoDaProjecao.Duplicado)
            {
                MetricasDeConsolidado.EventosDuplicados.Add(1);
                logger.LogInformation("Evento {IdEvento} duplicado, descartado pela deduplicação.", evento.IdEvento);
                return;
            }

            MetricasDeConsolidado.EventosProcessados.Add(1);
            var lag = (DateTimeOffset.UtcNow - evento.OcorridoEm).TotalSeconds;
            MetricasDeConsolidado.LagDeProcessamento.Record(Math.Max(0, lag));
            logger.LogInformation(
                "Evento {IdEvento} projetado. Merchant {MerchantId}, data {Data}, lag {LagSegundos:F1}s, correlacao {IdCorrelacao}.",
                evento.IdEvento, evento.MerchantId, evento.Data, lag, evento.IdCorrelacao);
        }
        catch (Exception ex)
        {
            var tentativas = LerTentativas(ea) + 1;
            MetricasDeConsolidado.EventosComFalha.Add(1, new KeyValuePair<string, object?>("motivo", "transiente"));

            var destino = tentativas >= _o.MaximoDeTentativas ? FilaDlq : FilaDeRetry;
            if (destino == FilaDlq)
                logger.LogError(ex, "Evento {IdEvento} excedeu {Max} tentativas. Enviado para a DLQ.",
                    evento.IdEvento, _o.MaximoDeTentativas);
            else
                logger.LogWarning(ex,
                    "Falha ao projetar evento {IdEvento} (tentativa {Tentativa}). Reagendado via fila de espera.",
                    evento.IdEvento, tentativas);

            try
            {
                Encaminhar(canal, destino, ea, tentativas);
                canal.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception encEx)
            {
                logger.LogWarning(encEx,
                    "Falha ao encaminhar o evento {IdEvento}; mensagem será reentregue pelo broker.",
                    evento.IdEvento);
            }
        }
    }

    private static int LerTentativas(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties?.Headers is { } headers
            && headers.TryGetValue("x-tentativas", out var v))
        {
            return v switch { int i => i, long l => (int)l, _ => 0 };
        }
        return 0;
    }

    private static void Encaminhar(IModel canal, string fila, BasicDeliverEventArgs ea, int? tentativas = null)
    {
        var props = canal.CreateBasicProperties();
        props.Persistent = true;
        props.MessageId = ea.BasicProperties?.MessageId;
        props.ContentType = ea.BasicProperties?.ContentType;
        // Preserva headers originais (x-death e afins) e sobrescreve só o contador.
        props.Headers = ea.BasicProperties?.Headers is { } h
            ? new Dictionary<string, object>(h)
            : new Dictionary<string, object>();
        if (tentativas is not null)
            props.Headers["x-tentativas"] = tentativas.Value;

        canal.BasicPublish("", fila, mandatory: false, props, ea.Body);
        canal.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
    }

    public override void Dispose()
    {
        try { _canal?.Dispose(); } catch { /* melhor esforço */ }
        try { _conexao?.Dispose(); } catch { /* melhor esforço */ }
        base.Dispose();
    }
}
