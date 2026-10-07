# Personal phone uniqueness

`users.phone_number` is optional and canonical unique across all users, including inactive and soft-deleted accounts. Multiple `NULL` values are allowed. Organization phone retains its separate uniqueness in `organizations.phone`; no cross-user/organization index is added.

Keep existing normalization: trim boundary whitespace, remove ASCII spaces/hyphens/parentheses, retain a leading `+` and 6–15 ASCII digits. Do not infer country codes: `090…` and `+8490…` remain different. No OTP, Google proof, username, password or login semantics change.

Email Trainee/OrganizationUser registration (including `/api/auth/register` alias), Google completion of either role and `PATCH /api/auth/me` return `409 PHONE_NUMBER_EXISTS`, `errors.phoneNumber`, `traceId` when a different user owns the canonical value. Only SQLSTATE23505 and index `users_phone_normalized_key` map to this code; email/username/slug/organization-phone conflicts keep their existing codes. Registration conflict rolls back account/organization/session/audit/proof consumption. Correct the phone and retry an unexpired proof. Successful Google completion replay keeps its existing AlreadyCompleted semantics.

PATCH can keep its own number, omit the field to preserve it, or send null to clear it. Conflict leaves revision/audit unchanged; missing/malformed/stale If-Match remain428/400/412. Removing a phone releases that personal number; inactive/deleted accounts still reserve any non-null number.

## Migration and rollout

1. Deploy binary containing conflict mapping before enabling the index when scheduling a rollout; an older binary can surface a unique violation as500. Database enforcement begins as soon as the index is applied, regardless of old binary behavior.
2. Run [masked read-only preflight](../database/007_personal_phone_preflight.sql) as an authorized identity that sees all users. Duplicates or invalid legacy values block migration. Do not automatically merge/delete accounts or rewrite phone representation.
3. Apply additive `20261007160000_AddPersonalPhoneUniqueness`; it repeats preflight under a table write lock and creates `users_phone_normalized_key` in the same transaction. It preserves existing data and permits multiple NULLs. Authorized data remediation is a separate operation, with profile revision/audit.
4. Verify migration history, unique/valid/ready index, runtime HTTP409/field errors and proof retry on deployment. Docker/source tests do not prove deployed binary behavior.

## Manual tests

- Register A with `phoneNumber: "0706364866"`; use a different email/proof for B with `"(070) 636-4866"`. Expect409 PHONE_NUMBER_EXISTS; retry B with another number using the same proof. Use a phone not already claimed in the test environment.
- Repeat email→Google, Google→email and both account types; use different organizationPhoneNumber values to isolate personal conflict.
- GET `/api/auth/me` for A; PATCH with its ETag and the same canonical number succeeds. PATCH a different user's number returns409 without changing ETag/audit. PATCH null clears the optional field; omitted preserves it.
- Two concurrent registrations for one number have at most one success; the loser gets409, not500, and can retry its proof. Incorrect format still400; multiple absent phones remain valid.
- The same string in a user's personal phone and an organization's phone is allowed. `0…`/`+84…` remain distinct.

## Supabase evidence — 2026-10-07

Read-only preflight found one duplicate personal canonical phone across two users, with no invalid values. The user explicitly authorized clearing just those two phone fields. A separate transaction set them toNULL, incremented profile revisions and wrote two system audits; credentials, accounts and sessions were preserved. No identifiers or raw phone values are included in this public report.

Fresh preflight was empty. EF-generated additive migration `20261007160000_AddPersonalPhoneUniqueness` was applied in one guarded transaction from the verified Redis recovery baseline. The migration preserved every user/organization representation after that authorized remediation. A fresh read-only connection confirmed both personal and organization indexes unique/valid/ready, migration history and counts11users/6organizations/2Buildings. Organization phone index was already enabled and was not recreated.

HTTP409 requires deployment of the new binary; an older deployment can still return500 for database uniqueness violations. Supabase enforcement alone is not an HTTP/FE acceptance result. No real registration, email, Google or payment provider calls were made for this verification.

## Verification — 2026-10-07

Final Auth regression after the fixture correction and Docker restart:458passed/0failed/0skipped. It covers personal and organization phone cross-flow conflicts, canonical/null/lifecycle, profile ETag, proof rollback/retry, concurrent registration, actual migration history and restricted runtime/nonsuperuser migration roles, plus OpenAPI. Solution build:0warnings/0errors.

An earlier full run had454passed/4failed: the new history fixture omitted required Trainee usernames and was corrected; the three existing PayOS/password-reset failures passed individually and in the final full run. Another full run was interrupted before producing final results. No PayOS/password-reset implementation or assertions were changed; keep those intermittent observations for follow-up rather than claim their cause is resolved. The final result is source/container evidence, not deployed HTTP/FE or real provider acceptance.

## Edit verification and FE diagnosis — 2026-10-07

Additional HTTP/PostgreSQL tests exercise both PATCH routes with both indexes enabled and a restricted runtime role: existing canonical phone held by an active/inactive/deleted profile rejects409, own phone is accepted, two profiles racing for a free phone have one winner/one409, and two edits of one profile with one ETag have one winner/one412. A rejected request preserves the complete row, ETag and audit, including other fields sent in the edit. Audit failure rolls back the edit and keeps the old number reserved; it becomes available only after a successful commit. Email/Google registration racing with PATCH has one winner; the loser retains its unchanged profile/ETag or can retry an unconsumed proof. These five tests passed; they did not mutate shared accounts. Related phone/profile/OpenAPI regression:38passed/0failed/0skipped; this is a targeted run, not a new full Auth total.

Read-only Supabase check found both indexes unique/valid/ready and zero duplicate canonical groups in each scope. The public Azure OpenAPI at the checked host describes the organization phone conflict but lacks the new personal phone conflict description. This is a deployment-contract observation, not proof of the deployed build version or the database used by that binary. The root local checkout also still has the older source; task worktree/branch contains the new mapping.

For an FE report of a successful duplicate edit, capture the actual PATCH URL, field `phoneNumber`, HTTP status/body and follow it with GET using the same account. Do not treat optimistic UI text as persisted data;200 must be reconciled with the returned profile and subsequent GET. Check whether the two values are in the same scope: personal and organization uniqueness are separate, and `0…`/`+84…` remain distinct by policy. If the same canonical value persisted twice in the same scope, verify that the API's database is the inspected Supabase project and that its schema has the index. An older binary can expose500 instead of409, but it cannot bypass an active unique index.
