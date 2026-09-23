# Fire3D BE — checklist API và thứ tự triển khai

Ngày rà soát: 19/09/2026. Đây là backlog triển khai dựa trên code và tài liệu, không phải báo cáo API đã nghiệm thu.

## 1. Phạm vi và nguồn đối chiếu

- Source BE được đọc: commit **54bc9281f75308e6700310be0a4ee8b25ab95d71**, ban đầu ở nhánh feature/be-foundation-review. API, Application handlers/contracts, Domain entities/enums, Infrastructure stores/mappings, DI, test source, migration, Docker/Render/Azure workflow và IFC spike được rà soát theo luồng.
- Hướng dẫn: AGENTS.md, .codex/context.md, workflow.md, bootstrap.md, skills-inventory.md, lessons và handoff local. Handoff cũ chỉ là lịch sử.
- Docs checkout tại máy: **64c41bff463e06bec505cb9928d03e36be912201**, schema v6.6. Sau fetch, đã đối chiếu thêm **origin/develop 5eaf152d36335f89b087bfbb243c2fc4eadd7079**, schema **v6.7**, có Redis/outbox/consumer receipt. Checklist lấy v6.7 làm thiết kế đích; chưa chuyển checkout Docs.
- Đã đọc bộ Markdown và nội dung text proposal DOCX tại checkout, đối chiếu thay đổi remote liên quan. Chưa kiểm tra bố cục render DOCX.
- Workspace hiện chỉ có BE và Docs. Chưa kiểm tra source FE, AI, Mobile/Unity; mô tả các phía đó là contract trong Docs.
- Không chạy app, migration, gọi DB Supabase, Firebase, S3, PayOS hay AI trong lượt review này. Không dùng kết quả test cũ làm bằng chứng bản hiện tại.

Nguồn sản phẩm: [requirements](../../Docs/fire_evacuation_requirements.md), [workflows](../../Docs/fire-evacuation-training-workflows.md), [technology](../../Docs/fire-evacuation-training-technology.md), [schema](../../Docs/fire_evacuation_schema.sql), [ERD](../../Docs/fire_evacuation_erd.md), [project overview](../../Docs/fire_evacuation_project_overview.md), [RAG](../../Docs/fire_evacuation_bim_rag_pccc.md), [web UX](../../Docs/fire3d-web-ux-design.md), [web implementation](../../Docs/fire3d-web-implementation.md). Liên kết tương đối mở checkout local; để xem phần v6.7 cần đối chiếu commit Docs nêu trên.

Nguồn quyền admin bổ sung đã được người dùng chốt: [platform-admin-policy.md](platform-admin-policy.md). PlatformAdmin được thao tác dữ liệu nghiệp vụ thay tổ chức và xem dữ liệu cá nhân phục vụ quản trị; vẫn dùng danh tính admin và chịu validation/lifecycle/audit.

## 2. Kết luận hiện trạng

**Có 21 operation trong controller và 1 health endpoint.** Trong đó 1 operation upload chỉ trả 501. Con số này không bao gồm Swagger/OpenAPI/static files và không có nghĩa 20 operation còn lại đã đạt acceptance.

Technology mục 9 có **38 cặp method/path đích**. Chỉ POST /api/buildings đã có route tương ứng; **37 contract còn lại chưa có route tương ứng** tại commit được đọc. Những API khác đang có như auth/accounts/list Building không nằm hết trong danh sách mẫu này.

Không lấy số endpoint để suy ra số feature hoặc phần trăm hoàn thành. Một feature IFC cần nhiều API, worker, storage và QA; một endpoint start có nhiều invariant hơn một API đọc danh sách.

Ký hiệu:
- **Có — cần rà soát**: đã có route và xử lý, chưa nghiệm thu lại.
- **Khung 501**: chưa có xử lý nghiệp vụ.
- **Thiếu — contract đích**: đường dẫn đã có trong technology mục 9.
- **Đề xuất**: năng lực còn thiếu nhưng đường dẫn/DTO do checklist đề xuất, team cần chốt trước code.
- Tất cả ô chưa tick là việc còn phải thực hiện hoặc kiểm chứng. Không tick chỉ vì có entity/controller.
- **P0**: chặn triển khai tin cậy; **P1**: luồng cốt lõi; **P2**: hoàn thiện bản cuối hoặc mở rộng sau luồng lõi. P2 không tự động loại khỏi phạm vi đồ án.

## 3. P0 — làm trước khi thêm module

