# IFC durable enqueue

Migration `AddIfcIntegrationOutbox` delivers the missing `integration_outbox_events`, consumer receipt table, immutable envelope trigger and six-argument `enqueue_integration_outbox_event` used by `IfcWriteStore.ProcessRevisionAsync`.

The ProcessingJobRequested contract is schema `1`. PostgreSQL derives the organization through job → revision → Building and computes SHA-256 from canonical JSONB. Duplicate key/same envelope returns `AlreadyEnqueued`; changed input fails. System and requeue events cannot use the tenant enqueue entrypoint. Processing job, audit and event commit together; enqueue failure rolls back all three.

The NOLOGIN integration owner has only the required table permissions. Backend roles can enqueue and read, without direct event DML; Supabase anon/authenticated cannot execute these entrypoints or read events. Temporary schema CREATE/membership used to transfer ownership is restored in the migration transaction.

Source: Docs `fire_evacuation_schema.sql` durable handoff and enqueue gates; this implementation uses PostgreSQL's built-in SHA-256 to avoid assuming where pgcrypto is installed, with the same UTF-8 JSONB digest.

Verified with disposable PostgreSQL: process creates scoped canonical event/audit, duplicate/conflict, invalid payload/hash and rollback on enqueue failure. The original migration tests alone do not prove transport or processing. Subsequent [HTTP worker](processing-worker.md) and [Redis delivery](redis-processing.md) tests cover isolated delivery/receipt/recovery contracts; provider/toolchain and production processing remain separate. This migration does not claim full schema v7.
