# Deployment readiness and smoke test (#64)

The deployment of `b6a7d74` (and later `973c29f`) answered `500` on Building detail/create, scenarios, billing and Learn reads while `/health` stayed `Healthy`. The common cause is a database that lags the code: the app starts, but the first query that touches a table or column from a missing migration fails.

## Endpoints

| Endpoint | Meaning |
| --- | --- |
| `GET /health` | Liveness only (process is up). It never checks the database, so a DB outage does not make the host restart the app. |
| `GET /health/ready` | Readiness: the database is reachable, every EF migration in this build is applied, and enabled Redis processing dependencies pass their read-only checks. `200 {"status":"Healthy"}` or `503` with dependency codes under `checks`. |
| `GET /health/version` | Commit of the running image. |

`/health/ready` codes:

| Code | Meaning | Action |
| --- | --- | --- |
| `OK` | Ready | — |
| `MIGRATIONS_PENDING` | `checks.database.data.pendingMigrations` lists the migration IDs missing from `__EFMigrationsHistory` | Apply them (see below), then re-check |
| `DATABASE_UNREACHABLE` | Connection failed | Check the connection string / network / pooler |
| `DATABASE_CHECK_FAILED` | The check threw; `data.error` is the exception type only | Read the app log for the same time |

The response never contains the connection string. The raw SQL recovery files (`002_password_reset_recovery.sql`, `003_local_password.sql`, `005_email_verification.sql`) are not EF migrations and are not covered by this check.

### Processing readiness

When `ProcessingWorker:Transport` is `RedisStreams` and either dispatcher or consumer is enabled, `/health/ready` includes the tagged `processing` check. `/health` remains process liveness. The processing probe performs Redis `PING` and read-only `XLEN` on the configured stream, then checks the database login's effective gate `EXECUTE` privilege without calling any gate:

| Dependency | Connection / required gate | Failure codes |
| --- | --- | --- |
| Redis streams | Configured Redis connection and stream keys | `PROCESSING_REDIS_UNAVAILABLE` |
| Enabled dispatcher | `DispatcherExecutor` / `processing_stream_dispatch_gate(text,jsonb)` | `PROCESSING_DISPATCHER_FORBIDDEN`, `PROCESSING_DISPATCHER_UNAVAILABLE` |
| Enabled consumer | `DefaultConnection` / `processing_stream_consumer_gate(text,jsonb)` | `PROCESSING_CONSUMER_FORBIDDEN`, `PROCESSING_CONSUMER_UNAVAILABLE` |
| Recovery (either loop enabled) | `DefaultConnection` / `processing_stream_recovery_gate(text,jsonb)` | `PROCESSING_RECOVERY_FORBIDDEN`, `PROCESSING_RECOVERY_UNAVAILABLE` |

Database checks require a restricted login: superuser or `BYPASSRLS` roles fail readiness even if they can execute the gate. `FORBIDDEN` means the effective privilege/login check failed; `UNAVAILABLE` means the connection/probe failed. The probe never calls the remote HTTP worker, writes streams, creates consumer groups, or claims/recovers jobs. Redis reads do not prove write ACLs or an end-to-end worker handoff; verify those separately through the authorized operational flow.

## Unhandled errors

An unexpected exception still returns ProblemDetails `500`, now with `code: "INTERNAL_ERROR"` and `traceId`. Search the app log by `traceId`; handler errors keep their own codes.

## Azure release gate

`.github/workflows/azure-deploy.yml` tests the deployment helper on pull requests. Release runs build a commit-tagged candidate, then run `tools/deployment/migration_preflight.py` **before** `azure/webapps-deploy`. Production deployment runs are serialized and are not cancelled when a newer run starts. Images are deployed by commit tag; the workflow no longer updates the mutable `latest` tag.

Set the required GitHub Actions secret **`DEPLOY_PREFLIGHT_CONNECTION`** to a PostgreSQL URI for the **same database used by the production app**:

```text
postgresql://PREFLIGHT_USER:URL_ENCODED_PASSWORD@DATABASE_HOST:5432/DATABASE_NAME?sslmode=require
```

Use a dedicated database account with only connection access, schema usage on `public`, and `SELECT` on `public."__EFMigrationsHistory"`. Do not use a migration-owner or administrator credential. The runner must be able to reach that database. The secret is a PostgreSQL URI, **not** an Npgsql `Host=...;Password=...` connection string. Percent-encode reserved characters in the username/password. Supported query parameter: `sslmode=require`, `verify-ca`, or `verify-full` (default `require`; certificate verification modes also need the appropriate trusted CA on the runner).

### Production setup (BE#148, applied 21/10/2026)

Production uses the dedicated login `fet3d_deploy_preflight`, created once by the database owner in the Supabase SQL editor. The password is chosen by the operator and stored only in the GitHub secret:

