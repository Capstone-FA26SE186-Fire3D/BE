# IFC capability spike — Week 2

Thử nghiệm local riêng cho BE/IFC worker, theo lựa chọn Python + IfcOpenShell trong Docs. Không chạy parser trong HTTP request, không thêm API upload hay thay đổi database IFC.

## Cách chạy (từ gốc BE, Windows)

```powershell
python -m venv .codex/local/ifc-venv
& .codex/local/ifc-venv/Scripts/python.exe -m pip install -r tools/ifc-spike/requirements.lock.txt
& .codex/local/ifc-venv/Scripts/python.exe -m unittest discover -s tools/ifc-spike -p 'test_*.py' -v
& .codex/local/ifc-venv/Scripts/python.exe tools/ifc-spike/inspect_ifc.py tools/ifc-spike/samples/fire3d-synthetic.ifc --output .codex/local/ifc-report.json
```

Toolchain đã thử: Windows x64, Python 3.13.14, IfcOpenShell 0.8.5. requirements.lock.txt ghi các dependency thực tế; chưa xác nhận nền tảng khác.

## Đầu ra và quy ước

- JSON gồm schema, SHA-256 của bytes nguồn, kích thước, toolchain, tỷ lệ đơn vị sang mét, tầng/GUID/elevation, số đối tượng, số đỉnh/tam giác và bounding box mỗi mesh.
- Bounds geometry là tọa độ thế giới tính bằng mét; elevation tầng được đổi từ đơn vị IFC sang mét. storeyGuid cho biết container trực tiếp/phân giải của phần tử nếu là tầng.
- Exit 0: parse và tạo mesh đạt tiêu chí spike; exit 1: đầu vào bị từ chối; exit 2: thiếu/lỗi representation hoặc không tạo được mesh, cần review.
- Chặn file rỗng, sai extension/envelope STEP, vượt 32 MiB, thiếu project/tầng/đơn vị. 32 MiB chỉ là giới hạn thử nghiệm, chưa phải giới hạn upload sản phẩm.
- File có header/envelope hợp lệ vẫn cần được parser đọc; kiểm tra này không thay thế full IFC schema validation.

## Bằng chứng

[samples/fire3d-synthetic.ifc](samples/fire3d-synthetic.ifc) do script generate_fixture.py của dự án tự tạo, không chứa bản vẽ/dữ liệu bên ngoài. Fixture IFC4 có 2 tầng ở 0m/3m và mỗi tầng một tường 4 x 0.2 x 3m. Đây không phải mô hình công trình đầy đủ.

[samples/inspection.json](samples/inspection.json): 2 mesh, mỗi mesh 8 đỉnh/12 tam giác; bounds z lần lượt 0–3m và 3–6m; không có issue. Runtime trong report chỉ là một lần chạy trên fixture rất nhỏ, không phải benchmark.

7 test đã đạt: metadata/geometry/hash; tính tương đương mét–milimét; file rỗng/truncated; sai extension/không tồn tại/quá kích thước; thiếu đơn vị; thiếu representation; CLI và chống ghi output đè source. IFC4 đã có fixture kiểm thử; parser cho phép IFC2X3 nhưng tính tương thích IFC2X3 chưa được xác nhận bằng fixture thực tế.

## Giới hạn và bước tích hợp

Chỉ chạy file local tin cậy trong thử nghiệm này. Chưa có giới hạn CPU/thời gian, cách ly native parser, full schema validation hoặc bộ mẫu Revit/ArchiCAD. Trước nhận upload thật cần worker process có timeout/resource budget, quarantine và kiểm tra quyền sở hữu.

Luồng cần triển khai sau: API kiểm tra role/tenant → nguồn private có hash → durable processing job → worker đọc IFC → geometry/floor/issue artifacts gắn revision/job/toolchain → cập nhật kết quả/idempotency. Không giữ transaction DB trong lúc convert.

Chưa xuất GLB, graph điều hướng, cửa/lối thoát có ngữ nghĩa, Unity bundle hoặc kiểm tra PCCC. Không dùng kết quả spike để đánh dấu toàn bộ FR-IFC-01..04 hoàn thành. Team cần thêm file IFC công trình đại diện và tiêu chí door/stair/space/exit.

Nguồn kỹ thuật: [IfcOpenShell installation](https://docs.ifcopenshell.org/ifcopenshell-python/installation.html), [Python API examples](https://docs.ifcopenshell.org/ifcopenshell-python/code_examples.html), [wall representation API](https://docs.ifcopenshell.org/autoapi/ifcopenshell/api/geometry/add_wall_representation/index.html).