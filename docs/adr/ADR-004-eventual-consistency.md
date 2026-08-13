# ADR-004: Consistência eventual no consolidado, com projeção recalculável

Status: aceito. Data: 2026-08-13.

## Contexto

Consequência direta dos ADRs 001 a 003: se a escrita não espera o consolidado, o consolidado não pode estar sempre em dia com a escrita. A decisão aqui é dupla: assumir a consistência eventual de forma explícita e escolher como a projeção é mantida.

O enquadramento correto não é o teorema CAP (não há um dado replicado disputando consistência sob partição; há uma fonte e uma projeção derivada). A troca é feita em regime normal, o tempo todo: menos consistência imediata na leitura em troca de isolamento de falha na escrita.

## Decisão

O lançamento confirmado é a fonte de verdade; o consolidado é uma projeção derivada. Existe um intervalo entre a confirmação e a presença no consolidado. Esse intervalo é monitorável (`consolidado_processing_lag_seconds`) e comunicado ao cliente (`atualizadoEm` e `consistencia: eventual` na resposta). Consistência eventual escondida do usuário é descoberta por ele na pior hora possível; comunicada, vira característica do produto.

A projeção recalcula em vez de acumular. O consumidor grava o fato numa cópia local (`lancamentos_recebidos`) e recomputa o consolidado da chave `(merchant, data)` com uma agregação. Dedup por id do evento, cópia e recálculo acontecem numa única transação.

## Alternativas consideradas

Consistência forte (escrita síncrona nos dois lados): viola o requisito central.

Acumulador incremental (`saldo += valor`): O(1) e frágil exatamente nos três cenários que at-least-once e vida real garantem: evento duplicado infla o saldo, desordem diverge, lançamento retroativo corrompe o dia já fechado. O recálculo custa uma agregação sobre dezenas de linhas indexadas e torna os três cenários irrelevantes, além de deixar reprojeção segura (reprocessar nunca piora o estado).

Cópia local versus consultar a origem no recálculo: consultar o banco do outro serviço quebra a fronteira; chamar via HTTP quebra o isolamento. A cópia local, alimentada pelos próprios eventos, é a única opção que preserva a autonomia do consumidor. Custo assumido: o lado de leitura armazena volume comparável ao da escrita; o ganho de CQRS aqui é forma de acesso, não espaço.

## Consequências

Duplicata, desordem e retroatividade deixam de ser casos especiais. O consolidado responde rápido (uma linha por chave) e diz de quando é o dado. O lag em operação normal fica na casa de 1 a 2 segundos (polling mais consumo) e tem métrica e alerta. Testes de integração cobrem duplicata sem efeito duplo e convergência após indisponibilidade.
