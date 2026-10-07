# Redis processing transport

Redis Streams is a transport for IFC and package-build jobs; PostgreSQL keeps the job, transactional outbox and receipts. Public API contracts do not change. `202` means a durable job, not completed work.

The default remains `ProcessingWorker:Transport=Http`, `ConsumerEnabled=false` and `Redis:Enabled=false`. Redis credentials stay in User Secrets/deployment secret storage. Connection options are `Redis:Host`, `Port`, `Ssl`, `Password`; TLS is mandatory outside Development. A configured but disconnected Redis must not stop unrelated API requests.

`RedisStreams` publisher requires Redis enabled. Its bridge consumer additionally requires the existing HTTPS WorkerUrl and separate MachineKey. Redis keys are namespaced by application environment; stream `fet3d:{environment}:processing`, group `fet3d-processing-bridge-v1`. No cache, distributed auth rate limit, Hangfire or BullMQ is added.

Configuration/connection foundation does not by itself publish or consume events. Migration, publisher, consumer and recovery acceptance are separate implementation milestones. Real Redis Azure and real IFC/Unity workers remain separate from isolated tests.
