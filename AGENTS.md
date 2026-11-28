# BE — hướng dẫn Codex

- **Đọc [.codex/code-map.md](.codex/code-map.md) trước tiên**: kiến trúc, vai trò, request đi qua gate SQL ra sao, tra code theo nghiệp vụ, worker nền, cách viết migration, chạy test, áp production và các bẫy đã gặp. Hàm SQL nào đang chạy thật: [.codex/sql-live-functions.md](.codex/sql-live-functions.md). Khi `context.md` lệch với code-map và code, tin code-map và code.
- Khi bắt đầu task, kiểm tra local notes và bộ AGENTS/.codex của workspace cha có thiếu không. Nếu thiếu, đọc [.codex/bootstrap.md](.codex/bootstrap.md) và khởi tạo theo điều kiện an toàn ở đó trước khi tiếp tục; chỉ tạo phần thiếu, không ghi đè. Plan Mode/read-only không ghi file.

- Trước thao tác, đọc [.codex/context.md](.codex/context.md), [.codex/workflow.md](.codex/workflow.md), rồi `.codex/local/handoff.md` nếu có. Xác minh lại trạng thái Git; ghi chú cũ không chứng minh remote mới nhất.
- Sau đó đọc [.codex/skills-inventory.md](.codex/skills-inventory.md) để chọn đúng skill project-local; chỉ đọc `SKILL.md` liên quan đến task.
- Tìm bài học liên quan trong `.codex/local/lessons.md` và [.codex/lessons.md](.codex/lessons.md); không đọc toàn bộ lịch sử hoặc mọi skill mặc định.
- Task mới phải có nhánh riêng (`feature/`, `fix/`, `docs/`…) từ `origin/main` đã fetch, PR vào `main`; `develop` không còn cập nhật từ 15/10/2026. Không code trực tiếp trên `main`.
- Dùng [skill bàn giao BE](.agents/skills/fire3d-be-handoff/SKILL.md) khi tiếp tục hoặc chốt công việc. Nếu host chưa khám phá skill, đọc trực tiếp file này; không giả định chỉ tạo .codex là nội dung đã được tự nạp.
- Checkpoint/bài học phiên chỉ ghi vào .codex/local/ (được ignore); không tự push, merge hay ghi nhật ký vào file dùng chung.
- Nếu ghi chú local chưa có, tạo khi được phép ghi; trong Plan Mode chỉ bàn giao trong chat. Không lưu secrets kể cả ở local notes.
- Bộ hướng dẫn này dùng độc lập với workspace cha. Khi thiếu Docs cho quyết định sản phẩm, báo thiếu thay vì tự clone hoặc đoán.
