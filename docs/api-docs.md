# Tài Liệu API Fire3D (Detailed API Specification)

Tài liệu này cung cấp chi tiết về chức năng, cách sử dụng, ý nghĩa nghiệp vụ cũng như cấu trúc dữ liệu (Request/Response) của toàn bộ các API trong hệ thống Fire3D.

## Cấu trúc chung
- **Authentication**: Sử dụng JWT token. Truyền qua header `Authorization: Bearer <token>`.
- **Định dạng lỗi**: Trả về chuẩn RFC 7807 Problem Details (mã lỗi 400, 401, 403, 404, 409).
- **Phân quyền**:
  - `PlatformAdmin`: Quản trị viên hệ thống (chỉ có quyền tạo/sửa Account và Organization).
  - Auth thông thường: Người dùng thuộc các tổ chức (OrganizationAdmin, OrganizationUser, Trainee) dùng để quản lý Building, IFC, Scenario.

---

## 1. Authentication (Xác thực người dùng)
Base URL: `/api/auth`

### 1.1 Đăng nhập bằng Email/Password
**`POST /api/auth/login`**
- **Mô tả**: API dùng để đăng nhập hệ thống dành cho người dùng đã có tài khoản (được tạo bởi Admin) bằng Email và Mật khẩu cơ bản.
- **Phân quyền**: Anonymous (Không yêu cầu token)
- **Schema**:
```json
// Request
{
  "email": "user@example.com",
  "password": "mySecurePassword123!"
}

// Response: 200 OK
{
  "accessToken": "eyJhb...",
  "accessTokenExpiresAt": "2026-09-22T10:00:00Z",
  "refreshToken": "def56...",
  "refreshTokenExpiresAt": "2026-10-22T10:00:00Z",
  "user": {
    "id": "3fa85f64-...",
    "email": "user@example.com",
    "fullName": "John Doe",
    "role": "OrganizationUser",
    "organizationId": "5fa85f64-..."
  }
}
```

### 1.2 Đăng nhập bằng Google / Firebase SSO
**`POST /api/auth/login-firebase`**
- **Mô tả**: API dùng để đăng nhập thông qua Google. FE sẽ gọi Google/Firebase để lấy `idToken`, sau đó gửi token này xuống BE. BE sẽ tự động xác thực token này với Firebase. Nếu email chưa từng tồn tại, hệ thống sẽ **tự động tạo mới tài khoản với Role là Trainee**.
- **Schema**:
```json
// Request
{
  "idToken": "eyJhbGciOiJSUz..." // Token lấy từ Firebase SDK ở Frontend
}
// Response: 200 OK (Cấu trúc TokenResponse giống hệt API Login)
```

### 1.3 Đăng ký tài khoản (Public Signup)
**`POST /api/auth/register`**
- **Mô tả**: Cho phép người dùng công cộng tự tạo tài khoản. Mặc định các tài khoản tự đăng ký qua API này sẽ được gán role thấp nhất là `Trainee`. Phù hợp cho tính năng cho phép học viên tự tham gia.
- **Schema**:
```json
// Request
{
  "email": "newuser@example.com",
  "password": "mySecurePassword123!",
  "fullName": "Jane Doe",
  "organizationName": "Tên tổ chức (tuỳ chọn)" 
}
// Response: 201 Created (Empty Body)
```

### 1.4 Quên mật khẩu & Đặt lại mật khẩu
**`POST /api/auth/forgot-password`**
- **Mô tả**: Gửi yêu cầu xin cấp lại mật khẩu. Hệ thống sẽ sinh ra một Outbox event để worker chạy ngầm gửi email chứa link đặt lại mật khẩu cho người dùng.
- **Schema**:
```json
// Request
{ "email": "user@example.com" }
// Response: 202 Accepted (Empty Body)
```

**`POST /api/auth/reset-password`**
- **Mô tả**: Sau khi bấm vào link trong email, người dùng sẽ nhận được mã `oobCode` của Firebase. Dùng mã này kèm mật khẩu mới để đổi mật khẩu. Đổi thành công sẽ tự động **đăng xuất (thu hồi session)** trên tất cả các thiết bị để đảm bảo an toàn.
- **Schema**:
```json
// Request
{
  "oobCode": "firebase_oob_code_from_email_link",
  "newPassword": "newSecurePassword123!"
}
// Response: 200 OK (Empty Body)
```

