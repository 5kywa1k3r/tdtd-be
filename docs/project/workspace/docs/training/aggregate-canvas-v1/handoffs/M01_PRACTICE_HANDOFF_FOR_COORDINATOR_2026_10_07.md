# Bàn giao agent tổng chuẩn bị bài thực hành M1

Ngày 07/10/2026. Người yêu cầu: Yud. Phạm vi: bài thực hành nhỏ về giao việc và tổng hợp số liệu qua hai cấp.

## 1. Yêu cầu trực tiếp và điểm dừng

Yud muốn dùng M1 làm bài thực hành, giữ đủ kịch bản để dựng lại thành dữ liệu mẫu sau khi reset DB. Tài liệu này dành cho **agent tổng** tiếp nhận và tổ chức tài liệu hướng dẫn; không phải bản Word phát cho học viên. M1/M2 đang tạm dừng. Không tiếp M2, không mở M3–M8, không tự reset DB hoặc tạo lại mẫu trong lượt chuẩn bị tài liệu.

Nhiệm vụ trước mắt của agent tổng: dùng gói này để biên soạn đề bài, hướng dẫn thao tác và đáp án đối chiếu của M1; lưu cùng bộ dữ liệu đầu vào. Khi Yud yêu cầu dựng lại, thực hiện bằng UX/UI theo đúng vai trò. Việc chuẩn bị bài thực hành không đồng nghĩa đã được yêu cầu thực hiện reset.

## 2. Kết quả học viên cần đạt

Người thực hành tạo được hai biểu mẫu số, giao cây 1–2–4, nhập và duyệt báo cáo nguồn, cấu hình tổng hợp vào báo cáo của phòng, rồi xem kết quả tổng hợp ở nhiệm vụ gốc tự cập nhật. Bài có cả báo cáo một lần và báo cáo định kỳ; giữ một nháp và một báo cáo chờ duyệt để thấy nguồn chưa duyệt không góp số.

Chỉ dùng bốn trường số và các phép **SUM, AVG**, cùng bộ lọc tập báo cáo theo ngày hoàn thành/nộp. M1 không có ONLY, Danh sách, Bảng hay văn bản dài. Không thêm phép tính chưa kiểm vào bài cơ bản.

## 3. Vai trò và cây mẫu

| Vai trò | Tài khoản kiểm thử hiện tại | Công việc trong bài |
|---|---|---|
| Chủ nhiệm vụ gốc | pv01 | Tạo Form M1, tạo nhiệm vụ, giao hai phòng, duyệt báo cáo phòng, cấu hình biểu mẫu xem ở gốc |
| Phòng nhánh một lần | px01 | Giao hai phường, duyệt nguồn một lần, tổng hợp và nộp lên PV01 |
| Phòng nhánh định kỳ | pa02 | Tạo Form M1B, giao hai phường định kỳ, duyệt nguồn tháng 9, tổng hợp và nộp lên PV01 |
| Phường Hạc Thành | cap_hacthanh | Nhập báo cáo một lần và hai kỳ tháng 9–10 |
| Phường Sầm Sơn | cap_samson | Nhập báo cáo một lần và hai kỳ tháng 9–10 |

Không lưu mật khẩu trong tài liệu. Sau reset cần người quản trị cung cấp lại tài khoản, đơn vị và quyền tương ứng nếu bộ tài khoản cũng bị xóa. Không nới quyền để làm bài chạy được.

```text
PV01 tạo nhiệm vụ gốc
├── PX01  báo cáo một lần bằng M1
│   ├── Hạc Thành  một lần bằng M1
│   └── Sầm Sơn    một lần bằng M1
└── PA02  báo cáo tháng bằng M1
    ├── Hạc Thành  báo cáo tháng bằng M1B
    └── Sầm Sơn    báo cáo tháng bằng M1B
```

Đây là **hai cấp giao việc dưới nhiệm vụ gốc**, gồm 1 Work và 6 phần việc đang hiệu lực. Tạo riêng mỗi lá, mỗi lần chỉ chọn một đơn vị; chọn hai đơn vị trong một lần giao không tạo hai node như cây trên.

