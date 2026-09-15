# Authentication — Fire3D

## Scope and product decisions

This implementation follows Docs v6 FR-AUTH-01/02 and BR-01/02/07:

- PlatformAdmin provisions accounts. There is no public registration endpoint or client-selected role during login.
- An account has one immutable role/organization binding. Only OrganizationUser has an organization; Trainee and PlatformAdmin have none.
- Accounts and organizations must be active and not soft-deleted. Their status is checked at login, refresh and authenticated requests.
- User creation and successful login/logout produce audit records without passwords or tokens.
- Existing SQL protects role/organization immutability. This module does not replace those triggers.

The baseline did not define token lifetimes, password policy or refresh storage. This implementation chooses configurable 60-minute access tokens, 7-day absolute refresh sessions, and 12–128 character passwords. Refresh token rotation does not extend the absolute expiry. Refresh-token replay revokes the entire login session; a separate login on another device remains valid. Clients must serialize refresh attempts and sign in again after a lost rotation response/replay rejection.

Organization administration and account-disable endpoints are implemented in [administration.md](administration.md), with a [Swagger test walkthrough](testing-authorization.md). Device-registration endpoints, password reset/change, email verification, SSO and Unity launch grants remain separate use cases. Existing database account/organization disable flags are enforced here. A mobile shell may hold refresh credentials in OS secure storage; never give them to Unity. A web frontend should use a server-side/BFF session with secure HttpOnly cookies, not persistent browser localStorage for the returned refresh token.

## Clean Architecture

| Project | Responsibility |
| --- | --- |
| Domain | Existing User/enums and the new RefreshToken entity; no EF/JWT references |
| Application | MediatR commands/queries and handlers, request/response contracts and storage/password/token interfaces; no ASP.NET/JWT dependency |
| Infrastructure | EF storage/transactions, hash implementation and JWT issuance |
| API | HTTP controllers, bearer validation, middleware, Swagger and DI composition in Extensions |

API referencing Infrastructure to register implementations is intentional. Controllers use Application; they do not query DbContext or return database entities. SQL remains the physical schema source. Do not run EnsureCreated or generate an InitialCreate migration against v6.

HTTP flow: `Controller → ISender.Send(command/query) → IRequestHandler in Application → storage/password/token interfaces → Infrastructure`.

Commands: Login, RefreshToken, Logout, CreateAccount, BootstrapAdmin. Queries: GetCurrentAccount and ValidateSession. Each request and its handler live together under `Application/Authentication/Commands/<UseCase>` or `Queries/<UseCase>`. Handlers contain the use-case orchestration; `Internal/AuthSupport` shares account validation/provisioning and token-issuance helpers. The previous AuthService has been removed.

`API/Extensions/ApplicationExtensions.cs` registers MediatR by scanning the Application assembly; Program calls `AddApplication(builder.Configuration)`. Bearer session checks and the local bootstrap command also use ISender. Actor/session IDs come from validated claims or the local CLI, not from public account/login body fields. CancellationToken is forwarded through Send to persistence.

The currently restored MediatR version is 14.2.0. If your team has a license key, supply `MediatR:LicenseKey` via User Secrets or `MediatR__LicenseKey` through deployment configuration; do not commit it. Registration does not suppress the package's license diagnostics. See the [MediatR registration documentation](https://github.com/LuckyPennySoftware/MediatR#registering-with-iservicecollection).

## Setup in Visual Studio

1. In pgAdmin, select the existing v6 database and run `database/001_auth_refresh_tokens.sql` from this BE repository **once**. This is an additive change; do not run the old v6 bootstrap again. Apply with the schema owner. A restricted runtime login needs SELECT/INSERT/UPDATE on auth_refresh_tokens, SELECT on organizations, SELECT/INSERT/UPDATE on users, and INSERT on audit_logs in addition to any existing grants.
2. Open **View → Terminal**, working directory `BE/Fire3D`. Keep the existing `ConnectionStrings:DefaultConnection` in API User Secrets. Generate a signing key without printing it:

```powershell
$jwtKey = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set "Jwt:SigningKey" $jwtKey --project Fire3D.API | Out-Null
Remove-Variable jwtKey
```

`Jwt:Issuer`, `Jwt:Audience`, `Jwt:AccessTokenMinutes`, and `Jwt:RefreshTokenDays` have non-secret defaults in appsettings.json. Deployment supplies `Jwt__SigningKey` and `ConnectionStrings__DefaultConnection` through its secret store. Use the same signing key across API replicas. There is no hardcoded/fallback signing key.

3. Create the first PlatformAdmin through the explicit CLI mode. Store `BootstrapAdmin:Email` and `BootstrapAdmin:Password` temporarily using **Fire3D.API → Manage User Secrets**. Use a new password of 12–128 characters. Add these keys to the existing JSON object; preserve the database and JWT settings.

```json
"BootstrapAdmin:Email": "admin@your-domain.example",
"BootstrapAdmin:Password": "YOUR_NEW_PASSWORD"
```

Run:

