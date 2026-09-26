# Danh sách sửa BE theo contract trong Docs

Tài liệu này là backlog đối chiếu code BE với chuẩn nghiệp vụ trong workspace [`Docs`](../../Docs/README.md). Contract gốc nằm ở [requirements](../../Docs/fire_evacuation_requirements.md), [workflows](../../Docs/fire-evacuation-training-workflows.md), [technology](../../Docs/fire-evacuation-training-technology.md), [schema SQL](../../Docs/fire_evacuation_schema.sql) và [ERD](../../Docs/fire_evacuation_erd.md). API guide mô tả hành vi source đang có; checklist này ghi phần còn phải sửa. Không coi tên entity, route hay thiết kế SQL là bằng chứng tính năng đã hoàn tất.

## Baseline và trạng thái

Source được rà tại BE `main` commit `946017d` (cũng là HEAD của nhánh tài liệu khi lập checklist). Trước khi bắt đầu mỗi work item, người thực hiện phải xác nhận lại code và remote mới nhất; status dưới đây không thay thế review source sau này.

- **GAP:** capability/contract còn thiếu hoặc source đang lệch.
- **VERIFY:** đã thấy code cho một phần contract; cần kiểm thử và/hoặc khớp schema/provider trước khi kết luận.
- **LATER:** requirement có trong Docs, triển khai sau dependency nêu trong thứ tự công việc.
- Mỗi task triển khai cập nhật checklist, API guide và bằng chứng trong PR. Chỉ cập nhật trạng thái sau khi kiểm cả quyền, tenant, trạng thái, persistence, audit/idempotency và lỗi liên quan.

## A. Database và nền tảng dùng chung

### DB-01 — P0 · GAP · Schema mapping

- **Contract/current:** Schema trong Docs là đích; code/SQL còn theo mô hình cũ, gồm `organizations.plan` và bảng reset token riêng. Chưa có dữ liệu nghiệp vụ cần chuyển.
- **Sửa code:** Lập mapping entity/enum/query/grant sang schema Docs; thay truy vấn legacy và tách reset token hash của schema đích khỏi queue email. Dựng database test mới theo schema đích; không dùng `EnsureCreated` thay SQL/gates.
- **Nghiệm thu:** Database test mới dựng được từ schema mục tiêu; enum/FK/constraint/grant và truy vấn task chạy đúng. Không chạy destructive SQL lên DB dùng chung hoặc thêm backfill cho dữ liệu không tồn tại.

### AUTHZ-01 — P0 · GAP · Actor và tenant

- **Contract/current:** Mọi thao tác phải kiểm actor, role, tenant và trạng thái resource ở BE. Một số luồng dùng `Guid.Empty` làm scope rộng hoặc dựa vào tenant claim mà chưa xác minh quan hệ resource.
- **Sửa code:** Rà controller, handler và store; lấy actor từ phiên đã xác thực, xác minh tenant qua quan hệ trong DB và áp dụng permission matrix của Docs. Bỏ fallback ID rỗng có thể tắt tenant check.
- **Nghiệm thu:** Tenant đúng được phép; tenant khác, Trainee không đủ quyền hoặc account/organization/Building không hoạt động bị từ chối. Lỗi không làm lộ resource ngoài scope; audit có đúng actor.

## B. Tài khoản và xác thực

### AUTH-01 — P0 · GAP · Đăng ký local

- **Contract/current:** [RegisterUserCommand](../Fire3D/Fire3D.Application/Authentication/Commands/RegisterUser/RegisterUserCommand.cs) hiện tạo Trainee từ email/password/fullName; chưa nhận username và chưa có self-registration OrganizationUser.
- **Sửa code:** Thêm hai luồng đăng ký theo Docs: Trainee có username; OrganizationUser có thông tin tổ chức. BE tự gán role/tenant, chuẩn hóa và bảo đảm username Trainee duy nhất không phân biệt hoa thường.
- **Nghiệm thu:** Tạo đúng role/hồ sơ; email hoặc username trùng, username sai chuẩn và request cố gửi role/tenant bị từ chối. Kiểm tra đăng ký đồng thời cùng username trên PostgreSQL test.

