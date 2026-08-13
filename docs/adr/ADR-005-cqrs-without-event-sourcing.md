# ADR-005: CQRS simples, sem Event Sourcing

Status: aceito. Data: 2026-08-13.

## Contexto

A separação entre escrita e leitura já existe no problema: o desafio descreve um serviço de lançamentos e um serviço de consolidado. CQRS aqui não foi imposto ao domínio, foi reconhecido nele. A pergunta real é se a fonte de verdade deve ser o estado atual (tabela de lançamentos) ou um stream de eventos (Event Sourcing). Os dois padrões costumam viajar juntos na literatura, mas são independentes.

## Decisão

CQRS sim: modelo de escrita normalizado e protegido por invariantes de um lado, read model desnormalizado do outro, cada um no seu serviço e no seu schema.

Event Sourcing não. A fonte de verdade é a tabela `lancamentos`, que já é append-only por decisão de domínio (sem UPDATE, sem DELETE; correção futura entra como lançamento compensatório). Isso entrega a auditabilidade que o domínio pede sem o custo do ES.

O custo decisivo do ES não é montar; é conviver: versionar eventos históricos imutáveis depois que o modelo mudou três vezes, manter snapshots e replay, treinar o time para depurar por eventos. Não temos requisito que pague essa conta.

## Alternativas consideradas

Event Sourcing completo: seria justificado por reconstrução do estado em qualquer instante do passado por exigência regulatória, auditoria de tentativas além dos fatos confirmados, ou múltiplas projeções com regras temporais divergentes. Nenhum desses requisitos existe hoje. Todos são plausíveis num banco digital, por isso o modelo append-only mantém a porta aberta: migrar seria trabalhoso, não bloqueado.

Modelo único (sem CQRS): obrigaria o mesmo schema a servir escrita transacional e leitura agregada, e o mesmo serviço a falhar junto. Contradiz o ADR-001.

## Consequências

O time opera com ferramentas que já domina (SQL, transação, EXPLAIN). Novas projeções (consolidado mensal, por categoria) nascem dos mesmos eventos sem tocar na escrita. Se um dos gatilhos acima aparecer, este ADR é substituído por um que defina a migração; até lá, ES seria complexidade sem requisito.
