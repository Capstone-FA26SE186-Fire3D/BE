# Learner sessions, seats, offline sync and results

Trainee lifecycle on the existing `sessions`, `session_events` and `session_results` tables. Rows created by this contract have
`contract_version = 7`; legacy rows keep `contract_version = 1` and their stored values and are never relabelled. Migration:
`20261011120000_AddLearnerSessions` (plus EF snapshot `20261011120100_SyncLearnerSessionsModel`).

## Configuration

```text
LearnerSessions__Enabled=true
LearnerSessions__SigningKey=<launch grant key, 32-512 bytes>
LearnerSessions__ContinuationSigningKey=<different key, 32-512 bytes>
LearnerSessions__ContinuationTtlDays=7
```

Disabled by default: prepare works, start and continuation return `503 LEARNER_LAUNCH_UNAVAILABLE` without holding a seat. Keys must differ from each other and from the API JWT key; the continuation audience (`FET3D.Learner.Continuation`) differs from the API audience.

## Endpoints (Trainee)

| Method | Route | Notes |
|---|---|---|
| POST | `/api/training/sessions` | `{trainingId, mode: Learn|Guided|Assessment, runtimeVersion}` + Idempotency-Key. Pins Training, Published release, version, rubric hash, package/manifest and runtime. No grant, no seat, not a play. Expires after 24 hours. |
| GET | `/api/training/sessions/{id}` | Prepared or started state. Accepts continuation. |
| POST | `/api/training/sessions/{id}/start` | Idempotency-Key. Online recheck of live session, account lifecycle, Public/Private access (participation grant at the current `access_revision`), content approval, Published release and active Training dates, current paid entitlement and runtime. Allocates the seat and returns a five-minute launch grant plus a continuation token. Before launch the same session may call start again for a newer grant generation (same play and seat). |
| POST | `/api/training/sessions/{id}/launched` | `{generation}` of the current unexpired grant: Launching → Running. |
| POST | `/api/training/sessions/{id}/heartbeat` | Server-received liveness. Accepts continuation. |
| POST | `/api/training/sessions/{id}/events:batch` | 1–500 events. Accepts continuation. |
| POST | `/api/training/sessions/{id}/complete` | `{lastEventSequence, endReason: Finished|TimedOut|Abandoned}` + Idempotency-Key. 200 Completed or 202 AwaitingSync. Accepts continuation. |
| GET | `/api/training/sessions/{id}/result` | Server result; `409 RESULT_NOT_READY` until finalized. Accepts continuation. |
| POST | `/api/training/sessions/{id}/continuation` | Live login only; reissues continuation for the same owner and started session. The previous token stops working. |
| POST | `/api/training/reconcile` | `{sessionIds?, idempotencyKeys?}` (1–100). Returns only the caller's own sessions/preparations; foreign IDs are absent. |

Errors: 401 invalid session or continuation, 403 role or Building access (`BUILDING_ACCESS_REQUIRED`), 404 outside owner scope, 409 state/precondition (`TRAINING_UNAVAILABLE`, `MODE_NOT_ALLOWED`, `CONTENT_APPROVAL_REQUIRED`, `RUNTIME_INCOMPATIBLE`, `RUBRIC_NOT_SUPPORTED`, `BUILDING_ENTITLEMENT_REQUIRED`, `LEARNER_LIMIT_REACHED`, `LAUNCH_GRANT_STALE`, `LAUNCH_GRANT_EXPIRED`, `SESSION_TERMINAL`, `COMPLETION_ALREADY_REQUESTED`), 422 invalid telemetry/completion, 503 signing not configured.

## Seats

One seat per Trainee per entitlement period (`billing_learner_seats`, unique entitlement + Trainee). Start locks the entitlement row, reuses the Trainee's seat or counts seats against `billing_effective_learner_limit(entitlement, now)`. Two Trainees racing for the last seat admit exactly one. Seat, session row, receipt and audit commit atomically. Login, list, prepare and playtest never allocate. Upgrade raises the limit on the same period and keeps used seats; renewal is a new entitlement and a new seat period. Legacy entitlements without a learner limit allocate seats without a limit check.

