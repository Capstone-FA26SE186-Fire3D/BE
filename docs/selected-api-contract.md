# Checklist API được chọn — source, test, deployment

Contract hiện tại trong nhánh triển khai; dấu tick chỉ xác nhận source và test ghi dưới đây. Danh sách toàn bộ route lấy từ OpenAPI tại [api-route-inventory.md](api-route-inventory.md); số route không phải số nghiệp vụ hoàn tất.

| Task | Endpoint/contract | Code | Test HTTP/PG/fake | Shared deployment |
|---|---|---|---|---|
|1|POST/GET /api/buildings; PUT/DELETE /api/buildings/{id}; body tenant/admin, validation, archive/audit atomic|✅|✅|Binary chưa kiểm |
|2|POST /api/buildings/{id}/revisions/upload-url và /api/buildings/{buildingId}/ifc; upload-complete bind intent/ETag/size/hash/recovery|✅|✅|Migration áp; S3/binary chưa kiểm |
|3|POST /api/revisions/{revisionId}/process, /api/processing-jobs/{jobId}/retry; outbox/HTTP machine worker claim/renew/output/complete/fail|✅|✅|Migration áp; worker thật ❌ |
|4|Scenario/draft create, PUT/validate/snapshot; ETag/receipt/numbering/hash/audit; package-build job|✅|✅|Migration áp; Unity thật ❌ |
|5|confirm-for-training/reviews đúng cặp; submit/admin approve/reject content/rubric và live family|✅|✅|Migration nền đã áp; family/audit forward mới chưa áp; binary/client chưa kiểm |
|6|Playtest prepare/start, immutable pins, quota and5-minute grant|✅|✅|Migration áp; runtime thật ❌ |
|7|POST /api/releases Built; GET /api/buildings/{id}/trainings; access/participation dependencies|✅|✅|Migration áp; binary/client chưa kiểm |
|8|Feedback/support owner/admin create/list/detail/message/PATCH; receipts/ETag/paging|✅|✅|Migration áp; binary/client chưa kiểm |
|9|PayOS GET200 DTO; role/header/security metadata; relative server; OpenAPI-generated inventory/docs|✅|✅|Deployed OpenAPI chưa kiểm |
|Publish|POST /api/releases/{releaseId}/publish; paid entitlement/approval/readiness/package gate và authorized replay|✅|✅|Cờ rollout mặc định tắt; binary/provider chưa kiểm |
|Backlog|Learner start/sync/result/training analytics, real IFC/Blender/Unity pipeline|❌|❌|❌|

## Header, response và phụ thuộc

- IFC/scenario create, snapshot/package-build, process, playtest prepare/start, release Build và support create/message dùng Idempotency-Key. Retry processing dùng requestId/reason hiện có. Keys không đổi để retry cùng command.
- Draft PUT/snapshot, Building access/code mutation và support PATCH dùng If-Match; thiếu428, malformed400, stale412. Draft GET/PUT dùng xmin ETag, Building access dùng "access-N", support dùng "support-N"; không dùng ETag khác resource.
- Confirm trả reviewId cho exact revision/version/run/annotation/artifact, không dùng revision.status làm bằng chứng version khác. Technical rejection khác approval nội dung.
- Worker là machine authentication X-Worker-Key, JWT user không cấp quyền worker. Transport mặc định Http; tùy chọn RedisStreams dùng PostgreSQL outbox → Redis → BE bridge → HTTP worker. [Hướng dẫn Redis](redis-processing.md) tách Published, durable handoff/ACK và Succeeded; provider/toolchain thật chưa nghiệm thu.
- Playtest preparation không grant/quota; start pin actor/family/package/runtime, Trial consumption hoặc entitlement đúng Building. Không seed Trial tự động.
- Release Built derive metadata/provenance từ accepted ReleasePackage và Approved content/rubric; create matching Training atomic. Publish có gate paid entitlement/approval/exact readiness/package và cờ Publishing:Enabled mặc định tắt; đủ điều kiện trả204, thiếu điều kiện nghiệp vụ409, chưa bật rollout503.
- Paid/checkout/provisioning trong GET PayOS là ba trạng thái riêng. Chỉ sửa metadata GET, không thay thanh toán.

## Test tay theo dependency

1. Đăng nhập OrgUser đúng tenant hoặc admin. [Building](building-manual-test.md): create body tenant admin, validation và foreign tenant.
2. [Upload IFC](ifc-upload-manual-test.md): tính SHA-256, initiate, PUT storage, complete; thử key/hash/size sai và replay.
3. [Worker](processing-worker.md): cấu hình worker fake riêng; process trả202 bền vững, claim/output/complete đúng attempt. Không mong process202 là kết quả đã thành công.
4. [Scenario](scenario-authoring.md): GET draft ETag → PUT state v7/rubric → validate → snapshot cùng ETag/key → package build. Fake output phải accept trước readiness.
5. [Readiness/approval](scenario-readiness.md): confirm exact pair; submit lấy server hashes → admin approve; sai version/hash phải bị chặn.
6. [Playtest](playtest-manual-test.md): prepare không trừ Trial; start runtime đúng với explicit entitlementfixture; replay không trừ lần hai.
7. [Release/access](release-building-access.md): Build body4 IDs/key, thử metadata mismatch, replay; list Built không hiện cho Trainee. GET access ETag → rotate/code verify → rotate/revoke grant cũ vô hiệu. Không gỡ publish containment để tạo Published bằng test tay.
8. [Support](support-api.md): create/message replay, cross-user, detail ETag/admin transitions, stale PATCH và pageSize101.
9. Swagger GET PayOS phải hiện response200 đúng DTO; copy server-relative HTTPS origin. Kiểm binary/schema deploy trước khi kết luận runtime.

## Migration và bằng chứng

Task1–6 đã có bằng chứng trước đó tại [ifc-authoring-deployment.md](ifc-authoring-deployment.md). Tasks7–8 thêm 20261007100000_AddReleaseAndBuildingAccess và20261007110000_AddSupportCommandContracts: additive, không attest legacy, không reset. Migrate trước binary, preflight support phải giữ dữ liệu; gate EXECUTE và no direct support/release provenance DML cần kiểm lại trên target.

Tests dùng PostgreSQL native disposable với actual EF history và nonsuperuser gate-only roles, fake S3/HTTP/package/runtime. Bằng chứng Tasks7–9: release/access3 tests, support5 tests, OpenAPI1 test; release unit6 tests. Regression cuối nhánh được ghi riêng khi chạy xong. Ba migration mới, gồm bản sửa quyền20261007120000, đã áp và postcheck trên Supabase; chưa test provider/binary/client thật. Không sửa auth/OTP/Avatar/payment business hoặc WMS.

## Bản sửa sau review

IFC gate đối chiếu Building đã lưu trong receipt khi replay. HTTP handler đã hash Building ID; kiểm tra SQL bổ sung bảo vệ payload legacy hoặc caller của gate, giữ nguyên hash receipt cũ. Scenario anchor chỉ lấy từ current Geometry attempt Succeeded có validation Passed cùng job/revision/attempt và không có Error/Critical. Worker Succeeded không đồng nghĩa QA Passed. Hai thay đổi có migration forward riêng, không sửa migration đã áp hoặc dữ liệu legacy. Quyền Building access và custom runtime DML đã được sửa trước đó; bằng chứng rollout/test ghi tại [tiến độ](task-progress-checklist.md).
