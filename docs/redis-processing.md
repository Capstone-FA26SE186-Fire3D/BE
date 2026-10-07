# Redis processing transport

Redis Streams is a transport for IFC and package-build jobs; PostgreSQL keeps the job, transactional outbox and receipts. Public API contracts do not change. `202` means a durable job, not completed work.

The default remains `ProcessingWorker:Transport=Http`, `ConsumerEnabled=false` and `Redis:Enabled=false`. Redis credentials stay in User Secrets/deployment secret storage. Connection options are `Redis:Host`, `Port`, `Ssl`, `Password`; TLS is mandatory outside Development. A configured but disconnected Redis must not stop unrelated API requests.

`RedisStreams` publisher requires Redis enabled. Its bridge consumer additionally requires the existing HTTPS WorkerUrl and separate MachineKey. Redis keys are namespaced by application environment; stream `fet3d:{environment}:processing`, group `fet3d-processing-bridge-v1`. No cache, distributed auth rate limit, Hangfire or BullMQ is added.

Apply `AddProcessingRedisDelivery` before enabling the publisher. Configure `ConnectionStrings:DispatcherExecutor` with a restricted login that is a member of `fet3d_dispatcher_executor`; it cannot directly mutate outbox or jobs. Publisher claims a 60-second lease, XADDs outside the transaction, then records stream ID/publication under the current lease. Lost responses can duplicate delivery of the same immutable event. `Published` means sent to Redis, not handed off or completed. Publication failures retry with backoff; ten failed publication attempts remain Failed for intervention. The legacy HTTP transport is unchanged.

Consumer and recovery acceptance are separate milestones. Real Redis Azure and real IFC/Unity workers remain separate from isolated tests.
