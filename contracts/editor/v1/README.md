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

Training modes use the database vocabulary `Learn`, `Guided`, `Assessment`. Rubric criteria must use a server-computed metric (`reached_exit`, `completion_time_seconds`, `wrong_exits`, `hazard_exposure`, `distance_meters`); see [learner sessions](../../../docs/learner-sessions.md).

Structural validation is not geometry QA and not Unity acceptance. Object kinds are placement categories; behaviour
comes only from capabilities published in the runtime catalog. No Unity behaviour is inferred or seeded here.
