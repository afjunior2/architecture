-- Inicialização do PostgreSQL local.
-- Dois schemas e dois usuários: a regra "nenhum serviço lê a tabela do outro"
-- vale por privilégio de banco, não por convenção. app_lancamentos não enxerga
-- o schema consolidado e vice-versa. Em produção seriam instâncias separadas;
-- a fronteira lógica é a mesma e a migração é de configuração.

CREATE SCHEMA IF NOT EXISTS lancamentos;
CREATE SCHEMA IF NOT EXISTS consolidado;

CREATE ROLE app_lancamentos LOGIN PASSWORD 'app_lancamentos';
CREATE ROLE app_consolidado LOGIN PASSWORD 'app_consolidado';

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CONNECT ON DATABASE fluxodecaixa TO app_lancamentos, app_consolidado;
-- CREATE no database é necessário para o CREATE SCHEMA IF NOT EXISTS idempotente do boot.
GRANT CREATE ON DATABASE fluxodecaixa TO app_lancamentos, app_consolidado;

GRANT USAGE, CREATE ON SCHEMA lancamentos TO app_lancamentos;
GRANT USAGE, CREATE ON SCHEMA consolidado TO app_consolidado;
