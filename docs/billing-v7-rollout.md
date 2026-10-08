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

Rollout schema before the compatible binary. Do not enable v7 sales until quotation and provisioning tasks are accepted on all worker instances. The migration has not been applied to Supabase in this implementation phase.

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

`Publishing__Enabled=false` is the default. Apply `AddReleasePublishGate` before enabling it on every API instance. `POST /api/releases/{releaseId}/publish` returns 204 only after checking the live session, actor/tenant lifecycle, active matching Training, current paid Building entitlement, exact content/rubric approval, revision/version validation and accepted package/manifest provenance. Trial cannot publish. Valid legacy paid entitlements remain eligible without invented seats/quota.

Missing business prerequisites return 409 (`BUILDING_ENTITLEMENT_REQUIRED`, `CONTENT_APPROVAL_REQUIRED`, `RELEASE_READINESS_REQUIRED`, `PACKAGE_COMPATIBILITY_REQUIRED`, `TRAINING_INACTIVE`, `RELEASE_NOT_BUILT`). Disabled rollout returns 503 `PUBLISH_GATE_UNAVAILABLE`. An authorized replay of an already Published release returns 204 without another receipt/audit; Revoked cannot be republished. Publication, receipt and audit commit together. No Unity/S3/provider call occurs in this transaction; publish does not certify a real Unity build or grant a learner seat.
