# Quy ước cho agent — tdtd-guide

## Mục đích
Đây là dự án tài liệu độc lập và là code mẫu cho các chương tiếp theo.
Dự án sản phẩm tham chiếu: ../tdtd. Không được suy ra quyền sửa sản phẩm từ quyền sửa hướng dẫn.

## Đọc theo thứ tự
1. README.md
2. REQUIREMENTS.md
3. PLAN.md
4. content/layout.json, content/01-tai-khoan-mu.json; khi sửa phần IV đọc content/dynamic-form-guide.json
5. qa/SOURCE-MAP.md và qa/UAT-MATRIX.md

## Ranh giới
- Chỉ sửa file trong dự án này và allowlist của việc được giao.
- Không sửa FE/BE, reset/seed DB, dùng token thật hoặc lưu dữ liệu thật để chụp hình.
- Không chạy lại toàn bộ UAT sản phẩm nếu người dùng mới giao việc biên soạn.
- Không tự xuất bản/tích hợp route vào app. Đọc source sản phẩm được phép để đối chiếu.
- Giữ thay đổi của agent khác; kiểm trạng thái trước khi làm. Dự án này chưa mặc định có Git.
- Không sao chép mặc định mật khẩu vào ví dụ/ảnh. Dùng dữ liệu hư cấu có nhãn mẫu.
- Không khẳng định một tính năng tồn tại chỉ vì có component; phải đọc handler/route đang dùng.
- Quyết định trực tiếp của Yud ưu tiên hơn tài liệu cũ. Điểm nghiệp vụ chưa rõ ghi vấn đề, không tự chốt.

## Cấu trúc bài chuẩn
Dùng content/lesson.template.json.
Bài bắt buộc có id, title, summary, audience, minutes, image, prereq, steps, result, note, sources.
Mỗi bước: title, text, image; focus là [x,y,width,height] theo % ảnh, không bắt buộc.
imagePhong thay ảnh khi nhánh phòng khác. Dữ liệu nghiệp vụ chỉ là ví dụ cố định.
Nguồn và UAT nằm trong qa; không nhét thông tin implementation vào luồng đọc của người dùng.

## Quy trình sửa
1. Nhận phạm vi, liệt kê file sở hữu.
2. Đọc màn FE và quy tắc BE thực tế, ghi nguồn/phiên bản.
3. Viết bài bằng tiếng Việt; dùng đúng nhãn và thứ tự thao tác.
4. Tạo ảnh theo nguồn hiện hành, ghi mẫu và che thông tin bí mật.
5. node tools/build.mjs; node tools/check.mjs.
6. Kiểm desktop/mobile, tìm kiếm, hai nhánh, player và bản in.
7. Cập nhật QA-REPORT, UAT-MATRIX nếu có bằng chứng mới.
8. Bàn giao đường dẫn index.html, thay đổi, kiểm tra đã làm và khoảng trống còn lại.

## Quy tắc bằng chứng
SOURCE_CHECKED / FE_FIXTURE_CHECKED / GUIDE_QA_PASSED / LIVE_UAT_PENDING / LIVE_UAT_PASSED là các trạng thái khác nhau.
Chỉ đánh LIVE_UAT_PASSED khi có môi trường, tài khoản/role, thời điểm, thao tác và bằng chứng kết quả.
Không dùng giả lập trả 200 hoặc ảnh thành công để xác nhận BE/ACL/DB.
Nếu nguồn thay đổi, cập nhật hash bằng cách tái kiểm/chụp; không thay hash để che sai lệch.

## Kiến trúc
- content/: nội dung có cấu trúc; assets/: hình.
- src/: CSS/JS dùng chung và module riêng của từng phần; tools/build.mjs sinh một index.html tự chứa.
- Không thêm framework/dependency khi không có nhu cầu cụ thể.
- Không sửa trực tiếp index.html. Sửa source rồi build.
- Không làm một app nghiệp vụ thứ hai trong tài liệu.
- tools/capture-fe.mjs dùng dependency sẵn có của source tham chiếu; build/đọc HTML không phụ thuộc source đó.

