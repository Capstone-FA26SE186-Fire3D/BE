# Learner, revenue and AI usage analytics

Real-data dashboards built on server-owned facts from #55–#58. The existing operations analytics
(`/api/admin/analytics/operations`, `/api/organizations/me/analytics/operations`) are unchanged.
Migration: `20261011150000_AddLearnerAnalytics` (`Persistence/Sql/LearnerAnalytics.sql`, read-only functions plus two indexes).

| Method | Route | Auth | Content |
|---|---|---|---|
| GET | `/api/organizations/me/analytics/training?from=&to=` | OrganizationUser | Training metrics for the caller's organization and its `aiUsage`. |
| GET | `/api/admin/analytics/training?from=&to=` | PlatformAdmin | Platform totals, `byOrganization` (top 100) and platform `aiUsage`. |
| GET | `/api/admin/analytics/revenue?from=&to=` | PlatformAdmin | Money received per currency, purpose and UTC day. |

The range is UTC `[from,to)` with timezone-qualified ISO timestamps. It defaults to the last 30 days and allows at most
90 days; otherwise `400 VALIDATION_ERROR`. The API opens one `READ ONLY` RepeatableRead transaction, reads `asOf`, and the
SECURITY DEFINER `analytics_gate` re-checks the actor, the live session family and the organization inside that snapshot.
Responses are `no-store`. Tenant filters are applied in the source rows before aggregation, never after.

## Metric definitions

| Metric | Source and rule |
|---|---|
| `plays` | Contract-7 learner sessions with `started_at` in range. Prepare (`training_session_preparations`) and playtests (own tables) are never counted. |
| `uniqueTrainees` | Distinct `trainee_user_id` of those sessions. |
| `activeSessions` | `Running` sessions whose last heartbeat was received by the server within `activeWindowSeconds` (default 120, `Analytics:ActiveSessionSeconds`, 10–3600) before `asOf`. Not range-bound. |
| `completed`, `completionRate` | Cohort sessions with a server-accepted result at `asOf`, divided by plays. A late offline result counts for the cohort of the session start. |
| `outcomes` | Server result outcomes `Passed`/`NotPassed`/`Incomplete`/`NotAssessed`, all keys always present. `assessment.passRate` = Passed / (Passed + NotPassed). |
| `duration` | `ended_at − launched_at` (server timestamps) of completed cohort sessions: count, average, median seconds. |
| `byMode`, `byTraining` | Same cohort split by Learn/Guided/Assessment and by Training (top 100 by plays). |
| `aiUsage` | `settledUnits` per unit with `settled_at` in range; `openReservations` (Reserved now) and `needsReconcile` (requests and reserved units now) reported separately; `requests` by status created in range. |
| Revenue | `Applied` payment transactions by `received_at`, summed per currency, also by quotation `billing_purpose` and UTC day; `payingOrganizations`. Quotation totals are never revenue. |

An empty dataset returns counts of 0. A rate or average without samples is `null`, not 0. Every response carries
`asOf`, `from`, `to`, `activeWindowSeconds` and the `definitions` text above.

## Tests

`Fire3D.AuthTests/LearnerAnalyticsPostgresTests.cs`: empty cohort, `[from,to)` boundaries, a result synced after the
range, stale versus fresh heartbeat, cross-tenant exclusion, platform breakdown, role/revoked-family/range errors, Applied
versus Rejected and out-of-range payments, settled versus reserved AI units, HTTP roles, range and `no-store`.