### AUTH-02 — P0 · GAP · Google onboarding và link

- **Contract/current:** [ExchangeFirebaseTokenCommand](../Fire3D/Fire3D.Application/Authentication/Commands/FirebaseLogin/ExchangeFirebaseTokenCommand.cs) xác minh Google nhưng tài khoản mới thành Trainee; chưa có onboarding chọn role hoặc link có chứng minh tài khoản local.
- **Sửa code:** Thêm onboarding token ngắn hạn, hoàn tất Trainee/OrganizationUser và link explicit sau khi xác thực local. Lưu hash/expiry và kết quả hoàn tất để retry cùng input trả kết quả đã commit.
- **Nghiệm thu:** Kiểm tra UID mới/đã link, email local chưa link, token hết hạn, retry cùng/khác input và hai request đồng thời. Không tạo user/organization trùng hoặc đổi role/tenant tài khoản đã link.

### AUTH-03 — P1 · GAP · Profile, ETag và avatar

- **Contract/current:** [UpdateCurrentProfileCommand](../Fire3D/Fire3D.Application/Authentication/Commands/RegisterUser/UpdateCurrentProfileCommand.cs) chỉ cập nhật full name; [OrganizationProfileController](../Fire3D/Fire3D.API/Controllers/OrganizationProfileController.cs) hiện chỉ GET. Docs yêu cầu profile revision/ETag.
- **Sửa code:** Bổ sung GET/PATCH profile, username, avatar S3 intent/complete/delete và PATCH organization profile. GET trả ETag; cập nhật yêu cầu `If-Match`; ETag cũ bị từ chối. Chỉ nhận các trường profile được phép sửa.
- **Nghiệm thu:** Lưu thành công trả ETag mới; ETag cũ không ghi đè thay đổi; không sửa được role/email/tenant/status; username, organization scope và quyền sở hữu object S3 được kiểm tra.

### AUTH-04 — P1 · VERIFY · Reset và change password

- **Contract/current:** Forgot/reset gửi qua worker Mailgun; change xác minh mật khẩu cũ. [LocalPasswordReset](../Fire3D/Fire3D.Infrastructure/Authentication/LocalPasswordReset.cs) đã khóa user, consume token, đổi hash, revoke session và ghi audit trong transaction. Bảng token BE cần đối chiếu schema đích.
- **Sửa code:** Giữ reset và change là hai luồng riêng. Hoàn tất mapping sang schema đích; kiểm tra rollback, race login/reset và mọi refresh-token family. Không coi có worker là bằng chứng Mailgun production đã gửi thành công.
- **Nghiệm thu:** Token hết hạn/dùng lại thất bại; change sai mật khẩu hiện tại thất bại; lỗi DB/audit rollback toàn bộ; session cũ bị từ chối sau reset/change. Kiểm tra PostgreSQL riêng với provider Mailgun thật.

### AUTH-05 — P1 · VERIFY · FCM device token

- **Contract/current:** AuthController có đăng ký/xóa device token và adapter FCM.
- **Sửa code:** Rà ownership theo user/installation, rotate/revoke, validate token và che token khỏi log. FCM chỉ dùng push, không dùng để đăng nhập.
- **Nghiệm thu:** Nhiều thiết bị, token invalid/replaced và user khác xóa token đều được xử lý đúng. Unit/mock không được ghi là bằng chứng FCM thật.

## C. Building, IFC, authoring và release

### IFC-01 — P0 · GAP · Outbox và worker result

- **Contract/current:** [IfcWriteStore](../Fire3D/Fire3D.Infrastructure/Ifc/IfcWriteStore.cs) ghi `payload_hash = 'hash'`; worker result phải qua gate lease-bound và receipt theo Docs.
- **Sửa code:** Sinh canonical envelope/hash đúng schema, dùng outbox entry point được phép và nối worker result qua gate kiểm tra current attempt/lease/provenance. Commit business effect và receipt trước ACK; worker không có DML trực tiếp.
- **Nghiệm thu:** Hash/envelope hợp lệ; duplicate delivery idempotent; sai hash, lease cũ hoặc attempt cũ bị từ chối; crash trước ACK replay không nhân đôi kết quả.

