# Audit and operations reporting

Audit routes require an active PlatformAdmin session. List supports actorId, organizationId, named action, targetEntity, targetId, correlationId and UTC `[from,to)`. Timestamps must include `Z` or an explicit offset. Defaults: last 30 days, page 1, pageSize 20; limits: 90 days, pageSize 100. Ordering is createdAt descending, then ID descending. Numeric action values are rejected.

Detail preserves metadata fields at the root and adds `changes`. Changes are selected by recognized entity/action and safe scalar fields, including status/publication, revisions, service periods, package/amount/capacity/quota and assignment. Unknown legacy records return empty changes. Raw old/new JSON, email/phone, password/hash/token, credentials, signed URLs, IP/user agent and ticket content are excluded. No historical audit is invented and no mutation API is added.

Manual check: login as Admin, list `/api/admin/audit-logs?targetEntity=Release&action=Publish`, open one ID and inspect safe status changes. A non-admin must receive 403. Missing timezone, numeric action or pageSize 101 must return 400.

## Operations analytics contract

`GET /api/admin/analytics/operations` returns account counts by role/active status, organization and Building counts by active status, created counts, processing jobs by kind/status and tickets by status. `GET /api/organizations/me/analytics/operations` contains only tenant Building/processing and caller-created tickets; it has no account or organization count. `processingJobs` replaces the ambiguous old `ifcJobs` field because package builds are included. FE must update to these explicit DTOs.

Authorization, `asOf` and all aggregates use one read-only RepeatableRead transaction. Both routes require timezone-qualified from/to and UTC `[from,to)`, default last 30 days and maximum 90 days. Snapshot excludes soft-deleted records but includes inactive; created counts retain deleted historical records. Processing/ticket status distributions filter createdAt rather than execution/closed time. Empty known buckets return zero; a missing table/query failure remains an error. No PII lists, learner completion or revenue metrics are returned.

Manual check: compare Admin versus OrganizationUser responses, supply an offset/Z range and check boundary exclusion. Organization must never see another caller's tickets or account aggregate. The Docker concurrency test commits a lifecycle mutation between aggregates and verifies the response retains the initial snapshot.

`AddReportingReadPermissions` grants SELECT and backend-only read policies to the existing trusted `fire3d_api` role for reporting source tables. It grants no money/provenance DML and no browser-role access. Apply it before serving these readers with a restricted API connection; a superuser-only test is insufficient. Login audit writing continues to work with INSERT-only permission, independently from these authorized read views.
