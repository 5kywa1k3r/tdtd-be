# TD-TD Guide

> Mốc điều phối 08/10/2026: [bàn giao chung của 5 Lam](../tdtd/docs/coordination/LAM_CURRENT_2026_10_08.md). Nguồn offline v0.8.7; bản web đã tích hợp qua thư mục tĩnh FE và có sửa đóng gói mới ngày 08/10. Đọc `qa/TRAINING-V087-HANDOFF.md` và `qa/QA-HOSTED-GUIDE-20261008.md` trước các trạng thái lịch sử bên dưới.
Sổ tay HTML độc lập dành cho cán bộ. Phiên bản 0.8.7 giữ một trang và một menu. Có năm chương, demo Mẫu 1 hiện có, thực hành M1 Số hóa hồ sơ và Mẫu 2 nâng cao tới M01/M02 ngày 04/10 đã duyệt.

## Mở ngay
Mở index.html trong trình duyệt. File tự chứa ảnh, CSS, JavaScript; không cần TD-TD chạy.
Bản phân phối index.html tự chứa gồm I. Đăng nhập; II. Quản trị đơn vị; III. Quản trị tài khoản; IV. Tạo biểu mẫu động; V. Nhiệm vụ, giao việc và báo cáo. Chương V có tra cứu và demo Mẫu 1, tra cứu Mẫu 2, bài nối M01 và bài nối M02. Một menu và một khung hỏi–đáp dùng chung.

## Dành cho agent
Đọc AGENTS.md → REQUIREMENTS.md → PLAN.md → content/layout.json, content/01-tai-khoan-mu.json và content/dynamic-form-guide.json → qa/SOURCE-MAP.md, qa/UAT-MATRIX.md.
Chỉ được sửa sản phẩm trong phạm vi Yud giao rõ; ngoại lệ v0.4 gồm bố cục Người dùng và trích hộp kiểm tra nhập dùng chung. Đọc AGENTS.md trước khi sửa.
FE hiện phục vụ bản web tại `../tdtd/tdtd-fe/public/huong-dan/`. Sau khi sinh source mới, dùng `npm run guide:sync` tại FE rồi kiểm `guide:check`; không sửa tay bản đóng gói. Bản offline và GUIDE_QA giữ ranh giới riêng với UAT sản phẩm.

## Lệnh
- npm run build — sinh lại index.html từ source bằng Node.js; có renderer tools/render-dynamic-form-guide.mjs.
- npm run check — kiểm tài liệu, nội dung, liên kết và mô hình tương tác bằng Node.js; đây không phải UAT sản phẩm.
- npm run preview — xem tại http://127.0.0.1:5187, chỉ bind loopback. Phần biểu mẫu: http://127.0.0.1:5187/#bieu-mau-dong.
- npm run check:dom — kiểm DOM mô phỏng tùy chọn; cần jsdom có sẵn tại FE tham chiếu. Không xác nhận browser, responsive hoặc bản in thật.
- tools/qa-browser.mjs là công cụ lịch sử của bản cũ. Browser và ảnh mới dùng phương thức được môi trường hiện tại cho phép; không dùng công cụ cũ để vượt giới hạn.
- node tools/capture-fe.mjs — tái chụp FE với dữ liệu giả; đọc thư viện ở ../tdtd/tdtd-fe và ../tdtd.
Biến TDTD_SOURCE chỉ dùng cho công cụ ảnh/QA, mặc định dự án TD-TD ngang cấp.
Build/check không cần npm install.

## Thư mục
content/ — dữ liệu chương, template bài; dynamic-form-guide.json schemaVersion 2 giữ ba nhóm nội dung, 13 mục tra cứu, 14 bước bài tổng thể và hỏi đáp
src/ — giao diện và tương tác dùng chung
assets/ — ảnh đã che thông tin nhạy cảm
tools/ — build, preview, QA và chụp FE tùy chọn
qa/ — nguồn, trạng thái UAT và kết quả kiểm chứng
index.html — bản phân phối tự chứa, không sửa tay
.cache/ — tệp tạm của công cụ tạo ảnh; không phân phối

