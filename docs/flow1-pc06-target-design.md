# Flow 1 theo Mẫu PC06 — thiết kế đích cho BE (23/11/2026)

> **Trạng thái: chưa triển khai.** Tài liệu này mô tả việc BE cần làm theo định hướng của giảng viên hướng dẫn ngày 23/11/2026. Contract sản phẩm nằm ở `Docs`: [pháp lý PCCC](../../Docs/phap-ly-pccc.md), [Flow 1](../../Docs/phan_tich_khoang_cach_va_quyet_dinh_nghiep_vu.md), nhóm yêu cầu FR-PLAN trong [requirements](../../Docs/fire_evacuation_requirements.md). Route và DTO bên dưới là đề xuất để mở issue, chưa phải API đã có.

## Luồng

```text
Organization tạo Building (Apartment | School) + phiếu thông tin cơ sở PC06
  -> upload IFC (tiêu chí đầu vào) -> xử lý -> QA
  -> dựng Hạ tầng -> gửi thẩm định
  -> FireSafetyExpert: Approved (khóa) | Rejected (lý do)
  -> tạo Tình huống trên hạ tầng đã khóa -> gửi thẩm định
  -> FireSafetyExpert: Approved | Rejected (lý do)
  -> Organization yêu cầu báo giá -> PlatformAdmin báo giá -> thanh toán -> phát hành -> QR
  -> Trainee luyện tập -> nhật ký theo tình huống
```

Người đứng đầu cơ sở phê duyệt phương án PC06 ngoài hệ thống (Điều 15 Nghị định 105/2025/NĐ-CP). `FireSafetyExpert` chỉ thẩm định nội dung huấn luyện; không dùng từ "phê duyệt phương án" trong API, thông báo hay audit.

## So với source hiện tại

| Hạng mục | Hiện tại | Đích |
| --- | --- | --- |
| Vai trò | `UserRole`: PlatformAdmin, OrganizationUser, Trainee (`Domain/Enums/DatabaseEnums.cs`) | Thêm `FireSafetyExpert` |
| Loại công trình | `Building.BuildingType` là `string?` tự do | Enum `Apartment`, `School` |
| Thông tin cơ sở | Tên, địa chỉ, liên hệ | Thêm trường PC06 phần II, tổng diện tích sàn, nhóm nguy cơ, tệp PC06 đã ký |
| Upload IFC | Initiate kiểm đuôi và kích thước; upload-complete kiểm `ISO-10303-21;` (`IfcUploadService.cs`); `IfcUpload:MaxBytes` chưa đặt; `QuarantineStatus` không ai gán | Kiểm `FILE_SCHEMA`, đặt `MaxBytes`, dọn revision Draft khi từ chối, gán `QuarantineStatus` |
| QA sau xử lý | Mã trong `docs/ifc-qa-codes.md` | Thêm mã: không có tầng, nhiều tầng không có cầu thang, đơn vị không phải mét |
| Thiết bị, lối thoát | Trong `objects[]` của scenario draft (`kind` Equipment, Goal); quyết định thiết bị theo revision chỉ là gợi ý | Lớp hạ tầng theo revision, có phiên bản và khóa |
| Thẩm định nội dung | PlatformAdmin duyệt kịch bản (`ScenarioContentReviewsController`) | `FireSafetyExpert` thẩm định hạ tầng và tình huống |
| Kịch bản | Không có đối tượng, không có giả định PC06 | `audience`, giả định, `workingHours` |
| Chấm điểm | Metric `reached_exit`, `completion_time_seconds`, `wrong_exits`, `hazard_exposure`, `distance_meters`, thiết bị | Thêm `step_count`, `route_efficiency` |
| Báo giá | Quotation theo Building đã có (`docs/billing.md`) | Chỉ cho yêu cầu báo giá khi có ít nhất một tình huống đạt thẩm định |
| Nhật ký luyện tập | `analytics/training` có `groupBy=scenario` và export CSV, tối đa 90 ngày (`docs/learner-analytics.md`) | Khoảng một năm, ngày luyện gần nhất, tình huống chưa luyện, dòng miễn trừ trong bản xuất |

## 1. Vai trò `FireSafetyExpert`

- Thêm giá trị enum và seed tài khoản do PlatformAdmin tạo; không có tự đăng ký.
- Quyền: đọc hạ tầng và tình huống đang chờ thẩm định của mọi Organization, đọc preview 3D tương ứng; ghi quyết định. Không đọc billing, không sửa nội dung, không phát hành.
- Quyết định có `If-Match`, `Idempotency-Key`, lý do bắt buộc khi từ chối (1–2000 ký tự), ghi audit.

## 2. Building

- `buildingType`: `Apartment | School`; giá trị khác trả `400 VALIDATION_ERROR` với `errors.buildingType`. Dữ liệu cũ: ánh xạ "Chung cư" → `Apartment`, "Trường học" → `School`, còn lại để `null` và bắt chọn lại khi sửa.
- Trường mới: `floorAreaSquareMeters`, `facilityHeadName`, `facilityHeadPhone`, `onSiteForce {teamSize, leaderName, leaderPhone, availableInHours, availableOffHours}`, `emergencyContacts[]`, `signedPlanDocumentId` (tệp PDF phương án đã ký, lưu private).
- `riskGroup` do server tính theo Phụ lục II Nghị định 105/2025/NĐ-CP: `Group1 | Group2 | BelowThreshold`.

