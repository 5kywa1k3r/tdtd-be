# Handoff UAT Mẫu 2 — PA02, M03 và M04

Ngày kiểm: 07/10/2026. Tiếp sau [chặng M02](HANDOFF_UAT_MAU_2_M02_2026_10_07.md). Lượt này thao tác trên UI thật, bằng tài khoản đại diện PA02 và hai đội; không sửa sản phẩm, gọi API/DB trực tiếp, seed/reset, build/restart hoặc tạo agent.

**Kết quả:** PA02 đã tạo mẫu M03, sao chép M03 thành M04, lưu/đọc lại/nhập thử/công bố và giao đúng hai đội. Hai báo cáo ngày 04 đã đi hết nhập → lưu → đọc lại → nộp → phòng xem → xác nhận dữ liệu quá khứ → duyệt → người nhận đọc lại. Chưa thấy sai dữ liệu đã lưu. Có vướng hộp nhập ô M03 đã khôi phục được. Thông báo Đã duyệt M04 chưa thấy ở lượt đọc đầu, nhưng lượt đọc sau đã thấy và mở đúng phần việc/kỳ; độ trễ và việc cập nhật trong phiên đang mở chưa được đo. Chưa nghiệm thu toàn bộ UX/thông báo hoặc Mẫu 2.

## Dữ liệu và phạm vi được giữ

- Dùng đúng nhiệm vụ **[TẬP HUẤN] Mẫu 2 — bốn mô hình, bốn ngày**, cây PV01 → PX01/PA02 → bốn đội. Không dùng dữ liệu các bài KHCN của agent khác.
- Mỗi phòng dùng mẫu riêng thuộc phòng mình. PA02 không đọc được mẫu M01 của PX01 khi thử mở; không đổi quyền để vượt chặn. PA02 tạo M03 rồi dùng chức năng sao chép để có M04.
- Lịch ngày 04–07/10, hạn **07:00 Việt Nam = 00:00 UTC**, điều kiện tự duyệt kinh phí ≥ 0. Báo cáo ngày 04 mang cờ quá khứ nên vẫn được phòng xác nhận và duyệt tay.
- Report PX01 và dữ liệu tổng hợp đang Nháp được giữ; không nộp report phòng hoặc sửa mapping tổng hợp trong lượt này. Không sửa các báo cáo M01 đã có do người khác chuẩn bị.
- Không kiểm Dashboard, chấm điểm tiến độ nghiệp vụ, INP hoặc tổng hợp đầy đủ. Chương trình 180 phút vẫn chờ rà, giữ 50 phút Tổng hợp và 10 phút nghỉ.

## Đối tượng thật để tiếp tục

| Đối tượng | Tham chiếu tại mốc kiểm |
|---|---|
| Work | `6ac53a44c4378171baa7a1f8` |
| Cha PA02 / WA000004 | `6ac53de9c4378171baa7af2c`, báo cáo một lần |
| Mẫu M03 | `6ac563056710e17b1dc41a40`, FORM-pa02-2026-000002 v1, Đã công bố |
| Mẫu M04 | `6ac5637b6710e17b1dc41aa0`, FORM-pa02-2026-000003 v1, Đã công bố |
| M03 / WA000007 | `6ac5644c6710e17b1dc41ef9`, cha PA02, `pa02_doi1` |
| M04 / WA000008 | `6ac564956710e17b1dc41f3b`, cha PA02, `pa02_doi2` |
| M03 kỳ 04 / report | `6ac564646710e17b1dc41f20` / `6ac565926710e17b1dc423f2`, Đã duyệt |
| M04 kỳ 04 / report | `6ac564a26710e17b1dc41f61` / `6ac566cd6710e17b1dc428c4`, Đã duyệt |

ID chỉ dùng đối chiếu nội bộ, không đưa vào lời hướng dẫn người học. Mã mô hình M03/M04 là mã nghiệp vụ nhập tay; khác mã biểu mẫu tự cấp.

## Biểu mẫu và giao việc đã thực hiện

