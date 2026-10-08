# Handoff UAT Mẫu 2 — nhánh M02 của PX01

Ngày 07/10/2026, giờ Việt Nam. Tiếp tục sau [handoff sửa bốn vướng mắc](HANDOFF_UAT_MAU_2_FOUR_FIXES_2026_10_07.md). Bằng chứng của lượt này là thao tác và đọc lại trên UI thật; không chạy API, DB, seed, build hoặc restart. Không sửa sản phẩm hoặc quyền.

**Kết quả:** đã tạo mẫu M02 riêng của PX01, công bố, giao hằng ngày cho Đội 2 và hoàn thành lưu–nộp–duyệt báo cáo ngày 04/10. Còn điểm cần kiểm về cập nhật thông báo trong phiên đang mở. Chưa nghiệm thu toàn bộ Mẫu 2.

## Quyết định và dữ liệu giữ nguyên

- Hạn ngày cấp đội: **00:00 UTC = 07:00 Việt Nam**, theo quyết định trực tiếp mới. Cấu hình giờ khác làm sau; đề xuất 22:00 cũ không còn là điều kiện đạt của bài hiện tại.
- Khoảng báo cáo **04–07/10/2026**; mỗi phòng tự tạo hoặc sao chép mẫu riêng rồi giao xuống đội.
- Work Mẫu 2 `6ac53a44c4378171baa7a1f8`, hai phần việc phòng và M01 giữ nguyên. Không tạo lại nhánh cha.
- Không nộp report PX01, không hoàn thành Work hoặc assignment. Đã duyệt một báo cáo ngày không có nghĩa đã hoàn thành cả lịch.

## Điểm nối M01 → PX01 đã kiểm lại

1. Đăng nhập PX01, mở report tổng hợp tại PeriodId `6ac53dc3c4378171baa7af22`.
2. Màn nhập báo cáo hiện **Nháp**, **Chưa thay đổi**. Bảng Đơn vị — Khó khăn giữ dòng **Đội 1 / Kỳ 04/10/2026 / Tên hồ sơ thử chưa thống nhất.**
3. Bấm **Mở báo cáo nguồn**. Hộp mở đúng mẫu M01 PX01, **Đã duyệt**, chỉ xem; ngày triển khai và ngày hoàn thành đều **04/10/2026**, nội dung và Bảng/Danh sách giữ đúng.
4. Đóng hộp trở về report PX01. Không lưu hoặc nộp thêm.

Lượt này kiểm độc lập đường mở nguồn từ bảng đã lưu. Bằng chứng Canvas, preview và hai chu kỳ làm mới quyền đọc thuộc handoff sửa trước; không ghi là Lam đã chạy lại toàn bộ các ca đó.

## Biểu mẫu M02

| Thông tin | Giá trị đọc trên UI |
|---|---|
| Chủ sở hữu | `px01` — Phòng Tổ chức - Cán bộ |
| Tên | [TẬP HUẤN Mẫu 2] PX01 — Quy trình công tác cán bộ, báo cáo ngày |
| FormId | `6ac55b616710e17b1dc3fa67` |
| Mã / phiên bản | `FORM-px01-2026-000003` / v1, Đã công bố |
| Nguồn sao chép | M01 PX01 `6ac540c5c4378171baa7bc32`, giữ nguyên bản công bố |
| Cấu trúc | 21 trường, 2 Bảng, 2 Danh sách |

Các bước đã chạy:

1. **Biểu mẫu động → Thao tác khác → Sao chép thành biểu mẫu mới** trên M01 PX01.
2. **Thông tin biểu mẫu**: đổi tên và mô tả dữ liệu giả M02.
3. Sửa Danh sách thành **Quy trình đã hướng dẫn**, phần tử **Quy trình**, nút **Thêm quy trình**, ít nhất 0 / nhiều nhất 200.
4. Mẫu phần tử: **Mã quy trình — Văn bản tự do, bắt buộc; Tên quy trình — Văn bản tự do, bắt buộc; Số bước hướng dẫn — Số; Đã rà soát — Có/không**. Tóm tắt hiện mã và tên.
5. Ô Tên quy trình được thêm bằng kiểu **Nội dung** trong bộ chọn; cấu trúc sau thêm/lưu hiện **Văn bản tự do**, màn nhập là ô văn bản. Đã bỏ trường Số hồ sơ cũ trên bản sao; không chỉ đổi tên một trường số thành tên quy trình.
6. Giữ 9 trường chung, Bảng sản phẩm một chiều dọc, Bảng nội dung/kinh phí hai chiều và Danh sách Thông tin mô hình 1–1 dòng.
7. **Lưu biểu mẫu**: thấy **Đã đối chiếu dữ liệu đã lưu**. **Nhập thử** QT01 / Cập nhật thông tin cán bộ / 4 / Có, xác nhận đúng một dòng; dữ liệu thử không phải report.
8. **Công bố → Xác nhận**: đọc lại **Biểu mẫu v1 · Đã công bố**. Khi giao việc, bộ chọn có đúng M02 v1, mã 000003.

## Giao M02 và kiểm người nhận

| Đối tượng | Giá trị thực tế |
|---|---|
| Assignment | `WA000006`, id `6ac55c8c6710e17b1dc3fed9` |
| Cha trực tiếp | PX01 tổng hợp hai mô hình, `WA000003`, id `6ac53db8c4378171baa7af06` |
| Người giao | PX01 |
| Đơn vị nhận | Đội 2, ký hiệu `PX01_DOI2` |
| Đại diện nhận | Quản lý đơn vị Đội 2 — PX01, tài khoản `px01_doi2` |
| Hình thức | Định kỳ báo cáo / Hàng ngày |
| Khoảng / áp dụng | 04–07/10/2026 / bắt đầu áp dụng 04/10/2026 |
| Tự duyệt | Kinh phí sử dụng trong ngày ≥ 0 |
| Mẫu được ghim | M02 PX01, mã 000003 v1 |

Các bước đã chạy:

1. Từ **Giao việc** của Work Mẫu 2, mở hộp **Giao việc**, chọn nhánh cha PX01.
2. Đặt tên M02, mô tả dữ liệu giả; chọn đúng mẫu riêng PX01 đã công bố.
3. Chọn Định kỳ báo cáo / Hàng ngày; nhập từ, đến, ngày áp dụng; bật điều kiện tự duyệt nêu trên.
4. Trong **Đơn vị nhận việc**, tìm **Đội 2**: chỉ có kết quả **Đội 2 · PX01_DOI2**. Bấm chọn, trạng thái chọn đổi ngay, trở về hộp giao hiện Đội 2. Không chọn cá nhân chồng phạm vi.
5. **Xem lịch báo cáo**: chỉ các ngày 04, 05, 06, 07/10 được mở trong khoảng; ngày ngoài khoảng bị khóa.
6. **Tạo mới**: bảng giao việc có WA000006 cho Quản lý đơn vị Đội 2 — PX01. **Xem chi tiết** đọc lại đúng cha, mẫu, lịch và tự duyệt ≥ 0.
7. Tra danh mục bằng **menu tài khoản → Quản trị tài khoản → Người dùng**, tìm Đội 2. Xác nhận `px01_doi2` là đại diện; `px01_doi2_doitruong`, `px01_doi2_doipho`, `px01_doi2_canbo` là tài khoản cá nhân riêng. Chỉ đọc danh mục, không sửa tài khoản.
8. Đăng nhập `px01_doi2`, banner đúng Đội 2 — PX01. Chuông hiện Được giao việc lúc **03:39**, ba Báo cáo đến hạn và một Sắp đến hạn lúc **03:40**.
9. Bấm Được giao việc → đúng inbox M02 → **Xem kỳ báo cáo**: đủ bốn ngày, hạn **07:00** từng ngày, ba quá hạn và một sắp tới hạn tại mốc kiểm.

Ảnh: [cấu hình đã giao](images/mau-2-m02-giao-dinh-ky-2026-10-07.jpg), [thông báo nhận việc](images/mau-2-m02-thong-bao-2026-10-07.jpg), [bốn kỳ trước nhập](images/mau-2-m02-bon-ky-2026-10-07.jpg).

## Báo cáo thật M02 ngày 04/10

