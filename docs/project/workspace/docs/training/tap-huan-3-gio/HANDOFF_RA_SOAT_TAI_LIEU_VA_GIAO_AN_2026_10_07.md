# Rà soát tài liệu và giáo án ba giờ — đề xuất để rà

Ngày 07/10/2026. Yêu cầu trực tiếp: đọc tài liệu đã soạn, yêu cầu agent tài liệu sửa nếu cần và liệt kê nội dung bài giảng để tránh quá thời gian.

**Cập nhật sau khi đọc bài M1 mới:** yêu cầu mới nhất tăng giải lao lên 15–20 phút. Bản nội dung hiện hành chọn **180 phút, Tổng hợp 50 phút, nghỉ 20 phút**, dùng M1 Số hóa hồ sơ làm bài thực hành chính. Xem [giáo án hiện hành để duyệt](GIAO_AN_180_PHUT_2026_10_07.md) và [gói bài thực hành](bai-thuc-hanh-so-hoa-ho-so/README.md). Các bảng thời lượng/50 phút phía dưới là phương án ban đầu trước khi tiếp nhận M1, giữ để tra lịch sử; không dùng làm lịch giảng mới. Nhận xét về độ dày/ảnh thiếu của guide v0.8.6 vẫn áp dụng.

## Phạm vi đã đọc

- Guide hiện hành v0.8.6 tại `D:/Job/CA/tdtd-guide/`: README, AGENTS, layout, nguồn Biểu mẫu động, nguồn giao việc/báo cáo, nguồn và Markdown Mẫu 2; đọc QA/handoff/bảng ảnh v0.8.6 và xem hai ảnh đã lưu của HTML/bản in.
- Bộ tập huấn: README, prompt tiếp nhận, handoff PA02 mới nhất và prompt viết HTML/Markdown.
- Đây là rà nội dung/source và ảnh QA đã lưu. Không chạy lại browser/build/print của guide, không kiểm sản phẩm trong lượt rà này. Số trang in dưới đây lấy từ QA của agent, không phải kết quả in lại của Lam.

## Nhận xét

**Phần đã có:** phân biệt chức năng nhỏ và bài nối; tra 10 kiểu dữ liệu và 3 khối; bài tạo mẫu tới Lưu/Nhập thử/Công bố; Mẫu 1 có vòng trả lại–sửa–nộp lại–duyệt; Mẫu 2 có M01/M02 tới người nhận đọc Đã duyệt. Dữ liệu QT01/QT02, mô hình và hai Bảng nằm trong bài. Quét phần chữ HTML sau bỏ script/style không thấy Yud, Lam, prompt, handoff hoặc PASS.

**Cần sửa trước khi dùng như giáo án:**

1. Mẫu 2 có 37 mục tra cứu, 11 mục bài M01 và 12 mục bài M02: tổng 60 thẻ, 230 bước nhỏ. Riêng bài M02 có 83 bước, trùng nhiều với 20 mục tra cứu M02. Toàn guide có 119 thẻ theo QA. Không giảng tuần tự tất cả thẻ.
2. Vai tài khoản bị nhắc lại ở từng trường trên cùng màn; kết quả kiểu “có đúng giá trị mẫu” lặp mà ít giúp đối chiếu. Ở phần nhập trường đơn, chuyển sang một bảng Trường — Giá trị và một kết quả chung cụ thể. Vẫn giữ bước riêng cho Lưu, Nộp và đổi vai.
3. Mục mở đầu M02 lặp lời nhắc dữ liệu giả. Sơ đồ giao/báo cáo đang là chuỗi dài; cần sơ đồ ngắn có vai, chiều giao xuống/báo cáo lên và liên kết tới mốc thực hành.
4. Bảng ảnh v0.8.6 còn thiếu sao chép/đổi tên, cấu hình Danh sách, Lưu/Nhập thử/Công bố, đại diện nhận, các khối report/ngày hoàn thành và màn xem/duyệt. Không dùng ảnh khác mô hình/vai để lấp thiếu. Bộ ảnh PA02 mới có 35 ảnh thật để bổ sung đúng phạm vi M03/M04.
5. QA v0.8.6 ghi bài nối M02 28 trang, chương V 172 trang, toàn bộ 226 trang. Tạo bản giáo án gọn riêng; sổ tay chi tiết và bản in giáo án phục vụ hai nhu cầu khác nhau. Không thu chữ/ảnh để ép số trang.
6. Chưa có hướng dẫn đầy đủ cho tự duyệt ngày hiện tại, tổng hợp đủ cây, báo cáo phòng gửi PV01 và View nhiệm vụ cuối luồng. Đây là các phần chờ UAT, không phải phần đã viết đạt. Không đưa thêm bốn walkthrough dài khi nhận bằng chứng mới.