## 4. Biểu mẫu và nhiệm vụ

PV01 tạo `M1 — Số hóa hồ sơ nghiệp vụ — Dữ liệu giả`. PA02 tạo `M1B — Số hóa hồ sơ định kỳ — Dữ liệu giả`. Cả hai có cấu trúc như nhau:

| Thứ tự | Tên trường | Kiểu | Đơn vị hiểu trong bài | Phép tổng hợp |
|---|---|---|---|---|
| 1 | Hồ sơ tiếp nhận | Số | Hồ sơ | SUM |
| 2 | Hồ sơ đã số hóa | Số | Hồ sơ | SUM |
| 3 | Giờ công tiết kiệm | Số | Giờ | SUM |
| 4 | Điểm chất lượng | Số | Điểm minh họa | AVG |

Bật Hiển thị trên tổng quan, không đặt required trong bộ gốc. Lưu rồi công bố v1 trước khi giao. Mã biểu mẫu do hệ thống cấp; dùng tên/mã mới để chọn đúng mẫu, không cố nhập lại mã cũ. Không chỉnh cấu trúc published tại chỗ.

M1B cần thiết trong bộ đã chạy vì UI chặn cùng Form và cùng người nhận ở hai nhánh. Giữ cách dựng đã kiểm: PA02 tự tạo M1B qua Designer; đích PA02 nộp lên PV01 vẫn là M1. Không bỏ rule hoặc tự clone dữ liệu DB.

Nhiệm vụ gốc: `M1 — Thí điểm số hóa hồ sơ nghiệp vụ — Cây 1 2 4 — Dữ liệu giả`; từ 01/09/2026, đến và hạn 31/10/2026; ưu tiên Trung bình. Mô tả: số liệu giả phục vụ tập huấn, không phải số liệu nghiệp vụ thực.

| Khóa dùng trong tài liệu | Người giao → người nhận | Form được giao | Lịch và hạn bộ gốc |
|---|---|---|---|
| PX | PV01 → PX01 | M1 | Một lần, hạn 30/10/2026 |
| PA | PV01 → PA02 | M1 | Tháng 9–10, ngày nộp 30 hằng tháng |
| PX_HAC | PX01 → Hạc Thành | M1 | Một lần, hạn 30/09/2026 |
| PX_SAM | PX01 → Sầm Sơn | M1 | Một lần, hạn 14/10/2026 |
| PA_HAC | PA02 → Hạc Thành | M1B | Tháng 9–10, ngày nộp 29 hằng tháng |
| PA_SAM | PA02 → Sầm Sơn | M1B | Tháng 9–10, ngày nộp 28 hằng tháng |

Tất cả bắt đầu 01/09; khoảng giao định kỳ kết thúc 31/10/2026. Phân biệt hạn biên của phần việc với hạn nộp từng kỳ. Bộ gốc được chạy ngày 07/10/2026. Nếu tổ chức ở thời điểm khác, agent tổng phải chuẩn bị bảng ngày thay thế trước buổi học: dịch đồng bộ tháng lịch sử/tháng hiện tại, lịch nộp, ngày hoàn thành và khoảng dữ liệu; kiểm ngày hợp lệ của tháng. Không đổi giờ máy, không tự biến ngày hoàn thành lịch sử thành ngày nộp hiện tại. Nếu giữ nguyên bộ ngày cũ, phải nói rõ màu/thời hạn và Dashboard tháng hiện tại có thể khác bằng chứng gốc.

## 5. Bảng dữ liệu nhập bắt buộc