1. Đăng nhập PA02, kiểm tên và đơn vị. Đọc danh sách mẫu của PA02; mẫu KHCN riêng của agent khác không đủ cấu trúc bài và không bị sửa.
2. Tạo **[TẬP HUẤN Mẫu 2] PA02 — Bản đồ số ANTT, báo cáo ngày**. Có chín trường chung, Danh sách Thông tin mô hình đúng một dòng, Danh sách Điểm bản đồ đã cập nhật, Bảng sản phẩm một chiều dọc và Bảng nội dung/kinh phí hai chiều. Đọc lại UI: 21 trường, 2 Danh sách, 2 Bảng.
3. Lưu, tải lại đối chiếu; Nhập thử điểm T01/Thôn/Có và kinh phí 0.6. Nhập thử không phải báo cáo thật. Công bố, chờ đọc thấy v1 Đã công bố.
4. Sao chép M03 sang bản riêng M04; đổi tên/mô tả, thay Danh sách riêng bằng Phiếu phản hồi; thêm trường Số lượt liên hệ trong ngày. Đọc lại UI: 22 trường, 2 Danh sách, 2 Bảng. Điểm hài lòng là Số nguyên 0–5; các kiểu còn lại giữ đúng cấu trúc.
5. Nhập thử M04: điểm 6 bị chặn, nội dung phiếu vẫn còn; sửa 4 xác nhận được. Lưu/đọc lại rồi Công bố v1. M03 nguồn vẫn giữ nguyên.
6. Mở giao việc của nhiệm vụ, chọn Nhánh công việc PA02. Giao M03 cho Đội 1 PA02 và M04 cho Đội 2 PA02 qua tìm kiếm đơn vị; người nhận được phân giải đúng tài khoản đại diện thực tế.
7. Cả hai chọn báo cáo định kỳ, hằng ngày, từ 04 đến 07/10, ngày áp dụng 04/10, bật tự duyệt và chọn trường kinh phí chung ≥ 0. Lưu và mở chi tiết đọc lại đúng cha, mẫu, đại diện, lịch và điều kiện. Không dùng chỉ số riêng làm điều kiện tự duyệt.
8. Đăng nhập từng đội. Chuông có Được giao việc, Báo cáo đến hạn cho ba kỳ cũ và Sắp đến hạn cho kỳ hôm nay. Bấm thông báo giao → đúng phần việc → Xem kỳ báo cáo: đủ bốn ngày, hạn 07:00, ba quá hạn/một sắp tới hạn tại mốc mở.

## M03 ngày 04 — dữ liệu và các bước thật

| Phần | Giá trị đã lưu và đọc lại |
|---|---|
| Kinh phí / đánh giá | 1 triệu / Đạt yêu cầu |
| Nội dung | Nhập thông tin hai thôn giả vào bản đồ thử. |
| Kết quả | Đã nhập hai thôn giả, kiểm thông tin mô tả. |
| Ngày triển khai / hoàn thành | Cùng 04/10/2026 |
| Khó khăn / kiến nghị | Trống có chủ ý, không phải thiếu báo cáo nguồn |
| Tiếp theo | Bổ sung vị trí camera giả. |
| Mô hình | M03 / Bản đồ số về an ninh, trật tự / Cán bộ B1 (giả định) |
| Hai điểm bản đồ | T01/Thôn/Thông tin Thôn thử 01/Có; T02/Thôn/Thông tin Thôn thử 02/Có |
| Bảng sản phẩm | M03-1004 / Lớp thông tin thôn thử / Đã kiểm tra theo tình huống giả. |
| Bảng hai chiều | Hàng Chuẩn bị: nội dung triển khai, 0.6; hàng Vận hành: kết quả, 0.4 |
| Minh chứng | Trống, không bắt buộc; chưa kiểm tải tệp |

