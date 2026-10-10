# Billing v7 rollout

Task branch: `feature/publish-billing-v7-reporting`. These notes describe source changes; they do not establish Azure/Supabase/provider acceptance.

## Catalog and immutable quota policies

New or updated service packages use commercial version 7: `durationMonths` is 6 or 12, `unitPrice` is a monthly whole-VND price, `learnerLimit` is positive and `aiQuotaUnits` is supplied and non-negative. A positive quota requires `aiPolicyVersionId`. No business price/capacity/quota is seeded.

Admin creates policy versions with `POST /api/admin/billing/quota-policies`, and reads the paged collection or `/{id}`. Body:

```json
{
  "quotaUnit": "tokens",
  "effectiveFrom": "2026-10-08T00:00:00Z",
  "effectiveUntil": null
}
```

The unit is an explicit business choice; the example does not prescribe a quota size. Audience `organization`, kind `quota` and rollover `None` are server-controlled. A policy is immutable; changes create a new version. The additive commercial policy table is `billing_quota_policy_versions`; consumption/reservation of AI usage is outside this rollout.

```json
{
  "code": "YOUR_CODE",
  "name": "Your package",
  "unitPrice": 100000,
  "durationMonths": 6,
  "learnerLimit": 20,
  "aiQuotaUnits": 0,
  "aiPolicyVersionId": null,
  "isActive": true
}
```

Package response exposes `commercialVersion`, capacity/quota/policy, `pricingBasis: Monthly` and `isPurchasable`. PATCH requires the current ETag in If-Match and a complete editable body. Missing capacity/quota, invalid durations and missing/ineffective policies are field validation errors. Updates do not alter issued quotations or historical entitlements.

Migration `20261008090000_AddBillingV7Catalog` retains legacy rows unchanged with version 1 and nullable v7 fields. Legacy packages remain readable with `isPurchasable=false`; configure all required fields to sell as v7. SQL constraints protect v7 configuration, and a trigger prevents policy update/delete. API has SELECT/INSERT on policies; browser database roles receive no access.

Rollout schema before the compatible binary. Do not enable v7 sales until quotation and provisioning tasks are accepted on all worker instances. The six additive migrations were applied to Supabase on 2026-10-08; schema verification is recorded below. This does not establish binary or provider acceptance.

## Quotation snapshots and fixed service intervals

New Draft quotations only select complete v7 packages. The monthly price is multiplied by 6/12; discount/tax follow the existing whole-VND rounding contract. Lines expose package revision, learner limit, quota amount/unit/policy and service interval.

Issue receives tax/terms/payment expiry plus `items`, one entry for each quotation item:

```json
{
  "taxAmount": 0,
  "terms": "Your approved commercial terms",
  "validUntil": "2026-10-10T00:00:00Z",
  "items": [
    { "quotationItemId": "<line-id>", "startsAt": "2026-10-11T00:00:00Z" }
  ]
}
```

Use future dates when testing. New/expired Renewal require a future start; ongoing Renewal derives the last committed period end (omit startsAt or supply that exact timestamp). End is UTC calendar AddMonths, using a half-open interval. Payment expiry cannot exceed the earliest start. A quota policy must cover the entire line interval. Quota expires with service and does not roll over.

Issue reprices and freezes the snapshot under the quotation lock. Catalog edits afterwards do not alter it. Create, PATCH, Issue and Accept recheck the JWT session family under the same lifecycle/user lock used by auth; clients never send a family ID. PATCH/Issue/Accept preserve 428/400/412 ETag errors. Upgrade remains unsupported. Legacy quotations stay readable but are not converted to v7 by reading or accepting them.

`20261008091000_AddBillingV7QuotationSnapshots` adds nullable snapshot/period fields, defaults historical rows to version 1 and protects complete issued v7 lines with a database gate. It does not change legacy dates or amounts.

## Checkout reservations and provisioning

New checkout requires a complete v7 snapshot. Existing legacy operations retain their original amounts and provisioning path. The server reserves each fixed Building interval in PostgreSQL before calling PayOS. An exclusion constraint blocks overlapping Reserved/Consumed intervals; Building locks also check already committed entitlements. Competing quotations do not silently move their dates.

