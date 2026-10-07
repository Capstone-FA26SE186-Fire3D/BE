# Scenario authoring and package builds

OrganizationUser operates only in their active tenant; PlatformAdmin has explicit platform scope. Every mutation rechecks the live actor, organization and Building under the lifecycle/Building locks. No implicit Guid.Empty tenant scope.

- `POST /api/scenarios` and `POST /api/scenarios/{id}/draft` require `Idempotency-Key` (1–128 characters, no whitespace/control characters). Same actor/key/input returns the existing ID; different input returns `409 IDEMPOTENCY_KEY_CONFLICT`.
- `GET /api/scenario-drafts/{id}` returns the PostgreSQL xmin as a quoted ETag. `PUT` and `POST .../snapshot` require that exact `If-Match`: missing 428, malformed 400, stale 412. Snapshot also requires an idempotency key; its receipt includes the original revision. Retry a lost response with the original key and ETag.
- Draft numbers and version numbers are allocated under a parent scenario lock, with database uniqueness as a second guard. State update/snapshot/receipt/audit are atomic; audit failure rolls back the operation.
- A version contains a canonical SHA-256 of its JSON state and an immutable full state snapshot. Updating or deleting a version is rejected by PostgreSQL. Author a new version to change content.

V7 snapshots require nonblank `learningObjectives` and `learnerInstructions`, plus an explicit rubric:

```json
{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1}]}
```

Thresholds above are a test fixture, not a default grading policy. Criteria IDs must be unique; operator is gte/lte/eq, weight nonnegative, mandatory Boolean and threshold numeric. Spawn/hazard/scoring/routing structure remains validated. Optional `objectAnchors` must exist in accepted Geometry output metadata for the same revision. Nonempty `requiredCapabilities` must be supported by the specified active `runtimeVersion` catalog entry. Structural validation returns issues by JSON path and does not create geometry QA, readiness or approval.

`POST /api/scenario-versions/{id}/package-builds` accepts `{ "kind":"PlaytestPackage", "buildTarget":"Windows" }` (or ReleasePackage) and an idempotency key. It returns 202 with jobId. The server pins verified IFC source ID/hash, immutable version/hash/rubric and target before outbox delivery. Worker claim returns this input and the immutable scenario snapshot; package outputs must include unity_package and manifest and match pinned target/runtime metadata. Only accepted outputs can be used downstream. A queued or mocked build is not evidence of a real Unity build.

Migration is additive: receipt table, draft-number unique index, nullable legacy version snapshot/rubric/learner fields, pinned job input and gates. Duplicate legacy numbering stops preflight; no legacy rubric/readiness is invented. Apply migrations before the binary. Tests used actual migrations on isolated PostgreSQL and fake worker outputs; Supabase and IFC/Unity providers remain unverified.

Manual test: create a scenario/draft with distinct keys; GET ETag; PUT a complete v7 state; GET fresh ETag; validate; snapshot with new key/current ETag; replay identical request; change key input to observe 409; submit package build and inspect processing status. Publish remains gated.

Geometry acceptance: object anchors come only from the current Succeeded Geometry attempt with a matching Passed validation run and no Error/Critical issues. A Succeeded worker job can carry Failed validation; it is not sufficient for validate/snapshot. Missing, failed, blocked or stale geometry returns ANCHOR_NOT_FOUND by path; snapshot rejects without creating a version/receipt. Forward migration20261007140000_RequirePassedGeometryAnchors repairs this check without changing legacy data. Warning-only Passed geometry remains usable.
