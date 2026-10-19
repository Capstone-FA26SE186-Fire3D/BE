# Unity package worker (BE#110) and package download (Mobile#22)

The Unity build worker runs Unity Editor 6000.6.6f1 in batch mode on a team Azure VM with **no inbound port**. It pulls package
jobs from the backend, downloads its inputs and uploads its outputs through short signed S3 URLs, and never holds S3 or database
credentials. Contract of the files: [`contracts/package/v1/manifest.schema.json`](../contracts/package/v1/manifest.schema.json)
(`fet3d.package/1`). Migration `20261017120000_AddPackageBuildPull`.

## Deployment settings

```ini
ProcessingWorker__WorkerApiEnabled=true
ProcessingWorker__PackagePullEnabled=true          # the HTTP dispatcher then skips package jobs
ProcessingWorker__PackageLeaseSeconds=300          # 60-900, lease of package jobs on claim and renew
ProcessingWorker__MachineKey=<32-512 random characters, the VM's X-Worker-Key>
ProcessingWorker__AllowedToolchains__0=<the Unity worker toolchain id, e.g. unity-6000.6.6f1-worker-1>
ProcessingWorker__PackageBuildTargets__0=Android   # default; package-builds refuses other targets (400 BUILD_TARGET_UNSUPPORTED)
ConnectionStrings__ProcessingExecutor=<restricted login, member of fet3d_processing_executor>
```

The Geometry (IFC) worker on the same VM pulls Geometry jobs the same way: see [Geometry worker](geometry-worker.md).
Pull works with the `Http` transport (dispatcher on or off). The API refuses to start with pull plus `RedisStreams` delivery,
because the Redis publisher would still push package jobs. Apply the migration before deploying the binary; keep pull off until the
VM is ready (with pull off, package jobs stay with the push dispatcher as before).

## Production state (17/10/2026)

- The VM `UNITY-WORKER` runs the Unity repo agent `Tools/PackageWorker` (Windows PowerShell 5.1, Scheduled Task started at boot,
  `X-Worker-Key` stored with DPAPI). It sends `toolchainVersion: "unity-6000.6.6f1-worker-1"`, so `AllowedToolchains` must contain
  exactly that string (the value was still `unity-6000.6.3f1-worker-1` and answered `400 VALIDATION_ERROR` until it was changed).
- The `ProcessingExecutor` connection string must be a login that is a member of `fet3d_processing_executor`: the lease calls
  `processing_package_lease_gate`, which is granted to that role only. Production answered `500` on lease with key and toolchain
  accepted and `/health/ready` healthy; tracked in BE#118.
- The VM is deallocated when idle; jobs created meanwhile wait in the database and are leased when it starts again.

## Checking the worker login (BE#118)

`GET /health/worker` (header `X-Worker-Key` required, so it answers `401` unless the worker API is enabled with a valid key; codes only, result cached 30 s) checks the `ProcessingExecutor` login: `200 OK` or `503`
with `PROCESSING_EXECUTOR_FORBIDDEN` (the login is not a member of `fet3d_processing_executor`, or cannot execute the lease gate
while pull is on), `PROCESSING_EXECUTOR_LOGIN_FAILED` (wrong user or password; through the Supabase pooler also `Tenant or user not found` and, after
repeated failures, `ECIRCUITBREAKER`, which blocks the login for a few minutes even once the setting is fixed), `PROCESSING_EXECUTOR_UNREACHABLE`, or
`PROCESSING_EXECUTOR_TOO_PRIVILEGED` (works, but the login bypasses row security, e.g. `postgres`: use a dedicated login). It is not
part of `/health` or `/health/ready`, so a worker misconfiguration never marks the API unhealthy. Worker calls with such a login
answer `503` with the same code instead of `500`. Create the login once:

```sql
CREATE ROLE fet3d_processing_worker LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION CONNECTION LIMIT 10 PASSWORD '<random>';
GRANT fet3d_processing_executor TO fet3d_processing_worker;
GRANT USAGE ON SCHEMA public TO fet3d_processing_worker;
```

Through the Supabase pooler the connection string user is `fet3d_processing_worker.<project-ref>`; keep the API's host and port.

## Worker loop

All calls use the header `X-Worker-Key`; user tokens are rejected (401). Base URL: the API (`https://api.fet3d.io.vn`).

