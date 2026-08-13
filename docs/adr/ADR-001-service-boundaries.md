# ADR-001: Fronteira de serviços entre Lançamentos e Consolidado

Status: aceito. Data: 2026-08-13.

## Contexto

O desafio exige que o registro de lançamentos continue disponível se o consolidado cair. Além disso, os dois lados têm perfis opostos: a escrita precisa de consistência forte e perda zero; a leitura tolera defasagem de segundos e perda de até 5% das consultas em pico. Perfis de falha e de consistência incompatíveis num mesmo processo obrigam o lado crítico a herdar os riscos do lado tolerante.

## Decisão

Dois serviços com domínios de falha independentes: Lançamentos (API + outbox publisher) e Consolidado (worker + API de leitura). Sem chamada síncrona entre eles em nenhuma direção; o único artefato compartilhado é o contrato do evento (`FluxoDeCaixa.Contracts`). Cada serviço é dono exclusivo do seu armazenamento, com a fronteira garantida por privilégio de banco desde o ambiente local.

Não é uma migração para microsserviços em geral: são exatamente dois serviços, o mínimo que o requisito de isolamento pede. Granularidade maior adicionaria custo operacional sem atender nenhum requisito melhor.

## Alternativas consideradas

Monolito modular: superior em simplicidade, transação local única, um deploy. Descartado por um único motivo, decisivo: unidade de processo é unidade de falha. Um vazamento de memória na consolidação derrubaria o registro de vendas, violando o requisito por construção.

Processos separados com banco compartilhado: isolaria o processo, mas manteria acoplamento por schema e um domínio de falha comum no banco; um lock ou uma migração do consolidado afetaria a escrita.

## Consequências

O requisito central passa a valer por construção e é verificado por teste (arquitetura: assemblies não se referenciam; integração: worker derrubado não afeta a escrita). O preço: consistência eventual entre os lados (ADR-004), duas unidades de deploy e a necessidade de comunicação assíncrona confiável (ADR-002, ADR-003).
