# Kế hoạch — Sổ tay HTML TD-TD
Ngày lập ban đầu: 05/10/2026 · Baseline hiện tại: 0.7.0 (06/10/2026). Các mục tiến độ phiên bản cũ là lịch sử.

## Quyết định của Yud
- Làm dần hướng dẫn HTML song song với luồng kiểm thử UAT lại.
- Bắt đầu đăng nhập và quản trị tài khoản cho MU cấp phường/xã, phòng.
- MU tạo đơn vị con và tài khoản trong phạm vi mình. Danh mục chức vụ/đơn vị cấp hệ thống do admin phụ trách.
- Tách dự án khỏi TD-TD. Dự án này đặt tại D:/Job/CA/tdtd-guide, ngang cấp D:/Job/CA/tdtd.
- Phần đầu là code mẫu và chuẩn nội dung cho các agent viết tiếp.

## Mục tiêu của đợt đầu
Một chương hoàn chỉnh gồm đọc hướng dẫn, ảnh giao diện, xem minh họa từng bước và in A4.
Đồng thời bàn giao requirements, quy ước agent, nguồn nội dung và sổ kiểm chứng.

## Các giai đoạn
| Giai đoạn | Công việc | Đầu ra | Điều kiện hoàn thành |
|---|---|---|---|
| P0 — Chốt phạm vi | Rà FE và quy tắc BE liên quan; tách project; ghi nguồn | REQUIREMENTS.md, AGENTS.md, hồ sơ nguồn | Không đưa quyền admin hệ thống vào luồng MU |
| P1 — Mẫu chương | Soạn 9 bài; tạo ảnh từ FE với dữ liệu mẫu; thống nhất cấu trúc | content/01-tai-khoan-mu.json, assets/ | Mỗi bài có điều kiện, bước, kết quả, lưu ý, nguồn |
| P2 — HTML dùng được | Mục lục, tìm kiếm, chọn cấp, ảnh phóng to, xem từng bước/tự chạy, in | index.html và mã nguồn tái tạo | Mở trực tiếp; không cần backend/CDN/đăng nhập |
| P3 — Kiểm tài liệu | Kiểm tương tác desktop/mobile, liên kết, ảnh, bản in | qa/, báo cáo QA | Lỗi tài liệu được sửa; phân biệt với lỗi sản phẩm |
| P4 — Đối chiếu UAT thật | Chạy lại các ca MU xã/phường/phòng; ghi sai lệch trước/sau | qa/UAT-MATRIX.md cập nhật bằng bằng chứng | Mỗi ca có môi trường, vai trò, thao tác, kết quả thực, bằng chứng |
| P5 — Nhân rộng và tích hợp | Agent thêm chương cùng khuôn; tích hợp lối mở hướng dẫn khi được giao | Chương tiếp theo / thay đổi app riêng | Không đổi renderer để thêm bài thông thường; không tự sửa sản phẩm |

P0–P3 là phạm vi xây bản mẫu của lượt này. P4 theo luồng UAT riêng, không đánh dấu hoàn thành từ ảnh mẫu.
P5 là kế hoạch tiếp theo; chưa tự triển khai vào app hoặc thêm chương ngoài phạm vi.

## Cách làm song song với UAT
1. Lấy kết quả đọc source để viết bản hướng dẫn ban đầu.
2. Mỗi bài trỏ đến ca UAT theo ID ổn định, không ghi “đã UAT” chung chung.
3. Khi UAT phát hiện khác biệt: ghi hành vi đang có, hành vi mong muốn, ảnh hưởng tài liệu và phần chưa chốt.
4. Nếu nhãn/bố cục đổi: cập nhật nội dung và ảnh, kiểm bản in lại.
5. Nếu quyền/nghiệp vụ đổi: chờ quyết định nghiệp vụ rõ ràng, không dùng mô phỏng để tự đặt quy tắc.
6. Kết quả UAT của sản phẩm và QA của tài liệu có sổ riêng.

