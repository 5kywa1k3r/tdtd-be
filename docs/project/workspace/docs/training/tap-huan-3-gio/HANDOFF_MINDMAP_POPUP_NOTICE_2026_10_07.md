# Popup và thông báo thiếu tổng hợp — 07/10/2026

## Quyết định mới nhất: chỉ xem kết quả trong mindmap

Các entry tổng hợp của Work, assignment và trang mindmap truyền `resultOnly=true`. Ẩn cấu hình/xem trước cả trong popup lẫn ActionToast; không mount editor và không hướng dẫn mở cấu hình ở màn này. Chưa có kết quả vẫn thông báo, không mở popup rỗng. Các entry ngoài mindmap giữ luồng cấu hình hiện hữu. Nội dung bên dưới mô tả lượt trước; nút Cấu hình trong toast đã bị thay thế theo quyết định mới này.

Đổi “Tổng hợp nhiệm vụ con” thành “Tổng hợp công việc con”, đồng thời đổi nhãn truy cập của nút xem chi tiết tương ứng. Tên Work/nhiệm vụ, chỉ tiêu ở cấp gốc giữ đúng nghĩa nghiệp vụ.

- Mindmap giữ fullscreen. Các popup người được giao, đơn vị, công việc con, báo cáo, xem biểu mẫu và tổng hợp dùng Dialog căn giữa. Bảng tùy chọn người/bộ lọc trong node cũ cũng chuyển từ Popover bám card sang Dialog căn giữa.
- Entry tổng hợp trong mindmap (`allowFormSelection=false`) chờ API `aggregate-v2/views/read` trả về trước khi mở cửa sổ; đang kiểm tra thì hiện spinner nhỏ. Không thêm request kiểm tra riêng và không tải trước khi bấm.
- Nếu chưa gắn biểu mẫu: dùng lại `ActionToast`, thông báo “Chưa có biểu mẫu tổng hợp cho công việc này.” Không mở popup.
- Nếu đã gắn nhưng chưa có `result`: thông báo “Chưa có kết quả báo cáo tổng hợp.” Có nút Cấu hình nếu `canEdit=true` và có context hợp lệ, đi vào editor chính thức với popup căn giữa. Không tạo báo cáo, không tự tính hay áp dụng kết quả.
- Lỗi API/quyền đọc hiện thông báo lỗi, không coi là chưa có dữ liệu. Nếu có result nhưng nguồn không còn đủ điều kiện đọc, giữ xử lý lỗi nguồn và đường sửa cấu hình hiện hữu.
- `ActionToast` thêm prop action tùy chọn; các nơi dùng trước giữ mặc định. Toast được portal ra ngoài vùng zoom của cây nhưng bên trong dialog chứa mindmap để thao tác bàn phím vẫn nằm trong focus trap. Trang không có dialog dùng document.body.
- Các entry ngoài mindmap giữ luồng chọn biểu mẫu/cấu hình hiện hữu; không sửa BE, hợp đồng API, phép tính, quyền hay vòng đời báo cáo.

Kiểm tra: 16 test Aggregate View; 30 test node controls/report popup/unit chips/recipients. Có kiểm không mở dialog trong lúc chờ, trạng thái chưa có dữ liệu và lỗi đọc tách biệt, cấu hình từ thông báo, và đóng/mở lại dialog tùy chọn. TypeScript app và lint qua. Không kiểm trực quan browser do truy cập localhost đã bị policy chặn ở lượt trước. Không restart FE/BE, không sửa dữ liệu nghiệp vụ, không commit/push.
