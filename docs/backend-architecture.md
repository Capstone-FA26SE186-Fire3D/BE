# Fire3D BE architecture and event-driven migration

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

The API is the only public entry point. Clients never receive database, Redis, S3, Firebase Admin, or Mailgun credentials. Authorization reloads role, organization and active state from PostgreSQL; it does not trust a tenant or role from the request body.

## Current API communication flow

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API + MediatR
    participant D as PostgreSQL
    participant S as S3
    participant W as IFC worker

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
    A->>D: create job + audit + outbox event in one transaction
    A-->>C: 202 /processing-jobs/{jobId}
    W->>D: claim event/job and write attempt/artifact/QA
    C->>A: GET job, QA, issues, artifacts, logs
    A->>D: tenant-scoped read
    A-->>C: durable status/result
```

## Event-driven target

An external call must never run inside an open PostgreSQL transaction. A command writes its aggregate, audit record, and an immutable outbox envelope in one short transaction. A dispatcher then publishes it; a consumer records its receipt and business effect in one transaction before acknowledging the message.

```mermaid
flowchart LR
    Command[HTTP command] --> Tx[Short PostgreSQL transaction]
    Tx --> Aggregate[Business state + audit]
    Tx --> Outbox[(integration_outbox_events)]
    Outbox --> Dispatcher[Outbox dispatcher\nlease / retry]
    Dispatcher --> Stream[Redis Stream or queue]
    Stream --> Consumer[IFC / email / notification worker]
    Consumer --> Receipt[(integration_event_consumptions)]
    Consumer --> Effect[Worker business transaction]
    Effect --> Ack[ACK only after commit]
```

### Migration order

1. Keep existing synchronous resource creation transactional with its audit record.
2. Standardize `ProcessingJobRequested` and `ProcessingJobRequeue` outbox envelopes: event ID, aggregate ID, tenant-derived scope, schema version, canonical payload hash and idempotency key.
3. Add a dispatcher that leases outbox rows, publishes them, and retries without exposing a queue to clients.
4. Add an IFC consumer that claims a processing attempt through the database gate, records an idempotent consumption receipt, then writes artifacts, BIM facts, QA and progress logs.
5. Move email and FCM delivery to the same pattern. Password reset already uses a durable queue/worker pattern and is the reference for worker retries.
6. Add integration tests for duplicate delivery, consumer crash after effect/before ACK, stale lease, tenant isolation and outbox retry.

The current IFC process path already creates the processing job, audit record and `ProcessingJobRequested` outbox row in one transaction. It still needs a real dispatcher/consumer and schema-level validation of the event envelope before it can be treated as a completed event-driven pipeline.
