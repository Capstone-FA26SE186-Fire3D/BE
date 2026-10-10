# Organization AI and quota accounting

Organization users ask an external FastAPI adapter for a scenario draft or a grounded answer. The backend owns tenant
checks, source selection, quota reservation and settlement; the adapter only produces a proposal. Migration:
`20261011140000_AddOrganizationAi` (`Persistence/Sql/OrganizationAi.sql`). Nothing is seeded: no policy, no source, no quota.

AI never changes a scenario, rubric or release. A draft is returned for the editor to copy; saving still goes through the
normal editor contract.

## Configuration

```text
OrganizationAi__Enabled=false        # default
OrganizationAi__BaseUrl=https://ai.internal.example/   # https, or loopback for local testing
OrganizationAi__ApiKey=<sent as Bearer, optional>
OrganizationAi__TimeoutSeconds=60    # 5-600
OrganizationAi__LeaseSeconds=180     # > timeout, <= 3600
OrganizationAi__MaxAttempts=3        # only for provably undelivered calls
OrganizationAi__BackoffSeconds=30
OrganizationAi__PollSeconds=5
```

Without an enabled, valid adapter, submit returns `503 AI_PROVIDER_UNAVAILABLE` before anything is reserved, and the worker
does not start.

## Policies and units

`ai_operation_policies` (Admin, immutable): `operation` (ScenarioDraft | Answer), `quotaUnit`, `reserveUnits`,
`maxInputChars`, `maxSources`, `effectiveFrom`/`effectiveUntil`. Periods for one operation cannot overlap
(`409 AI_POLICY_OVERLAP`). No active policy → `503 AI_POLICY_UNAVAILABLE`. The unit is matched against the quota grant unit;
there is no money/token conversion in the backend.