## Cách phân việc cho agent sau
- Một agent nhận một chương hoặc một tập bài có ID cụ thể.
- Đọc AGENTS.md, REQUIREMENTS.md, chương mẫu và hồ sơ kiểm chứng trước khi sửa.
- Ghi allowlist và không sửa ngoài phần được giao.
- Tránh cùng sửa src/renderer hoặc tools/build trong khi agent khác đang dùng.
- Người tổng hợp nhận: nội dung, assets, nguồn, ca UAT, bản HTML và báo cáo kiểm tra.
- Chỉ chia việc khi giúp hoàn thành phạm vi được giao; ghi file sở hữu rõ và tránh sửa chồng WIP.

## Bàn giao
- Người dùng: index.html, mở được offline hoặc phục vụ trên web.
- Agent: README.md, AGENTS.md, REQUIREMENTS.md, PLAN.md, content/, src/, tools/.
- Kiểm chứng: qa/SOURCE-MAP.md, qa/UAT-MATRIX.md, qa/capture-result.json, qa/QA-REPORT.md.
- Bản HTML là sản phẩm sinh từ source. Khi sửa lâu dài phải sửa content/src rồi build lại.

## Tiến độ bản mẫu 0.1.0
P0–P3 đã hoàn thành cho chương 01: 9 bài, 19 ảnh, 16 nhóm QA trình duyệt PASS; đã rà bản in A4. P4 còn LIVE_UAT_PENDING theo ma trận; P5 chưa triển khai.
## Kế hoạch nâng mẫu 0.2.0 — đã thực hiện
1. Đối chiếu quy tắc cấp đơn vị và danh mục chức vụ mẫu với source hiện hành.
2. Tách dữ liệu hỏi–đáp và dữ liệu bài thử khỏi bộ hiển thị; giữ 9 bài đã được đánh giá đạt.
3. Thêm xếp hạng câu hỏi cố định, alias tiếng Việt và trạng thái chưa có đáp án.
4. Dựng mô hình dữ liệu thử và form tương tác cho 3 bài × 2 cấp, có hướng dẫn theo trạng thái và tự chạy theo yêu cầu.
5. Kiểm logic bằng Node, sau đó thao tác trực tiếp trên trình duyệt: xã làm thủ công, phòng tự chạy, lỗi nhập liệu/chức vụ và màn nhỏ.
6. Ghi kết quả tại qa/QA-V02.md. LIVE_UAT_PENDING giữ nguyên; các chương chưa có mocktest vẫn có ảnh từng bước.

### Khuôn để agent viết tiếp
- Thêm câu hỏi/alias/lesson/practice vào content/questions.json; không nhét đáp án vào renderer.
- Chỉnh bộ mẫu trong content/mocktest.json; nếu đổi quy tắc, đối chiếu FE/BE và ghi nguồn trước.
- Logic mẫu thuần ở src/mock-model.js, tra cứu ở src/lookup-model.js; giao diện riêng ở mock-ui.js/qa-ui.js.
- Thêm kịch bản vào model, UI, bài kiểm tra và requirements cùng lúc; không mở rộng mocktest thành app quản trị thật.
- Build index.html; chạy npm run check; kiểm UI thực và cập nhật báo cáo có ngày/phiên bản.

## Điều chỉnh 0.3.0 theo phản hồi Yud
1. Việt hóa cách gọi loại tài khoản và nêu rõ Người dùng / Quản trị.
2. Tổ chức hai phần lớn qua content/layout.json; giữ chín bài và liên kết cũ.
3. Q&A có danh sách câu hỏi cố định và tìm theo từ khóa.
4. Đổi bài all thành login độc lập, có chọn Người dùng / Quản trị; tạo đơn vị và cấp tài khoản là hai bài riêng.
5. Chụp lại menu FE, chọn đúng dòng Quản trị tài khoản; sửa tọa độ đánh dấu theo ảnh mới.
6. Kiểm schema, logic và tích hợp DOM; ghi QA-V03. Browser/print của bố cục mới cần kiểm trực tiếp nếu tab được truy cập.