## Trạng thái lịch sử các phiên bản đầu
Ảnh lấy từ component FE thực chạy trong fixture với dữ liệu mẫu, không kết nối DB thật.
Người dùng đã cho biết UAT full luồng trước đây; lượt kiểm UAT lại của chương này chưa có bằng chứng thực trong project.
Xem qa/FORM-GUIDE-V07-HANDOFF.md và qa/FORM-SOURCE-V07.md (chương hiện tại), qa/QA-V06.md (bản ghép trước), qa/QA-V051.md, qa/QA-V05.md, qa/QA-V04.md, qa/QA-V03.md, qa/QA-V02.md, qa/QA-REPORT.md (lịch sử) và qa/UAT-MATRIX.md để phân biệt phần đã kiểm với phần đang chờ.

## Cấu trúc v0.3.0 (lịch sử, thay bằng v0.5.0)
I. Giao diện đăng nhập: Người dùng (cán bộ, chỉ huy, lãnh đạo) và Quản trị (tài khoản quản trị).
II. Giao diện quản trị: nhóm Quản trị tài khoản và Quản trị người dùng; nhãn thao tác bám tab Đơn vị / Người dùng của sản phẩm.
content/layout.json tổ chức phần/nhóm, không sửa tay bản HTML sinh ra.
node tools/check-guide-dom.mjs kiểm 9 nhóm tích hợp DOM mô phỏng; cần jsdom có sẵn tại source FE tham chiếu, chỉ là công cụ QA tùy chọn.
node tools/menu-fixture.mjs phục vụ component FE để chụp lại ảnh menu khi cần; đọc tools/fixture/README.md.

## Tải mẫu và nhập thử v0.4.0
- Bài Nhập danh sách từ tệp có bảng quy ước 11 cột Đơn vị / 7 cột Người dùng, ví dụ riêng phường/xã và phòng.
- Bấm Mở bài nhập thử, dùng menu FE gốc để tải XLSX/CSV và chọn lại tệp. Có 16 tệp: 2 cấp × 2 tab × đúng/lỗi × 2 định dạng.
- Menu ImportDataMenu và hộp ImportPreviewDialog dùng cùng source với sản phẩm. Bộ đọc/kiểm/xác nhận của bài thử là adapter dữ liệu mẫu, chỉ trong bộ nhớ.
- assets/import-practice.html là artifact đã đóng gói, nhúng base64 trong index.html. Normal build đọc artifact này, không cài package, không chạy FE/BE.
- Tái tạo artifact khi component hoặc mẫu thay đổi: node tools/import-practice/build.mjs; cần esbuild/exceljs đã có trong ../tdtd/tdtd-fe. Sau đó build tài liệu và kiểm lại; manifest nằm ở qa/import-source-v04.json.
- Bảng ví dụ của tài liệu đọc từ đúng CSV hợp lệ, tránh khác nội dung tệp được tải trong bài thử.
- Công cụ browser cũ trong README là công cụ tùy chọn lịch sử. Lượt v0.4 thao tác/chụp bằng CUA theo quyền của môi trường; không dùng Playwright trực tiếp để thay thế hành động bị chặn.

## Cấu trúc cơ bản v0.5.0
I. Đăng nhập; II. Quản trị đơn vị; III. Quản trị tài khoản. Tài khoản quản trị khác tài khoản cá nhân cán bộ/chỉ huy/lãnh đạo; bảng cây và mã đơn vị giải thích theo nơi công tác.
content/account-basics.json giữ ví dụ từ bộ kiểm thử pv01/cap_hacthanh; không đồng nhất các mã đó với dữ liệu hư cấu trong bài thử offline.
Nhập tệp có bảng tra unitCode/positionCode khớp mẫu tải thử và hướng dẫn lấy danh mục triển khai ở trang Units/Positions. Checklist ngắn ở qa/UAT-MATRIX.md.

## Danh mục tải riêng v0.5.1
Đầu bài Nhập danh sách từ tệp có hai nút tải XLSX: Danh mục mã đơn vị / Danh mục mã chức vụ. Chọn Phường/xã hoặc Phòng ngay tại đó; mục lục cũng có Tải danh mục tra mã.
Tệp thực ở assets/catalogues/, nguồn từ hai sheet tham chiếu của XLSX mẫu. HTML nhúng cả bốn tệp để mở offline, không cần runtime bài thử. Tệp không có tài khoản hoặc mật khẩu.
Tái tạo bằng tools/build-catalogues.mjs theo runtime bảng tính sẵn có; xem qa/catalogues-v05.json. Sau khi tái tạo, build/check tài liệu.

## Ghép hướng dẫn v0.6.0 — 06/10/2026