Quota comes only from the existing `billing_ai_quota_grants` (service, upgrade and top-up grants from #56). The request
reserves `reserveUnits` in `billing_ai_quota_allocations` across active grants of that unit, earliest expiry first, under one
organization lock. If the free balance is lower → `409 AI_QUOTA_EXHAUSTED`. Balance and history stay readable through
`GET /api/organizations/me/ai-quota` and `/ai-quota/grants`.

## Sources

Eligible sources for a tenant, re-evaluated at submit and again at settlement:

| Kind | Eligible when |
|---|---|
| KnowledgeSource | Approved, inside its effective window, and Common or owned by this organization |
| LibraryVersion | Published version of an active Library item |
| LearnPost | Current published version of a Published or Hidden post (`learn_rag_eligible`) |

The request pins the chosen sources (id, version, content hash). An empty list pins every eligible source up to
`maxSources`. Anything outside the list is `422 AI_SOURCE_NOT_ALLOWED`. ScenarioDraft also needs an active Building of the
caller's organization (`404` otherwise).

Knowledge source registry (Admin): create as Draft (`visibility`, `title`, `versionLabel`, `sourceHash` = SHA-256 of the
document, optional https `sourceUri`, `jurisdiction`, effective window), then approve (Draft → Approved) or retire
(Approved → Retired) with If-Match. Each change enqueues `PlatformCacheInvalidation` with `scope:"knowledge"`. Learn
publishing (#57) already requires approved Common sources.

## Request lifecycle

```text
Queued ──claim──> Running ──valid result──> Succeeded | InsufficientEvidence | SafetyRejected   (settle used units, release the rest)
                     │──not delivered──> Queued (backoff) … after MaxAttempts ──> Failed AI_PROVIDER_UNREACHABLE (release)
                     │──provider 400/422──> Failed AI_PROVIDER_REJECTED (release)
                     │──timeout / 5xx / lease expired──> NeedsReconcile AI_OUTCOME_UNKNOWN (reservation kept)
                     └──invalid result──> NeedsReconcile AI_RESULT_INVALID | AI_CITATION_INVALID | AI_USAGE_INVALID | AI_USAGE_EXCEEDS_RESERVATION
NeedsReconcile ──admin reconcile──> lookup GET /v1/requests/{id}
                     ├──valid result──> settled as above
                     ├──404──> Failed AI_REQUEST_NOT_RECEIVED (release)
                     └──anything else──> stays NeedsReconcile
```

- The `ai_requests` row, committed together with the reservation, is the durable dispatch record. The worker claims it with a
  lease and calls the adapter outside any transaction.
- An outcome that is not known (timeout, dropped connection, 5xx, worker crash) is never re-sent and never refunded
  automatically. The stable request id lets the adapter deduplicate and answer the lookup.
- Settlement accepts only usage in the pinned unit and within the reservation. Every citation must be one of the pinned
  sources and still eligible. The adapter's own tenant filtering is not trusted.
- Invalid results are kept as `providerResult`, which only Admin can see. The organization sees the status and
  `failureCode`, never the withheld content.
- Reconcile only schedules a lookup; it never edits consumed numbers.

## FastAPI adapter contract v1

`POST {BaseUrl}/v1/organization/scenario-draft` and `POST {BaseUrl}/v1/organization/answer`, header `X-Request-Id`:

```json
{ "requestId": "…", "organizationId": "…", "operation": "Answer",
  "request": { "question": "…", "sources": [] },
  "sources": [{ "kind": "KnowledgeSource", "id": "…", "versionId": "…", "title": "…", "versionLabel": "2022", "contentHash": "…" }],
  "limits": { "quotaUnit": "tokens", "maxUnits": 100 },
  "building": { "id": "…", "name": "…" } }
```

Response `200` (also returned by `GET {BaseUrl}/v1/requests/{requestId}`; unknown id → `404`):

```json
{ "requestId": "…", "outcome": "Succeeded | InsufficientEvidence | SafetyRejected",
  "answer": "plain text (Answer)", "draft": { "state": { "schemaVersion": "fet3d.editor/1" }, "notes": "…" },
  "message": "plain text (non-success outcomes)",
  "citations": [{ "kind": "KnowledgeSource", "id": "…", "locator": "Article 3" }],
  "usage": { "quotaUnit": "tokens", "units": 42 } }
```

Answers and notes are plain text (no markup, at most 8000 characters). Drafts must pass the `fet3d.editor/1` shape
validator. `Succeeded` needs at least one citation.

## Endpoints

| Method | Route | Auth | Notes |
|---|---|---|---|
| GET | `/api/ai/organization/sources` | OrganizationUser | Eligible sources now. |
| POST | `/api/ai/organization/scenario-draft` | OrganizationUser | `{buildingId, prompt, sources?}` + Idempotency-Key → `202` + Location. |
| POST | `/api/ai/organization/answer` | OrganizationUser | `{question, sources?}` + Idempotency-Key → `202`. |
| GET | `/api/ai/requests/{requestId}` | OrganizationUser | Same organization only. |
| GET | `/api/admin/ai/requests?status=&organizationId=` | PlatformAdmin | Includes allocations and `providerResult`. |
| GET | `/api/admin/ai/requests/{requestId}` | PlatformAdmin | |
| POST | `/api/admin/ai/requests/{requestId}/reconcile` | PlatformAdmin | Idempotency-Key; NeedsReconcile only → `202`. |
| GET/POST | `/api/admin/ai/policies` | PlatformAdmin | POST + Idempotency-Key → `201`. |
| GET/POST | `/api/admin/knowledge-sources` | PlatformAdmin | POST + Idempotency-Key → `201` Draft. |
| GET | `/api/admin/knowledge-sources/{id}` | PlatformAdmin | `ETag` = revision. |
| POST | `/api/admin/knowledge-sources/{id}/approve\|retire` | PlatformAdmin | If-Match. |

Every mutation recomputes the actor and family from the JWT inside the gate. Idempotency receipts are keyed by
actor, operation and key, and hash the canonical input (not the family).

## Not covered yet

Acceptance used a fake in-process FastAPI service only. The real provider/model, retrieval quality, indexing and
production prompts need their own acceptance. Neither Supabase nor Azure has been configured.

## Tests

`Fire3D.AuthTests/OrganizationAiPostgresTests.cs`: policy pinning, reservation, idempotent replay, cross-tenant reads,
revoked family; race for the last quota; timeout after the provider committed → NeedsReconcile → lookup settles without
re-sending; crashed lease; lookup 404 releases; foreign-tenant citation, usage beyond reservation, markup and safety
outcome; retry then fail for undelivered calls; ScenarioDraft with a foreign Building; HTTP 503 without adapter, 422
validation, If-Match on the source registry.