## Tự kiểm trước bàn giao
- Tên nút khớp nguồn; đúng phạm vi tài khoản quản trị; không lẫn admin hệ thống.
- Link/ảnh đủ; tìm kiếm có dấu/không dấu; hash link hoạt động.
- Player không tự chạy, dừng khi đóng/ẩn tab, thoát bằng Escape.
- Bản in đủ nội dung cả khi đang lọc; in bài riêng không lẫn bài khác.
- Không lỗi console từ tài liệu, không tràn mobile, không có yêu cầu network ngoài.
## Mẫu tương tác v0.2.0
Theo yêu cầu mới của Yud, cho phép mocktest nhỏ cho đăng nhập/tạo đơn vị/cấp tài khoản. Giới hạn “không làm app nghiệp vụ thứ hai” vẫn áp dụng; mô hình chỉ chạy trong bộ nhớ, dữ liệu hư cấu, không API hoặc storage.
- Đọc thêm content/questions.json, content/mocktest.json, qa/QA-V02.md.
- Q&A phải là nội dung duyệt sẵn + alias; không tích hợp AI, không tự sinh đáp án.
- Phân biệt ảnh component FE thực trong fixture với HTML tương tác được viết riêng để dạy thao tác.
- Giữ pure model, renderer, data tách nhau; mỗi thay đổi quy tắc phải có ca kiểm sai/đúng tương ứng.
- npm run check là kiểm mặc định. Browser QA mới phải ghi phiên bản; không dùng kết quả v0.1 để tuyên bố v0.2 đã chạy đầy đủ.

## Quyết định trực tiếp v0.3.0
- Giao diện tài liệu chia I. Giao diện đăng nhập và II. Giao diện quản trị (dành riêng tài khoản quản trị).
- Trong phần II có hai nhóm tài liệu: Quản trị tài khoản / Quản trị người dùng. Đây là nhóm bài, không phải đổi tên tab sản phẩm Đơn vị / Người dùng.
- Phân hệ Người dùng: cán bộ, chỉ huy, lãnh đạo. Phân hệ Quản trị: tài khoản quản trị. Không dùng viết tắt MU trong nội dung hiện ra cho người đọc.
- Bài thực hành login kết thúc sau đăng nhập; không tiếp tục tạo đơn vị/tài khoản. unit/user là bài riêng.
- Q&A có bộ chọn câu hỏi theo mẫu và tìm từ khóa; không gọi AI.
- content/layout.json sở hữu cấu trúc phần/nhóm; giữ ID bài cũ để liên kết không gãy.
- Ảnh menu mới có hồ sơ riêng qa/menu-capture-v03.json; không thay toàn bộ hash manifest cũ chỉ vì chụp lại một ảnh.
- Các kiểm tra DOM mô phỏng không xác nhận responsive/print thật; ghi riêng khoảng trống trình duyệt.

## Quyết định trực tiếp v0.4.0 — tải mẫu, nhập thử và bố cục FE
- Yud yêu cầu dùng lại code FE gốc cho tải mẫu và nhập thử; kiểm bố cục Người dùng ở source, sửa lỗi gốc rồi mới chụp.
- Ngoại lệ có phạm vi cho quy tắc không sửa FE: UsersPanel (bố cục bộ lọc/nút), ImportDataMenu (tham số style tùy chọn), ImportPreviewDialog (trích hộp kiểm tra dùng chung) và UnitsPanel (dùng hộp chung, giữ hành vi). Không cấp quyền sửa BE, seed/reset DB hay thao tác dữ liệu thật.
- Quy tắc A10/D06 cũ được bổ sung: runtime FE bài thử đóng gói trước vào assets/import-practice.html, nhúng offline qua iframe. Tái tạo artifact dùng dependency đã có của FE; build tài liệu thông thường vẫn dùng Node chuẩn và artifact đã lưu, không cần FE chạy.
- Giao diện menu/hộp kiểm tra là component FE thực; đọc tệp, kiểm dữ liệu mẫu và xác nhận trong bài thử là adapter riêng, chỉ dùng bộ nhớ. Không coi kiểm bài thử là UAT BE.
- content/import-guide.json sở hữu bảng cột, quy ước và ví dụ; tools/render-import-guide.mjs tạo nội dung đọc/in; src/import-guide.js chỉ mở/đóng runtime và đồng bộ cấp đã chọn.
- Ghi manifest mới cho FE import và ảnh Người dùng; không thay bằng chứng lịch sử để che source thay đổi.
- Khi source lỗi ở bước xác nhận import, không hứa hoàn tác mọi dòng; hướng dẫn tra cứu trước khi thử lại.

