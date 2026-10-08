# Quy tắc màu và chữ của giao diện

## Nguồn cấu hình

- `index.tsx`: bốn tông giao diện; registry `appThemes` dùng chung với bộ chọn và lưu tùy chọn.
- `createAppTheme.ts`: ghép palette với màu bề mặt, trạng thái điều khiển, bảng, nút, hộp thoại. Mọi theme đều cung cấp CSS variables; không dựa vào màu dự phòng chỉ dành cho bản sáng.
- `typography.ts`: font và cấp chữ dùng chung. Mantine lấy font từ theme MUI trong `AppMantineProvider.tsx`.
- `canvasTheme.ts`: bộ màu riêng của Canvas, chỉ phụ thuộc sáng/tối. Màu dây, cổng, biến và trạng thái không thay đổi theo Xanh đậm/Xanh xám/Xanh rêu.
- `chartTheme.ts`: nền/chữ tooltip và trục biểu đồ. Màu chuỗi dữ liệu giữ theo định nghĩa nghiệp vụ.

## Chữ

Font giao diện là Inter, được phục vụ cục bộ. Chữ nội dung 400; nhãn 500; tiêu đề nhóm/nút 600; tiêu đề trang/thẻ 700. Không tự dùng 750/850/900 trong màn mới.

| Vai trò | Cách dùng |
| --- | --- |
| Tiêu đề trang | `Typography variant="h5"`, chữ chính |
| Tiêu đề thẻ/hộp thoại | `h6` hoặc `DialogTitle`, chữ chính |
| Tiêu đề nhóm | `subtitle1`/`subtitle2`, chữ chính |
| Nội dung | `body1`/`body2`, `text.primary` |
| Mô tả, chú thích | `body2`/`caption`, `text.secondary` |
| Điều khiển không khả dụng | trạng thái `disabled`, `text.disabled` |
| Liên kết/thao tác | `primary.main` |
| Thành công/cảnh báo/lỗi/thông tin | `success`/`warning`/`error`/`info` |

Không giảm opacity cả khối để làm chữ phụ. Chữ ở Header hoặc trên nút nền màu phải theo cặp màu nền–chữ của vùng đó, không ép `text.primary` lên mọi phần tử con.

## Nút và bề mặt

- Hành động chính: `Button variant="contained" color="primary"`.
- Hành động phụ: `outlined`; hủy/quay lại ít nổi bật: `text` hoặc `outlined` theo bố cục.
- Hành động xóa/nguy hiểm: `color="error"`; không tự gán màu đỏ bằng mã hex.
- Không tự thêm gradient hoặc sửa riêng màu hover của nút trong từng màn. `workActionStyles.ts` chỉ giữ bố cục/kích thước, dùng màu và trạng thái từ theme chung.
- Dùng `background.default`, `background.paper`, `text.primary`, `text.secondary`, `divider` trong MUI. Với HTML/SVG dùng `--app-surface`, `--app-surface-muted`, `--app-ink`, `--app-ink-muted`, `--app-border`, `--app-selected`, `--app-hover`.
- Dùng `getAppColors(theme)` khi cần màu cụ thể để gọi `alpha`. Không đưa chuỗi `var(...)` vào `alpha` của MUI.
- Nút và tiêu đề trên thanh công cụ Canvas vẫn dùng theme giao diện. Phần thể hiện dữ liệu Canvas dùng `getCanvasColors(mode)`/`--canvas-*`.

## Nội dung có định dạng

Trang soạn văn bản và bảng tính là vùng tài liệu. Giữ nền giấy, màu chữ và màu ô do người dùng đặt; không đảo màu hay phủ CSS toàn bộ phần tử bên trong. Các token `--app-document*` và vùng Fortune được tách khỏi giao diện tối. Đổi theme không sửa schema, màu nhãn do người dùng chọn, định dạng ô hoặc nội dung lưu.

## Kiểm tra khi thêm/sửa theme

Chạy `tests/ThemeSelection.test.tsx`: lựa chọn/lưu tùy chọn, tương phản cặp màu, độc lập màu Canvas và tài liệu. Kiểm màn thật có bảng, nút thường/rê chuột/focus/disabled, ô nhập/lỗi/chỉ đọc, popup Mantine và Canvas. Palette đạt kiểm tra tự động không chứng minh mọi màu pha alpha hoặc mọi màn đều đã đạt.