| Báo cáo | Tiếp nhận | Đã số hóa | Giờ tiết kiệm | Điểm | Ngày hoàn thành lịch sử | Trạng thái cần giữ cuối bài |
|---|---:|---:|---:|---:|---|---|
| PX_HAC một lần | 120 | 108 | 36 | 9 | 29/09/2026 | Nộp và PX01 duyệt |
| PX_SAM một lần | 90 | 72 | 24 | 7 | Báo cáo hiện tại trong bộ gốc | Nộp và PX01 duyệt |
| PA_HAC tháng 9 | 80 | 60 | 20 | 8 | 30/09/2026 | Nộp và PA02 duyệt |
| PA_SAM tháng 9 | 70 | 63 | 21 | 9 | 27/09/2026 | Nộp và PA02 duyệt |
| PA_HAC tháng 10 | 50 | 35 | 12 | 7 | Không nhập | Chỉ lưu nháp |
| PA_SAM tháng 10 | 40 | 32 | 10 | 8 | Báo cáo hiện tại trong bộ gốc | Nộp và giữ chờ duyệt |

Lý do chậm của PA_HAC tháng 9: “Dữ liệu giả tập huấn: hoàn thành đối soát số hóa ngày 30/9, chậm một ngày so với hạn 29/9.” Người duyệt xác nhận đúng ngày hoàn thành của báo cáo lịch sử. Không bấm duyệt hết các kỳ tháng 10 vì sẽ mất tình huống nguồn chưa đủ điều kiện.

## 6. Trình tự bài thực hành

### Bước 1 Chuẩn bị biểu mẫu và giao cây

PV01 và PA02 tạo/công bố M1, M1B theo mục 4. PV01 tạo nhiệm vụ rồi vào Giao việc để tạo PX và PA. Đăng nhập đúng phòng, vào cùng nhiệm vụ, chọn đúng nhánh được PV01 giao rồi giao hai phường. Kiểm lại đủ 6 phần việc đang hiệu lực trước khi nhập số.

### Bước 2 Nhập và duyệt nguồn

Từng tài khoản phường vào Báo cáo kết quả thực hiện → đúng nhóm biểu mẫu → đúng kỳ/phần việc → Mở báo cáo → Bắt đầu nhập biểu mẫu. Nhập theo mục 5; lưu nháp hoặc nộp theo trạng thái yêu cầu. Chờ thông báo ghi thành công và đọc lại. PX01 duyệt hai báo cáo một lần; PA02 duyệt hai báo cáo tháng 9. Với lịch sử, kiểm ngày hoàn thành rồi xác nhận và duyệt.

### Bước 3 Tổng hợp báo cáo một lần của PX01

PX01 mở báo cáo của mình nộp lên PV01 → Tổng hợp vào báo cáo → Thêm biểu mẫu → chọn M1 nguồn. Nguồn là các báo cáo con trực tiếp hiện hành đã duyệt; không đặt điều kiện thời gian ở nhánh này.

Với mỗi trường: chọn trường trên biểu mẫu nguồn → thêm khối Tính toán → nối trường nguồn vào biến A → mở Thiết lập tính toán → nhập công thức → nối Kết quả vào đúng trường đích. Ba trường đầu dùng `=SUM(GET(A))`; điểm dùng `=AVG(GET(A))`. Mỗi khối có biến A riêng. UI có thể chuẩn hóa thành SUM(A)/AVG(A), vẫn giữ ý nghĩa tập nguồn.

Sắp xếp lại → Vừa khung → Xem trước thay đổi → đối chiếu đáp án → Lưu và cập nhật nền → đợi “Đã tính và cập nhật báo cáo” → đóng và đọc lại. Các trường mapping quản lý phải hiện dữ liệu tự tổng hợp. Nộp qua bước xem trước/xác nhận, sau đó PV01 duyệt.

### Bước 4 Tạo biểu mẫu xem ở nhiệm vụ gốc

PV01 → Giao việc → Biểu mẫu tổng hợp → gắn M1 → Cấu hình và xem trước. Chọn nguồn M1, đặt cùng ba SUM và một AVG. Nguồn cấp gốc là report trực tiếp của PX01 và PA02, không lấy lại báo cáo phường.

Thực hiện bước này khi mới PX01 được duyệt để quan sát mốc kết quả đầu tiên. Lưu cấu hình và đọc kết quả ở gốc. Đây là biểu mẫu xem của nhiệm vụ; khác nút Tổng hợp vào báo cáo khi PX01/PA02 đang soạn báo cáo nộp lên cấp trên.

