# BE docs — đọc và triển khai theo chuẩn FET3D

Tài liệu sản phẩm chuẩn nằm trong repo `Docs`. Khi tài liệu BE và `Docs` khác nhau, dùng `Docs` làm contract đích; không tự đổi nghiệp vụ theo code prototype hoặc theo backlog cũ. Tài liệu trong thư mục này ghi cách BE hiện hoạt động, phần còn lệch và cách triển khai contract.

## Thứ tự đọc

1. [Docs gốc: requirements](../../Docs/fire_evacuation_requirements.md) — hành vi và tiêu chí sản phẩm.
2. [Docs gốc: workflows](../../Docs/fire-evacuation-training-workflows.md) — luồng actor và nghiệp vụ.
3. [Docs gốc: technology](../../Docs/fire-evacuation-training-technology.md) — service boundary, API contract và invariant kỹ thuật.
4. [Docs gốc: schema SQL](../../Docs/fire_evacuation_schema.sql) và [ERD](../../Docs/fire_evacuation_erd.md) — mô hình dữ liệu mục tiêu.
5. [BE API guide](api-docs.md) — route/DTO/status của code hiện tại; mỗi điểm khác contract đích phải được ghi rõ.
6. [BE implementation checklist](api-implementation-checklist.md) — phần đã có, phần còn thiếu và tiêu chí hoàn tất.

Chi tiết từng luồng: [authentication](authentication.md), [password reset](password-reset.md), [administration](administration.md), [architecture](backend-architecture.md), [integration test](integration-tests.md) và [IFC progress](ifc-api-progress.md). [Foundation](backend-foundation.md), [Week 2 review](week2-review.md) và [project review](project-review-2026-09-22.md) là ảnh chụp lịch sử; không dùng backlog/status ở đó thay cho API guide và checklist hiện hành.

## Quy tắc phân biệt trạng thái

- **Contract đích:** yêu cầu trong `Docs`; đây là chuẩn để thiết kế và sửa BE.
- **Đã có trong source:** chỉ kết luận khi route, handler/store và persistence tương ứng tồn tại ở source BE đang làm việc.
- **Đã kiểm thử:** chỉ kết luận theo kết quả kiểm thử gắn đúng commit và nêu rõ fixture/provider thật hay giả. Có route hoặc test mock không đồng nghĩa đã tích hợp production.
- Các báo cáo có ngày/commit cũ là bằng chứng lịch sử. Không dùng lại danh sách endpoint, số bảng, backlog hoặc nhận định trong báo cáo cũ như trạng thái hiện tại nếu chưa đối chiếu source.

## Database mới và dữ liệu

Thiết kế SQL/ERD trong `Docs` là schema mục tiêu. Theo xác nhận hiện chưa có dữ liệu nghiệp vụ cần chuyển đổi, nên khi bắt đầu triển khai có thể tạo database môi trường mới theo schema mục tiêu và migration cần thiết; không viết yêu cầu backfill chỉ để giữ schema prototype. Không chạy migration, xóa database hoặc áp dụng SQL lên môi trường dùng chung/production nếu chưa có task và xác nhận môi trường riêng.

`Docs` chỉ mô tả thiết kế, không chứng minh migration, quyền PostgreSQL, provider hoặc luồng production đã chạy. Ghi riêng trạng thái code, schema từng môi trường và kết quả test.

Source BE hiện còn mapping/entity legacy; database tạo theo schema đích chưa chắc chạy được với API source hiện tại cho tới khi EF mapping, SQL gates, quyền và use case được đồng bộ. Không xem “chưa có dữ liệu” là bằng chứng database bất kỳ đang trống hoặc được phép xóa.