| ID | Checklist | Bằng chứng / đầu ra cần có |
|---|---|---|
| F01 | [ ] Sửa role mặc định của user Firebase mới | ExchangeFirebaseTokenCommand tạo User không gán Role; EnumProperties không có initializer; UserRole bắt đầu PlatformAdmin = 0. Gán Trainee tường minh cho self-onboarding được phép; role tổ chức/admin chỉ do provisioning có quyền. Thêm test đăng nhập mới không có quyền admin. Đây là phát hiện tĩnh, chưa thử khai thác trên hệ thống chạy. |
| F02 | [ ] Sửa liên kết Firebase UID theo email | Handler tìm theo UID hoặc email rồi ghi đè FirebaseUid khác UID hiện tại. Không tự ghi đè liên kết đã tồn tại. Chốt luồng liên kết tài khoản legacy bằng danh tính đã xác minh, kiểm tra provider/email theo policy, unique UID và xử lý race first-login. TryCreateUserAsync trả false phải được xử lý. |
| F03 | [ ] Đồng bộ mô hình xác thực với Docs | Hiện Firebase token chỉ dùng đổi lấy JWT/refresh token nội bộ; middleware vẫn HMAC JWT với sub GUID và sid. Thiết kế đích là Firebase identity được BE xác minh rồi ánh xạ DB role/tenant. Chọn implementation theo đích, hoặc ghi ADR nếu giữ token exchange. Không thay thẳng middleware rồi giữ Guid.Parse(Firebase sub) vì Firebase UID không phải GUID nội bộ. |
| F04 | [ ] Sửa contract admin tạo account/bootstrap | CreateAccountRequest còn bắt password 12–128 ký tự nhưng AuthSupport.CreateAsync không hash/lưu password và không tạo credential Firebase. Chuyển provisioning thành cấp role/tenant và liên kết danh tính theo policy; không bắt nhập mật khẩu vô tác dụng. Rà soát bootstrap đầu tiên tương ứng. |
| F05 | [ ] Chuẩn hóa actor/tenant và admin override | BuildingsController/RevisionsController parse organization_id trực tiếp; admin và Trainee không có organization có thể lỗi trước khi kiểm tra quyền. Tạo actor context từ identity đã xác minh + DB; OrganizationUser chỉ tenant của mình; admin chỉ định tenant đích rõ ràng, không impersonate. Handler cũng kiểm tra quyền. |
| F06 | [ ] Chuẩn hóa validation và HTTP error | Building controller dùng Problem() chung, làm mất status/code 400/404 từ handler. Giữ mã lỗi nghiệp vụ, 401/403/404/409/422 và traceId nhất quán; validate nested location/contact, độ dài, tọa độ, null và pagination. Package FluentValidation có trong project chưa chứng minh đã có validator/pipeline thực thi. |
| F07 | [ ] Lập migration tăng dần từ DB thật sang v6.7 | Đối chiếu bảng/cột/enum/FK/index/function/role hiện hữu trước; staging và backup/rollback. Không chạy nguyên schema thiết kế hoặc migration tên AddPasswordResetTokens lên DB đang có dữ liệu: migration hiện chứa CreateTable cho nhiều bảng nền tảng, không chỉ reset token. |
| F08 | [ ] Sửa test fixture và auth regression | AuthTests vẫn gọi /api/auth/login và kiểm tra password_hash; route password login đã xóa. Viết lại auth adapter test/Firebase identity, tenant và concurrency; fixture chỉ DB tạm riêng. Không để WebApplicationFactory vô tình dùng connection application DB. Test bị skip không tính pass. |
| F09 | [ ] Đưa tích hợp Firebase ra Infrastructure | Application đang tham chiếu FirebaseAdmin và gọi singleton SDK trực tiếp. Đề xuất IFirebaseIdentityVerifier tại Application, adapter tại Infrastructure, composition root tại API. Áp dụng tương tự S3/AI/payment/clock khi cần; không bắt buộc tạo project Interface mới. |
| F10 | [ ] Giao dịch nghiệp vụ + audit + outbox | Building store hiện SaveChanges nghiệp vụ rồi ghi audit riêng. Cần transaction ngắn nguyên tử cho thay đổi có audit/event; rollback khi audit/outbox lỗi. Không giữ transaction trong lúc chờ S3/LLM/PayOS/worker. |
| F11 | [ ] Sửa cấu hình runtime/deploy lệch source | Render dùng dockerContext ./Fire3D nhưng Dockerfile giả định context root BE và WORKDIR /src/Fire3D. Render đặt Jwt__Key còn code đọc Jwt:SigningKey. Firebase khởi tạo từ firebase-admin.json; cần cấu hình secret/mount đúng môi trường. Mailgun ValidateOnStart hiện bắt cấu hình ngay cả khi không dùng reset password. Đây là rà soát tĩnh, chưa chạy deploy. |
| F12 | [ ] Cập nhật Swagger và tài liệu test | docs/authentication.md, testing-authorization.md và bằng chứng week2 có phần thuộc auth cũ. Document đúng loại token, DTO, role/tenant, ví dụ request, lỗi, prerequisite và idempotency. Bằng chứng “22 passed” lịch sử không phải kết quả commit này. |

Bằng chứng chính: [Firebase handler](../Fire3D/Fire3D.Application/Authentication/Commands/FirebaseLogin/ExchangeFirebaseTokenCommand.cs), [User role](../Fire3D/Fire3D.Domain/Entities/EnumProperties.cs), [enums](../Fire3D/Fire3D.Domain/Enums/DatabaseEnums.cs), [auth support](../Fire3D/Fire3D.Application/Authentication/Internal/AuthSupport.cs), [JWT middleware](../Fire3D/Fire3D.API/Extensions/AuthenticationExtensions.cs), [BuildingsController](../Fire3D/Fire3D.API/Controllers/BuildingsController.cs), [migration](../Fire3D/Fire3D.Infrastructure/Migrations/20260916070926_AddPasswordResetTokens.cs), [Render](../render.yaml).

## 4. Checklist 21 operation đang có

ID ở đây chỉ quản lý việc sửa/nghiệm thu source hiện hữu; POST /api/buildings được tham chiếu lại ở phần contract đích, không tính là hai API.

| ID | Method/path hiện tại | Trạng thái | Việc cần làm để nghiệm thu |
|---|---|---|---|
| E01 | POST /api/auth/login-firebase | Có — cần sửa P0 | F01–F03. Hiện body là JSON string token, chưa phải object DTO. Xác định DTO/flow mục tiêu; invalid token không tạo user; phân biệt token lỗi với dịch vụ xác thực không khả dụng; chống race first-login. |
| E02 | POST /api/auth/refresh | Có — auth legacy | Quyết định giữ/loại theo F03; nếu giữ, rotation/replay/concurrency và lifetime cấu hình phải đúng. Không có hai luồng refresh không được mô tả. |
| E03 | POST /api/auth/logout | Có — auth legacy | Chốt logout thiết bị hiện tại/toàn bộ; thu hồi session theo kiến trúc chọn và vô hiệu đăng ký push phù hợp. Không suy ra Firebase logout từ việc xóa local refresh token. |
| E04 | GET /api/auth/me | Có — cần rà soát | Trả account DB, role/organization đúng hiện hành; user/org bị khóa bị từ chối; không lộ credential. |
| E05 | PUT /api/auth/devices | Có — một phần | Actor lấy từ identity, không nhận UserId do client quyết định; validate UUID/token/metadata; upsert/rotate đồng thời; xử lý thiết bị đổi user. |
| E06 | POST /api/accounts | Có — cần sửa P0 | F04; chỉ PlatformAdmin, unique email/UID, ràng buộc role–organization, audit, không tạo credential giả. |
| E07 | GET /api/accounts | Có — cần rà soát | PlatformAdmin; phân trang/filter; DTO cá nhân tối thiểu, không token/hash; kiểm thử không leak cho role khác. |
| E08 | GET /api/accounts/{id} | Có — cần rà soát | PlatformAdmin; 404 đúng; account deleted/inactive theo policy; DTO được phép. |
| E09 | PATCH /api/accounts/{id}/status | Có — cần rà soát | Khóa/mở khóa, invalidate authorization hiện hành; bảo vệ admin cuối; audit transaction; cập nhật test theo Firebase. |
| E10 | POST /api/organizations | Có — cần rà soát | PlatformAdmin; slug/name hợp lệ, duplicate 409, audit nguyên tử. |
| E11 | GET /api/organizations | Có — cần rà soát | PlatformAdmin; phân trang/filter, không mở cho OrganizationUser chỉ để FE tiện gọi. |
| E12 | GET /api/organizations/{id} | Có — cần rà soát | PlatformAdmin; 404 và DTO đúng. Nhu cầu org tự xem hồ sơ dùng contract riêng có scope. |
| E13 | PATCH /api/organizations/{id}/status | Có — cần rà soát | Khóa org chặn nghiệp vụ mới/cấp account mới; test race với login/start/publish; không xóa lịch sử đã ghi. |
| E14 | POST /api/buildings | Có — cần sửa | F05/F06/F10; tạo Building thuộc tenant được phép, location/contact hợp lệ. |
| E15 | GET /api/buildings | Có — cần sửa | Filter/pagination đã có; kiểm thử tenant, admin target scope và account/org inactive. |
| E16 | GET /api/buildings/{id} | Có — cần sửa | Truy xuất đúng tenant, lỗi 404/403 có chủ đích; không dùng Guid.Parse thiếu claim. |
| E17 | PUT /api/buildings/{id} | Có — cần sửa | Quyền + validation + ETag/version chống mất cập nhật; thay geometry không ghi đè revision lịch sử. |
| E18 | DELETE /api/buildings/{id} | Có — archive mềm | Hiện chỉ đặt IsActive=false. Document archive, không mô tả xóa vật lý; chốt hậu quả với start/publish và cách restore. |
| E19 | POST /api/buildings/{id}/revisions/upload-url | Khung 501 | Thay bằng/định tuyến tương thích với D02; không làm hai luồng upload độc lập. Cần S3 private và kiểm tra file thực sau upload. |
| E20 | GET /api/buildings/{id}/revisions | Có — metadata | Scope, sort/pagination khi tăng dữ liệu, trạng thái/summary; chưa đại diện pipeline xử lý IFC. |
| E21 | GET /api/revisions/{id} | Có — metadata | Scope/role/error, nguồn/hash/status đúng; artifacts/issues/jobs là contract bổ sung. |

