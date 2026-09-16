# Validation — prepared Supabase baseline

Date: 2026-09-16. Target schema: PostgreSQL17+. Hosted Supabase baseline applied atomically through the session pooler; read-only verify.sql passed.

| Check | Result |
|---|---|
| Generated schema | 37 tables,30 functions; source hashes captured |
| Apply as non-superuser migration owner | PASS |
| Core SQL invariants | 51 PASS |
| Phase2/PayOS SQL invariants | 6 PASS |
| RLS/client function and table privileges | PASS |
| Restricted auth/admin runtime writes | PASS,rolled back |
| Unrelated managed object privileges | Preserved |
| Second baseline apply | Rejected at preflight |
| Read-only verify.sql | PASS |

Test runner: supabase/test-local.ps1. Database/roles are random isolated local resources, removed in finally; no application database schema/data was migrated. Existing login response changes and running BE were left intact.

Known limitation: local PostgreSQL role model does not reproduce every Supabase managed restriction. Apply to a fresh staging project and verify before changing the API connection. Hosted PostgreSQL 17.6 migration and restricted Npgsql runtime connection verified. Client SSL Mode=Require; certificate verification and Google Cloud deployment are not yet validated.