Một trang và một menu dùng chung tại index.html; không nhúng nguyên trang HTML cũ hoặc mở một guide thứ hai trong luồng đọc. Liên kết chuẩn để gửi phần biểu mẫu là http://127.0.0.1:5187/#bieu-mau-dong. Giữ các ID bài tài khoản trước đây; ID bài biểu mẫu có tiền tố bieu-mau- để tránh trùng neo.

- tools/render-dynamic-form-guide.mjs đọc content/dynamic-form-guide.json; tools/build.mjs ghép phần IV, menu, dữ liệu hỏi–đáp và tương tác vào index.html.
- assets/dynamic-form/ chứa 9 ảnh mới; normal build nhúng bytes ảnh thành data URL. assets/form-practice.html là artifact FE đã lưu, được nhúng offline vào iframe sandbox chỉ cho script. Không gọi API, DB, dùng token hoặc lưu storage.
- Hai nhóm hỏi–đáp dùng chung #hoi-dap, bộ chọn câu và ô từ khóa. Đổi nhóm giới hạn câu hỏi trong nhóm đó; không ẩn bài hướng dẫn. src/qa-groups.css giữ bố cục của bảng tra cứu ở khung nhỏ.
- Bài biểu mẫu có 15 trường nghiệp vụ trong Danh sách, STT tự động và một vùng Tài liệu minh chứng chung ngoài Danh sách. Tài khoản thietke_bieumau_mau chỉ là nhãn dữ liệu mẫu; lưu nháp trong bài thử chỉ giữ dữ liệu trong phiên.
- docs/training/tap-huan-3-gio/ ở sản phẩm giữ vai trò nguồn nội dung và handoff lịch sử của phần biểu mẫu. Không sửa biểu mẫu thật đã công bố hoặc thay bằng chứng STT cũ khi ghép guide. Bản HTML ở cổng 5191 là mốc bản xem riêng trước khi ghép, không phải liên kết phân phối chuẩn hiện tại.
- Kết quả kiểm của bản v0.5.1 hoặc chương độc lập không tự chứng minh v0.6 đã qua browser/print. Ghi bằng chứng của bản ghép riêng; LIVE_UAT và QA tài liệu vẫn tách nhau.

## Viết lại chương Biểu mẫu động v0.7.0 — 06/10/2026

Yêu cầu mới thay điểm dừng Bản nháp của v0.6: hướng dẫn phần mềm đến Công bố, vẫn giữ một guide và một menu. Không kéo dài lịch tập huấn hoặc sửa các chương cơ bản. Nội dung mới không bị ép vào sáu thẻ cũ.

- A cho thấy biểu mẫu hoàn chỉnh và quan hệ Biểu mẫu → Phần → trường độc lập/Bảng/Danh sách; phân biệt người thiết kế với người nhập báo cáo.
- B có 13 mục tra cứu: 10 kiểu dữ liệu và Phần/Bảng/Danh sách. Danh sách chọn mới 6 kiểu, Bảng chọn mới 7 kiểu ô; không coi mọi kiểu độc lập đều thêm được vào các khối.
- C có 14 bước từ tạo biểu mẫu đến Công bố và sao chép cấu trúc. Bài có 5 trường chung, 15 trường Danh sách, STT tự động và một vùng tệp chung. Tổng số mô hình là ô nhập; ba phương án Tình hình thực hiện chung là cấu hình mẫu.
- Điều kiện, hộp và kết quả Công bố được đọc từ FE/BE; chưa thực hiện trên bản thật của bài mới. Sao chép tạo bản riêng ở Bản nháp v1, giữ cấu trúc và không sửa nguồn; cơ chế xin quyền ngoài phạm vi.
- Bài thử chạy trong bộ nhớ, dùng UI FE được đóng gói. Giới hạn offline đặt ở đầu bài; mô phỏng Công bố chỉ là trạng thái mẫu, không xuất bản lên phần mềm.
- Lệnh thông thường là npm run build, npm run preview và npm run check trong D:/Job/CA/tdtd-guide. Preview: http://127.0.0.1:5187/#bieu-mau-dong. Link cũ ở cổng 5191 chuyển về chương chung. Không cần npm install hoặc build sản phẩm.
- Bản Markdown tại ../tdtd/docs/training/tap-huan-3-gio/02_BIEU_MAU_VA_DANH_MUC.md đồng bộ nội dung công khai. HTML cũ và nguồn sinh tiếp tục chuyển hướng về guide chung. README, prompt, handoff và bằng chứng không đưa vào bản public.