```powershell
dotnet run --project Fire3D.API --launch-profile https -- --bootstrap-admin true
dotnet user-secrets remove "BootstrapAdmin:Email" --project Fire3D.API
dotnet user-secrets remove "BootstrapAdmin:Password" --project Fire3D.API
```

The command exits without starting the HTTP server, refuses to run if any PlatformAdmin already exists, and serializes concurrent bootstrap attempts. It does not reset or overwrite an existing administrator. If an existing account has an unrelated password-hash format, do not replace that hash with plaintext; password-format migration is a separate task.

4. Start the API using the `https` profile and open `/swagger`.

## Endpoints

| Method / path | Authorization | Purpose |
| --- | --- | --- |
| POST /api/auth/login | Anonymous | Email/password → access + refresh tokens |
| POST /api/auth/refresh | Refresh token in body | Rotate token and issue access token |
| POST /api/auth/logout | Bearer | Revoke the current login session |
| GET /api/auth/me | Bearer | Read only the authenticated account |
| POST /api/accounts | PlatformAdmin bearer | Create an account |

Login body:

```json
{"email":"admin@your-domain.example","password":"YOUR_PASSWORD"}
```

The response contains `accessToken`, `accessTokenExpiresAt`, `refreshToken`, `refreshTokenExpiresAt` and a safe `user` DTO. Token responses use Cache-Control: no-store. Paste only the access token into Swagger's **Authorize** field.

Create a Trainee:

```json
{
  "email": "trainee@your-domain.example",
  "password": "A_NEW_PASSWORD_OF_AT_LEAST_12_CHARACTERS",
  "fullName": "Trainee One",
  "role": "Trainee",
  "organizationId": null
}
```

For OrganizationUser, use `"role":"OrganizationUser"` and the UUID of an existing active organization. For PlatformAdmin, organizationId must be null. Roles are JSON strings; arbitrary numeric enum values are rejected.

Refresh body:

```json
{"refreshToken":"THE_LATEST_REFRESH_TOKEN"}
```

Replace both stored tokens after success. Never reuse the old refresh token. Logout has no body; send `Authorization: Bearer <accessToken>`. Logout also invalidates access tokens from that login session because bearer validation checks the session in PostgreSQL.

Known errors use ProblemDetails with a `code`: INVALID_CREDENTIALS / INVALID_REFRESH_TOKEN (401), FORBIDDEN (403), validation failures (400), EMAIL_EXISTS / ADMIN_EXISTS (409). Framework authentication/authorization failures use 401/403. Authentication requests are limited to 10/minute per observed IP per API process; this is an ASP.NET native limiter for the current single-instance setup. Multi-instance deployment needs a shared gateway/distributed limiter and explicit trusted-proxy configuration. Do not trust an arbitrary forwarded IP header.

## Storage and security behavior

- Passwords use Identity V3 salted PBKDF2-HMAC-SHA512 with 220,000 iterations; successful verification upgrades an older compatible hash.
- Refresh tokens contain 64 cryptographically random bytes; only SHA-256 hashes are stored. Token rows retain family IDs and consumed/revoked timestamps.
- User-scoped PostgreSQL transaction advisory locks serialize refresh/logout. Token consumption, successor creation and audit writes commit atomically where applicable. Replay revocation is committed even when the HTTP result is 401.
- Keep consumed refresh records until the whole family's absolute expiry, so replay remains detectable. Expired families may later be removed in a bounded maintenance job; none is scheduled automatically.
- JWT validation enforces signature, HS256, issuer, audience and expiry with a 30-second clock skew, and checks role/org claims against the current user. No token/secret is logged by this module.
- The additive auth schema is maintained in BE for this implementation. A later Docs sync should update bootstrap/ERD/dictionary; this task does not rewrite the shared v6 docs.

## Verification

```powershell
dotnet build Fire3D/Fire3D.slnx
```

Integration tests use real PostgreSQL and the actual v6 core SQL/triggers from the sibling Docs repository. They create and drop a random `fire3d_auth_test_*` database **per test** on localhost. They explicitly replace the TestHost DbContext configuration and assert its database name before provisioning any account. Phase 2 role provisioning is not exercised.

From BE, explicitly opt in to using the API's local connection credentials to create test databases (requires CREATEDB):

```powershell
$env:FIRE3D_TEST_USE_LOCAL_SECRETS = "1"
dotnet test Fire3D/Fire3D.AuthTests/Fire3D.AuthTests.csproj
Remove-Item Env:FIRE3D_TEST_USE_LOCAL_SECRETS
```

Alternatively supply `FIRE3D_TEST_ADMIN_CONNECTION` through the environment for a local test PostgreSQL server. `FIRE3D_TEST_DOCS` can point to an alternate local Docs directory. Without explicit test configuration, PostgreSQL tests are reported as skipped, not passed. Test Data Protection keys are ephemeral and do not use the Windows user's key ring.

References: [ASP.NET JWT validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0), [OWASP password storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html), [RFC 9700 refresh-token replay protection](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14).