Các operation trên đều còn cần nghiệm thu: [ ] E01–E05 auth/devices; [ ] E06–E13 administration; [ ] E14–E21 Building/revision.

GET /health đang có nhưng chỉ AddHealthChecks() không đăng ký kiểm tra dependency: xem O01 ở phần vận hành.

## 5. Checklist 38 contract đã nêu trong technology mục 9

Quyền viết tắt: **O** = OrganizationUser đúng tenant; **T** = Trainee; **A** = PlatformAdmin; **W** = adapter/hệ thống có credential riêng; **Public** = chưa đăng nhập. A thao tác thay O phải chọn tenant đích, ghi audit chính danh và tuân thủ entitlement/QA/lifecycle; không mặc nhiên bypass billing hoặc sửa snapshot.

### 5.1 Building, IFC, processing và QA — P1

Nguồn: FR-BUILD, FR-IFC, FR-PROCESS; technology 9, 14.3.1; workflows BIM pipeline.

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D01 | [ ] POST /api/buildings | O/A | Nghiệm thu E14. Không tạo thêm controller trùng. |
| D02 | [ ] POST /api/buildings/{buildingId}/ifc | O/A | Chốt upload initiation/receive contract; IFC-only, hạn mức thử, size/hash, private S3, revision/source ownership. Client không tùy chọn object key của tenant khác. Trả ID + trạng thái; nếu presigned flow phải có finalize (P08). |
| D03 | [ ] POST /api/revisions/{revisionId}/process | O/A | Chỉ source đã xác nhận/quarantine đạt; job input hash + idempotency; job và ProcessingJobRequested outbox cùng transaction; trả 202/jobId. Không convert trong HTTP request. |
| D04 | [ ] GET /api/revisions/{revisionId}/issues | O/A | Issue severity/object/floor/provenance theo validation run; phân biệt lỗi critical/error với warning. Không chỉ lấy revision_issues legacy rồi coi đủ v6.7. |
| D05 | [x] GET /api/buildings/{buildingId}/editor-preview | O/A | revisionId query bắt buộc; current Geometry attempt thành công; metadata cùng artifact; URL TTL 5 phút; Ready/NotReady. Xem giới hạn kiểm thử trong ifc-api-progress.md. |
| D06 | [ ] POST /api/processing-jobs/{jobId}/retry | O/A | Gate requeue; key mới chỉ Failed → Queued + outbox. Cùng key/envelope replay AlreadyRequeued dù job tiến trạng thái; khác envelope Conflict; Cancelled terminal. |
| D07 | [ ] GET /api/processing-jobs/{jobId}/qa | O/A | Job/attempt hiện hành và validation/artifact hash khớp; không trình bày QA của attempt cũ như kết quả mới. |
| D08 | [ ] GET /api/validation-runs/{validationRunId} | O/A | Trả summary/issues/provenance đúng tenant và runtime/toolchain; trạng thái chưa xong không báo Passed. |

Dữ liệu: buildings, building_locations, building_contacts, building_floors, revisions, source_documents, processing_jobs, processing_job_attempts, revision_processing_logs, revision_artifacts, bim_facts, validation_runs, validation_issues, integration_outbox_events, integration_event_consumptions.

### 5.2 Scenario editor, version và playtest — P1

Nguồn: FR-SCENARIO, FR-COMPAT; workflows authoring/playtest. Editor thao tác trên draft; snapshot immutable.

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D09 | [ ] POST /api/scenarios | O/A | Tạo logical scenario gắn Building/revision hợp lệ; nhiều scenario dùng lại geometry. |
| D10 | [ ] POST /api/scenarios/{scenarioId}/draft | O/A | Tạo draft từ revision/version được phép; pin schema và base version; retry không tạo draft ngoài ý muốn. |
| D11 | [ ] PUT /api/scenario-drafts/{draftId} | O/A | ETag/version chống overwrite; validate spawn, goals, hazards/items, routing_config, scoring_config, time_limit_seconds, IFC anchors và capabilities. Không cho client nhúng executable script. |
| D12 | [ ] POST /api/scenario-drafts/{draftId}/snapshot | O/A | Snapshot append-only với canonical hash; retry cùng key/payload trả version cũ, payload khác 409; version không sửa tại chỗ. |
| D13 | [ ] GET /api/scenario-interactions/catalog | O/A | Chỉ capability runtime hỗ trợ, version/schema/constraints; catalog server quản lý. Cache có version, không dùng client tự khai báo capability làm bằng chứng tương thích. |
| D14 | [ ] POST /api/scenarios/{scenarioId}/playtests | O/A | Prepare riêng, pin draft/version/package/runtime; chưa cấp launch grant; không qua QR Trainee, không ghi learner analytics. |
| D15 | [ ] POST /api/playtests/{playtestId}/start | O/A | Kiểm tra online Trial còn hạn/quota hoặc entitlement Active, identity, package/manifest/QA/runtime; grant và quota start nguyên tử/idempotent; hết hạn sau start không cắt phiên. |
| D16 | [ ] POST /api/revisions/{revisionId}/confirm-for-training | O/A | Body xác định scenarioVersion/candidate package/validation cụ thể; QA và provenance khớp. Chỉ xác nhận readiness cặp revision–version; không khóa toàn revision hoặc tự publish. |

