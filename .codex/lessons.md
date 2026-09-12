# BE — bài học dùng chung

Chỉ lưu kiến thức đã xác minh và cần cho team. Nhật ký lỗi, thử nghiệm và bàn giao từng phiên nằm trong .codex/local/ và không được track.

Chưa nâng bài học phiên nào vào file này. Khi cần bổ sung trong PR liên quan, ghi ngắn: tình huống → nguyên nhân/bằng chứng → cách khắc phục → cách kiểm tra và phạm vi áp dụng. Gộp trùng, không viết lại toàn bộ file.

## 2026-09-13 — cài project skills

- `dotnet build` không thể xác nhận trên máy này: chỉ có SDK `8.0.425`, trong khi solution dùng `.slnx` cần SDK mới hơn. Không tự đổi target framework để làm test xanh.
