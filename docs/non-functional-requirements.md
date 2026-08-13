# Requisitos não funcionais

As metas abaixo são objetivos arquiteturais propostos, não SLAs informados pelo negócio. Em produção bancária, seriam validadas com produto, segurança e infraestrutura antes de virar compromisso.

## Objetivos de qualidade

API de Lançamentos:

```text
Disponibilidade alvo:      >= 99,9%
Erro em condição normal:   < 1%
p95 de escrita:            < 200 ms
Lançamento confirmado pela API não pode ser perdido por falha de mensageria ou consolidação.
```

API de Consolidado:

```text
Disponibilidade alvo:  >= 99,9%
p95 de leitura:        < 200 ms
Perda sob pico:        <= 5% (teto do desafio; meta interna < 1%)
```

Processamento assíncrono:

```text
consolidado_processing_lag_seconds:     p99 <= 5 s em operação normal
outbox_oldest_message_seconds:          alerta acima de 60 s
profundidade da fila consolidado.projecao (via management do RabbitMQ): alerta em crescimento sustentado
```

A meta interna de erro fica 5 vezes abaixo do teto do requisito. O teto é contrato; a folga absorve a degradação natural do sistema antes que ela vire violação.

Com essas três métricas o time responde às duas perguntas operacionais que importam neste desenho: o consolidado está atrasado? Quanto?

## Capacidade para 50 req/s

A consulta do consolidado é um index scan de uma linha por chave `(merchant_id, data)`. Para o requisito de 50 req/s, a primeira versão mantém o PostgreSQL como read model da consulta. Os testes de carga (`tests/load/consolidado.js`, ver Verificação) validaram essa decisão no ambiente local utilizado para execução; os resultados são evidência de capacidade e headroom da implementação, não benchmark ou SLA de produção.

Por isso não há cache. Redis entraria como componente extra, com invalidação para acertar e mais um modo de falha, para resolver um problema que não existe nesta escala. Se as métricas mostrarem o banco pressionado pela leitura, o caminho está descrito na evolução.

Rodamos duas réplicas de cada API em produção mesmo assim, por disponibilidade e deploy sem downtime, não por capacidade.

No caminho assíncrono, o gargalo teórico é o publisher da outbox (lotes de 100 a cada 500 ms, 200 msg/s). Está uma ordem de grandeza acima da carga e o primeiro ajuste é um parâmetro, não uma refatoração.

## Resiliência e modos de falha

| Falha | Efeito no usuário | Comportamento do sistema |
|-------|-------------------|--------------------------|
| Worker do consolidado fora | Consulta responde dado defasado, com `atualizadoEm` dizendo isso | Fila durável retém; ao voltar, processa o backlog e converge. Escrita intacta |
| API de Consolidado fora | Consulta indisponível | Escrita intacta. É o requisito central operando |
| RabbitMQ fora | Nenhum na escrita | Outbox acumula; publisher drena quando o broker volta. `outbox_oldest_message_seconds` denuncia o atraso |
| Postgres do consolidado fora | Consulta falha | Escrita intacta em produção (instâncias separadas). No compose local a instância é compartilhada e a queda afeta os dois lados; a fronteira lógica por schema e usuário é o que torna a separação uma mudança de configuração. Eventos aguardam na fila |
| Postgres de lançamentos fora | Escrita indisponível | Único ponto que para o núcleo, por decisão: a fonte da verdade não se contorna sem sacrificar integridade. Mitigação em produção: instância gerenciada multi-AZ com failover |
| Mensagem duplicada | Nenhum | Dedup por id do evento; contador de duplicados cresce, saldo não |
| Mensagem malformada | Nenhum | DLQ direto, fila não trava, alerta para investigação |
| Falha transitória na projeção | Atraso de segundos | Fila de espera com TTL de 5 s, até 5 tentativas, depois DLQ |

O retry não usa nack com requeue de propósito: requeue devolve a mensagem imediatamente para a cabeça da fila e queima as tentativas em milissegundos, exatamente quando a dependência ainda está fora. A fila de espera com TTL e dead-letter de volta para a fila principal dá o intervalo entre tentativas que o requeue não dá.

## Consistência

O lançamento confirmado é a fonte de verdade; o consolidado é uma projeção. Existe um intervalo entre a confirmação de um lançamento e sua presença no consolidado, tipicamente abaixo de 2 s no ambiente local (polling de 500 ms mais o consumo). Esse intervalo é monitorável (`consolidado_processing_lag_seconds`) e comunicado na resposta da API (`atualizadoEm`, `consistencia: eventual`).

Entrega é at-least-once. Não prometemos exactly-once distribuído porque ele não existe na prática; o que existe é efeito exatamente-uma-vez, obtido com dedup transacional no consumidor. Duplicata vai chegar, e o teste de integração prova que ela não tem efeito.

## RPO e RTO

Propostos para produção, dependentes de validação com o negócio:

```text
RPO: <= 5 min   (point-in-time recovery do Postgres gerenciado)
RTO: <= 1 h     (infraestrutura recriável por configuração; restore documentado)
```

Backup diário com retenção de 30 dias e restore testado periodicamente. Backup que nunca foi restaurado é uma pasta com nome esperançoso, não um plano de recuperação.

## Verificação

| Requisito | Como é verificado |
|-----------|-------------------|
| Escrita sobrevive à queda do consolidado | `IndisponibilidadeTests.Worker_indisponivel...` e `scripts/demo.sh` no CI |
| Escrita sobrevive à queda do broker | `IndisponibilidadeTests.Broker_indisponivel...` |
| Zero perda e zero duplicação | Testes de outbox, idempotência e dedup na integração |
| 50 req/s com perda <= 5% | `tests/load/consolidado.js` com thresholds de erro < 1% e p95 < 200 ms. Executar contra ambiente representativo; números de CI compartilhado não são benchmark |