Đối chiếu nguồn tại qa/FORM-SOURCE-V07.md. Handoff clone 06/10 xác nhận riêng sao chép, thêm cấu trúc, lưu và Nhập thử bản mới; không chứng minh Công bố, nhập–nộp hoặc tổng hợp. QA browser, ảnh và bản in của v0.7 phải ghi bằng chứng mới trong qa/FORM-GUIDE-V07-HANDOFF.md; kết quả v0.6 vẫn là lịch sử.
## Thu gọn chương v0.7.1 — 06/10/2026

Mỗi chương có nút Thu gọn/Mở rộng ở tiêu đề và mục lục; hai vị trí đồng bộ. Liên kết mục lục, hỏi–đáp và neo mở trực tiếp tự mở chương đích. Nội dung không bị xóa khi thu gọn; in toàn bộ vẫn đủ chương, in một mục vẫn giữ phạm vi mục đã chọn. Thanh cuộn mục lục nằm ở mép trái, chữ giữ hướng đọc bình thường. Trạng thái thu gọn chỉ giữ trong trang đang mở. Nguồn tương tác dùng chung: src/chapter-ui.js và src/chapter-ui.css.

## Chụp lại màn quản trị v0.7.2 — 06/10/2026

Ảnh 20–23 là ảnh thật bằng pv01 tại localhost:5173 (read-only), thay ảnh màn Đơn vị/Người dùng/menu nhập cũ trong bài và trình phát. Nhãn tải Người dùng có danh mục XLSX, CSV cần XLSX tra mã; ảnh JPEG1280×840 có chú thích đúng nguồn. Markdown phần01 trong bộ tập huấn đồng bộ3 ảnh. QA-V072.md và admin-capture-v072.json ghi phạm vi và nguồn; BE mẫu mới vẫn chờ kiểm sau cập nhật, không restart trong lượt tài liệu.

## Menu và đánh số v0.7.3 — 06/10/2026

Menu chương dùng tam giác không khung. Ba mục lớn của chương Biểu mẫu động đổi A/B/C thành 10/11/12, tiếp sau bài09; bài con 11.1–11.13 và12.1–12.14. Menu và nội dung dùng chung số, giữ toàn bộ neo và dữ liệu bài thử. Nguồn cấu trúc JSON không đổi.

## Chiều rộng menu v0.7.4 — 06/10/2026

Menu rộng310px trên desktop và280px ở721–1100px, màn nhỏ vẫn100%. Nội dung căn theo cùng biến CSS. Thanh cuộn menu3px ở Chromium/IAB, track trong suốt, không nút mũi tên; fallbackthin ở trình duyệt khác. Chỉ vùng menu ngoài cuộn, ở mép trái.

## Xem qua Wi-Fi

Server hỗ trợ biến HOST để bind đúng IPv4 của Wi-Fi. Mặc định vẫn127.0.0.1; npm run preview không tự mở ra LAN. Chỉ phục vụ index.html ở / và /index.html, không chia sẻ các thư mục qa/content/tools. Ví dụ PowerShell: đặt $env:HOST bằng IP Wi-Fi hiện tại rồi chạy npm run preview; PORT tùy chọn, mặc định5187. Điện thoại cần cùng mạng Wi-Fi.

## In chương và toàn bộ v0.7.5 — 06/10/2026

Cả bốn chương có In chương này, dùng cùng cơ chế chọn chương. Nút đầu trang là In toàn bộ. In mở đủ nội dung đang thu gọn bằng CSS, không thay trạng thái đọc; kết thúc hoặc hủy in xóa lựa chọn. In bài riêng giữ phạm vi cũ. Wi-Fi server đọc index.html mới mỗi lần truy cập, không cần restart.

Bằng chứng kiểm in v0.7.5: [qa/QA-V075.md](qa/QA-V075.md).

## Tra cứu kiểu dữ liệu v0.7.6 — 06/10/2026

Bỏ hàng chip bên dưới mục11 Tra cứu kiểu dữ liệu và khối thành phần. Điều hướng13 mục qua menu bên trái; giữ nội dung, số mục và neo. Mục12 giữ các liên kết bước thực hành. Sửa renderer, build và kiểm menu/neo trên trình duyệt.