1. Đội 1 mở kỳ 04, bấm **Bắt đầu nhập bảng** rồi nhập các trường chung. Dấu bắt buộc và nhãn hiện đúng; ngày hoàn thành 04/10 được nhập trước nộp.
2. **Thêm mô hình** → nhập ba trường → **Xác nhận**. Không thêm STT nhập tay.
3. **Thêm điểm** hai lượt, chọn Thôn/Có và xác nhận từng dòng. Bảng sản phẩm dùng **Thêm bản ghi**; bấm từng ô, nhập **Giá trị ô → Xác nhận**. Hoàn tất cả bốn ô bảng hai chiều; ô số có nhắc dùng dấu chấm thập phân.
4. Sau vướng hộp nhập được mô tả bên dưới, mở lại đúng ô để khôi phục; lưu nháp, sau đó điền phần bảng còn lại và lưu lần nữa. Tải lại trang, đối chiếu đầy đủ dữ liệu, số 1 và hai ô 0.6/0.4; không mất dữ liệu đã lưu.
5. **Nộp báo cáo** → trạng thái Đã nộp, chỉ đọc. Màn duyệt ghi ngày nộp 07/10 lúc 04:21.
6. PA02 thấy M03 trong nhóm Duyệt báo cáo ở phiên đang mở; không cần tải lại cả trang. **Xem danh sách báo cáo → Mở màn duyệt** đúng kỳ/người nhận; **Xem báo cáo** để đối chiếu dữ liệu.
7. **Duyệt báo cáo kết quả** → hộp **Xác nhận duyệt dữ liệu từ quá khứ** hiện Kỳ 04/10 và Ngày hoàn thành 04/10 → **Xác nhận và duyệt**.
8. Đội 1 tải lại báo cáo: Đã duyệt, chỉ xem, ngày và hai kiểu Bảng/Danh sách đúng. Chưa dùng kết quả này để xác nhận tự duyệt ngày 07.

## M04 ngày 04 — dữ liệu và các bước thật

| Phần | Giá trị đã lưu và đọc lại |
|---|---|
| Kinh phí / đánh giá | 0 triệu / Cần bổ sung |
| Nội dung | Thử hướng dẫn ba lượt liên hệ giả. |
| Kết quả | Đã hướng dẫn ba lượt giả và ghi hai phiếu phản hồi. |
| Ngày triển khai / hoàn thành | Cùng 04/10/2026 |
| Khó khăn | Một câu trả lời còn dài. |
| Kiến nghị | Rút gọn câu trả lời, giữ đủ ý. |
| Tiếp theo | Thử thêm tình huống cần bổ sung hồ sơ. |
| Số lượt liên hệ | 3, là trường riêng của mô hình |
| Mô hình | M04 / Mô hình hướng dẫn, tiếp công dân / Cán bộ B2 (giả định) |
| Hai phiếu | PH01/Tra cứu nơi tiếp nhận/4/Không; PH02/Giải thích danh mục hồ sơ/5/Không |
| Bảng sản phẩm | M04-1004 / Nhật ký hướng dẫn ngày 04 / Một câu trả lời còn dài. |
| Bảng hai chiều | Chuẩn bị: nội dung, 0; Vận hành: kết quả, 0 |
| Minh chứng | Trống, không bắt buộc; chưa kiểm tải tệp |