Dữ liệu: scenarios, scenario_drafts, scenario_versions, annotation_sets, revision_reviews, playtest_sessions, release_packages, runtime_compatibility_catalog, validation_runs, service_entitlements. Không sử dụng sessions Trainee như playtest mà không có phân loại và invariant tách biệt.

### 5.3 Release, Training và QR — P1

Nguồn: FR-RELEASE, FR-BILLING, FR-COMPAT. Cần billing entitlement trước khi nghiệm thu publish thật.

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D17 | [ ] POST /api/releases/{releaseId}/publish | O/A | Release Built, confirm đúng version, QA không còn lỗi chặn, package/artifact/validation/hash/build target khớp, runtime catalog hỗ trợ, Building entitlement Active. Publish nguyên tử + audit; retry không tạo release mới. |
| D18 | [ ] GET /api/buildings/{buildingId}/trainings | O/A hoặc T theo view | Chốt view quản trị và view learner. T chỉ thấy bài được phép/Published và metadata công khai; không lộ draft/internal artifacts. Không dùng việc có BuildingId làm quyền đọc nội bộ. |
| D19 | [ ] GET /api/qr/{qrToken} | Public | Chỉ public metadata/trạng thái/download/login links; QR canonical trỏ Building. Không trả raw IFC/private data. QR sai/revoked trả 404/410; hết service không xóa landing. |
| D20 | [ ] GET /api/qr/{qrToken}/trainings | T | Firebase identity hợp lệ, resolve Building và danh sách Published; kiểm tra QR hiện hành. Lấy danh sách không tạo session/start grant. |

Dữ liệu: releases, release_packages, trainings, release_qr_codes, service_entitlements, runtime_compatibility_catalog. ReleaseQrCode trong code còn ReleaseId/TrainingId: phải migrate thành QR cấp Building trước.

### 5.4 Training session, offline sync và result — P1

Nguồn: FR-TRAINING, FR-ANALYTICS, FR-COMPAT; technology contract session. Backend giữ lifecycle/dữ liệu; Unity mô phỏng, Mobile giữ outbox offline.

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D21 | [ ] POST /api/training/sessions | T | Chỉ preparation: pin training/release/version/QR/package/manifest/schema/build target và preparation key; chưa started_at/grant; không tính lượt học. |
| D22 | [ ] POST /api/training/sessions/{sessionId}/start | T sở hữu | Online recheck Published, QR, entitlement Active, package verify/runtime compatibility. Key + runtime payload phải khớp; cùng key khác input 409. Cấp grant và started_at đúng một lần. |
| D23 | [ ] POST /api/training/sessions/{sessionId}/heartbeat | T sở hữu | Session đã start + grant hợp lệ; thời gian server ghi PostgreSQL trước ACK; không để client timestamp hoặc Redis tự quyết định active. |
| D24 | [ ] POST /api/training/sessions/{sessionId}/events:batch | T sở hữu | Batch giới hạn, eventId ổn định/sequence/schema, pinned release/hash; duplicate cùng payload không nhân event, conflict khác payload; trả ACK rõ phần đã ghi. |
| D25 | [ ] POST /api/training/sessions/{sessionId}/complete | T sở hữu | Trạng thái kết thúc hợp lệ + event/result evidence + duration/rubric version; không tin điểm client tùy ý. Idempotent, không ghi đè kết quả đã chốt; chính sách Assessment mở debrief sau nộp. |
| D26 | [ ] POST /api/training/reconcile | T sở hữu | Đồng bộ phiên đã start khi trở lại mạng; xử lý mất ACK/out-of-order/duplicate/checkpoint. Không biến preparation/offline request mới thành phiên đã start. Hết entitlement sau start vẫn tiếp nhận sync hợp lệ. |

Dữ liệu: sessions, session_events, session_results, session_checkpoints, debrief_artifacts. Kết quả người khác bị chặn kể cả biết UUID; quyền admin xem dữ liệu không đồng nghĩa được sửa snapshot điểm lịch sử.

### 5.5 Payment và Building entitlement — P1

Nguồn: FR-BILLING và FR-BILLING-RECOVERY; technology payment adapter và short transactions. Không dùng cờ isPaid do client gửi.

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D27 | [ ] GET /api/buildings/{buildingId}/service-entitlement | O/A | Trả Trial/Active/expired state, kỳ và capability/hạn mức liên quan; không dùng org plan chung thay entitlement từng Building. |
| D28 | [ ] POST /api/quotations | O/A | Phân biệt BuildingService/AIUsage; duration, currency, price/terms snapshot; validate tenant/Building/period và idempotency. |
| D29 | [ ] POST /api/payments/payos/create | O/A | Quotation hợp lệ, server tạo Pending/payment order qua gate; không giữ DB transaction chờ PayOS. Timeout có request/status reconcile, không tạo order trùng. |
| D30 | [ ] POST /api/payments/payos/webhook | W | Verify theo SDK/contract PayOS trước apply; đối chiếu order/amount/currency/snapshot; duplicate/replay idempotent; giả mạo bị từ chối. Endpoint không dùng Firebase user token nhưng không phải thao tác vô điều kiện. |
| D31 | [ ] POST /api/payments/{transactionId}/reconcile | O/A hoặc W có scope | Đối soát trạng thái tin cậy, sửa tình huống Applied nhưng chưa provision bằng deterministic key; không cấp hai kỳ dịch vụ. AIUsage payment chỉ chốt kỳ AI, không cấp Building service. |

Dữ liệu: service_packages, quotations, payos_payment_requests, payment_transactions, invoice_metadata, service_entitlements, payment_provisioning_records. ReturnUrl/cancelUrl chỉ điều hướng.

### 5.6 AI/RAG và accounting — P1, triển khai sau identity/quota nền

