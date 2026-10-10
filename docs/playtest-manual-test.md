# Playtest preparation, start and launch grant

Only an active OrganizationUser in the Building tenant can prepare/start their own playtest. PlatformAdmin is not a playtest actor. The live JWT family is passed by BE, never from request JSON. Without a handoff, prepare and start are bound to that same family; to launch on Mobile, use the handoff in section 5 (another session of the same user).

Before testing, upload and verify IFC, create/update/snapshot a v7 draft and enqueue a `PlaytestPackage` build for that immutable version. A trusted HTTP worker must claim/register outputs/complete with Passed validation and an accepted unity_package + manifest. Structural validation and a Built row alone are insufficient. Tests here use explicit fake worker outputs; no Unity/Blender execution is claimed.

1. In Swagger authorize as OrganizationUser. Call `POST /api/scenarios/{scenarioId}/playtests`, header `Idempotency-Key: prepare-001`:

```json
{
  "revisionId": "<revision UUID>",
  "scenarioVersionId": "<version UUID>"
}
```

Query buildingId is optional and must match the scenario if supplied. Alternatively use scenarioDraftId, exactly one of the two. Draft prepare resolves a previously created immutable snapshot with the exact current draft state/revision; editing the draft requires a new snapshot and accepted package build. No snapshot/package is silently fabricated. Optional legacy packageHash/protocolVersion/manifestSchemaVersion must equal the accepted server output; runtimeVersion in prepare is legacy metadata, start is authoritative.

Response 201: id, scenarioVersionId, artifactId, validationRunId, packageHash, manifestHash, buildTarget. No entitlement is assigned, no Trial is consumed, no launch grant is returned. Replay keeps the original immutable pin even if the draft is subsequently edited.

2. Configure launch signing in environment/secret (never commit the key):

```text
Playtest__Enabled=true
Playtest__SigningKey=<separate random signing key, 32-512 UTF-8 bytes>
Playtest__Issuer=FET3D.Playtest
Playtest__Audience=FET3D.Runtime.Playtest
```

Disabled by default: start returns 503 PLAYTEST_LAUNCH_UNAVAILABLE without quota mutation. Runtime/client must be configured to validate this separate issuer/audience/key and purpose=playtest, pinned actor/family/version/artifacts/hashes/target. A launch grant is not an API Bearer token or a learner start grant.

3. Require current service entitlement for this exact Building: Active paid BuildingService with Applied transaction, or an explicit Trial with remaining playtest units. No automatic Trial, default quota or production fixture is created. Call `POST /api/playtests/{id}/start`, header `Idempotency-Key: start-001`:

```json
{ "runtimeVersion": "1.0.0" }
```

Use an actual active catalog version in canonical major.minor.patch format. It must meet package minimum runtime, protocol, manifest schema and required capabilities. No content approval is required to playtest the organization's own draft/version.

Response 200 contains launchGrant, expiresAt (five minutes from the recorded start after entitlement locking), actor/family, version/artifact/validation identity, package/manifest hashes and keys, target and runtime metadata. Paid service consumes no Trial units. Trial start consumes one unit atomically with Launching state, receipt, grant generation 1, grant timing and audit. Open handoff codes are revoked at start. Both callbacks and quota depend on DB truth, not client-provided metadata.

4. Replay the same start/key/input: same grant/expiry, no extra quota/audit. Different input with the same key: 409 IDEMPOTENCY_KEY_CONFLICT. A new start key for an already running session: 409 PLAYTEST_ALREADY_STARTED. After the original grant expires, replay returns 409 PLAYTEST_GRANT_EXPIRED; it never extends launch TTL or consumes quota again. Use `POST /api/playtests/{id}/launch-grants` (section 6) to obtain a fresh grant before launch is confirmed.

Other errors: missing/invalid key or fields 400 with field errors where appropriate; wrong owner/tenant/Building or inactive resource 404; expired/revoked session or inactive actor 401; no matching snapshot/package/runtime/entitlement 409 with DRAFT_SNAPSHOT_REQUIRED, PLAYTEST_PACKAGE_REQUIRED, RUNTIME_INCOMPATIBLE or PLAYTEST_ENTITLEMENT_REQUIRED. Legacy sessions without accepted pins cannot start. Learner start/sync/results are a separate lifecycle (`/api/training/sessions`).

Evidence: HTTP/OpenAPI and isolated PostgreSQL migration history with EXECUTE-only caller; concurrent same-key start and two sessions competing for final Trial unit; fake paid ledger fixture; audit fault rollback; expiry after waiting under Building lock; capability/minimum runtime and wrong Building entitlement; JWT verified by a fake runtime verifier and rejected by API authentication. Production S3/Unity/client/deployment are not certified by these tests.

Accepted manifest metadata must match the package protocol, schema, target, minimum runtime and required capabilities. Missing/malformed metadata is rejected without preparation or quota mutation. Source/grant tests do not establish that an external runtime has implemented token verification.

## 5. Status and Mobile handoff — 2026-10-11