### Quyết định cũ được thay thế
- 0.2: bài thực hành đi từ đăng nhập đến cấp tài khoản → 0.3: bài đăng nhập riêng; bỏ mô hình toàn luồng.
- 0.2: tra cứu xếp hạng câu hỏi → 0.3: thêm bộ chọn câu hỏi và ô từ khóa.
- 0.1/0.2: dùng MU trong prose → 0.3: dùng tài khoản quản trị, phân biệt với người dùng.
- Hai nhóm mới là cách tổ chức tài liệu; nhãn tab FE vẫn Đơn vị / Người dùng; không thêm chức năng cấp quyền quản trị.

## Điều chỉnh 0.4.0 — đã triển khai
1. Đối chiếu template và parser BE; soạn bảng cột, quy ước, ví dụ theo cấp.
2. Trích hộp kết quả nhập thành component chung trong FE, giữ nguyên điều kiện xác nhận và các callback; dùng cùng component trong bài thử offline.
3. Chuẩn bị tệp XLSX/CSV hợp lệ và có lỗi; adapter chỉ đọc/kiểm/tạo dữ liệu mẫu trong bộ nhớ.
4. Kiểm bố cục Người dùng gốc, sửa bộ lọc/nút đã đo được lệch; chụp lại sau sửa.
5. Nhúng artifact FE đã đóng gói vào HTML, kiểm mẫu/tệp/guard/hai cấp và nội dung in.
6. Ghi QA-V04, manifest mới và các khoảng trống UAT. Không biến source check hoặc adapter thành LIVE_UAT_PASSED.

Kết quả v0.4: code/nội dung/ảnh và các kiểm source/model/DOM, browser mở riêng đã thực hiện. Bản nhúng srcdoc đã kiểm hiển thị và đồng bộ cấp. Giới hạn sự kiện tải/chọn tệp củaIAB và phân trangPDF ghi tại qa/QA-V04.md; UAT sản phẩm thật không tự đánh dấuPASS.

## Điều chỉnh 0.5.0
1. Tách ba phần cơ bản theo quyết định mới; thêm giải thích tài khoản quản trị/cá nhân/chỉ huy/lãnh đạo.
2. Đối chiếu gói kiểm thử pv01/cap_hacthanh; thêm cây và mã theo đơn vị công tác được chọn.
3. Bổ sung bảng tra mã nhập tệp, ảnh menu đúng bước và ảnh nội dung XLSX; không lấy danh mục mẫu thay cho danh mục triển khai.
4. Build và kiểm tài liệu; bàn giao checklist ngắn UAT. Đối chiếu source/seed không xác nhận DB hay quyền runtime.

## Bổ sung 0.5.1
Tách Units và Positions của XLSX mẫu thành hai tệp tải riêng cho mỗi cấp; thêm nút ở đầu bài và mục lục. Giữ dữ liệu nguồn, mã dạng chuỗi, ghi provenance; nhúng tệp vào HTML tự chứa rồi kiểm bytes/DOM/browser. Không sửa sản phẩm hoặc DB.

## Điều chỉnh v0.6.0 — ghép guide đăng nhập/quản trị và biểu mẫu

Quyết định mới: một trang, một menu dùng chung; hỏi–đáp chia nhóm nhưng cùng bảng tra cứu. Mục tiêu phân phối là index.html gồm 4 phần, 15 bài; canonical http://127.0.0.1:5187/#bieu-mau-dong.

