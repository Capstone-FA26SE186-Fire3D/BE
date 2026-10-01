# Building billing / PayOS

Requirements: Docs FR-BILLING-01..10, workflows §11, technology §9/10. This implementation uses the current Building address source `building_locations.address`; it does not create a second address on `buildings`.

## Task 1: database foundation

`20261002090000_AddBuildingBilling` runs the embedded `Billing/Schema.sql`. It retains legacy quotation/package fields and rows; new quotes use their Building lines. `20261002090100_SyncBuildingBillingModel` records the EF model only; it does not repeat the DDL. Financial migrations do not offer a destructive down migration: recover with a forward migration.

Adds discount rules, quotation lines/snapshots/revision, per-Building entitlements, per-line provisioning records, reminder storage, enterprise requests, command receipts, checkout operations and verified-webhook inbox. The last three tables supply recovery storage, not a running checkout/dispatcher. Legacy header package FK becomes nullable and is not authoritative for new quotes. Existing quotes are retained; incomplete legacy quotes are not silently converted into purchasable lines.

Dedicated NOLOGIN request/webhook executors receive only EXECUTE on their respective SECURITY DEFINER payment functions. They do not inherit the NOLOGIN ledger owner or have direct payment DML. PUBLIC, Supabase `anon` and `authenticated` are denied financial-table access. Actual deployment must use separated least-privilege backend identities, not a superuser/service owner, and verify role memberships. Never grant the webhook executor to the general API login. SQL records adapter attestations; the future .NET adapter must verify signatures before invoking the webhook function.

The PayOS options section has `Enabled`, `ClientId`, `ApiKey`, `ChecksumKey`, `ReturnUrl`, `CancelUrl`. It remains disabled; enabling before the checkout/webhook task fails startup. Real credentials stay in ignored local appsettings/User Secrets or deployment secret storage. No payment adapter, provider request, entitlement worker or Supabase migration is implied by this foundation.

## Verification

Billing tests opt in through `FET3D_BILLING_TEST_ADMIN`, accept only a loopback PostgreSQL host and the `postgres` admin database, and create/drop a dedicated `fet3d_billing_test_*` database for each test. They never read application settings or User Secrets. Tests execute the real additive migration on a model-created relational baseline; this does not certify the full historical migration chain or production grants.

Example: `FET3D_BILLING_TEST_ADMIN=Host=127.0.0.1;Port=<disposable-port>;Database=postgres;Username=<test-admin>` followed by `dotnet test Fire3D/Fire3D.AuthTests --filter FullyQualifiedName~Billing`.
