# Geometry worker in pull mode

The IFC/Geometry worker runs on the same outbound-only Windows VM as the [Unity package worker](unity-package-worker.md) and pulls
Geometry jobs exactly like the package worker pulls package jobs. With pull on, the HTTP push dispatcher never delivers Geometry
jobs, so no inbound worker URL is needed for Geometry. The worker never holds S3 or database credentials: it downloads the verified
IFC source and uploads its outputs through short signed S3 URLs. Migration `20261019090000_AddGeometryPull`
(`Persistence/Sql/GeometryPull.sql`, additive: no table/column/data change).

## Deployment settings

```ini
ProcessingWorker__WorkerApiEnabled=true
ProcessingWorker__GeometryPullEnabled=true         # default false; the HTTP dispatcher then skips Geometry jobs
ProcessingWorker__GeometryLeaseSeconds=600         # default 600, 60-900: lease of pulled Geometry attempts on lease and renew
ProcessingWorker__MachineKey=<32-512 random characters, the VM's X-Worker-Key>
ProcessingWorker__AllowedToolchains__0=<the Geometry worker toolchain id, compared ordinally>
ProcessingWorker__AllowedToolchains__1=<the Unity worker toolchain id, when package pull is also on>
ConnectionStrings__ProcessingExecutor=<restricted login, member of fet3d_processing_executor>
```

- Like package pull, Geometry pull needs the worker API and is refused at startup together with `RedisStreams` delivery
  (publisher or consumer on), because the Redis publisher skips no job kind.
- Dispatcher exclusion key (`processing_dispatch_gate('Claim', key)`): `ExcludePackages` (package pull only), `ExcludeGeometry`
  (Geometry pull only), `ExcludePackagesAndGeometry` (both). With both on, the HTTP dispatcher has nothing left to deliver and can be
  turned off (`DispatcherEnabled=false`).
- Apply the migration before deploying the binary. Keep pull off until the VM worker is ready; with pull off, Geometry jobs stay with
  the push dispatcher exactly as before (60 s lease, `sourceKey`, no storage check).
- `GET /health/worker` (machine key) also checks EXECUTE on `processing_geometry_lease_gate(jsonb)` while Geometry pull is on
  (`503 PROCESSING_EXECUTOR_FORBIDDEN` otherwise; also when the migration is not applied yet).

## Sequence

All calls send `X-Worker-Key`; user tokens are rejected (`401`). Base URL: the API.

```text
loop:
  POST /internal/processing/geometry-jobs/lease {toolchainVersion}      -> 204 (sleep, e.g. 30 s) | 200 lease
  GET  inputs[0].url  (verify SHA-256 = inputs[0].sha256)                -> model.ifc
  run the IFC/Blender pipeline; every GeometryLeaseSeconds/3: POST .../renew
  POST .../upload-urls {files:[building.glb, building_metadata.json, ...]} -> PUT each file with the returned headers
  POST .../outputs  preview_glb          (objectKey = outputPrefix + "building.glb")
  POST .../outputs  building_metadata    (objectKey = outputPrefix + "building_metadata.json")
  POST .../complete {outputHash of the last outputs call}
  on a pipeline failure: POST .../fail {reason}
```

`...` is `/internal/processing/jobs/{jobId}` and every call after the lease carries `attemptId` and `leaseToken`.

## Endpoints

1. **Lease** `POST /internal/processing/geometry-jobs/lease` `{ "toolchainVersion": "..." }`
   - `200`: the claim of the oldest waiting Geometry job (Recover runs first, so a job whose worker died is offered again):

     ```json
     {
       "jobId": "…", "kind": "Geometry", "attemptId": "…", "leaseToken": "…", "leaseUntil": "…", "receiptId": "…",
       "revisionId": "…", "outputPrefix": "worker-output/<organizationId>/<jobId>/<attemptId>/", "inputHash": "<sha256 of the IFC>",
       "replayed": false,
       "inputs": [ { "artifactType": "ifc_source", "artifactId": "<source document id>", "sha256": "…", "sizeBytes": 123,
                     "url": "https://… (GET, 15 minutes)", "expiresAt": "…" } ]
     }
     ```

     `url` is null when the object is missing or its size changed: fail the job, never process other bytes. Check the SHA-256 after
     download. The response contains no storage key or credential.
   - `204`: nothing waiting. `400 VALIDATION_ERROR`: toolchain not in `AllowedToolchains` (exact, case-sensitive).
     `409 GEOMETRY_PULL_DISABLED`: pull is off on this deployment. `503 PROCESSING_EXECUTOR_*`: worker login misconfigured.
   - A delivery that the push dispatcher already handed to a push worker (lost ACK) is marked delivered and not handed out again.
2. **Renew** `POST /internal/processing/jobs/{jobId}/renew` `{ attemptId, leaseToken }` about every `GeometryLeaseSeconds / 3`.
   Returns `{ leaseUntil }` extended by `GeometryLeaseSeconds` (only attempts obtained through the Geometry lease; push attempts keep
   60 s). `409 ATTEMPT_STALE`: the lease is lost; stop and discard the output.
3. **Inputs again** `POST /internal/processing/jobs/{jobId}/inputs` `{ attemptId, leaseToken }` → `{ inputs, outputPrefix }`
   (same `ifc_source` entry, freshly signed).
4. **Upload URLs** `POST /internal/processing/jobs/{jobId}/upload-urls`
   `{ attemptId, leaseToken, files: [ { path, sizeBytes, sha256 } ] }` → `{ outputPrefix, files: [ { path, url, headers, expiresAt } ] }`.
   Paths are relative to the attempt prefix (`building.glb`, `building_metadata.json`, `preview_metadata.json`, …; letters, digits,
   `.`, `_`, `-`, `/`, no `..`, max 200 characters). `PUT` the body within 30 minutes with exactly the returned headers
   (`x-amz-checksum-sha256`, `x-amz-sdk-checksum-algorithm`); S3 rejects a body whose SHA-256 differs. Single PUT, no multipart.
