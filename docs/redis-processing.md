# Redis processing transport

Redis Streams is a transport for IFC and package-build jobs; PostgreSQL keeps the job, transactional outbox and receipts. Public API contracts do not change. `202` means a durable job, not completed work.

The default remains `ProcessingWorker:Transport=Http`, `ConsumerEnabled=false` and `Redis:Enabled=false`. Redis credentials stay in User Secrets/deployment secret storage. Connection options are `Redis:Host`, `Port`, `Ssl`, `Password`; TLS is mandatory outside Development. A configured but disconnected Redis must not stop unrelated API requests.

`RedisStreams` publisher requires Redis enabled. Its bridge consumer additionally requires the existing HTTPS WorkerUrl and separate MachineKey. Redis keys are namespaced by application environment; stream `fet3d:{environment}:processing`, group `fet3d-processing-bridge-v1`. No cache, distributed auth rate limit, Hangfire or BullMQ is added.

Apply `AddProcessingRedisDelivery` before enabling the publisher. Configure `ConnectionStrings:DispatcherExecutor` with a restricted login that is a member of `fet3d_dispatcher_executor`; it cannot directly mutate outbox or jobs. Publisher claims a 60-second lease, XADDs outside the transaction, then records stream ID/publication under the current lease. Lost responses can duplicate delivery of the same immutable event. `Published` means sent to Redis, not handed off or completed. Publication failures retry with backoff; ten failed publication attempts remain Failed for intervention. The legacy HTTP transport is unchanged.

Apply `AddProcessingRedisConsumption` before enabling the bridge. Consumer checks the entire envelope and tenant against PostgreSQL, then calls the configured HTTP worker. Worker claim writes its attempt, delivery receipt and `integration_event_consumptions` atomically. Bridge ACKs only after verifying durable receipt; this means handoff, not completed IFC/package output. Old handoff receipts are carried forward from trusted evidence. Duplicate/missing HTTP responses and missing ACKs replay the same receipt, even after that processing attempt expires. Invalid envelopes are preserved as bounded diagnostics without raw payload and do not reach the worker.

Apply `AddProcessingRedisRecovery` with the other two migrations before switching transport. Every 30 seconds, the restricted API recovery gate recovers expired processing attempts and requeues Published events without handoff receipts after five minutes. It keeps the original event/hash, skips terminal jobs and preserves delivery history. Publication and handoff retries use 30-second to 30-minute backoff with a ten-attempt ceiling; exhausted or rejected entries need operator investigation. XAUTOCLAIM reassigns pending messages idle for 60 seconds, using a scan cursor so protected entries do not starve later work.

Retention is seven days by default. A message is removed only after PostgreSQL confirms its durable handoff and an atomic Redis script checks its age, expected sole consumer group, delivery cursor and absence from pending. Pending, unread, rejected and unconfirmed entries stay. Extra groups disable deletion. There is no stream TTL, MAXLEN or unconditional XTRIM, and this worker does not delete PostgreSQL outbox/receipts. Logs contain event/job/stream IDs, retry/error codes and backlog counts, without payload/credentials.

Docker tests use isolated loopback PostgreSQL with real EF migrations/restricted roles and password-protected Redis 7.4 with AOF/NoEviction. HTTP worker execution is simulated. Real Azure delivery and real IFC/Unity workers require separate acceptance.

## Configuration and rollout

Nonsecret settings belong in appsettings or environment variables. Passwords and connection strings belong in User Secrets locally, Azure App Service settings/Key Vault when deployed. User Secrets are normally loaded only in Development; environment variables override appsettings and secrets.

