# Playtest preparation, start and launch grant

Only an active OrganizationUser in the Building tenant can prepare/start their own playtest. PlatformAdmin is not a playtest actor. The live JWT family is passed by BE, never from request JSON. Prepare and start are bound to that same family; after login/logout to another family, prepare a new session.

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

Response 200 contains launchGrant, expiresAt (five minutes from the recorded start after entitlement locking), actor/family, version/artifact/validation identity, package/manifest hashes and keys, target and runtime metadata. Paid service consumes no Trial units. Trial start consumes one unit atomically with Running state, receipt, grant timing and audit. Both callbacks and quota depend on DB truth, not client-provided metadata.

4. Replay the same start/key/input: same grant/expiry, no extra quota/audit. Different input with the same key: 409 IDEMPOTENCY_KEY_CONFLICT. A new start key for an already running session: 409 PLAYTEST_ALREADY_STARTED. After the original grant expires, replay returns 409 PLAYTEST_GRANT_EXPIRED; it never extends launch TTL or consumes quota again. Recovery/new launch lifecycle beyond this is not implemented in this task.

Other errors: missing/invalid key or fields 400 with field errors where appropriate; wrong owner/tenant/Building or inactive resource 404; expired/revoked session or inactive actor 401; no matching snapshot/package/runtime/entitlement 409 with DRAFT_SNAPSHOT_REQUIRED, PLAYTEST_PACKAGE_REQUIRED, RUNTIME_INCOMPATIBLE or PLAYTEST_ENTITLEMENT_REQUIRED. Legacy sessions without accepted pins cannot start. API/runtime publish and learner start/sync/results remain separate unfinished work.

Evidence: HTTP/OpenAPI and isolated PostgreSQL migration history with EXECUTE-only caller; concurrent same-key start and two sessions competing for final Trial unit; fake paid ledger fixture; audit fault rollback; expiry after waiting under Building lock; capability/minimum runtime and wrong Building entitlement; JWT verified by a fake runtime verifier and rejected by API authentication. Production S3/Unity/client/deployment are not certified by these tests.

Accepted manifest metadata must match the package protocol, schema, target, minimum runtime and required capabilities. Missing/malformed metadata is rejected without preparation or quota mutation. Source/grant tests do not establish that an external runtime has implemented token verification.
