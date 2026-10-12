# Checklist API — issues #52–#58 và #67–#69

Nguồn: OpenAPI dump của nhánh `feature/support-billing-catalog-contracts`, so với inventory tại `3c9ef1f` (trước #54). Chỉ liệt kê route **mới** hoặc **đổi contract**; route cũ không đổi xem trong `docs/api-route-inventory.md`.

## Quy ước chung

| Mục | Quy ước |
|---|---|
| Xác thực | `Authorization: Bearer <access token>`. Session family bị thu hồi → `401` (`SESSION_REVOKED` với Billing/PayOS/Enterprise). |
| Quyền | Route `/api/admin/**` chỉ dành cho PlatformAdmin. Sai role → `403`. Truy cập tài nguyên của tổ chức khác → `404` (không lộ là tài nguyên có tồn tại). |
| Idempotency | Lệnh tạo dùng header `Idempotency-Key`. Cùng key + cùng input → trả lại kết quả cũ. Cùng key + input khác → `409`. Thiếu key → `400`/`428`, tùy module. |
| ETag / If-Match | Billing: `"billing-{id:N}-{revision}"`. Support: `"support-{revision}"`. Knowledge source: `"{revision}"`. Thiếu If-Match → `428` (`IF_MATCH_REQUIRED` / `PRECONDITION_REQUIRED`). Sai revision → `412` (`REVISION_MISMATCH` / `PRECONDITION_FAILED`). |
| Phân trang | `page` ≥ 1, `pageSize` 1–100. Vượt giới hạn → `400`. Response `{ items, total, page, pageSize }` (Billing, Support, Audit, Learn/Catalog); Administration, review queue và scenario list dùng `totalCount` thay cho `total`. |
| Lỗi | ProblemDetails với `code` (PayOS dùng `errorCode`). Cột "Mã nghiệp vụ" liệt kê các mã đọc từ service/gate. |
| Email | Email người tạo/tác giả chỉ trả cho Admin. Owner chỉ thấy `displayName`. |
| Thời gian | Filter `from`/`to` phải là ISO-8601 có timezone (`2026-10-12T00:00:00Z`). Thiếu timezone → `400` (PayOS ledger, analytics). |

Cột **HTTP test** = "Có" khi route được gọi trực tiếp qua HTTP trong `Fire3D.AuthTests`. "Service/gate" = chỉ được kiểm qua service hoặc SQL gate.

## Bảng endpoint

Tổng: **121** endpoint mới hoặc đổi contract so với `3c9ef1f` (trước #54).

### #54 — Editor contract

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | GET | `/api/revisions/{id}/floors` | Editor contract | Đăng nhập | — | — | — | 200 | 400, 401, 403, 404 | — | Mới | Có |
| ☐ | PUT | `/api/scenario-drafts/{draftId}` | — | OrganizationUser, PlatformAdmin | If-Match | — | — | 204 | 400, 401, 403, 404, 409, 412, 422, 428 | EDITOR_JSON_MALFORMED, EDITOR_SCHEMA_INVALID, EDITOR_SCHEMA_VERSION_UNSUPPORTED | Đổi | Có |
| ☐ | POST | `/api/scenario-drafts/{draftId}/snapshot` | — | OrganizationUser, PlatformAdmin | If-Match, Idempotency-Key | — | — | 201 | 400, 401, 403, 404, 422 | — | Đổi | Có |

<details><summary>Mô tả từng endpoint</summary>

- **GET `/api/revisions/{id}/floors`** — Lists floors of the accepted versioned Geometry artifact for one revision.
- **PUT `/api/scenario-drafts/{draftId}`** — Updates the state of a scenario draft (D11). Requires If-Match header for concurrency control.
- **POST `/api/scenario-drafts/{draftId}/snapshot`** — Creates an immutable snapshot (ScenarioVersion) from a draft (D12).

</details>

### #55 — Playtest

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | POST | `/api/playtests/handoffs/redeem` | Playtest | OrganizationUser | — | — | RedeemPlaytestHandoffRequest | 200 | 401, 403, 404, 409 | PLAYTEST_HANDOFF_USED | Mới | Có |
| ☐ | GET | `/api/playtests/{playtestId}` | Playtest | OrganizationUser | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/cancel` | Playtest | OrganizationUser | Idempotency-Key | — | — | 200 | 400, 401, 403, 404, 409 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/complete` | Playtest | OrganizationUser | Idempotency-Key | — | CompletePlaytestRequest | 200 | 400, 401, 403, 404, 409, 422 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/events:batch` | Playtest | OrganizationUser | — | — | PlaytestEventsRequest | 200 | 401, 403, 404, 409, 422 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/handoffs` | Playtest | OrganizationUser | Idempotency-Key | — | — | 201 | 400, 401, 403, 404, 409, 503 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/heartbeat` | Playtest | OrganizationUser | — | — | — | 200 | 401, 403, 404, 409 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/launch-grants` | Playtest | OrganizationUser | Idempotency-Key | — | — | 200 | 400, 401, 403, 404, 409, 503 | — | Mới | Có |
| ☐ | POST | `/api/playtests/{playtestId}/launched` | Playtest | OrganizationUser | — | — | LaunchedPlaytestRequest | 200 | 401, 403, 404, 409, 422 | — | Mới | Có |

<details><summary>Mô tả từng endpoint</summary>

- **POST `/api/playtests/handoffs/redeem`** — Redeems a handoff code from another session (Mobile) of the same OrganizationUser and binds the playtest to it.
- **GET `/api/playtests/{playtestId}`** — Status, pinned package, current grant generation/expiry and which recovery actions the calling session may perform.
- **POST `/api/playtests/{playtestId}/cancel`** — Ends the playtest as Cancelled from any live session of the owner; serialised with complete. Trial is not refunded.
- **POST `/api/playtests/{playtestId}/complete`** — Completes once after events 1..lastEventSequence are stored; same key and input replays, otherwise 409.
- **POST `/api/playtests/{playtestId}/events:batch`** — Durable append-only telemetry. Same eventId and content replays; a different hash or reused sequence is a per-event conflict.
- **POST `/api/playtests/{playtestId}/handoffs`** — Creates a five-minute single-use code for QR/deep link. A new code revokes earlier open codes. Only before start.
- **POST `/api/playtests/{playtestId}/heartbeat`** — Liveness for a Running playtest from the launching session; stores server-received time.
- **POST `/api/playtests/{playtestId}/launch-grants`** — Reissues the five-minute launch grant before the runtime confirms launch; increments generation and fences older grants.
- **POST `/api/playtests/{playtestId}/launched`** — Runtime confirms it received the session for the current, unexpired grant generation: Launching → Running.

</details>

### #56 — Billing, quota & yêu cầu Enterprise

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | POST | `/api/admin/enterprise-quote-requests/{id}/quotations` | Enterprise | PlatformAdmin | Idempotency-Key | — | EnterpriseQuotationRequest | 201 | 400, 401, 403, 404, 409, 412, 428 | ENTERPRISE_REQUESTER_UNAVAILABLE, ENTERPRISE_REQUEST_CLOSED, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/admin/organizations/{organizationId}/ai-quota` | Quota & entitlement | PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |
| ☐ | GET | `/api/admin/organizations/{organizationId}/ai-quota/grants` | Quota & entitlement | PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |
| ☐ | GET | `/api/admin/organizations/{organizationId}/ai-usage` | Quota & entitlement | PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |
| ☐ | POST | `/api/admin/quotations/{id}/issue` | — | PlatformAdmin | If-Match | — | IssueQuotationRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | POST | `/api/billing/quotations` | — | OrganizationUser, PlatformAdmin | Idempotency-Key | — | QuotationWriteRequest | 201 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | PATCH | `/api/billing/quotations/{id}` | — | OrganizationUser, PlatformAdmin | If-Match | — | QuotationWriteRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | GET | `/api/buildings/{id}/service-entitlement` | Quota & entitlement | OrganizationUser, PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |
| ☐ | GET | `/api/organizations/me/ai-quota` | Quota & entitlement | OrganizationUser, PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |
| ☐ | GET | `/api/organizations/me/ai-quota/grants` | Quota & entitlement | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |
| ☐ | GET | `/api/organizations/me/ai-usage` | Quota & entitlement | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Mới | Có |

<details><summary>Mô tả từng endpoint</summary>

- **POST `/api/admin/enterprise-quote-requests/{id}/quotations`** — Admin drafts a Building quotation for the requesting OrganizationUser and marks the request Quoted. Idempotency-Key required; no service is granted.
- **POST `/api/admin/quotations/{id}/issue`** — Admin reprices and freezes the v7 snapshot: monthly price, 6/12 month term, learner limit, quota/policy and fixed UTC service periods. Provide items with quotationItemId/startsAt, taxAmount, terms, validUntil and If-Match. Renewal derives an existing committed end; payment deadline cannot exceed the earliest start.
- **POST `/api/billing/quotations`** — OrganizationUser creates a Draft for their own Buildings. Idempotency-Key is required; totals are server-calculated.
- **PATCH `/api/billing/quotations/{id}`** — Replaces Draft Building selections using If-Match. Issued/Accepted lines are immutable.
- **GET `/api/buildings/{id}/service-entitlement`** — Current and upcoming paid service of a Building: effective learner limit (with upgrades), seats used/remaining, period and upgrades.
- **GET `/api/organizations/me/ai-quota`** — Organization prepaid AI quota per unit: granted, reserved, consumed, expired, available and scheduled at asOf.

</details>

### Task 4 — Learner sessions & QR

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | GET | `/api/buildings/{buildingId}/qr-codes` | Learner sessions & QR | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404 | — | Mới | Service/gate |
| ☐ | POST | `/api/buildings/{buildingId}/qr-codes` | Learner sessions & QR | OrganizationUser, PlatformAdmin | — | — | CreateBuildingQrRequest | 201 | 400, 401, 403, 404, 409 | QR_ALREADY_EXISTS | Mới | Service/gate |
| ☐ | POST | `/api/buildings/{buildingId}/qr-codes/{qrId}/revoke` | Learner sessions & QR | OrganizationUser, PlatformAdmin | — | — | — | 200 | 401, 403, 404, 409 | — | Mới | Service/gate |
| ☐ | POST | `/api/buildings/{buildingId}/qr-codes/{qrId}/rotate` | Learner sessions & QR | OrganizationUser, PlatformAdmin | — | — | CreateBuildingQrRequest | 200 | 401, 403, 404, 409 | — | Mới | Có |
| ☐ | GET | `/api/qr/{qrToken}` | Learner sessions & QR | Trainee, OrganizationUser, PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Service/gate |
| ☐ | GET | `/api/qr/{qrToken}/trainings` | Learner sessions & QR | Trainee, OrganizationUser, PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Service/gate |
| ☐ | POST | `/api/training/reconcile` | Learner sessions & QR | Trainee | — | — | ReconcileTrainingRequest | 200 | 400, 401, 403 | — | Mới | Có |
| ☐ | POST | `/api/training/sessions` | Learner sessions & QR | Trainee | Idempotency-Key | — | PrepareTrainingSessionRequest | 201 | 400, 401, 403, 404, 409 | — | Mới | Có |
| ☐ | GET | `/api/training/sessions/{id}` | Learner sessions & QR | Trainee | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | POST | `/api/training/sessions/{id}/complete` | Learner sessions & QR | Trainee | Idempotency-Key | — | CompleteTrainingSessionRequest | 200, 202 | 400, 401, 403, 404, 409, 422 | — | Mới | Có |
| ☐ | POST | `/api/training/sessions/{id}/continuation` | Learner sessions & QR | Trainee | — | — | — | 200 | 401, 403, 404, 409, 503 | — | Mới | Service/gate |
| ☐ | POST | `/api/training/sessions/{id}/events:batch` | Learner sessions & QR | Trainee | — | — | TrainingEventsRequest | 200 | 401, 403, 404, 409, 422 | — | Mới | Có |
| ☐ | POST | `/api/training/sessions/{id}/heartbeat` | Learner sessions & QR | Trainee | — | — | — | 200 | 401, 403, 404, 409 | — | Mới | Có |
| ☐ | POST | `/api/training/sessions/{id}/launched` | Learner sessions & QR | Trainee | — | — | LaunchedTrainingSessionRequest | 200 | 401, 403, 404, 409, 422 | — | Mới | Có |
| ☐ | GET | `/api/training/sessions/{id}/result` | Learner sessions & QR | Trainee | — | — | — | 200 | 401, 403, 404, 409 | — | Mới | Có |
| ☐ | POST | `/api/training/sessions/{id}/start` | Learner sessions & QR | Trainee | Idempotency-Key | — | — | 200 | 400, 401, 403, 404, 409, 503 | — | Mới | Có |

<details><summary>Mô tả từng endpoint</summary>

- **POST `/api/buildings/{buildingId}/qr-codes`** — Creates the canonical QR code of a Building.
- **GET `/api/qr/{qrToken}/trainings`** — Published Trainings of the QR Building; Private Buildings still require the Trainee's participation grant.
- **POST `/api/training/reconcile`** — Reports the caller's own sessions for given IDs or prepare/start idempotency keys; other users' IDs are simply absent.
- **POST `/api/training/sessions/{id}/complete`** — Pins lastEventSequence. 200 Completed with one immutable result, or 202 AwaitingSync with missing ranges until every event arrives.
- **POST `/api/training/sessions/{id}/continuation`** — Live login reissues the continuation for the same owner and started session; the previous token stops working.
- **POST `/api/training/sessions/{id}/events:batch`** — Append-only telemetry: same eventId and content replays; changed content or reused sequence is a per-event conflict. Returns acknowledged sequence and gaps.
- **POST `/api/training/sessions/{id}/start`** — Online start: live session, lifecycle, Public/Private access, approval, Published release, entitlement, runtime and seat; returns a five-minute launch grant and a continuation token. Before launch the same session may call start again for a new grant generation.

</details>

### #57 — Learn CMS & Library

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | POST | `/api/admin/learn/media/validate` | Learn CMS & Library | PlatformAdmin | — | — | MediaValidationRequest | 200 | 401, 403, 422 | — | Mới | Có |
| ☐ | GET | `/api/admin/learn/posts` | Learn CMS & Library | PlatformAdmin | — | status, page, pageSize | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | POST | `/api/admin/learn/posts` | Learn CMS & Library | PlatformAdmin | Idempotency-Key | — | CreateLearnPostRequest | 201 | 401, 403, 409, 422 | — | Mới | Có |
| ☐ | GET | `/api/admin/learn/posts/{id}` | Learn CMS & Library | PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | POST | `/api/admin/learn/posts/{id}/versions` | Learn CMS & Library | PlatformAdmin | Idempotency-Key | — | LearnVersionInput | 201 | 401, 403, 409, 422 | — | Mới | Có |
| ☐ | POST | `/api/admin/learn/posts/{id}/{operation}` | Learn CMS & Library | PlatformAdmin | If-Match | — | — | 200 | 401, 403, 409, 412, 428 | — | Mới | Có |
| ☐ | GET | `/api/admin/learn/versions/{versionId}` | Learn CMS & Library | PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Service/gate |
| ☐ | PUT | `/api/admin/learn/versions/{versionId}` | Learn CMS & Library | PlatformAdmin | If-Match | — | LearnVersionInput | 200 | 401, 403, 409, 412, 422, 428 | — | Mới | Service/gate |
| ☐ | POST | `/api/admin/learn/versions/{versionId}/publish` | Learn CMS & Library | PlatformAdmin | If-Match | — | — | 200 | 401, 403, 409, 412, 428 | — | Mới | Service/gate |
| ☐ | GET | `/api/admin/library/items` | Learn CMS & Library | PlatformAdmin | — | kind, page, pageSize | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | POST | `/api/admin/library/items` | Learn CMS & Library | PlatformAdmin | Idempotency-Key | — | CreateLibraryItemRequest | 201 | 401, 403, 409 | — | Mới | Có |
| ☐ | GET | `/api/admin/library/items/{id}` | Learn CMS & Library | PlatformAdmin | — | — | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | PATCH | `/api/admin/library/items/{id}` | Learn CMS & Library | PlatformAdmin | If-Match | — | UpdateLibraryItemRequest | 200 | 401, 403, 412, 428 | — | Mới | Có |
| ☐ | POST | `/api/admin/library/items/{id}/versions` | Learn CMS & Library | PlatformAdmin | Idempotency-Key | — | LibraryVersionInput | 201 | 401, 403, 422 | — | Mới | Có |
| ☐ | GET | `/api/admin/library/versions/{id}` | Learn CMS & Library | PlatformAdmin | — | — | — | 200 | 401, 403 | — | Mới | Service/gate |
| ☐ | PUT | `/api/admin/library/versions/{id}` | Learn CMS & Library | PlatformAdmin | If-Match | — | LibraryVersionInput | 200 | 401, 403, 409, 412, 422, 428 | — | Mới | Service/gate |
| ☐ | POST | `/api/admin/library/versions/{id}/publish` | Learn CMS & Library | PlatformAdmin | If-Match | — | — | 200 | 401, 403, 409, 412, 428 | — | Mới | Service/gate |
| ☐ | GET | `/api/learn/bookmarks` | Learn CMS & Library | Trainee | — | page, pageSize | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | DELETE | `/api/learn/bookmarks/{postId}` | Learn CMS & Library | Trainee | — | — | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | PUT | `/api/learn/bookmarks/{postId}` | Learn CMS & Library | Trainee | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | GET | `/api/learn/posts` | Learn CMS & Library | Anonymous | — | situation, page, pageSize | — | 200 | 400 | — | Mới | Có |
| ☐ | GET | `/api/learn/posts/{slug}` | Learn CMS & Library | Anonymous | — | — | — | 200 | 404, 410 | — | Mới | Có |
| ☐ | GET | `/api/library/items` | Learn CMS & Library | OrganizationUser, PlatformAdmin | — | kind, page, pageSize | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | GET | `/api/library/items/{id}` | Learn CMS & Library | OrganizationUser, PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | GET | `/api/library/versions/{id}` | Learn CMS & Library | OrganizationUser, PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Service/gate |

<details><summary>Mô tả từng endpoint</summary>

- **POST `/api/admin/learn/media/validate`** — Validates a YouTube, Facebook or TikTok video URL and returns its canonical descriptor. The URL is never fetched.
- **POST `/api/admin/learn/posts`** — Creates a post with its first Draft version; publish=true publishes it atomically. Idempotency-Key required.
- **POST `/api/admin/learn/posts/{id}/{operation}`** — Post lifecycle with If-Match = post ETag: hide (Published→Hidden), show (Hidden→Published), delete, restore (to Hidden when it was ever published, otherwise Unpublished).
- **POST `/api/admin/learn/versions/{versionId}/publish`** — Publishes a Draft version (If-Match = version ETag). Cited sources must be approved Common sources.
- **PATCH `/api/admin/library/items/{id}`** — Deactivating an item prevents new selections; published versions referenced by snapshots stay readable.
- **GET `/api/learn/bookmarks`** — Bookmarks of posts that are no longer public are listed as available=false without content.

</details>

### #58 — Organization AI, Knowledge & Analytics

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | GET | `/api/admin/ai/policies` | Organization AI | PlatformAdmin | — | — | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | POST | `/api/admin/ai/policies` | Organization AI | PlatformAdmin | Idempotency-Key | — | CreateAiPolicyRequest | 201 | 401, 403, 409, 422 | — | Mới | Có |
| ☐ | GET | `/api/admin/ai/requests` | Organization AI | PlatformAdmin | — | status, organizationId, page, pageSize | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | GET | `/api/admin/ai/requests/{requestId}` | Organization AI | PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | POST | `/api/admin/ai/requests/{requestId}/reconcile` | Organization AI | PlatformAdmin | Idempotency-Key | — | — | 202 | 401, 403, 409 | — | Mới | Có |
| ☐ | GET | `/api/admin/analytics/revenue` | Analytics | PlatformAdmin | — | from, to | — | 200 | 400, 401, 403 | — | Mới | Có |
| ☐ | GET | `/api/admin/analytics/training` | Analytics | PlatformAdmin | — | from, to | — | 200 | 400, 401, 403 | — | Mới | Có |
| ☐ | POST | `/api/admin/knowledge-sources` | Knowledge sources | PlatformAdmin | Idempotency-Key | — | CreateKnowledgeSourceRequest | 201 | 401, 403, 409, 422 | — | Mới | Có |
| ☐ | POST | `/api/admin/knowledge-sources/{id}/{operation}` | Knowledge sources | PlatformAdmin | If-Match | — | — | 200 | 401, 403, 409, 412, 428 | — | Mới | Có |
| ☐ | POST | `/api/ai/organization/answer` | Organization AI | OrganizationUser | Idempotency-Key | — | AnswerAiRequest | 202 | 401, 403, 409, 422, 503 | — | Mới | Có |
| ☐ | POST | `/api/ai/organization/scenario-draft` | Organization AI | OrganizationUser | Idempotency-Key | — | ScenarioDraftAiRequest | 202 | 401, 403, 409, 422, 503 | — | Mới | Có |
| ☐ | GET | `/api/ai/organization/sources` | Organization AI | OrganizationUser | — | — | — | 200 | 401, 403 | — | Mới | Có |
| ☐ | GET | `/api/ai/requests/{requestId}` | Organization AI | OrganizationUser | — | — | — | 200 | 401, 403, 404 | — | Mới | Có |
| ☐ | GET | `/api/organizations/me/analytics/training` | Analytics | OrganizationUser | — | from, to | — | 200 | 400, 401, 403 | — | Mới | Có |

<details><summary>Mô tả từng endpoint</summary>

- **POST `/api/admin/ai/policies`** — Creates an immutable reservation policy for one operation; periods of one operation may not overlap. Idempotency-Key required.
- **POST `/api/admin/ai/requests/{requestId}/reconcile`** — Schedules a provider lookup for a NeedsReconcile request. Never edits consumed usage. Idempotency-Key required.
- **GET `/api/admin/analytics/revenue`** — PlatformAdmin revenue: Applied payment transactions by receipt time, per currency, purpose and UTC day.
- **GET `/api/admin/analytics/training`** — PlatformAdmin training analytics across organizations, with per-organization breakdown and platform AI usage.
- **POST `/api/admin/knowledge-sources`** — Registers one source version as Draft. Common sources have no organization; Organization sources belong to one tenant. Idempotency-Key required.
- **POST `/api/ai/organization/answer`** — Queues a grounded answer over the caller's eligible sources. Idempotency-Key required; returns 202 with the request.
- **POST `/api/ai/organization/scenario-draft`** — Queues a scenario draft suggestion for a Building of the caller's organization. Idempotency-Key required; returns 202 with the request.
- **GET `/api/organizations/me/analytics/training`** — OrganizationUser training analytics for the caller's organization plus its AI usage. UTC [from,to), default 30 days, maximum 90.

</details>

### #67 — Support, Audit & vận hành Admin

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | GET | `/api/admin/audit-logs` | — | PlatformAdmin | — | ActorId, OrganizationId, Action, TargetEntity, TargetId, CorrelationId, From, To, Page, PageSize | — | 200 | 400, 401, 403 | — | Đổi | Có |
| ☐ | GET | `/api/admin/audit-logs/{id}` | — | PlatformAdmin | — | — | — | 200 | 401, 403, 404 | — | Đổi | Có |
| ☐ | GET | `/api/admin/feedback` | — | PlatformAdmin | — | Page, PageSize, Status, Priority, AssignedTo, OrganizationId | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Đổi | Có |
| ☐ | GET | `/api/admin/feedback/{id}` | Support | PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | NOT_FOUND | Mới | Có |
| ☐ | PATCH | `/api/admin/feedback/{id}/status` | — | PlatformAdmin | If-Match | — | AdminFeedbackUpdateRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | INVALID_STATUS_TRANSITION, NOT_FOUND, PRECONDITION_FAILED, PRECONDITION_REQUIRED | Đổi | Có |
| ☐ | GET | `/api/admin/organizations/{organizationId}/analytics/operations` | Admin org operations | PlatformAdmin | — | from, to | — | 200 | 400, 401, 403, 404 | — | Mới | Có |
| ☐ | GET | `/api/admin/support/tickets` | — | PlatformAdmin | — | Page, PageSize, Status, Priority, AssignedTo, OrganizationId | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Đổi | Có |
| ☐ | GET | `/api/admin/support/tickets/{id}` | — | PlatformAdmin | — | Page, PageSize, Status, Priority, AssignedTo, OrganizationId | — | 200 | 400, 401, 403, 404, 409, 412, 428 | NOT_FOUND | Đổi | Có |
| ☐ | PATCH | `/api/admin/support/tickets/{id}` | — | PlatformAdmin | If-Match | — | AdminTicketUpdateRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | INVALID_STATUS_TRANSITION, NOT_FOUND, PRECONDITION_FAILED, PRECONDITION_REQUIRED | Đổi | Có |
| ☐ | POST | `/api/admin/support/tickets/{id}/messages` | — | PlatformAdmin | Idempotency-Key | — | MessageRequest | 201 | 400, 401, 403, 404, 409, 412, 428 | IDEMPOTENCY_KEY_CONFLICT, IDEMPOTENCY_KEY_REQUIRED, NOT_FOUND, TICKET_CLOSED, VALIDATION_ERROR | Đổi | Có |
| ☐ | GET | `/api/feedback` | — | Trainee, OrganizationUser | — | Page, PageSize, Status, Priority, AssignedTo, OrganizationId | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Đổi | Có |
| ☐ | POST | `/api/feedback` | — | Trainee, OrganizationUser | Idempotency-Key | — | CreateFeedbackRequest | 201 | 400, 401, 403, 404, 409, 412, 428 | IDEMPOTENCY_KEY_CONFLICT, IDEMPOTENCY_KEY_REQUIRED, NOT_FOUND, VALIDATION_ERROR | Đổi | Có |
| ☐ | GET | `/api/support/tickets` | — | Trainee, OrganizationUser | — | Page, PageSize, Status, Priority, AssignedTo, OrganizationId | — | 200 | 400, 401, 403, 404, 409, 412, 428 | — | Đổi | Có |
| ☐ | POST | `/api/support/tickets` | — | Trainee, OrganizationUser | Idempotency-Key | — | CreateTicketRequest | 201 | 400, 401, 403, 404, 409, 412, 428 | IDEMPOTENCY_KEY_CONFLICT, IDEMPOTENCY_KEY_REQUIRED, NOT_FOUND, SUPPORT_REFERENCE_NOT_FOUND, VALIDATION_ERROR | Đổi | Có |
| ☐ | GET | `/api/support/tickets/{id}` | — | Trainee, OrganizationUser | — | Page, PageSize, Status, Priority, AssignedTo, OrganizationId | — | 200 | 400, 401, 403, 404, 409, 412, 428 | NOT_FOUND | Đổi | Có |
| ☐ | POST | `/api/support/tickets/{id}/messages` | — | Trainee, OrganizationUser | Idempotency-Key | — | MessageRequest | 201 | 400, 401, 403, 404, 409, 412, 428 | IDEMPOTENCY_KEY_CONFLICT, IDEMPOTENCY_KEY_REQUIRED, NOT_FOUND, TICKET_CLOSED, VALIDATION_ERROR | Đổi | Có |

<details><summary>Mô tả từng endpoint</summary>

- **GET `/api/admin/audit-logs`** — Reads immutable audit metadata. PlatformAdmin; default 30 days, maximum 90, UTC [from,to), stable createdAt/ID pagination.
- **GET `/api/admin/audit-logs/{id}`** — Reads metadata and allowlisted field changes. Raw JSON, credentials, PII and support content are never returned.
- **GET `/api/admin/feedback/{id}`** — Feedback detail for PlatformAdmin with creator (including email) and organization labels; the ETag is the If-Match value for PATCH status.
- **GET `/api/admin/organizations/{organizationId}/analytics/operations`** — PlatformAdmin operations of one organization: its Buildings, processing jobs and tickets stored with that organization. No account, learner, revenue or AI aggregate. UTC [from,to), default 30 days, maximum 90; inactive organizations included, deleted ones 404.

</details>

### #68 — Billing, Payments, Notifications & Enterprise workflow

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | GET | `/api/admin/enterprise-quote-requests` | Enterprise | PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | GET | `/api/admin/enterprise-quote-requests/{id}` | Enterprise | PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Mới | Có |
| ☐ | PATCH | `/api/admin/enterprise-quote-requests/{id}` | Enterprise | PlatformAdmin | If-Match | — | EnterpriseStatusRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | ENTERPRISE_REQUEST_CLOSED, IF_MATCH_REQUIRED, INVALID_IF_MATCH, REVISION_MISMATCH, SESSION_REVOKED | Mới | Có |
| ☐ | POST | `/api/admin/enterprise-quote-requests/{id}/close` | Enterprise | PlatformAdmin | If-Match, Idempotency-Key | — | EnterpriseCloseRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | ENTERPRISE_REQUEST_CLOSED, IF_MATCH_REQUIRED, INVALID_IF_MATCH, REVISION_MISMATCH, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/admin/enterprise-quote-requests/{id}/responses` | Enterprise | PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Mới | Có |
| ☐ | POST | `/api/admin/enterprise-quote-requests/{id}/responses` | Enterprise | PlatformAdmin | Idempotency-Key | — | EnterpriseResponseBody | 201 | 400, 401, 403, 404, 409, 412, 428 | ENTERPRISE_REQUEST_CLOSED, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/admin/payments/payos/checkouts` | Billing | PlatformAdmin | — | OrganizationId, QuotationId, Status, From, To, Page, PageSize | — | 200 | 400, 401, 403, 404, 409, 503 | BILLING_PAGINATION_INVALID, BILLING_ROLE_FORBIDDEN, BILLING_VALIDATION_FAILED, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/admin/payments/payos/requests` | Billing | PlatformAdmin | — | OrganizationId, QuotationId, Status, From, To, Page, PageSize | — | 200 | 400, 401, 403, 404, 409, 503 | BILLING_PAGINATION_INVALID, BILLING_ROLE_FORBIDDEN, BILLING_VALIDATION_FAILED, SESSION_REVOKED | Mới | Có |
| ☐ | POST | `/api/admin/quotations/{id}/cancel` | Billing | PlatformAdmin | If-Match, Idempotency-Key | — | CancelQuotationRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | IF_MATCH_REQUIRED, INVALID_IF_MATCH, ORGANIZATION_USER_REQUIRED, QUOTATION_ALREADY_PAID, QUOTATION_CHECKOUT_UNRESOLVED, QUOTATION_EXPIRED, REVISION_MISMATCH, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/billing/enterprise-quote-requests` | Enterprise | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | GET | `/api/billing/enterprise-quote-requests/{id}` | Enterprise | OrganizationUser, PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/billing/enterprise-quote-requests/{id}/responses` | Enterprise | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/billing/quotations` | — | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | GET | `/api/billing/quotations/{id}` | — | OrganizationUser, PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | SESSION_REVOKED | Đổi | Có |
| ☐ | POST | `/api/billing/quotations/{id}/accept` | — | OrganizationUser, PlatformAdmin | If-Match | — | — | 200 | 400, 401, 403, 404, 409, 412, 428 | IF_MATCH_REQUIRED, INVALID_IF_MATCH, REVISION_MISMATCH, SESSION_REVOKED | Đổi | Có |
| ☐ | POST | `/api/billing/quotations/{id}/cancel` | Billing | OrganizationUser, PlatformAdmin | If-Match, Idempotency-Key | — | CancelQuotationRequest | 200 | 400, 401, 403, 404, 409, 412, 428 | IF_MATCH_REQUIRED, INVALID_IF_MATCH, ORGANIZATION_USER_REQUIRED, QUOTATION_ALREADY_PAID, QUOTATION_CHECKOUT_UNRESOLVED, QUOTATION_EXPIRED, REVISION_MISMATCH, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/billing/quotations/{id}/checkouts` | Billing | OrganizationUser, PlatformAdmin | — | page, pageSize | — | 200 | 400, 401, 403, 404, 409, 503 | BILLING_PAGINATION_INVALID, BILLING_ROLE_FORBIDDEN, BILLING_VALIDATION_FAILED, SESSION_REVOKED | Mới | Có |
| ☐ | GET | `/api/organizations/me/notifications` | Notifications | OrganizationUser | — | Unread, Type, Page, PageSize | — | 200 | 400, 401, 403, 404 | — | Mới | Có |
| ☐ | PATCH | `/api/organizations/me/notifications/{id}/read` | Notifications | OrganizationUser | — | — | — | 200 | 400, 401, 403, 404 | NOTIFICATION_NOT_FOUND | Mới | Có |
| ☐ | POST | `/api/payments/payos/create` | Billing | OrganizationUser | Idempotency-Key | — | CreatePayosRequest | 200, 201, 202 | 400, 401, 403, 404, 409, 503 | PAYOS_QUOTATION_CANCELLED, PAYOS_QUOTATION_EXPIRED, PAYOS_QUOTATION_REQUIRED, QUOTATION_NOT_PAYABLE, SESSION_REVOKED | Đổi | Có |
| ☐ | GET | `/api/payments/payos/requests/{id}` | — | OrganizationUser, PlatformAdmin | — | — | — | 200 | 400, 401, 403, 404, 409, 503 | BILLING_PAGINATION_INVALID, BILLING_ROLE_FORBIDDEN, BILLING_VALIDATION_FAILED, SESSION_REVOKED | Đổi | Có |

<details><summary>Mô tả từng endpoint</summary>

- **PATCH `/api/admin/enterprise-quote-requests/{id}`** — Admin changes status to Contacted, Rejected or Cancelled with If-Match. No charge or service grant.
- **POST `/api/admin/enterprise-quote-requests/{id}/close`** — Admin closes handling with If-Match, Idempotency-Key and a reason. Linked quotations and payments are not cancelled; no service is granted.
- **POST `/api/admin/enterprise-quote-requests/{id}/responses`** — Admin appends a reply visible to the requesting organization. Idempotency-Key required; Closed requests reject new replies.
- **GET `/api/admin/payments/payos/checkouts`** — PlatformAdmin lists PayOS checkouts (including ones that failed before a payment request was bound) with quotation, payment, transaction and provisioning status. Filters: organizationId, quotationId, checkout status, UTC [from,to) on creation; createdAt DESC, id.
- **GET `/api/admin/payments/payos/requests`** — PlatformAdmin lists PayOS payment requests with their checkout, transaction and provisioning status. Filters as for checkouts; status is the payment request status.
- **POST `/api/admin/quotations/{id}/cancel`** — PlatformAdmin cancels any quotation under the same payment and checkout gates as the organization route.
- **GET `/api/billing/enterprise-quote-requests/{id}`** — OrganizationUser reads one enterprise request of their organization.
- **GET `/api/billing/enterprise-quote-requests/{id}/responses`** — OrganizationUser reads the Admin replies on one enterprise request of their organization, oldest first.
- **POST `/api/billing/quotations/{id}/accept`** — OrganizationUser accepts an unexpired Issued quotation in their tenant with If-Match. Acceptance does not charge or grant service.
- **POST `/api/billing/quotations/{id}/cancel`** — OrganizationUser cancels a Draft/Issued/Accepted quotation of their tenant with If-Match, Idempotency-Key and a reason. 409 QUOTATION_ALREADY_PAID when a payment is Paid/Applied; 409 QUOTATION_CHECKOUT_UNRESOLVED (with checkoutIds) while a checkout is Creating/Ready/NeedsReconcile; an Expired quotation keeps its status; an already Cancelled quotation returns its current state. Same key and input replay the result.
- **GET `/api/billing/quotations/{id}/checkouts`** — Checkout history of one quotation for its OrganizationUser or PlatformAdmin, newest first.
- **GET `/api/organizations/me/notifications`** — Reminders addressed to the current OrganizationUser in their organization, newest first. Filters: unread, type (BuildingServiceExpiring); page 20 by default, maximum 100.
- **PATCH `/api/organizations/me/notifications/{id}/read`** — Marks one of the caller's notifications as read. Idempotent: the first read time is kept.
- **POST `/api/payments/payos/create`** — OrganizationUser creates/replays checkout for an Accepted quotation. Server determines amount and tenant.
- **GET `/api/payments/payos/requests/{id}`** — Tenant-scoped payment truth and per-Building provisioning. Paid does not mean all lines succeeded.

</details>

### #69 — Danh mục & Rubric

| ☐ | Method | Route | Nhóm | Quyền | Header bắt buộc | Query | Body | Thành công | Lỗi HTTP | Mã nghiệp vụ | Loại | HTTP test |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ☐ | GET | `/api/admin/knowledge-sources` | Knowledge sources (đọc) | PlatformAdmin | — | visibility, status, search, page, pageSize | — | 200 | 400, 401, 403 | — | Mới | Có |
| ☐ | GET | `/api/admin/knowledge-sources/{id}` | Knowledge sources (đọc) | PlatformAdmin | — | — | — | 200 | 401, 403, 404 | NOT_FOUND | Mới | Có |
| ☐ | GET | `/api/admin/learn/situations` | — | PlatformAdmin | — | search, isActive, page, pageSize | — | 200 | 400, 401, 403 | — | Mới | Có |
| ☐ | POST | `/api/admin/learn/situations` | — | PlatformAdmin | Idempotency-Key | — | CreateLearnSituationRequest | 201 | 401, 403, 409 | LEARN_SITUATION_INVALID | Mới | Có |
| ☐ | GET | `/api/learn/situations` | — | Anonymous | — | — | — | 200 | — | — | Mới | Có |
| ☐ | GET | `/api/scenario-rubrics/catalog` | Rubric catalog | OrganizationUser, PlatformAdmin | — | — | — | 200 | 401, 403 | — | Mới | Có |

<details><summary>Mô tả từng endpoint</summary>

- **GET `/api/admin/knowledge-sources`** — Knowledge source registry and editor catalog. Without visibility every source is listed (admin review); the Learn editor passes visibility=Common so tenant sources never enter its catalog. status Draft/Approved/Retired; search on title, version label and jurisdiction. canUseInDraft: a Learn draft may cite it; canPublishReference: publish/Hidden/RAG accept it now.
- **GET `/api/admin/learn/situations`** — PlatformAdmin situation catalog for the editor: search on slug/name, isActive filter, ordered by name; default 20, max 100. The public GET stays unchanged.
- **GET `/api/scenario-rubrics/catalog`** — Rubric contract published by the backend: supported schema versions (the canonical first version is the string "1"), default
version, server-computed metrics, operators, criterion fields and limits. The same registry validates scenario drafts,
Library rubric samples and AI drafts. No pass threshold, weight or criterion is suggested.

</details>


## Kịch bản kiểm thử theo nghiệp vụ (#67–#69)

### #67 — Support, Audit, vận hành Admin
- [ ] Owner tạo feedback/ticket → response có `createdBy.displayName` và `organization.name`, **không** có `createdBy.email`.
- [ ] Admin `GET /api/admin/feedback/{id}` → có `createdBy.email`, header `ETag: "support-{revision}"`. User thường → `403`. ID lạ → `404`.
- [ ] `PATCH /api/admin/feedback/{id}/status` với ETag lấy từ detail → `200`. Thiếu If-Match → `428`. ETag cũ → `412`.
- [ ] Admin trả lời ticket → owner thấy tên staff (`author.displayName`), không thấy email staff. Admin thấy email cả hai phía.
- [ ] Gửi lại cùng `Idempotency-Key` → trả lại kết quả cũ, label được tính lại theo người gọi. Receipt không lưu label/email.
- [ ] Tổ chức khác đọc ticket → `404`. Danh sách của tổ chức khác → `total = 0`.
- [ ] User hoặc tổ chức bị xóa → Admin vẫn đọc được (`available:false`, `displayName:null`). Admin gửi message hoặc đổi status → `404 NOT_FOUND`.
- [ ] `GET /api/admin/audit-logs` trả `actorDisplayName` / `organizationName`. `GET /api/admin/audit-logs/{id}` trả thêm `changes` và `reason` (support writer PATCH ticket/feedback status, hủy quotation, đóng enterprise request, reject nội dung scenario). Lọc được theo `ActorId`, `OrganizationId`, `Action`, `TargetEntity`, `TargetId`, `CorrelationId`, `From`, `To`.
- [ ] `GET /api/admin/organizations/{organizationId}/analytics/operations?from&to` trả Buildings, processing jobs và tickets của đúng tổ chức. Không có account, learner, revenue hay AI.
  - [ ] Khoảng `[from,to)` UTC, mặc định 30 ngày, tối đa 90 ngày.
  - [ ] Timestamp không có timezone, khoảng âm hoặc dài hơn 90 ngày → `400 VALIDATION_ERROR`.
  - [ ] Tổ chức inactive vẫn trả số liệu. Tổ chức đã xóa hoặc ID lạ → `404`. Không phải PlatformAdmin → `403`.

### #68 — Billing, Payments, Notifications, Enterprise
- [ ] Quotation Draft: `taxAmount: null`, `pricingStatus: "Estimated"`. Sau khi Issue thì có số thuế thật.
- [ ] Hủy quotation (`POST /api/billing/quotations/{id}/cancel`, body `{reason}`, cần `If-Match` + `Idempotency-Key`):
  - [ ] Draft → `200`, `status: "Cancelled"`, audit ghi `reason`.
  - [ ] Cùng key, cùng reason → replay `200`. Cùng key, reason khác → `409`.
  - [ ] Key mới trên quotation đã Cancelled → `200` trả trạng thái hiện tại, không thêm audit.
  - [ ] Reason rỗng → `400`. Thiếu If-Match → `428`. Tổ chức khác → `404`.
  - [ ] Owner gọi route admin → `403`. PlatformAdmin gọi route owner → `403 ORGANIZATION_USER_REQUIRED`.
  - [ ] Đang có checkout mở → `409 QUOTATION_CHECKOUT_UNRESOLVED` kèm `checkoutIds`. Sau khi provider xác nhận hủy link → hủy được.
  - [ ] Đã thanh toán → `409 QUOTATION_ALREADY_PAID`.
  - [ ] `Expired`, hoặc `Issued`/`Accepted` đã quá `validUntil` → `409 QUOTATION_EXPIRED`, status giữ nguyên.
  - [ ] Sau khi hủy, tạo PayOS link mới → bị chặn (`PAYOS_QUOTATION_CANCELLED`).
- [ ] Danh sách checkout/payment (`/api/admin/payments/payos/checkouts`, `/requests`, `/api/billing/quotations/{id}/checkouts`):
  - [ ] Checkout mất response từ provider vẫn hiện, với `paymentRequestId: null`.
  - [ ] Quotation nhiều Building → 1 dòng payment.
  - [ ] `provisioningStatus` thống nhất với trang chi tiết payment: `NotStarted` / `Pending` / `Partial` / `Succeeded` / `NeedsReconcile`.
  - [ ] List của owner không trả `checkoutUrl`.
  - [ ] `createdAt` của list requests là thời điểm tạo payment request (đúng với thứ tự sort).
  - [ ] Filter `status`, `from`/`to` (bắt buộc có offset), `organizationId`, `quotationId`. `pageSize > 100` → `400`.
  - [ ] Session family bị thu hồi → `401`.
- [ ] `GET /api/payments/payos/requests/{id}` trả `provisioningStatus` cùng quy tắc với list.
- [ ] Notifications (`/api/organizations/me/notifications`):
  - [ ] Chỉ người nhận thấy notification của mình. Lọc được `unread=true` và `type`. `type` lạ → `400`.
  - [ ] Admin gọi → `403`. Người khác mark read → `404`.
  - [ ] Mark read lặp lại vẫn giữ nguyên `readAt` cũ.
  - [ ] Sau khi gia hạn, `stillRelevant` = `false` và lịch sử vẫn giữ.
- [ ] Worker gửi email nhắc gia hạn:
  - [ ] Hai worker không bao giờ gửi trùng.
  - [ ] Lease hết hạn → worker khác lấy lại (at-least-once).
  - [ ] Lease hết hạn ở lần thử cuối → `Failed` / `LEASE_EXPIRED_AFTER_MAX_ATTEMPTS`.
  - [ ] Entitlement đã hết hạn hoặc bị suspend trước lúc gửi → `SUPPRESSED_ENTITLEMENT_ENDED`, không gửi email.
- [ ] Enterprise workflow:
  - [ ] Admin `PATCH /api/admin/enterprise-quote-requests/{id}` dùng `assignedTo` / `clearAssignment` / `status`. Chỉ gán được PlatformAdmin đang active, gán người khác → `400`. Status giữ nguyên khi chỉ đổi assignee. ETag cũ → `412`.
  - [ ] Admin thêm response (append-only, replay theo key). Org đọc được qua `GET /api/billing/enterprise-quote-requests/{id}/responses`. Tổ chức khác → `404`. Owner tự trả lời → `403`.
  - [ ] Đóng request cần `reason`, `If-Match` và `Idempotency-Key`. Quotation đã liên kết giữ nguyên. Sau khi đóng, response hoặc đổi status → `409 ENTERPRISE_REQUEST_CLOSED`.
  - [ ] Org `GET /api/billing/enterprise-quote-requests/{id}` và list **không** trả `assignedTo` / `closeReason`. Admin thấy đủ.

### #69 — Danh mục và Rubric
- [ ] `GET /api/admin/knowledge-sources` mặc định trả **mọi** visibility (Common + Organization). Editor gọi với `?visibility=Common`. `visibility` lạ → `400`.
- [ ] Mỗi dòng có cờ `canUseInDraft` / `canPublishReference`: Approved → cả hai `true`. Draft → chỉ `canUseInDraft` là `true`.
- [ ] `search` coi `%` và `_` là ký tự thường.
- [ ] `GET /api/admin/knowledge-sources/{id}` trả cùng cờ, header `ETag` = revision. ID lạ → `404`.
- [ ] `GET /api/admin/learn/situations` phân trang, có `search` và `isActive`. `pageSize > 100` → `400`. Danh sách public `/api/learn/situations` chỉ có situation active.
- [ ] `GET /api/scenario-rubrics/catalog` (OrganizationUser và PlatformAdmin) → `defaultSchemaVersion: "1"`, `supportedSchemaVersions: ["1"]`, metrics, operators, giới hạn criterion (id ≤ 128, description ≤ 1000, tối đa 100 criteria). Anonymous → `401`.
- [ ] Rubric có `schema_version` là `1` (số), `"v1"` hoặc `"1.0"` → `RUBRIC_SCHEMA_VERSION_UNSUPPORTED`, cả ở Library sample, editor draft lẫn snapshot.