Provider timeout, lost response or local link TTL does not release the reservation. Only a provider-confirmed Cancelled/Expired unpaid result releases it. Paid consumes the reservation together with successful provisioning. A late payment still records Paid/Applied; an expired/missing reservation or unavailable service interval leaves NeedsReconcile. Neither automatic date shifting nor automatic refund is implemented.

Provisioning uses the issued price/terms/period/capacity/quota snapshot. Entitlement and an optional quota grant commit with per-line status and audit. Positive grants have stable payment/item provenance, the same service interval, explicit policy/unit and no rollover. Zero quota creates no grant. Grants from multiple Buildings form the organization pool while retaining each grant's own expiry; AI reserve/consume is outside scope.

Replay returns existing provenance before expiry checks and does not grant again. Existing legacy operations preserve nullable seats/quota. Runtime API and request/webhook executor roles cannot directly insert grants or reservations; approved SQL gates own the financial writes. Workers must all run the v7 gates before new v7 sales are enabled.

`20261008092000_AddBillingV7Provisioning` adds reservations, grants and entitlement capacity, and upgrades provisioning gates. Preflight rejects overlapping paid legacy periods without rewriting them. The migration requires the PostgreSQL `btree_gist` extension; review permissions and extension availability before deployment.
# Publish rollout

`Publishing__Enabled=false` is the default. Apply `AddReleasePublishGate` before enabling it on every API instance. `POST /api/releases/{releaseId}/publish` requires Idempotency-Key and returns 200 ReleaseResponse only after checking the live session, actor/tenant lifecycle, active matching Training, current paid Building entitlement, exact content/rubric approval, revision/version validation and accepted package/manifest provenance. Trial cannot publish. Valid legacy paid entitlements remain eligible without invented seats/quota.

Missing business prerequisites return 409 (`BUILDING_ENTITLEMENT_REQUIRED`, `CONTENT_APPROVAL_REQUIRED`, `RELEASE_READINESS_REQUIRED`, `PACKAGE_COMPATIBILITY_REQUIRED`, `TRAINING_INACTIVE`, `RELEASE_NOT_BUILT`). Disabled rollout returns 503 `PUBLISH_GATE_UNAVAILABLE`. An authorized replay of an already Published release returns 200 ReleaseResponse with the original receipt and no new audit; receipt replay does not republish. A fresh operation on Revoked is rejected. See [publish/review contract](publish-review-manual-test.md) for QA/artifact codes and forward migrations. Publication, receipt and audit commit together. No Unity/S3/provider call occurs in this transaction; publish does not certify a real Unity build or grant a learner seat.

## Supabase schema verification — 2026-10-08

At the user's request, applied these migrations in one reviewed transaction after a read-only preflight:

| Migration | Schema change |
|---|---|
| `20261008090000_AddBillingV7Catalog` | Immutable quota policy versions and conditional v7 package capacity/quota constraints. |
| `20261008090100_SyncBillingV7CatalogModel` | EF model/history synchronization; no data change. |
| `20261008091000_AddBillingV7QuotationSnapshots` | Commercial snapshots and fixed service intervals on quotation lines. |
| `20261008092000_AddBillingV7Provisioning` | Non-overlapping service reservations, immutable quota grants, entitlement capacity and restricted provisioning gates; `btree_gist`. |
| `20261008093000_AddReleasePublishGate` | Restricted transactional publication gate. |
| `20261008094000_AddReportingReadPermissions` | Trusted backend SELECT policies for the reporting queries. |

History advanced from `20261007160000_AddPersonalPhoneUniqueness` (51 entries) to `20261008094000_AddReportingReadPermissions` (57 entries). Preflight found no overlapping paid service history. A separate read-only postcheck confirmed validated constraints, the reservation exclusion constraint, eight reporting SELECT policies, retained gate owners and restored temporary role/schema permissions. API/request/webhook roles have no direct INSERT into the ledger, quota grants or reservations; API also has no direct UPDATE on those tables.

