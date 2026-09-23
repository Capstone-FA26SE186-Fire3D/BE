# Rà soát BE sau merge main — 2026-09-22

## Phạm vi và kết luận

Rà soát solution, project references, controller routes, auth/DI/configuration,
store/EF mapping, script reset và đối chiếu các luồng IFC/playtest với schema Docs.
Source hiện có 45 HTTP action attributes; đây không phải 45 endpoint đã pass E2E.
Không truy cập hoặc thay đổi Supabase, không gửi email thật, không gọi Firebase/S3
thật để xác minh nghiệp vụ. Không coi build thành công là toàn dự án đã ổn.

Main được fetch tại `7a0898b`, merge vào `fix/auth-registration`. Conflict nằm tại
Program.cs. Giữ cấu hình CORS mới và hợp nhất cách nạp Firebase thay vì chọn toàn
bộ một phía. File `Fire3D.API.http` chưa track là file của người dùng, không đưa vào merge.

## Đã sửa trong lượt này

- Program.cs: bỏ khối Firebase trùng, ngoặc/else dư và giải quyết conflict.
- Firebase configuration: hỗ trợ secret JSON, section FirebaseAdmin và file local;
  bỏ section placeholder trong appsettings đang gây lỗi PKCS8 lúc khởi động.
  Không thay đổi credential thật trong file local.
- AuthenticationExtensions: đăng ký IIdentityProvider và Fire3DSessionIssuer còn
  thiếu; regression test đã tái hiện thiếu DI trước khi sửa.
- PasswordResetPostgresTests: tái sử dụng EF options/name translator, tránh tạo quá
  nhiều internal service providers khi chạy suite/concurrent enqueue.
- Bổ sung test Firebase section/newline và hướng dẫn migration/reset/recovery.

## Các lỗi/rủi ro còn lại cần ưu tiên

| Ưu tiên | Vị trí | Bằng chứng và tác động | Việc tiếp theo |
|---|---|---|---|
| P1 | Application/Authentication/Internal/AuthSupport.cs, CreateAsync | Nhận password/IPasswordService nhưng chỉ tạo hồ sơ User, không hash/lưu password và không tạo Firebase user. Tài khoản admin/bootstrap tạo ra chưa có credential để đăng nhập bằng mật khẩu đã nhập. | Thống nhất local password hay Firebase cho đường admin provision và triển khai nhất quán; cần test tạo rồi đăng nhập. |
| P1 | Commands/FirebaseLogin/ExchangeFirebaseTokenCommand.cs | Tìm user theo email rồi tự gán FirebaseUid nếu UID đang trống; không kiểm tra email_verified hoặc yêu cầu xác thực chủ tài khoản hiện tại. Firebase token hợp lệ không đủ để chứng minh quyền liên kết hồ sơ có sẵn. | Tách explicit link có bằng chứng sở hữu, kiểm tra verified email/provider và concurrency; không tự gắn tài khoản admin theo email. |
| P1 | API/Controllers/AuthController.cs | Không có HttpPost("login") dù tài liệu ghi route này và LoginWithPasswordCommandHandler tồn tại. | Nối route vào handler sau khi thống nhất credential model; sửa mapping lỗi provider/disabled và kiểm thử HTTP. |
| P1 | Infrastructure/Ifc/IfcWriteStore.cs, ProcessRevisionAsync | INSERT outbox ghi payload_hash literal 'hash'. Không đáp ứng hash 64 ký tự/canonical JSON trong schema mục tiêu. | Tạo payload bằng serializer, hash đúng contract DB, kiểm tra scope/transaction trên schema thật; không chỉ EnsureCreated. |
| P1 | Infrastructure/Scenarios/PlaytestWriteStore.cs | Query entitlement dùng is_active trong khi Docs có status/starts_at/ends_at/building_id; bắt mọi exception rồi tiếp tục với Guid.Empty. Start chỉ đổi Created→Running, chưa gọi contract start gate, chưa kiểm tra creator/entitlement/runtime đầy đủ. | Đồng bộ prepare/start với contract DB, quyền theo actor và idempotency; lỗi SQL phải được xử lý rõ. Chưa kết luận bypass được trên Supabase vì triggers có thể từ chối. |
| P2 | Commands/RegisterUser/RegisterUserCommand.cs | Firebase user được tạo trước DB. Compensation chỉ chạy khi TryCreateUserAsync trả false, không xử lý DB/audit/commit exception hoặc kết quả commit không rõ. | Thiết kế durable registration/reconcile, tránh Firebase orphan; không xóa identity mù sau commit timeout. |
| P2 | Controller + docs/api-docs.md | Register trả 200 với AccountResponse, docs ghi 201 empty; Firebase login nhận JSON string, docs gửi object idToken; DTO register không có organizationName. | Chốt contract và đồng bộ OpenAPI, docs, FE. |
| P2 | PasswordResetStore + migration 002 | Pending chặn cấp phiên sau kết quả Firebase không rõ; chưa có công cụ/job reconcile. Bảng mới không nằm trong EF EnsureCreated/migration cũ. | Áp dụng script trước deploy; bổ sung vận hành recovery có kiểm soát theo password-reset.md. |
| P2 | Application.csproj và auth handlers | Application tham chiếu FirebaseAdmin và gọi static SDK trực tiếp. Nhiều PackageReference dùng wildcard. | Đưa provider SDK ra Infrastructure qua interface; pin phiên bản/package lock để build lặp lại. |

S3StorageService đã có presigned URL/metadata calls, không còn là stub như review
cũ. Runtime/scenario/playtest mappings snake_case cũng đã được thêm. Những nhận xét
cũ này không được lặp lại như lỗi hiện tại; chưa chứng minh end-to-end bằng việc đọc code.

## Bằng chứng kiểm tra

- Build Release toàn solution thành công: 0 lỗi; 2 cảnh báo constructor Testcontainers cũ.
- AuthTests sau sửa: 35 pass, 0 fail, 22 integration tests cũ skip trong lượt chính.
  Có test controller/handler/provider transport/config/DI và 4 test PostgreSQL reset
  (migration, parallel dedup/claim, lease/retry, revoke/fence và rollback finalization).
- IfcTests: 54 pass, 2 fail ở bước khởi tạo Docker/Testcontainers (Docker endpoint
  timeout), chưa tới assertion nghiệp vụ ScenarioFlow và Retry_gate.
- Thử riêng fixture auth cũ với DB test trước khi sửa cấu hình đã tái hiện PKCS8
  do key mẫu. Sau khi bỏ section mẫu, host đã khởi động và test login thất bại
  thực sự ở HTTP: expected 200, actual 404 vì `/api/auth/login` chưa được expose.
  Đây là một test probe riêng; 22 test cũ vẫn skip trong lượt suite chính.
- TRX lưu local: `.codex/local/test-results/post-main-merge/` (ignored).

Để xác nhận hoàn chỉnh còn cần chạy lại integration suite với fixture đúng contract,
Docker sẵn sàng, schema đầy đủ và tài khoản Firebase/Mailgun/S3 test riêng. Các test
PostgreSQL reset dùng schema tối thiểu cộng script 002 thật; chưa kiểm chứng mọi
trigger/RLS/role của Supabase.
