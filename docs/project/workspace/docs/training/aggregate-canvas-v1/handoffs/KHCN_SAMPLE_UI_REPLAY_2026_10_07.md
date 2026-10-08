# Kịch bản dựng lại dữ liệu mẫu KHCN bằng UI

Bài thực hành M1 dành cho agent tổng chuẩn bị trước reset: [Gói bàn giao M1](M01_PRACTICE_HANDOFF_FOR_COORDINATOR_2026_10_07.md). Gói này chốt riêng cây, số liệu, đường thao tác và đáp án M1; không mở lại M2–M8.

## Cách dùng sau reset

Yud muốn bộ dữ liệu này trở thành data mẫu. File này lưu thao tác đã làm được, tránh phải tìm lại. **Không phải lệnh reset DB hoặc seed trực tiếp.** M01 đã chạy xuyên luồng; M02 đã dựng cây 1–2–4, đang nhập và duyệt nguồn (mục 8); M03–M08 mới có Form. Mỗi phần mới phải ghi kết quả vào log, không biến dữ liệu dự kiến thành ca đã chạy.

Giữ file này, `KHCN_UI_SAMPLE_TREE_2026_10_07.md`, `KHCN_UI_ISSUES_FOR_OWNERS_2026_10_07.md` và `outputs/khcn-ui-samples-20261007/` trước reset. Data manifest dùng logical key; các ObjectId, WA number, Form code cũ không dùng làm ID seed sau reset. Tạo mới xong ghi bảng logical key → ID/URL mới. Không copy published hash hoặc tự chế schema pin.

Mốc lịch gốc: tháng hiện tại **10/2026**, kỳ lịch sử **09/2026**. Nếu dựng lại ở tháng khác, chọn một referenceMonth trước khi chạy và dịch đồng bộ lịch, completed date, due date, data window; ghi rõ bản mới. Không dùng ngày hiện tại của máy một cách âm thầm làm thay expected hoặc cho mọi report thành lịch sử. Bộ cố định tháng 9–10 vẫn giữ để tái hiện bằng chứng gốc, nhưng Dashboard hiện tại sẽ không đếm report các tháng cũ.

## 0. Chuẩn bị

1. Kiểm WIP/AGENTS/Quy ước; đọc handoff lỗi mở. Không sửa owner files hoặc restart host khác đang dùng.
2. FE localhost:5173; chọn đúng browser/tab. Đăng nhập pv01, px01, pa02, cap_hacthanh, cap_samson bằng phiên/mật khẩu Yud cấp, không lưu password vào file. Tài khoản Sầm Sơn đã đối chiếu từ bảng Duyệt UI, không chèn dấu gạch dưới giữa sam và son.
3. Đăng nhập theo nhịp: logout → đợi login → điền cả hai ô → bước riêng click ĐĂNG NHẬP → xác nhận banner tài khoản → điều hướng. Tránh click submit ngay cùng đợt điền khi React chưa cập nhật.
4. Chỉ dùng UI để tạo/công bố/giao/nhập/nộp/duyệt/map. Không fetch API, DB, inject store hoặc tự sửa payload để bỏ bước.
5. Đặt prefix `[KHCN-0710 Mxx]` và hậu tố `— dữ liệu giả`; mô tả khẳng định dữ liệu tập huấn. Tìm tên trước khi tạo để tránh nhân đôi nếu lượt trước đã commit.

## 1. Tạo và công bố Form

PV01 → Biểu mẫu động → Tạo mới → Thông tin biểu mẫu → tên/mô tả → Xác nhận → thêm từng trường/cấu trúc → Lưu biểu mẫu → đợi “Đã đối chiếu dữ liệu đã lưu” → Công bố → Xác nhận → thấy nút Tạo phiên bản mới.

