# ADR-003: Transactional Outbox na publicação de eventos

Status: aceito. Data: 2026-08-13.

## Contexto

Registrar um lançamento exige duas escritas em sistemas distintos: o banco e o broker. Sem transação comum entre eles, qualquer ordem de operações abre uma janela de inconsistência (dual-write): commit no banco seguido de falha na publicação deixa um lançamento que o consolidado nunca verá; a ordem inversa cria evento de um lançamento que não existe. Em dado financeiro, essa divergência é silenciosa e permanente, a pior combinação.

## Decisão

Persistimos o lançamento e a intenção de publicação (linha na tabela `outbox`, mesma transação, mesmo banco). Assim a API não depende da disponibilidade do broker no momento da escrita e eliminamos a janela de inconsistência entre essas duas operações. Um hosted service faz polling da outbox (lote 100, intervalo 500 ms), publica com publisher confirms e marca `publicado_em` na mesma transação do lote.

`SELECT ... FOR UPDATE SKIP LOCKED` permite múltiplas réplicas do publisher sem publicação duplicada e sem leader election. Falha na publicação aborta a transação do lote: as linhas continuam pendentes e serão tentadas de novo. A semântica resultante é at-least-once, e o consumidor é idempotente por isso.

A ordem de publicação entre réplicas não é garantida, e não precisa ser: a projeção recalcula e é indiferente à ordem (ADR-004).

## Alternativas consideradas

Publicar direto no broker durante o request: é o dual-write descrito acima, não uma alternativa.

Transação distribuída (2PC): indisponibilidade de um participante trava o outro; suporte fraco entre Postgres e RabbitMQ; transforma duas falhas independentes numa falha conjunta.

CDC (Debezium lendo o WAL): tecnicamente superior em latência e sem polling, ao custo de dois componentes operacionais a mais (Connect + Debezium). Para ganhar algumas centenas de milissegundos que nenhum usuário do consolidado percebe, não se paga hoje. Gatilho: exigência de lag abaixo de ~1 s.

## Consequências

Broker fora do ar vira atraso observável (`outbox_pending_total`, `outbox_oldest_message_seconds`), não perda. O preço: a tabela outbox precisa de expurgo das linhas publicadas, o polling adiciona ~250 ms de latência média à propagação, e duplicatas são possíveis por construção, tratadas no consumidor. Testes de integração cobrem a atomicidade, a retenção com broker parado e a drenagem quando ele volta.