### IFC-02 — P1 · GAP · Readiness theo revision/version

- **Contract/current:** `ConfirmForTrainingAsync` hiện đổi trạng thái toàn revision; API không gắn confirmation với scenario version. Docs yêu cầu readiness theo cặp revision–scenario version.
- **Sửa code:** Đổi command/API/persistence/review để ghi và kiểm tra đúng cặp; release phải tham chiếu confirmation tương ứng.
- **Nghiệm thu:** Confirm một cặp không làm sẵn sàng scenario khác; version không thuộc revision hoặc tenant sai bị từ chối; audit/replay giữ đúng cặp.

### SCENARIO-01 — P1 · VERIFY · Draft ETag và snapshot

- **Contract/current:** Draft GET/PUT, ETag dựa trên xmin và snapshot/version đã có. Không cần tạo API GET draft mới.
- **Sửa code:** Giữ GET draft trả ETag; kiểm tra PUT `If-Match`, snapshot bất biến, liên kết version với đúng scenario/revision và review reject đúng cặp.
- **Nghiệm thu:** GET draft lấy ETag ban đầu; lưu với ETag hiện tại thành công; thiếu/cũ ETag bị từ chối; hai editor không ghi đè nhau; snapshot cũ không đổi.

### PLAYTEST-01 — P1 · GAP · Tenant và entitlement

- **Contract/current:** Runtime dùng [FailClosedPlaytestWriteStore](../Fire3D/Fire3D.Infrastructure/Scenarios/FailClosedPlaytestWriteStore.cs): prepare trả 503 thay vì tạo session khi entitlement gate chưa deployed; start kiểm owner. Legacy store còn source nhưng không được DI đăng ký. Launch grant vẫn chưa có.
- **Sửa code:** Dùng gate/schema entitlement đích: OrganizationUser đúng tenant, Trial còn quota hoặc entitlement Active của Building; pin version/package/runtime và cấp playtest grant/session type riêng. Bỏ nuốt lỗi và ID rỗng thay cho dữ liệu bắt buộc.
- **Nghiệm thu:** Trial còn/hết quota, Active/Expired, tenant sai, DB lỗi, thiếu version và replay đều fail-closed/đúng trạng thái. Playtest không dùng QR Trainee, không ghi learner analytics; session đã bắt đầu vẫn sync theo contract.

### RELEASE-01 — P1 · GAP · Publish gates

- **Contract/current:** Runtime dùng [FailClosedReleaseStore](../Fire3D/Fire3D.Infrastructure/Releases/FailClosedReleaseStore.cs): publish trả 503 thay vì bỏ qua gate. Create Built, GET và revoke vẫn delegate implementation hiện có.
- **Sửa code:** Tách ghi nhận build hoàn tất khỏi việc chạy Unity. Trước publish kiểm tra package/manifest hash, artifact, validation Passed, blocker, runtime compatibility, Training và entitlement theo Docs.
- **Nghiệm thu:** Provenance/review sai, QA lỗi hoặc còn blocker, runtime không tương thích, entitlement không Active đều không publish. Retry/revoke/audit đúng; create Built không bị mô tả là đã chạy Unity.

### SESSION-01 — P1 · LATER · QR và training session

- **Contract/current:** QR Building, Training list, session preparation/start, launch grant và offline continuation/sync chưa có đủ API production.
- **Sửa code:** Sau khi entitlement/release gates sẵn sàng, triển khai QR canonical cấp Building, preparation pin release/scenario/package, explicit online start cấp grant; hỗ trợ tiếp tục và sync kết quả sau mất mạng.
- **Nghiệm thu:** Preparation không cấp quyền; start mới kiểm tra online và idempotency; session đã bắt đầu tiếp tục/sync được khi mất mạng nhưng không thể mở session mới khi entitlement hết hạn.

## D. Billing, notification, AI, Learn và báo cáo

### BILLING-01 — P1 · LATER · PayOS và entitlement Building

