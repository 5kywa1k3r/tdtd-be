# Handoff refactor theme, nút và chữ — 06/10/2026

## Quyết định trực tiếp

- Refactor theme phần mềm TD-TD; bốn lựa chọn phải đều có icon.
- Thống nhất màu nút, tiêu đề thẻ, màu chữ, mức đậm nhạt và font.
- Canvas có bộ màu riêng, cần tách khỏi màu giao diện chung.
- Đây là công việc FE tiếp nối yêu cầu dark mode, không phải sửa HTML hướng dẫn hoặc thực hiện nghiệp vụ UAT.

## Đã làm

- `tdtd-fe/src/theme/createAppTheme.ts`: một nơi tạo các quy tắc bề mặt/chữ/nút/ô nhập/bảng/hộp thoại/tooltip cho bốn theme. CSS variables có ở cả sáng và tối. Tách màu đường viền điều khiển với đường phân cách.
- `typography.ts`: Inter cục bộ; nội dung 400, nhãn 500, nhóm/nút 600, tiêu đề 700; định nghĩa cỡ chữ và giãn dòng theo variant. Chuẩn hóa các mức 750–950 trên các thành phần công việc/tổng quan đã rà. Không ép đổi font nội dung báo cáo.
- `ThemeColorPicker.tsx`: sóng, mây, cây, trăng; icon vẫn hiện khi được chọn, thêm dấu tích nhỏ và viền; có tên qua tooltip và aria-label.
- `AppMantineProvider.tsx`: nhận màu và font từ MUI thay vì giữ một bộ mã màu dark riêng.
- `workActionStyles.ts`: bỏ gradient/màu hover tự định nghĩa; giữ bố cục và dùng quy tắc Button chung. Nút tiêu hủy vẫn dùng `error`; chữ trên nút nền màu theo cặp contrastText.
- `AppTable`: đầu bảng, nút sắp xếp và checkbox dùng cùng cặp nền–chữ; màu dòng hover và viền lấy từ theme. Không còn nền hover quá sáng ở dark.
- Migrate màu nền/chữ cố định tại Work detail, giao việc, bảng lọc, tài liệu, Flow runtime, Tổng quan, cây/khung chi tiết sơ đồ, vùng thiết kế biểu mẫu cũ và màn tổng hợp văn bản. Chỉnh chữ phụ navy vì thiếu tương phản trên nền đang chọn.
- `chartTheme.ts`: trục/chữ/tooltip theo theme, giữ màu chuỗi dữ liệu. Màu nhãn do người dùng chọn vẫn giữ nguyên.
- `canvasTheme.ts`: dây/cổng/trạng thái/biến công thức có palette riêng sáng/tối; ba tông sáng dùng cùng màu Canvas. AggregateRecipeCanvas và header/canvas cấu trúc dùng bộ màu này; nút/menu/hộp thoại tiếp tục dùng theme chung.
- Vùng soạn văn bản giữ nền giấy và màu chữ tài liệu; Fortune giữ color-scheme sáng trong vùng tài liệu. Không viết lại định dạng ô hoặc màu người dùng đã lưu.
- Quy tắc dùng tiếp: `tdtd-fe/src/theme/README.md`.

## Bằng chứng kiểm tra

- `node node_modules/typescript/bin/tsc -p tsconfig.app.json --noEmit`: đạt.
- ESLint các tệp TS/TSX chạm trong lượt refactor: đạt.
- Kiểm whitespace diff trong phạm vi tệp đã chạm: đạt.
- `tests/ThemeSelection.test.tsx`: 9 ca đạt; bốn lựa chọn/icon/lưu lựa chọn/storage bị chặn; tương phản cặp màu >= 4.5; màu Canvas độc lập với ba tông sáng và nền tài liệu không đổi.
- `tests/workInbox.page.test.tsx`: 6 ca đạt.
- `canvas-wire-filter.mounted.test.tsx` qua `src/features/aggregateMapping/contractChecks/vitest.p04.config.ts`: 22 ca đạt sau lần đổi màu Canvas cuối. Đây là test component, không phải nghiệm thu Canvas trên trình duyệt.
- Browser localhost với pv01: chuyển đủ bốn theme; quan sát Tổng quan, danh sách công việc, hộp tạo nhiệm vụ, Canvas biểu mẫu bản nháp; không lưu/công bố/nộp dữ liệu. Mở hộp tạo chỉ để kiểm giao diện, không tạo nhiệm vụ.
- DOM thật ở theme Xanh rêu: nút tạo có nền rgb(46,79,79), chữ rgb(236,242,242), đậm 600; đầu bảng cùng cặp màu, đậm 700. Hộp nhập dark hiển thị font Inter và chữ sáng trên nền tối.
- Ảnh cuối: `images/theme-dark/refactor-four-theme-icons.png`, `images/theme-dark/refactor-light-table.png`.

## Giới hạn và phần cần kiểm tiếp

- Không chạy build, BE, seed; không sửa dữ liệu nghiệp vụ. WIP của các phần khác giữ nguyên, chưa commit.
- Không kết luận tất cả màn và mọi trạng thái đều đã UAT. Chưa kiểm đủ mobile, in/xuất, autofill thực tế, rich text có mọi loại màu tùy chọn và mọi ô Fortune. Không có đo INP trong lượt theme này.
- Các mã màu còn lại cần phân loại trước khi sửa: bảng màu nhãn/chuỗi biểu đồ, định dạng ô và import/export, bóng đổ/trang trí, màu riêng Canvas; không thay thế hàng loạt bằng palette chính.
- `src/index.css` là stylesheet mẫu chưa được entry point hiện tại import; vẫn có màu cố định/media query theo hệ điều hành. Nếu đưa vào dùng phải bỏ quy tắc màu đó để tránh ghi đè lựa chọn của người dùng.
- Handoff dark mode trước đây ghi CSS variables chỉ áp dụng dark; quy tắc đó đã được thay bằng bộ token chung sáng/tối trong lượt này. Không coi ảnh/test PASS là Yud đã nghiệm thu toàn bộ giao diện.