1. Đội 2 mở từ thông báo giao M04 → Xem kỳ báo cáo → Mở báo cáo ngày 04 → Bắt đầu nhập bảng. Nhập các trường chung, số 0, số lượt 3 và ngày hoàn thành 04.
2. Thêm mô hình, rồi **Thêm phiếu** PH01. Cố ý nhập điểm 6, chọn Không, bấm Xác nhận: bị chặn với lỗi giới hạn; mã, nội dung, số 6 và lựa chọn Không vẫn còn. Sửa điểm 4 → xác nhận được; thêm PH02 điểm 5. Đây là chặn đúng dữ liệu không hợp lệ, không phải lỗi sản phẩm.
3. Thêm một bản ghi sản phẩm; nhập ba ô, rồi bốn ô bảng hai chiều. Lưu nháp, tải lại; mọi trường/List/Bảng và số 0 đọc đúng. Không gặp lại vướng hộp nhập M03 trong chuỗi này.
4. Nộp → Đã nộp và chỉ đọc. PA02 mở nhóm Duyệt báo cáo, lọc bài, chọn đúng M04/người nhận/kỳ; xem báo cáo trước duyệt.
5. PA02 xác nhận ngày hoàn thành 04/10 và duyệt. Đọc lại màn duyệt: kỳ/báo cáo Đã duyệt, dữ liệu quá khứ Đã xác nhận, nộp **04:25**, duyệt **04:26** ngày 07/10.
6. Đội 2 tải lại: Đã duyệt, dữ liệu giữ nguyên. Quay lại danh sách kỳ ở bộ lọc Chưa xử lý: còn đúng ba kỳ 05, 06, 07; hai quá hạn/một sắp tới hạn tại mốc kiểm. Duyệt một kỳ không hoàn thành cả lịch.
7. PA02 mở thông báo Cần duyệt M04 cũ sau khi đã duyệt → đúng M04, bộ lọc Mọi trạng thái; danh sách ghi **0 chờ duyệt / 1 không còn chờ duyệt**, dòng 04 là Báo cáo đã duyệt. Đọc thông báo không tạo nghĩa vụ duyệt mới.
8. Ở lượt kiểm tiếp theo, Đội 2 thấy thông báo **Đã duyệt**, thời gian **04:26 ngày 07/10**, đúng tên M04. Bấm thông báo → đúng phần việc M04 → **Xem kỳ báo cáo**: đủ bốn kỳ, **3 cần nộp / 1 không còn cần nộp**, kỳ 04 Báo cáo đã duyệt. Mở báo cáo kỳ 04 vẫn thấy Đã duyệt và chỉ xem. Thời gian ghi trên thông báo không chứng minh nó đã hiện ngay lúc 04:26.

## Điểm cần rà, không đưa thành thao tác chuẩn

### Hộp nhập ô M03 không hiện nhưng khóa lưu

Sau xác nhận ô Mã sản phẩm, mở ô Tên sản phẩm: không có hộp nhập trong DOM/ảnh, Lưu nháp/Nộp bị khóa, nhắc Xác nhận hoặc đóng phần đang nhập. Escape không khôi phục. Bấm lại đúng ô Tên sản phẩm mở được hộp, nhập/xác nhận rồi lưu thành công. Không tải lại khi còn dữ liệu chưa lưu.

Đây là vướng UI làm gián đoạn; có cách khôi phục qua UI và chặng đã đi tiếp, chưa thấy sai dữ liệu lưu. Chưa cô lập nguyên nhân hoặc khẳng định liên quan việc làm mới quyền. Ở Nhập thử M03 trước đó cũng có một lần hộp Danh sách biến mất, phải mở lại; chưa đủ bằng chứng coi hai hiện tượng cùng nguyên nhân. Cần agent kỹ thuật tái hiện trên bản nháp thử riêng, không sửa báo cáo đã duyệt để tái hiện. Không coi retry này là quy trình bình thường trong tài liệu công khai.

### Thông báo Đã duyệt M04 chưa thấy ở lượt đọc đầu

Đội 2 đọc report Đã duyệt, còn ba nghĩa vụ đúng; nhưng chuông/Lịch sử chưa có thông báo Đã duyệt sau duyệt. Đã mở lại chuông, bấm Làm mới ở lịch sử và tải lại cả trang, vẫn chỉ có ba nhóm giao/đến hạn/sắp đến hạn tại mốc đọc. PA02 có thông báo Cần duyệt M03 04:21 và M04 04:25 khi mở lại chuông ở màn duyệt.