- **Contract/current:** Chưa có API/persistence PayOS và entitlement Building production. Docs yêu cầu quotation nhiều Building, snapshot và provisioning theo từng dòng.
- **Sửa code:** Thêm quotation/item, discount/terms snapshot, payment request và webhook đã xác minh; cấp/gia hạn entitlement từng Building bằng idempotency key; reconcile nếu provision một phần lỗi.
- **Nghiệm thu:** Không cấp quyền từ return URL; webhook lặp/đến trễ/sai amount không ghi trùng; retry từng dòng không nhân đôi; kỳ từng Building độc lập.

### NOTIFY-01 — P1 · LATER · Nhắc hết hạn

- **Contract/current:** Mailgun hiện phục vụ reset; reminder dịch vụ chưa có. Docs yêu cầu web/email trước 5 ngày.
- **Sửa code:** Thêm scheduler/outbox/delivery idempotent theo entitlement, kỳ và channel; gia hạn phải vô hiệu reminder cho kỳ cũ.
- **Nghiệm thu:** Retry/duplicate không gửi trùng một kỳ/kênh; email và in-app notification cùng đúng entitlement; kiểm provider thật riêng với unit test.

### AI-01 — P1 · LATER · AI request và quota

- **Contract/current:** Chưa có luồng production BE sở hữu AI request, authorization, quota, consent, reservation/settlement và reconcile.
- **Sửa code:** Xây durable request/result qua BE và service AI; giữ quota/ledger/price snapshot ở BE; xử lý timeout/retry qua idempotency và reconcile, không cho AI ghi billing.
- **Nghiệm thu:** Test concurrent reserve, duplicate/hash conflict, timeout sau khi AI trả kết quả, consent, quota Trainee riêng và scope organization/Building.

### LEARN-01 — P1 · LATER · CMS và bookmark

- **Contract/current:** Chưa có CMS/API/persistence Learn production. Docs quy định PlatformAdmin quản trị; không có bước approve riêng.
- **Sửa code:** Thêm post/version/situation/source/bookmark; public chỉ đọc Published; Hidden không public nhưng được phép RAG, Deleted bị loại; validate provider URL; audit/idempotency và cache invalidation qua outbox.
- **Nghiệm thu:** Draft không public; version Published bất biến; Hidden/Deleted không trả public, Deleted không vào RAG; bookmark chỉ thuộc Trainee; không thêm approve route ngoài contract.

### REPORT-01 — P2 · LATER · Analytics/support/audit views

- **Contract/current:** Analytics/support/audit views chưa được xác nhận hoàn chỉnh theo từng requirement.
- **Sửa code:** Triển khai sau capability nguồn; định nghĩa plays, active sessions và Building usage theo Docs; áp tenant scope, pagination, retention và redaction.
- **Nghiệm thu:** Playtest/preparation không tính learner play; dashboard/API dùng chung định nghĩa; người dùng không đọc tenant khác; dữ liệu nhạy cảm được che.

## E. Thứ tự phụ thuộc và cách hoàn tất task

1. **DB-01, AUTHZ-01, AUTH-01–04:** schema test, tenant boundary, đăng ký/onboarding/profile/session/recovery.
2. **IFC-01, IFC-02, SCENARIO-01:** outbox/worker gate, QA provenance, draft/version/readiness.
3. **BILLING-01, NOTIFY-01, PLAYTEST-01, RELEASE-01:** entitlement Building và các gate phụ thuộc entitlement; song song hoàn thiện package/QA.
4. **SESSION-01:** QR, Training, launch grant và offline sync sau khi release/entitlement đạt.
5. **AI-01, LEARN-01, REPORT-01:** tích hợp các capability còn lại theo quyền và schema đã chốt.

Task chỉ hoàn tất khi contract, handler/store/schema/gate và role/tenant đúng; có kiểm tra happy path cùng lỗi/race/replay phù hợp trên database test; tài liệu API phản ánh source mới; và PR ghi lệnh, kết quả, phần bị mock/bỏ qua, cùng giới hạn provider. Không coi build, route tồn tại hoặc mock test là bằng chứng provider/production đã hoạt động. Đợt đồng bộ này chỉ cập nhật BE docs; không sửa bộ `Docs` chuẩn.
