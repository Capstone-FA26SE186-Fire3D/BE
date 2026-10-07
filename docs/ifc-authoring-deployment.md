# Selected API database rollout — 2026-10-07

Scope: Tasks 1-9 (schema; two source review findings remain) on `feature/ifc-authoring-support-hardening`. Supabase schema rollout is verified; deployed API artifact and real IFC/Blender/Unity/S3 clients are not certified by it. No database reset, automatic duplicate repair or synthetic production Trial was performed.

## Applied migrations

| Migration | Change |
| --- | --- |
| 20261006120000_AddBoundIfcUploads | Durable IFC upload intents/candidates/receipts/cleanup; verified immutable source metadata and restricted upload gate. Legacy sources stay unverified. |
| 20261006130000_AddProcessingWorkerGates | Attempts, worker/delivery/command receipts, candidate outputs, canonical source/job binding and accepted-output provenance; HTTP dispatch/worker/recovery gates with least privileges. |
| 20261006140000_AddScenarioAuthoringContracts | Durable authoring receipts, locked draft/version numbering, immutable v7 state/rubric/learner snapshot and server package-build input. |
| 20261006145000_GrantIfcGateDependencies | Forward repair of hash/outbox EXECUTE dependencies for the existing NOLOGIN gate owner; temporary membership/schema grants restored. |
| 20261006150000_AddScenarioReadinessAndApproval | Content-review history and command receipts, exact technical readiness and separate content approval/rejection SQL gate. No approval is invented for legacy versions. |
| 20261006160000_AddPlaytestLifecycle | Immutable package/session pins, command receipts, prepare/start/runtime compatibility gates; nullable service_entitlement_id so prepare does not consume or assign quota. |

Task 1 Building contract/validation changes required no migration. Existing organizations/users/password/session data were not rewritten. Every SQL update used EF-generated additive migrations, bounded lock/statement timeouts, preflight and history/grant checks.

Preflight and postcheck: users **8**, organizations **6**, buildings **2**; source documents/jobs/drafts/versions/artifacts/reviews/playtests/entitlements **0**. Counts were preserved. No invalid job hash or duplicate logical-job/draft/version number was found. These are a point-in-time verification, not a promise that future activity cannot change counts.

Postcheck: `fire3d_api` has EXECUTE on readiness/playtest gates, no INSERT/UPDATE/DELETE on revision_reviews, content reviews, readiness receipts, playtest sessions/pins/receipts. PUBLIC has no EXECUTE on the gates. Gate owner is NOLOGIN/NOSUPERUSER/NOBYPASSRLS, has no schema CREATE and no payment ledger INSERT/UPDATE; its entitlement mutation grant is only UPDATE(playtest_units_used). RLS is enabled on the protected tables. The helper runtime-compatibility function is private to the gate owner.

## API/worker activation

Deploy the matching branch binary before exercising the newly gated mutations. An old binary that writes directly to protected tables can now fail with insufficient privilege. Schema rollout does not mean the API host already runs this source.

Keep HTTP dispatcher disabled until a trusted, separately authenticated worker endpoint is configured. IFC upload requires an explicit positive IfcUpload MaxBytes and enabled cleanup; no business size limit was guessed. The new playtest preparation requires a real accepted PlaytestPackage for the exact immutable version. Start stays disabled until separate Playtest signing configuration is supplied; then requires an active catalog and explicit paid BuildingService entitlement or remaining Trial units. No seed/default entitlement was added to Supabase.

See [scenario-readiness.md](scenario-readiness.md), [playtest-manual-test.md](playtest-manual-test.md), [processing-worker.md](processing-worker.md), [scenario-authoring.md](scenario-authoring.md) and [ifc-upload-manual-test.md](ifc-upload-manual-test.md).

## Test evidence

- Task 5/6 final selected PostgreSQL actual migration history + restricted roles + HTTP/OpenAPI/provider fixtures: **14 passed, 0 failed, 0 skipped**.
- Auth/Billing/PayOS regression: **401 passed, 0 failed, 17 skipped**; all 17 skipped legacy auth DB tests were subsequently enabled against the isolated loopback server: **17 passed, 0 failed, 0 skipped**. Those legacy auth tests use partial DDL. The selected task tests separately use actual migrations. One additional metadata case was added after the broad run and passed in the final selected group.
- IFC final regression: **105 passed, 0 failed, 0 skipped**, including 11 loopback SQL read/editor tests. Those 11 use read-contract fixtures; migration/grant/race/rollback checks reside in the selected actual-history PostgreSQL tests. Docker-only IFC write/store/legacy playtest fixtures were excluded because Docker was unavailable.
- Final solution build: **0 warnings, 0 errors**.

Initial regression failures were retained in local logs: Testing host lacked early DefaultConnection, then a read fixture still seeded Running output and a pre-migration column spelling. Scoped local-only placeholder configuration and corrected accepted-output fixtures resolved them; no account/provider validation assertion was removed and shared User Secrets were not used as test data. Native isolated PostgreSQL 17 was used because Docker was unavailable.

Tasks 7-9 source has been committed. Task 7/8 schema rollout is recorded below. Publish, learner start/sync/result, real IFC/Unity and production runtime acceptance remain unfinished. No main merge was performed.

## Tasks 7–8 additive rollout and privilege repair — 2026-10-07

Applied in one transaction, from verified history ending20261006160000:

| Migration | Changes |
|---|---|
|20261007100000_AddReleaseAndBuildingAccess|Building visibility/default Private, access revision/code hash; account-bound participation grants; immutable release package/provenance, Build receipts and authorized gate; no legacy approval invented.|
|20261007110000_AddSupportCommandContracts|Feedback/ticket revision, content constraints, command receipts, pagination indexes, append-only messages and atomic support gate; no message content rewritten.|
|20261007120000_HardenSelectedGatePrivileges|Convert inherited/direct table-wide Building INSERT/UPDATE grants to legacy-column grants; remove protected access-column and custom-login support/release DML grants; restrict client EXECUTE; add access-writer trigger. Temporary gate-owner membership restored.|

Read-only postcheck after commit verified all three history entries, RLS, runtime EXECUTE, no direct protected DML and preserved legacy Building name INSERT/UPDATE. Gate owner stays NOLOGIN/NOSUPERUSER/NOBYPASSRLS and without schema CREATE. Client anon/authenticated and PUBLIC cannot execute release/support gates. Counts remain users8, organizations6, buildings2, feedback/tickets/messages/releases/trainings0; both existing Buildings Private/access revision1. No reset or provider call.

Tests before rollout: 15 targeted Release/Support/ACL tests passed; strengthened ACL test passed with actual migrations under a nonsuperuser migration identity, custom/inherited grants, restored membership and future overgrant attempts. Broad regression before the privilege repair: Auth428 passed, IFC105 passed (Docker-only legacy IFC fixtures excluded). Binary/client/provider deployment is separate. IFC cross-Building receipt and Failed geometry anchor findings remain open; see [progress checklist](task-progress-checklist.md).

Post-repair verification: 4 Building HTTP/PostgreSQL lifecycle/audit regression tests passed; solution build0 warnings/0 errors. Raw provider tests and deployed API smoke tests were not run.