| Loại | Group1 | Group2 |
| --- | --- | --- |
| Apartment | từ 7 tầng hoặc từ 3.000 m² | 5 đến dưới 7 tầng hoặc 1.000 đến dưới 3.000 m² |
| School | từ 5 tầng hoặc từ 3.000 m² | 3 đến dưới 5 tầng hoặc 1.500 đến dưới 3.000 m² |

## 3. Hạ tầng PCCC theo revision

- Thực thể `FireInfrastructure {id, revisionId, version, status, items[], submittedAt, decidedBy, decisionReason}`; `status`: `Draft → Submitted → Approved | Rejected`. `Approved` là khóa: không sửa, không xóa.
- `items[]`: thiết bị (catalogKey, placement, anchorId), lối thoát nạn, điểm tập kết, nguồn nước. Nguồn ban đầu là quyết định thiết bị đã có (`revision_equipment_decisions`).
- Sửa sau khi khóa: tạo `version` mới từ bản đã khóa, thẩm định lại. Tình huống đang tham chiếu phiên bản cũ giữ nguyên.
- Scenario draft thêm `infrastructureId` bắt buộc, chỉ nhận hạ tầng `Approved` cùng revision. Validate từ chối `objects[]` có `kind` Equipment cố định hoặc Goal kiểu lối thoát (`422 SCENARIO_CONTAINS_INFRASTRUCTURE`). Package build gộp hạ tầng và tình huống để Unity nhận như hiện nay.

## 4. Upload IFC

- `upload-complete`: đọc phần đầu tệp, yêu cầu `FILE_SCHEMA` thuộc IFC2X3, IFC4, IFC4X3; sai trả `422 IFC_UNSUPPORTED_SCHEMA`.
- Đặt `IfcUpload:MaxBytes` cho mọi môi trường và trả giá trị này qua API cấu hình để web kiểm tra trước.
- Khi initiate hoặc complete bị từ chối: xóa revision Draft và upload intent vừa tạo.
- Gán `QuarantineStatus` `Accepted` hoặc `Rejected` theo kết quả kiểm tra.
- Mã QA mới: `IFC_NO_STOREYS`, `IFC_NO_STAIRS_MULTISTOREY`, `IFC_UNIT_NOT_METRE`.

## 5. Tình huống

- Trường mới trong scenario state: `audience` (`Evacuee | Communications | EvacuationGuide | FirstResponse`), `assumption {time, location, cause, peoplePresent}`, `workingHours` (`InHours | OffHours`).
- Thẩm định tình huống thay cho bước PlatformAdmin duyệt nội dung hiện nay.
- Gate phát hành: hạ tầng `Approved`, tình huống `Approved`, và Building có ít nhất một tình huống `InHours` và một `OffHours` đã đạt.
- API dữ liệu "hồ sơ phương án trực quan": trả thông tin cơ sở, hạ tầng đã khóa và danh sách tình huống theo thứ tự mục của Mẫu PC06.

## 6. Chấm điểm

- Event mới từ Unity: `RouteBaseline {optimalDistanceMeters}` lúc bắt đầu; `Moved` thêm `steps`.
- Metric mới cho rubric: `step_count`, `route_efficiency` = quãng đường tối ưu / quãng đường thực tế, giới hạn 0–1; thiếu baseline thì tiêu chí dùng metric này là không đánh giá được.

## 7. Báo giá

Dùng quotation hiện có. Thêm điều kiện: Organization chỉ gửi yêu cầu báo giá cho Building có ít nhất một tình huống đạt thẩm định; sai trả `409 QUOTE_REQUIRES_APPROVED_SCENARIO`.

## 8. Nhật ký luyện tập

Mở rộng `analytics/training` thay vì tạo API mới:

- Cho phép khoảng thời gian tới 366 ngày khi `groupBy=scenario`.
- Mỗi nhóm thêm `lastPlayedAt` và `workingHours`, `audience` của tình huống.
- Trả cả tình huống đã phát hành nhưng chưa có lượt nào trong khoảng (plays = 0) để web cảnh báo "chưa luyện trong năm".
- Bản CSV thêm dòng đầu: "Số liệu luyện tập trên mô phỏng, mang tính hỗ trợ, không thay buổi thực tập phương án."

## Ca kiểm tra nghiệm thu

Mỗi issue kèm ca kiểm tra riêng. Tối thiểu: quyền của `FireSafetyExpert` (đọc đúng hàng chờ, không đọc billing); `buildingType` sai; `riskGroup` ở các ngưỡng biên; hạ tầng khóa không sửa được; tình huống trên hạ tầng chưa khóa bị từ chối; tệp STEP không phải IFC; revision Draft không còn sau khi từ chối; phát hành thiếu tình huống ngoài giờ; `route_efficiency` thiếu baseline; nhật ký trả tình huống 0 lượt.