| Logical key | Tên | Trường/cấu trúc |
|---|---|---|
| FORM_M01 | Số hóa hồ sơ nghiệp vụ | Số: Hồ sơ tiếp nhận; Hồ sơ đã số hóa; Giờ công tiết kiệm; Điểm chất lượng |
| FORM_M02 | Thí điểm tiếp nhận hồ sơ điện tử | Ngày đầy đủ: Ngày vận hành thí điểm; Ngày/kỳ: Kỳ nghiệm thu dự kiến; Có/không: Đã kiểm tra an toàn thông tin; Nội dung: Tên mô hình thí điểm |
| FORM_M03 | Phân loại sáng kiến công nghệ | Chọn một Nhóm sáng kiến (Số hóa hồ sơ / Dịch vụ công trực tuyến / An toàn thông tin); Chọn nhiều Công nghệ áp dụng (Nhận dạng ký tự OCR / Chữ ký số / Mã QR); Nội dung Tên sáng kiến |
| FORM_M04 | Diễn giải hiệu quả số hóa | Một Nội dung: Kết quả và khó khăn triển khai; dữ liệu >1.000 ký tự khi nhập |
| FORM_M05 | Thuyết minh mô hình công nghệ | Một Soạn thảo văn bản: Thuyết minh mô hình |
| FORM_M06 | Kiến nghị cải tiến dịch vụ công | Một Danh sách nội dung: Các kiến nghị cải tiến |
| FORM_M07 | Danh sách sáng kiến cấp cơ sở | Danh sách Sáng kiến công nghệ cơ sở; tên phần tử Sáng kiến; nút Thêm sáng kiến; max 200; trường con Tên sáng kiến (Nội dung), Điểm đánh giá (Số), Đã vận hành (Có/không), Ngày ghi nhận (ngày đầy đủ) |
| FORM_M08 | Ma trận thiết bị phục vụ số hóa | Một Bảng ma trận Thiết bị số hóa theo trạng thái; hàng Đang sử dụng/Cần bảo trì; cột Máy quét/Máy tính; 4 ô số |
| FORM_M01B | Số hóa hồ sơ định kỳ PA02 | PA02 tạo riêng, cùng bốn nhãn/kiểu M01; không sửa Form PV01 đã công bố |

Các field M01 bật Hiển thị trên tổng quan, không required. Không áp mã kỹ thuật vào nhãn tiếng Việt. Lấy code do server cấp; không đoán số thứ tự. Mỗi mẫu phức tạp chỉ một cấu trúc để tài liệu HTML chụp/hướng dẫn riêng.

## 2. Tạo Work và cây M01 (đã chạy)

PV01 → Danh sách công việc → Tạo nhiệm vụ mới. Tên `[KHCN-0710] Thí điểm mô hình số hóa và sáng kiến công nghệ — cây 1-2-4`; từ 01/09/2026, đến/hạn 31/10/2026; Trung bình. Mô tả: dữ liệu giả, hai nhánh cấp phòng, mỗi phòng giao hai phường; không số liệu nghiệp vụ.

Mỗi dòng sau là **một lần Tạo mới riêng**, chỉ chọn một đơn vị. Bắt đầu tất cả 01/09. Định kỳ hết 31/10. Không tạo lại hai assignment nhiều người nhận WA3/WA4 thử nghiệm của bộ cũ.

| Logical key | Người thao tác | Cha | Nhận | Form | Lịch/hạn |
|---|---|---|---|---|---|
| ASG_PX | pv01 | Work gốc | PX01 | FORM_M01 | Một lần 30/10/2026 |
| ASG_PA | pv01 | Work gốc | PA02 | FORM_M01 | Tháng, ngày 30, 09–10/2026 |
| ASG_PX_HAC | px01 | ASG_PX | CAP Hạc Thành | FORM_M01 | Một lần 30/09/2026 |
| ASG_PX_SAM | px01 | ASG_PX | CAP Sầm Sơn | FORM_M01 | Một lần 14/10/2026 |
| ASG_PA_HAC | pa02 | ASG_PA | CAP Hạc Thành | FORM_M01B | Tháng, ngày 29, 09–10/2026 |
| ASG_PA_SAM | pa02 | ASG_PA | CAP Sầm Sơn | FORM_M01B | Tháng, ngày 28, 09–10/2026 |

Đường giao: Work → Giao việc → Giao việc. Cấp phòng chọn đúng **Nhánh công việc** là phần việc được giao từ PV01. Chọn Form đã công bố. Bộ chọn Đơn vị nhận việc → tìm Hạc Thành/Sầm Sơn → chọn đúng CAP có tên/mã → Đóng → Tạo mới. Với định kỳ chọn Hình thức theo dõi/Loại kỳ Tháng, chọn ngày lịch, Đóng lịch trước Tạo mới. Sau commit đọc lại tên, đơn vị, loại, hạn; lưu URL/ID.

## 3. Nhập nguồn theo vai trò (đã chạy)

Work → Báo cáo kết quả thực hiện → Form → kỳ đúng assignment → Mở báo cáo → Bắt đầu nhập biểu mẫu. Số theo thứ tự M01.

