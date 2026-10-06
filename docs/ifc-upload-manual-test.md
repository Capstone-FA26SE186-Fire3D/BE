# Test upload IFC có intent

1. Dùng migration `AddBoundIfcUploads` trên PostgreSQL disposable trước deployment. Upload mặc định tắt; khai báo `IfcUpload__Enabled=true`, `IfcUpload__MaxBytes` bằng giới hạn đã chốt, `IfcUpload__CleanupEnabled=true`; cấu hình S3 riêng trong secret/environment. Không dùng test connection từ User Secrets.
2. Đăng nhập OrganizationUser đúng Building (hoặc admin), tính SHA-256 file IFC bằng `Get-FileHash -Algorithm SHA256`. Gọi một trong hai route initiate với header `Idempotency-Key: ifc-file-001`, size/hash/fileName/versionLabel thực. Kỳ vọng 201.
3. Gọi route alias với cùng key/input: cùng revision/objectKey. Đổi size/hash/versionLabel với cùng key: 409; chưa tạo thêm revision.
4. PUT bytes lên uploadUrl bằng Content-Type application/octet-stream. Giữ đủ key/size/hash/filename của intent. Gọi upload-complete: 204; gọi lại: 204, một source. Không có nút multipart IFC tại BE.
5. Key khác, actor khác, size/hash hoặc bytes lệch phải bị chặn. Source staging bị thay giữa inspect/copy không được adopt. Intent chưa complete hết 60 phút: 410; receipt đã commit vẫn replay được.
6. Kiểm source cuối có `upload_verified_at`, hash thực và key riêng mỗi attempt. Lỗi copy/DB/audit giữ candidate + cleanup; retry sau lease dùng key mới, cleanup không xóa source đã adopt. S3 NotFound là cleanup thành công; provider lỗi được retry. Object được bảo vệ vẫn giữ job.

Bằng chứng tự động: tests `IfcUploadBindingTests`, `IfcSourceInspectorTests`, `AuthIntegrationTests.Ifc_*` chạy actual migration history, API HTTP và restricted gate role; fake S3 bao phủ overwrite, cạnh tranh, replay mất response, lỗi audit sau copy, cleanup lỗi/retry và late orphan write. Không chứng minh S3/worker IFC/deployment thật. Process/readiness/worker là các task tiếp theo.
