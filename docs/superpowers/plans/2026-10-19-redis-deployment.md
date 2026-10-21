# Redis recovery and deployment repair

**Goal:** Fix the reviewed Redis delivery dead ends and prevent avoidable deployment failures.

**Architecture:** Keep PostgreSQL authoritative and preserve immutable envelopes and receipts. Use an additive migration to repair recovery selection and allow an authorized, idempotent retry of exhausted deliveries. Readiness must check enabled processing dependencies without changing process liveness. Deployment checks the candidate migration list before replacing the live image.

**Constraints:** No production migrations, remote deployment, push, or secret changes. Preserve tenant checks, restricted SQL roles, job/attempt fencing, and receipt idempotency. Work from reviewed main 4bfe6a5: develop diverges by 4/227 commits; merging it would add unrelated changes.

## Task 1: Redis SQL repair
- [ ] Add regression tests for more than 20 blocked deliveries and exhausted publication/handoff retry.
- [ ] Add a forward migration filtering blocked/deferred/terminal entries before LIMIT.
- [x] Extend the existing authorized requeue gate for queued jobs whose delivery is exhausted; reuse the immutable event and retain retry evidence, preventing duplicate retries or retrying running/completed jobs.
- [ ] Verify on isolated PostgreSQL, including restricted runtime roles and unchanged receipt behavior.

## Task 2: Processing readiness
- [ ] Add tests for disabled processing, disconnected Redis, missing SQL privileges, and healthy dependencies.
- [ ] Add tagged readiness check for Redis and enabled dispatcher/consumer/recovery gates; sanitize errors and preserve liveness.
- [ ] Add actionable warnings for blocked/exhausted work without logging payloads or credentials.

## Task 3: Deployment
- [ ] Correct Render build context, JWT and email setting names; exclude local artifacts from Docker context.
- [ ] Add read-only candidate migration preflight before Azure deploy; missing secret, DB access, or migrations blocks rollout.
- [ ] Add relevant CI regression checks and bounded smoke requests, document required settings and rollout.

## Verification
- [ ] Run focused Redis SQL/readiness tests, build, configuration validation and diff review.
- [ ] Review final changes for authorization, locks, idempotency and deployment ordering.
- [ ] Report actual pass/fail/skip counts and any environment limits; leave production untouched.
