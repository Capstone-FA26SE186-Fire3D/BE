# IFC API implementation progress

Task branch: feature/ifc-api. Each endpoint is committed/pushed separately after its scoped checks.
Target: section 4 of the 88-operation checklist (13 endpoints).
Documentation schema reference: Docs origin/develop v6.7; actual deployment schema must be verified separately.

| Endpoint | Implementation | Validation |
|---|---|---|
| GET /api/revisions/{id} | Database account authorization; tenant via Building; admin cross-organization read; excludes deleted/inactive Building/organization; metadata only; documented errors | Handler tests and Release build; live Supabase integration pending correct connection |

Read-only metadata check of the locally saved Supabase connection did not show the target IFC tables.
No migration or production data changes were made. Existing Firebase onboarding/linking security findings are outside this endpoint change and remain a deployment blocker; these handler tests do not certify the authentication pipeline.