## Quyết định trực tiếp v0.5.0
- Nội dung cơ bản chỉ có Đăng nhập / Quản trị đơn vị / Quản trị tài khoản, thay cấu trúc v0.3. Giữ tên tab sản phẩm và ID bài; alias phần cũ vẫn điều hướng được.
- content/account-basics.json sở hữu phân cấp/ví dụ mã theo nơi công tác; renderer tĩnh riêng tools/render-account-basics.mjs. Không tính mã user từ username hay chức vụ.
- Tài khoản lãnh đạo/chỉ huy là tài khoản cá nhân; quyền quản trị phải được cấp riêng. Tên/mã từ seed chỉ là ví dụ kiểm thử, phải tra lại khi UAT.
- Bảng tra mã nhập tệp lấy cùng bộ dữ liệu với tệp tải thử; danh mục thực lấy từ XLSX tải mới. Positions không phải bảo đảm quyền chọn chức vụ cho mọi đơn vị.
- Ảnh nút/menu đặt đúng bước thao tác; ảnh nội dung XLSX phải có nguồn tệp và nhãn mẫu, không giả ảnh Microsoft Excel nếu dùng công cụ xem bảng.

## Bổ sung v0.5.1 — danh mục riêng
- Yud yêu cầu tệp tải riêng cho danh mục. assets/catalogues/ giữ hai tệp XLSX cho mỗi cấp, chỉ có sheet Units hoặc Positions; lấy đúng danh mục từ mẫu đang dùng.
- tools/build-catalogues.mjs là công cụ tái tạo tùy chọn dùng runtime bảng tính đóng gói. tools/build.mjs chỉ đọc bytes đã lưu và nhúng liên kết tải; normal build không thêm dependency.
- qa/catalogues-v05.json ghi nguồn/hashes/headers/rows; khi thay mẫu phải tái tạo danh mục và kiểm lại. Không sao chép Users/mật khẩu vào tệp danh mục.
- Công cụ render/kiểm XML khác với mở Excel thật; sự kiện tải IAB nếu không quan sát được phải ghi giới hạn riêng.

## Quyết định trực tiếp v0.6.0 — ghép một trang, một menu

Yud yêu cầu ghép hai guide; hỏi–đáp có thể tách nhóm nhưng cùng một trang. Quyết định này thay cách phục vụ chương biểu mẫu ở một trang/cổng riêng. Canonical: http://127.0.0.1:5187/#bieu-mau-dong.

- Một index.html, 4 phần, 15 bài; không thêm menu thứ hai hoặc iframe chứa cả guide biểu mẫu. Chín bài cơ bản và các ID cũ giữ nguyên; 6 bài biểu mẫu dùng tiền tố bieu-mau-. Namespace df- dành cho dialog, dữ liệu và điều khiển của phần biểu mẫu.
- content/dynamic-form-guide.json giữ nội dung public dành cho cán bộ, 15 trường, 6 bước và 10 câu hỏi. tools/render-dynamic-form-guide.mjs tạo HTML, menu và dữ liệu nhúng; src/dynamic-form-guide.js/.css chỉ phụ trách ảnh, trình phát, bài thử và in của phần IV. Không sửa tay index.html.
- Hỏi–đáp chỉ có một #hoi-dap: nhóm tai-khoan 33 câu, nhóm bieu-mau 10 câu. Bộ chọn câu và tìm từ khóa tra nhóm đang chọn; giữ điều khiển xóa riêng, aria-pressed trên nút nhóm, ngữ cảnh phường/xã–phòng của câu cũ. Không gọi AI hoặc dùng tìm kiếm để giấu bài.
- Runtime FE assets/form-practice.html đã đóng gói, nhúng offline qua iframe sandbox allow-scripts. Mở/đóng phải tạo/xóa phiên thử độc lập; không API/DB, token hoặc storage. Không mở quyền tải tệp lên hệ thống. assets/dynamic-form/ có ảnh; build nhúng ảnh inline.
- Guide công khai chỉ chứa lời hướng dẫn cho cán bộ và ghi chú dữ liệu mẫu cần thiết. README, prompt, handoff, quyết định cá nhân, nguồn/hash và sổ UAT là nội bộ, không được nhúng hoặc liên kết trong index.html.
- Danh sách chỉ có 15 trường nghiệp vụ, không thêm STT nhập tay. Tài liệu minh chứng là Đính kèm tệp ngoài Danh sách dùng chung. Không tự chốt đơn vị kinh phí, trường bắt buộc hoặc danh mục Đánh giá tiến độ. Bài dừng ở Bản nháp/Nhập thử; không hướng dẫn Công bố, giao việc, nộp–duyệt hoặc tổng hợp.
- Nguồn/handoff lịch sử tại ../tdtd/docs/training/tap-huan-3-gio/ giữ nguyên. Bản thật đã công bố và chênh lệch STT nhập tay không được sửa bởi việc ghép tài liệu; ảnh fixture đúng 15 trường không chứng minh bản thật đã được sửa.
- npm run build / preview / check dùng Node chuẩn và artifact đã lưu, không cần FE/BE chạy hoặc npm install. npm run check:dom là kiểm tùy chọn qua jsdom có sẵn của FE; không thay browser hoặc print.
- Preserve WIP và allowlist của từng agent. Khi nhiều agent cùng làm, phân file rõ; renderer/build và dữ liệu/module cùng phần phải thống nhất contract trước khi ghi.

