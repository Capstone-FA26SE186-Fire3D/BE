# Runtime bridge contract, protocol `1` (Mobile ↔ Unity ↔ BE)

Shared by the Mobile app (Expo host, native module `Fire3DUnity`), the Unity runtime (Unity as a Library) and the backend.
BE owns this folder (Unity#5); the protocol follows Mobile#22 and the Unity DTOs in `Runtime/Contracts/LaunchContracts.cs`.
BE tests (`RuntimeBridgeContractTests`) validate every fixture with a JSON Schema 2020-12 validator and check that the event,
complete and result shapes match the BE API DTOs, so the schemas cannot drift from the API.

| File | Direction | Carried by |
|---|---|---|
| `launch.schema.json` | Mobile → Unity | `UnitySendMessage("Fire3DBridge", "Launch", json)` |
| `control.schema.json` | Mobile → Unity | `UnitySendMessage("Fire3DBridge", "Pause" \| "Resume" \| "Abort", argument)`: `""` or `{sessionId?, reason?}` |
| `bridge-message.schema.json` | Unity → Mobile | `com.fet3d.unity.Fire3DUnityBridge.onUnityMessage(json)`: `{type, sessionId, payload}` |
| `session-event.schema.json` | Unity → Mobile → BE | items of the `Events` payload = items of `events:batch` (BE `TrainingEvent`) |
| `training-result.schema.json` | BE → Mobile (→ Unity) | `GET /api/training/sessions/{id}/result` (BE `TrainingResultView`) |
| `fixtures/valid/*.json`, `fixtures/invalid/*.json` | all | samples; `fixtures/index.json` names the schema of each file and why each invalid one fails |

## Flow

| Step | Mobile | Unity | BE |
|---|---|---|---|
| 1 | Prepare, download the `fet3d.package/1` files and check every SHA-256 ([package download](../../../docs/unity-package-worker.md#mobile-download)) | | `POST /api/training/sessions`, `GET .../package` |
| 2 | Start online | | `POST .../start` (seat or Trial, launch grant) |
| 3 | Open Unity | `Ready {runtimeVersion, protocolVersion}` | |
| 4 | `Launch` (no grant or token inside) | loads the package, checks hashes and runtime again | |
| 5 | | `Launched {runtimeVersion, resumeFromSequence}` | Mobile: `POST .../launched {generation}` |
| 6 | stores events durably, then `events:batch`; retries `missingRanges`, never resends `conflicts` | `Events [...]` (≤ 500 per message) | `POST .../events:batch` |
| 7 | | `Completed {lastEventSequence, endReason, …}` | Mobile: `POST .../complete {lastEventSequence, endReason}` + Idempotency-Key; `202 AwaitingSync` → sync, call again |
| 8 | shows the server result | `Exited` (player left) | `GET .../result` (Unity never scores) |

Playtests use the same messages with `kind: "Playtest"` and the `/api/playtests/{id}/…` endpoints. When the app goes to the
background Mobile sends `Pause`; Unity also pauses itself and flushes its events. When the Unity activity is in the foreground
again Mobile sends `Resume`. `Abort` (back button, expired launch grant) ends the play as `Abandoned`. Unity ignores the argument
of the three commands: Mobile sends `""` for `Pause`/`Resume` and `{"reason": "BackButton"}` for `Abort`. After a crash or offline
period Mobile resumes with `resumeFromSequence` = BE `acknowledgedSequence` and Unity numbers new events from there (continuation
token, 7 days).

Unity runs one session at a time and stays loaded between sessions. After `Exited` it unloads the package and sends `Ready` again;
Mobile may then send the next `Launch` (step 4) without restarting Unity. A `Launch` that fails before `Launched` (bad launch,
package or mode) leaves Unity idle without a new `Ready`, so Mobile may send `Launch` again at once.

## Error codes (`Error.payload.code`)

Unity sends `Error {code, message}`. An `Error` before `Launched` means that `Launch` did not start a play: Mobile shows the
message and brings the React Native screen back (with `SESSION_BUSY` the earlier play keeps running). Codes are
`UPPER_SNAKE_CASE`; an unknown code is shown generically.

| Code | Meaning |
|---|---|
| `LAUNCH_INVALID` | the Launch JSON is malformed or misses a required field |
| `PROTOCOL_UNSUPPORTED` | `Launch.protocolVersion` is not supported by this runtime |
| `MODE_UNSUPPORTED` | unknown `mode` |
| `MODE_NOT_ALLOWED` | `mode` is not in the scenario `modePolicy.allowedModes` |
| `RUNTIME_INCOMPATIBLE` | the package `minRuntimeVersion` does not fit this runtime |
| `PACKAGE_FILE_MISSING` | a file of the manifest is not next to `manifest.json` |
| `PACKAGE_HASH_MISMATCH` | a file does not match its SHA-256 in the manifest |
| `MATERIAL_LIBRARY_MISSING` | the material library bundle shipped with the app is missing |
| `MANIFEST_UNREADABLE` | `manifest.json` cannot be read |
| `MANIFEST_INVALID` | `manifest.json` misses a required field or lists a bundle that is not in `files` |
| `MANIFEST_SCHEMA_UNSUPPORTED` | the manifest is not `fet3d.package/1` |
| `CONTENT_UNREADABLE` | `building_metadata.json` or `scenario.json` cannot be read |
| `CONTRACT_EMPTY` / `CONTRACT_INVALID` | a contract document (metadata, scenario) is empty or malformed |
| `BUILDING_SCHEMA_UNSUPPORTED` | the building metadata is not `fet3d.building/1` |
| `BUILDING_UNITS_UNSUPPORTED` | the building metadata uses units the runtime does not support |
| `SCENARIO_SCHEMA_UNSUPPORTED` | the scenario is not `fet3d.editor/1` |
| `SCENARIO_OBJECT_INVALID` | a scenario object cannot be placed (unknown capability, missing parameters) |
| `BUNDLE_LOAD_FAILED` | a floor scene bundle cannot be loaded |
| `WORLD_BUILD_FAILED` | the simulation world could not be built |
| `SESSION_BUSY` | a play is already running |

BE error codes of the API calls in the flow are documented with each endpoint ([learner sessions](../../../docs/learner-sessions.md),
[playtest](../../../docs/playtest-manual-test.md)).

## Versioning

`protocolVersion` `"1"` covers this folder. Adding an optional field or a new message type is compatible; renaming, removing or
changing the meaning of a field needs protocol `2`, a new folder `contracts/runtime/v2`, and a runtime that accepts both while
older apps are in use. Event payloads have their own `schemaVersion` (`"1"`); capability events are declared by the pinned
runtime catalog (`GET /api/scenario-interactions/catalog`).