## Nội dung giảng đề xuất

| Mốc | Phút | Nội dung và cách tổ chức | Kết quả cần thấy |
|---|---:|---|---|
| 0–20 | 20 | Đăng nhập/định hướng; tài khoản đại diện và cá nhân; menu chính. Giảng viên minh họa một Đội/Tổ, cấp một tài khoản đúng đơn vị/chức vụ, chỉ vị trí tải mẫu và danh mục. Học viên chỉ đăng nhập và tìm việc. | Đúng tài khoản/đơn vị; biết nơi nhận việc. Không lầm chức vụ lãnh đạo với quyền quản trị. |
| 20–45 | 25 | Biểu mẫu động: cấu trúc Phần/trường/Danh sách/Bảng. Dùng mẫu chuẩn bị sẵn hoặc bản sao; minh họa sửa vài trường Số, Nội dung, Chọn một, Ngày, một dòng Danh sách và vùng tệp. Lưu → Nhập thử → Công bố. Tra kiểu khác khi cần. | Biết lưu và công bố mẫu dùng để giao; hiểu STT tự có và vùng tệp chung. Không tạo đủ 15 trường từ đầu tại lớp. |
| 45–70 | 25 | Mẫu 1: PV01 tạo nhiệm vụ, chọn lãnh đạo/hạn/mẫu, giao hai đơn vị, nhận thông báo và mở đúng phần việc. Giới thiệu cây Mẫu 2 PV01 → hai phòng → bốn đội; phòng/xã dùng Đội/Tổ tương ứng; lịch ngày và cấu hình tự duyệt. | Phân biệt nhiệm vụ, phần việc và kỳ báo cáo; biết việc của người giao/người nhận. Phần thiết lập cây/lịch Mẫu 2 được chuẩn bị trước. |
| 70–100 | 30 | 12 phút demo Mẫu 1: nhập–lưu–nộp, trả lại–sửa–nộp lại–duyệt. 13 phút cả lớp nhập một báo cáo Mẫu 2 ở kỳ còn mở, dùng bảng dữ liệu chung; 5 phút xem kết quả duyệt/nghĩa vụ. Tự duyệt ngày hiện tại chỉ dùng khi đã qua UAT. | Mẫu 1 có kết quả sau duyệt; mỗi nhóm Mẫu 2 có một report đúng kỳ, dữ liệu được đọc lại. Không bắt nhập bốn mô hình × bốn ngày. |
| 100–110 | 10 | Nghỉ. Giảng viên kiểm nguồn đã chuẩn bị/nguồn lớp vừa nộp. | Không dùng giờ Tổng hợp để nhập nốt lịch sử. |
| 110–160 | 50 | Tổng hợp từ bài Mẫu 2; dùng cấu hình chuẩn bị sẵn, sửa/thử các điểm chính theo bảng dưới. Chỉ triển khai thao tác đã có bằng chứng UAT. | Đọc đúng nguồn/kỳ, số, nội dung, danh sách/bảng; cập nhật, đọc lại và mở nguồn. Phân biệt tổng hợp vào report phòng gửi lên với View nhiệm vụ. |
| 160–180 | 20 | 5 phút đọc Tổng quan/phạm vi và trạng thái; 10 phút hoàn tất thao tác, hỗ trợ nhóm còn vướng; 5 phút hỏi đáp. Dashboard chi tiết và đánh giá/chấm điểm tiến độ chờ phần riêng. | Biết báo cáo Đã duyệt chưa đồng nghĩa toàn phần việc hoàn thành; biết tìm việc còn cần xử lý và báo lỗi có ngữ cảnh. |
| **Tổng** | **180** | **Đề xuất chờ rà.** | |

## Cách dùng 50 phút Tổng hợp

| Phút | Nội dung |
|---:|---|
| 5 | Ngữ cảnh report phòng/View nhiệm vụ; nguồn trực tiếp, kỳ và trạng thái nguồn. |
| 12 | COUNT đếm báo cáo hay phần tử, SUM kinh phí, AVG điểm hài lòng trên đúng tập dữ liệu. Không gộp điểm/chỉ số riêng của các mô hình khác nhau. |
| 8 | GET lấy trường; IF với GET/COUNT, một điều kiện đơn giản. |
| 10 | CONCAT nội dung/văn bản dài; bảng Đơn vị — Khó khăn; mở nguồn của dòng. |
| 10 | Nối Danh sách và Bảng; minh họa tính SUM/AVG/CONCAT trong Bảng bằng cấu hình đã chuẩn bị. |
| 5 | Xem kết quả, xác nhận/cập nhật theo nhãn đã kiểm, đọc lại và truy nguồn. |