**Cập nhật cuối lượt:** sau khi PA02 đọc lại hai phần việc ở trạng thái Đang thực hiện, lần kiểm tiếp phía Đội 2 đã thấy thông báo Đã duyệt M04, thời gian 04:26 ngày 07/10. Bấm thông báo mở đúng M04; danh sách đủ bốn kỳ, ba còn cần nộp, kỳ 04 Đã duyệt. Mở kỳ 04 đúng report Đã duyệt, chỉ xem. Đã lưu hai ảnh bổ sung bên dưới.

Như vậy đã kiểm được việc nhận và mở đúng thông báo kết quả M04 ở lượt đọc sau; chưa có bằng chứng mất thông báo. Chưa đo độ trễ hoặc xác nhận thông báo tự cập nhật trong phiên đang mở. Không suy nguyên nhân bộ nhớ đệm/xử lý nền, không hứa thông báo hiện ngay và không lấy việc M02 đã thấy thông báo làm bằng chứng thay M04.

### Tên biểu mẫu ở danh sách duyệt đang là dấu gạch

Trong màn duyệt M03/M04, cột Biểu mẫu và tiêu đề Danh sách kỳ cần đánh giá hiện `-`. Mở Xem báo cáo vẫn có đúng tên mẫu, dữ liệu, kỳ và người nhận. Đây là điểm hiển thị cần kiểm, chưa chặn duyệt; không dùng dấu gạch như tên mẫu trong hướng dẫn hoặc sửa ảnh để lấp tên. Ảnh PA02 đọc lại M04 đã duyệt giữ hiện trạng này.

## Ảnh thật đã lưu

Có **35 ảnh thật** thuộc thư mục `images/` trong bộ tập huấn, không phải link tạm. Mở xem trước khi chọn vào guide; ảnh report dài cần chọn phần/ảnh phóng phù hợp, không sửa dữ liệu trên ảnh. Ảnh hộp nhập M03 và lịch sử chưa thấy Đã duyệt M04 là bằng chứng QA ở lượt đọc đầu, không dùng làm ảnh thao tác thành công.