1. Giữ 9 bài cơ bản, ID neo, chọn cấp, danh mục tải riêng và các bài thử cũ. Đăng ký phần IV và 6 bài biểu mẫu với namespace riêng, không gói toàn trang cũ vào iframe.
2. Chép dữ liệu public và 9 ảnh fixture của chương biểu mẫu vào content/dynamic-form-guide.json và assets/dynamic-form/. Giữ nguồn/handoff/ảnh sản phẩm cũ tại docs/training/tap-huan-3-gio/ làm lịch sử.
3. Dùng tools/render-dynamic-form-guide.mjs để trả nội dung/menu/QA cho build; nhúng ảnh inline và assets/form-practice.html như artifact offline, giữ các phiên thử độc lập.
4. Đổi bảng tra cứu thành hai nhóm 33 + 10 câu. Tìm kiếm chỉ theo nhóm đang chọn; không ẩn bài. Giữ ngữ cảnh và nút bài thử cũ, nối câu biểu mẫu với bài thực hành cùng phần.
5. Kiểm normal build/check bằng Node chuẩn; kiểm DOM mô phỏng tùy chọn nếu FE tham chiếu có jsdom. Tiếp đó kiểm trực tiếp menu/hash, chuyển nhóm/câu/từ khóa, ảnh/player/dialog/iframe, phần cơ bản và phần IV trên desktop/khung hẹp; kiểm in toàn bộ/từng bài khi có bằng chứng.
6. Ghi kết quả của bản ghép riêng; không kế thừa browser PASS/print PASS của bản cũ. Bàn giao một liên kết chuẩn và một tệp index.html; không khẳng định UAT sản phẩm từ fixture.

Lượt ghép không sửa FE/BE, API/DB/seed hoặc biểu mẫu thật. Bản thật đã công bố vẫn giữ trạng thái và chênh lệch STT lịch sử; không tự xử lý trường thừa. Bài mẫu đúng 15 trường không phải bằng chứng bản thật đã đổi. Phạm vi bài dừng ở Bản nháp/Nhập thử; không mở phần Công bố/giao việc/nộp–duyệt/tổng hợp.

Phân việc: dữ liệu/module biểu mẫu, QA chung, build/kiểm và docs nội bộ có allowlist riêng. Khi đổi contract nhóm/lesson/formPractice hoặc namespace, báo người tổng hợp trước khi build. Mọi tiến độ PASS phải gắn bằng chứng của chính v0.6.

## Điều chỉnh v0.7.0 — viết lại chương Biểu mẫu động

Yêu cầu mới thay cấu trúc sáu bài v0.6 và điểm dừng Bản nháp. Cán bộ cần chọn kiểu, cấu hình, tạo biểu mẫu hoàn chỉnh, lưu, nhập thử rồi Công bố. Được phép viết hướng dẫn Công bố; không được công bố bản thật hoặc đổi dữ liệu sản phẩm.

1. Đối chiếu handoff clone 06/10 và handler canvas, validator FE–BE. Ghi SOURCE_CHECKED; giữ bằng chứng UAT clone/lưu/Nhập thử theo đúng phạm vi handoff.
2. Viết dữ liệu schemaVersion 2: A cấu trúc và hình đích, B: 13 mục tra cứu (10 kiểu + 3 khối), C: 14 bước hoàn chỉnh. Renderer và kiểm tra nhận cấu trúc mới; không xóa kiểm chỉ để qua.
3. Bài có hai Phần và 21 trường: 5 trường chung, 15 trường Danh sách,1 vùng tệp chung. Giữ STT tự động, Tổng số mô hình nhập tay và bộ phương án mẫu; không tự đặt đơn vị kinh phí hoặc yêu cầu bắt buộc nghiệp vụ.
4. Viết rõ Xác nhận/Lưu/Nhập thử/Công bố. Đường canvas dùng Công bố → Công bố phiên bản này? → Xác nhận. Sao chép chỉ tận dụng cấu trúc bản có quyền; không mở cơ chế xin quyền.
5. Bài thử và ảnh dùng component FE thực trong bộ nhớ, có nhãn mẫu. Chụp cạnh đúng thao tác; thiếu ảnh ghi Cần bổ sung ảnh. Không dựng ảnh thành công BE để lấp bước Công bố.
6. Đồng bộ JSON, Markdown, HTML sinh, hỏi đáp, bài thử và bản in. Giữ canonical 5187, redirect 5191, một menu và các ID cơ bản.
7. Kiểm Node/DOM trước; root kiểm browser, ảnh, liên kết, QA, player, iframe và CSS in. Chỉ ghi PASS đã có bằng chứng; phân trangPDF thực và UAT sản phẩm ghi riêng.
8. Bàn giao HTML/Markdown và khoảng trống trong qa/FORM-GUIDE-V07-HANDOFF.md. Không sửa các chương khác hoặc lịch 180 phút; Tổng hợp 50 phút và nghỉ 10 phút giữ nguyên.