`GET /api/playtests/{id}` (the `Location` of prepare) returns status, pinned package, `runtimeVersion`, `grant {generation, issuedAt, expiresAt}`, `launchedAt`, `lastHeartbeatAt`, `acknowledgedSequence`, `boundToMobile`, `launchSession` (whether the calling session may start, launch or sync), `openHandoffExpiresAt`, `recovery {canStart, canReissueGrant, canSync, canCancel}` and `completion`. Family IDs are never returned. Status: `Created` → `Launching` (start) → `Running` (runtime confirmed) → `Completed` or `Cancelled`.

Configure handoff in the secret store (never commit):

```text
Playtest__HandoffKey=<base64 of 32 random bytes>
Playtest__HandoffDeepLinkBaseUrl=fet3d://playtest/handoff
```

Without both values, `POST /api/playtests/{id}/handoffs` returns `503 PLAYTEST_HANDOFF_UNAVAILABLE`.

1. Web (owner, playtest `Created`): `POST /api/playtests/{id}/handoffs` with `Idempotency-Key`. Response 201 `{handoffId, playtestId, code, deepLink, expiresAt}`; the QR encodes only `deepLink` (`<base>?code=<code>`). The code is 32 random bytes (base64url, 43 characters), valid for 5 minutes and single use. The database stores its SHA-256 plus an AES-GCM ciphertext (playtest ID as associated data) only so that a retry with the same key returns the same code within the TTL; the ciphertext is cleared on redeem, rotation, start or cancel. A new code revokes earlier open codes. Codes and JWTs are never logged or written to receipts.
2. Mobile (same OrganizationUser, its own login session): `POST /api/playtests/handoffs/redeem` with `{ "code": "..." }`. The issuing Web session must still be valid. Success binds the playtest to the Mobile session and returns the status (`launchSession: true`). A retry from the same Mobile session returns the status again. Other outcomes: another session `409 PLAYTEST_HANDOFF_USED`; another user or unknown code `404 PLAYTEST_HANDOFF_INVALID`; replaced code `409 PLAYTEST_HANDOFF_REVOKED`; expired `409 PLAYTEST_HANDOFF_EXPIRED`; issuer logged out `409 PLAYTEST_HANDOFF_ISSUER_INVALID`; already started `409 PLAYTEST_ALREADY_STARTED`. Prepare, code creation and redeem consume no Trial.
3. Mobile starts with `POST /api/playtests/{id}/start`. The Web session now receives `409 PLAYTEST_SESSION_MISMATCH`.

## 6. Grant recovery, launch confirmation, sync and terminal states

- `POST /api/playtests/{id}/launch-grants` (`Idempotency-Key`): launching session, status `Launching` only. Rechecks the live session, accepted package, runtime compatibility and the entitlement selected at start (still Active or Trial and inside its period) without consuming Trial. The generation increments; the JWT carries `grant_generation` and `jti = <playtestId>:<generation>`. The same key replays the same generation until it expires or is superseded (`409 PLAYTEST_GRANT_EXPIRED`).
- `POST /api/playtests/{id}/launched` with `{ "generation": n }`: confirms launch with the current, unexpired generation and moves to `Running`. An older generation returns `409 PLAYTEST_GRANT_STALE`; expired `409 PLAYTEST_GRANT_EXPIRED`; the same generation after Running replays. A launched playtest is never relaunched or moved to another device; reissue then returns `409 PLAYTEST_ALREADY_LAUNCHED`.
- `POST /api/playtests/{id}/heartbeat`: launching session, `Running`; stores server-received time.
- `POST /api/playtests/{id}/events:batch` with `{ "events": [{eventId, sequence, schemaVersion, type, occurredAt?, payload}] }` (1–500, unique eventId): append-only. The same eventId and content is a duplicate, safe after a lost ACK; different content is `EVENT_HASH_CONFLICT`; a used sequence with a new eventId is `EVENT_SEQUENCE_CONFLICT`. Response `{accepted, duplicates, conflicts, acknowledgedSequence, missingRanges}`.
- `POST /api/playtests/{id}/complete` with `{ lastEventSequence, summary? }` (`Idempotency-Key`): requires events `1..lastEventSequence`, otherwise `409 PLAYTEST_EVENTS_INCOMPLETE`. One terminal result; the same key and input replays, different input returns `409 IDEMPOTENCY_KEY_CONFLICT`.
- `POST /api/playtests/{id}/cancel` (`Idempotency-Key`): any live session of the owner, any non-terminal state. Complete and cancel are serialised on the playtest row; the loser receives `409 PLAYTEST_TERMINAL`. Trial is never refunded automatically.
- Playtests are excluded from learner seats and learner analytics (separate tables).

Error mapping: 401 invalid or expired session, 403 not OrganizationUser, 404 outside owner or tenant, 409 state or precondition, 422 invalid telemetry or completion content, 503 signing or handoff not configured.

Migration `20261011100000_AddPlaytestRuntime` is additive: runtime state, handoff and append-only event tables with owner-only RLS policies; a forward replacement of `playtest_lifecycle_gate`; backfill of existing sessions (started sessions become launched generation 1, preserving status and Trial usage). Evidence: actual migrations on disposable PostgreSQL with an EXECUTE-only caller and fake package outputs. The Mobile app, Unity runtime token verification and deployment are not verified.
