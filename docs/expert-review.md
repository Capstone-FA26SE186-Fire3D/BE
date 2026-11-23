# Thẩm định by FireSafetyExpert (BE#211)

Flow 1 PC06 (FR-PLAN-05, Docs#128). A **FireSafetyExpert** (Chuyên viên PCCC) thẩm định the training content an Organization submits. Wording everywhere: "thẩm định", never "phê duyệt phương án" — under Điều 15 Nghị định 105/2025/NĐ-CP the head of the facility approves the PC06 plan, outside FET3D.

Migrations: `20261124090000_AddFireSafetyExpertRole` (enum label, runs outside a transaction) and `20261124090100_ExpertContentReview`.

## Role

- `FireSafetyExpert` is a value of `UserRole` / `user_role_enum`. The account has **no organization** (like PlatformAdmin and Trainee).
- Created only by PlatformAdmin: `POST /api/accounts` with `role: "FireSafetyExpert"` and no `organizationId` (with one: 400). Self-registration and Google onboarding cannot create it.
- Reads: the queue below, the frozen content of a reviewed version, and the 3D preview of the revision that version uses — for every organization. Nothing else: no drafts, no billing (billing and notification routes answer 403), no other organization data, no edits, no publishing.
- At deploy time at least one expert account must exist, otherwise Submitted items wait. Reviews already Submitted move to the expert queue as they are; Approved ones stay Approved.

## API

All routes: `[Authorize(Roles="FireSafetyExpert")]`. OrganizationUser and PlatformAdmin get 403 — PlatformAdmin has no thẩm định path, not even when no expert exists.

| Method | Route | Notes |
|---|---|---|
| GET | `/api/expert/reviews?kind=&status=&page=&pageSize=` | One shared queue, no assignment. `kind`: `Scenario` \| `Infrastructure` (no Infrastructure items until BE#213). `status`: `Submitted` (default, oldest first) \| `Approved` \| `Rejected` (newest decision first, read-only). Page size 1-100, default 20. |
| GET | `/api/expert/reviews/{id}` | Review item + frozen `content` (fet3d.editor/1 state), `rubric`, `learningObjectives`, `learnerInstructions`. `ETag` = revision. |
| GET | `/api/expert/reviews/{id}/preview` | Same shape as `editor-preview` (signed GLB, floors, anchors) for the revision of that scenario version. |
| POST | `/api/expert/reviews/{id}/approve` | Đạt. Headers `If-Match` (the review ETag) and `Idempotency-Key`. |
| POST | `/api/expert/reviews/{id}/reject` | Chưa đạt. Body `{ "reason": "..." }` 1-2000 characters (else 400 `VALIDATION_ERROR`, `errors.reason`). Same headers. |

Review item: `reviewId, kind, status, revision, scenarioVersionId, revisionId, contentHash, rubricHash, submittedBy(+Name), submittedAt, decidedBy(+Name), decidedAt, reason, organizationId/Name, buildingId/Name, scenarioId/Name, versionNumber`.

### Decision rules

- **Revision / ETag:** a review row changes once (Submitted → Approved | Rejected), so `revision` is `1` while Submitted and `2` once decided; `ETag: "1"`.
- **First decision wins:** the expert who decides second sends a stale `If-Match` and gets `412 PRECONDITION_FAILED`. `decidedBy` records who decided.
- No `If-Match`: `428 PRECONDITION_REQUIRED`; malformed: 400. No `Idempotency-Key`: 400 `IDEMPOTENCY_KEY_REQUIRED`.
- Same `Idempotency-Key` and request: returns the first result, one decision only. Same key, different request: `409 IDEMPOTENCY_KEY_CONFLICT`.
- `409 CONTENT_REVIEW_NOT_PENDING` (already decided, current ETag), `409 CONTENT_HASH_MISMATCH` (content no longer matches the submission).
- **Final:** a decision cannot be changed. A fix is a new scenario version and a new submission; a wrong rejection is resubmitted as a new version. Old decisions stay in the audit log (`audit_logs`, target `ScenarioVersion`, `new_values.decidedByRole = FireSafetyExpert`).

## Result for the Organization

- Status: unchanged reads, e.g. `GET /api/scenario-versions/{id}/review` and the version list review state.
- Web notification to the **submitter** (only while still an active OrganizationUser of that organization), no email/push: `GET /api/organizations/me/notifications`, types `ContentReviewApproved` / `ContentReviewRejected`, with `contentReviewId` and `scenarioVersionId`; the body of a rejection carries the reason. `entitlementId`/`referenceEndsAt` are null for these types and `stillRelevant` is always true.

## Deprecated PlatformAdmin routes (to remove after the web switches)

`POST /api/admin/scenario-versions/{id}/approve` and `/reject` (PlatformAdmin, body `{contentHash, rubricHash, reason}`) still work and are marked deprecated in OpenAPI. FR-PLAN-05 says PlatformAdmin does not thẩm định; remove them (controller actions + the Approve/Reject branch of `scenario_readiness_gate`) once the web uses the expert routes. `GET /api/admin/scenario-reviews` stays as a read-only monitor for PlatformAdmin.

## Web (FE) work

- Expert area: queue (`status` tabs Submitted / Approved / Rejected), detail with the frozen content and the 3D preview, Đạt / Chưa đạt buttons sending `If-Match` from the detail ETag and a fresh `Idempotency-Key`; on 412 reload the item (someone decided first).
- PlatformAdmin: create expert accounts in account management (role `FireSafetyExpert`, no organization); hide the old approve/reject buttons.
- Organization: show the two new notification types and link to the scenario version.