## Cập nhật chương Biểu mẫu động v0.7 — thay điểm dừng cũ

Yêu cầu viết lại 06/10 thay F06 và giới hạn sáu bước/10 câu của v0.6. Chỉ thay chương IV và công cụ của chương; giữ chín bài cơ bản và menu chung.

- Chương IV có A hiểu cấu trúc, B tra cứu 10 kiểu dữ liệu và 3 khối, C làm biểu mẫu hoàn chỉnh tới Công bố và sao chép.
- SchemaVersion 2 dùng introduction, topics, walkthrough và qa. Không khóa sáu thẻ hoặc bỏ kiểm tra để chạy qua. Kiểm đủ loại được yêu cầu, tham chiếu và cấu trúc bài 21 trường.
- Bài mới gồm 5 trường chung, Danh sách 15 nghiệp vụ/STT tự động và vùng tệp chung; đối chiếu HANDOFF_UAT_CLONE_BIEU_MAU_2026_10_06.md.
- Công bố phải viết theo source/nhãn đã kiểm; không được công bố bản thật, sửa sản phẩm, API/DB/seed/build sản phẩm. Nhập thử và mô phỏng công bố chỉ ở bài mẫu, không biến thành bằng chứng UAT.
- Tên cần có khi thiết kế khác Bắt buộc khi nhập báo cáo. Hiển thị trên tổng quan là cờ hiển thị, không tạo công thức; cần kiểm consumer thực nếu chưa có bằng chứng.
- Ảnh cấu hình/nhập thử nằm đúng mục; thiếu ảnh ghi rõ. Nhãn mẫu không được gọi là ảnh lưu/readback/Công bố thành công trên hệ thống.
- Hỏi đáp, Markdown, HTML, iframe và bản in phải dùng cùng phạm vi tới Công bố. Không còn lời dừng trước Công bố trong nguồn public hiện hành.
- Ghi bằng chứng và việc còn cần kiểm ở qa/handoff; nội dung public không kể quyết định, tên người giao/duyệt hoặc quá trình triển khai.

## Menu và số mục v0.7.3 — 06/10/2026

Quyết định trực tiếp v0.7.3 thay nhãn A/B/C của bố cục v0.7: ba mục lớn Biểu mẫu động là10/11/12 tiếp sau09, mục con11.x/12.x. Renderer sở hữu số hiển thị và bỏ prefix bước trùng; JSON/ID giữ nguyên. Menu chương dùng tam giác không box, phía trước tên. Các kiểm collapse/anchor/Q&A/in phải giữ; xem qa/QA-V073.md.

## In chương và toàn bộ v0.7.5 — 06/10/2026

- In chương dùng data-print-chapter trên nút của cả bốn chương; src/chapter-ui.js sở hữu phạm vi chung và cleanup. Không thêm handler riêng cho IV hoặc thay đổi trạng thái thu gọn để in.
- In toàn bộ và in bài vẫn đi qua guide:before-print để dừng hai trình phát và bỏ phạm vi cũ. beforeprint xác định chương của .print-current khi in bài; afterprint xóa phạm vi.
- CSS print mở nội dung hidden, ẩn chương khác khi in chương, và giữ tiêu đề/giới thiệu cùng trang. Khi sửa in, kiểm cả chapter→all, chapter→lesson và cancel/afterprint; DOM không thay PDF thực.
- Xem qa/QA-V075.md cho bằng chứng v0.7.5. Nội dung QA/nguồn không được nhúng vào guide public.

## Quyết định trực tiếp v0.8.3 — bài báo cáo tự chạy

