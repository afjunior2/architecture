// Teste de carga da API de Consolidado (k6).
//
// Execução local, com o ambiente do compose de pé:
//
//   k6 run tests/load/consolidado.js                          (cenário base, 50 req/s)
//   k6 run -e CENARIO=100 tests/load/consolidado.js           (100 req/s)
//   k6 run -e CENARIO=250 tests/load/consolidado.js           (250 req/s)
//
// Os thresholds refletem os objetivos de docs/non-functional-requirements.md:
// erro < 1% e p95 < 200 ms no cenário base. Resultados dependem da máquina;
// não publicamos números que não medimos.

import http from 'k6/http';
import { check } from 'k6';

const URL = __ENV.URL || 'http://localhost:8082';
const RPS = parseInt(__ENV.CENARIO || '50', 10);
const MERCHANT = __ENV.MERCHANT || '11111111-1111-1111-1111-111111111111';

export const options = {
  scenarios: {
    consolidado: {
      executor: 'constant-arrival-rate',
      rate: RPS,
      timeUnit: '1s',
      duration: '2m',
      preAllocatedVUs: 20,
      maxVUs: 200,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],           // meta interna; o teto do requisito é 5%
    http_req_duration: ['p(50)<50', 'p(95)<200', 'p(99)<500'],
  },
};

export default function () {
  const hoje = new Date().toISOString().slice(0, 10);
  const res = http.get(`${URL}/api/v1/consolidado/${hoje}`, {
    headers: { 'X-Merchant-Id': MERCHANT },
  });

  check(res, {
    'status 200': (r) => r.status === 200,
    'tem saldo': (r) => r.json('saldo') !== undefined,
    'informa frescor': (r) => r.json('consistencia') === 'eventual',
  });
}