Nguồn: FR-AI, FR-BILLING-05/06, FR-AI-RECOVERY và RAG. Public client chỉ gọi .NET; FastAPI không được tự tính tiền/cấp quota/publish.

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D32 | [ ] GET /api/organizations/{organizationId}/ai-usage | O/A | Usage/quota/reserved/ước tính phí theo kỳ, filter Building/user/request type trong scope; không tính lại lịch sử theo giá mới. |
| D33 | [ ] POST /api/organizations/{organizationId}/ai-overage-consents | O/A | Consent có actor, policy/version/price scope/thời điểm; idempotency; không mặc định đồng ý vì gửi câu hỏi. |
| D34 | [ ] GET /api/ai/requests/{requestId} | Chủ request/O được cấp/A | Trạng thái và kết quả đã lưu, citations/provenance/usage kỹ thuật; tenant/user-scoped. Là đường tra cứu bắt buộc khi timeout. |
| D35 | [ ] POST /api/ai/usage/{requestId}/reconcile | O/A hoặc W có scope | Đối soát theo evidence, settle/release reservation đúng một lần. Internal recovery vẫn xử lý request Accepted trước khi user bị khóa; không mở API cho user đã bị khóa. |
| D36 | [ ] POST /api/ai/organization/scenario-draft | O/A | Authorize tenant/Building/revision, create request + reserve quota ngắn, gọi AI ngoài transaction; citations + BIM anchors, NeedsUserEdit. Không tự cập nhật scenario/editor hoặc publish. |
| D37 | [ ] POST /api/ai/trainee/answer | T | Quota ngày theo user, chỉ corpus chung đã duyệt/nội dung được phép/kết quả chính mình; không dùng quota organization. Thiếu evidence trả InsufficientEvidence, safety rejection có trạng thái rõ. |

Dữ liệu: ai_requests, ai_policy_versions, ai_quota_grants, ai_usage_reservations, ai_usage_reservation_allocations, ai_usage_ledger, ai_overage_consents, ai_billing_periods, ai_billing_period_items, ai_billing_adjustments, knowledge_sources, knowledge_chunks, bim_facts.

Request idempotency gồm canonical input + identity/audience/scope/source/policy; cùng key khác input là conflict. Terminal result/policy bất biến. Lock order: period nếu có → request → ledger/reservation → grants theo ID tăng dần. Không dùng SKIP LOCKED để kết luận hết quota.

### 5.7 Feedback — P2 theo thứ tự triển khai

| ID | Checklist API | Quyền | Acceptance riêng |
|---|---|---|---|
| D38 | [ ] POST /api/feedback | User xác thực | Nội dung hợp lệ, reference session/building nếu có phải được phép, actor từ identity, trạng thái Submitted/audit; không cho gửi feedback dưới user khác. Nguồn FR-SUPPORT/FR-AUDIT. |

## 6. API bổ sung để các luồng chạy đủ — đường dẫn đề xuất

Những hàng dưới **chưa phải đường dẫn đã chốt trong technology**. Có thể gộp vào response/API đã có nếu contract đáp ứng; không cần tạo endpoint riêng cho mỗi bảng. Mỗi ID là một capability để team review, không dùng tổng số hàng này như cam kết số endpoint cuối cùng.

### 6.1 Identity, tổ chức và Building

- [ ] **P01 — DELETE /api/auth/devices/{deviceUuid}**: user hiện tại revoke installation/push token; idempotent; đăng xuất/đổi user không gửi push nhầm người. Sửa sender để xử lý token invalid/unregistered; không log nguyên FCM token.
- [ ] **P02 — PATCH /api/auth/me**: nếu sản phẩm cho sửa tên/profile, chỉ các trường profile cho phép; role, tenant và Firebase UID không sửa qua self-profile.
- [ ] **P03 — PATCH /api/accounts/{id}/authorization**: admin cấp/đổi role–organization có audit, concurrency và last-admin guard; không chỉnh role bằng generic entity update.
- [ ] **P04 — GET /api/organizations/me**: O tự xem org của mình phục vụ dashboard; có thể gộp metadata vào /auth/me; không mở API list toàn bộ org.
- [ ] **P05 — PATCH /api/organizations/{id}**: admin cập nhật thông tin org được phép; tách khỏi status; validate slug/name/concurrency.
- [ ] **P06 — PATCH /api/buildings/{id}/status**: archive/restore có policy phụ thuộc release/session; hợp nhất ngữ nghĩa với E18, không xóa cascade dữ liệu học tập.
- [ ] **P07 — GET/PUT /api/buildings/{id}/floors**: nếu cần hồ sơ tầng do người dùng nhập; phân biệt building_floors với tầng/facts trích IFC thuộc revision. Không sửa source geometry qua CRUD tầng.

Không bổ sung lời mời thành viên/role thứ tư hoặc forgot-password nội bộ chỉ từ thói quen CRUD: Docs mới dùng Firebase Google. Mailgun có thể dùng notification nghiệp vụ, nhưng không đồng nghĩa cần thêm password_reset_tokens cho Google Sign-In.

### 6.2 Hoàn tất IFC và editor

- [ ] **P08 — POST /api/revisions/{revisionId}/upload-complete**: cần nếu D02 cấp presigned URL. Verify object tồn tại, ownership/key/size/hash/content/quarantine trước Uploaded; cùng upload finalize nhiều lần không tạo source/revision trùng.
- [ ] **P09 — GET /api/revisions/{revisionId}/processing-jobs** và **GET /api/processing-jobs/{jobId}**: FE polling status/progress/attempt/error; trả trạng thái durable PostgreSQL, không chỉ push notification.
- [ ] **P10 — GET /api/revisions/{revisionId}/processing-logs**: paging/log đã làm sạch; không lộ filesystem, secret, signed URL dài hạn hoặc log tenant khác.
- [ ] **P11 — GET /api/revisions/{revisionId}/artifacts** và **GET /api/revisions/{revisionId}/bim-facts**: authorized artifact descriptors/facts có provenance; có thể trả qua editor-preview để giảm round trip.
- [x] **P12 — GET/PUT /api/revisions/{revisionId}/annotations**: overlay nhãn/ghi chú IFC, ETag/version append-only, validate anchor cùng revision; transaction annotation + audit. Không nhận thay geometry/exit. Kiểm thử local không thay thế xác minh schema/quyền Supabase.
- [ ] **P13 — GET /api/buildings/{buildingId}/scenarios** và **GET /api/scenarios/{scenarioId}**: danh sách/detail authoring đúng tenant, version/revision liên quan; cần cho editor mở lại.
- [ ] **P14 — GET /api/scenario-drafts/{draftId}**: nạp lại draft và version/ETag để tiếp tục sửa; AI output chưa được accept không giả thành draft đã lưu.
- [ ] **P15 — GET /api/scenarios/{scenarioId}/versions** và **GET /api/scenario-versions/{versionId}**: lịch sử/read-only snapshot; tránh API PUT version đã publish.
- [ ] **P16 — POST /api/scenario-drafts/{draftId}/validate**: chạy validation cấu hình/geometry/capability; nếu tốn thời gian trả job/run để polling, không tự confirm-for-training.
- [ ] **P17 — POST /api/ai/requests/{requestId}/review**: accept/edit/reject AI suggestion với actor và audit; accept vẫn đi qua validate draft + snapshot; không tự publish. Có thể tích hợp vào D11 nếu lưu được provenance/review decision.
- [ ] **P18 — POST /api/revisions/{revisionId}/reviews**: ghi Rejected cho cặp revision–version và lý do. Không đổi toàn geometry dùng chung sang Rejected chỉ vì một scenario bị từ chối.