| Key report | Vai nhập | Số liệu | Mốc lịch sử | Trạng thái cuối cần giữ |
|---|---|---|---|---|
| R_PX_HAC_ONCE | cap_hacthanh | 120 / 108 / 36 / 9 | Hoàn thành 29/09 | Nộp, PX01 duyệt |
| R_PX_SAM_ONCE | cap_samson | 90 / 72 / 24 / 7 | Hiện tại | Nộp, PX01 duyệt |
| R_PA_HAC_SEP | cap_hacthanh | 80 / 60 / 20 / 8 | Hoàn thành 30/09, trễ hạn 29/09 | Nộp, PA02 duyệt |
| R_PA_SAM_SEP | cap_samson | 70 / 63 / 21 / 9 | Hoàn thành 27/09 | Nộp, PA02 duyệt |
| R_PA_HAC_OCT | cap_hacthanh | 50 / 35 / 12 / 7 | Không nhập ngày hoàn thành | Chỉ Lưu nháp |
| R_PA_SAM_OCT | cap_samson | 40 / 32 / 10 / 8 | Hiện tại | Nộp, chưa duyệt |

Lý do trễ cho R_PA_HAC_SEP: `DỮ LIỆU GIẢ TẬP HUẤN: hoàn thành đối soát số hóa ngày 30/9, chậm một ngày so với hạn 29/9; dùng minh họa trạng thái chậm muộn.` Không bỏ qua lỗi field; giữ nháp và nhập thiếu rồi xác nhận lại.

Duyệt: vai phòng → Work → Duyệt biểu mẫu → row đúng tài khoản → Xem danh sách cần duyệt → đúng kỳ → Duyệt báo cáo kết quả. Với lịch sử đọc đúng Ngày hoàn thành → Xác nhận và duyệt. Đợi dialog tự đóng, mở lại kiểm **Đã duyệt/Đã xác nhận**, không suy từ click. Không duyệt tháng 10 ở bộ baseline.

## 4. Tổng hợp PX01, PA02 và Work (đã chạy)

### Thao tác canvas dùng lại

1. Báo cáo phòng → Tổng hợp vào báo cáo → Thêm biểu mẫu → chọn source. Đợi picker đóng hoàn toàn.
2. Vừa khung; chọn checkbox từng trường. Nếu card đổi chiều cao, Vừa khung lại trước trường kế; kiểm checked thực sự.
3. Tính toán → click port nguồn của field → click “Nối nguồn cho Tính toán”.
4. Trong cột chi tiết click ⚙ Thiết lập tính toán → ô Công thức → nhập `=SUM(GET(A))` hoặc `=AVG(GET(A))` → Xong. UI có thể chuẩn hóa về `=SUM(A)`/`=AVG(A)`; GET giữ tập trong contract hiện tại.
5. Click port Kết quả của khối vừa thêm → port đúng field đích. Lặp bốn khối: SUM cho ba số đầu, AVG cho điểm. Không ngầm lấy dòng đầu.
6. Sắp xếp lại → Vừa khung → Xem trước thay đổi → đối chiếu expected → Lưu và cập nhật nền → đợi “Đã tính và cập nhật báo cáo” → Đóng → kiểm giá trị đã lưu và ô mapping chỉ đọc.
7. Khi nộp: lưu cả ngày hoàn thành/ghi chú trước; nếu guard stale sau sửa report, mở tổng hợp tính/lưu lại → Nộp → đọc preview và số nguồn → Xác nhận nộp. Không gọi API để bỏ guard.

### Nhánh một lần

PX01 source FORM_M01, không điều kiện thời gian; chỉ report con hiện hành đã duyệt. Expected **210 / 180 / 60 / 8**. Nộp, PV01 duyệt.

### Nhánh định kỳ

PA02 kỳ 30/09 source FORM_M01B. Khoảng dữ liệu → nhập 01/09–30/09 → Đóng lịch → Xem trước khai báo → Xác nhận khoảng dữ liệu → Đóng popup. Nguồn → Lọc tập báo cáo → Thêm điều kiện → Ngày hoàn thành/nộp → Trong khoảng → Cách chọn mốc **Theo khoảng dữ liệu kỳ đích** → đợi menu đóng → Dùng bộ lọc. Cấu hình chung r1, bốn công thức như trên. Expected **150 / 123 / 41 / 8.5**. Báo cáo PA02 ngày hoàn thành 30/09, nộp, PV01 xác nhận lịch sử và duyệt.

### Biểu mẫu xem ở Work

