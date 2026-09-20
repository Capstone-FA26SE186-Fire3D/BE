# IFC API implementation progress

Task branch: feature/ifc-api. Each endpoint is committed/pushed separately after its scoped checks.
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