### 6.3 Package, release, QR và training authoring

- [ ] **P19 — POST /api/scenario-versions/{versionId}/package-builds**: yêu cầu Unity build job idempotent; artifact/validation/runtime/toolchain pinned. Có thể phát sinh tự động từ snapshot/validate nếu workflow chốt như vậy.
- [ ] **P20 — POST /api/releases** và **GET /api/releases/{releaseId}**: tạo/đọc Built release từ package đã validate, không nhận URL/hash tự khai tùy ý. Có thể tạo nội bộ sau worker accept; phải chốt để D17 có release đầu vào.
- [ ] **P21 — POST /api/buildings/{buildingId}/trainings** và **PATCH /api/trainings/{trainingId}**: tạo/chỉnh cấu hình training hợp lệ, status/mode/release/version, policy debrief; không đổi pin phiên đã start.
- [ ] **P22 — POST /api/releases/{releaseId}/revoke**: thu hồi có lý do/audit; ngăn cấp quyền mới, giữ lịch sử/pin và áp dụng policy continuation đã chốt; không chạy lại publish gate để được revoke.
- [ ] **P23 — POST/GET /api/buildings/{buildingId}/qr**: cấp/đọc metadata QR canonical. Đề xuất trả token gốc tại thời điểm cấp/rotate, DB giữ hash; chốt cơ chế in lại với FE, không hứa khôi phục token từ hash.
- [ ] **P24 — POST /api/buildings/{buildingId}/qr/rotate** và **DELETE /api/buildings/{buildingId}/qr/{qrId}**: O/A, thu hồi QR cũ; canonical uniqueness và cache invalidation sau commit; không đổi Building của QR đã cấp.
- [ ] **P25 — GET /api/training/sessions/{sessionId}/package** và **GET /api/playtests/{playtestId}/package**: scoped signed manifest/content URLs TTL + hashes/build target/runtime requirements; có thể gộp trong prepare response. Không phát raw IFC cho T, không cấp launch grant ở đây.

### 6.4 Resume, playtest telemetry, kết quả và dashboard

- [ ] **P26 — POST /api/playtests/{playtestId}/events:batch**, **/complete**, **/reconcile**: telemetry/results riêng của playtest, owner + grant, idempotency, không ghi learner plays/completion. Có thể reuse service nội bộ, vẫn tách authorization/session kind.
- [ ] **P27 — PUT /api/training/sessions/{sessionId}/checkpoint** và **POST /api/training/sessions/{sessionId}/resume**: phiên đã start, checkpoint/schema/hash hợp lệ, bảo vệ resume sau crash theo policy; không mở một start offline mới. Mốc/checkpoint/resume grant chốt cùng Mobile/Unity.
- [ ] **P28 — GET /api/training/sessions/{sessionId}** và **GET /api/training/sessions/{sessionId}/result**: chủ phiên xem status/score/debrief được phép; org chỉ aggregate trừ use case được cấp; admin truy cập dữ liệu có audit.
- [ ] **P29 — GET /api/me/training-results**: T xem lịch sử của mình, pagination/filter; không nhận arbitrary userId để vượt ownership.
- [ ] **P30 — GET /api/buildings/{buildingId}/analytics**: O/A, unique trainee/plays/active/completion/duration đúng định nghĩa Docs; loại preparation/playtest, sync chưa xác nhận chưa tính hoàn tất.
- [ ] **P31 — GET /api/organizations/{organizationId}/analytics**: aggregate nhiều Building đúng tenant, cùng metric definitions. Export/cohort/heatmap mở rộng là giai đoạn sau, không làm trước dữ liệu session chuẩn.

### 6.5 Đủ UI billing/AI và nội dung Learn

- [ ] **P32 — GET /api/service-packages**: bảng gói/giá/terms đang áp dụng; admin tạo version giá qua contract quản trị riêng nếu cần UI. Đổi giá không sửa quotation/usage lịch sử.
- [ ] **P33 — GET /api/quotations/{quotationId}**, **GET /api/payments/{transactionId}**, **GET /api/organizations/{organizationId}/payments**: scope, status và receipt để UI polling sau PayOS redirect; không coi redirect là bằng chứng trả tiền.
- [ ] **P34 — GET /api/organizations/{organizationId}/ai-billing-periods** và **GET /api/ai-billing-periods/{periodId}**: kỳ/item/adjustment/invoice snapshot read-only theo scope; chưa Closed thì thể hiện tạm tính.
- [ ] **P35 — POST /api/ai/organization/answer**: hỏi đáp BIM/kiến thức của O có scope/citations; D36 chỉ scenario-draft chưa bao trùm chức năng hỏi đáp. Reuse request/quota/reconcile pipeline.
- [ ] **P36 — GET /api/me/ai-quota** và **GET /api/me/ai-requests**: quota ngày/lịch sử AI của T; không trừ org grant.
- [ ] **P37 — API admin policy/quota và trial configuration**: đề xuất /api/ai/policies, /api/ai/quota-grants; actor A, versioned policy, không sửa snapshot lịch sử hoặc trực tiếp tăng counter bỏ qua ledger. Giá/quota cụ thể chốt theo project overview trước production.
- [ ] **P38 — GET /api/learn/articles** và **GET /api/learn/articles/{slug}**: Public đọc/tìm bài đã duyệt có source/version. Có thể dùng nội dung tĩnh/CMS từ FE; nếu thế không xây BE CRUD không cần thiết.
- [ ] **P39 — PUT/DELETE /api/me/saved-articles/{articleId}**, **GET /api/me/saved-articles**: yêu cầu lưu bài của web; cần thiết kế bảng bookmark mới nếu BE sở hữu, vì schema hiện chưa có bảng chuyên biệt.
- [ ] **P40 — Quản trị knowledge source/ingestion**: đề xuất POST /api/knowledge/sources, POST /api/knowledge/sources/{id}/ingest, PATCH /api/knowledge/sources/{id}/status. A quản lý corpus chung được duyệt; O chỉ tài liệu riêng được phép. Async ingestion/index version, status polling, citations không mất provenance; quyền service indexing tối thiểu.

### 6.6 Support và audit — hoàn thiện sau các luồng lõi

- [ ] **P41 — GET /api/feedback** và **PATCH /api/feedback/{id}/status**: admin/owner view theo policy, Submitted → Reviewed → Closed, audit và pagination.
- [ ] **P42 — POST/GET /api/support-tickets**, **GET /api/support-tickets/{id}**, **POST /api/support-tickets/{id}/replies**: người gửi xem ticket của mình, admin phản hồi; xác định model lưu reply vì support_tickets một bảng chưa chứng minh có conversation history.
- [ ] **P43 — GET /api/audit-logs**: admin và org view được cấp, filter thời gian/entity/actor/correlation, pagination và redaction; không public generic SQL query hoặc trả token/credential trong old_values/new_values.

