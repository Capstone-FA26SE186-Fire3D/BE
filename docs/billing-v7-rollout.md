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
