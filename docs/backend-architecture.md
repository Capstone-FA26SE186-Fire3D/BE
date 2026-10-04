# BE runtime and target architecture

Use [Docs technology](../../Docs/fire-evacuation-training-technology.md), [workflows](../../Docs/fire-evacuation-training-workflows.md) and [schema](../../Docs/fire_evacuation_schema.sql) as the target contract. Diagrams below label current code separately from target behavior; an architecture diagram is not proof a worker/provider is deployed.

## Product v7 gates — target design

[Docs v7](../../Docs/schema_v7_contract.md) requires PlatformAdmin approval of the submitted scenario/rubric content hash, separately from revision/version technical readiness. Submission freezes reviewed content; editing creates a new version/review. Publish checks both gates and package/runtime/provenance, Training and entitlement. The versioned Organization template/rubric/equipment library remains separate from public Learn.

Private is the default Building visibility. Participation-code verification grants account-bound access at the current access revision; rotation/revocation or visibility changes invalidate older grants. List/package/prepare check access. Online start rechecks access, approved/published content, active entitlement and runtime, allocates a distinct-user seat atomically per Building/period and pins entitlement/review/rubric. Prepare/playtest allocate no seats; started sessions continue/sync after expiry or access loss with owner/replay checks.

Payments provision 6/12-month Building lines and prepaid AI top-ups idempotently, including replay after expiry. Upgrade retains period and consumed seats; renewal creates a new period without overlap. Organization AI pools prepaid grants; Trainee daily quota stays separate. Request reserve/settle/release consumes existing quota; uncertain requests reconcile without postpaid invoices/debt.

Learner retrieval rechecks access/service before vector search and indexes exactly approved/published name/objectives/instructions. Draft/rubric/answers/private IFC stay excluded. After access loss, own saved result explanations and permitted Common content remain available. Assessment blocks AI and records the pinned rubric, criterion results and outcome separately from session completion. Prices/quota validity/rollover/upgrade formulas/rubric thresholds require explicit policy. These modules remain implementation work in the [checklist](api-implementation-checklist.md).

## HTTP response rules

`POST` does not automatically mean `201`.

| Operation type | Status | Fire3D examples |
|---|---:|---|
| Creates a new resource synchronously | `201 Created` with `Location` | register, create account/organization/building/scenario/draft/snapshot/playtest, initiate IFC upload, create review |
| Requests durable asynchronous work | `202 Accepted` with a polling location | process IFC revision, retry failed processing job, forgot password |
| Runs an action on an existing resource | `200 OK` or `204 No Content` | login, refresh, validate draft, publish/start/confirm, logout, reset password, upload-complete |

`POST /api/buildings/{buildingId}/ifc` creates a revision before it returns the presigned upload URL, therefore it returns `201 Created` and `Location: /api/revisions/{revisionId}`.

## Current runtime architecture

```mermaid
flowchart LR
    Client[Web / Mobile / Unity] -->|HTTPS + Bearer| API[ASP.NET Core API]
    API --> Auth[Authentication + authorization]
    Auth --> Firebase[Firebase Admin\nGoogle ID token verification]
    API --> MediatR[MediatR commands and queries]
    MediatR --> App[Application layer\nvalidation + business rules]
    App --> Stores[Infrastructure stores]
    Stores --> DB[(Supabase PostgreSQL)]
    App --> S3[AWS S3\nprivate objects + presigned upload]
    App --> Mailgun[Mailgun]
    API --> FCM[Firebase Cloud Messaging]
    DB --> ResetWorker[Password reset worker\nclaim / send / complete]
    DB --> IFCWorker[IFC worker\nplanned consumer]
```

The API is the only public entry point. Clients never receive database, S3, Firebase Admin, or Mailgun credentials. Authorization reloads role, organization and active state from PostgreSQL; it does not trust a tenant or role from the request body.

## Current IFC API path — partial implementation

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API + MediatR
    participant D as PostgreSQL
    participant S as S3

    C->>A: POST /buildings/{id}/ifc
    A->>D: create revision
    A->>S: create presigned PUT URL
    A-->>C: 201 revision + upload URL
    C->>S: upload IFC directly
    C->>A: POST /revisions/{id}/upload-complete
    A->>S: verify object size
    A->>D: create source document
    A-->>C: 204
    C->>A: POST /revisions/{id}/process
    A->>D: create job + audit + outbox event
    Note over A,D: Current source writes placeholder payload_hash; align with the v7 event contract
    A-->>C: 202 /processing-jobs/{jobId}
    C->>A: GET job, QA, issues, artifacts, logs
    A->>D: tenant-scoped read
    A-->>C: persisted status/result (does not prove a worker ran)
