# Deployment readiness and smoke test (#64)

The deployment of `b6a7d74` (and later `973c29f`) answered `500` on Building detail/create, scenarios, billing and Learn reads while `/health` stayed `Healthy`. The common cause is a database that lags the code: the app starts, but the first query that touches a table or column from a missing migration fails.

## Endpoints

| Endpoint | Meaning |
| --- | --- |
| `GET /health` | Liveness only (process is up). It never checks the database, so a DB outage does not make the host restart the app. |
| `GET /health/ready` | Readiness: the database is reachable **and** every EF migration in this build is applied. `200 {"status":"Healthy"}` or `503` with `checks.database.code`. |
| `GET /health/version` | Commit of the running image. |

`/health/ready` codes:

| Code | Meaning | Action |
| --- | --- | --- |
| `OK` | Ready | — |
| `MIGRATIONS_PENDING` | `checks.database.data.pendingMigrations` lists the migration IDs missing from `__EFMigrationsHistory` | Apply them (see below), then re-check |
| `DATABASE_UNREACHABLE` | Connection failed | Check the connection string / network / pooler |
| `DATABASE_CHECK_FAILED` | The check threw; `data.error` is the exception type only | Read the app log for the same time |

The response never contains the connection string. The raw SQL recovery files (`002_password_reset_recovery.sql`, `003_local_password.sql`, `005_email_verification.sql`) are not EF migrations and are not covered by this check.

## Unhandled errors

An unexpected exception still returns ProblemDetails `500`, now with `code: "INTERNAL_ERROR"` and `traceId`. Search the app log by `traceId`; handler errors keep their own codes.

## CI smoke test

`.github/workflows/azure-deploy.yml` runs after the version check:

1. `GET /health/ready` must be `200`. Otherwise the job fails with the readiness body (pending migration IDs).
2. Anonymous reads that touch the database (`/api/learn/situations`, `/api/learn/posts`) and `/openapi/v1.json` must not return `5xx`.

A failed smoke test means the image is deployed but the database is behind: apply the pending migrations to the deployment database, then re-run the job.
