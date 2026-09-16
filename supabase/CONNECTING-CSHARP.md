# C# / Npgsql connection to Supabase

ASP.NET Core WebApplication.CreateBuilder already loads appsettings, development User Secrets and environment variables. EF Core/Npgsql is installed; do not execute a package command containing the literal YOUR_DOTNET_VERSION.

## Configuration

ConnectionStrings:DefaultConnection remains the local database. Store ConnectionStrings:Supabase in API User Secrets or a deployment secret manager. The supabase launch profile sets Database__ConnectionName=Supabase. Existing http/https profiles use DefaultConnection.

Example only (replace fields privately, never commit the password):

```json
{
  "ConnectionStrings:Supabase": "Host=<session-pooler-host>;Port=5432;Database=postgres;Username=<role>.<project-ref>;Password=<secret>;SSL Mode=VerifyFull;Maximum Pool Size=10;Timeout=10;Command Timeout=30;Include Error Detail=false"
}
```

Use the exact host/port/username from Supabase Connect, not a guessed region. VerifyFull checks the server certificate and hostname; install/configure the Supabase CA certificate if your platform needs it. SSL Mode=Require encrypts but does not provide that verification in current Npgsql. The supplied local development connection was stored with Require; no validation bypass callback is added in C#.

The supplied owner login is for initial connection/schema diagnostics. After migration, configure the restricted backend login from provision-backend.sql for regular API operation. Keep the signing key for each deployed environment separate.

## Read-only check

From this worktree's BE root:

```powershell
dotnet run --project Fire3D/Fire3D.API --launch-profile supabase -- --check-database true
```

The checker opens a real Npgsql connection, reads PostgreSQL version, configured client SSL mode, server-side hop TLS status (which can differ behind a pooler), and auth schema prerequisites. It never creates tables, runs migrations or reads account data. Full object/grant validation is in supabase/verify.sql. Diagnostic output excludes passwords/connection strings.

Process result: 0 prerequisites present; 2 schema missing; 3 connection/query failure; 4 connection name not configured. The dotnet run wrapper may report a generic failure; direct application execution exposes the process code.

## Run API

```powershell
dotnet run --project Fire3D/Fire3D.API --launch-profile supabase
```

Swagger for this profile: http://localhost:5183/swagger/index.html. Start it after the connection probe and migration verification succeed. Swagger alone is not evidence of database connectivity.

Normal local API remains on http://localhost:5173/swagger/index.html in the original checkout. The Supabase profile uses the restricted runtime login; migration credentials are retained separately as ConnectionStrings:SupabaseMigration in User Secrets.

## Current verification

- Build passed with 0 warnings/errors.
- Session pooler connection verified against hosted PostgreSQL 17.6 using the restricted runtime login.
- Baseline applied atomically to the initially empty hosted public schema; verify.sql passed. Runtime login has no superuser, role creation, database creation or RLS bypass privileges.

References: https://supabase.com/docs/guides/database/connecting-to-postgres and https://www.npgsql.org/doc/security.html