---

## 2. Quản trị hệ thống (Platform Administration)
Dành riêng cho PlatformAdmin (chủ hệ thống SaaS) quản lý khách thuê (Organizations) và tài khoản (Accounts).

### 2.1 Quản lý Tổ chức (Organizations)
**`POST /api/organizations`**
- **Mô tả**: Tạo một tổ chức (tenant) mới trên hệ thống. Ví dụ: Tạo 1 tenant cho Sở PCCC Hà Nội, 1 tenant cho Sở PCCC TP.HCM để họ tự quản lý dữ liệu tách biệt với nhau.
- **Schema**:
```json
// Request
{
  "name": "Sở PCCC Hà Nội",
  "slug": "pccc-hn"
}
// Response: 201 Created
{
  "id": "...",
  "name": "Sở PCCC Hà Nội",
  "slug": "pccc-hn",
  "isActive": true,
  "createdAt": "2026-09-22T10:00:00Z"
}
```

### 2.2 Quản lý Tài khoản (Accounts)
**`POST /api/accounts`**
- **Mô tả**: Nơi PlatformAdmin cấp phát tài khoản thủ công cho nhân sự của các tổ chức, có thể gán role cụ thể (OrganizationAdmin, OrganizationUser). Không thể tự đổi quyền của mình.
- **Schema**:
```json
// Request
{
  "email": "admin@pccc-hn.gov",
  "password": "TempPassword123!",
  "fullName": "Admin HN",
  "role": 1, // 0=PlatformAdmin, 1=OrgAdmin, 2=OrgUser, 3=Trainee
  "organizationId": "id-cua-to-chuc"
}
// Response: 201 Created
{
  "id": "...",
  "email": "admin@pccc-hn.gov",
  "fullName": "Admin HN",
  "role": "OrganizationAdmin",
  "isActive": true
}
```

---

## 3. Quản lý Tòa nhà (Buildings)
Base URL: `/api/buildings`
Các api này tự động scope theo `OrganizationId` của user đang đăng nhập. User chỉ xem và sửa được tòa nhà thuộc tổ chức của mình, dữ liệu hoàn toàn bị cách ly với tenant khác.

### 3.1 Khởi tạo Tòa nhà
**`POST /api/buildings`**
- **Mô tả**: Tạo mới hồ sơ cho một tòa nhà (ví dụ: Vincom Landmark 81). Hồ sơ này là vật chứa gốc, sau đó mới upload các bản vẽ 3D IFC vào tòa nhà này. Bao gồm thông tin vị trí địa lý và thông tin người liên hệ tại hiện trường.
- **Schema**:
```json
// Request
{
  "name": "Vincom Landmark 81",
  "buildingType": "Commercial",
  "totalFloors": 81,
  "location": {
    "address": "720A Điện Biên Phủ",
    "city": "Hồ Chí Minh",
    "district": "Bình Thạnh",
    "latitude": 10.793,
    "longitude": 106.721
  },
  "contact": {
    "contactName": "Nguyễn Văn A",
    "contactRole": "Trưởng ban quản lý",
    "phone": "0901234567"
  }
}
// Response: 201 Created (Toàn bộ Object Building có chứa ID mới tạo)
```

---

## 4. Nhập & Xử lý File 3D (IFC Processing)
Chuỗi quy trình bắt buộc: Lấy link upload S3 -> Báo đã upload xong -> Kích hoạt xử lý -> Lấy trạng thái xử lý bằng Polling.

### 4.1 Xin URL Upload (Initiate)
**`POST /api/buildings/{buildingId}/ifc`**
- **Mô tả**: Tạo một phiên bản bản vẽ (Revision) mới cho tòa nhà và xin link pre-signed của S3 (AWS/MinIO). Điều này cho phép Frontend có thể upload trực tiếp file IFC nặng hàng trăm MB lên cloud mà không làm nghẽn Backend.
- **Schema**:
```json
// Request
{
  "fileSizeBytes": 15000000,
  "originalFilename": "Landmark_v1.ifc",
  "versionLabel": "Bản vẽ hoàn công 2024"
}
// Response: 200 OK
{
  "revisionId": "...",
  "uploadUrl": "https://s3.amazonaws.com/..." // Link để client dùng HTTP PUT file IFC lên
}
```

