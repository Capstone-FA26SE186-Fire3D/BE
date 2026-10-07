# Checklist tiến độ API đã chọn

Cập nhật: 07/10/2026. Nhánh BE: `feature/ifc-authoring-support-hardening`.

Checklist này ghi nhận bằng chứng đã có; chưa đánh dấu toàn bộ đợt hoàn tất. Test PostgreSQL dùng database disposable, không chứng minh provider hoặc deployment hoạt động.

| Task | Phần việc | Code / commit | Test | Push | Supabase / nghiệm thu thật |
| --- | --- | --- | --- | --- | --- |
| 1 | Building mutation, tenant và audit | ✅ Đã commit | ✅ Có test; ACL đã sửa/kiểm PostgreSQL | ✅ | ✅ Đã áp và kiểm ACL mới |
| 2 | IFC upload intent, source bất biến | ✅ Đã commit | ⚠️ Review còn lỗi receipt khác Building | ✅ | ✅ Migration đã áp; S3 thật chưa nghiệm thu |
| 3 | Process, outbox, HTTP worker và retry | ✅ Đã commit | ✅ Có test worker giả lập | ✅ | ✅ Migration đã áp; worker/toolchain thật chưa nghiệm thu |
| 4 | Scenario/draft/version | ✅ Đã commit | ⚠️ Review còn lỗi dùng anchor từ validation Failed | ✅ | ✅ Migration đã áp |
| 5 | Readiness và approval nội dung | ✅ Đã commit | ✅ Có test cặp revision/version và gate | ✅ | ✅ Migration đã áp; deployment chưa smoke test toàn luồng |
| 6 | Playtest prepare/start và grant | ✅ Đã commit | ✅ Runtime giả lập | ✅ | ✅ Migration đã áp; Unity/Mobile chưa tích hợp nghiệm thu |
| 7 | Built release, Training list, Building access | ✅ `1399916` + sửa quyền `f973b13` | ✅ 3 test PostgreSQL/HTTP, 6 unit và test ACL | ✅ | ✅ Đã áp; binary/client chưa smoke test |
| 8 | Feedback/support, receipt, ETag, paging | ✅ `2b3301b` | ✅ 5 test PostgreSQL/HTTP | ✅ | ✅ Đã áp; binary/client chưa smoke test |
| 9 | PayOS GET response, Swagger và tài liệu | ✅ `a03eb45` | ✅ 1 test OpenAPI/HTTP | ✅ | Không thêm nghiệp vụ PayOS; contract deployment chưa xác minh |

## Việc cần đóng trước khi hoàn tất

- [x] Chặn direct INSERT/UPDATE các cột Building access khi runtime đang có quyền ghi toàn bảng; kiểm lại quyền legacy và gate bằng PostgreSQL.
- [ ] Ràng buộc receipt IFC initiate với Building ID; cùng key nhưng khác Building phải conflict.
- [ ] Chỉ dùng geometry anchor từ validation Passed và không có blocker Error/Critical.
- [ ] Sửa/test và commit hai finding IFC/scenario còn mở; finding ACL đã commit/push f973b13.
- [x] Auth regression: 428 passed, 0 failed, 0 skipped. Hai finding IFC/scenario còn cần sửa/test bổ sung; ACL đã có test nâng cấp bằng migration identity không phải superuser.
- [x] IFC regression phù hợp trên PostgreSQL native: 105 passed, 0 failed, 0 skipped.
- [x] Build solution sau sửa quyền: 0 warning, 0 error.
- [x] Push Task 9 a03eb45 và bản sửa quyền f973b13; remote HEAD khớp.
- [ ] Commit/push hai bản sửa IFC/scenario còn lại sau test.
- [x] Triển khai migration Task 7/8 và migration sửa quyền theo dependency sau khi test đạt; không reset dữ liệu.
- [ ] Smoke test deployment; ghi riêng S3, worker thật và client integration.

## Tài liệu theo dõi

- [Contract và giới hạn đợt triển khai](selected-api-contract.md)
- [Danh sách 134 HTTP operations từ OpenAPI](api-route-inventory.md)
- [Release và Building access](release-building-access.md)
- [Support](support-api.md)
- [Checklist API toàn BE](api-implementation-checklist.md)

Docs repo riêng: nhánh `docs/ifc-authoring-support-hardening`, commit `1c402a6` đã push.

Các phần tiếp tục chưa hoàn tất: publish gate, IFC/Blender/Unity thật, learner start/sync/result và nghiệm thu provider. Không tick các phần này từ kết quả mock.

## Rollout Supabase ngày 07/10/2026

Đã áp 20261007100000_AddReleaseAndBuildingAccess, 20261007110000_AddSupportCommandContracts và 20261007120000_HardenSelectedGatePrivileges trong một transaction. Postcheck sau commit xác nhận history, RLS và quyền gate; giữ nguyên 8 user, 6 organization, 2 Building. Hai Building có Private/access revision 1. Không reset hoặc tạo dữ liệu thử trên database dùng chung.

Login API giữ quyền INSERT/UPDATE các cột Building cũ; không ghi trực tiếp visibility/access_revision/participation_code_hash hoặc bảng support/release/provenance. Trigger bảo vệ thêm khi có overgrant về sau. Hai finding IFC/scenario và smoke test deployment vẫn còn chờ; không đánh dấu toàn đợt hoàn tất.

Checklist này đã được commit/push trên nhánh BE; tiến độ tiếp tục ghi theo từng mốc có bằng chứng.