Yêu cầu 06/10 cho phép bài Mẫu 1 tự chạy sau khi cán bộ mở, thay quy tắc player không tự chạy riêng với bài này. Dùng trình xem mặc định, không yêu cầu nhập dữ liệu. Phải dừng khi đóng, ẩn tab hoặc in; mở lại giữ bước và tạm dừng. Trình xem giữ phạm vi dữ liệu mẫu trong bộ nhớ và không thay trạng thái UAT sản phẩm.

## Phạm vi tài liệu v0.8.4 — Mẫu 2 / M01

- Mẫu 2 dùng content/mau2-guide.json và tools/render-mau2-guide.mjs, ghép vào cùng chương V/menu/Q&A. Không tạo simulator Mẫu 2 hoặc sửa tay index.html.
- Tách chức năng15.x/bài nối16.x; vai PV01 → PX01 → px01_doi1. PA02 mới nhận cấp phòng. Không vẽ cây1→2→4 như đã chạy đủ.
- Đối chiếu main handoff và FOUR_FIXES mới nhất. Mốc bài lần này tới đọc report; mẫu04 hiện đã duyệt thủ công ở lượt tiếp tục, phải nói rõ lịch sử bước và trạng thái hiện tại. Không tự mở rộng sang hướng dẫn duyệt/tổng hợp hoặc UAT đầy đủ.
- Bảy ảnh thật/những bước thiếu ảnh ở qa/mau2-images-v084.json và QA-V084.md. Nhập thử khác report; ảnh lỗi chỉ trongQA. Giữ nhãnchưa xác nhận trongQA, không nghĩ tên nút.
- Giữ dữ liệu/artifact/autoplay Mẫu1v083 và bằng chứng lịch sử. Handoff: qa/MAU2-M01-V084-HANDOFF.md. Chỉ sửa guide; không sửa product/seed/dữ liệuUAT. Lịch180/50/10 vẫn đề xuất như trước.

## Viết theo chức năng — bổ sung v0.8.4

Caption chỉ ghi tên trường hoặc khối. Không ghép các cụm diễn giải số lượng như hai phần tử/một bản ghi vào tiêu đề. Ghi thao tác thêm/sửa/nhập và kết quả trong bước. Không thêm khung quote dài chỉ để nhắc lại nội dung hoặc chặng. Nếu nhãn UI khó hiểu, báo và ghi QA; không đổi lời hướng dẫn để che lỗi sản phẩm. Quyết định này áp dụng khi biên soạn các phần tiếp theo.

## Bổ sung M01 v0.8.5 — 07/10/2026

Yêu cầu trực tiếp “tự bổ sung ảnh tự bổ sung luồng” cho phép hoàn thiện phần M01 từ handoff mới: trả lại, sửa/nộp lại, duyệt thủ công, xem kết quả và đọc một nguồn tổng hợp còn Nháp. Điểm dừng chỉ mở/đọc trong v0.8.4 được thay trong phạm vi này. Không cho phép thay dữ liệu UAT hoặc coi Mẫu 2 đã đạt toàn bộ.

Chụp UI thật bằng tài khoản mẫu chỉ đọc; hộp trả lại/chọn đơn vị được mở rồi Hủy, không xác nhận. Không lưu mật khẩu trong source/ảnh/QA. Ảnh lịch sử trước duyệt và ảnh nút từ kỳ còn Nháp phải chú thích đúng thời điểm; không tái tạo trạng thái cũ bằng sửa dữ liệu.

Hồ sơ: qa/MAU2-M01-V085-HANDOFF.md, qa/QA-V085.md, qa/mau2-images-v085.json. Trong lượt kiểm có tác vụ khác cập nhật source lên v0.8.6/M02: giữ WIP đó. PDF v0.8.5 là lịch sử, không đại diện bản in v0.8.6; kiểm hiện tại có hashes riêng trong qa/mau2-v085/current-m01-v086.json. Mẫu 1/v0.8.3 và các verdict cũ giữ nguyên.

## Quyết định trực tiếp v0.8.6 — M02, xác nhận quá khứ và duyệt

Yêu cầu ngày 07/10 cho phép viết trọn chặng M02 đã chạy trên UI. Thay giới hạn không mở M02/duyệt của v0.8.4 trong đúng phạm vi chứng minh: PV01 giao PX01 → PX01 sao chép mẫu riêng/chỉnh kiểu/lưu/nhập thử/công bố → giao Đội 2 hằng ngày → nhận/mở kỳ 04 → nhập đủ/lưu/đọc lại/ngày hoàn thành/nộp → PX01 xem/xác nhận/duyệt → Đội 2 đọc Đã duyệt và ba kỳ cần nộp. Giữ M01 và Mẫu 1 hiện có, một menu và các neo.

