# AI requests, quota accounting and the AI adapter contract

Organization users ask the external FastAPI adapter for a scenario draft or a grounded answer; Trainees ask grounded questions
about Learn, a Training they may play or their own result. The backend owns tenant and Trainee checks, source and BIM pinning,
quota reservation and settlement; the adapter only produces a proposal. Migrations: `20261011140000_AddOrganizationAi`,
`20261012180000_AddOrganizationAiRequestList`, `20261014138000_AddAiContractV2` (`Persistence/Sql/AiContractV2.sql`,
`Persistence/Sql/KnowledgeChunks.sql`). Nothing is seeded: no policy, no source, no quota.

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

Without an enabled, valid adapter, submit (organization and Trainee) returns `503 AI_PROVIDER_UNAVAILABLE` before anything is
reserved, and the worker does not start. Invalid settings never stop the API (#176): with `Enabled=true` and a missing/invalid
`BaseUrl`, out-of-range timing, or any value of the wrong type (`TimeoutSeconds=abc`), AI stays off, the worker logs an error and
`/health/ready` keeps its status (AI is optional) while listing `checks.organizationAi.code = AI_CONFIGURATION_INVALID` with the
reason; the deploy smoke test prints a warning for it.

## Vocabulary (Docs v7)

| Field | Values |
|---|---|
| `status` | `Accepted` → `Processing` → `Succeeded` \| `Failed` \| `NeedsReconcile` (`Rejected` is reserved by v7, not produced yet) |
| `resultType` (set when `Succeeded`) | `KnowledgeAnswer`, `ScenarioDraft`, `InsufficientEvidence`, `RejectedBySafetyGate` |
| `result.status` (what the AI produced) | `Answered` (KnowledgeAnswer), `NeedsUserEdit` (ScenarioDraft only), `InsufficientEvidence`, `RejectedBySafetyGate` |
| `audience` | `organization`, `trainee` |
| `operation` | `Answer`, `ScenarioDraft` (organization), `TraineeAnswer` (trainee) |

The pre-v7 names `Queued`, `Running`, `SafetyRejected` and the status `InsufficientEvidence` are gone; list filters reject them
with 400. Existing rows were mapped in place by the migration.

## Policies and units

`ai_operation_policies` (Admin, immutable except for ending): `operation`, `quotaUnit`, `reserveUnits`, `maxInputChars`, `maxSources`,
`effectiveFrom`/`effectiveUntil`, and `dailyUnits` for `TraineeAnswer` only (1–100000000, at least `reserveUnits`). Periods for
one operation cannot overlap (`409 AI_POLICY_OVERLAP`). No active policy → `503 AI_POLICY_UNAVAILABLE`.

`quotaUnit` is `tokens` or `requests`, the only units the AI service settles; anything else is `422` with issue code
`AI_QUOTA_UNIT_UNSUPPORTED` (billing quota policies, which back package/top-up AI grants, answer `422 AI_QUOTA_UNIT_UNSUPPORTED`
too, as do packages, quotations, upgrades and top-ups that would grant another unit). A quotation issued earlier that still pins
another unit is refused at checkout (`POST /api/payments/payos/create` → `409 AI_QUOTA_UNIT_UNSUPPORTED`) before any link or
reservation exists, so money never buys quota the AI service cannot spend; the admin cancels it and issues a new one. Agreed plan: `requests` (one question = one unit) for `TraineeAnswer` (20/day), `Answer` and `ScenarioDraft`.
A Trainee's daily usage counts only requests in the unit of the active `TraineeAnswer` policy, so changing the unit mid-day
neither locks the Trainee out nor lifts the limit.

To replace a policy, end it with `POST /api/admin/ai/policies/{id}/end` (`{effectiveUntil?}`, default now; Idempotency-Key) and
create the next one from that instant. Only `effectiveUntil` changes: it must not be in the past and must be earlier than the
current end (`422 AI_POLICY_END_INVALID`); an ended policy is `409 AI_POLICY_ENDED`. A policy that has not started is cancelled
(removed, `cancelled: true`) when the end is at or before its start. Accepted requests keep the policy they pinned. Migration
`20261027090000_AddAiPolicyEndAndUnits` ended started policies with other units and cancelled scheduled ones (production:
`TraineeAnswer` `ce279028-cdd0-49bf-ba09-094151e6551f`, `luot-hoi`).

- **Organization:** quota comes only from `billing_ai_quota_grants` (service, upgrade and top-up grants). A request reserves
  `reserveUnits` in `billing_ai_quota_allocations` across active grants of that unit, earliest expiry first, under one
  organization lock; lower free balance → `409 AI_QUOTA_EXHAUSTED`. `GET /api/ai/organization/limits` tells the client before
  submitting: per operation `policyActive`, `quotaUnit`, `estimatedReserve`, `maxInputChars`, `maxSources`, `availableUnits`,
  `canSubmit`.
- **Trainee:** no billing grant. Each Trainee may hold `dailyUnits` per Asia/Ho_Chi_Minh calendar day: settled usage plus open
  reservations (`Accepted`, `Processing`, `NeedsReconcile`). Above that → `409 AI_DAILY_QUOTA_EXHAUSTED`. A replay with the same
  Idempotency-Key never reserves twice. `GET /api/ai/trainee/quota` returns `usedUnits`, `reservedUnits`, `remainingUnits`,
  `estimatedReserve`, `resetsAt`, `blockedByAssessment`, `canSubmit`.

## Sources

| Kind | Organization may pin | Trainee may pin |
|---|---|---|
| KnowledgeSource | Approved, in its effective window, Common or own tenant | Approved Common only |
| LibraryVersion | Published version of an active Library item | never (rubric samples) |
| LearnPost | Current published version of a Published or Hidden post (`learn_rag_eligible`) | same |
| TrainingSnapshot / SessionResult | — | the Trainee context below |

The request pins the chosen sources (kind, id, versionId, title, versionLabel, contentHash, link). An empty list pins every
eligible source up to `maxSources`; anything outside → `422 AI_SOURCE_NOT_ALLOWED`. Eligibility is checked again at settlement.

`link` lets the client open a citation: `learnSlug` + `learnPostId` (LearnPost), `libraryItemId` (LibraryVersion), `sourceUri`
(KnowledgeSource). Every response field is present; the ones that do not apply are null.

Knowledge source registry (Admin): create as Draft (`visibility`, `title`, `versionLabel`, `sourceHash` = SHA-256 of the document,
optional https `sourceUri`, `jurisdiction`, effective window), then approve (Draft → Approved) or retire (Approved → Retired)
with If-Match. Each change enqueues `PlatformCacheInvalidation` with `scope:"knowledge"`.

## Scenario draft BIM facts (#91)

`POST /api/ai/organization/scenario-draft` takes `{buildingId, prompt, sources?, revisionId?}`. The request pins one revision
whose geometry was accepted (status ReadyForScenario/ConfirmedForTraining, current succeeded Geometry attempt, Passed QA without
Error/Critical issues, `fet3d.editor/1` metadata): `revisionId` if given (another Building's revision → 404), otherwise the newest
such revision. None → `409 AI_REVISION_NOT_READY`. The pinned `bim` object (sent to the adapter, shown to Admin):

```json
{ "revisionId": "…", "buildingId": "…", "versionLabel": "v1", "revisionStatus": "ReadyForScenario",
  "geometry": { "artifactId": "…", "sha256Hash": "…", "schemaVersion": "fet3d.editor/1" },
  "units": "m", "upAxis": "Y", "handedness": "right", "coordinateTransform": [16 numbers],
  "floors": [{ "id": "L1", "name": "Tầng 1", "elevationMeters": 0, "ifcGlobalId": "…" }],
  "anchors": [{ "ifcGlobalId": "…", "nodeId": "door_101", "floorId": "L1", "semanticType": "IfcDoor" }],
  "annotations": [{ "id": "…", "ifcGlobalId": "…", "label": "…", "note": "…" }],
  "qualityFlags": [{ "code": "EXIT_MAPPING_MISSING", "severity": "Warning", "message": null, "ifcGlobalId": null }],
  "runtime": { "runtimeVersion": "1.4.0", "protocolVersion": "1", "capabilities": [], "capabilityContracts": {} } }
```

`qualityFlags` holds the QA findings of the accepted run (Info/Warning) plus derived flags: `SEMANTIC_MAPPING_EMPTY`,
`EXIT_MAPPING_MISSING` (no IfcDoor/IfcStair/IfcStairFlight/IfcRamp), `FLOOR_MAPPING_INCOMPLETE`, `ANCHORS_TRUNCATED` (more than
5000 anchors) and `RUNTIME_CATALOG_EMPTY`. `runtime` is the newest active runtime catalog entry.

A draft result is accepted only when it keeps the pinned geometry (`state.geometry.artifactId`/`sha256Hash`), anchors to the
pinned revision (every `bim_anchors[].revision_id` equal, every `ifc_global_id` an anchor or floor of the pin, at least one), and
the editor reference check (`scenario_reference_issues`) finds nothing. Otherwise `NeedsReconcile AI_BIM_ANCHOR_INVALID`. If the
revision lost its accepted geometry meanwhile: `NeedsReconcile AI_REVISION_STALE`.

## Trainee context (#77)

`POST /api/ai/trainee/answer` takes `{question, context?, sources?}` (+ Idempotency-Key). `context.kind`:

- `Learn` + `postId` + `versionId`: the post is Published and that is its published version; the version is pinned as a LearnPost source.
- `Training` + `trainingId`: Active Training, Published release with approved content review, inside its dates, on an active Building
  that is Public or where the Trainee holds a participation grant of the current access revision. A learner-safe snapshot is pinned as
  `TrainingSnapshot` (training name/description, Building, scenario name, learning objectives, learner instructions, allowed modes,
  time limit) — never rubric, scoring, scenario state, drafts or IFC.
- `Result` + `sessionId`: the Trainee's own learner session with a saved result; pinned as `SessionResult` (mode, outcome, reason,
  score, criteria, metrics). It stays usable after Building access is lost.

Unavailable context → `404 AI_CONTEXT_NOT_FOUND`. While the Trainee has an Assessment session in Created/Launching/Running, submit
returns `409 AI_BLOCKED_DURING_ASSESSMENT`. A Trainee sees only their own requests; when a Training context is no longer
accessible, or the Learn post was removed, the request is returned with `resultWithheld: true`, `result: null` and no citations.

## Request representation

`GET /api/ai/requests[/{id}]` (organization), `GET /api/ai/trainee/requests[/{id}]` (Trainee) and the Admin routes return
`AiRequestView` (OpenAPI schema): `requestId, audience, operation, status, resultType, quotaUnit, reservedUnits, consumedUnits,
input {text, inputHash, buildingId, buildingName, revisionId, requestedSources, context}, sources (pins with link), result
{status, answer | draft {state, notes} | message, confidence, assumptions, limitations, bimAnchors}, citations [{kind, id,
versionId, title, versionLabel, chunkId, locator, relevance, link}], failureCode, resultWithheld (Trainee only), createdAt,
completedAt, revision`. `input` lets a client that lost the submit response find its request again by text or `inputHash`.

Citations are resolved against the pins: kind, title, version and link come from the backend, never from the provider. Admin
views add `organizationId, actorUserId, policyId, attempts, modelVersion, technicalUsage, reconcileRequestedAt, providerResult,
updatedAt, bim, allocations`. Invalid provider output is kept in `providerResult` for Admin only.

Validation failures of these routes return `422 CONTENT_INVALID` with both `issues` (code/path/message) and `errors` (field →
messages, the same map as `VALIDATION_ERROR` elsewhere).

## Request lifecycle

```text
Accepted ──claim──> Processing ──valid result──> Succeeded (resultType set; settle used units, release the rest)
                       │──not delivered──> Accepted (backoff) … after MaxAttempts ──> Failed AI_PROVIDER_UNREACHABLE (release)
                       │──provider 400/422──> Failed AI_PROVIDER_REJECTED | AI_SOURCE_HASH_MISMATCH | AI_SOURCE_UNAVAILABLE | AI_REVISION_NOT_PINNED (release)
                       │──timeout / 5xx / lease expired──> NeedsReconcile AI_OUTCOME_UNKNOWN (reservation kept)
                       └──invalid result──> NeedsReconcile AI_RESULT_INVALID | AI_CITATION_INVALID | AI_USAGE_INVALID
                                            | AI_USAGE_EXCEEDS_RESERVATION | AI_BIM_ANCHOR_INVALID | AI_REVISION_STALE
NeedsReconcile ──admin reconcile──> lookup GET /v2/requests/{id}
                       ├──valid result──> settled as above
                       ├──404──> Failed AI_REQUEST_NOT_RECEIVED (release)
                       └──anything else──> stays NeedsReconcile
```

- The `ai_requests` row, committed together with the reservation, is the durable dispatch record. The worker claims it with a
  lease and calls the adapter outside any transaction.
- An unknown outcome is never re-sent and never refunded automatically; the stable request id lets the adapter deduplicate and
  answer the lookup. Reconcile only schedules a lookup; it never edits consumed numbers.

## FastAPI adapter contract v2

Routes: `POST {BaseUrl}/v2/organization/answer`, `POST {BaseUrl}/v2/organization/scenario-draft`, `POST {BaseUrl}/v2/trainee/answer`,
`GET {BaseUrl}/v2/requests/{request_id}` (same response body; unknown id → 404). Headers: `X-Request-Id`, `Authorization: Bearer
<ApiKey>` when configured. Request body (built by the gate at claim time):

```json
{ "request_id": "…", "audience": "organization | trainee", "operation": "Answer | ScenarioDraft | TraineeAnswer",
  "response_type": "KnowledgeAnswer | ScenarioDraft", "organization_id": "… or null for trainee",
  "input": { "text": "question or prompt" }, "input_hash": "sha256 hex",
  "sources": [{ "kind": "KnowledgeSource | LibraryVersion | LearnPost | TrainingSnapshot | SessionResult",
                "document_id": "…", "document_version": "…", "title": "…", "version_label": "…", "content_hash": "…",
                "source_uri": "https://… (KnowledgeSource) or null", "content": { } , "content_omitted": false }],
  "bim": { … see above, ScenarioDraft only },
  "learn_context": { "post_id": "…", "version_id": "…" },
  "context": { "kind": "Learn | Training | Result", … },
  "limits": { "quota_unit": "tokens", "max_units": 100 }, "attempt": 1 }
```

Source content: LearnPost (`title, summary, contentKind, blocks`), LibraryVersion (`kind, code, name, payload,
requiredCapabilities`), TrainingSnapshot and SessionResult (the snapshot) arrive in `content`, at most 256 KiB per source
(`content_omitted: true` when larger). KnowledgeSource documents are not stored by the backend: the service downloads
`source_uri` and must check SHA-256 = `content_hash`, or uses `knowledge_chunks` of that `document_id` (below). Documents are
untrusted data; instructions inside them must be ignored.

Response `200` (Docs `fire_evacuation_bim_rag_pccc.md` section 6):

```json
{ "request_id": "…", "response_type": "KnowledgeAnswer | ScenarioDraft",
  "status": "Answered | NeedsUserEdit | InsufficientEvidence | RejectedBySafetyGate",
  "recommendation": "plain text (answer, draft notes or the reason)",
  "scenario_draft": { "state": { "schemaVersion": "fet3d.editor/1", "geometry": { "artifactId": "…", "sha256Hash": "…" } }, "notes": "…" },
  "bim_anchors": [{ "ifc_global_id": "…", "entity_type": "IfcDoor", "revision_id": "…" }],
  "citations": [{ "document_id": "…", "document_version": "…", "chunk_id": "…", "excerpt_locator": "Article 3", "relevance": 0.8 }],
  "rule_results": [{ "rule_id": "…", "outcome": "flag | pass | not_evaluable", "reason": "…" }],
  "confidence": "low | medium | high", "assumptions": ["…"], "limitations": ["…"],
  "usage": { "provider_units": 42, "input_tokens": 0, "output_tokens": 0, "model_version": "…" } }
```

Rules checked before settlement: `request_id` and `response_type` match; `NeedsUserEdit` only for ScenarioDraft (with a
`scenario_draft` passing the `fet3d.editor/1` shape validator), `Answered` only for knowledge answers; text fields are plain text
(no markup, recommendation at most 8000 characters, list items 500, at most 20 items); `Answered`/`NeedsUserEdit` need at least
one citation and every citation names a pinned `document_id` + `document_version` that is still eligible; `bim_anchors` only for
ScenarioDraft; `provider_units` is in the pinned quota unit and at most `max_units`.

Refusal: `400`/`422` with `{ "code": "AI_SOURCE_HASH_MISMATCH | AI_SOURCE_UNAVAILABLE | AI_REVISION_NOT_PINNED" }` names why the
pinned input could not be used; any other refusal is recorded as `AI_PROVIDER_REJECTED`. `401/403/404/429/503` count as not
delivered (retried); timeouts and other 5xx are unknown outcomes (reconciled, never re-sent).

## Index storage (#91)

- **Owner:** the backend owns the DDL (migration `20261014138000`); the AI service only writes rows.
- **`knowledge_chunks`:** exactly the v7 table (`id, source_id → knowledge_sources, chunk_index, content, locator, embedding vector,
  metadata, created_at`, unique `(source_id, chunk_index)`). It is created only where pgvector exists (Supabase: schema
  `extensions`). The embedding dimension and the ANN index stay open until the production embedding model is chosen. A chunk
  version is its knowledge source row (each row is one version with its own `source_hash`).
- **Role `fet3d_ai_indexer`** (NOLOGIN): the AI login is made a member of it. It may `SELECT/INSERT/UPDATE/DELETE` `knowledge_chunks`
  (RLS policy for this role only), `USAGE` on the vector schema, and `SELECT` on `ai_source_states`. It cannot read tenant tables, and no
  service-role key is used. `fire3d_api` and `fet3d_backend_executor` have no access to chunks.
- **`ai_source_states`** (view: `kind, id, version_id, organization_id, visibility, content_hash, source_uri, eligible, status`):
  the indexer drops chunks of sources that are no longer `eligible` (retired, expired, unpublished). This is housekeeping only:
  retrieval for a request must be limited to the `document_id`/`document_version` pins of that request, which already exclude
  retired, deleted and foreign-tenant sources.

## Endpoints

| Method | Route | Auth | Notes |
|---|---|---|---|
| GET | `/api/ai/organization/sources` | OrganizationUser | Eligible sources now, with links. |
| GET | `/api/ai/organization/limits` | OrganizationUser | estimatedReserve and balance per operation. |
| POST | `/api/ai/organization/scenario-draft` | OrganizationUser | `{buildingId, prompt, sources?, revisionId?}` + Idempotency-Key → `202`. |
| POST | `/api/ai/organization/answer` | OrganizationUser | `{question, sources?}` + Idempotency-Key → `202`. |
| GET | `/api/ai/requests?status=&resultType=&operation=&mine=&page=&pageSize=` | OrganizationUser | Own organization only, newest first. |
| GET | `/api/ai/requests/{requestId}` | OrganizationUser | Same organization only. |
| GET | `/api/ai/trainee/quota` | Trainee | Today's allowance. |
| POST | `/api/ai/trainee/answer` | Trainee | `{question, context?, sources?}` + Idempotency-Key → `202`. |
| GET | `/api/ai/trainee/requests?status=&page=&pageSize=` | Trainee | Own requests only. |
| GET | `/api/ai/trainee/requests/{requestId}` | Trainee | Own request only. |
| GET | `/api/admin/ai/requests?status=&operation=&audience=&organizationId=&page=&pageSize=` | PlatformAdmin | Includes allocations, bim and `providerResult`. |
| GET | `/api/admin/ai/requests/{requestId}` | PlatformAdmin | |
| POST | `/api/admin/ai/requests/{requestId}/reconcile` | PlatformAdmin | Idempotency-Key; NeedsReconcile only → `202`. |
| GET | `/api/admin/ai/policies?operation=&page=&pageSize=` | PlatformAdmin | Paginated. |
| POST | `/api/admin/ai/policies` | PlatformAdmin | Idempotency-Key → `201`; `dailyUnits` for TraineeAnswer; `quotaUnit` tokens/requests. |
| POST | `/api/admin/ai/policies/{id}/end` | PlatformAdmin | `{effectiveUntil?}` + Idempotency-Key → `200`; sets only `effectiveUntil`, or cancels a policy that has not started. |
| GET/POST | `/api/admin/knowledge-sources` | PlatformAdmin | POST + Idempotency-Key → `201` Draft. |
| GET | `/api/admin/knowledge-sources/{id}` | PlatformAdmin | `ETag` = revision. |
| POST | `/api/admin/knowledge-sources/{id}/approve\|retire` | PlatformAdmin | If-Match. |

Every mutation recomputes the actor and family from the JWT inside the gate. Idempotency receipts are keyed by actor,
operation and key, and hash the canonical input (not the family).

## Not covered yet

Acceptance used a fake in-process FastAPI service only. The real provider/model, retrieval quality, the embedding model and
dimension, indexing and production prompts need their own acceptance (#92). Organization knowledge sources without `sourceUri`
have no document the service can fetch yet.

## Tests

- `OrganizationAiPostgresTests`: policy pinning, reservation, replay, cross-tenant reads, revoked family, last-quota race, unknown
  outcome → reconcile lookup, crashed lease, lookup 404, foreign citation, usage beyond reservation, markup, safety gate, retries,
  HTTP 503/422 (`errors`), limits.
- `AiContractV2Tests`: BIM pin and draft anchoring (`AI_BIM_ANCHOR_INVALID`, wrong response type); Trainee daily quota, replay,
  Common-only sources, Learn/Training/Result contexts, learner-safe snapshot, access loss → `resultWithheld`, Assessment block,
  Trainee HTTP routes; `knowledge_chunks` and `fet3d_ai_indexer` privileges.
- `OrganizationAiRequestListTests`: tenant list with v7 filters.