### Bước 5 Tổng hợp báo cáo định kỳ tháng 9 của PA02

PA02 mở báo cáo kỳ 30/09 đích M1. Lưu ngày hoàn thành 30/09/2026 và thông tin lịch sử cần thiết trước khi tính tổng hợp.

Mở Tổng hợp vào báo cáo → Khoảng dữ liệu → chọn 01/09–30/09/2026 → xem trước khai báo → xác nhận. Thêm nguồn M1B. Trên biểu mẫu nguồn mở **Lọc tập báo cáo** → Ngày hoàn thành/nộp → Trong khoảng → Theo khoảng dữ liệu kỳ đích → Dùng bộ lọc.

Chọn Cấu hình chung và nối bốn phép tính như PX01. Xem trước, đối chiếu đáp án, lưu và cập nhật nền, mở lại kiểm số liệu. Nộp PA02; PV01 xác nhận lịch sử và duyệt. Hai báo cáo tháng 10 chưa duyệt không góp số. Khoảng dữ liệu tách biệt với hạn nộp; báo cáo quá khứ dùng ngày hoàn thành cho bộ lọc này.

### Bước 6 Quan sát cập nhật lên gốc

PV01 mở lại biểu mẫu tổng hợp ở nhiệm vụ gốc sau khi PA02 được duyệt. Chờ xử lý nền hoàn tất và đọc kết quả mới; không sửa công thức gốc để ép số. Ghi kết quả trước/sau, thời điểm quan sát và nguồn góp số. Nếu chưa cập nhật, ghi trạng thái công việc nền và chờ/đối chiếu; không nhập tay số vào ô tự tổng hợp.

### Bước 7 Đọc cây và trạng thái

Mở mindmap từ Dashboard theo nhiệm vụ mẫu, bung cả hai nhánh. Kiểm 1 nhiệm vụ + 2 phòng + 4 lá. Báo cáo Đã duyệt không đồng nghĩa phần việc hoặc cả nhiệm vụ đã hoàn thành. Trong bộ gốc, Hạc Thành một lần tự hoàn thành khi đủ điều kiện và hết hạn; các nhánh khác vẫn đang thực hiện. Không thao tác kết thúc để ép màu.

Dashboard báo cáo xét các kỳ có hạn trong tháng hiện tại. Hai báo cáo lịch sử tháng 9 không mặc nhiên làm tăng số báo cáo tháng 10. Bài M1 chỉ minh họa các trạng thái đã có, không yêu cầu tạo đủ mọi màu Dashboard.

## 7. Đáp án cho người hướng dẫn

| Mốc kiểm | Tiếp nhận | Đã số hóa | Giờ tiết kiệm | Điểm |
|---|---:|---:|---:|---:|
| PX01 sau tổng hợp hai phường | 210 | 180 | 60 | 8 |
| PA02 tháng 9 sau tổng hợp hai phường | 150 | 123 | 41 | 8,5 |
| Gốc khi chỉ PX01 được duyệt | 210 | 180 | 60 | 8 |
| Gốc sau khi cả PX01 và PA02 được duyệt | 360 | 303 | 101 | 8,25 |

Điểm cấp gốc là AVG của hai report phòng: (8 + 8,5) / 2 = 8,25; không mặc nhiên là trung bình theo hồ sơ hoặc có trọng số. Bộ này không có số trống để kiểm quy tắc mẫu số AVG. Không cộng lặp số của cả phòng và các phường cháu.

Câu hỏi ngắn cho học viên: Vì sao tháng 10 có số liệu mà PA02 tháng 9 không lấy? Vì sao ở gốc chỉ có hai nguồn góp số? Vì sao đã duyệt báo cáo nhưng nhánh vẫn chưa hoàn thành? Khai báo khoảng dữ liệu có thay hạn nộp không?

Đáp án: nguồn cần đủ điều kiện duyệt và qua bộ lọc; mỗi cấp chỉ lấy con trực tiếp; trạng thái báo cáo tách khỏi hoàn thành phần việc; khoảng dữ liệu và hạn là hai khái niệm riêng.

## 8. Phiếu kiểm cuối bài