1. **Lease** `POST /internal/processing/package-jobs/lease` `{ "toolchainVersion": "...", "buildTarget": "Android" }`
   - `204`: nothing to build; poll again later (e.g. every 30 s).
   - `200`: the claim of the oldest waiting `PlaytestPackage`/`ReleasePackage` job with that build target:
     `jobId`, `kind`, `attemptId`, `leaseToken`, `leaseUntil`, `outputPrefix`, `inputHash`, `inputSnapshot`
     (`scenarioVersionId`, `scenarioHash`, `rubricHash`, `buildTarget`, …), `scenarioSnapshot` (the approved `fet3d.editor/1`
     state; write it as `scenario.json`), `geometryAttemptId` and `inputs[]`.
   - `inputs[]`: one entry per artifact of the revision's accepted Geometry attempt (`preview_glb`, `building_metadata`, …):
     `artifactType`, `artifactId`, `sha256`, `sizeBytes`, `schemaVersion`, `url` (GET, 15 minutes), `expiresAt`. Check the SHA-256
     after download. `url` is null when the object is missing or its size changed: fail the job, do not build from other bytes.
     `inputs` is empty when the revision has no accepted Geometry yet (the job cannot be built: fail it).
   - `409 PACKAGE_PULL_DISABLED`: pull is off on this deployment. `400`: toolchain not allowed.
2. **Renew** `POST /internal/processing/jobs/{jobId}/renew` `{ attemptId, leaseToken }` about every `PackageLeaseSeconds / 3`
   (100 s with the default) while Unity runs. A lost lease answers `409 ATTEMPT_STALE`: stop the build and discard its output.
3. **Inputs again** `POST /internal/processing/jobs/{jobId}/inputs` `{ attemptId, leaseToken }` re-signs `inputs[]` if they expired.
4. **Upload URLs** `POST /internal/processing/jobs/{jobId}/upload-urls`
   `{ attemptId, leaseToken, files: [ { path, sizeBytes, sha256 } ] }` for every output file **including `manifest.json`**
   (1-2001 files). Returns `{ outputPrefix, files: [ { path, url, headers, expiresAt } ] }`: `PUT` the file body to `url` within 30
   minutes and send **exactly** the returned `headers` (`x-amz-checksum-sha256`, `x-amz-sdk-checksum-algorithm`). S3 rejects a body
   whose SHA-256 differs. Single PUT only (no multipart). Paths are relative (`floor_L1`, `bundles/floor_L1_lighting`), no `..`.