Các quyết định và bằng chứng v0.1–v0.6 phía trên là lịch sử. Các mô tả sáu bước/10 câu hoặc dừng trước Công bố trong mục v0.6 không còn mô tả baseline hiện tại. Không dùng source hoặc ảnh fixture làm nghiệm thu Công bố thật.
## Điều hướng chương v0.7.1 — 06/10/2026

- [x] Tổ chức mỗi chương thành tiêu đề và nội dung, giữ neo và bài cũ.
- [x] Bổ sung nút thu gọn/mở rộng chung cho tiêu đề và mục lục; mở lại chương khi đi theo liên kết.
- [x] Chuyển thanh cuộn mục lục sang trái, kiểm màn lớn/màn nhỏ và chế độ in.
- [x] Sinh HTML, kiểm 19 nhóm DOM và lưu bằng chứng tại qa/QA-V071.md.

## Chụp quản trị v0.7.2 — 06/10/2026

- [x] Đọc chốt cuối handoff, giữ WIP và tách source/runtime.
- [x] Chụp pv01, lưu ảnh thật cùng theme/kích thước, không ghi dữ liệu.
- [x] Đồng bộ ảnh/bước/nhãn/focus/chú thích trong HTML và Markdown.
- [x] Kiểm browser, DOM và bản in riêng; ghi QA/UAT pending cho mẫu BE mới.

## Menu và đánh số v0.7.3 — 06/10/2026

1. Thay icon và bỏ box trên nút menu chương; giữ aria và đồng bộ mở/đóng.
2. Renderer sinh số10/11/12 cho ba mục lớn,11.x/12.x cho mục con; bỏ số thứ tự cũ trùng trong tiêu đề hiển thị, giữ ID.
3. Build/check/DOM rồi kiểm trực tiếp desktop/mobile, neo từ hỏi–đáp và chế độ in. Ghi bằng chứng tại qa/QA-V073.md.

## Chiều rộng menu v0.7.4 — 06/10/2026

Nới chiều rộng bằng biến --guide-sidebar-width, chỉnh page/skiplink/breakpoint/print theo cùng biến. Thu nhỏ scrollbar ngoài, giữ RTL scroller/LTR chữ. Build và kiểm browser1280/900/390, nhóm con dài và print media; không sửa nội dung hoặc sản phẩm.

## In chương và toàn bộ v0.7.5 — 06/10/2026

1. Bổ sung nút in bốn chương và đổi nhãn In toàn bộ; giữ ID/neo.
2. Dùng sharedchapter-ui để chọn/reset chương; bỏ handler in chương riêng ở phần biểu mẫu, giữ in từng bài.
3. Kiểm20 nhóm trước và ca in chương mới; xuất PDF QA cho4 chương/toàn bộ bằng browser, đối chiếu phạm vi và xem render; kiểm mobile/desktop và cleanup. Không tự in ra máy in hoặc sửa sản phẩm.

## Tra cứu kiểu dữ liệu v0.7.6 — 06/10/2026

Bỏ hàng chip bên dưới mục11 Tra cứu kiểu dữ liệu và khối thành phần. Điều hướng13 mục qua menu bên trái; giữ nội dung, số mục và neo. Mục12 giữ các liên kết bước thực hành. Sửa renderer, build và kiểm menu/neo trên trình duyệt.

## Hoàn thành lượt mẫu v0.8.2
Đã tách pure model/fixture/adapter/runtime, đóng gói artifact FE offline, ghép nút vào mục14, kiểm model+DOM+browser desktop/mobile và PDF chươngV. Bằng chứng trong QA-V082.md. Chưa dùng kết quả guide để nghiệm thu product; blocker độc lập và các pending cũ giữ nguyên.

## v0.8.3 — chuyển bài mẫu sang trình xem

