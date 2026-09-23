# IFC API implementation progress

Current task branch: fix/auth-registration (continued IFC work). Earlier entries below are historical evidence, not current deployment certification.
Target: section 4 of the 88-operation checklist (13 endpoints).
Documentation schema reference: Docs origin/develop v6.7; actual deployment schema must be verified separately.

| Endpoint | Implementation | Validation |
|---|---|---|
| GET /api/revisions/{id} | Database account authorization; tenant via Building; admin cross-organization read; excludes deleted/inactive Building/organization; metadata only; documented errors | Handler tests and Release build; live Supabase integration pending correct connection |

Read-only metadata check of the locally saved Supabase connection did not show the target IFC tables.
No migration or production data changes were made. Existing Firebase onboarding/linking security findings are outside this endpoint change and remain a deployment blocker; these handler tests do not certify the authentication pipeline.

| GET /api/buildings/{id}/revisions | Database actor/tenant checks; bounded pagination; active Building and organization; explicit 400/401/403/404 | IFC suite 17 passed; Release build 0 warnings/errors; DB integration pending |

2026-09-20: merged current .codex guidance (local password/session plus optional Firebase Google, OneShield edge). Latest design reference: Docs addd4df. Saved DefaultConnection/SupabaseMigration metadata still shows the legacy processing_jobs layout; no application schema was changed.

- GET /api/revisions/{revisionId}/processing-jobs: implemented scoped paginated logical-job list; 22 handler tests passed and Release build succeeded. No lease tokens or storage credentials in DTO.

- GET /api/processing-jobs/{jobId}: current attempt with job/input-hash provenance, tenant scope, no lease credential. Restored excluded EF enum mappings and replaced legacy revision composite navigation joins after SQL tests caught missing columns. 29 tests passed (26 unit, 3 isolated-local-PostgreSQL read-contract tests), 0 skipped; Release build passed. Read fixture is not full production schema/permission validation.
`n- GET /api/validation-runs/{validationRunId}: scoped historical run with job/attempt provenance; 32 tests passed (including 4 PostgreSQL tests), Release build passed.
- GET /api/processing-jobs/{jobId}/qa: current-attempt-only validation pagination; 36 tests passed, including 5 SQL tests; Release build passed.
- GET /api/revisions/{revisionId}/issues: paged issues with validation/attempt provenance and historical flag; 41 tests passed, 6 PostgreSQL tests; Release build passed.
- GET /api/revisions/{revisionId}/artifacts: metadata-only with job/attempt provenance; 46 tests passed, 7 PostgreSQL tests; Release build passed.
- GET /api/revisions/{revisionId}/bim-facts: paged typed facts with source hash and quality flags; 51 tests passed, 8 PostgreSQL tests; Release build passed.

## Editor APIs — 2026-09-23

- [x] D05 GET /api/buildings/{buildingId}/editor-preview: revisionId required; tenant scoped; successful current Geometry attempt and input-hash provenance; same-artifact transform/floors/semantic mapping; five-minute signed S3 GET; Ready/NotReady.
- [x] P12 GET /api/revisions/{revisionId}/annotations: current immutable snapshot, empty version 0, ETag.
- [x] P12 PUT /api/revisions/{revisionId}/annotations: If-Match required (428), stale version (412), IFC anchor validation, append-only version; revision lock and atomic annotation/audit transaction. Label/note overlay only, no geometry/exit mutation.
- Targeted tests: 8 passed, 0 skipped, including isolated PostgreSQL concurrency, tenant/provenance and audit-failure rollback. This fixture exercises these SQL contracts, not all production triggers/RLS/permissions.
- Regression run initially found processing logs pagination missing the created_at alias; corrected logged_at AS created_at. Two legacy Testcontainers tests require Docker, which is unavailable in this environment.
- After the fix: 97 IFC tests passed, 0 skipped with the two Docker-dependent test classes explicitly excluded. Full solution build: 0 warnings, 0 errors. This is not a claim that the unfiltered 99-test suite passed.
- Not verified: live S3 object existence/download, worker metadata output and deployed Supabase schema/permissions. No Supabase migration or production data mutation performed. See api-docs.md for request/response and worker metadata contract.

## Docker follow-up verified — 2026-09-23

- IFC: 99/99 passed, 0 skipped, including real retry/outbox SQL and Docker scenario flow.
- Auth: 69/69 passed, 0 skipped, after provisioning raw SQL recovery tables, cleaning up fixture-owned FirebaseApp, and aligning disabled-login expectations with documented 403 ACCOUNT_DISABLED (refresh/access remain 401).
- Fixed fixtures: PostgreSQL enum mapping, required active seed records and source metadata, real xmin concurrency values; replay asserts one outbox event and one audit.
- Reproduction and scope: [integration-tests.md](integration-tests.md). No deployed Supabase schema/permissions or live Firebase/Mailgun/S3 certification implied.
