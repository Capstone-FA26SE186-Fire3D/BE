# Local integration tests

Run `dotnet test Fire3D/Fire3D.slnx` from BE. Docker Desktop must be running for ScenarioStoreTests and IfcWriteSqlTests. They create and dispose PostgreSQL containers automatically.

For all database tests, set the following process environment variables to a **disposable loopback PostgreSQL server** whose test user can create databases:

- `FIRE3D_IFC_TEST_CONNECTION`
- `FIRE3D_TEST_ADMIN_CONNECTION`
- `FIRE3D_RESET_TEST_ADMIN`

Use the same isolated local server for all three; do not use the application's Supabase connection. Test fixtures create randomly named databases and drop only their own databases. Without these variables some tests skip; check both failed and skipped counts.

## Fixture contracts

- EF `EnsureCreated` only creates mapped schema. Auth tests additionally execute `database/002_password_reset_recovery.sql` and `003_local_password.sql` in the newly created database.
- Retry uses `Fire3D.IfcTests/retry-contract.sql`, containing the actual v6.7 hash/outbox/requeue functions and outbox table copied from Docs `fire_evacuation_schema.sql` at `addd4df`, plus the `current_attempt_id` column needed by the gate. The SQL runs through Npgsql, so braces in SQL regexes are not interpreted as EF format arguments.
- This retry fixture checks store/gate/replay/audit integration against real PostgreSQL. It does **not** apply the complete production schema, security-definer ownership/grants, all triggers, dispatcher or consumer contracts. Synchronize the fixture when those source SQL functions change; passing tests are not proof of deployed Supabase schema or least-privilege permissions.
- Test PostgreSQL enum mapping preserves label case. Seed data includes active users/tenants/buildings and required source-document fields.
- Scenario draft concurrency uses the actual PostgreSQL `xmin`; it is not a version counter starting at 1.
- Auth test fixture deletes only the default FirebaseApp it created, after disposing the host. This prevents repeated hosts in the same test process from colliding. The production Firebase initialization remains unchanged. No live Google token/email/S3 round trip is certified by this suite.
- Login with a valid password for an unavailable account returns documented `403 ACCOUNT_DISABLED`. Existing access tokens and refresh requests are rejected with `401`. Assertions preserve this distinction and verify session revocation.

For evidence use `--logger trx --results-directory .codex/local/test-results`; these local results must remain ignored.
