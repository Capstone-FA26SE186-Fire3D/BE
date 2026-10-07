# Checklist tiến độ API đã chọn

Cập nhật: 07/10/2026. Nhánh BE: `feature/ifc-authoring-support-hardening`.

Đã hoàn tất 9 task trong phạm vi source, test BE và migration Supabase. Worker/provider thật, binary deployment và FE/Mobile/Unity vẫn cần nghiệm thu riêng. PostgreSQL tests dùng database disposable; không suy production hoạt động từ fixture.

| Task | Phần việc | Code / commit | Test BE | Push | Supabase / giới hạn |
| --- | --- | --- | --- | --- | --- |
| 1 | Building mutation, tenant và audit | ✅ Đã commit; ACL sửa `f973b13` | ✅ HTTP/PostgreSQL | ✅ | ✅ Kiểm ACL; binary chưa smoke test |
| 2 | IFC intent, immutable source, cleanup và receipt Building | ✅ Sửa cuối `259b6b5` | ✅ HTTP/gate/race và S3 giả lập | ✅ | ✅ Migration đã áp; S3 thật chưa nghiệm thu |
| 3 | Process, outbox, HTTP worker và retry | ✅ Đã commit | ✅ Worker giả lập, PostgreSQL | ✅ | ✅ Migration đã áp; worker/toolchain thật chưa nghiệm thu |
| 4 | Scenario/draft/version và anchor QA | ✅ Sửa cuối `5377654` | ✅ HTTP/PostgreSQL, Passed/Failed/blocker/stale attempt | ✅ | ✅ Migration đã áp; Unity thật chưa nghiệm thu |
| 5 | Readiness và approval nội dung | ✅ Đã commit | ✅ Đúng revision/version/artifact/hash | ✅ | ✅ Migration đã áp; deployment chưa smoke test toàn luồng |
| 6 | Playtest prepare/start và grant | ✅ Đã commit | ✅ Runtime giả lập, quota/replay | ✅ | ✅ Migration đã áp; Unity/Mobile chưa tích hợp nghiệm thu |
| 7 | Built release, Training list và Building access | ✅ `1399916` + ACL `f973b13` | ✅ HTTP/PostgreSQL/unit | ✅ | ✅ Migration đã áp; Built không chứng minh package chạy Unity |
| 8 | Feedback/support, receipt, ETag và paging | ✅ `2b3301b` | ✅ HTTP/PostgreSQL | ✅ | ✅ Migration đã áp; binary/client chưa smoke test |
| 9 | PayOS GET response, Swagger và tài liệu | ✅ `a03eb45` | ✅ OpenAPI/HTTP | ✅ | Không đổi nghiệp vụ PayOS; deployed contract chưa xác minh |

## Bản sửa review và kết quả cuối

- [x] Building access không còn direct protected DML; giữ quyền ghi cột legacy. Migration bằng nonsuperuser identity kiểm quyền và khôi phục membership.
- [x] IFC gate kiểm Building đã lưu trong receipt. HTTP store đã hash Building ID; SQL bổ sung bảo vệ payload legacy/caller gate, không đổi hash cũ.
- [x] Geometry anchor yêu cầu current Succeeded attempt có matching Passed validation, không Error/Critical. Worker Succeeded không thay QA Passed.
- [x] Regression API đã chọn sau hai bản sửa: **36 passed, 0 failed, 0 skipped**.
- [x] IFC regression phù hợp: **105 passed, 0 failed, 0 skipped**. Ba nhóm Docker-only legacy không nằm trong lệnh này vì Docker unavailable; không ghi chúng là passed/skipped bởi runner.
- [x] Build solution cuối: **0 warning, 0 error**.
- [x] Hai bản sửa code commit/push riêng; không rewrite lịch sử hoặc merge main.
- [x] Migration Task 7/8, ACL và hai repair cuối đã áp Supabase; postcheck history/ownership/ACL/dữ liệu đạt.

Auth regression rộng trước các repair SQL cuối: 428 passed, 0 failed, 0 skipped. Nhóm 36 ở trên là lượt regression cuối; không cộng các lượt chạy trùng thành số test unique.

## Rollout Supabase ngày 07/10/2026

Đã áp 20261007100000_AddReleaseAndBuildingAccess, 20261007110000_AddSupportCommandContracts và 20261007120000_HardenSelectedGatePrivileges. Sau test/review bổ sung, áp tiếp 20261007130000_BindIfcReceiptBuilding và 20261007140000_RequirePassedGeometryAnchors trong một transaction có baseline/assertions trước commit.

Read-only postcheck xác nhận history mới, function owner/security/EXECUTE và logic repair. Runtime API giữ EXECUTE; PUBLIC/anon/authenticated không được gọi gate. Giữ nguyên 8 user, 6 organization, 2 Building; không reset hoặc tạo dữ liệu provider thử. Hai repair cuối chỉ thay function, không thêm/xóa cột hoặc backfill dữ liệu.

## Chưa nghiệm thu / ngoài phạm vi

- [ ] Deploy binary khớp schema và smoke test với FE/Mobile/Unity.
- [ ] S3 và HTTP worker/toolchain IFC/Blender/Unity thật.
- [ ] Publish gate hoàn chỉnh; hiện vẫn chặn 503.
- [ ] Learner start/heartbeat/offline sync/result và training analytics.

Tài liệu contract: [selected-api-contract.md](selected-api-contract.md), [134 HTTP operations từ OpenAPI](api-route-inventory.md), [rollout](ifc-authoring-deployment.md), [release/access](release-building-access.md), [support](support-api.md). Checklist toàn BE vẫn phân biệt phạm vi này với các nghiệp vụ còn thiếu. Bàn giao tiến độ cho người dùng bằng checklist trong chat.
