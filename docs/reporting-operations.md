# Audit and operations reporting

Audit routes require an active PlatformAdmin session. List supports actorId, organizationId, named action, targetEntity, targetId, correlationId and UTC `[from,to)`. Timestamps must include `Z` or an explicit offset. Defaults: last 30 days, page 1, pageSize 20; limits: 90 days, pageSize 100. Ordering is createdAt descending, then ID descending. Numeric action values are rejected.

Detail preserves metadata fields at the root and adds `changes`. Changes are selected by recognized entity/action and safe scalar fields, including status/publication, revisions, service periods, package/amount/capacity/quota and assignment. Unknown legacy records return empty changes. Raw old/new JSON, email/phone, password/hash/token, credentials, signed URLs, IP/user agent and ticket content are excluded. No historical audit is invented and no mutation API is added.

Manual check: login as Admin, list `/api/admin/audit-logs?targetEntity=Release&action=Publish`, open one ID and inspect safe status changes. A non-admin must receive 403. Missing timezone, numeric action or pageSize 101 must return 400.
