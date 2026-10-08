# Bàn giao tài liệu v0.8.7 — riêng dự án guide

## Bản để rà

- [index.html](../index.html#lo-trinh-180-phut): một menu, năm chương. Lộ trình có hai luồng: demo xử lý báo cáo Mẫu 1 hiện có và bài thực hành M1 Số hóa hồ sơ.
- [Markdown Mẫu 2](../content/mau2-guide.md): 9 nhóm tra cứu, 5 nhóm M01, 6 nhóm M02. Bảng nhập đủ QT01/QT02, mô hình M02, Bảng sản phẩm và Bảng hai chiều. Kết thúc bằng Đội 2 đọc Đã duyệt; kỳ 05–07 còn Cần nộp.
- [Markdown Số hóa hồ sơ](../content/digitization-guide.md): 7 mốc, cây 1–2–4, sáu báo cáo nguồn, bốn trường Số; đề bài, đáp án và tự kiểm. Bài M1 này khác M01 Kho hồ sơ.
- [Giáo án giảng viên](GIAO-AN-V087.md): 180 phút, Tổng hợp 50 phút, nghỉ 20 phút; chờ duyệt theo yêu cầu mới nhất. Mỗi nhóm dùng một báo cáo còn mở riêng; bộ đã duyệt phục vụ đọc lại.
- Bản in: [giáo án 2 trang](../output/pdf/giao-an-v087.pdf), [Số hóa 17 trang](../output/pdf/so-hoa-ho-so-v087.pdf), [M02 16 trang](../output/pdf/mau2-m02-v087.pdf), [chương V 135 trang](../output/pdf/chuong-v-v087.pdf), [sổ tay 189 trang](../output/pdf/toan-bo-v087.pdf).

## Nội dung đã thay đổi

Mẫu 2 giảm từ 60 xuống 20 nhóm, từ 230 xuống 105 bước. M02 giảm từ 83 xuống 38 bước, bản in từ 28 xuống 16 trang. Vai ghi ở đầu nhóm hoặc khi đổi; bảng Trường — Giá trị thay phần lặp từng ô. Lưu/Công bố, Lưu/Nộp, ngày hoàn thành và kết thúc đọc Đã duyệt vẫn có bước riêng.

M03/M04 chỉ bổ sung biến thể cấu trúc, Nhập thử, Công bố và đọc kết quả theo ảnh đã có. Không thêm hai luồng dài. Ghi chú lặp được rút khỏi phần công khai; giới hạn quan trọng vẫn có ở nhóm và QA.

Bài Số hóa dùng ba SUM và một AVG với GET giữ tập. Các mốc đối chiếu:

| Mốc | Tiếp nhận | Đã số hóa | Giờ tiết kiệm | Điểm |
|---|---:|---:|---:|---:|
| PX01 | 210 | 180 | 60 | 8 |
| Gốc chỉ PX01 | 210 | 180 | 60 | 8 |
| PA02 tháng 9 | 150 | 123 | 41 | 8,5 |
| Gốc đủ hai phòng | 360 | 303 | 101 | 8,25 |

Cấp gốc chỉ lấy hai báo cáo phòng trực tiếp, không cộng lại bốn báo cáo phường. Điểm là trung bình hai phòng. Bài dùng duyệt thủ công; giữ bốn nguồn đã duyệt, một Nháp và một chờ duyệt. Core M1 không có ONLY/IF hoặc Danh sách/Bảng.

Lịch nghỉ 20 phút thay lịch 10 phút cũ. Tổng vẫn 180 phút, giữ 50 phút Tổng hợp. Giáo án có thời gian, việc của giảng viên/lớp, kết quả, chuẩn bị trước và thứ tự cắt khi chậm. Chưa chốt chương trình hoặc dựng lại dữ liệu.

## Kiểm và bảo toàn

Build/check Node đạt; 27 nhóm DOM, 26 nhóm tương tác, 13 ca model/controller. Kiểm HTML thật ở desktop 1365 px và màn 768/390 px; ảnh gốc, Q&A, neo cũ, mục thu gọn và phạm vi in hoạt động. Demo Mẫu 1 đi đủ 20 cảnh bằng Tiếp, giữ trả lại–sửa–nộp lại–duyệt và chuyển vai. Chưa chạy lại cả 20 cảnh theo timer trong lượt này.

Bảy artifact Mẫu 1, 13 bảng Mẫu 2 và 468 neo cũ được giữ. Không reset hoặc xóa WIP. Có năm PDF đúng phạm vi; 95 trang được render/rà bố cục. Chưa in giấy hoặc rà hình toàn bộ 189 trang. Máy không có npm; đã chạy đúng các lệnh Node tương đương package check.

[QA đầy đủ](QA-V087.md), [file thay đổi](training-v087/FILE-CHANGES.md), [nguồn và mức bằng chứng](training-source-v087.json).

## Ảnh và phần còn cần kiểm

[Bảng 59 ảnh theo bước](TRAINING-V087-IMAGES.md), [manifest Mẫu 2](mau2-images-v087.json), [manifest Số hóa](digitization-images-v087.json), [ảnh/nhãn cần bổ sung](training-v087/IMAGE-GAPS.md).

Chưa đủ ảnh để gọi phần ảnh hoàn chỉnh. Cần ưu tiên tạo/đổi tên/cấu hình Danh sách, lưu–mở lại, Nhập thử/Công bố, đại diện nhận, đầy đủ các khối báo cáo M02 và ngày hoàn thành. Ảnh PX01/PA02 Số hóa chỉ thấy trường đầu 210/150; cần ảnh đủ bốn số. Ảnh gốc thấy đủ bốn số. Không dùng ảnh mindmap sai hạn hoặc Nhập thử thay báo cáo thật. Tên file không thay bằng chứng chặn thiếu ngày.

Ca lọc độc lập 01–30/09 → 01–29/09 → khôi phục còn cần kiểm trên nháp/xem trước riêng. Kỳ vọng lần lượt 150/123/41/8,5 → 70/63/21/9 → 150/123/41/8,5. Tháng 10 bị loại theo trạng thái chưa đủ chứng minh bộ lọc ngày. Dashboard/cây/hạn mới, hai báo cáo phòng đã duyệt và lượt chạy trên bản deploy còn cần bằng chứng.

KUI-01–07 có mức source/test/browser khác nhau; KUI-08/09 còn mở; KUI-10 với ONLY ngoài core M1. Không lấy kiểm guide làm nghiệm thu sản phẩm.

Mẫu 2 giữ hạn cấp đội 07:00 Việt Nam (=00:00 UTC), không còn yêu cầu 22:00. Báo cáo quá khứ cần xác nhận ngày. Tự duyệt kinh phí ≥0 là cấu hình bài, chưa chứng minh ngày hiện tại. PX01 của Mẫu 2 còn Nháp, một nguồn M01; View nhiệm vụ khác Tổng hợp vào báo cáo gửi cấp trên.

Ngày 04 cả bốn mô hình đã duyệt theo bằng chứng PA02 mới nhất. Không giữ PA02/M03/M04 chưa chạy làm hiện trạng. Ngày 05–07, tự duyệt hôm nay, tổng hợp đủ nguồn, báo cáo phòng và truy nguồn hai tầng vẫn còn UAT. Thông báo có sau tải lại/lượt đọc sau và mở đúng việc; chưa đo độ trễ hoặc tự cập nhật.

Chỉ chụp đọc lại dữ liệu đã lưu khi được cấp phiên riêng. Không tự sửa, nhập, lưu, nộp, duyệt lại dữ liệu thật để dựng ảnh. Lượt tài liệu không sửa/chạy sản phẩm, API/DB, seed/reset, build/restart hoặc thay dữ liệu UAT.

## Tiếp tục sửa

Sửa content/mau2-guide.json, digitization-guide.json, course-guide.json và renderer tương ứng. node tools/build.mjs sinh index.html, hai Markdown hướng dẫn và Markdown giáo án. Không sửa tay bản sinh. Giữ QA cũ và WIP; đọc AGENTS hiện hành trước. Bổ sung ảnh theo manifest và giới hạn thật, không thay hash để che nguồn đổi.
