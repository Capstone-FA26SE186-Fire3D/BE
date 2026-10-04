# Backend database permissions

Migration `20261004090000_HardenBackendObjectPermissions` closes Supabase default grants on backend-only Avatar, installation, legacy reset, support-message and migration-history tables. It enables RLS and preserves existing backend grants with policies for `fire3d_api`/`fet3d_backend_executor`. It explicitly denies `PUBLIC`, `anon` and `authenticated` execution of existing payment gates, preserving request/webhook executor grants and their `SET LOCAL ROLE` contract.

The migration changes default ACLs for its own owner: future public tables/sequences/functions are not automatically granted to Supabase REST roles. The global default PUBLIC function EXECUTE is revoked for this owner, so new functions must grant their intended callers explicitly. It does not change defaults owned by Supabase-managed roles or remove data. Apply with the reviewed schema/migration identity that owns these objects; a different owner requires a separately reviewed grant/default-ACL change.

Before deployment, record identity, migration history, row counts, policies, default ACLs and executor grants. After deployment confirm client roles cannot read/write these backend tables or execute payment gates, backend RLS access still works, and executor role membership/EXECUTE remains separated. History alone does not prove correct permissions. Do not use an application superuser or drop/reset the database.

The PostgreSQL regression reproduces explicit Supabase-style default grants, preserves backend reads and payment executor access, checks new object defaults and unchanged existing account data. This does not certify HTTP Supabase API exposure or provider payment behavior.

The IFC enqueue and refresh-cleanup migrations transfer functions to restricted NOLOGIN owners. PostgreSQL 17 requires both SET permission and inherited object privileges for this operation; CREATEROLE's automatic ADMIN-only membership is insufficient. The migrations temporarily grant SET/INHERIT and schema CREATE, then restore the original membership options and CREATE privilege before commit. Regression covers a non-superuser schema owner, existing ADMIN-only membership and function execution under RLS, including deletion of an expired family while preserving an active family.

## Deployment evidence — 2026-10-05

The authorized existing Supabase database received only these three additive migrations, in a transaction with target/history checks, statement/lock timeouts and postconditions:

| Migration | Delivered behavior |
| --- | --- |
| `20261004090000_HardenBackendObjectPermissions` | Backend table RLS/client ACL hardening, explicit denial on payment gates and migration-owner default ACL corrections |
| `20261004091000_AddIfcIntegrationOutbox` | Event/consumer-receipt tables, canonical SHA-256 envelope, tenant enqueue, immutable trigger and restricted owner/grants |
| `20261004092000_AddRefreshCleanupGate` | Bounded retention gate with lifecycle/user locks and restricted maintenance owner; no general API DELETE |

History advanced from 29 to 32. Existing user IDs and refresh IDs were checked in the deployment transaction: 4 accounts and 16 refresh tokens remained. Payment executor function grants were unchanged. A subsequent read-only check using the configured restricted API login confirmed account/token access, EXECUTE on enqueue/cleanup, no direct refresh DELETE or outbox DML, and no anon/authenticated access to the eight affected backend tables or inspected financial/maintenance entrypoints. Migration history was verified separately using the migration identity; the runtime API does not need access to that table. No test business mutation or provider call was made against Supabase.

Before deployment, the generated idempotent SQL was applied twice to a disposable PostgreSQL 17 database, retaining 32 history entries. Validation: Auth/Billing/PayOS suite 243 passed; IFC regression 104 passed plus 5 Docker Testcontainers tests passed; all groups had 0 failed/0 skipped. Solution build passed with 0 warnings/0 errors. Docker classes were run separately after Docker became available. Test connections were explicit disposable loopback/container connections, independent of User Secrets.

This is database deployment evidence, not an Azure binary rollout, provider acceptance test or production login benchmark. Deploy the corresponding BE branch and measure App Service p50/p95/p99 separately. Full IFC delivery/worker result gates and the remaining product schema v7 modules remain backlog.