5. **Outputs** `POST /internal/processing/jobs/{jobId}/outputs` `{ attemptId, leaseToken, output }`, once per artifact:

   ```json
   { "artifactType": "preview_glb", "objectKey": "<outputPrefix>building.glb", "sha256Hash": "…", "sizeBytes": 123,
     "schemaVersion": "fet3d.editor/1", "metadata": { "schemaVersion": "fet3d.editor/1", "...": "geometry metadata" },
     "validatorVersion": "…", "outcome": "Passed", "issues": [] }
   { "artifactType": "building_metadata", "objectKey": "<outputPrefix>building_metadata.json", "sha256Hash": "…", "sizeBytes": 456,
     "schemaVersion": "fet3d.building/1", "metadata": { "schemaVersion": "fet3d.building/1", "...": "spaces, interactables, …" },
     "validatorVersion": "…", "outcome": "Passed", "issues": [] }
   ```

   - `metadata` of `preview_glb`: [`contracts/editor/v1/geometry-metadata.schema.json`](../contracts/editor/v1/geometry-metadata.schema.json)
     (`422 EDITOR_SCHEMA_INVALID` / `EDITOR_SCHEMA_VERSION_UNSUPPORTED`). `metadata` of `building_metadata`:
     [`contracts/ifc/v1/building-metadata.schema.json`](../contracts/ifc/v1/building-metadata.schema.json)
     (`422 BUILDING_METADATA_INVALID` / `BUILDING_METADATA_VERSION_UNSUPPORTED`).
   - `issues[]`: `{ severity: Info|Warning|Error|Critical, code, message, details?: {}, ifcGlobalIds?: [≤100 GlobalIds of 22 chars] }`,
     at most 1000; codes in [ifc-qa-codes.md](ifc-qa-codes.md). A blocking problem is `Error`/`Critical` with `outcome: Failed`.
   - Returns `{ artifactId, outputHash }`. `objectKey` must be under `outputPrefix`; the same artifact type again with other content
     answers `409 OUTPUT_CONFLICT`.
6. **Complete** `POST /internal/processing/jobs/{jobId}/complete` `{ attemptId, leaseToken, outputHash }`. Before accepting a pulled
   attempt the backend reads S3: every registered output object must exist with the declared size and S3-verified SHA-256, otherwise
   `409 GEOMETRY_STORAGE_MISMATCH` (upload again and retry while the lease lasts) or `503 GEOMETRY_STORAGE_UNAVAILABLE` (retry).
   Then the existing Geometry acceptance runs: artifacts, validation run, issues; revision `ReadyForScenario` (Passed, no
   `IFC_EXIT_UNKNOWN`) or `NeedsFix`; `building_metadata.spaces` upserted into `bim_facts`. `409 OUTPUT_CONFLICT`: wrong `outputHash`.
7. **Fail** `POST /internal/processing/jobs/{jobId}/fail` `{ attemptId, leaseToken, reason (1-1000 chars) }` when the pipeline cannot
   produce a result (unreadable IFC, missing input). The job becomes Failed; the OrganizationUser retries explicitly. Never fail for a
   lost lease.

A worker that dies keeps its attempt until the lease passes; the next lease call recovers it (attempt `Expired`, job `Queued`, a
`ProcessingJobRequeue` event) and claims the job under a new attempt with a new prefix. Run one Geometry job per worker process.

## Error codes

| Status | Code | Where | Meaning |
|---|---|---|---|
| 400 | `VALIDATION_ERROR` | all | Bad body, toolchain not allowed, unsafe path, bad hash |
| 401 | — | all | Missing/wrong `X-Worker-Key` (user JWTs are never accepted) |
| 409 | `GEOMETRY_PULL_DISABLED` | lease | `GeometryPullEnabled=false` |
| 409 | `ATTEMPT_STALE` | renew, inputs, upload-urls, outputs, complete, fail | Not the current attempt/lease |
| 409 | `OUTPUT_CONFLICT` | outputs, complete | Different content for a registered type, or wrong `outputHash` |
| 409 | `GEOMETRY_STORAGE_MISMATCH` | complete | Uploaded objects missing or not matching size/SHA-256 |
| 422 | `EDITOR_SCHEMA_*`, `BUILDING_METADATA_*` | outputs | Metadata does not satisfy its contract |
| 503 | `GEOMETRY_STORAGE_UNAVAILABLE` | complete | S3 could not be checked |
| 503 | `PROCESSING_EXECUTOR_FORBIDDEN` / `_LOGIN_FAILED` | all | Worker database login misconfigured (see `/health/worker`) |

## Tests and limits

`AuthIntegrationTests.Geometry_pull_*`, `Pushed_geometry_attempts_keep_the_short_lease`, `Geometry_lease_endpoint_*`,
`Health_worker_checks_the_geometry_lease_gate_*` (actual migrations, restricted API/worker logins, fake S3) and
`GeometryWorkerUnitTests` (options, dispatcher key, storage check, response shaping). A real VM, IFC/Blender toolchain and AWS
checksum behaviour are not verified by these tests. The S3 bucket must store `x-amz-checksum-sha256` on presigned PUT (AWS S3 does);
without stored checksums every pulled Complete answers `GEOMETRY_STORAGE_MISMATCH`.

Decisions: the storage check applies to pulled attempts only, so the existing push worker contract (own uploads, 60 s lease) is
unchanged; the pull worker shares `MachineKey` with the package worker; output paths are not restricted to fixed names (any safe
relative path under the attempt prefix).