Before/after counts agree: users 12, organizations 7, Buildings 2, packages 2, quotations 1, entitlements 0 and payment transactions 0. Both packages remain legacy version 1; policy/grant/reservation tables are empty. No commercial values, dates or application records were seeded, rewritten or removed.

Only schema deployment is verified. The compatible API/worker binary, real PayOS/webhook, frontend and Unity acceptance remain pending. `Publishing:Enabled` remains false by default; no Azure feature flag was changed. Deploy every compatible API/worker instance before enabling v7 sales or publication, and use the [manual acceptance guide](publish-billing-v7-manual-test.md).
# Mutation session checks

Package/discount create and PATCH, quota-policy create and enterprise requests
derive family from JWT and recheck account/organization/session under lifecycle
and actor locks. A revoked/expired family returns401 with no mutation, receipt or
audit. GET contracts, ETags and immutable quotation snapshots are unchanged.

## Upgrade, AI top-up, quota ledger reads, enterprise and expiry reminders — 2026-10-11

Migration `20261011110000_AddBillingUpgradeTopUp` (plus EF snapshot `20261011110100_SyncBillingUpgradeTopUpModel`) is additive. Preflight stops on unknown purchase actions or billing purposes. No price, seat count, quota amount or policy is seeded.

### Upgrade (same entitlement, period and consumed seats)

- Draft: `POST /api/billing/quotations` with lines `{ buildingId, servicePackageId, purchaseAction: "Upgrade", upgradeEntitlementId }`. The entitlement must be the Building's current paid v7 entitlement. The target package's learner limit must exceed the current limit (`409 UPGRADE_LIMIT_NOT_HIGHER`); a missing or foreign entitlement returns `409 UPGRADE_ENTITLEMENT_INVALID`. Upgrade lines are not mixed with New/Renewal lines. The draft line has `pricingBasis: "OneTime"`, total 0, and pins `upgradeBaseCapacityRevision` and `upgradePreviousLearnerLimit`; repricing at Issue re-pins them.
- Issue (`POST /api/admin/quotations/{id}/issue`): each Upgrade item takes `startsAt` (effective time: future, not before `validUntil`, before the entitlement end), `learnerLimit` (must exceed the previous limit), `oneTimePrice` (positive whole VND, fixed by Admin — no prorata from the monthly price) and optional `additionalQuotaUnits` with `aiPolicyVersionId` covering the effective time through the entitlement end. The line `endsAt` is the entitlement end; standalone quota purchases use top-up. Discount rules never apply to one-time lines.
- Checkout reserves the capacity baseline: a unique `(entitlement, base capacity revision)` reservation means two checkouts on the same baseline cannot both proceed (`409 PAYOS_UPGRADE_RESERVED`; a changed baseline returns `409 PAYOS_UPGRADE_BASELINE_CHANGED`). Provider-confirmed cancellation releases it.
- Provisioning writes an immutable `billing_entitlement_upgrades` row (capacity revision n+1, previous/new limit, effective time, payment and line provenance) and, for bundled quota, an `Upgrade` grant from the effective time to the entitlement end. The entitlement row, `endsAt` and seats are unchanged. A payment arriving after the baseline changed or the entitlement ended stays Paid/Applied and the line becomes `NeedsReconcile` (`PAYOS_UPGRADE_NEEDS_RECONCILE`); nothing is applied to a moved baseline. Replays never apply twice.
- `billing_effective_learner_limit(entitlement, at)` returns the limit of the latest upgrade effective at that time, otherwise the base limit. Renewal still opens a new entitlement and seat period.

### AI quota top-up

- Draft: `POST /api/billing/quotations` with `{ "purpose": "AIQuotaTopUp", "topUp": { "requestedQuotaUnits": 500 } }` (no Building lines). PATCH keeps the purpose.
- Issue: `topUp: { policyVersionId, quotaUnits, amount, startsAt, endsAt }` plus tax/terms/validUntil and no `items`. The organization quota policy must cover the whole interval (`409 BILLING_POLICY_INTERVAL_INVALID`); `startsAt` is not before the payment deadline. Unit comes from the policy.
- Checkout accepts Accepted AIQuotaTopUp quotations; there is no service reservation. Provisioning creates only a `TopUp` grant (`entitlement_id` null) with payment/line provenance. Building entitlements and periods never change. Duplicate webhooks and recovery replays return the existing grant.