Đã nhận yêu cầu cho bài mẫu tự chạy. Sở hữu playback.mjs/playback.test.mjs, watch.tsx, entry của app.tsx, adapter.installScene, mở/đóng/visibility src/report-practice.js; renderer/build/check/package và QA liên quan. Giữ59thẻ/67Q&A/29ảnh cũ. Kiểm20cảnh, một timer, pause/seek/replay/close/visibility/print, 6/10→10/10 và8/8, desktop/mobile; không chạy UAT sản phẩm.

## Bàn giao Mẫu 2 / M01 v0.8.4

1. Đã tiếpnhận nguồn theo thứtự và đốichiếu FOUR_FIXES cập nhật03:01; không tự chạyUAT.
2. Đã viết11 chức năng/7chặng/9Q&A từ nguồn, giữ scope tới đọc; ghi rõ mẫu04 hiện đãduyệt thủcông ở lượt sau.
3. Đã xem/copy7ảnh, chúthích/khoanh/tài khoản/zoom, giữ thiếuảnh; không mở rộng M02–M04 hoặc simulator.
4. Đã build/check25DOM/browser/print và kiểmMẫu1 bounded; QA/ảnh/PDF/handoff trongqa/v084. Chưa in giấy hay chạy lại full20timer.
5. Tiếp theo cần bổ sung ảnh reportthật và các nhãn chưa chụp, kiểm các nhánh cònchờ bằng nhiệm vụ riêng. Lịch180phút vẫn đềxuất; Tổnghợp50/nghỉ10 không đổi.

## Bàn giao M02 v0.8.6 — 07/10/2026

1. Đã đọc nguồn yêu cầu, FOUR_FIXES tới cuối và M02; đối chiếu quyết định 07:00, lịch sử lỗi đã sửa và giới hạn hiện hành.
2. Đã giữ 17 chức năng/11 chặng M01, thêm 20 chức năng/12 chặng M02; sinh Markdown và HTML chung nguồn; giữ Mẫu 1 và neo cũ.
3. Đã xem/copy tám ảnh M02, ghi manifest và bảng ảnh theo bước. Còn thiếu ảnh tạo/đổi tên/kiểu/nhập thử/lưu đọc lại/công bố và một số màn report; không tái thao tác dữ liệu thật.
4. Đã kiểm build/check/DOM, browser menu/neo/Q&A/đổi vai/zoom/màn nhỏ, PDF bài/chương/toàn bộ; Mẫu 1 đi đủ 20 cảnh bằng Tiếp, bản thân artifact/dữ liệu giữ hashes.
5. Chưa có UAT sản phẩm mới, ảnh đầy đủ, kiểm tự cập nhật thông báo, ngày hôm nay/PA02/M03/M04/đủ nguồn/truy hai tầng. Chi tiết QA-V086 và handoff M02.

Lịch 180 phút vẫn chờ rà, giữ 50 phút Tổng hợp và 10 phút nghỉ. Không dùng kiểm tài liệu để chốt lịch hoặc nghiệm thu toàn bài 1 → 2 → 4.

## Tiến độ hiện hành v0.8.7 — 07/10/2026

Đã biên soạn bản review: 9 nhóm tra cứu chung; M01/M02 đọc lại; M03/M04 biến thể có bằng chứng; 7 nhóm thực hành Số hóa và bảng tự kiểm; giáo án 180/50/20 chờ duyệt. Tổng Mẫu 2 giảm 60→20 nhóm, 230→105 bước; M02 83→38 bước, bản in 28→16 trang. M1 Số hóa có 38 bước, khác M01.

Guide build/check/DOM/browser/print đã kiểm theo QA-V087.md. Phần cần bổ sung là ảnh đúng màn, ca lọc độc lập, Dashboard/hạn mới và lượt kiểm trên bản deploy. Không biến khoảng trống này thành kết quả đạt; không reset, dựng dữ liệu hoặc triển khai trong lượt tài liệu. QA cũ và WIP được giữ, ảnh mindmap sai hạn chưa dùng.