## Mẫu 2 / M01 v0.8.4 — 07/10/2026

Cùng index/menu chươngV có11 mục chức năng15.x và7 chặng M01 16.x;9 câu mới thuộc nhóm Giao việc và báo cáo. Mở http://127.0.0.1:5187/#mau-2-tra-cuu hoặc #mau-2-thuc-hanh. JSON public ở content/mau2-guide.json, renderer tools/render-mau2-guide.mjs; build/check theo lệnh hiện có, không cần build sản phẩm.

Bài này tới đọc báo cáo sau lượt nộp ban đầu. Mẫu04 đã được duyệt thủ công trong handoff cập nhật; public phân biệt lịch sử và trạng thái đãduyệt/chỉxem. Không coi Mẫu2 hoàn chỉnh hoặc hướng dẫn tổng hợp hai tầng. Mẫu1/trìnhxemv083 giữ nguyên. [Handoff](qa/MAU2-M01-V084-HANDOFF.md), [QA](qa/QA-V084.md). Lịch180phút vẫn đềxuất, Tổnghợp50phút/nghỉ10phút.

## Bàn giao M02 v0.8.6 — 07/10/2026

Phạm vi mới được yêu cầu trực tiếp thay điểm dừng M02 của v0.8.4. Chỉ sửa tài liệu; không thao tác sản phẩm, tài khoản, API/DB hoặc dữ liệu UAT. Nguồn chung content/mau2-guide.json sinh cả content/mau2-guide.md và index.html bằng tools/render-mau2-guide.mjs trong build. Không sửa hai tệp sinh bằng tay.

37 mục tra cứu Mẫu 2, 11 chặng M01, 12 chặng M02, 23 câu hỏi Mẫu 2; toàn guide 119 thẻ và 90 câu hỏi. Tám ảnh M02 nguyên bản bổ sung vào 33 ảnh hiện có; manifest qa/mau2-images-v086.json. Có nút Xem ảnh gốc trong hộp phóng ảnh để đọc trên màn nhỏ.

Hồ sơ: [handoff](qa/MAU2-M02-V086-HANDOFF.md), [QA](qa/QA-V086.md), [bảng ảnh và yêu cầu bổ sung](qa/MAU2-M02-V086-IMAGES.md). Nội dung đã nối trọn chặng M02 được chứng minh; bộ ảnh thao tác chưa đầy đủ. Không gọi toàn Mẫu 2 đã nghiệm thu.

Trong môi trường lượt này npm không có trên PATH; đã chạy trực tiếp các lệnh Node tương đương scripts build/check/check:dom của package.json. Không cài package, không build lại artifact FE. Chương trình 180 phút còn chờ rà, giữ Tổng hợp 50 phút và nghỉ 10 phút.

## Bản hiện hành 0.8.7

Mở [index.html](index.html#lo-trinh-180-phut) để chọn hai luồng. Demo xử lý báo cáo Mẫu 1 giữ nguyên; bài [Số hóa hồ sơ](content/digitization-guide.md) dùng bốn trường Số, cây 1–2–4, duyệt thủ công, SUM/AVG và lọc ngày. [Mẫu 2](content/mau2-guide.md) có 9 nhóm tra cứu, 5 nhóm M01, 6 nhóm M02; M03/M04 chỉ biến thể thu gọn. Không có simulator mới.

[Giáo án giảng viên](qa/GIAO-AN-V087.md) và [bản in gọn](output/pdf/giao-an-v087.pdf): 180 phút, Tổng hợp 50 phút, nghỉ 20 phút, chờ duyệt theo yêu cầu mới nhất. Lịch 10 phút nghỉ ở các mục cũ là lịch sử.

Sinh HTML/Markdown bằng node tools/build.mjs. Kiểm thêm node tools/check-training-guide.mjs; lệnh package check đã bao gồm ca này. Máy hiện tại có Node nhưng không có npm, đã chạy đúng các lệnh Node tương đương, không cài dependency.

[Bàn giao](qa/TRAINING-V087-HANDOFF.md), [QA](qa/QA-V087.md), [ảnh theo bước](qa/TRAINING-V087-IMAGES.md), [ảnh/nhãn cần bổ sung](qa/training-v087/IMAGE-GAPS.md). Chưa đủ ảnh và chưa chạy lại bản deploy sản phẩm. Giữ các file QA cũ để tra lịch sử.