PV01 → Giao việc → Biểu mẫu tổng hợp → gắn FORM_M01 → Cấu hình và xem trước → source FORM_M01 (report con trực tiếp PX01/PA02), 3 SUM + AVG, không giới hạn thời gian trong baseline. Nếu lưu khi mới PX01 duyệt: **210/180/60/8**; sau PA02 duyệt tự cập nhật **360/303/101/8.25**. Không cộng lại cháu; điểm là AVG của hai report phòng, không bình quân theo số hồ sơ.

## 5. Đọc màu và bảo toàn trạng thái mẫu

PV01 → Dashboard → ô Trung bình/Đang thực hiện → Xem chi tiết đúng Work → Mở công việc con hai nhánh → Vừa khung. Kỳ vọng 1–2–4. PX01 01/02 con hoàn thành; PA02 00/02; root 00/02. Hạc Thành một lần tự hoàn thành vì đủ report/hết hạn; không đồng nhất Approved thành hoàn thành mọi nhánh. Đừng duyệt hết nháp/chờ duyệt vì mất phân bố trạng thái dùng tập huấn.

KUI-01 đã có bản sửa source hạn mindmap theo bàn giao agent `outputs/khcn-ui-fixes-20261007/RESULTS.md`, chưa kiểm runtime bằng binary mới. Khi chạy lại, đối chiếu màn Giao việc/tiến độ; không sửa fixture để khớp ngày sai. Quan sát hạn cha ở popup giao việc M02 chưa được khép cùng bản sửa mindmap. Các màu khác (chưa bắt đầu/nguy cơ/chậm) cần mẫu bổ sung, chưa ghi đạt.

## 6. Quy tắc lưu bước và dữ liệu phần tiếp theo

Mỗi mẫu M02–M08 bổ sung một mục: logical keys/IDs, vai trò, giá trị nhập, trạng thái giữ, công thức/lọc qua UI, preview expected/actual, sau save/readback, ảnh. Chỉ cập nhật trạng thái “đã chạy” sau màn cuối xác nhận. Các mẫu này chưa có đầy đủ expected runtime; không sao chép nhận định source/test cũ làm nghiệm thu bộ KHCN.

Không lưu selector theo số AX qua các phiên; dùng nhãn tiếng Việt và context. Sau action lấy DOM/AX mới. Tránh gộp click menu chọn và click xác nhận phía dưới khi transition chưa xong. Chờ kết quả commit; mất mạng thì đối chiếu lượt lưu, không tạo lại mù. Nếu cần đổi tài khoản thường xuyên, tránh tab khác đồng thời thay session trong cùng browser.

## 7. Checkpoint cho lần chạy lại

- [ ] Form published/pins mới ghi vào manifest theo logical key.
- [ ] 6 assignment hoạt động, không nhân đôi giao thử.
- [ ] 6 report nguồn đúng giá trị và đúng trạng thái chủ đích.
- [ ] PX01 + PA02 tháng 9 nộp/duyệt, lịch sử xác nhận.
- [ ] Root 360/303/101/8.25; chỉ con trực tiếp; không ghi vào nguồn.
- [ ] Mindmap 1–2–4; màu/tiến độ được kiểm độc lập thời hạn report.
- [ ] M02–M08 chạy thêm theo log mới; không đánh dấu từ Form published.

Runbook này là gói mô tả có thể tái lập sau reset; chưa phải bản dump DB hay bộ seed tự động. Nếu phase sau cần seed, phải thiết kế riêng qua authoritative writers và kiểm provenance sau seed, không copy ObjectId/hash từ đây.

## 8. M02 — nhật ký dựng bằng UI ngày 07/10

Work: `WORK_M02` → `6ac562926710e17b1dc416c2`, mã `pv012026000008`, URL http://localhost:5173/works/6ac562926710e17b1dc416c2. Tên `[KHCN-0710 M02] Thí điểm tiếp nhận hồ sơ điện tử — cây 1-2-4`; Từ 01/09, Đến/Hạn 31/10/2026; ưu tiên Trung bình; mô tả ghi dữ liệu giả phục vụ tập huấn.

Form PA02 bổ sung: `FORM_M02B` → `6ac563c16710e17b1dc41acf`, code `FORM-pa02-2026-000004`, v1 đã công bố. Tên `[KHCN-0710 M02B] Nghiệm thu điện tử định kỳ PA02 — dữ liệu giả`. Tạo qua Designer theo thứ tự: Ngày đầy đủ `Ngày vận hành thí điểm`; Ngày/kỳ `Kỳ nghiệm thu dự kiến`; Có/không `Đã kiểm tra an toàn thông tin`; Nội dung `Tên mô hình thí điểm`. Cả bốn không required, bật Hiển thị trên tổng quan. Lưu → đợi “Đã đối chiếu dữ liệu đã lưu” → Công bố → Xác nhận → thấy Đã công bố/Tạo phiên bản mới.