```

The API creates a durable job/outbox record, but this does not establish that an outbox dispatcher or production IFC consumer is running. The current BE store still writes a placeholder payload hash. Treat processing/QA output as unavailable until the event contract is valid and a worker is connected and verified.

## Event-driven target

### PayOS payment boundary (implemented 2026-10-02)

The PayOS path uses PostgreSQL recovery queues and the official `payOS` SDK 2.1.0. A short application transaction reserves a checkout with a stable orderCode and immutable provider input. Create/get/cancel run outside database transactions, with timeout15s and no SDK retries. Verified results are persisted before an EXECUTE-only SQL gate binds a Pending request. A fully paid link recovered after losing the create response can still bind; provider GET and navigation query parameters never authorize financial writes.

An SDK-verified webhook commits a normalized inbox entry before HTTP200. Separate least-privilege request/webhook connections invoke SECURITY DEFINER gates owned by a NOLOGIN ledger role. The inbox gate atomically applies a transaction, records Paid, inserts per-line provisioning work and audit. A fenced worker then commits each snapshot entitlement/receipt/audit in a short transaction, keeping Paid independent from provisioning success. Admin reconciliation reschedules failed work and expired capped Pending claims; it never creates an unsigned payment or repeats Applied money.

Provider results received after tenant/session/quotation validity changes remain durable for investigation; they do not bypass the bind gate. Missing-order responses other than confirmed HTTP404 remain uncertain until the provider contract is verified. Runtime migration, production identity/grant checks and a real payment are deployment acceptance steps, not established by mock or disposable database tests. See [PayOS deployment and manual tests](payos-deployment.md).

An external call must never run inside an open PostgreSQL transaction. A command writes its aggregate, audit record, and an immutable outbox envelope in one short transaction. A dispatcher then publishes it; a consumer records its receipt and business effect in one transaction before acknowledging the message.

```mermaid
flowchart LR
    Command[HTTP command] --> Tx[Short PostgreSQL transaction]
    Tx --> Aggregate[Business state + audit]
    Tx --> Outbox[(integration_outbox_events)]
    Outbox --> Dispatcher[Outbox dispatcher\nlease / retry]
    Dispatcher --> Queue[Durable queue]
    Queue --> Consumer[IFC / email / notification consumer]
    Consumer --> Gate[Backend-owned DB gate]
    Gate --> Effect[Business result + receipt in one transaction]
    Effect --> Receipt[(integration_event_consumptions)]
    Receipt --> Ack[ACK only after commit]
```

The processing worker claims its attempt/lease through the backend and registers artifact/validation/issues through the lease-bound gate such as `register_processing_output`. It does not write protected provenance tables directly. This target follows the event and recovery contract in Docs technology; it is not evidence that the full dispatcher/consumer/gates run in the current environment.

### Migration order

These steps cover the event pipeline only; v7 product dependencies are tracked in the [implementation checklist](api-implementation-checklist.md).

1. Keep existing synchronous resource creation transactional with its audit record.
2. Standardize `ProcessingJobRequested` and `ProcessingJobRequeue` outbox envelopes: event ID, aggregate ID, tenant-derived scope, schema version, canonical payload hash and idempotency key.
3. Add a dispatcher that leases outbox rows, publishes them, and retries without exposing a queue to clients.
4. Add an IFC consumer that claims a processing attempt through the database gate, then commits artifacts, BIM facts, QA/progress and an idempotent consumption receipt through the authorized result path before ACK. Keep the business effect and receipt atomic.
5. Move email and FCM delivery to the same pattern. Password reset already uses a durable queue/worker pattern and is the reference for worker retries.
6. Add integration tests for duplicate delivery, consumer crash after effect/before ACK, stale lease, tenant isolation and outbox retry.

The current IFC process path creates the processing job, audit record and `ProcessingJobRequested` outbox row in one transaction, but its payload hash is a placeholder. It still needs the canonical envelope/hash, real dispatcher/consumer and gate-backed result path before it can be treated as a completed event-driven pipeline. For the task-by-task gaps and acceptance criteria, use the [BE implementation checklist](api-implementation-checklist.md).
