"""Build a fresh-project Supabase baseline from reviewed Fire3D SQL sources."""
import argparse, hashlib, json, re
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--docs", required=True, type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent
repo = root.parent
names = ["00_types.sql", "10_core.sql", "20_functions.sql", "30_phase2.sql"]
paths = [args.docs / "database" / name for name in names] + [repo / "database/001_auth_refresh_tokens.sql"]
texts = [p.read_text(encoding="utf-8-sig") for p in paths]
tables = re.findall(r"CREATE TABLE\s+(?:public\.)?(\w+)", "\n".join(texts), re.I)
functions = sorted(set(re.findall(r"CREATE (?:OR REPLACE )?FUNCTION\s+(?:public\.)?(\w+)", "\n".join(texts), re.I)))
roles = ["fet3d_payos_ledger_owner", "fet3d_payos_request_executor", "fet3d_payos_webhook_executor", "fire3d_api"]
sql_array = lambda items: "ARRAY[" + ",".join("'" + item + "'" for item in items) + "]"
header = f"""-- Generated fresh-project baseline. No application data or passwords.
-- Apply atomically with Supabase CLI OR psql --single-transaction -v ON_ERROR_STOP=1.
SET LOCAL search_path = public, extensions, pg_catalog;
DO $preflight$
DECLARE n text;
BEGIN
 IF current_setting('server_version_num')::integer < 170000 THEN
  RAISE EXCEPTION 'Fire3D baseline requires PostgreSQL 17 or newer';
 END IF;
 FOREACH n IN ARRAY {sql_array(tables)} LOOP
  IF to_regclass('public.' || n) IS NOT NULL THEN
   RAISE EXCEPTION 'Fresh-project baseline only: public.% already exists', n;
  END IF;
 END LOOP;
 FOREACH n IN ARRAY {sql_array(roles)} LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=n) THEN
   RAISE EXCEPTION 'Role % already exists; review its ownership/permissions before migration', n;
  END IF;
 END LOOP;
END $preflight$;
CREATE SCHEMA IF NOT EXISTS extensions;
CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;
"""
texts[0] = texts[0].replace("CREATE EXTENSION IF NOT EXISTS pgcrypto;", "-- pgcrypto installed above; existing extension location is preserved.")
# Avoid schema-wide REVOKEs touching Supabase-managed/extension objects.
texts[2] = texts[2].replace("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;", "-- App-only permissions applied at end of baseline.")
texts[2] = texts[2].replace("REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;", "-- App-only function permissions applied at end of baseline.")
anchor = "ALTER TABLE payos_payment_requests OWNER TO fet3d_payos_ledger_owner;"
assert anchor in texts[3]
texts[3] = texts[3].replace(anchor, """-- Non-superuser migration owner needs membership and target-owner schema CREATE.
GRANT fet3d_payos_ledger_owner, fet3d_payos_request_executor, fet3d_payos_webhook_executor TO CURRENT_USER;
GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
""" + anchor)
texts[4] = re.sub(r"(?m)^(BEGIN;|COMMIT;)\s*$", "", texts[4])
hardening = f"""
-- Trusted backend group, never grant to anon/authenticated/service_role.
CREATE ROLE fire3d_api NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
GRANT USAGE ON SCHEMA public TO fire3d_api;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;
DO $permissions$
DECLARE n text; r text; f record;
BEGIN
 FOREACH n IN ARRAY {sql_array(tables)} LOOP
  EXECUTE format('REVOKE ALL ON TABLE public.%I FROM PUBLIC', n);
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
    EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I', n, r);
   END IF;
  END LOOP;
  EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', n);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure AS signature, p.prosecdef
  FROM pg_proc p JOIN pg_namespace ns ON ns.oid=p.pronamespace
  WHERE ns.nspname='public' AND p.proname=ANY({sql_array(functions)}) LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f.signature);
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', f.signature, r);
   END IF;
  END LOOP;
  IF NOT f.prosecdef THEN
   EXECUTE format('ALTER FUNCTION %s SET search_path = pg_catalog, public, extensions, pg_temp', f.signature);
  END IF;
 END LOOP;
END $permissions$;
-- Only the auth/admin module is granted to today's backend runtime.
GRANT SELECT, INSERT, UPDATE ON public.users, public.organizations, public.auth_refresh_tokens TO fire3d_api;
GRANT INSERT ON public.audit_logs TO fire3d_api;
CREATE POLICY fire3d_api_users ON public.users TO fire3d_api USING (true) WITH CHECK (true);
CREATE POLICY fire3d_api_organizations ON public.organizations TO fire3d_api USING (true) WITH CHECK (true);
CREATE POLICY fire3d_api_sessions ON public.auth_refresh_tokens TO fire3d_api USING (true) WITH CHECK (true);
CREATE POLICY fire3d_api_audit_insert ON public.audit_logs FOR INSERT TO fire3d_api WITH CHECK (true);
-- SECURITY DEFINER payment functions retain their narrow owner and column grants.
CREATE POLICY fire3d_ledger_user_read ON public.users FOR SELECT TO fet3d_payos_ledger_owner USING (true);
CREATE POLICY fire3d_ledger_quote_read ON public.quotations FOR SELECT TO fet3d_payos_ledger_owner USING (true);
-- No row-level tenant isolation claim: trusted BE must authorize every request.
"""
output = header + "\n".join("\n-- SOURCE: " + p.name + "\n" + text for p, text in zip(paths, texts)) + hardening
(root / "migrations/20260916000100_fire3d_baseline.sql").write_text(output, encoding="utf-8", newline="\n")
manifest = {"sourceSha256": {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}, "tables": tables, "functions": functions, "roles": roles}
(root / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
for name in ["fixtures.sql", "database_core.sql", "database_phase2.sql"]:
 (root / "tests" / name).write_text((args.docs / "tests" / name).read_text(encoding="utf-8-sig"), encoding="utf-8", newline="\n")
print(f"Generated baseline: {len(tables)} tables, {len(functions)} functions; no source data copied.")
# Read-only verification, generated from the same explicit object manifest.
verify = f"""-- Read-only post-migration verification. Run as migration owner, ON_ERROR_STOP=1.
DO $verify$
DECLARE n text; r text; f record;
BEGIN
 FOREACH n IN ARRAY {sql_array(tables)} LOOP
  IF to_regclass('public.'||n) IS NULL THEN RAISE EXCEPTION 'Missing table %',n; END IF;
  IF NOT (SELECT relrowsecurity FROM pg_class WHERE oid=to_regclass('public.'||n)) THEN
   RAISE EXCEPTION 'RLS disabled on %',n;
  END IF;
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) AND
      has_table_privilege(r, 'public.'||n, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') THEN
    RAISE EXCEPTION 'Unexpected client privilege: % / %',r,n;
   END IF;
  END LOOP;
 END LOOP;
 FOREACH n IN ARRAY {sql_array(functions)} LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace ns ON ns.oid=p.pronamespace WHERE ns.nspname='public' AND p.proname=n) THEN
   RAISE EXCEPTION 'Missing function %',n;
  END IF;
 END LOOP;
 FOR f IN SELECT p.oid FROM pg_proc p JOIN pg_namespace ns ON ns.oid=p.pronamespace
  WHERE ns.nspname='public' AND p.proname=ANY({sql_array(functions)}) LOOP
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) AND has_function_privilege(r,f.oid,'EXECUTE') THEN
    RAISE EXCEPTION 'Unexpected client function EXECUTE: % / %',r,f.oid::regprocedure;
   END IF;
  END LOOP;
 END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api' AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolcanlogin)) THEN
  RAISE EXCEPTION 'fire3d_api role attributes are too broad';
 END IF;
 IF NOT has_table_privilege('fire3d_api','public.users','INSERT') OR
    NOT has_table_privilege('fire3d_api','public.auth_refresh_tokens','UPDATE') OR
    has_table_privilege('fire3d_api','public.audit_logs','UPDATE') OR
    has_table_privilege('fire3d_api','public.payment_transactions','INSERT') OR
    pg_has_role('fire3d_api','fet3d_payos_ledger_owner','MEMBER') THEN
  RAISE EXCEPTION 'Runtime role grants do not match auth/admin boundary';
 END IF;
 IF (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='public.payment_transactions'::regclass)<>'fet3d_payos_ledger_owner' THEN
  RAISE EXCEPTION 'Payment ledger owner is incorrect';
 END IF;
END $verify$;
SELECT 'PASS: Fire3D tables/functions/RLS/client denial/runtime grants verified' AS result;
"""
(root / "verify.sql").write_text(verify, encoding="utf-8", newline="\n")