5. **Outputs** `POST /internal/processing/jobs/{jobId}/outputs`, twice, with the same `objectKey = outputPrefix + "manifest.json"`,
   `sha256Hash` and `sizeBytes` of manifest.json:
   - `artifactType: "manifest"`, `schemaVersion: "fet3d.package/1"`, `metadata`: the manifest document (all required fields of the
     schema, including `files[]` and `floors[]`). `422 PACKAGE_MANIFEST_INVALID` lists the problems by path.
   - `artifactType: "unity_package"`, any `schemaVersion`, `metadata` with `buildTarget` (the pinned one), `protocolVersion`,
     `manifestSchemaVersion`, `minRuntimeVersion`, `requiredCapabilities` (same values as the manifest). It may add provenance
     (`unityVersion`, `runtimeVersion`, input hashes, `fileCount`, `totalSizeBytes`): playtest start, release Build and Publish compare
     only those five fields between the two outputs (migration `20261019130000_CompareReleasePackageContractFields`, BE#130).
   - `outcome` `Passed` (or `Failed` with issues when the build ran but the result is unusable), `validatorVersion`, `issues[]`.
6. **Complete** `POST /internal/processing/jobs/{jobId}/complete` `{ attemptId, leaseToken, outputHash }` (the `outputHash` of the
   last output call). Before accepting, the backend reads S3: manifest.json and every listed file must exist under the attempt prefix
   with the declared size and S3-verified SHA-256, and manifest.json must list the same `files` and `floors` as the registered
   metadata. Otherwise `409 PACKAGE_STORAGE_MISMATCH` (fix the upload and call Complete again while the lease lasts) or
   `503 PACKAGE_STORAGE_UNAVAILABLE` (retry). `422 PACKAGE_MANIFEST_INVALID`: the two outputs do not name the same object.
7. **Fail** `POST /internal/processing/jobs/{jobId}/fail` `{ attemptId, leaseToken, reason }` (Unity exit code 1, missing inputs).
   The job becomes Failed; the OrganizationUser retries it explicitly. Never fail for a lost lease.

A worker that dies keeps its attempt until the lease passes; the next lease call recovers the job under a new attempt (new
`attemptId`, new prefix). Run one job per Unity project at a time. The worker never publishes and never writes billing data.

## Why the manifest is the package

The `manifest` and `unity_package` outputs name the same `manifest.json`, so `packageHash` = `manifestHash` everywhere
(release, playtest, learner start). The chain of trust is: package hash → manifest.json → SHA-256 of each file it lists. Existing
release/playtest/learner pins keep working unchanged. Packages registered before this contract (one file, no `files[]`) still
work for those flows but have no download endpoint (`409 PACKAGE_FORMAT_UNSUPPORTED`).

## Mobile download

After the download Mobile launches Unity with the bridge protocol in [`contracts/runtime/v1`](../contracts/runtime/v1/README.md).

Docs FR-TRAINING-04: prepare pins the package, Mobile downloads and verifies it, then calls start (which rechecks access,
entitlement and the seat). Migration `20261017130000_AllowPackageDownloadBeforeStart`.

- Before start the download passes the same checks as start, without allocating anything (migration `20261018110000_RecheckPackageDownloadAccess`): learner live session, Building/organization active, Public or participation grant, Training/release available, content approval, runtime, an Active paid entitlement and a free or existing seat (`BUILDING_ACCESS_REQUIRED`, `TRAINING_UNAVAILABLE`, `BUILDING_ENTITLEMENT_REQUIRED`, `LEARNER_LIMIT_REACHED`, …); playtest Building/organization active and the entitlement start would use (Active with an Applied BuildingService payment, or Trial with playtest units left; otherwise `PLAYTEST_ENTITLEMENT_REQUIRED`), nothing consumed (migration `20261018120000_MatchPackageDownloadToStart`). Unverified pending registrations are refused like at start.
- Learner: `GET /api/training/sessions/{id}/package` (Trainee). With the preparation id right after prepare (Prepared, not
  expired; no seat is used), and with the same id after start for the login that started it (`Launching`/`Running`, not completing).
- Playtest: `GET /api/playtests/{playtestId}/package` (OrganizationUser). Before start for the session start would accept (the Mobile
  login that redeemed the handoff, or the preparing session when there is no handoff; no Trial unit is used); after start for the login that started it (`Launching`/`Running`).

Response (no storage keys):

```json
{
  "schemaVersion": "fet3d.package/1",
  "manifest": { "url": "https://…", "sha256": "…", "sizeBytes": 2048 },
  "files": [ { "path": "floor_L1", "url": "https://…", "sha256": "…", "sizeBytes": 10485760 } ],
  "floors": [ { "id": "L1", "bundle": "floor_L1", "lightingBundle": "floor_L1_lighting", "scene": "L1" } ],
  "runtimeVersion": "2.0.0", "buildTarget": "Android", "expiresAt": "…"
}
```

URLs last 10 minutes; call again for fresh ones. Save every file under one folder at its `path` next to `manifest.json`, check
manifest.json against `manifest.sha256` and each file against its `sha256`, then send `Launch` with `manifestPath`. Cache by
manifest hash; a cache never allows a new start offline. Errors: `409 PACKAGE_SESSION_NOT_ACTIVE` (expired preparation, another
login, or the session ended), `409 PACKAGE_FORMAT_UNSUPPORTED`, `404` outside scope.

## Tests and limits

`AuthIntegrationTests.Package_pull_*`, `Package_lease_endpoint_*`, `Playtest_package_files_*` (actual migrations, restricted
logins) and `PackageWorkerUnitTests` (storage verification, signed checksum header, manifest rules). S3 is faked in tests; a real
VM, Unity build and AWS checksum behaviour are not verified yet. The S3 bucket must support `x-amz-checksum-sha256` on presigned PUT
and `ChecksumMode` on HEAD (AWS S3 does); without stored checksums every Complete returns `PACKAGE_STORAGE_MISMATCH`.

Open decisions: a separate machine key for the Unity VM (it shares `MachineKey` with the IFC worker today); whether package jobs
should pin the Geometry attempt in `input_snapshot` (today the accepted attempt is resolved at lease time and returned as
`geometryAttemptId` for provenance); whether learner downloads should also accept the continuation token.