### Cây đã giao thành công

| Logical key | Mã hiện tại | Cha → người nhận | Form | Từ–Đến 2026 / lịch |
|---|---|---|---|---|
| M02_PX | WA000001 | Work → px01 | M02 | 01/09–30/10, một lần |
| M02_PA | WA000002 | Work → pa02 | M02 | 01/09–30/10, ngày 30 hàng tháng |
| M02_PX_HAC | WA000003 | PX → cap_hacthanh | M02 | 01/09–30/09, một lần |
| M02_PX_SAM | WA000004 | PX → cap_samson | M02 | 01/09–14/10, một lần |
| M02_PA_HAC | WA000005 | PA → cap_hacthanh | M02B | 01/09–29/10, ngày 29 hàng tháng |
| M02_PA_SAM | WA000006 | PA → cap_samson | M02B | 01/09–28/10, ngày 28 hàng tháng |

PA02 parent ID đọc từ UI: `6ac5630f6710e17b1dc41a41`. Các ID khác có thể đọc qua Chi tiết/cây khi cần; không suy từ thứ tự ObjectId. Mỗi lần giao chọn một đơn vị. Khi chuyển Một lần→Định kỳ, nhập lại Đến ngày vì UI có thể xóa mốc này. Sau click đơn vị, chờ tên và “Xem 1 đơn vị đã chọn”, hết trạng thái kiểm tra trước Tạo mới. Đợi danh sách có row rồi mới coi đã tạo.

### Sáu report nguồn đã nhập / đọc lại

| Logical key | Ngày vận hành | Kỳ nghiệm thu | An toàn thông tin | Tên mô hình | Ngày hoàn thành lịch sử | Trạng thái cuối đã kiểm |
|---|---|---|---|---|---|---|
| M02_R_PX_HAC | 15/09/2026 | 09/2026 | Có | Tiếp nhận hồ sơ điện tử Hạc Thành | 29/09/2026 | PX01 đã duyệt |
| M02_R_PX_SAM | 20/09/2026 | 10/2026 | Không | Tiếp nhận hồ sơ điện tử Sầm Sơn | Không có, report hiện tại | PX01 đã duyệt |
| M02_R_PA_HAC_SEP | 18/09/2026 | 2026 | Có | Điểm tiếp nhận số Hạc Thành — tháng 9 | 29/09/2026 | PA02 đã duyệt/đã xác nhận |
| M02_R_PA_SAM_SEP | 25/09/2026 | 09/2026 | Không | Bàn tiếp nhận số Sầm Sơn — tháng 9 | 27/09/2026 | PA02 đã duyệt/đã xác nhận |
| M02_R_PA_HAC_OCT | 05/10/2026 | 10/2026 | Không | Điểm tiếp nhận số Hạc Thành — mở rộng tháng 10 | Không có | Nháp, giữ có chủ đích |
| M02_R_PA_SAM_OCT | 06/10/2026 | 2027 | Có | Bàn tiếp nhận số Sầm Sơn — mở rộng tháng 10 | Không có | Đã nộp, giữ chờ duyệt có chủ đích |

Report IDs đã thấy trong UI: PX Hạc `6ac5649c6710e17b1dc41f48`; PX Sầm `6ac566096710e17b1dc42421`; PA Hạc Sep `6ac564c86710e17b1dc41f7a`; PA Hạc Oct `6ac564de6710e17b1dc41f9f`; PA Sầm Sep `6ac566356710e17b1dc42500`; PA Sầm Oct `6ac566766710e17b1dc4287b`. Bốn nguồn đã duyệt, một đã nộp, một nháp.

Thao tác: vai phường → Work/REPORT → row đúng Form → Mở chi tiết → row đúng kỳ → Mở báo cáo → Bắt đầu nhập biểu mẫu. **Ngày đầy đủ trong Dynamic Form nhập `15/09/2026` có dấu `/`**, khác control ngày metadata tự mask nhận `29092026`. Kỳ thiếu ngày nhập `09/2026` hoặc `2026`, không điền ngày giả. Checkbox mặc định chưa nhập; để lưu Không tường minh đã bật Có rồi bỏ chọn, kiểm điều hướng vẫn tính field “Có giá trị”. Save/Submit → đợi Đối chiếu đóng → thấy Đã lưu/Đã nộp và field chỉ đọc. Duyệt lịch sử kiểm ngày hoàn thành → Xác nhận và duyệt.

