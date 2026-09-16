# Quyền đặc biệt của PlatformAdmin

Quyết định phạm vi sản phẩm ngày 2026-09-15: PlatformAdmin được thao tác thay tổ chức, bao gồm dữ liệu nghiệp vụ, và xem thông tin cá nhân phục vụ quản trị. Cần team review tài liệu này cùng API contract trước khi triển khai từng module.

## Ma trận quyền và tiến độ

| Phạm vi | Quyền đã thống nhất | Hiện trạng thực thi |
|---|---|---|
| Tổ chức | Quản trị xuyên tổ chức | Có tạo, danh sách, chi tiết, đổi trạng thái |
| Tài khoản | Tạo, xem thông tin cá nhân, đổi trạng thái | Đã có; chỉ PlatformAdmin được gọi API quản trị |
| Tòa nhà / revision / IFC | Thực hiện nghiệp vụ thay tổ chức | Yêu cầu cho module tương lai; chưa có API |
| Kịch bản / phiên bản / release | Thực hiện nghiệp vụ thay tổ chức | Yêu cầu cho module tương lai; chưa có API |
| Huấn luyện / phiên / kết quả | Quản trị, xem dữ liệu phục vụ hỗ trợ tổ chức | Yêu cầu cho module tương lai; chưa có API |

Quyền quản trị không tự tạo ra chức năng chưa có, không bỏ qua validation, trạng thái hay quy tắc nghiệp vụ. Việc sửa kết quả huấn luyện đã hoàn tất cần có use case hiệu chỉnh và lịch sử riêng được team chốt; quyết định này không mặc nhiên cho phép ghi đè kết quả gốc.

## Dữ liệu cá nhân

Hiện API quản lý trả ID, email, họ tên, vai trò, organizationId, trạng thái, lần đăng nhập gần nhất và thời điểm tạo/cập nhật. Không trả password/hash, refresh token hoặc thông tin bí mật xác thực. Các trường cá nhân/kết quả huấn luyện bổ sung phải được xác định theo DTO và mục đích của từng use case.

## Thao tác thay tổ chức

Admin sử dụng chính danh tính PlatformAdmin; không giả mạo user hoặc cấp token dưới danh tính người khác. Với API hiện tại, ID tài khoản/tổ chức xác định đối tượng thao tác. Module tương lai phải xác minh tài nguyên thuộc tổ chức đích, kiểm tra quyền ở server và áp dụng đầy đủ quy tắc nghiệp vụ. OrganizationUser tiếp tục bị giới hạn trong tổ chức của mình; quyền admin không làm rộng quyền của role khác.

Các thao tác tạo/đổi trạng thái hiện có audit ghi người thực hiện, tổ chức đích và correlation ID khi dữ liệu thay đổi. Yêu cầu cho module tương lai: thao tác thay tổ chức phải ghi actor admin, tổ chức/tài nguyên đích, hành động, thời điểm và lý do hỗ trợ; thông tin nhạy cảm không đưa vào log. Audit lượt đọc dữ liệu cá nhân/lý do hỗ trợ chưa được triển khai trong API hiện tại và cần task riêng nếu team chọn đưa vào tiêu chí nghiệm thu.

## Nghiệm thu

- API quản trị hiện có: 401 khi thiếu/sai token, 403 với OrganizationUser/Trainee; kiểm tra actor ở Application và policy tại API.
- DTO quản lý không lộ thông tin xác thực. Thay đổi trạng thái thu hồi phiên theo docs/administration.md.
- Với mỗi module tương lai: thêm test admin thao tác đúng tổ chức đích, OrganizationUser bị từ chối truy cập chéo, không bỏ qua trạng thái/validation và audit xác định đúng admin.
- Swagger mô tả quyền; authorization thực tế nằm trong policy/handler, không nằm trong comment.