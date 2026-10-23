# Feedback and support contract

Trainee/OrganizationUser POST/GET /api/feedback, POST/GET /api/support/tickets, GET /api/support/tickets/{id}, POST /api/support/tickets/{id}/messages operate on caller-owned resources. Organization membership never grants access to another creator's ticket. PlatformAdmin uses /api/admin/feedback and /api/admin/support/tickets routes for list/detail/message/PATCH. Live account, organization and session family are rechecked under lifecycle/resource locks.

Create feedback/ticket and message require Idempotency-Key (1-128 non-whitespace/control characters). Canonical input is trimmed plain text and includes operation/resource. Same actor/key/input returns the committed response without duplicate message/audit; another input returns 409 IDEMPOTENCY_KEY_CONFLICT. Replay still checks identity/ownership, but a committed message can replay after closing the ticket. New messages to Closed return 409 TICKET_CLOSED.

Ticket detail, ticket create/PATCH and feedback create/PATCH return ETag "support-{revision}". Admin PATCH requires If-Match: missing428, malformed400, stale412. Messages increment ticket revision. Feedback list items include revision for updating status. Ticket transitions: Open → InProgress → Resolved → Closed; admin may reopen Resolved/Closed to Open and clears resolvedAt. Feedback: Submitted → Reviewed → Closed. Assignee must be active PlatformAdmin.

List and detail query page=1&pageSize=20 by default; maximum100. Lists ordered createdAt descending then ID; message history ascending createdAt then ID. Detail returns messages as {items,total,page,pageSize}; list returns that same page envelope. Admin filters status/priority/assignedTo/organizationId; user scope remains creator-only. Feedback status filters use Submitted/Reviewed/Closed.

Feedback body: category 1-100, message1-10000, optional rating1-5/sessionId. Ticket: subject1-255, description1-10000, optional feedbackId/sessionId. Message: message1-10000. References must belong to creator and a live scope. Validation returns VALIDATION_ERROR/errors by field/traceId; no attachments, rich HTML or notification delivery.

## People and organization labels (#67)

Ticket and feedback responses carry `createdBy {id,displayName,available,email?}` and `organization {id,name,available}` (null when the resource has no organization). Messages keep `authorId` and add `author {id,displayName,available,email?}`. `email` appears only in PlatformAdmin responses (`/api/admin/...`); a Trainee or OrganizationUser never sees an email, including the staff email on admin replies. `displayName` is the current label (`full_name`, else `username`), not a snapshot. The organization is the one stored on the resource (from the session/feedback at creation), not the creator's current membership. A missing or deleted user/organization keeps its ID with `displayName`/`name` null and `available:false`; Admin can still open such tickets, but admin writes (message, ticket update, feedback status) on a resource of an inactive or deleted organization return `404 NOT_FOUND`.

`GET /api/admin/feedback/{id}` (PlatformAdmin) returns the feedback with labels and `ETag "support-{revision}"` for `PATCH /api/admin/feedback/{id}/status`.

Labels are joined once per page in the SQL gate (no per-row lookups, totals unchanged). Receipts store only the unlabelled representation; a replay first re-checks the caller and then adds labels for that caller, so an Admin email is never served from a receipt to a regular user. Migration: `20261012090000_AddSupportResponseLabels`.

## Ticket status counts (#153)

`GET /api/support/tickets` and `GET /api/admin/support/tickets` return `{items,total,page,pageSize,counts}` (`SupportTicketPage` in OpenAPI). `counts` is `{Open,InProgress,Resolved,Closed}`, the ticket status enum; every key is always present, `0` when there is none. Counts use the same scope and filters as the list except `status`, so the status chips keep their numbers while one is selected; `total` still follows the full filter including `status`.

- Trainee/OrganizationUser: only the caller's own tickets (creator scope, as for the list), filtered by `priority`/`assignedTo`. `organizationId` is ignored on this route.
- PlatformAdmin: every organization, or only `organizationId` when given, filtered by `priority`/`assignedTo`.

Example: `GET /api/support/tickets?status=Open` → `{"items":[…],"total":3,"page":1,"pageSize":20,"counts":{"Open":3,"InProgress":1,"Resolved":2,"Closed":0}}`. Feedback lists are unchanged (no `counts`). Migration: `20261023120000_AddSupportTicketStatusCounts` (CREATE OR REPLACE of `support_command_gate`; owner and grants unchanged).

## Storage

Migration 20261007110000_AddSupportCommandContracts adds revisions, receipts, paging indexes, constraints, append-only message protection and a restricted SQL gate. Preflight stops on invalid legacy content without changing it. Mutation/receipt/audit are atomic and audit tenant comes from the resource, including admin actions. API executor has gate EXECUTE, no direct mutation on support tables. Legacy history is preserved.

Test manually: create ticket with key support-001; repeat identical request and verify same ID. Change subject with that key →409. GET detail and copy ETag; admin PATCH InProgress with that header →200/new ETag; repeat old ETag →412. Add a message with a new key, repeat and verify only one message. Another user cannot read/message. Complete Resolved→Closed; new message fails, then reopen and retry. Test pageSize=101 →400. Current evidence: disposable PostgreSQL actual migration history/restricted-role HTTP/race/rollback tests; migration and client/deployment acceptance remain separate, no real email/push sent.