Bằng chứng: `m02-two-parent-branches.png`, `m02-px01-two-leaves.png`, `m02-pa02-two-leaves.png/.txt`, `m02-hacthanh-once-submitted.png`, `m02-hacthanh-sept-submitted.txt`, `m02-hacthanh-sept-reopen.txt`, `m02-px01-hacthanh-approved.txt`, `m02-samson-once-submitted.txt`, `m02-samson-sept-submitted.txt`, `m02-samson-oct-submitted.png/.txt`, `m02-pa02-samson-periods.png/.txt`.

### Tổng hợp PX01 — đã áp dụng một trường, ba trường còn chặn

Report PX01 `6ac567606710e17b1dc42b56` đã tạo qua Mở báo cáo. Cấu hình r1: thêm source M02 → chọn Ngày vận hành → Tính toán → source port tới A → Thiết lập `=DATE_MIN(GET(A))` → output tới Ngày vận hành đích → Xem trước **15/09/2026, Hợp lệ** → Lưu và cập nhật nền → “Đã tính và cập nhật báo cáo”. Mở lại sau ứng dụng nạp trang: **15/09/2026**, trường disabled, nhãn “Dữ liệu này được tự động tổng hợp”, 1/4 có giá trị. Evidence `m02-px01-date-min-readback.png/.txt`.

Các bước đã dựng tiếp nhưng **chưa áp dụng** do KUI-10:

| Đích | Dây và thiết lập qua UI | Expected cần kiểm sau sửa |
|---|---|---|
| Ngày vận hành | Source → DATE_MIN(GET(A)) → đích | 15/09/2026, đã kiểm riêng r1 |
| Kỳ nghiệm thu | Source → Lọc Ngày/kỳ, `=` giá trị `09/2026` → ONLY(GET(A)) → đích | 09/2026; chỉ Hạc Thành qua lọc, giữ thiếu ngày |
| An toàn thông tin | Source → Lọc Có/Không, Bằng/Có → ONLY(GET(A)) → đích | Có; chỉ Hạc Thành qua lọc |
| Tên mô hình | Source → CONCAT(GET(A)) → đích; Bỏ khoảng trắng đầu/cuối, dấu ` | `, Đơn vị rồi kỳ | Hai tên Hạc Thành/Sầm Sơn, mỗi tên một lần; thứ tự actual chưa nhận |

Sau thêm từng khối bấm **Sắp xếp lại → Vừa khung** để không chồng khối/che điểm nối. Sau r1, Phạm vi sửa mặc định Riêng kỳ này chỉ cho phần override; chọn Cấu hình chung để thêm khối. Chọn checkbox nháp PX01 r1 khi preview revision mới. Công thức có GET được UI chuẩn hóa thành DATE_MIN(A), ONLY(A), CONCAT(A); đó không phải lỗi mất tập.

KUI-10: ONLY đổi output thành A trùng input A, preview và Lưu nháp sơ đồ báo “Mã điểm nối bị thiếu hoặc trùng trong khối”. Bốn trường chưa chạy job và chưa ghi vào report; bản r1 vẫn an toàn. Không gỡ cấu hình đã lưu để né lỗi; owner công thức xử lý theo issue handoff rồi dựng lại ba trường từ bảng trên. Nháp canvas trong browser không phải bản lưu bền vững.

Bước tiếp sau sửa: preview đủ bốn trường → kiểm nguồn/expected → lưu/cập nhật nền → mở lại → nộp PX01/PV01 duyệt. PA02 kỳ tháng 9: khai báo khoảng dữ liệu 01/09–30/09, lọc tập báo cáo bằng Ngày hoàn thành/nộp theo khoảng kỳ đích như M01. Trường Ngày/kỳ nguồn có cả `2026` và `09/2026`; nếu lọc mốc tháng thì nguồn chỉ có năm phải báo thiếu độ chi tiết. Muốn demo kết quả thành công phải chọn điều kiện đủ chi tiết (ví dụ kết hợp report/nguồn phù hợp), không tự thêm ngày/tháng cho `2026`. Work chỉ lấy hai report phòng đã duyệt. Chưa chạy PA02/root M02 và M03–M08; không duyệt hết tháng 10 để giữ đa trạng thái.