PeriodId trên URL: `6ac55cb06710e17b1dc3ff01`. ReportId đọc ở nút duyệt: **`6ac55cf66710e17b1dc40327`**. AssigneeId trên đường dẫn màn duyệt: `6ab4d0073633fb260f57deb7`. Không dùng PeriodId thay ReportId.

Dữ liệu nhập theo [M02 ngày 04](mau-2-bon-mo-hinh/data/02_QUY_TRINH_CAN_BO.md):

| Nội dung | Dữ liệu đã lưu và đọc lại |
|---|---|
| Kinh phí | 0 triệu đồng |
| Nội dung triển khai | Rà soát hai quy trình hướng dẫn đang dùng trong bài. |
| Kết quả | Đã lập sơ đồ hai quy trình đầu tiên; chưa đủ tình huống để đánh giá. |
| Mức đánh giá | Chưa đủ cơ sở đánh giá |
| Ngày triển khai | 04/10/2026 |
| Khó khăn | Chưa đủ tình huống để kiểm cách hướng dẫn. |
| Kiến nghị | Bổ sung tình huống giả để kiểm. |
| Công việc tiếp theo | Mô tả hồ sơ đầu vào. |
| Quy trình | QT01 / Cập nhật thông tin cán bộ / 4 / Có; QT02 / Tiếp nhận hồ sơ đề nghị / 5 / Có |
| Mô hình | M02 / Mô hình hóa quy trình hướng dẫn các mặt công tác tổ chức cán bộ / Cán bộ A2 (giả định) |
| Bảng sản phẩm | M02-1004 / Sơ đồ quy trình đợt 1 / ghi chú là nội dung khó khăn |
| Bảng hai chiều | Chuẩn bị: nội dung triển khai, kinh phí 0; Vận hành: kết quả, kinh phí 0 |
| Minh chứng | Trống, trường không bắt buộc; chưa kiểm tải tệp |
| Ngày hoàn thành | Nhập riêng 04/10/2026; không tự lấy từ trường Ngày triển khai |

Các bước và kết quả:

1. Mở kỳ 04, bấm **Bắt đầu nhập bảng**, nhập đủ phần chung, hai Danh sách và hai Bảng.
2. **Lưu nháp** khi Ngày hoàn thành trống: lưu được, trạng thái Nháp, toàn bộ dữ liệu được đọc lại.
3. **Nộp báo cáo** khi Ngày hoàn thành trống: bị chặn, hiện **Trước khi nộp báo cáo quá khứ, bắt buộc nhập ngày hoàn thành.** Không mất dữ liệu. Ảnh [ca thiếu ngày](images/mau-2-m02-thieu-ngay-bi-chan-2026-10-07.jpg).
4. Nhập riêng **04/10/2026** vào Ngày hoàn thành → Lưu nháp → tải lại trang. Ngày và dữ liệu Bảng/Danh sách giữ đúng, các ô số 0 vẫn có giá trị.
5. **Nộp báo cáo**: thành **Đã nộp**, chỉ xem, Ngày hoàn thành vẫn 04/10. Không tự duyệt báo cáo quá khứ dù thỏa kinh phí ≥ 0. Ảnh [đã nộp](images/mau-2-m02-da-nop-ngay-04-2026-10-07.jpg).
6. PX01: **Công việc cần thực hiện → Duyệt báo cáo (1) → Xem danh sách báo cáo → Mở màn duyệt** đúng Đội 2/kỳ 04. Dữ liệu quá khứ hiện **Cần xác nhận**.
7. **Xem báo cáo**: nội dung đúng, ngày hoàn thành 04/10, không có quyền sửa trực tiếp tại màn xem.
8. **Duyệt báo cáo kết quả**: hộp xác nhận có **Kỳ báo cáo 04/10/2026, Ngày hoàn thành 04/10/2026**. Bấm **Xác nhận và duyệt**. Ảnh [xác nhận duyệt](images/mau-2-m02-xac-nhan-duyet-2026-10-07.jpg).
9. Đội 2 tải lại nguồn: **Đã duyệt**, chỉ xem, ngày hoàn thành 04/10. PX01 đọc danh sách thấy Đã duyệt / Đã xác nhận, ngày nộp **03:43 07/10**, ngày duyệt **03:45 07/10**.
10. Bấm thông báo Đã duyệt ở Đội 2 → đúng M02, bộ lọc tất cả → danh sách bốn kỳ: 04 đã duyệt; 05, 06, 07 cần nộp. Bấm thông báo Cần duyệt cũ ở PX01 → đúng M02, **0 chờ duyệt / 1 không còn chờ duyệt**; không biến thông báo cũ thành nghĩa vụ duyệt mới.