| Phạm vi | Ảnh |
|---|---|
| M03 cấu trúc/lưu | [Cấu trúc](images/mau-2-m03-cau-truc-da-luu-2026-10-07.jpg), [Nhập thử](images/mau-2-m03-nhap-thu-2026-10-07.jpg), [Công bố](images/mau-2-m03-cong-bo-2026-10-07.jpg) |
| M03 giao | [Cấu hình](images/mau-2-m03-cau-hinh-giao-2026-10-07.jpg), [Đọc lại](images/mau-2-m03-giao-da-doc-lai-2026-10-07.jpg) |
| M03 nhận | [Thông báo](images/mau-2-m03-thong-bao-nguoi-nhan-2026-10-07.jpg), [Bốn kỳ](images/mau-2-m03-bon-ky-2026-10-07.jpg) |
| M03 lưu/nộp | [Đọc lại](images/mau-2-m03-bao-cao-da-luu-doc-lai-2026-10-07.jpg), [Đã nộp](images/mau-2-m03-da-nop-2026-10-07.jpg) |
| M03 duyệt | [Cần duyệt](images/mau-2-m03-pa02-can-duyet-2026-10-07.jpg), [Xem báo cáo](images/mau-2-m03-pa02-xem-bao-cao-2026-10-07.jpg), [Xác nhận quá khứ](images/mau-2-m03-xac-nhan-duyet-qua-khu-2026-10-07.jpg), [Người nhận đọc lại](images/mau-2-m03-nguoi-nhan-da-duyet-2026-10-07.jpg) |
| M03 vướng UI | [Hộp nhập chưa thấy](images/mau-2-m03-o-san-pham-hop-nhap-chua-thay-2026-10-07.jpg) |
| M04 cấu trúc/lưu | [Cấu trúc](images/mau-2-m04-cau-truc-da-luu-2026-10-07.jpg), [Điểm 6 ở Nhập thử](images/mau-2-m04-diem-ngoai-gioi-han-2026-10-07.jpg), [Nhập thử điểm 4](images/mau-2-m04-nhap-thu-phieu-2026-10-07.jpg), [Công bố](images/mau-2-m04-cong-bo-2026-10-07.jpg) |
| M04 giao | [Cấu hình](images/mau-2-m04-cau-hinh-giao-2026-10-07.jpg), [Đọc lại](images/mau-2-m04-giao-da-doc-lai-2026-10-07.jpg) |
| M04 nhận | [Thông báo](images/mau-2-m04-thong-bao-nguoi-nhan-2026-10-07.jpg), [Bốn kỳ](images/mau-2-m04-bon-ky-2026-10-07.jpg) |
| M04 nhập/lưu/nộp | [Điểm sai giữ dữ liệu](images/mau-2-m04-bao-cao-diem-sai-giu-du-lieu-2026-10-07.jpg), [Đọc lại](images/mau-2-m04-bao-cao-da-luu-doc-lai-2026-10-07.jpg), [Đã nộp](images/mau-2-m04-da-nop-2026-10-07.jpg) |
| M04 duyệt | [Cần duyệt](images/mau-2-m04-pa02-can-duyet-2026-10-07.jpg), [Xem báo cáo](images/mau-2-m04-pa02-xem-bao-cao-2026-10-07.jpg), [Xác nhận quá khứ](images/mau-2-m04-xac-nhan-duyet-qua-khu-2026-10-07.jpg), [Người nhận đọc lại](images/mau-2-m04-nguoi-nhan-da-duyet-2026-10-07.jpg), [PA02 đọc lại](images/mau-2-m04-pa02-doc-lai-da-duyet-2026-10-07.jpg) |
| M04 nghĩa vụ/thông báo | [Còn ba kỳ](images/mau-2-m04-sau-duyet-con-ba-ky-2026-10-07.jpg), [Thông báo cũ và trạng thái hiện tại](images/mau-2-m04-thong-bao-cu-trang-thai-hien-tai-2026-10-07.jpg), [Lịch sử chưa thấy Đã duyệt](images/mau-2-m04-lich-su-chua-thay-da-duyet-2026-10-07.jpg) |
| M04 thông báo — lượt đọc sau | [Đã thấy Đã duyệt](images/mau-2-m04-thong-bao-da-duyet-doc-sau-2026-10-07.jpg), [Từ thông báo mở đúng bốn kỳ](images/mau-2-m04-tu-thong-bao-bon-ky-sau-duyet-2026-10-07.jpg) |

## Mốc bàn giao

Cây đã giao đủ **hai phòng/bốn đội**. Theo bằng chứng M01/M02 trước và lượt PA02 này, ngày 04 của cả bốn mô hình đã duyệt: **4/12 báo cáo quá khứ**, kinh phí của bốn báo cáo này **1 triệu**. Đây là cộng dữ liệu để đối chiếu, chưa phải kết quả engine tổng hợp đã chạy.

PA02 đọc lại danh sách Giao: M03/M04 đều Đang hiệu lực, tiến độ **Đang thực hiện**, cập nhật lần cuối 04:33. Duyệt một kỳ không làm phần việc hoàn thành; lượt này chưa kiểm cách tính phần trăm tiến độ.

Ngày 05–06 còn phải hoàn tất tám báo cáo quá khứ; ngày 07 chưa dùng để kiểm tự duyệt trong bài này. M03/M04 ngày 05–07 không bị nhập/nộp trong lượt này. Giữ report phòng Nháp; sau đủ 12 kỳ cũ mới cấu hình/đối chiếu tổng hợp và thử tự duyệt hôm nay, trả lại, báo cáo phòng gửi PV01 và View nhiệm vụ. Không bỏ ca nhỏ vì đã đi được một ngày.

Agent tài liệu dùng [prompt riêng](PROMPT_AGENT_HTML_MAU_2_M01_M02_2026_10_07.md), viết cả Markdown và HTML, có bài nối các vai đến kết quả cuối của chặng đã kiểm. Chưa gọi toàn demo Mẫu 2 hoàn chỉnh; các vướng mắc trên phải nằm trong QA nội bộ và được rà tiếp.
