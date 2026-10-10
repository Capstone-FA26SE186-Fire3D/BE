# Publish and scenario review: frontend acceptance

These contracts supersede the issue's historical baseline b6a7d74. Source and disposable PostgreSQL/HTTP tests do not prove an Azure deployment, real Unity worker or learner launch.

## Rollout

Apply the additive migrations `20261010100000_AddPublishClientReceipts` and `20261010101000_AddScenarioReviewReads` before deploying the matching binary. Existing receipt/approval data remains intact. Publish stays disabled by default; set `Publishing__Enabled=true` only after inspecting grants and testing the eligible path. This task does not apply these two migrations to Supabase or change Azure settings.

The migration identity must be able to manage schema CREATE grants and administer the existing gate-owner role. Both scripts temporarily enable only the necessary owner membership/schema privileges and restore their original state before commit. Runtime identities receive EXECUTE on authenticated gates, without direct receipt/review DML or review-table SELECT. The nonsuperuser migration test checks ownership and restoration explicitly.

## Review queue and reload

1. Log in as the OrganizationUser owning the version. Submit with a fresh Idempotency-Key. Preserve the returned hashes for the decision, not as an authorization credential.
2. Log in as PlatformAdmin. In Swagger call `GET /api/admin/scenario-reviews` with status Submitted, page 1 and pageSize 20. organizationId is an optional exact filter. Response is `{items,totalCount,page,pageSize}`; IDs and names allow opening work after reload.
3. Open `GET /api/admin/scenario-reviews/{reviewId}`. Check `review.contentHash`, `review.rubricHash`, submitter/time, organization/Building/scenario names, frozen `content`, `rubric`, objectives/instructions and `readiness`.
4. Approve with the pinned hashes or reject with hashes and reason. Refresh the queue/detail: status and decision actor/time must change; a rejection must retain its reason.
5. Switch to the OrganizationUser. Read `GET /api/scenario-versions/{versionId}` and the scenario version list: reviewStatus/reviewId/rejectReason must survive reload. Read full review through `GET /api/scenario-versions/{versionId}/review`.
6. Repeat detail using an OrganizationUser from another tenant: 404. OrganizationUser calling the Admin queue: 403. No Bearer token: 401. Invalid status/page: 400 VALIDATION_ERROR. Unsubmitted versions report NotSubmitted with null IDs/reason; review detail is 404.

The detail shape is `{review:{...},content:{...},rubric:{...},learningObjectives:...,learnerInstructions:...,readiness:{validationRunId,outcome,blockerCount,confirmationReviewId,candidateArtifactId,technicalReady}}`. Missing legacy snapshots remain null. This endpoint does not provide signed package URLs.

## Publish and revoke

1. Use a Built release with an active matching Training, an effective paid Building entitlement, Approved exact content/rubric hashes, exact revision/version confirmation and accepted current package/manifest/runtime provenance. Trial is insufficient.
2. In Swagger call `POST /api/releases/{releaseId}/publish`, no body, header `Idempotency-Key: publish-test-001`. Expected 200 ReleaseResponse with status Published, publishedAt/publishedBy and pinned package metadata.
3. Repeat with the same key: the same original response, no additional publication or audit. Reusing the key for a different release gives 409 IDEMPOTENCY_KEY_CONFLICT once that resource is in the caller's scope.
4. `GET /api/releases/{releaseId}` must read Published. A logged-in Trainee with current Public/private participation access sees its Active Training through `GET /api/buildings/{id}/trainings`. Listing does not allocate learner seats or start sessions.
5. Revoke through the existing endpoint. GET now reads Revoked and the Training disappears from Trainee listing. Replay of an earlier successful publish key returns the original Published receipt, **without republishing**; read GET for current state. A fresh publish key on Revoked returns RELEASE_NOT_BUILT.

| Missing prerequisite | HTTP/code |
|---|---|
| Idempotency-Key or malformed input | 400 VALIDATION_ERROR |
| Revoked/expired/missing session family | 401 UNAUTHORIZED |
| Foreign tenant or inactive resource | 404 NOT_FOUND |
| Release is not Built (fresh operation) | 409 RELEASE_NOT_BUILT |
| Matching Training inactive | 409 TRAINING_INACTIVE |
| Effective paid entitlement | 409 BUILDING_ENTITLEMENT_REQUIRED |
| Approved pinned content/rubric | 409 CONTENT_APPROVAL_REQUIRED |
| Exact current confirmation/validation provenance | 409 RELEASE_READINESS_REQUIRED |
| Validation outcome is not Passed | 409 RELEASE_QA_NOT_PASSED |
| Error/Critical blocker | 409 RELEASE_QA_BLOCKERS_PRESENT |
| Accepted runtime-ready package/manifest | 409 RELEASE_ARTIFACT_REQUIRED |
| Package/runtime metadata compatibility | 409 PACKAGE_COMPATIBILITY_REQUIRED |
| Key reused with different canonical input | 409 IDEMPOTENCY_KEY_CONFLICT |
| Publish rollout flag disabled | 503 PUBLISH_GATE_UNAVAILABLE |

No learner launch/grant implementation is introduced here. Revoke makes publication unavailable to Training listing; existing independent Building participation grants are not deleted. Real Unity output and deployment need separate evidence.