```sql
CREATE ROLE fet3d_deploy_preflight LOGIN PASSWORD '<random letters and digits>'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS NOREPLICATION CONNECTION LIMIT 3;
ALTER ROLE fet3d_deploy_preflight SET default_transaction_read_only = on;
ALTER ROLE fet3d_deploy_preflight SET statement_timeout = '10s';
GRANT CONNECT ON DATABASE postgres TO fet3d_deploy_preflight;
GRANT USAGE ON SCHEMA public TO fet3d_deploy_preflight;
GRANT SELECT ON public."__EFMigrationsHistory" TO fet3d_deploy_preflight;
-- The history table has row level security (policies only for fire3d_api and fet3d_backend_executor).
-- Without this policy the login sees zero rows and the gate fails with "Migration history is empty or invalid".
CREATE POLICY deploy_preflight_read ON public."__EFMigrationsHistory" FOR SELECT TO fet3d_deploy_preflight USING (true);
```

Through the Supabase pooler (Supavisor) the user name carries the project reference: `fet3d_deploy_preflight.<project-ref>`, for example `postgresql://fet3d_deploy_preflight.<project-ref>:<password>@aws-0-<region>.pooler.supabase.com:5432/postgres?sslmode=require`. To rotate, `ALTER ROLE ... PASSWORD` and replace the secret; nothing else uses this login.

Release order for a pull request that adds a migration: review its SQL, back up `public` (`pg_dump -n public -Fc`), apply the EF script for exactly the new migrations with `psql -v ON_ERROR_STOP=1`, check `__EFMigrationsHistory` and the changed functions, and only then merge. Merging first is safe but the deployment stops at the gate until the migration is applied; rerun the failed job afterwards.