Ảnh: [thông báo đã duyệt](images/mau-2-m02-thong-bao-da-duyet-2026-10-07.jpg), [bốn kỳ sau duyệt](images/mau-2-m02-sau-duyet-bon-ky-2026-10-07.jpg).

## Điểm cần rà tiếp: cập nhật thông báo trong phiên đang mở

Sau nộp, nhóm Cần làm của PX01 có M02 chờ duyệt, nhưng chuông và Lịch sử thông báo ở phiên đang mở chưa hiện mục mới; đã thử Làm mới tại inbox. Sau duyệt, chuông Đội 2 cũng chưa hiện Đã duyệt ngay trong phiên đang mở. Tải lại trang thì thấy **Cần duyệt 03:43** và **Đã duyệt 03:45**, đúng M02 và thứ tự thời gian.

**Đã xác nhận:** thông báo có khi đọc phiên mới; bấm mở đúng phần việc và trạng thái hiện tại. **Chưa chốt:** tự cập nhật/độ trễ của chuông và lịch sử khi phiên giữ mở. Chưa đo khoảng làm mới hoặc phân biệt ảnh hưởng tab nền, bộ nhớ đệm và xử lý nền. Không kết luận mất thông báo hoặc nguyên nhân FE/BE chỉ từ hiện tượng này; không ghi cả phần thông báo đã đạt.

Ca kiểm lại: giữ cùng phiên PX01 và Đội 2 đang mở trong một lượt nộp/duyệt kỳ kế tiếp; ghi thời điểm thao tác, lúc mục xuất hiện, thao tác mở chuông/Làm mới, và chỉ tải lại sau khi đã ghi đủ hiện tượng. Không đổi đồng hồ/job, không nộp lặp để ép thông báo.

## Mốc bàn giao và chặng tiếp

| Phạm vi | Trạng thái cuối |
|---|---|
| M01 ngày 04 | Đã duyệt, kiểm độc lập mở nguồn từ report PX01 |
| M02 mẫu riêng / WA000006 | Đã công bố / Đang hiệu lực, đúng cha và đại diện Đội 2 |
| M02 ngày 04 | Đã duyệt, đúng ngày, dữ liệu đã giữ |
| M02 ngày 05–07 | Chưa nhập/nộp; đủ kỳ thật |
| M03–M04 / PA02 | Chưa dựng/giao trong lượt này |
| Report PX01 | Giữ Nháp và bảng đã lưu chỉ có M01 ngày 04; chưa bổ sung mapping M02 |
| View PV01 | Không thao tác trong lượt này; không dùng kết quả report PX01 thay View |
| Các phép SUM/AVG/COUNT/CONCAT/List/Bảng trên bộ nguồn mới | Chưa nghiệm thu |
| Thông báo tự cập nhật trong phiên | Cần kiểm lại như mục trên |

Chặng tiếp: dựng mẫu riêng M03/M04 của PA02 và giao đúng hai đội; sau đó hoàn tất lần lượt 12 báo cáo quá khứ 04–06 ở bốn nhánh. Chỉ khi các kỳ quá khứ đã xử lý mới dùng ngày 07 kiểm tự duyệt, số liệu tăng theo nguồn và ca trả lại. Giữ report phòng Nháp trước khi kiểm trả lại/thu hồi nguồn. Tổng hợp đủ hai phòng và truy nguồn hai tầng còn ở các chặng sau.

Phiên kết thúc: tài khoản Đội 2 ở danh sách bốn kỳ M02; PX01 trở về report tổng hợp còn Nháp. Không có hộp xác nhận ghi dữ liệu còn chờ thao tác. Chương trình 180 phút vẫn chờ rà, giữ 50 phút Tổng hợp và 10 phút nghỉ.
