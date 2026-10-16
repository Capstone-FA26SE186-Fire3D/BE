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
| GET | `/api/training/sessions` | Trainee history (#78), live login only. The caller's own started sessions, newest first (`startedAt`, id); preparations and Organization playtests never appear; losing Building access keeps history. Filters `status` (Launching, Running, AwaitingSync, Completed — the only states v7 sessions take; other values return 400), `mode`, `buildingId`, `from`/`to` on startedAt (to exclusive), `page`, `pageSize` ≤100. Items: `sessionId, status, mode, resultStatus` (Passed/NotPassed/Incomplete/NotAssessed or null without a stored result), `endReason, startedAt, endedAt, resultCreatedAt`, pinned `training`, `releaseId`, `building`, `scenario` (name, version). Response `{items,totalCount,page,pageSize}`. |
| GET | `/api/training/buildings` | Live login only (#96). Buildings of the caller's own started sessions for the history filter: `{items:[{id,name,sessionCount,lastSessionAt}],totalCount,page,pageSize}`, most recent first; losing access keeps a Building listed. |
| POST | `/api/training/sessions` | `{trainingId, mode: Learn|Guided|Assessment, runtimeVersion}` + Idempotency-Key. Pins Training, Published release, version, rubric hash, package/manifest and runtime. No grant, no seat, not a play. Expires after 24 hours. |
| GET | `/api/training/sessions/{id}` | A preparation (`Prepared`, `Expired`) or a started session (`Launching`, `Running`, `AwaitingSync`, `Completed`). Accepts continuation. |
| POST | `/api/training/sessions/{id}/start` | Idempotency-Key. Online recheck of live session, account lifecycle, Public/Private access (participation grant at the current `access_revision`), content approval, Published release and active Training dates, current paid entitlement and runtime. Allocates the seat and returns a five-minute launch grant plus a continuation token. Before launch the same session may call start again for a newer grant generation (same play and seat). |
| POST | `/api/training/sessions/{id}/launched` | `{generation}` of the current unexpired grant: Launching → Running. |
| POST | `/api/training/sessions/{id}/heartbeat` | Server-received liveness. Accepts continuation. |
| POST | `/api/training/sessions/{id}/events:batch` | 1–500 events. Accepts continuation. |
| POST | `/api/training/sessions/{id}/complete` | `{lastEventSequence, endReason: Finished|TimedOut|Abandoned}` + Idempotency-Key. 200 Completed or 202 AwaitingSync. Accepts continuation. |
| GET | `/api/training/sessions/{id}/result` | Server result; `409 RESULT_NOT_READY` until finalized (also for a preparation id). Accepts continuation. See [result fields](#result-fields). |
| POST | `/api/training/sessions/{id}/continuation` | Live login only; reissues continuation for the same owner and started session. The previous token stops working. |
| POST | `/api/training/reconcile` | `{sessionIds?, idempotencyKeys?}` (1–100). Returns only the caller's own sessions/preparations; foreign IDs are absent. |

Errors: 401 invalid session or continuation, 403 role (a non-Trainee token is refused by the framework before the gate: 403 without `code`; `FORBIDDEN` when the stored role no longer matches the token), Building access (`BUILDING_ACCESS_REQUIRED`) or a continuation token used for another session id (`CONTINUATION_SESSION_MISMATCH`), 404 outside owner scope, 409 state/precondition (`TRAINING_UNAVAILABLE`, `MODE_NOT_ALLOWED`, `CONTENT_APPROVAL_REQUIRED`, `RUNTIME_INCOMPATIBLE`, `RUBRIC_NOT_SUPPORTED`, `BUILDING_ENTITLEMENT_REQUIRED`, `LEARNER_LIMIT_REACHED`, `LAUNCH_GRANT_STALE`, `LAUNCH_GRANT_EXPIRED`, `SESSION_TERMINAL`, `COMPLETION_ALREADY_REQUESTED`), 422 invalid telemetry/completion, 503 signing not configured.

## Seats

One seat per Trainee per entitlement period (`billing_learner_seats`, unique entitlement + Trainee). Start locks the entitlement row, reuses the Trainee's seat or counts seats against `billing_effective_learner_limit(entitlement, now)`. Two Trainees racing for the last seat admit exactly one. Seat, session row, receipt and audit commit atomically. Login, list, prepare and playtest never allocate. Upgrade raises the limit on the same period and keeps used seats; renewal is a new entitlement and a new seat period. Legacy entitlements without a learner limit allocate seats without a limit check.

## Continuation (offline sync)

The continuation JWT (issuer `FET3D.Learner`, audience `FET3D.Learner.Continuation`, purpose `learner_continuation`) names the owner, one started session and a continuation ID; it lasts `ContinuationTtlDays` (default 7). It is accepted only by get/heartbeat/events/complete/result of that session, and the database checks it is the session's current continuation. It never prepares, starts or reconciles. Logout, lost Building access and entitlement expiry do not delete history or block finishing a started session. After expiry a live login calls `/continuation`.

## Telemetry and result

Event: `{eventId, sequence ≥ 1, schemaVersion, type, occurredAt, elapsedMs ≥ 0, payload}`. Same `eventId` and content replays (`duplicates`); different content is `EVENT_HASH_CONFLICT`; a reused sequence is `EVENT_SEQUENCE_CONFLICT`; events after the pinned completion sequence are `EVENT_AFTER_COMPLETION`. The response returns `acknowledgedSequence` (highest N with 1..N stored) and `missingRanges`, so a lost ACK loses nothing.

### Allowed event types (SCENARIO-02)

Migration `20261016090000_AddTrainingEventAllowList`; the same rule applies to playtest events.

- Core events, valid for every runtime: `ExitReached`, `WrongExit`, `HazardExposure {amount}`, `Moved {distanceMeters}`. Metrics use only these.
- Any other `type` must be declared in `capabilityContracts[*].events` of the runtime the session pinned at prepare
  (`GET /api/scenario-interactions/catalog`), and `payload` must match that event's schema (declared properties only, required present,
  type/range/length/enum; no schema means `{}`).
- Otherwise the event is returned in `conflicts` with `EVENT_TYPE_UNSUPPORTED` or `EVENT_PAYLOAD_INVALID`. It still fills its
  sequence, so `acknowledgedSequence` advances and completion never waits for it; it is stored with `rejection_code` and never
  scored. Resending it returns the same conflict. The rest of the batch is accepted.
- `schemaVersion` of an event is stored as sent; it does not change the allow-list (pending Unity decision).

Batch 1 proposal ([fixture](../contracts/editor/v1/fixtures/runtime-catalog.batch1.json), Docs IFC 6.1). API names are PascalCase; Docs
names map as follows. Each payload carries `objectId` (the editor object that emitted it).

| API event | Docs event | Capability | Extra payload |
|---|---|---|---|
| `AlarmRaised` | `alarm_raised` | `alarm.call_point` | |
| `DetectorTriggered` | `detector_triggered` | `alarm.detector` | |
| `ExtinguisherUsed` | `extinguisher_used` | `extinguisher.portable` | `durationSeconds` |
| `ExtinguishAttemptLargeFire` | `extinguish_attempt_large_fire` | `extinguisher.portable` | |
| `ItemWetted` | `item_wetted` | `water.sink`, `water.bath`, `water.shower` | `itemId` |
| `NoseCoveredInSmoke` | `nose_covered_in_smoke` | `item.towel` | |
| `CheckedDoorHeat` | `checked_door_heat` | `door.standard`, `door.fire`, `door.exit` | `hot` |
| `ClosedDoor` | `closed_door` | `door.standard`, `door.fire`, `door.exit` | |
| `UsedElevator` | `used_elevator` | `transport.elevator` | |
| `ExitReached` (core) | `reached_exit` | `exit.stair`, `exit.final` | |
| `WentBackForBelongings` | `went_back_for_belongings` | `bait.belongings` | |
| `NpcGuided` | `npc_guided` | `npc.guide` | |
| `SprinklerActivated` | `sprinkler_activated` (debrief only) | `suppression.sprinkler` | |

No rubric metric uses these events yet; adding one (e.g. `used_elevator`) is a separate change.

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

### Result fields

OpenAPI publishes every value below as an enum (#96).

| Field | Values |
|---|---|
| `mode` | `Learn`, `Guided`, `Assessment` |
| `outcome` | `Passed`, `NotPassed`, `Incomplete` (Assessment); `NotAssessed` (Learn, Guided) |
| `reason` | `MODE_NOT_ASSESSED` (Learn/Guided), `ENDED_BEFORE_COMPLETION` (Abandoned), `RUBRIC_NOT_SCORABLE` (no rubric or total weight 0), `MANDATORY_CRITERION_FAILED`, `BELOW_PASS_THRESHOLD`, `ALL_REQUIREMENTS_MET` |
| `scorePercent` | Passed weight / total weight × 100; null when not scored |
| `passThresholdPercent` | The rubric `pass_threshold` (authored as a ratio 0–1) × 100, on the same scale as `scorePercent`; Assessment only |
| `criteria` | Assessment only (null for Learn and Guided). Each: `id, metric, value, operator (gte/lte/eq), threshold, mandatory, weight, passed, reason`, plus `label` and `description` from the pinned rubric (null when the author left them out) |
| `criteria[].reason` | `METRIC_UNAVAILABLE` when the metric was not computed (the criterion fails), otherwise null |

Rubric criteria accept an optional `label` (1–200 characters) beside `description`; labels and the threshold are read from the scenario version the session pinned, so results stored before labels existed show them too. History `endReason` is `Finished`, `TimedOut` or `Abandoned` — Abandoned is an end reason, not a session status.

## Building QR

| Method | Route | Notes |
|---|---|---|
| POST | `/api/buildings/{id}/qr-codes` | Organization (own tenant) or Admin; `{label?}`; returns the token once (32 random bytes, base64url). Only its SHA-256 is stored. The response also carries `content`, the exact text to print in the QR: `{AuthEmail:FrontendUrl}/qr/{token}` (web landing for users without the app). A Building has one canonical Active code: a second create, including a retry, returns `409 QR_ALREADY_EXISTS` (rotate instead). |
| GET | `/api/buildings/{id}/qr-codes?page=&pageSize=` | Code history without tokens, newest first: `{items,total,page,pageSize}`, default 20, max 100. Each item has `replacedBy` (its successor) and `replacedId` (the code it replaced). |
| POST | `/api/buildings/{id}/qr-codes/{qrId}/rotate` | Revokes the code and issues a new token. |
| POST | `/api/buildings/{id}/qr-codes/{qrId}/revoke` | |
| GET | `/api/qr/{qrToken}` | Signed-in user: Building ID, name, visibility, `hasAccess`, `serviceStatus` (`Active` paid period running, `Expired` paid before but none now, `NotPurchased`), `serviceEndsAt` and `canStartNewSessions` (Active service and access). Expired service still resolves so the landing can explain it. A revoked or replaced code returns `410 QR_REVOKED` (never the new token); an unknown token, another tenant's code (OrganizationUser) or an archived Building returns `404 QR_NOT_FOUND`. |
| GET | `/api/qr/{qrToken}/trainings` | Published Trainings with the normal access checks; same 404/410 rules. |

QR content format: scanners (Web and Mobile) read the printed URL and use its last path segment as `qrToken`; a bare 43-character base64url token is accepted the same way. A QR identifies a Building only: it does not authenticate, grant participation or entitlement, and legacy release QR rows are not reused.

## Evidence

Actual migrations on disposable PostgreSQL with an EXECUTE-only caller: prepare/start/seat/regrant/launch, events with gaps, duplicates and conflicts, AwaitingSync then finalize, rubric result and immutability, seat reuse, last-seat race, upgrade and renewal capacity, continuation after logout/access loss/expiry and reissue, Private access and code rotation, mode/runtime/rubric gates, reconcile scoping, QR create/rotate/revoke/resolve and the HTTP continuation scheme. Mobile, Unity and deployment are not verified.