Đọc FOUR_FIXES tới cập nhật cuối 03:41–03:54 và handoff M02. Hạn cấp đội 07:00 Việt Nam = 00:00 UTC thay yêu cầu 22:00 cũ. Không dùng lỗi đã sửa làm blocker hiện tại. Tự duyệt kinh phí ≥ 0 chỉ là cấu hình bài; báo cáo quá khứ cần xác nhận; hôm nay chưa có bằng chứng đạt.

Chỉ ghi tài liệu tại guide và chạy build/check guide. Không tạo task/agent; không sửa/chạy sản phẩm, API/DB/seed/reset/build/restart FE/BE, đổi UAT hoặc thao tác lại để dựng ảnh. Lượt này không cấp phiên sản phẩm riêng để chụp ảnh; yêu cầu ảnh bổ sung giữ trong QA. Nội dung public không có tên cá nhân, prompt/handoff/hash/ID kỹ thuật hay log PASS.

content/mau2-guide.json là nguồn; renderer sinh HTML/Markdown. Tám ảnh M02 đã mở xem và copy nguyên byte vào assets/mau2-v086. Không dùng ảnh “thiếu ngày” làm bằng chứng hiển thị lỗi khi ảnh không có lỗi. QA/manifest/bảng ảnh ghi mọi khoảng thiếu; chưa gọi bộ ảnh hoàn chỉnh.

Report PX01 còn Nháp, một nguồn M01 đã lưu. Phân biệt View tổng hợp nhiệm vụ với tổng hợp report cấp trên. PA02/M03/M04, ngày 05–07, tự duyệt hôm nay, đủ nguồn và truy nguồn hai tầng chờ chứng minh. Thông báo có sau tải lại và mở đúng phần việc; chưa chốt tự cập nhật/độ trễ. Giữ lịch 180/50/10 chờ rà. Handoff riêng: qa/MAU2-M02-V086-HANDOFF.md.

## Quyết định hiện hành v0.8.7 — tài liệu và giáo án

Quyết định trực tiếp 07/10 thay giới hạn biên soạn cũ: hai luồng demo xử lý báo cáo Mẫu 1 và M1 Số hóa hồ sơ. Bài bốn mô hình là tra cứu nâng cao; giữ M01/M02 ngày 04 đọc lại, M03/M04 chỉ biến thể có bằng chứng. Không dựng simulator, task hoặc agent mới.

- Chỉ sửa guide. Không FE/BE, API/DB, seed/reset, build/restart sản phẩm, thay dữ liệu UAT hoặc dựng lại ảnh bằng ghi thật. Build/check Node của guide được phép; các ngoại lệ sửa sản phẩm ở mục lịch sử không áp dụng lượt này.
- Nội dung nguồn: content/mau2-guide.json, digitization-guide.json, course-guide.json; renderer sinh HTML và hai Markdown. Không sửa tay index.html hoặc Markdown sinh tự động.
- Một menu, neo cũ giữ bằng alias, in dùng điều khiển chung. Không khóa kiểm tra vào số thẻ cũ; giữ kiểm dữ liệu, vai, trạng thái và artifact Mẫu 1.
- Lịch hiện hành chờ duyệt: 180 phút, 50 phút Tổng hợp, 20 phút nghỉ (thay 10 phút cũ). Giáo án ngắn và sổ tay cùng bảng thời lượng.
- M1 Số hóa dùng bốn trường Số, SUM/AVG/GET, lịch một lần/tháng và duyệt thủ công; khác M01 Kho hồ sơ. Bốn nguồn duyệt, một Nháp, một chờ. Ca lọc độc lập và Dashboard/hạn mới còn cần kiểm.
- Ảnh nguồn giữ nguyên bytes; mỗi ảnh có vai, màn, giới hạn. Ảnh mindmap cũ sai hạn không dùng công khai. Nhãn/ảnh thiếu ghi QA, không tự tạo tên nút hoặc khẳng định hoàn chỉnh.
- QA guide không đổi verdict sản phẩm. Xem qa/TRAINING-V087-HANDOFF.md, QA-V087.md, TRAINING-V087-IMAGES.md và training-v087/IMAGE-GAPS.md.
