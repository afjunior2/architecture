#!/usr/bin/env bash
# Roteiro de demonstração de ponta a ponta, contra o ambiente do docker compose.
# Prova, na prática, o requisito central: o registro de lançamentos continua
# funcionando com o consolidado fora do ar, e o saldo converge quando ele volta.
#
# Reexecutável sem recriar o banco: cada execução gera um RUN_ID (UUIDv4) e deriva
# dele as chaves de idempotência usadas nos lançamentos. A tabela "idempotencia"
# não expira (por desenho: replay tem que ser detectável a qualquer momento), então
# reusar uma chave literal faria a segunda execução receber 200 (replay) onde a
# primeira recebeu 201 (criação) -- não é bug da API, é o script assumindo estado
# que só existe em banco limpo. O estado inicial do consolidado (contagem e saldo)
# também é lido antes de agir, e as asserções seguintes usam deltas sobre ele, não
# valores absolutos -- o script não pode assumir banco vazio.
#
# Uso: ./scripts/demo.sh
set -euo pipefail

LANC=${LANC:-http://localhost:8081}
CONS=${CONS:-http://localhost:8082}
MERCHANT="11111111-1111-1111-1111-111111111111"
HOJE=$(date -u +%F)

# UUIDv4 via python3 (já é dependência obrigatória do script, usado para JSON).
# date +%s sozinho colide se dois demos disparam no mesmo segundo; UUIDv4 usa
# os.urandom e não depende de instalar nada além do que o script já exige.
RUN_ID=$(python3 -c 'import uuid; print(uuid.uuid4())')
CHAVE_CREDITO="demo-$RUN_ID-c1"
CHAVE_DEBITO="demo-$RUN_ID-d1"

passo() { echo; echo "==> $1"; }

falha() { # mensagem [esperado] [observado]
  echo "DEMO FALHOU: $1" >&2
  if [ $# -ge 3 ]; then
    echo "  esperado:  $2" >&2
    echo "  observado: $3" >&2
  fi
  exit 1
}

registrar() { # tipo valor chave
  curl -sS -o /tmp/resp.json -w '%{http_code}' -X POST "$LANC/api/v1/lancamentos" \
    -H "Content-Type: application/json" \
    -H "X-Merchant-Id: $MERCHANT" \
    -H "Idempotency-Key: $3" \
    -d "{\"tipo\":\"$1\",\"valor\":$2,\"descricao\":\"demo\"}"
}

saldo() {
  curl -sS "$CONS/api/v1/consolidado/$HOJE" -H "X-Merchant-Id: $MERCHANT"
}

quantidade() { saldo | python3 -c 'import sys,json;print(json.load(sys.stdin)["quantidadeLancamentos"])'; }
saldo_valor() { saldo | python3 -c 'import sys,json,decimal;d=json.load(sys.stdin, parse_float=decimal.Decimal);print(d["saldo"])'; }

# Soma decimal exata (Decimal, não float) -- valores monetários não podem
# carregar erro de ponto flutuante nas comparações abaixo.
soma() { python3 -c "from decimal import Decimal; print(Decimal('$1') + Decimal('$2'))"; }
decimal_igual() { python3 -c "from decimal import Decimal; import sys; sys.exit(0 if Decimal('$1') == Decimal('$2') else 1)"; }

echo "RUN_ID: $RUN_ID"

passo "0. Ler estado inicial do consolidado (o banco pode já ter dados de execuções anteriores)"
QTD_INICIAL=$(quantidade)
SALDO_INICIAL=$(saldo_valor)
echo "Estado inicial: $QTD_INICIAL lançamento(s), saldo $SALDO_INICIAL."

passo "1. Registrar crédito de 100.00 e débito de 30.00"
codigo=$(registrar CREDITO 100.00 "$CHAVE_CREDITO")
[ "$codigo" = "201" ] || falha "crédito não aceito" "201" "$codigo"
codigo=$(registrar DEBITO 30.00 "$CHAVE_DEBITO")
[ "$codigo" = "201" ] || falha "débito não aceito" "201" "$codigo"

passo "2. Aguardar projeção e consultar o consolidado"
QTD_APOS_NOMINAL=$((QTD_INICIAL + 2))
SALDO_APOS_NOMINAL=$(soma "$SALDO_INICIAL" 70.00)
for i in $(seq 1 30); do [ "$(quantidade)" = "$QTD_APOS_NOMINAL" ] && break; sleep 1; done
[ "$(quantidade)" = "$QTD_APOS_NOMINAL" ] || falha "consolidado não convergiu no cenário nominal" "$QTD_APOS_NOMINAL lançamentos" "$(quantidade) lançamentos"
decimal_igual "$(saldo_valor)" "$SALDO_APOS_NOMINAL" || falha "saldo divergente após o cenário nominal" "$SALDO_APOS_NOMINAL" "$(saldo_valor)"
saldo | python3 -m json.tool

passo "3. Parar o Consolidado Worker"
docker compose stop consolidado-worker

passo "4. Registrar 5 lançamentos com o worker parado"
for i in 1 2 3 4 5; do
  codigo=$(registrar CREDITO 10.00 "demo-$RUN_ID-off-$i")
  [ "$codigo" = "201" ] || falha "lançamento $i rejeitado com worker parado" "201" "$codigo"
done
echo "5 lançamentos aceitos com o consolidado indisponível."

passo "5. Confirmar que o consolidado ainda não viu os novos lançamentos"
[ "$(quantidade)" = "$QTD_APOS_NOMINAL" ] || echo "aviso: worker processou antes de parar (aceitável)"

passo "6. Religar o worker e aguardar convergência"
docker compose start consolidado-worker
QTD_FINAL=$((QTD_INICIAL + 7))
SALDO_FINAL=$(soma "$SALDO_INICIAL" 120.00)
for i in $(seq 1 60); do [ "$(quantidade)" = "$QTD_FINAL" ] && break; sleep 1; done
[ "$(quantidade)" = "$QTD_FINAL" ] || falha "backlog não convergiu após o worker voltar" "$QTD_FINAL lançamentos" "$(quantidade) lançamentos"
decimal_igual "$(saldo_valor)" "$SALDO_FINAL" || falha "saldo divergente após a convergência do backlog" "$SALDO_FINAL" "$(saldo_valor)"
saldo | python3 -m json.tool
echo "Nenhum lançamento perdido: $QTD_FINAL lançamentos, saldo $SALDO_FINAL."

passo "7. Reenviar requisição com a chave de idempotência do crédito desta execução"
codigo=$(registrar CREDITO 100.00 "$CHAVE_CREDITO")
[ "$codigo" = "200" ] || falha "repetição deveria retornar 200 (replay), não criar lançamento novo" "200" "$codigo"
sleep 3
[ "$(quantidade)" = "$QTD_FINAL" ] || falha "repetição duplicou o lançamento" "$QTD_FINAL lançamentos" "$(quantidade) lançamentos"
decimal_igual "$(saldo_valor)" "$SALDO_FINAL" || falha "repetição duplicou o saldo" "$SALDO_FINAL" "$(saldo_valor)"
echo "Requisição repetida (mesma chave de idempotência) não duplicou o lançamento nem o saldo."

echo
echo "DEMO OK"