The helper extracts migration IDs from `[Migration("...")]` attributes in the checked-out candidate, including designer files, and checks that each exists in the database history. It uses a static `SELECT` inside `BEGIN READ ONLY` / `ROLLBACK`, disables psql startup files and password prompts, and bounds connection, query and process time. Credentials are passed via process environment, never command arguments or logs; psql diagnostics are suppressed to avoid exposing connection details. Treat the runner as trusted because its process environment contains the credential. See [PostgreSQL environment variables](https://www.postgresql.org/docs/current/libpq-envars.html).

Missing/invalid secrets, unreadable/empty candidate migrations, connection errors, missing/empty/inaccessible history, timeouts, or missing migration IDs fail the job before Azure changes its running image. The workflow never applies migrations. Apply reviewed migrations separately through the authorized database process, then rerun deployment. A missing secret intentionally blocks the first release after this change.

This gate verifies recorded EF migration history, not the complete live schema or manually executed recovery SQL. It assumes the secret targets the production database and the default `public` history table. Changes to migration compilation/discovery or the history schema require updating this helper. Schema compatibility of separately applied migrations with the currently running image still needs release review.

## Post-deployment verification

After deployment:

1. `GET /health/version` must return `200` with a JSON `version` exactly equal to the candidate commit SHA. The workflow retries for at most 30 attempts.
2. `GET /health/ready` must return `200`; otherwise the job reports the readiness response.
3. Anonymous reads (`/api/learn/situations`, `/api/learn/posts`) and `/openapi/v1.json` must return `200`. Redirects, authentication errors, missing routes and server errors all fail.

Every HTTP request has a 10-second connection timeout and 20-second total timeout. These checks can still fail after the new image is running (for example, dependency failure after preflight). A failed smoke test does not automatically roll back or prove the database is behind. Inspect the failing endpoint and readiness codes, then use the authorized release process to restore the previously validated commit-tagged image if needed; verify schema compatibility first. No Azure slot or additional control-plane credential is assumed.

Run helper regression tests locally without a database:

```sh
python -m unittest discover -s tools/deployment -p 'test_*.py'
```

## Backup, restore and recovery (BE#142)

There is no Down migration. Recovery is either a restore from backup or a reviewed forward migration that repairs the problem.

**Backup** before every production migration (about 45 s, ~1.7 MB on 23/10/2026): `pg_dump -n public -Fc -f backup_public_<UTC timestamp>.dump`
with the migration login. Keep the file outside the repository; it contains personal data and password hashes.

**Restore drill (verified 23/10/2026, production backup at 130 migrations).** Restore into a disposable database first, never over
production without a decision to roll back:

1. Use PostgreSQL 17 with pgvector, e.g. `docker run -d --name fet3d-restore-drill -e POSTGRES_PASSWORD=<random> -p 127.0.0.1::5432 pgvector/pgvector:pg17`.
2. Create the roles the dump references (owners, RLS policies, grants), as NOLOGIN, before restoring:
   `fire3d_api, fet3d_backend_executor, fet3d_ifc_upload_owner, fet3d_integration_owner, fet3d_payos_ledger_owner, fet3d_payos_local_api,
   fet3d_payos_local_request, fet3d_payos_local_webhook, fet3d_payos_request_executor, fet3d_payos_webhook_executor, fet3d_processing_executor,
   fet3d_dispatcher_executor, fet3d_processing_worker, fet3d_ai_indexer, fet3d_ai_service, fet3d_auth_maintenance_owner, fet3d_avatar_cleanup_owner,
   fet3d_password_recovery_owner, fet3d_pending_cleanup_owner, fet3d_deploy_preflight, service_role, supabase_admin, anon, authenticated`.
   Check the current list with `SELECT DISTINCT r FROM pg_policies, unnest(roles) r WHERE schemaname='public'` and the owners/grantees of `public`.
3. Recreate the Supabase extensions in schema `extensions` and put it on the search path:
   `CREATE SCHEMA extensions; CREATE EXTENSION vector SCHEMA extensions; CREATE EXTENSION pgcrypto SCHEMA extensions;
   CREATE EXTENSION btree_gist SCHEMA extensions; ALTER DATABASE <db> SET search_path = public, extensions;`
   (`knowledge_chunks` uses `vector`; exclusion constraints need `btree_gist`.)
4. `pg_restore -d <db> --no-comments backup.dump`. The only expected error is `schema "public" already exists`.
5. Compare `__EFMigrationsHistory` and the main table counts with production (`users`, `buildings`, `revisions`, `revision_artifacts`,
   `processing_jobs`, `scenario_versions`, `releases`, `sessions`, `session_results`, `service_entitlements`, `payment_transactions`,
   `billing_ai_quota_grants`, `audit_logs`).
6. Run the API against it with a LOGIN that is a member of `fire3d_api` and `fet3d_backend_executor` (not a superuser; several gates
   refuse superuser/BYPASSRLS logins), workers and PayOS disabled. `/health/ready` must be 200; log in and read a Building, entitlements,
   AI quota, a job and a Trainee's session result.

Result of the drill: restore in 2 s; 130 migrations and all counts above identical to production; the API read the restored state
(login, Building, entitlements, 1100 AI credits, package job `Succeeded`, analytics 10 plays/8 completed, a `Passed` result).

**S3.** Object storage is not part of the database backup. Every key referenced by `revision_artifacts`, `source_documents`
(verified uploads), `release_packages` (package and manifest) and `users.avatar_storage_key` must exist with the stored SHA-256/size.
Drill 23/10/2026: 34/34 objects present and matching (13.7 MB). Restoring a database older than the bucket is safe (extra objects are
ignored or cleaned); restoring a bucket older than the database is not, so do not delete worker outputs or sources by hand.

**Backlog checks** (read-only; run after incidents and before releases):
`SELECT status, last_error, count(*) FROM integration_outbox_events GROUP BY 1,2`; `processing_jobs` still `Queued` after 30 minutes;
`processing_job_attempts` `Running` with `lease_until < now()`; `payos_webhook_inbox` not `Applied`; `notification_deliveries` `Failed`;
`ai_requests` `NeedsReconcile`. Known on 23/10/2026: one outbox/job `Failed` (Windows playtest job 2e3d04ba, failed on purpose on 19/10) and
one PayOS inbox row `NeedsReconcile` with `PAYOS_UNKNOWN_ORDER` for order code 123, the test webhook PayOS sends when the webhook URL is saved.

## Render configuration

The Azure workflow also calls `processing-tests.yml` and requires it to pass before the production deploy job. It exercises Redis/worker recovery and readiness against disposable PostgreSQL 17 and Redis 7.4 services. These tests do not use deployment credentials or production data.

The Blueprint's `dockerContext: .` is the BE repository root, while `dockerfilePath` remains `./Fire3D/Dockerfile`. This provides the `Fire3D/` directory expected by the Dockerfile and the root `database/` SQL files embedded by Infrastructure. `.dockerignore` excludes local tooling, build output and secret files while retaining SQL and contract resources. See [Render's Docker context settings](https://render.com/docs/blueprint-spec#docker).

Set `Jwt__SigningKey` (not `Jwt__Key`) to a Base64-encoded random key with at least 32 decoded bytes; for example generate it securely with `openssl rand -base64 32` and store the output only in Render's secret environment settings. Set `AuthEmail__FrontendUrl` (not `Auth__FrontendUrl`) to the actual frontend origin used in authentication emails. Existing services need these environment keys updated as well as the Blueprint. Render's `/health` check remains liveness; the Azure preflight does not protect direct Render deploys, which require the same reviewed migration check before release.
