# Trade-offs e evolução

## Trade-offs assumidos

Cada linha registra o que ganhamos, o que pagamos e quando a decisão deve ser revista. Decisão sem custo declarado é marketing, não arquitetura.

| Decisão | Benefício | Custo | Alternativa considerada | Quando revisar |
|---------|-----------|-------|-------------------------|----------------|
| Dois serviços com domínios de falha separados | Consolidado cai, escrita continua | Duas unidades de deploy, consistência eventual | Monolito modular: mais simples, mas processo único é domínio de falha único | Se o requisito de isolamento deixar de existir (improvável no domínio) |
| Comunicação só assíncrona entre os lados | Sem acoplamento temporal | Latência de segundos até o consolidado refletir | REST síncrono: violaria o requisito na primeira falha do consolidado | Não revisar; é o coração do desenho |
| Transactional Outbox com polling | Sem dual-write; broker fora não perde nada | Componente a mais, ~250 ms de latência média do polling | Publicar direto no broker (a janela de inconsistência é o bug); CDC com Debezium (melhor latência, dois componentes operacionais a mais) | Se o lag exigido cair abaixo de ~1 s, avaliar CDC |
| At-least-once + consumidor idempotente | Modelo honesto de entrega | Dedup obrigatória, tabela de eventos processados | "Exactly-once" de framework: não sobrevive a falha entre efeito e ack | Não revisar |
| Projeção recalculável com cópia local | Duplicata, desordem e retroativo viram não-problemas | Lado de leitura armazena volume similar ao da escrita | Acumulador O(1): frágil aos três cenários acima | Se o volume por merchant/dia crescer ordens de grandeza, coalescer recálculos |
| CQRS sem Event Sourcing | Modelos separados sem custo de replay/versionamento de eventos históricos | Sem viagem no tempo | ES completo: capaz de mais e caro de operar e versionar | Gatilhos no ADR-005 |
| Sem cache (Redis) | Um componente e um modo de falha a menos | Toda leitura vai ao banco | Redis cache-aside | Quando métricas mostrarem o banco pressionado pela leitura, não antes |
| RabbitMQ, não Kafka | Operação simples, retry e DLQ nativos | Sem replay nem retenção longa | Kafka: retenção e throughput que 50 req/s não pedem | Necessidade de replay, múltiplos consumidores pesados ou volume 100x |
| Publisher como hosted service da API | Um contêiner a menos | Escala junto com a API | Processo dedicado | Se o volume de publicação justificar ciclo de vida próprio |
| Postgres único com 2 schemas no local | `docker compose up` leve | Menos fiel à produção | Duas instâncias locais | Manter; a fronteira por GRANT preserva o que importa |
| Autenticação fora do MVP | Foco no que o desafio avalia | Header substitui claim | Keycloak no compose: peso sem valor avaliativo | Antes de qualquer exposição real |
| EnsureCreated/DDL no boot (local) | Ambiente sobe sem passo manual | Não serve para produção | Migrações versionadas | Primeira evolução de schema em produção exige pipeline expand/contract |

Fora de escopo por decisão, com caminho preparado: estorno (entra como lançamento compensatório, nunca UPDATE; o modelo append-only já assume isso), multi-moeda (coluna e chave da projeção mudariam; registrado como extensão cara), múltiplas contas, extrato paginado, categorias, conciliação, ledger de partidas dobradas.

## Evolução arquitetural

A primeira versão é pequena de propósito. O que segue não é backlog, é mapa: cada estágio tem o gatilho que o aciona. Nenhum item do estágio 2 ou 3 é necessário para 50 req/s.

### Estágio 1: volume atual (até centenas de req/s)

O que está no repositório: Postgres, RabbitMQ, serviços stateless, outbox, consumidor idempotente, observabilidade básica. Escala horizontal simples: mais réplicas de API (o publisher tolera concorrência via SKIP LOCKED) e mais réplicas do worker (competing consumers).

Primeiros ajustes baratos, se o lag subir: lote e intervalo do publisher (um parâmetro), coalescer recálculos da mesma chave `(merchant, data)` dentro do lote consumido.

### Estágio 2: crescimento (milhares de merchants, milhares de req/s agregadas)

Acionado por métricas, na ordem provável de aparecimento dos sintomas:

Leitura pressionando o banco: read replica do Postgres para a API de Consolidado, depois cache com TTL curto se a réplica não bastar. Nessa ordem, porque réplica não exige lógica de invalidação.

Tabela de lançamentos crescendo (dezenas de milhões de linhas): particionamento por data (range mensal), que também barateia retenção e arquivamento. Partição por merchant só se aparecer hot partition real; hash por merchant no consumo distribui o processamento antes disso.

Consumo atrasando com o volume: mais consumidores com particionamento por merchant (filas particionadas por hash ou consistent hash exchange), preservando ordem por chave onde vier a importar.

Operação: autoscaling por métrica de fila e de CPU, ambientes por IaC, migrações expand/contract no pipeline, tracing atravessando o broker via traceparent na mensagem.

### Estágio 3: escala bancária

Acionado por criticidade e regulação, além de volume:

Mensageria: Kafka (ou log distribuído equivalente) quando existir necessidade real de replay massivo, múltiplos consumidores independentes do mesmo stream ou retenção longa como fonte de reprocessamento. Migração viável porque o contrato do evento já é versionado e o consumidor já é idempotente. Junto vêm schema registry e governança de contratos.

Resiliência geográfica: multi-região com DR ativo/passivo primeiro (RPO minutos, RTO abaixo de 1 h); ativo/ativo só com requisito explícito, porque dinheiro em ativo/ativo exige resolução de conflito que não se adota por estética. Isolamento por célula (cell-based) quando o raio de explosão de um incidente precisar ser limitado por fatia de clientes.

Proteção de fluxo: rate limiting por merchant na borda, backpressure explícito no consumo, load shedding com 429 e Retry-After em vez de degradar todo mundo por timeout.

Regulatório e segurança: trilha de auditoria imutável separada dos logs, retenção fiscal formalizada, segregação de funções (quem opera não altera saldo), gestão de chaves com HSM/KMS, LGPD com anonimização que alcança todas as cópias do dado (fonte, fila, DLQ, projeção), reconciliação contábil automática entre fonte e projeção com alerta em divergência.

Confiabilidade como processo: SLOs formais com orçamento de erro decidindo prioridade entre feature e estabilidade, game days do cenário "consolidado fora", teste de restore como rotina.

## O critério que atravessa tudo

Quando simplicidade e integridade financeira conflitam, integridade vence: é por isso que outbox, idempotência dupla e recálculo estão na primeira versão. Quando sofisticação e simplicidade conflitam sem requisito que desempate, simplicidade vence: é por isso que Redis, Kafka, Kubernetes e Event Sourcing estão neste documento e não no docker-compose.
