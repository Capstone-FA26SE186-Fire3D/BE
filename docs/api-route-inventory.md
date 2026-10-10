# Route inventory generated from OpenAPI

216 HTTP operations in the captured source test artifact. Includes metadata only; this is not a completion or deployment checklist. Run the generator again after route/metadata changes. Role/tenant/lifecycle and feature evidence: [selected-api-contract.md](selected-api-contract.md), [auth-api-checklist.md](auth-api-checklist.md), [api-implementation-checklist.md](api-implementation-checklist.md).

| Method | Endpoint | Authentication metadata | Required headers | Declared responses |
|---|---|---|---|---|
| GET | /api/accounts | Bearer | None | 200 |
| POST | /api/accounts | Bearer | None | 201 |
| GET | /api/accounts/{id} | Bearer | None | 200 |
| PATCH | /api/accounts/{id}/status | Bearer | None | 200 |
| GET | /api/admin/ai/policies | Bearer | None | 200 |
| POST | /api/admin/ai/policies | Bearer | Idempotency-Key | 201, 409, 422 |
| GET | /api/admin/ai/requests | Bearer | None | 200 |
| GET | /api/admin/ai/requests/{requestId} | Bearer | None | 200, 404 |
| POST | /api/admin/ai/requests/{requestId}/reconcile | Bearer | Idempotency-Key | 202, 409 |
| GET | /api/admin/analytics/operations | Bearer | None | 200, 400, 401, 403 |
| GET | /api/admin/audit-logs | Bearer | None | 200, 400, 401, 403 |
| GET | /api/admin/audit-logs/{id} | Bearer | None | 200, 401, 403, 404 |
| GET | /api/admin/billing/quota-policies | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/admin/billing/quota-policies | Bearer | None | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/billing/quota-policies/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/discount-rules | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/admin/discount-rules | Bearer | None | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/discount-rules/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| PATCH | /api/admin/discount-rules/{id} | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/enterprise-quote-requests | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/enterprise-quote-requests/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| PATCH | /api/admin/enterprise-quote-requests/{id} | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/admin/enterprise-quote-requests/{id}/quotations | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/feedback | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| PATCH | /api/admin/feedback/{id}/status | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/knowledge-sources | Bearer | None | 200 |
| POST | /api/admin/knowledge-sources | Bearer | Idempotency-Key | 201, 409, 422 |
| GET | /api/admin/knowledge-sources/{id} | Bearer | None | 200, 404 |
| POST | /api/admin/knowledge-sources/{id}/{operation} | Bearer | If-Match | 200, 409, 412, 428 |
| POST | /api/admin/learn/media/validate | Bearer | None | 200, 422 |
| GET | /api/admin/learn/posts | Bearer | None | 200 |
| POST | /api/admin/learn/posts | Bearer | Idempotency-Key | 201, 409, 422 |
| GET | /api/admin/learn/posts/{id} | Bearer | None | 200, 404 |
| POST | /api/admin/learn/posts/{id}/versions | Bearer | Idempotency-Key | 201, 409, 422 |
| POST | /api/admin/learn/posts/{id}/{operation} | Bearer | If-Match | 200, 409, 412, 428 |
| POST | /api/admin/learn/situations | Bearer | Idempotency-Key | 201, 409 |
| GET | /api/admin/learn/versions/{versionId} | Bearer | None | 200, 404 |
| PUT | /api/admin/learn/versions/{versionId} | Bearer | If-Match | 200, 409, 412, 422, 428 |
| POST | /api/admin/learn/versions/{versionId}/publish | Bearer | If-Match | 200, 409, 412, 428 |
| GET | /api/admin/library/items | Bearer | None | 200 |
| POST | /api/admin/library/items | Bearer | Idempotency-Key | 201, 409 |
| GET | /api/admin/library/items/{id} | Bearer | None | 200 |
| PATCH | /api/admin/library/items/{id} | Bearer | If-Match | 200, 412, 428 |
| POST | /api/admin/library/items/{id}/versions | Bearer | Idempotency-Key | 201, 422 |
| GET | /api/admin/library/versions/{id} | Bearer | None | 200 |
| PUT | /api/admin/library/versions/{id} | Bearer | If-Match | 200, 409, 412, 422, 428 |
| POST | /api/admin/library/versions/{id}/publish | Bearer | If-Match | 200, 409, 412, 428 |
| GET | /api/admin/organizations/{organizationId}/ai-quota | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/organizations/{organizationId}/ai-quota/grants | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/organizations/{organizationId}/ai-usage | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/admin/payments/payos/checkouts/{id}/reconcile | Bearer | None | 202, 400, 401, 403, 404, 409, 503 |
| POST | /api/admin/quotations/{id}/issue | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/scenario-reviews | Bearer | None | 200, 400, 401, 403 |
| GET | /api/admin/scenario-reviews/{reviewId} | Bearer | None | 200, 401, 403, 404 |
| POST | /api/admin/scenario-versions/{id}/approve | Bearer | Idempotency-Key | 200, 401, 403, 404, 409 |
| POST | /api/admin/scenario-versions/{id}/reject | Bearer | Idempotency-Key | 200, 400, 401, 403, 404, 409 |
| POST | /api/admin/service-packages | Bearer | None | 201, 400, 401, 403, 404, 409, 412, 428 |
| PATCH | /api/admin/service-packages/{id} | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/support/tickets | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/admin/support/tickets/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| PATCH | /api/admin/support/tickets/{id} | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/admin/support/tickets/{id}/messages | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/ai/organization/answer | Bearer | Idempotency-Key | 202, 409, 422, 503 |
| POST | /api/ai/organization/scenario-draft | Bearer | Idempotency-Key | 202, 409, 422, 503 |
| GET | /api/ai/organization/sources | Bearer | None | 200 |
| GET | /api/ai/requests/{requestId} | Bearer | None | 200, 404 |
| POST | /api/auth/change-password | Bearer | None | 204, 400, 401 |
| PUT | /api/auth/devices | Bearer | X-Installation-Key | 200, 400, 403, 409 |
| DELETE | /api/auth/devices/{deviceUuid} | Bearer | X-Installation-Key | 204, 400 |
| POST | /api/auth/forgot-password | Anonymous | None | 202, 400, 429, 503 |
| POST | /api/auth/google/onboarding/complete | Anonymous | None | 201, 400, 403, 409, 503 |
| POST | /api/auth/login | Anonymous | None | 200, 400, 401, 403 |
| POST | /api/auth/login-firebase | Anonymous | None | 200, 400, 401, 403, 409, 429, 503 |
| POST | /api/auth/logout | Bearer | None | 200 |
| POST | /api/auth/logout-all | Bearer | None | 204, 401 |
| GET | /api/auth/me | Bearer | None | 200 |
| PATCH | /api/auth/me | Bearer | If-Match | 200, 400, 401, 409, 412, 428 |
| POST | /api/auth/refresh | Anonymous | None | 200, 401, 429 |
| POST | /api/auth/register | Anonymous | None | 201, 400, 409 |
| POST | /api/auth/register/organization | Anonymous | None | 201, 400, 409 |
| POST | /api/auth/register/trainee | Anonymous | None | 201, 400, 409 |
| POST | /api/auth/registration/request-otp | Anonymous | None | 202, 400, 409, 429 |
| POST | /api/auth/registration/verify-otp | Anonymous | None | 200, 400 |
| POST | /api/auth/resend-verification | Anonymous | None | 202, 400, 409, 429 |
| POST | /api/auth/reset-password | Anonymous | None | 204, 400, 409, 503 |
| POST | /api/auth/verify-email | Anonymous | None | 204, 400 |
| GET | /api/billing/enterprise-quote-requests | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/billing/enterprise-quote-requests | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/billing/entitlements | Bearer | None | 200, 400, 401, 403, 404, 409, 503 |
| GET | /api/billing/quotations | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/billing/quotations | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/billing/quotations/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| PATCH | /api/billing/quotations/{id} | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/billing/quotations/{id}/accept | Bearer | If-Match | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/billing/service-packages | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/billing/service-packages/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/buildings | Bearer | None | 200 |
| POST | /api/buildings | Bearer | None | 201, 400, 401, 403, 409 |
| GET | /api/buildings/{buildingId}/editor-preview | Bearer | None | 200, 400, 401, 403, 404, 503 |
| POST | /api/buildings/{buildingId}/ifc | Bearer | Idempotency-Key | 201, 400, 404 |
| GET | /api/buildings/{buildingId}/qr-codes | Bearer | None | 200, 404 |
| POST | /api/buildings/{buildingId}/qr-codes | Bearer | None | 201, 400, 404 |
| POST | /api/buildings/{buildingId}/qr-codes/{qrId}/revoke | Bearer | None | 200, 404, 409 |
| POST | /api/buildings/{buildingId}/qr-codes/{qrId}/rotate | Bearer | None | 200, 404, 409 |
| GET | /api/buildings/{buildingId}/scenarios | Bearer | None | 200, 400, 403, 404 |
| DELETE | /api/buildings/{id} | Bearer | None | 200 |
| GET | /api/buildings/{id} | Bearer | None | 200 |
| PUT | /api/buildings/{id} | Bearer | None | 200 |
| GET | /api/buildings/{id}/access | Bearer | None | 200 |
| PATCH | /api/buildings/{id}/access | Bearer | If-Match | 200 |
| DELETE | /api/buildings/{id}/participation-code | Bearer | If-Match | 200 |
| POST | /api/buildings/{id}/participation-code/rotate | Bearer | If-Match | 200 |
| POST | /api/buildings/{id}/participation/verify | Bearer | None | 200 |
| GET | /api/buildings/{id}/revisions | Bearer | None | 200, 400, 401, 403, 404 |
| POST | /api/buildings/{id}/revisions/upload-url | Bearer | Idempotency-Key | 201, 400, 401, 403, 404 |
| GET | /api/buildings/{id}/service-entitlement | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/buildings/{id}/trainings | Bearer | None | 200, 400, 404 |
| GET | /api/feedback | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/feedback | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/learn/bookmarks | Bearer | None | 200 |
| DELETE | /api/learn/bookmarks/{postId} | Bearer | None | 200 |
| PUT | /api/learn/bookmarks/{postId} | Bearer | None | 200, 404 |
| GET | /api/learn/posts | Anonymous | None | 200, 400 |
| GET | /api/learn/posts/{slug} | Anonymous | None | 200, 404, 410 |
| GET | /api/learn/situations | Anonymous | None | 200 |
| GET | /api/library/items | Bearer | None | 200 |
| GET | /api/library/items/{id} | Bearer | None | 200, 404 |
| GET | /api/library/versions/{id} | Bearer | None | 200, 404 |
| DELETE | /api/me/avatar | Bearer | If-Match | 204, 400, 401, 412, 428 |
| GET | /api/me/avatar | Bearer | None | 200, 400, 401, 404 |
| POST | /api/me/avatar/complete | Bearer | If-Match | 200, 400, 401, 409, 412, 428 |
| POST | /api/me/avatar/upload | Bearer | If-Match | 200, 400, 401, 409, 412, 428, 503 |
| POST | /api/me/avatar/upload-intent | Bearer | None | 200, 400, 401 |
| POST | /api/me/link-google | Bearer | None | 200, 400, 401, 403, 409, 503 |
| GET | /api/organizations | Bearer | None | 200 |
| POST | /api/organizations | Bearer | None | 201 |
| GET | /api/organizations/me | Bearer | None | 200, 401, 403 |
| PATCH | /api/organizations/me | Bearer | If-Match | 200, 400, 401, 403, 409, 412, 428 |
| GET | /api/organizations/me/ai-quota | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/organizations/me/ai-quota/grants | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/organizations/me/ai-usage | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/organizations/me/analytics/operations | Bearer | None | 200, 400, 401, 403 |
| GET | /api/organizations/{id} | Bearer | None | 200 |
| PATCH | /api/organizations/{id}/status | Bearer | None | 200 |
| GET | /api/payments/payos/checkouts/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 503 |
| POST | /api/payments/payos/create | Bearer | Idempotency-Key | 200, 201, 202, 400, 401, 403, 404, 409, 503 |
| GET | /api/payments/payos/requests/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 503 |
| POST | /api/payments/payos/requests/{id}/cancel | Bearer | Idempotency-Key | 200, 202, 400, 401, 403, 404, 409, 503 |
| POST | /api/payments/payos/webhook | Anonymous | None | 200, 400, 401, 403, 404, 409, 503 |
| POST | /api/playtests/handoffs/redeem | Bearer | None | 200, 401, 403, 404, 409 |
| GET | /api/playtests/{playtestId} | Bearer | None | 200, 401, 403, 404 |
| POST | /api/playtests/{playtestId}/cancel | Bearer | Idempotency-Key | 200, 400, 401, 403, 404, 409 |
| POST | /api/playtests/{playtestId}/complete | Bearer | Idempotency-Key | 200, 400, 401, 403, 404, 409, 422 |
| POST | /api/playtests/{playtestId}/events:batch | Bearer | None | 200, 401, 403, 404, 409, 422 |
| POST | /api/playtests/{playtestId}/handoffs | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 503 |
| POST | /api/playtests/{playtestId}/heartbeat | Bearer | None | 200, 401, 403, 404, 409 |
| POST | /api/playtests/{playtestId}/launch-grants | Bearer | Idempotency-Key | 200, 400, 401, 403, 404, 409, 503 |
| POST | /api/playtests/{playtestId}/launched | Bearer | None | 200, 401, 403, 404, 409, 422 |
| POST | /api/playtests/{playtestId}/start | Bearer | Idempotency-Key | 200, 400, 404, 409, 503 |
| GET | /api/processing-jobs/{jobId} | Bearer | None | 200, 400, 401, 403, 404 |
| GET | /api/processing-jobs/{jobId}/qa | Bearer | None | 200, 400, 401, 403, 404 |
| POST | /api/processing-jobs/{jobId}/retry | Bearer | None | 202, 400, 401, 403, 404, 409 |
| GET | /api/qr/{qrToken} | Bearer | None | 200, 404 |
| GET | /api/qr/{qrToken}/trainings | Bearer | None | 200, 403, 404 |
| POST | /api/releases | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409 |
| GET | /api/releases/{releaseId} | Bearer | None | 200, 401, 403, 404 |
| POST | /api/releases/{releaseId}/publish | Bearer | Idempotency-Key | 200, 400, 401, 403, 404, 409, 503 |
| POST | /api/releases/{releaseId}/revoke | Bearer | None | 204, 400, 401, 403, 404, 409 |
| GET | /api/revisions/{id} | Bearer | None | 200, 400, 401, 403, 404 |
| GET | /api/revisions/{id}/floors | Bearer | None | 200, 400, 401, 403, 404 |
| GET | /api/revisions/{revisionId}/annotations | Bearer | None | 200 |
| PUT | /api/revisions/{revisionId}/annotations | Bearer | If-Match | 200, 400, 412, 428 |
| GET | /api/revisions/{revisionId}/artifacts | Bearer | None | 200, 400, 401, 403, 404 |
| GET | /api/revisions/{revisionId}/bim-facts | Bearer | None | 200, 400, 401, 403, 404 |
| POST | /api/revisions/{revisionId}/confirm-for-training | Bearer | None | 200, 400, 401, 403, 404, 409 |
| GET | /api/revisions/{revisionId}/issues | Bearer | None | 200, 400, 401, 403, 404 |
| POST | /api/revisions/{revisionId}/process | Bearer | Idempotency-Key | 202, 400, 404, 409 |
| GET | /api/revisions/{revisionId}/processing-jobs | Bearer | None | 200, 400, 401, 403, 404 |
| GET | /api/revisions/{revisionId}/processing-logs | Bearer | None | 200, 400, 401, 403, 404 |
| POST | /api/revisions/{revisionId}/reviews | Bearer | None | 201, 400, 401, 403, 404, 409 |
| POST | /api/revisions/{revisionId}/upload-complete | Bearer | None | 204, 400, 404, 409, 410, 422, 503 |
| GET | /api/scenario-drafts/{draftId} | Bearer | None | 200 |
| PUT | /api/scenario-drafts/{draftId} | Bearer | If-Match | 204, 400, 404, 409, 412, 422, 428 |
| POST | /api/scenario-drafts/{draftId}/snapshot | Bearer | If-Match, Idempotency-Key | 201, 400, 404, 422 |
| POST | /api/scenario-drafts/{draftId}/validate | Bearer | None | 200, 400, 403, 404 |
| GET | /api/scenario-interactions/catalog | Bearer | None | 200 |
| POST | /api/scenario-versions/{id}/package-builds | Bearer | Idempotency-Key | 202, 400, 409 |
| POST | /api/scenario-versions/{id}/submit | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409 |
| GET | /api/scenario-versions/{versionId} | Bearer | None | 200, 400, 403, 404 |
| GET | /api/scenario-versions/{versionId}/review | Bearer | None | 200, 401, 403, 404 |
| POST | /api/scenarios | Bearer | Idempotency-Key | 201, 400, 404 |
| GET | /api/scenarios/{scenarioId} | Bearer | None | 200, 400, 403, 404 |
| POST | /api/scenarios/{scenarioId}/draft | Bearer | Idempotency-Key | 201, 400, 404 |
| POST | /api/scenarios/{scenarioId}/playtests | Bearer | Idempotency-Key | 201, 400, 404 |
| GET | /api/scenarios/{scenarioId}/versions | Bearer | None | 200, 400, 403, 404 |
| GET | /api/support/tickets | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/support/tickets | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| GET | /api/support/tickets/{id} | Bearer | None | 200, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/support/tickets/{id}/messages | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409, 412, 428 |
| POST | /api/training/reconcile | Bearer | None | 200, 400, 401 |
| POST | /api/training/sessions | Bearer | Idempotency-Key | 201, 400, 401, 403, 404, 409 |
| GET | /api/training/sessions/{id} | Bearer | None | 200, 401, 403, 404 |
| POST | /api/training/sessions/{id}/complete | Bearer | Idempotency-Key | 200, 202, 400, 401, 404, 409, 422 |
| POST | /api/training/sessions/{id}/continuation | Bearer | None | 200, 401, 404, 409, 503 |
| POST | /api/training/sessions/{id}/events:batch | Bearer | None | 200, 401, 404, 409, 422 |
| POST | /api/training/sessions/{id}/heartbeat | Bearer | None | 200, 401, 404, 409 |
| POST | /api/training/sessions/{id}/launched | Bearer | None | 200, 401, 404, 409, 422 |
| GET | /api/training/sessions/{id}/result | Bearer | None | 200, 401, 404, 409 |
| POST | /api/training/sessions/{id}/start | Bearer | Idempotency-Key | 200, 400, 401, 403, 404, 409, 503 |
| GET | /api/validation-runs/{validationRunId} | Bearer | None | 200, 400, 401, 403, 404 |
| GET | /health/version | Anonymous | None | 200 |
| POST | /internal/processing/jobs/{jobId}/claim | ProcessingWorker | None | 200 |
| POST | /internal/processing/jobs/{jobId}/complete | ProcessingWorker | None | 200 |
| POST | /internal/processing/jobs/{jobId}/fail | ProcessingWorker | None | 200 |
| POST | /internal/processing/jobs/{jobId}/outputs | ProcessingWorker | None | 200 |
| POST | /internal/processing/jobs/{jobId}/renew | ProcessingWorker | None | 200 |
