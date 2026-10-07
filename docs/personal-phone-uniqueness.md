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