## 7. Công việc nội bộ bắt buộc, không cộng thành API client

| ID | Checklist | Contract / kiểm chứng |
|---|---|---|
| W01 | [ ] Outbox enqueue + dispatcher Redis Streams | Nghiệp vụ và outbox cùng transaction; tenant allowlist ProcessingJobRequested/schema 1 và payload job_id đúng aggregate; SystemNotification/PlatformCacheInvalidation qua executor riêng; ProcessingJobRequeue chỉ gate requeue. Event không chứa worker lease. |
| W02 | [ ] Dispatcher claim/renew/mark/fail/replay | Lease dispatcher riêng; SKIP LOCKED dùng cho hàng đợi outbox; mark Published không có nghĩa consumer xong; replay cùng envelope, stale token bị chặn, không trim mất cửa sổ recovery. |
| W03 | [ ] Worker claim/renew/accept/fail | Backend cấp attempt/lease sau khi nhận job; job→attempt lock order; stale result bị chặn, artifact/validation/input/toolchain phải khớp. SQL gates đã có ở thiết kế; HTTP /internal/... nếu dùng là contract cần chốt, không public cho O/T. |
| W04 | [ ] Consumer transaction + receipt + ACK | Đối chiếu outbox envelope/schema/hash/scope, kiểm tra receipt trước tác động, commit tác động và integration_event_consumptions rồi ACK. Worker/AI không được tự ghi receipt ngoài executor được cấp. Test crash trước/sau commit và mất ACK. |
| W05 | [ ] IFC/Blender/Unity worker adapter | Spike CLI không phải production pipeline. Cần private input access, artifact upload/hash, structured failure và QA; worker không tự publish hoặc ghi billing. |
| W06 | [ ] AI request/result + reservation/settlement | .NET tạo Accepted, reserve allocations, gọi AI ngoài TX; record result qua gate kiểm soát rồi settle một lần. Timeout giữ NeedsReconcile; background recovery không tạo charge mới. |
| W07 | [ ] Close/invoice/pay AI period | Open → Closed → Invoiced → Paid; close tạo item snapshot nguyên tử; late usage qua adjustment có actor/lý do/idempotency. Accounting executor, không mở quyền DML trực tiếp cho AI. |
| W08 | [ ] Payment provisioning/reconcile scheduler | Payment Applied và deterministic provisioning; retry/restart không cấp quyền hai lần; AIUsage settlement tách BuildingService. |
| W09 | [ ] Notification delivery | FCM token rotation/revoke/cleanup; notification chỉ báo trạng thái, client đọc lại API authoritative; Mailgun cho use case email thật sự được yêu cầu. Retry có giới hạn và không log token. |
| W10 | [ ] Redis cache-aside | Environment/tenant/user/version trong key khi cần, TTL/invalidation sau commit, chống stampede. Cache không cấp start/publish/quota/payment và không thay DB result/heartbeat. Redis lỗi fallback DB có giới hạn tải. |

Không tạo generic endpoint cho client enqueue event, ghi ledger, reserve quota, sửa entitlement hoặc upload “worker result” rồi tự publish. Đó là quyền backend/service riêng.

## 8. Checklist database cần đi cùng API

Schema v6.7 có **56 CREATE TABLE**. EF hiện có **37 ToTable trong DbContext chính + 1 auth_refresh_tokens trong configuration riêng = 38 mapping bảng**. Đây là đối chiếu source, không phải số bảng DB Supabase đang chạy.

**22 bảng thiết kế chưa có mapping tương ứng trong DbContext hiện tại**:

- BIM/editor/QA/runtime: bim_facts, scenario_drafts, validation_issues, playtest_sessions, runtime_compatibility_catalog.
- Job/event: processing_job_attempts, integration_outbox_events, integration_event_consumptions.
- Building payment: service_entitlements, payment_provisioning_records.
- AI: ai_billing_periods, ai_quota_grants, ai_policy_versions, ai_overage_consents, ai_requests, ai_usage_ledger, ai_billing_period_items, ai_billing_adjustments, ai_usage_reservations, ai_usage_reservation_allocations, knowledge_sources, knowledge_chunks.

EF còn revision_floors, revision_issues, password_reset_tokens và auth_refresh_tokens ngoài tập tên bảng CREATE TABLE v6.7. Không xóa ngay chỉ vì tên không khớp: cần migration mapping/backfill theo DB thật và quyết định auth.

| ID | Checklist dữ liệu | API phụ thuộc |
|---|---|---|
| DB01 | [ ] Identity UID unique + role/tenant/inactive constraints; migration account legacy không tự link đè | E01–E13, mọi API protected |
| DB02 | [ ] Revision/source/floors/facts, private object keys/hash/quarantine, artifact provenance | D02–D08, P08–P12 |
| DB03 | [ ] Job attempts/lease/current attempt/input hash + outbox/receipts và DB executor grants | D03/D06, W01–W05 |
| DB04 | [ ] Draft mutable/version immutable; routing_config/scoring_config/time_limit_seconds/hash thống nhất | D09–D16 |
| DB05 | [ ] QR cấp Building, release/package/manifest/runtime compatibility catalog, playtest/session pins | D14–D26, P19–P27 |
| DB06 | [ ] Per-Building entitlement, Trial quota, quotation/payment/provisioning deterministic keys | D15/D17/D22, D27–D31 |
| DB07 | [ ] AI request/ledger/reservation allocations/grants/consent/period/item/adjustment và lock order | D32–D37, W06/W07 |
| DB08 | [ ] pgvector model/dimension/index scope, source/chunk version và ingestion permissions | P35/P40, D36/D37 |
| DB09 | [ ] EventId/sequence/session-owner uniqueness, started_at/server heartbeat/complete/result immutability | D21–D26, P26–P31 |
| DB10 | [ ] Thiết kế lưu saved articles/support replies nếu BE sở hữu; không giả định đã có trong v6.7 | P39/P42 |
| DB11 | [ ] Kiểm thử constraint/function/GRANT với đúng runtime roles, không chỉ superuser | Tất cả invariant DB |
| DB12 | [ ] Phân trang/index/query plan phù hợp workload; retention/backup/recovery gate theo quyết định team | API list, analytics, audit, jobs |

Một số bảng trùng tên vẫn khác thiết kế: ReleaseQrCode còn pin release/training; ProcessingJob/ValidationRun thiếu mô hình attempt mới; ScenarioVersion cần hợp nhất config; Session/package thiếu các pin/compatibility fields mới. So tên bảng chỉ là bước đầu.

