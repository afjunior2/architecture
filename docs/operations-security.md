# Operação e segurança

## Segurança

O MVP não implementa autenticação real, e isso é uma decisão declarada, não um esquecimento. Implementar OAuth de verdade exigiria um provedor de identidade no compose e afastaria o foco do que o desafio avalia. O desenho de produção fica registrado aqui.

Autenticação e autorização em produção: OIDC com provedor gerenciado (Entra ID, Auth0, Keycloak gerido). Tokens de vida curta, validação por JWKS. O `merchantId` sai de claim do token, nunca de header controlado pelo cliente; aceitar identificador de tenant vindo do cliente é a vulnerabilidade de autorização mais comum em API multi-tenant (BOLA). No MVP o `X-Merchant-Id` existe exatamente onde a claim entraria, então a troca é localizada.

A chave de idempotência já é escopada por merchant na PK `(merchant_id, chave)`. Mesmo sem autenticação, um merchant reutilizando a chave de outro não recebe a resposta alheia. Há teste de integração para isso.

Transporte e dados: TLS 1.2+ na borda em produção (o compose local usa HTTP simples e diz isso). Criptografia em repouso pelo serviço gerenciado de banco. Valores monetários não aparecem em log.

Segredos: nada versionado no repositório; gitleaks roda no CI e falha o build. Local usa defaults de desenvolvimento explícitos no compose. Produção usa cofre de segredos (Key Vault ou Secrets Manager) com identidade gerenciada, sem credencial em variável de configuração de longa duração, e rotação periódica.

Menor privilégio no banco: `app_lancamentos` e `app_consolidado` só enxergam o próprio schema, garantido por GRANT, desde o ambiente local. A fronteira entre os serviços não depende de disciplina de código.

Broker: usuário próprio por serviço em produção, vhost dedicado, TLS entre serviços e broker. Mensagens não carregam dado além do necessário à projeção.

OWASP API: validação de entrada no domínio com erros de código estável (422), limites de tamanho de payload do ASP.NET, paginação inexistente porque não há endpoint de listagem, rate limiting na borda em produção (o middleware nativo do ASP.NET atende; num banco real isso normalmente fica no gateway de entrada da companhia).

Proteção contra replay: a chave de idempotência neutraliza replay de escrita, que é o que importa aqui; replay de leitura não tem efeito colateral.

Auditoria: o modelo append-only é a trilha primária do dinheiro; nada de UPDATE ou DELETE em lançamento. Um requisito regulatório de trilha completa (quem, de onde, quando, incluindo tentativas rejeitadas) adicionaria armazenamento de auditoria separado dos logs operacionais, com retenção própria; está na evolução.

LGPD: o sistema guarda o mínimo (merchant id, valores, descrição livre). Descrição é texto do usuário e pode conter dado pessoal; em produção ela seria classificada assim, ficaria fora de log (já fica) e entraria no procedimento de anonimização. Retenção fiscal de lançamento convive com pedido de eliminação por anonimização do titular, preservando o registro contábil.

## Observabilidade

Logs estruturados em JSON (Serilog), com `servico`, evento, ids de negócio e trace id. Sem valor monetário, sem descrição, sem token. O `IdCorrelacao` da requisição viaja dentro do evento e aparece no log do worker: uma busca liga o POST à projeção.

Métricas via OpenTelemetry, expostas em `/metrics` (formato Prometheus) em cada serviço:

```text
lancamentos_created_total{tipo}
lancamentos_failed_total{codigo}
outbox_pending_total
outbox_oldest_message_seconds
outbox_publish_failures_total
consolidado_events_processed_total
consolidado_events_failed_total{motivo}
consolidado_events_duplicated_total
consolidado_processing_lag_seconds (histograma)
http_server_request_duration_seconds (instrumentação ASP.NET Core)
```

`consolidado_events_duplicated_total` maior que zero é sinal de saúde: prova que o at-least-once produz duplicatas e que a deduplicação as descarta. Se esse contador ficar em zero para sempre, a dedup nunca foi exercitada em produção.

Tracing: instrumentação ASP.NET Core ligada, exportador OTLP habilitado por variável de ambiente (`OTEL_EXPORTER_OTLP_ENDPOINT`). O trace cobre o HTTP; a travessia completa pelo broker (span de publicação e de consumo ligados pelo traceparent na mensagem) está na evolução e o `IdCorrelacao` cobre a lacuna enquanto isso.

## Health checks

| Endpoint | Verifica | Usado por |
|----------|----------|-----------|
| `/health/live` | Só o processo | Orquestrador, para reiniciar |
| `/health/ready` | Banco do próprio serviço | Balanceador, para rotear |

Dois detalhes deliberados. O liveness não verifica dependência nenhuma: se verificasse, uma oscilação do banco faria o orquestrador matar processos saudáveis e transformaria degradação em apagão. E o readiness da API de Lançamentos não inclui o RabbitMQ: broker fora do ar não impede a API de aceitar lançamentos, e derrubar a API nessa hora seria destruir exatamente a propriedade que a outbox existe para dar.

## Operação

Backlog do consolidado atrasado: conferir a profundidade da fila no management do RabbitMQ e `consolidado_processing_lag_seconds`. Se a fila cresce com o worker de pé, escalar réplicas do worker (competing consumers). Se `outbox_oldest_message_seconds` cresce, o problema está entre publisher e broker. Nunca apagar linhas da outbox: cada linha pendente é um evento financeiro ainda não propagado.

Mensagem na DLQ: é malformação ou falha persistente após 5 tentativas. Investigar, corrigir a causa e republicar manualmente para a fila principal. A DLQ não tem consumo automático de propósito.

Reprojeção: como a projeção recalcula a partir de `lancamentos_recebidos`, corrigir um bug de projeção é apagar as linhas afetadas de `consolidado_diario` e reprocessar, ou recomputar direto por SQL. Operação segura e idempotente.

Deploy: serviços stateless atrás de balanceador, rolling update. Migração de schema em produção é etapa do pipeline (expand/contract), nunca do startup; a criação de schema no boot (DDL idempotente sob advisory lock) é conveniência de ambiente local e de teste, e está condicionada a configuração.

Ambiente produtivo de referência: contêineres gerenciados (Container Apps, ECS ou equivalente) com 2+ réplicas por API, Postgres gerenciado multi-AZ com PITR, RabbitMQ gerenciado, WAF na borda, backups com restore testado. Nada disso está em IaC neste repositório de propósito: seria referência não executável, e preferimos documentar a manter Terraform decorativo.
