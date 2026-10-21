# Editor contract `fet3d.editor/1`

Shared by the Web editor (FE), Mobile/Unity runtime and the Geometry worker. The BE validator in
[`EditorContract.cs`](../../../Fire3D/Fire3D.Application/Editor/EditorContract.cs) is authoritative; these JSON Schemas
document the same rules and the fixtures are exercised by BE tests.

| File | Used for |
|---|---|
| `geometry-metadata.schema.json` | `metadata` of the Geometry job `preview_glb` output (`POST internal/processing/jobs/{id}/outputs`) |
| `scenario-state.schema.json` | Body of `PUT /api/scenario-drafts/{id}`; state in GET draft and in the immutable snapshot |
| `capability-contract.schema.json` | Values of `capabilityContracts` in `GET /api/scenario-interactions/catalog` |
| `fixtures/*.json` | Valid samples. `runtime-catalog.sample.json` is a test fixture, not a seed |

## Coordinates

- GLB space: metres, Y-up, right-handed.
- Matrices: 16 finite numbers, column-major, multiplied with column vectors: `x' = m0·x + m4·y + m8·z + m12`.
  Row 4 (indices 3, 7, 11, 15) must be `0,0,0,1`; the 3×3 part must be invertible.
- `coordinateTransform` maps IFC model coordinates to GLB and therefore contains unit conversion and origin.
  Fixture: IFC millimetres, Z-up → GLB metres, Y-up; IFC `(1000, 2000, 3000)` → GLB `(-9, 3, 3)`.
- `floors[].transform` maps floor-local metres to GLB; object `placement.position` is floor-local.
  Fixture: local `(1, 0, 2)` on `L2` (elevation 3.5 m) → GLB `(1, 3.5, 2)`.
- `placement.rotation` is a unit quaternion `{x,y,z,w}` in GLB axes.

## Validation layers

| Where | Checks | Error |
|---|---|---|
| PUT draft | Unknown field, type, finite number, enum, duplicate IDs (partial drafts allowed) | `422 EDITOR_SCHEMA_INVALID` + `issues[{code,path,message}]` |
| PUT draft | Malformed JSON | `400 EDITOR_JSON_MALFORMED` |
| PUT / validate / snapshot | Unsupported `schemaVersion` | `422 EDITOR_SCHEMA_VERSION_UNSUPPORTED` |
| Validate / snapshot | Completeness; pinned accepted Geometry artifact of the draft revision; floor and anchor existence and anchor floor; runtime capability version, kind and parameters | validate: `200 isValid=false`; snapshot: `422 EDITOR_SCHEMA_INVALID` |
| Worker output | Geometry metadata declaring `schemaVersion` must pass the schema before acceptance | `422 EDITOR_SCHEMA_INVALID` |

A document without `schemaVersion` is legacy: drafts keep the historical typed shape and stored snapshots are read as
saved. Legacy Geometry metadata is never reported as satisfying this contract: editor preview and floors return
`ReprocessRequired` without coordinates or a download URL.

Training modes use the database vocabulary `Learn`, `Guided`, `Assessment`. Rubric criteria must use a server-computed metric: the core metrics (`reached_exit`, `completion_time_seconds`, `wrong_exits`, `hazard_exposure`, `distance_meters`) or a player-action count named after the Docs event (`alarm_raised`, `used_elevator`, …); `GET /api/scenario-rubrics/catalog` lists them all. See [learner sessions](../../../docs/learner-sessions.md).

Structural validation is not geometry QA and not Unity acceptance. Object kinds are placement categories; behaviour
comes only from capabilities published in the runtime catalog. No Unity behaviour is inferred or seeded here.

A capability contract may declare `events` (`[{type, description?, payload?}]`, PascalCase types, payload in the parameter-schema
subset) and `actions` (`[{id, event?, ...}]`, Unity interaction metadata passed through unchanged). Learner and playtest events are
accepted only for core types or types declared here by the session's runtime; see
[learner sessions](../../../docs/learner-sessions.md#allowed-event-types-scenario-02). `fixtures/runtime-catalog.batch1.json` is the
Unity runtime `2.0.0` catalog (Docs IFC 6.1/6.3, 20 capabilities including `suppression.sprinkler` and the Hazards `fire.source`,
`smoke.source`, `event.power_outage`, `event.alarm`; confirmed by Unity in BE#101). PlatformAdmin registers it with
`POST /api/admin/runtime-catalog` (without `_comment`); it is data, not a seed. Timeline Hazards still need a placement in the editor;
the runtime ignores it.

## Wind and routing weights (BE#144)

Both are optional scenario-wide fields of `fet3d.editor/1`; drafts without them stay valid. A present object must be complete:
a missing, unknown, non-numeric or out-of-range field is a `422 EDITOR_SCHEMA_INVALID` issue, never a default.

| Field | Range | Meaning |
|---|---|---|
| `wind.directionDegrees` | 0 ≤ θ < 360 | Direction the wind blows **towards**. GLB vector `(sin θ, 0, cos θ)`: 0 = +Z, 90 = +X. Unity mirrors X on load (`ContractFrame`), like every contract coordinate. |
| `wind.strength` | 0–1 | Normalised strength: 0 calm, 1 the strongest wind the runtime models. Game model, not CFD (Docs README). |
| `routingConfig.hazardExposureWeight` | 0–1000 | `wh` in `cost(edge) = distance + hazardExposure*wh + congestion*wc + portalPenalty*wp + blocked*wb` (Docs technology). |
| `routingConfig.congestionWeight` | 0–1000 | `wc` |
| `routingConfig.portalPenaltyWeight` | 0–1000 | `wp` |
| `routingConfig.blockedWeight` | 0–1000 | `wb` |

Without `wind` there is no wind. Without `routingConfig` the runtime uses the defaults it publishes for `runtimeVersion`. Both live in
`state_snapshot`, so they are covered by `scenario_hash`: changing them creates new content that needs a new content review, and the
package worker receives them in `scenarioSnapshot`. The `routing_config` projection of a version returns
`{ goalObjectIds, weights? }` (migration `20261021090000_AddScenarioWindRouting`). Ranges are the BE proposal pending Docs D2
(Docs#39); a change there is a new reviewed contract revision, not a silent reinterpretation.

## Rubric schema version (#69)

`GET /api/scenario-rubrics/catalog` (OrganizationUser, PlatformAdmin) publishes the rubric contract: `supportedSchemaVersions`, `defaultSchemaVersion`, server-computed `metrics` with descriptions, `operators`, the criterion fields, ID pattern and limits, and `maxCriteria`. It suggests no pass threshold, weight or criterion.

The first canonical rubric `schema_version` is the string `"1"`. The number `1`, `"v1"` and `"1.0"` are not aliases. One registry (`RubricContract`) validates the v1 editor contract, the legacy draft validator used before snapshot/submit, Library `RubricSample` payloads and AI scenario drafts. Any other value is `422 RUBRIC_SCHEMA_VERSION_UNSUPPORTED` at `$.rubric.schema_version` (`$.payload.rubric.schema_version` for Library). Stored snapshots are never rewritten. An unrecognised legacy version stays readable but cannot be snapshotted or submitted again, and a started session keeps the rubric it pinned.

Preflight before rollout (read-only, lists legacy versions):

```sql
SELECT 'scenario_versions' AS source, rubric->>'schema_version' AS version, count(*) FROM scenario_versions WHERE rubric IS NOT NULL GROUP BY 2
UNION ALL SELECT 'scenario_drafts', state->'rubric'->>'schema_version', count(*) FROM scenario_drafts WHERE state ? 'rubric' GROUP BY 2;
```