Cả lớp thực hành một báo cáo, theo dõi ba ví dụ COUNT/SUM/CONCAT. Giảng viên minh họa AVG, GET/IF và xử lý Danh sách/Bảng bằng cấu hình sẵn. Không dựng toàn bộ công thức từ đầu hoặc giảng mọi biến thể.

## Cách tránh quá giờ

- Một đường đi chính: Mẫu 1 do giảng viên demo; Mẫu 2 do lớp cùng nhập. M01/M02/M03/M04 là dữ liệu/mô hình của bài Mẫu 2, không phải bốn bài giảng mới.
- Giảng viên dùng một nhánh và một kỳ để thao tác chi tiết; cây 1 → 2 → 4 và bốn ngày giữ làm nguồn cho tổng hợp. Các ngày quá khứ được chuẩn bị và duyệt trước buổi.
- Các phép tổng hợp cần một tệp công thức/Canvas chuẩn bị sẵn đã kiểm; lớp chỉ sửa vài mapping/điều kiện theo yêu cầu bài. Không thay dữ liệu thật hoặc seed để làm tài liệu.
- Các ca mật khẩu sai, reset/ngừng tài khoản, nhập tệp lỗi, điểm ngoài giới hạn, thiếu ngày, kiểm bốn mô hình/bốn ngày và notification cũ thuộc tra cứu/Q&A/UAT. Chỉ demo một lỗi có ích nếu còn giờ.
- Nếu chậm tới phút 70, dùng nhiệm vụ/cây giao đã chuẩn bị và đi thẳng nhận–nhập–nộp. Nếu chậm tới phút 100, dừng thao tác lặp và nghỉ; sau nghỉ dùng nguồn hợp lệ đã chuẩn bị. Giữ 50 phút Tổng hợp, dùng 10 phút hỗ trợ cuối buổi cho nhóm chậm.
- Không bỏ Lưu/Công bố, Lưu/Nộp, kiểm kết quả sau duyệt hoặc mở nguồn tổng hợp để tiết kiệm thời gian.

## Trạng thái và phối hợp

Ngày 04 đã duyệt cả bốn mô hình, 4/12 report quá khứ; chưa qua tự duyệt hôm nay hoặc tổng hợp đầy đủ. M03 có vướng hộp nhập ô đã khôi phục, danh sách duyệt thiếu tên mẫu; M04 đã nhận/mở được thông báo duyệt ở lượt đọc sau, độ trễ chưa đo. Không gọi toàn Mẫu 2 nghiệm thu.

Yêu cầu sửa dành cho task **Cập nhật hướng dẫn UAT Mẫu 2**. Prompt thực hiện: [Rút gọn tài liệu và giáo án](PROMPT_AGENT_RUT_GON_TAI_LIEU_VA_GIAO_AN_2026_10_07.md); nội dung ưu tiên là giáo án mới/nghỉ 20 phút/M1 Số hóa hồ sơ. Lam không sửa nguồn guide của agent trong lượt này; không tạo task/agent mới. Agent cần bàn giao bản sửa để rà lại, không tự chốt chương trình đã được duyệt.

Đã gửi yêu cầu vào task hiện có `01a11308-2037-7832-83df-8bad9b11c510` theo ủy quyền trực tiếp của người dùng. Snapshot sau gửi xác nhận task đang chạy và agent đã nhận đúng Mẫu 1 demo/M1 Số hóa thực hành/bốn mô hình tra cứu/nghỉ 20 phút. Chưa có bản sửa hoàn tất ở mốc handoff này.

Lam đã tạo giáo án và gói Số hóa hồ sơ năm tệp ngắn; cập nhật README/prompt để phân biệt lịch mới với lịch 10 phút nghỉ cũ. Kiểm số học của bảng dữ liệu/đáp án đạt, lịch cộng đúng 180 phút; kiểm 77 liên kết nội bộ trong chín tệp, không có liên kết hỏng. Các kiểm này thuộc tài liệu/số liệu đề bài, không phải engine tổng hợp hoặc UAT mới. Không reset DB, chạy sản phẩm hoặc tạo lại bài trong lượt này.

Nguồn đối chiếu: [README tập huấn](README.md), [handoff PA02 mới nhất](HANDOFF_UAT_MAU_2_PA02_2026_10_07.md), [prompt Mẫu 2 có ảnh](PROMPT_AGENT_HTML_MAU_2_M01_M02_2026_10_07.md); `D:/Job/CA/tdtd-guide/qa/QA-V086.md`, `qa/MAU2-M02-V086-IMAGES.md`, `content/mau2-guide.md`.