```ini
Redis__Enabled=true
Redis__Host=fet3d-redis-streams-dev.eastasia.redis.azure.net
Redis__Port=10000
Redis__Ssl=true
Redis__Password=<regenerated-secret>
Redis__Group=fet3d-processing-bridge-v1
Redis__PendingIdleSeconds=60
Redis__RecoverySeconds=30
Redis__HandoffTimeoutSeconds=300
Redis__RetentionDays=7
ProcessingWorker__Transport=RedisStreams
ProcessingWorker__DispatcherEnabled=true
ProcessingWorker__ConsumerEnabled=true
ProcessingWorker__WorkerUrl=https://<trusted-worker>/<delivery-endpoint>
ProcessingWorker__MachineKey=<separate-machine-secret>
ConnectionStrings__DispatcherExecutor=<restricted-dispatcher-login>
```

The bridge/reconciler use `DefaultConnection` for execute-only gates. Existing worker routes use `ProcessingExecutor`, `WorkerApiEnabled=true`, and an explicit `AllowedToolchains` list on the API that serves them. A publisher-only host does not need the HTTP worker URL; a bridge does. Do not enable machine APIs without their existing configuration. Auth and PayOS configuration remain unchanged.

1. Run [read-only migration preflight](../database/redis-processing-preflight.sql). Use an authorized schema migration identity with ownership of the existing outbox/consumption/delivery tables, CREATEROLE and admin membership of the restricted function-owner role, and schema CREATE grant option (or schema ownership). Table ownership/REFERENCES and RLS-policy privileges are prerequisites; CREATEROLE alone is insufficient. Apply all three additive migrations; verify data, ownership/RLS, function EXECUTE grants and restoration of temporary role/schema privileges. Do not grant these migration capabilities to runtime logins. No Supabase migration is run as part of these Docker tests.
2. Give a dedicated login membership of `fet3d_dispatcher_executor`, schema USAGE and connection rights. Do not grant direct DML or membership of function-owner/worker roles. The API identity needs EXECUTE on stream consumer/recovery gates; the worker identity keeps only its existing machine gate. Anonymous/public clients must not have access.
3. Check Azure network access/TLS and cluster shard ports; validate HA, AOF policy and NoEviction in Azure before acceptance. Persistence does not replace the PostgreSQL replay source. See [Azure architecture](https://learn.microsoft.com/en-us/azure/redis/architecture), [persistence](https://learn.microsoft.com/en-us/azure/redis/how-to-persistence) and [configuration](https://learn.microsoft.com/en-us/azure/redis/configure).
4. Switch a dev namespace first, then observe outbox publication, stream pending, durable handoff and accepted processing output independently. Keep defaults Http/disabled until deployment gates pass.
5. For rollback, stop new Redis publication, drain/reconcile existing Redis deliveries and expired attempts, then switch Http. Preserve streams and receipts; never delete backlog or rewrite envelope hashes to force success.

Invalid messages remain pending with diagnostic codes; inspect event IDs and authoritative PostgreSQL envelope under operational access. Exhausted events require deliberate investigation/requeue through authorized gates, not direct edits of receipts/provenance. Redis stream length includes ACKed retention entries and is not the count of running jobs.

## Isolated tests

Use a disposable loopback PostgreSQL server with CREATEDB and a separate password-protected Redis 7.4 container. The fixture creates/drops its own databases and restricted logins; never set these variables from User Secrets or production connections.

```powershell
$env:FIRE3D_TEST_ADMIN_CONNECTION='<disposable-loopback-postgres-admin>'
$env:FET3D_BILLING_TEST_ADMIN=$env:FIRE3D_TEST_ADMIN_CONNECTION
$env:FIRE3D_RESET_TEST_ADMIN=$env:FIRE3D_TEST_ADMIN_CONNECTION
$env:FIRE3D_TEST_REDIS_PORT='<disposable-redis-loopback-port>'
$env:FIRE3D_TEST_DOCS='<Docs-checkout>'
dotnet test Fire3D/Fire3D.AuthTests/Fire3D.AuthTests.csproj
$env:FIRE3D_IFC_TEST_CONNECTION=$env:FIRE3D_TEST_ADMIN_CONNECTION
dotnet test Fire3D/Fire3D.IfcTests/Fire3D.IfcTests.csproj
dotnet build Fire3D/Fire3D.slnx
```

Redis fixtures use the explicit disposable password `fet3d-disposable-test`; it is never a deployment credential. Key namespaces are unique per test and cleaned up. Test classes cover concurrent publication/consumption, lost responses/ACKs, receipt rollback, wrong envelope scope/hash, Redis/group loss, expired publisher leases, retry exhaustion and protected retention. [Redis Streams](https://redis.io/docs/latest/develop/data-types/streams/) and [XAUTOCLAIM](https://redis.io/docs/latest/commands/xautoclaim/) document the transport primitives.

HTTP fixtures also need safe startup defaults if ignored appsettings files are absent: a fake base64 `Jwt__SigningKey` (at least32bytes), test issuer/audience, fake Mailgun ApiKey/Domain/From, HTTPS AuthEmail URLs and a disposable bootstrap DefaultConnection. Set these in the test process environment, keep email/PayOS/processing workers and Redis disabled globally, and let each fixture override services/connections. Never copy real User Secrets into test config. Re-read Docker's assigned Redis port after a container restart.

## Acceptance evidence — 2026-10-07

- Full Auth regression with disposable PostgreSQL/Redis: **452 passed, 0 failed, 0 skipped**, including 19 Redis cases and restricted/nonsuperuser migration coverage. An earlier full run had one PayOS partial-provisioning failure; that test passed individually and in the final full run. No PayOS implementation/assertion was changed to make it pass; keep the intermittent failure as a follow-up observation.
- IFC regression including Docker legacy fixtures: **110 passed, 0 failed, 0 skipped**. Solution build: **0 warnings, 0 errors**. Do not add overlapping target-run counts to these totals.
- Redis Docker AOF restart preserved a unique test stream and consumer group. This proves that disposable persistence check, not in-flight adapter reconnect or Azure HA.
- Initial Azure dev smoke failed before XADD because TCP port10000 was unavailable. The user subsequently reported Public network access disabled and enabled it; TCP/TLS/key and isolated stream publication/read/ACK then succeeded, with pending0. The owned dev-smoke stream was removed. This transport check did not call an HTTP worker or validate PostgreSQL handoff on Azure.
- The three Redis migrations were subsequently applied to Supabase with user authorization on2026-10-07. See deployment evidence below. Real HTTP worker/IFC/Blender/Unity, deployment and clients require separate acceptance. No auth cache/rate-limit Redis was implemented.

## Supabase deployment evidence — 2026-10-07

Applied `20261007150000_AddProcessingRedisDelivery`, `20261007151000_AddProcessingRedisConsumption` and `20261007152000_AddProcessingRedisRecovery` in one guarded transaction after read-only preflight. A fresh connection confirmed history, restricted gate ownership/EXECUTE and no direct runtime DML; temporary schema/role grants were restored. Counts and existing account/organization representations were preserved:11users,6organizations,2Buildings; processing jobs/outbox/receipts were empty at that snapshot. No reset or provider call was made.

This deployment creates the restricted NOLOGIN dispatcher role, not its application login/connection. Http/consumer-disabled defaults remain. Azure TCP/TLS/key/Streams smoke subsequently passed after the user's network change; no Azure settings were changed by the agent. Binary rollout, dispatcher login and full worker handoff remain deployment steps.

Read-only INFO reported `maxmemory_policy=volatile-lru`, which does not meet the planned NoEviction configuration. INFO on the contacted server reported `aof_enabled=0`; that alone cannot determine the resource's persistence setting because [Azure runs AOF only on replicas when HA is enabled](https://learn.microsoft.com/en-us/azure/redis/how-to-persistence). Confirm HA and persistence in Azure Advanced settings/control-plane evidence. No eviction/persistence changes were made automatically.
