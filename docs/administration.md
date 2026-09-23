# Organization and account administration

Phase 1 scope: PlatformAdmin creates organizations/accounts, reads paginated lists/details and changes their active status. Building management is separate. Authentication setup is in [authentication.md](authentication.md).

Đây là mô tả các route quản trị hiện có. Tạo organization hiện còn gán `plan = "free"` theo mô hình code legacy; đây không phải entitlement thương mại theo Docs. Gói, quotation, payment và entitlement theo từng Building trong [Docs](../../Docs/fire-evacuation-training-technology.md) là contract đích, chưa được trang này tuyên bố đã triển khai.

## Endpoints

All endpoints require a valid PlatformAdmin bearer token. OrganizationUser and Trainee receive 403; missing/invalid credentials receive 401.

Authorization is centralized in API/Authorization/AuthorizationPolicies.cs. Controllers require authentication by default, and administration uses the PlatformAdministration policy. Application handlers independently check the current actor. See [the Swagger authorization walkthrough](testing-authorization.md) for the role matrix and manual tests.

| Method | Route | Purpose |
|---|---|---|
| POST | /api/organizations | Create an active organization |
| GET | /api/organizations | Paginated organizations |
| GET | /api/organizations/{id} | Organization details |
| PATCH | /api/organizations/{id}/status | Activate/deactivate organization |
| POST | /api/accounts | Existing account provisioning |
| GET | /api/accounts | Paginated accounts |
| GET | /api/accounts/{id} | Account details |
| PATCH | /api/accounts/{id}/status | Activate/deactivate account |

Create an organization:

```json
{ "name": "FPT University", "slug": "fpt-university" }
```

Name is trimmed, required and limited to 200 characters. Slug is trimmed, lowercased, limited to 100 characters and accepts letters a–z, digits and single separating hyphens. Duplicate slugs return 409/SLUG_EXISTS, including a slug reserved by a deleted organization. New organizations use the existing database defaults for the Phase 1 plan (free) and empty metadata; no billing workflow is introduced.

Use the returned organization id to provision an OrganizationUser:

```json
{
  "email": "manager@example.com",
  "password": "<a unique password of 12–128 characters>",
  "fullName": "Organization manager",
  "role": "OrganizationUser",
  "organizationId": "<organization id>"
}
```

Only OrganizationUser has an organization. Trainee and PlatformAdmin use null. Role and organization cannot be reassigned; provision a separate account if a different role/scope is needed.

Both status endpoints accept:

```json
{ "isActive": false }
```

Use true to reactivate. Missing/null isActive is rejected with 400. Unknown/deleted records return 404 and cannot be restored through these endpoints.

## Lists and privacy

Both list endpoints accept page (default 1, allowed 1–100000), pageSize (default 20, allowed 1–100), search (max 200 characters) and isActive. Account lists also accept role and organizationId.

Examples:

- /api/organizations?page=1&pageSize=20&search=fpt&isActive=true
- /api/accounts?role=OrganizationUser&organizationId=<id>&isActive=true

Responses contain items, totalCount, page and pageSize; ordering is createdAt descending then id. Search matches name/slug for organizations and email/fullName for accounts. Deleted records are excluded. Count and items use separate reads; concurrent writes may change the count between them.

Account DTOs never expose password hashes, refresh tokens or entity navigation properties. Account isActive is the account's own state: an active OrganizationUser is still blocked while their organization is inactive. Organization status changes preserve individual account status.

## Status, concurrency and audit

- Account deactivation revokes every refresh-token family for that account; old access tokens fail session validation too.
- Organization deactivation revokes the families of its OrganizationUsers. It does not revoke unrelated administrators or Trainees.
- Reactivation does not revive old tokens. The user must log in again.
- A PlatformAdmin cannot deactivate their own account (409/SELF_DEACTIVATION). Concurrent attempts by two admins to deactivate each other leave an active administrator.
- Status changes recheck the actor in a transaction and write status, revocation and audit atomically. Repeating the current status succeeds without another Update audit.
- Organization/account creation and status responses include a server-generated X-Correlation-ID matching their audit record when a change is made. Requests that repeat the current status do not create another audit. Audit scope identifies the target organization, and status old/new values contain no credentials.
- Management writes use an exclusive PostgreSQL advisory transaction lock; ordinary auth transactions use the matching shared lock before their per-user lock. This prevents a login/refresh/provision operation from bypassing an organization/account lock. Rare management writes briefly pause new auth transactions; this is a correctness-first choice for Phase 1.

Administrative endpoints have an in-process limit of 120 requests/IP/minute. Account creation retains the existing auth limit of 10/IP/minute shared with login/refresh/logout. These limits are configurable in API/Extensions/AuthenticationExtensions.cs; a multi-instance deployment needs a coordinated rate-limit strategy.

There are no schema changes for this module. The existing auth refresh-token SQL must already be applied for deactivation. No application database is modified by implementation or tests.

This module implements account access control. Future Training resolve/start handlers must also check organization/building status. Historical Training session grants have a separate policy; organization deactivation does not mean deleting historical results.

## Implementation and verification

API controllers dispatch MediatR commands/queries in Application/Administration. IAdministrationStore is implemented by Infrastructure/Administration/AdministrationStore; DI remains in API/Extensions/ApplicationExtensions.cs.

The existing opt-in PostgreSQL test fixture creates dedicated random localhost databases. Administration tests cover role denial, safe DTOs/filtering, invalid/duplicate slug, deleted resources, session revocation across deactivate/reactivate, self-deactivation, concurrent administrators and rollback on audit failure. Run with the commands in authentication.md.

Broader PlatformAdmin scope and implementation status: [platform-admin-policy.md](platform-admin-policy.md). Architecture and Week 2 evidence: [backend-foundation.md](backend-foundation.md).