- [ ] Hai Form đã công bố, chọn đúng M1/M1B khi giao và làm báo cáo.
- [ ] Cây có đúng 6 phần việc đang hiệu lực, không có giao trùng hoặc gộp nhầm hai phường thành một lá.
- [ ] Sáu report nguồn đúng bảng; bốn đã duyệt, một nháp, một chờ duyệt.
- [ ] Hai report phòng đã tổng hợp, nộp và PV01 duyệt; PA02 tháng 10 chưa cần tạo report phòng.
- [ ] Đọc lại đủ bốn mốc kết quả chuẩn; ở gốc chỉ hai report phòng góp số.
- [ ] Đã ghi ảnh/URL theo các vai trò và giải thích được trạng thái trên mindmap.
- [ ] Đã lưu bảng khóa mẫu → URL/ID mới để tái sử dụng; không đưa mật khẩu vào tài liệu.

## 9. Lưu lại trước reset và cách dựng lại

Giữ file này cùng `KHCN_SAMPLE_UI_REPLAY_2026_10_07.md`, `KHCN_UI_SAMPLE_TREE_2026_10_07.md`, `KHCN_UI_ISSUES_FOR_OWNERS_2026_10_07.md` và thư mục `outputs/khcn-ui-samples-20261007/`. Đây là gói tái lập có giá trị đầu vào và bước thao tác, **không phải dump DB hay script seed**.

Sau reset, tạo Form/publish/giao/nhập qua UI sẽ phát sinh ID, code, phiên bản và hash mới. Không gán lại ObjectId/hash cũ. Không dựng lại hai phần việc giao thử đã ngừng WA3/WA4 của bộ cũ. Bảng ghi mới chỉ cần: M1, M1B, Work, PX, PA, PX_HAC, PX_SAM, PA_HAC, PA_SAM, sáu report nguồn và hai report phòng → URL/ID mới.

## 10. Bằng chứng và lưu ý cho agent tổng

M1 đã chạy xuyên luồng trên bộ gốc; bản sửa KUI-01–07 có mức kiểm source/test/browser khác nhau. Không ghi “toàn bộ hệ thống đã nghiệm thu” trong tài liệu thực hành. Khi mở lại buổi thực hành cần xác nhận host có bản BE phù hợp và đọc lại các ca hạn/hiệu lực/tên Form còn thiếu theo `outputs/khcn-ui-fixes-20261007/RESULTS.md`.

KUI-10 chặn M2 với ONLY, không nằm trong bộ công thức M1 này. KUI-08/09 vẫn là quan sát cần owner kiểm. Nếu gặp cảnh báo hậu xử lý, không gửi lại lệnh nộp mù hoặc xóa pending; ghi báo cáo đã commit hay chưa. Nếu báo nguồn thay đổi, giữ nháp và xem trước/tính lại theo UI trước khi nộp. KUI-02 có sửa giữ nháp nhưng không coi nháp đang mở trong browser là bản dữ liệu đã lưu bền vững.

Điểm mở bộ hiện tại trước reset: `http://localhost:5173/works/6ac5517cfb2c9c48e5a42b0d`. Sau reset URL này không còn là điểm mở bài mới.

Ảnh tham khảo cho agent biên soạn nằm trong `outputs/khcn-ui-samples-20261007/`: `px01-aggregate-readback.png`, `pa02-periodic-readback.png`, `pv01-root-two-branches-readback.png`, `mindmap-1-2-4.png`. Ảnh mindmap cũ có sai lệch hạn KUI-01; chỉ dùng để minh họa cấu trúc hoặc chụp lại sau kiểm bản sửa, không dùng hạn sai làm hướng dẫn.

Đầu ra mong muốn từ agent tổng: một bài thực hành M1 ngắn có đề bài, bảng dữ liệu, đường thao tác, đáp án và phiếu tự kiểm; có thể chuyển agent HTML trình bày sau. Giữ nguyên các số liệu và ranh giới nguồn trong gói này. Chưa cần mở rộng mẫu khác hoặc triển khai tối ưu kỹ thuật.