## Continuation (offline sync)

The continuation JWT (issuer `FET3D.Learner`, audience `FET3D.Learner.Continuation`, purpose `learner_continuation`) names the owner, one started session and a continuation ID; it lasts `ContinuationTtlDays` (default 7). It is accepted only by get/heartbeat/events/complete/result of that session, and the database checks it is the session's current continuation. It never prepares, starts or reconciles. Logout, lost Building access and entitlement expiry do not delete history or block finishing a started session. After expiry a live login calls `/continuation`.

## Telemetry and result

Event: `{eventId, sequence ≥ 1, schemaVersion, type, occurredAt, elapsedMs ≥ 0, payload}`. Same `eventId` and content replays (`duplicates`); different content is `EVENT_HASH_CONFLICT`; a reused sequence is `EVENT_SEQUENCE_CONFLICT`; events after the pinned completion sequence are `EVENT_AFTER_COMPLETION`. The response returns `acknowledgedSequence` (highest N with 1..N stored) and `missingRanges`, so a lost ACK loses nothing.

Completion pins `lastEventSequence`. If events are missing the session is `AwaitingSync` (202); the arrival of the missing events, or a retry with the same key, finalizes it. Exactly one immutable result is written.

Metrics are computed only from received events:

| Metric | Source |
|---|---|
| `reached_exit` | 1 if an `ExitReached` event exists, else 0 |
| `completion_time_seconds` | `elapsedMs` of the first `ExitReached`, else the last event |
| `wrong_exits` | count of `WrongExit` |
| `hazard_exposure` | sum of `HazardExposure.payload.amount` (finite, ≥ 0) |
| `distance_meters` | sum of `Moved.payload.distanceMeters` (finite, ≥ 0) |

Criteria compare a metric with `gte`/`lte`/`eq`; a missing metric fails the criterion. Score = passed weight / total weight (stored as percent). Assessment: `Passed` when every mandatory criterion passes and the ratio reaches `pass_threshold`, otherwise `NotPassed`; `Abandoned` gives `Incomplete`. Learn and Guided give `NotAssessed`. Completion never implies Passed and the client never sends a score. Versioned editor drafts reject criteria on other metrics (`RUBRIC_METRIC_UNSUPPORTED`), and Assessment prepare rejects legacy versions with unsupported metrics (`RUBRIC_NOT_SUPPORTED`).

## Building QR

| Method | Route | Notes |
|---|---|---|
| POST | `/api/buildings/{id}/qr-codes` | Organization (own tenant) or Admin; `{label?}`; returns the token once (32 random bytes, base64url). Only its SHA-256 is stored. |
| GET | `/api/buildings/{id}/qr-codes` | Codes without tokens. |
| POST | `/api/buildings/{id}/qr-codes/{qrId}/rotate` | Revokes the code and issues a new token. |
| POST | `/api/buildings/{id}/qr-codes/{qrId}/revoke` | |
| GET | `/api/qr/{qrToken}` | Signed-in user: Building ID, name, visibility and whether the caller has access. |
| GET | `/api/qr/{qrToken}/trainings` | Published Trainings with the normal access checks. |

A QR identifies a Building only: it does not authenticate, grant participation or entitlement, and legacy release QR rows are not reused.

## Evidence

Actual migrations on disposable PostgreSQL with an EXECUTE-only caller: prepare/start/seat/regrant/launch, events with gaps, duplicates and conflicts, AwaitingSync then finalize, rubric result and immutability, seat reuse, last-seat race, upgrade and renewal capacity, continuation after logout/access loss/expiry and reissue, Private access and code rotation, mode/runtime/rubric gates, reconcile scoping, QR create/rotate/revoke/resolve and the HTTP continuation scheme. Mobile, Unity and deployment are not verified.
