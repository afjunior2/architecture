# ADR-002: Comunicação assíncrona por eventos entre os serviços

Status: aceito. Data: 2026-08-13.

## Contexto

Com a fronteira do ADR-001, o consolidado precisa saber dos lançamentos. A forma dessa comunicação decide se o isolamento de falha é real: em comunicação síncrona, os dois lados precisam estar disponíveis no mesmo instante, que é exatamente o que o requisito proíbe.

## Decisão

A atualização do consolidado não participa da transação HTTP de criação do lançamento. O serviço de Lançamentos publica `lancamento.registrado` num exchange do RabbitMQ; o worker do Consolidado consome de uma fila durável e projeta. O evento carrega o estado completo (event-carried state transfer): o consumidor projeta sem consultar o serviço de origem.

Entrega assumida: at-least-once. Duplicata não é hipótese, é certeza estatística, e por isso o consumo é idempotente (ADR-003 cobre a publicação; a dedup transacional no consumidor cobre o consumo). Não prometemos exactly-once distribuído.

A fila principal é declarada pelos dois lados, de forma idempotente. Sem a fila existindo, mensagem publicada com o consumidor fora do ar seria descartada pelo exchange, e o cenário de indisponibilidade perderia dados em vez de acumular backlog.

## Alternativas consideradas

REST síncrono do Lançamentos para o Consolidado: acopla a escrita à disponibilidade do consolidado; primeira falha viola o requisito.

Evento de notificação (só o id, consumidor busca o resto): inverte a dependência mas não a elimina; o consumidor passaria a depender da disponibilidade da API de Lançamentos para projetar.

Consolidado lendo o banco de Lançamentos: o acoplamento mais silencioso e o pior; quebra a fronteira de dados e cria dependência invisível de schema.

RabbitMQ e não Kafka: o requisito atual é distribuição de eventos com volume baixo, retry e DLQ. Kafka traria retenção e replay que ainda não precisamos, com custo operacional maior. O gatilho de migração está em trade-offs-and-evolution.md.

## Consequências

Desacoplamento temporal completo entre os lados; o broker absorve indisponibilidade do consumidor. O preço: consistência eventual (ADR-004), contrato de evento versionado como interface pública entre os serviços, e a obrigação de idempotência no consumo, verificada por teste de integração.
