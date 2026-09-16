#!/usr/bin/env bash
set -euo pipefail
# This runner is exclusively for the disposable PostgreSQL GitHub service.
[[ "${CI:-}" == "true" && "${PGHOST:-}" == "localhost" ]] || { echo "Requires isolated CI localhost service"; exit 1; }
psql -X -v ON_ERROR_STOP=1 -d postgres <<'SQL'
CREATE ROLE fire3d_ci_migrator NOSUPERUSER CREATEROLE NOLOGIN;
CREATE ROLE anon NOLOGIN;
CREATE ROLE authenticated NOLOGIN;
CREATE ROLE service_role NOLOGIN;
CREATE DATABASE fire3d_ci_migration OWNER fire3d_ci_migrator;
SQL
psql -X -v ON_ERROR_STOP=1 -d fire3d_ci_migration --single-transaction \
  -c 'SET ROLE fire3d_ci_migrator' -f supabase/migrations/20260916000100_fire3d_baseline.sql
psql -X -v ON_ERROR_STOP=1 -d fire3d_ci_migration -f supabase/verify.sql
(cd supabase/tests && psql -X -v ON_ERROR_STOP=1 -d fire3d_ci_migration -f database_core.sql)
(cd supabase/tests && psql -X -v ON_ERROR_STOP=1 -d fire3d_ci_migration -f database_phase2.sql)