### Grants and ledger

`billing_ai_quota_grants` gains `source_kind` (`BuildingService` | `Upgrade` | `TopUp`) with per-source provenance checks; existing rows are `BuildingService`. `billing_ai_quota_allocations` (Reserved/Settled/Released per request and grant) is the reserve/settle/release ledger written by the AI accounting gates; `billing_learner_seats` stores one seat per Trainee per entitlement period. Runtime API has SELECT only; ledger tables have no direct runtime DML.

### Read APIs

| Method | Route | Notes |
|---|---|---|
| GET | `/api/buildings/{id}/service-entitlement` | Owner tenant or Admin. `current` and `upcoming` paid entitlements: period, `isEffective`, base and effective learner limit at asOf (or at start for upcoming), `capacityRevision`, `seatsUsed`, `seatsRemaining`, upgrades. |
| GET | `/api/organizations/me/ai-quota` | Per unit: `granted`, `reserved`, `consumed` (active grants at `asOf`), `expired` (unused units of ended grants), `available = granted - reserved - consumed`, `scheduled`. Units never combine. |
| GET | `/api/organizations/me/ai-quota/grants` | Paged grants with source kind, interval, status (Scheduled/Active/Expired), reserved/consumed/available. |
| GET | `/api/organizations/me/ai-usage` | Paged allocations (request, grant, reserved/consumed units, status). |
| GET | `/api/admin/organizations/{organizationId}/ai-quota`, `/ai-quota/grants`, `/ai-usage` | PlatformAdmin equivalents. |

Each read runs in one RepeatableRead snapshot with a single database `asOf`.

### Enterprise requests

- `GET /api/admin/enterprise-quote-requests/{id}` returns the request with `revision`, `quotationId` and an ETag.
- `PATCH /api/admin/enterprise-quote-requests/{id}` with `{ "status": "Contacted" | "Rejected" | "Cancelled" }` and If-Match (428/400/412). Contacted only from New; Rejected/Cancelled from New, Contacted or Quoted.
- `POST /api/admin/enterprise-quote-requests/{id}/quotations` (Idempotency-Key) creates a Draft quotation for the requesting OrganizationUser with the given Building lines and marks the request Quoted. It never creates a payment or entitlement; the tenant still reviews, Admin issues and the tenant accepts and pays.

### Expiry reminders

`BillingReminders:Enabled` (default false), `LeadDays` (5), `PollSeconds` (300), `MaxAttempts` (5). Each cycle inserts, in one transaction, one notification per active OrganizationUser recipient for paid entitlements ending within the lead window without a committed later period, keyed `expiring:<entitlement>:<recipient>`, with a Web delivery (immediately Sent) and an Email delivery (Pending). The delivery rows are the transactional outbox. Dispatch rechecks for a renewal before sending (suppressed as `SUPPRESSED_RENEWED`), sends through the email adapter outside any transaction and records attempts. Email is at-least-once: a crash between sending and recording may resend. Recipients and bodies are never logged.

### Privileges and evidence

The ledger owner owns the gates and writes upgrades, reservations and grants; the row lock on an upgraded entitlement uses `UPDATE(id)` only. New tables referencing users or organizations grant SELECT plus a policy to the pending-registration cleanup owner, which fails closed on unreadable references. The composite FK from provisioning records to Building lines is replaced by a purpose-aware insert trigger, because top-up records reference the top-up line. Tests: disposable PostgreSQL through the fake PayOS provider and real gates (upgrade once with bundled quota, baseline race and late payment, validation failures, top-up once with duplicate webhook and no service change, per-unit balance, enterprise ETag/quotation, reminder uniqueness and suppression). Supabase, PayOS, Mailgun and frontend acceptance remain pending.