database_overview.md còn trình bày nhóm bảng/lịch sử v6 để họp; không dùng riêng số bảng ở đó làm migration plan. Nguồn đích là schema/ERD + technology cùng commit, có kiểm tra runtime riêng.

## 9. Vận hành, Swagger và Definition of Done

- [ ] **O01**: phân biệt /health liveness hiện tại với readiness có DB/config thiết yếu; endpoint readiness đề xuất /health/ready. Không coi process còn sống là Firebase/Supabase/S3 sẵn sàng.
- [ ] **O02**: config mẫu không secret, Firebase credential injection, connection pool, CORS, forwarded headers chỉ trust proxy đã cấu hình; Nginx topology/BE provider chưa chốt trong Docs.
- [ ] **O03**: sửa Render context/JWT key nếu dùng; workflow Azure hiện build/deploy image nhưng chưa có bước chạy bộ regression auth/business. Không gọi “CI test pass” từ việc build image thành công.
- [ ] **O04**: tracing/correlation từ API → outbox/job/AI/payment, log scrubbed, rate limits cho auth/AI/upload; raw IFC và token không đi vào log.
- [ ] **O05**: mỗi operation Swagger ghi role, tenant source, prerequisite, request/response example, field bắt buộc, status/error code và idempotency/concurrency; không chỉ summary một dòng.

Một API được tick hoàn thành khi:

1. Contract được FE/Mobile/AI/Unity liên quan review; method/path/DTO/status và nguồn authority rõ.
2. Có handler + authorization ở server + mapping/migration cần thiết; không trả 501/mock thay implementation.
3. Validate body/query/nested object, resource state và liên kết cùng tenant; xử lý ID không có, role sai, user/org khóa.
4. Thao tác side effect có audit/transaction/idempotency phù hợp; cần ETag thì kiểm thử cập nhật đồng thời.
5. Có test meaningful cho happy path và failure đặc thù: duplicate/conflict, stale lease, package mismatch, quota race, payment replay hoặc offline sync tùy module.
6. Chạy integration trên DB test tách biệt với runtime roles, không dùng production để kiểm chứng.
7. Có bằng chứng request/response hoặc test report gắn commit; test skip/mock/old report được ghi rõ.
8. Swagger và checklist cập nhật đúng trạng thái; issue có blocker/PR khi đến bước review Git.

## 10. Thứ tự triển khai đề xuất cho BE

| Đợt | Làm gì | Luồng phải chứng minh được |
|---|---|---|
| 0 | F01–F12, DB01 và kế hoạch DB02–DB12 | Firebase identity → user đúng role → admin provision → org scope; user mới không thành admin, truy cập chéo bị chặn. |
| 1 | Building + IFC initiation/finalize + durable jobs/outbox + preview/QA | O tạo Building → upload IFC private → process → đọc trạng thái/issues/preview. Worker fail/retry không sinh artifact trùng. |
| 2 | Scenario draft/version/catalog/validation + Unity build integration + Trial/playtest | Editor lưu/reload → snapshot → build/QA → playtest prepare/start → result riêng; chưa cần giả publish để demo. |
| 3 | Payment/entitlement + confirm + Built/publish + canonical QR | PayOS verified → entitlement Active → publish → QR list. Payment replay không cấp hai lần; hết service chặn publish/start mới. |
| 4 | Trainee prepare/start/package/events/complete/reconcile/results + analytics lõi | Firebase T → scan QR → chọn bài → verify package → online start → offline continue → sync → kết quả và metric đúng. |
| 5 | AI request/quota/evidence/reconcile + org draft/answer + trainee AI + usage UI | Câu hỏi đúng scope → reserve → AI có nguồn → kết quả durable; retry không trừ hai lần; draft cần user review. Có thể làm song song các đợt khác sau nền auth/DB. |
| 6 | Learn persistence/support/audit UI, analytics mở rộng và hardening tích hợp | Các chức năng bản cuối hoàn chỉnh, test cross-tenant/concurrency/recovery và benchmark liên service. |

Đây là thứ tự dependency, chưa gán deadline/người vì team 4 người chưa chia task cụ thể. Không quy đổi mỗi đợt thành một tuần khi chưa có IFC sample/worker/Unity contract và capacity.

## 11. Các quyết định cần team chốt trước khi code phần phụ thuộc

Nguồn quyết định sản phẩm còn mở vẫn là bảng trong project overview; checklist không tự đặt giá/quota/provider mới.

- Auth transition: triển khai Firebase trực tiếp hay ADR cho exchange JWT; self-onboarding chỉ Trainee; cách provision/link tài khoản admin/org cũ.
- Admin acting scope: input tenant đích ở route/header/body nào, DTO cá nhân được xem, audit reason; không cần hỏi lại việc có quyền thao tác nghiệp vụ vì người dùng đã chốt có.
- Upload: D02 nhận file hay tạo presigned upload; finalize và trạng thái quarantine; tương thích route E19 đang 501.
- Draft/snapshot/build/release/Training: chốt thao tác nào tự sinh resource, thao tác nào cần API riêng; ETag/idempotency keys và schemas cùng FE/Unity.
- Runtime: catalog capability, package/manifest/build target và bridge contract; semantics playtest telemetry/resume/checkpoint.
- Billing/AI: giá, quota thử/ngày/kỳ, overage consent, settlement/rollover/refund/cancel/retention theo project overview trước production.
- Learn/bookmark/support: phần nào static FE, phần nào BE quản lý; bổ sung model cần thiết trước API.
- Hạ tầng: BE/worker compute, Firebase/S3/Redis secrets, Redis provider/retention/TTL/recovery window; Azure cho AI không quyết định nơi chạy BE.

## 12. Bằng chứng và cách dùng checklist

Review này kiểm kê routes trong controller, đối chiếu 38 contract technology, so bảng CREATE TABLE với EF ToTable, đọc call path của auth/tenant/Building/device và so test/deploy cấu hình. Chưa chạy build/test, chưa xác minh dữ liệu hay quyền DB thực tế.

Khi tạo Jira: dùng ID F/DB/D/P/W/O, ghi mục tiêu + prerequisite + acceptance ở hàng tương ứng + source contract + test evidence cần có. P là đề xuất contract, phải review trước implementation; D là đường dẫn đã nêu trong Docs; E là source cần sửa/nghiệm thu. Không tạo hai issue implementation trùng cho E14 và D01.

Ưu tiên ticket đầu tiên: **F01 + F02 (Firebase onboarding/linking)**, sau đó **F03–F06 + DB01**, rồi **E14–E21/D02–D08** để có luồng IFC thật.