### 4.2 Xác nhận Upload Thành Công
**`POST /api/revisions/{revisionId}/upload-complete`**
- **Mô tả**: Sau khi Frontend dùng link S3 upload file hoàn tất thành công 100%, phải gọi API này để báo Backend biết file đã nằm trên Cloud an toàn, BE khóa trạng thái file để chờ xử lý.
- **Schema**:
```json
// Request
{
  "objectKey": "s3-object-key-path",
  "fileSizeBytes": 15000000,
  "mimeType": "application/x-step",
  "sha256Hash": "a1b2c3d4...",
  "originalFilename": "Landmark_v1.ifc"
}
// Response: 204 No Content
```

### 4.3 Kích hoạt hệ thống xử lý (Trigger Process)
**`POST /api/revisions/{revisionId}/process`**
- **Mô tả**: Ra lệnh cho hệ thống Backend chạy luồng xử lý nặng. Luồng này sẽ tự động: Đọc file IFC, bóc tách dữ liệu BIM, tạo bản đồ di chuyển NavMesh, và kiểm tra lỗi thiết kế PCCC. API trả về ngay lập tức để FE có thể làm việc khác.
- **Schema**: 
  - Request Body rỗng.
  - Trả về `202 Accepted` kèm theo ID của `Job` để theo dõi tiến độ.

### 4.4 Lấy tiến độ xử lý (Polling)
**`GET /api/processing-jobs/{jobId}`**
- **Mô tả**: FE gọi định kỳ (ví dụ mỗi 5 giây) để xem trạng thái xử lý tới bước nào (Parsing, GenNavMesh, GenHazardGrid...) và xem phần trăm hoàn thành.

---

## 5. Kịch Bản & Trải Nghiệm VR (Scenarios & Playtest)
API dùng để vẽ kịch bản hỏa hoạn (node, hướng di chuyển) trên môi trường Web và khởi tạo session cho game kính VR.

### 5.1 Tạo bản nháp kịch bản (Draft)
**`POST /api/scenarios/{scenarioId}/draft`**
- **Mô tả**: Khi người dùng muốn sửa kịch bản VR, họ không sửa thẳng vào bản Live. API này tạo một bản nháp (Draft) cách ly. Người dùng có thể thoải mái thêm lửa, bình cứu hỏa mà không ảnh hưởng tới kịch bản chính đang có học viên chơi.
- **Schema**:
```json
{ "revisionId": "id-cua-ban-ve-ifc" }
```

### 5.2 Lưu trạng thái kéo thả của Kịch bản
**`PUT /api/scenario-drafts/{draftId}`**
- **Mô tả**: Lưu lại cấu hình logic không gian (tọa độ của ngọn lửa, bình chữa cháy, lối thoát hiểm, logic trigger) mà người dùng vừa kéo thả trên giao diện Web 3D. 
Áp dụng cơ chế **Khóa Lạc Quan (ExpectedVersion)**: Tránh lỗi nhiều người cùng lưu đè lên nhau.
- **Schema**:
```json
// Request
{
  "expectedVersion": 1,
  "state": {
    "nodes": [
      { "id": "fire_1", "type": "FireNode", "position": [10.5, 0, -5.2] }
    ],
    "edges": []
  }
}
```

### 5.3 Tạo phòng chơi VR (Playtest)
**`POST /api/scenarios/{scenarioId}/playtests`**
- **Mô tả**: Dùng để tạo ra một session (phòng chơi) để học viên đeo kính VR vào đăng nhập. Session này sẽ ghi nhận lại toàn bộ thao tác, quỹ đạo di chuyển của người học để sau này chấm điểm.
- **Schema**:
```json
// Request
{
  "mode": 0, // 0 = Solo, 1 = Multiplayer
  "isVR": true
}
// Response: Chứa PlaytestId để kính VR đăng nhập vào phòng.
```
