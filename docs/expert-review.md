# Thẩm định by FireSafetyExpert (BE#211)

Flow 1 PC06 (FR-PLAN-05, Docs#128). A **FireSafetyExpert** (Chuyên viên PCCC) thẩm định the training content an Organization submits. Wording everywhere: "thẩm định", never "phê duyệt phương án" — under Điều 15 Nghị định 105/2025/NĐ-CP the head of the facility approves the PC06 plan, outside FET3D.

Migrations: `20261124090000_AddFireSafetyExpertRole` (enum label, runs outside a transaction), `20261124090100_ExpertContentReview` and
`20261128090000_ExpertReviewAssignment` (round-robin assignment, BE#242) and `20261128150000_ExpertAssignmentFixes` (BE#245).

## Role

- `FireSafetyExpert` is a value of `UserRole` / `user_role_enum`. The account has **no organization** (like PlatformAdmin and Trainee).
- Created only by PlatformAdmin: `POST /api/accounts` with `role: "FireSafetyExpert"` and no `organizationId` (with one: 400). Self-registration and Google onboarding cannot create it.
- First sign-in (BE#235): the password alone opens no session; the expert enters the code sent to the account email (or uses Google of that email), then must change the initial password before any `/api/expert` call (403 `PASSWORD_CHANGE_REQUIRED`). See [authentication](authentication.md).
- Reads: the queue below, the frozen content of a reviewed version, and the 3D preview of the revision that version uses — for every organization. Nothing else: no drafts, no billing (billing and notification routes answer 403), no other organization data, no edits, no publishing.
- Deny by default (`ExpertRouteGuard`, runs right after authentication): an expert token gets `403 FORBIDDEN` on every `/api` route
  except `/api/expert/*`, `/api/auth/*` (me, logout, devices, change-password) and `/api/me/*` (avatar, password, Google link).
  Many controllers accept any signed-in role and leave role and tenant checks to handlers and SQL gates written before this role
  existed; the guard closes all of them at once, and a new route stays closed to experts unless it is added to the guard's list.
  Without the guard, a sweep of the 244 other routes found 235 already refusing, 2 answering 200 (`GET /api/learn/situations`,
  `GET /api/learn/posts`) and 7 not provable with fake ids.
- At deploy time at least one expert account must exist, otherwise Submitted items wait. Reviews already Submitted move to the expert queue as they are; Approved ones stay Approved.

## Assignment (BE#242)

Thẩm định works like a ticket system: each item has **one** assignee, so two experts never work on the same item.
- **When:** the organization submits a scenario version or a fire infrastructure version. The item goes to the next **available**
  expert in turn (A, B, C, A, …, ordered by account creation). Available = active, not deleted and not at the first-login `VerifyEmail`
  step (BE#235). An account created since BE#235 leaves that step only by verifying its email; an expert created before BE#235 (no
  `emailVerifiedAt`, no step) is available too (BE#245). Locked accounts and accounts still verifying their email get nothing.
- **Resubmission:** a new version after Chưa đạt is a new item and takes the next turn.
- **No expert available:** the item waits unassigned. It is handed out, oldest first, on the next expert or admin call, or on the next
  submission.
- **Who decides:** only the assignee approves or rejects. Other experts still read the item (detail, preview, `assignee=all`), but a
  decision answers `403 REVIEW_NOT_ASSIGNED_TO_YOU`. A decided item keeps its old answers (`412`, `409 …_NOT_PENDING`).
- **Reassignment:** PlatformAdmin sees who holds each item and moves a waiting item to another available expert, with a reason. This is
  recorded in `audit_logs` (target `ExpertReviewAssignment`, old and new `assigneeId`, `reason`). PlatformAdmin still never decides.
- **Expert no longer available** (locked, deleted, or sent back to `VerifyEmail`): the items still waiting for them go back to the
  rotation at once and are handed to the next available expert; items they already decided keep them as assignee (BE#245).
- **Lock order:** Building, then `fire3d:expert-assignment`. A submission takes them in that order (the assignment trigger runs inside
  the submitting gate); a decision and a reassignment hand out waiting items only after they hold the Building lock, so the two never
  deadlock (BE#245).
- **Storage:** `expert_review_assignments` (one row per item: `review_kind`, `review_id`, `assignee_id`, `assigned_at`, `source`
  RoundRobin | Reassigned, `assigned_by`, `reason`) and `expert_review_rotation` (the last expert served). Both are written only by
  SECURITY DEFINER functions.
- **Not done yet:** a notification to the expert when an item is assigned. The expert sees new items in the default queue
  (`assignee=me`).

## API

All routes: `[Authorize(Roles="FireSafetyExpert")]`. OrganizationUser and PlatformAdmin get 403 — PlatformAdmin has no thẩm định path, not even when no expert exists.

| Method | Route | Notes |
|---|---|---|
| GET | `/api/expert/reviews?kind=&status=&assignee=&page=&pageSize=` | `assignee`: `me` (default, the items assigned to you) \| `all` (every item; other experts' items are read-only). `kind`: `Scenario` \| `Infrastructure` (both when omitted; Infrastructure = fire infrastructure versions, BE#213, see [fire-infrastructure.md](fire-infrastructure.md)). `status`: `Submitted` (default, oldest first) \| `Approved` \| `Rejected` (newest decision first, read-only). Page size 1-100, default 20. |
| GET | `/api/expert/reviews/{id}` | Review item + frozen `content`: for a Scenario the fet3d.editor/1 state (its objects followed by the items of its fire infrastructure) with `rubric`, `learningObjectives`, `learnerInstructions`; for an Infrastructure `{geometry, runtimeVersion, items}`. `ETag` = revision. |
| GET | `/api/expert/reviews/{id}/preview` | Same shape as `editor-preview` (signed GLB, floors, anchors) for the revision of that scenario version. |
| POST | `/api/expert/reviews/{id}/approve` | Đạt. Headers `If-Match` (the review ETag) and `Idempotency-Key`. `403 REVIEW_NOT_ASSIGNED_TO_YOU` when the item is assigned to another expert. |
| POST | `/api/expert/reviews/{id}/reject` | Chưa đạt. Body `{ "reason": "..." }` 1-2000 characters (else 400 `VALIDATION_ERROR`, `errors.reason`). Same headers. |

Review item: `reviewId, kind, status, revision, scenarioVersionId, revisionId, contentHash, rubricHash, submittedBy(+Name), submittedAt, decidedBy(+Name), decidedAt, reason, organizationId/Name, buildingId/Name, scenarioId/Name, versionNumber, infrastructureId, assigneeId, assigneeName, assignedAt`. Scenario items leave `infrastructureId` null; Infrastructure items have `reviewId = infrastructureId`, `versionNumber` = infrastructure version, and null `scenarioVersionId`, `rubricHash`, `scenarioId/Name`.

### Decision rules

- **Revision / ETag:** a scenario review row changes once (Submitted → Approved | Rejected), so `revision` is `1` while Submitted and `2` once decided; `ETag: "1"`. An Infrastructure item uses the version's own revision (it grows on every change).
- **Assignee only:** a waiting item is decided by its assignee (`403 REVIEW_NOT_ASSIGNED_TO_YOU` for the others). A stale `If-Match` still answers `412 PRECONDITION_FAILED`. `decidedBy` records who decided.
- No `If-Match`: `428 PRECONDITION_REQUIRED`; malformed: 400. No `Idempotency-Key`: 400 `IDEMPOTENCY_KEY_REQUIRED`.
- Same `Idempotency-Key` and request: returns the first result, one decision only. Same key, different request: `409 IDEMPOTENCY_KEY_CONFLICT`.
- `409 CONTENT_REVIEW_NOT_PENDING` / `FIRE_INFRASTRUCTURE_NOT_PENDING` (already decided, current ETag), `409 CONTENT_HASH_MISMATCH` (content no longer matches the submission).
- **Final:** a decision cannot be changed. A fix is a new scenario version and a new submission; a wrong rejection is resubmitted as a new version. Old decisions stay in the audit log (`audit_logs`, target `ScenarioVersion`, `new_values.decidedByRole = FireSafetyExpert`).

## Result for the Organization

- Status: unchanged reads, e.g. `GET /api/scenario-versions/{id}/review` and the version list review state.
- Web notification to the **submitter** (only while still an active OrganizationUser of that organization), no email/push: `GET /api/organizations/me/notifications`, types `ContentReviewApproved` / `ContentReviewRejected`, with `contentReviewId` and `scenarioVersionId`; the body of a rejection carries the reason. `entitlementId`/`referenceEndsAt` are null for these types and `stillRelevant` is always true.

## PlatformAdmin follow-up (BE#242)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/admin/reviews?kind=&status=&assigneeId=&page=&pageSize=` | Items with `assigneeId/assigneeName/assignedAt`; `status` Submitted by default; `assigneeId` filters one expert. Same item shape and order as the expert queue. |
| POST | `/api/admin/reviews/{id}/reassign` | Body `{expertId, reason}` (reason 1-500 characters, else 400). 404 unknown item; `409 REVIEW_NOT_PENDING` once decided; `422 EXPERT_NOT_AVAILABLE` for a locked, deleted or unverified expert. Reassigning to the current assignee changes nothing. Audited. |

## Removed PlatformAdmin routes (BE#245)

`POST /api/admin/scenario-versions/{id}/approve` and `/reject` answer **410 `ADMIN_REVIEW_REMOVED`** and change nothing; the
Approve/Reject branch of `scenario_readiness_gate` refuses PlatformAdmin the same way. FR-PLAN-05: PlatformAdmin does not thẩm định;
the assigned FireSafetyExpert decides through `/api/expert/reviews/{id}`. `GET /api/admin/scenario-reviews` stays as a read-only
monitor for PlatformAdmin.

## Web (FE) work

- Expert area: queue (`status` tabs Submitted / Approved / Rejected, default "Được giao cho bạn" = `assignee=me`, a switch for `assignee=all` where other experts' items are read-only), detail with the frozen content and the 3D preview, Đạt / Chưa đạt buttons only on your own waiting items, sending `If-Match` from the detail ETag and a fresh `Idempotency-Key`; on 412 reload the item; on `403 REVIEW_NOT_ASSIGNED_TO_YOU` show that the item was moved to someone else.
- PlatformAdmin thẩm định follow-up: an assignee column (`GET /api/admin/reviews`) and a "Gán lại" action (`POST /api/admin/reviews/{id}/reassign`, pick an expert, enter a reason).
- PlatformAdmin: create expert accounts in account management (role `FireSafetyExpert`, no organization); hide the old approve/reject buttons.
- Organization: show the two new notification types and link to the scenario version.
