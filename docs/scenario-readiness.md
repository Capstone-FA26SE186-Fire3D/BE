# Technical readiness and content approval

`POST /api/revisions/{revisionId}/confirm-for-training` takes `{ "scenarioVersionId": "...", "validationRunId": "...", "annotationSetId": null }` and returns `{ "reviewId": "..." }`. OrganizationUser in the Building tenant or PlatformAdmin is required. The accepted worker attempt, artifact, revision, immutable scenario hash and validation run must match exactly. Annotation must equal the run's annotation (null is allowed only when the accepted run has no annotation). Passed/runtime-ready output and no Error/Critical issues are required. A confirmation of version A cannot authorize version B. Replay of the same attestation returns the original review.

`POST /api/revisions/{revisionId}/reviews` remains technical rejection, including the exact version/run/annotation and a nonblank reason; it does not approve content or erase confirmations.

Content review is separate:

1. OrganizationUser calls `POST /api/scenario-versions/{id}/submit`, `Idempotency-Key: submit-001`, no body. Response 201 includes `reviewId`, `status`, server `contentHash` and `rubricHash`.
2. PlatformAdmin calls `POST /api/admin/scenario-versions/{id}/approve` with another Idempotency-Key and `{ "contentHash": "<from submit>", "rubricHash": "<from submit>" }`. Response 200 is Approved.
3. Alternatively reject at `/reject` with the same hashes plus `reason` (1–4000 characters). A rejected/approved decision is immutable; changes require a new scenario version and submission.

Read APIs allow frontend recovery without retaining review IDs in browser storage:

- PlatformAdmin: `GET /api/admin/scenario-reviews?status=Submitted&organizationId=<uuid>&page=1&pageSize=20` returns `{items,totalCount,page,pageSize}`. Status is Submitted, Approved or Rejected; page size is 1–100; order is submittedAt/reviewId descending.
- PlatformAdmin: `GET /api/admin/scenario-reviews/{reviewId}` returns review metadata, frozen content/rubric/objectives/instructions and current technical readiness.
- OrganizationUser in the version's tenant or PlatformAdmin: `GET /api/scenario-versions/{versionId}/review` returns the same detail. Cross-tenant returns 404; revoked/expired family returns 401. No direct review-table SELECT is granted to runtime.
- `GET /api/scenario-versions/{versionId}` and `GET /api/scenarios/{scenarioId}/versions` include reviewStatus (NotSubmitted/Submitted/Approved/Rejected), reviewId and rejectReason. Unsubmitted versions have null reviewId/rejectReason. A missing review detail returns 404.

Detail places hashes, submittedBy/At, decidedBy/At, reason and organization/Building/scenario names under `review`; content and rubric are separate top-level fields. `readiness` reports validationRunId, outcome, blockerCount (Error/Critical), confirmationReviewId, candidateArtifactId and technicalReady. This is current technical information, not a substitute for the publish gate. Authorized detail may contain the rejection reason; audit views still exclude free-text reasons. Legacy content absent from the stored snapshot remains null; no content or readiness is invented.

Migration `20261010101000_AddScenarioReviewReads` adds private projections and an actor/session-aware read gate. [Manual tests and rollout](publish-review-manual-test.md) distinguish source tests from deployment.

Missing/invalid key: 400 IDEMPOTENCY_KEY_REQUIRED. Same actor/operation/key/input replays without another audit; changed input: 409 IDEMPOTENCY_KEY_CONFLICT. Wrong provenance/hash, blockers or nonpending review: 409 with the specific code. Foreign tenant: 404; wrong role: 403; inactive actor: 401. SQL gate rechecks lifecycle under the shared lifecycle lock. Mutation, receipt and audit commit atomically.

Migrations are additive: content reviews and command receipts; no legacy approval/readiness is invented. An UPDATE(id) grant to the restricted NOLOGIN gate owner permits row locks only in practice: the immutable scenario trigger still rejects changes. Runtime callers have EXECUTE, not direct review DML. GrantIfcGateDependencies repairs hash/outbox EXECUTE grants by temporarily setting the existing integration-owner role and restoring membership; this was tested with a nonsuperuser migration identity.

Evidence: isolated PostgreSQL actual migration history, limited runtime role, concurrent confirmation, wrong run/annotation/hash, role/lifecycle, receipt conflict, immutable decisions and injected audit failure rollback. Worker/package output is simulated. No actual IFC/Unity, runtime client, publish or training acceptance is implied. Supabase Tasks 2–6 and the dependency-grant repair were applied on 2026-10-07; see [ifc-authoring-deployment.md](ifc-authoring-deployment.md) for counts, privileges and deployment limits.
# Live session enforcement

Confirm, technical rejection, submit, approve and reject derive session family
from the authenticated JWT. The gate locks lifecycle then actor then resource,
rechecks the live family after lock waits and before receipt replay, and returns
401 for a revoked/expired/missing family. The family is not part of the canonical
receipt input. Legacy family-less SQL entrypoints fail closed after migration.
# Safe audit details

New submission/approval/rejection records include a versioned safe audit snapshot of review status, review/version identifiers, actor and timestamps. Technical confirmation/rejection adds the exact revision, validation, artifact and optional annotation IDs. Mutations, receipts and audit remain in the same transaction. Audit detail projects only approved scalar fields; it excludes reasons, content/rubric hashes and content, email, phone, credentials and signed URLs. Legacy `ScenarioVersion` audits without the new contract marker remain metadata-only with `changes: []`; no history is backfilled.
