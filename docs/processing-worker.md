# Processing gates and HTTP worker (selected scope)

Transport defaults to PostgreSQL transactional outbox → configured HTTP worker. Optional RedisStreams inserts a Redis publisher and BE bridge before the same HTTP worker; see [Redis processing](redis-processing.md). It implements BE delivery/provenance contracts with a simulated worker; it does not implement IFC/Blender/Unity toolchains.

## Enablement

Apply `AddBoundIfcUploads` then `AddProcessingWorkerGates`. Both are additive; legacy sources/artifacts are not marked verified/accepted. Preflight stops on invalid job hashes or duplicate logical inputs. Do not reset/delete data to hide a failed preflight.

Defaults: `ProcessingWorker__DispatcherEnabled=false`, `ProcessingWorker__WorkerApiEnabled=false`. A process 202 means durable job only, even with dispatcher disabled.

For an enabled worker deployment, configure:

```ini
ProcessingWorker__DispatcherEnabled=true
ProcessingWorker__WorkerApiEnabled=true
ProcessingWorker__WorkerUrl=https://<trusted-worker-host>/<delivery-endpoint>
ProcessingWorker__MachineKey=<separate-random-machine-secret-32-to-512-characters>
ProcessingWorker__AllowedToolchains__0=<approved-toolchain-version>
ProcessingWorker__PollSeconds=5
ConnectionStrings__ProcessingExecutor=<restricted-login-member-of-fet3d_processing_executor>
```

Do not commit credentials. API identity executes request/dispatch/requeue gates; worker executor only executes `processing_worker_gate`. Neither has direct provenance/outbox writes. The migration owner is restricted NOLOGIN; temporary membership/schema CREATE is restored. Configure worker URL in deployment, never in user requests. HTTPS only; redirects disabled; delivery timeout 15 seconds, lease 60 seconds, retry capped 30 minutes, 10 delivery failures become Failed.

## User flow

`POST /api/revisions/{revisionId}/process`, header `Idempotency-Key: process-file-001`: verified source required; returns 202 `{jobId}` + Location. Missing key 400; unverified source 422 `IFC_SOURCE_NOT_VERIFIED`. Same key/input replays; concurrent different keys still map to one logical job. Job, canonical schema `1` event, receipt and audit commit together.

Retry keeps `{requestId, reason}` at `/api/processing-jobs/{id}/retry`. Only Failed accepts a new key; old matching key returns AlreadyRequeued even after job completion; changed input conflicts. Cancelled/Succeeded are not retried by new keys.

## Worker protocol

Every internal request uses `X-Worker-Key`, not a user Bearer token. Base route `/internal/processing/jobs/{jobId}`:

- `POST /claim`: `{eventKey,payloadHash,inputHash,toolchainVersion}`. Validate event envelope and current source; returns attemptId, leaseToken, leaseUntil, receiptId, outputPrefix, sourceKey/sourceHash, inputHash, scenarioVersionId, replayed/status. Receipt and claim effects commit before HTTP ACK. When replayed, ACK existing receipt; do not start the same work again.
- `POST /renew`: `{attemptId,leaseToken}`; renew before lease expires.
- `POST /outputs`: `{attemptId,leaseToken,output:{artifactType,objectKey,sha256Hash,sizeBytes,schemaVersion,metadata,validatorVersion,outcome,issues}}`. Key must be within this attempt's outputPrefix. SHA-256 must be real worker-computed file digest; SQL checks binding/format, not geometry or file bytes. Outcome Passed/Failed; issue severity Info/Warning/Error/Critical, with code/message/details. Output registration returns artifactId and canonical aggregate outputHash.
- `POST /complete`: `{attemptId,leaseToken,outputHash}`. Hash must match registered outputs; package jobs require unity_package and manifest. Accepted artifacts/validation/issues/status/audit commit together. Passed structural data alone is not readiness or content approval.
- `POST /fail`: `{attemptId,leaseToken,reason}`. A new explicit retry can requeue Failed.

Delivery endpoint responds 2xx with `{eventKey,payloadHash,receiptId}` only after claim receipt has committed. Dispatcher checks DB receipt before marking Published. Missing ACK re-delivers the same envelope; expired attempt is fenced and recovery enqueues a new delivery. Old output cannot publish. QA/issues/artifacts/preview only read current accepted successful attempts; legacy unknown provenance stays excluded.

## Acceptance evidence and limits

Tests `AuthIntegrationTests.Ifc_process_*`, `Ifc_worker_*`, `Ifc_http_worker_*` use actual EF history, restricted API/machine executor logins and simulated HTTP provider. They cover process idempotency, source rejection, concurrent claim, lost ACK, stale lease, output hash rejection, audit rollback and retry. Provider/toolchain and deployment remain separate. Publish remains fail-closed; no learner grant/start or auto Trial is added here.

## Read-contract regression

Accepted-output read fixtures must seed both the job and current attempt as Succeeded. Validation, QA, artifacts and issues exclude Running/Failed/stale attempts; failed historical validation is not exposed as accepted output. Preview tests explicitly begin with a Running attempt and then accept it. The legacy processing-log column is quoted `"AttemptNumber"`, matching the real migration.

2026-10-07: 105 IFC tests passed with explicit loopback `FIRE3D_IFC_TEST_CONNECTION`, including 11 PostgreSQL read/editor tests. These read/editor fixtures use partial DDL; selected write/worker/readiness/playtest tests in AuthTests separately run actual EF migration history with restricted roles. Docker-only IFC write/store/legacy containment fixtures were not run because Docker was unavailable. No real IFC/Blender/Unity/provider execution is implied.
# Editor preview storage verification

Editor preview requires accepted current-attempt provenance, complete transform/floor/semantic metadata and a positive recorded artifact size. Before signing a five-minute GET URL, the backend performs S3 HEAD against the server-selected bucket/key and compares content length. Missing objects or size mismatches return `NotReady` with no URL. Access denied, timeout and service errors return `503 PREVIEW_STORAGE_UNAVAILABLE`; caller cancellation is preserved. HEAD verifies existence at that instant, not the SHA-256 or geometry QA. A signed URL is not publish authorization.
