# Ma trận kiểm lại UAT — phần cơ bản và biểu mẫu động
Ngày lập ban đầu: 05/10/2026; cập nhật chương v0.7.0 ngày 06/10/2026. Các ca UAT cơ bản hiện giữ LIVE_UAT_PENDING; checklist theo phiên bản tách kiểm tài liệu khỏi UAT thật.
Đây là checklist cho luồng UAT thật, không phải bản ghi đã thực thi.

Điền mỗi ca: môi trường/phiên bản, thời điểm, vai trò/tài khoản thử được phép, thao tác, kết quả thực, bằng chứng, người kiểm.
Không ghi mật khẩu/token vào biên bản.

| ID | Ca kiểm | Kết quả mong đợi | Bài liên quan |
|---|---|---|---|
| UAT-A01 | tài khoản quản trị phòng và tài khoản quản trị phường/xã đăng nhập hợp lệ; thiếu/sai mật khẩu | Vào được đúng tài khoản/đơn vị; lỗi rõ và không lộ mật khẩu | dang-nhap |
| UAT-A02 | Tự đổi mật khẩu; sai mật khẩu hiện tại; mới không khớp/ngoài độ dài | Chỉ giá trị hợp lệ đổi được; phiên cũ kết thúc; mới đăng nhập được | doi-mat-khau |
| UAT-A03 | Mở menu tài khoản quản trị, menu user thường, đăng xuất | tài khoản quản trị thấy Đơn vị/Người dùng; user thường không được quản trị; logout chấm dứt truy cập | dang-nhap |
| UAT-U01 | tài khoản quản trị xã và tài khoản quản trị phường tạo Tổ dưới chính đơn vị | Mã tự cấp, đúng cha/loại; đọc lại được và chọn tạo tài khoản được | tao-don-vi |
| UAT-U02 | tài khoản quản trị phòng tạo Đội; thử tạo Tổ hoặc con ngoài phạm vi | Đội trực thuộc hợp lệ; sai loại/sai scope bị từ chối ở BE | tao-don-vi |
| UAT-U03 | Thiếu tên/loại, trùng ký hiệu, đơn vị ảo, chọn Tổ làm cha | Lỗi đúng; Tổ không có cấp dưới; đơn vị ảo không nhận tài khoản trực tiếp | tao-don-vi |
| UAT-U04 | Sửa con; sửa chính root; ngừng dùng con có user; cây không còn user thường | Root không sửa; chặn cây có user thường; soft-delete đúng cây và tài khoản quản trị liên quan | quan-ly-don-vi |
| UAT-N01 | tài khoản quản trị tạo user ở mình/con; trùng username; thử ngoài nhánh | Tạo đúng phạm vi, tên duy nhất; BE chặn ngoài scope | tao-tai-khoan |
| UAT-N02 | Chọn xã/phường/phòng/Đội/Tổ, đổi đơn vị sau chọn chức vụ | Chức vụ đúng loại và xã/phường; Tổ không có Tổ phó; chức vụ cũ phải chọn lại | tao-tai-khoan |
| UAT-N03 | User mới đăng nhập; kiểm quyền quản trị | User mới dùng được; không tự nhận quyền tài khoản quản trị/hệ thống | tao-tai-khoan |
| UAT-N04 | Tìm/lọc/xóa lọc, sửa tên đăng nhập/họ tên/chức vụ | Readback đúng; không cho đổi unit qua hộp sửa; old/new username đúng sau cập nhật | sua-tai-khoan |
| UAT-N05 | tài khoản quản trị đặt lại mật khẩu user thuộc scope, sai scope, hủy xác nhận | Hủy không đổi; xác nhận đúng tài khoản; đăng nhập bằng giá trị mới được; ngoài scope bị chặn | dat-lai-mat-khau |
| UAT-N06 | Ngừng dùng user; hủy xác nhận; thử đăng nhập lại user ngừng dùng | Hủy giữ nguyên; xác nhận bỏ khỏi đang dùng; tài khoản ngừng dùng không login được | ngung-tai-khoan |
| UAT-I01 | Tải mẫu từ từng tab, import có lỗi, import hợp lệ, ngoài scope | Đúng mẫu; preview không ghi; lỗi chặn xác nhận; hợp lệ ghi đúng và tra được | nhap-danh-sach |
| UAT-I02 | Kiểm lại sau timeout/nhập lặp | Không tự nhập trùng khi chưa biết kết quả lần trước; lỗi/tra cứu rõ | nhap-danh-sach |

## Các điểm cần đặc biệt đối chiếu
1. Nhãn Email / Tên đăng nhập không tự chứng minh hỗ trợ đăng nhập bằng email.
2. Nút đặt lại mật khẩu thực dùng confirm mặc định. Component ResetPasswordDialog tồn tại nhưng handler hiện không mở.
3. Danh sách Người dùng hiện lọc đang dùng; không hướng dẫn một nút khôi phục chưa có.
4. Bài thử v0.4 đã kiểm tệp hư cấu bằng adapter; chưa kiểm import thực tại BE/ACL/DB. UAT phải dùng template tải từ môi trường được phép.
5. UI và source có thể đổi cùng luồng UAT khác; khi đổi cập nhật bài/ảnh/hash cùng nhau.

## Checklist ngắn giao agent UAT — v0.5.0
Chỉ kiểm ba phần cơ bản: Đăng nhập, Quản trị đơn vị, Quản trị tài khoản. Dùng môi trường và dữ liệu kiểm thử đã được giao; tên dưới đây đối chiếu gói mẫu, cần xác nhận chúng có trên môi trường đang chạy. Không reset/seed chỉ để khớp checklist.

1. Đăng nhập lần lượt bằng `pv01`, `cap_hacthanh`; thử thiếu/sai mật khẩu, đăng xuất rồi đăng nhập lại. Kiểm đúng tên và đơn vị.
2. Đăng nhập `pv01_truongphong`, `pv01_doi1_doitruong`, `pv01_doi1_canbo`, `cap_hacthanh_truong`, `cap_hacthanh_canbo`: tài khoản cá nhân/chỉ huy/lãnh đạo không tự có menu hoặc quyền quản trị. Thử truy cập trang/API quản trị phải bị chặn.
3. Với `pv01`, tạo một Đội thử dưới Phòng PV01; với `cap_hacthanh`, tạo một Tổ thử dưới phường Hạc Thành. Kiểm cây cha–con, loại đơn vị, mã hệ thống cấp; sai loại/ngoài nhánh bị chặn. Nếu kiểm tầng tiếp, `pv01_doi1` tạo Tổ dưới Đội 1; Tổ không có con.
4. Sửa đơn vị thử, hủy/ngừng dùng; chặn sửa/ngừng dùng đơn vị gốc và chặn ngừng dùng cây còn tài khoản cá nhân hoạt động. Chỉ thao tác với dữ liệu thử.
5. Tạo tài khoản mới ở phòng, Đội hoặc phường/Tổ thử. Trưởng phòng dùng mã phòng; đội trưởng/cán bộ công tác tại Đội dùng mã Đội; Tổ trưởng mới dùng mã Tổ vừa cấp. Đổi đơn vị phải chọn lại chức vụ; trùng tên/đơn vị ảo/ngoài phạm vi/chức vụ sai bị chặn.
6. Lọc, tìm, xóa lọc và sửa tài khoản thử. Kiểm tên/họ tên/chức vụ/mã đơn vị đọc lại đúng; không đổi đơn vị ở hộp sửa. Kiểm bộ lọc và các nút Người dùng không lệch ở desktop/mobile.
7. Trên tài khoản thử, hủy rồi xác nhận đặt lại mật khẩu, tự đổi mật khẩu, đăng nhập lại; ngừng dùng rồi thử đăng nhập phải bị chặn. Không ghi mật khẩu vào báo cáo.
8. Cả `pv01` và `cap_hacthanh`: mở nút Nhập dữ liệu, kiểm đủ ba mục. Người dùng có Tải mẫu và danh mục (XLSX); Đơn vị có Tải mẫu XLSX; hai tab đều có Tải mẫu CSV và Nhập dữ liệu (XLSX hoặc CSV). XLSX Người dùng có Users/Units/Positions; chép đúng code từ hai trang tra mã. CSV không có danh mục, tải thêm XLSX để tra. Không nhập lại username mẫu đã tồn tại.
9. Nhập tệp đúng/sai cho cả Đơn vị và Người dùng: kiểm chưa ghi, còn lỗi khóa xác nhận, hủy không ghi, sửa rồi đọc lại cùng tệp được; hợp lệ lưu đúng và tìm lại được. Timeout/lỗi xác nhận phải tra kết quả trước khi nhập lại.
10. Đối chiếu HTML: ba phần cơ bản, loại tài khoản, cây/mã đơn vị, Q&A theo mẫu/từ khóa, ảnh từng bước (nút/menu, Excel, điền dữ liệu), hai cấp và bản in. Ghi PASS/FAIL và ảnh sai lệch, không dùng bài thử offline để đánh PASS sản phẩm.

Mã trong gói mẫu: PV01 `100001002`; Đội 1 PV01 `100001002001`; phường Hạc Thành `100001001127`. Gói mẫu chưa có Tổ con ở phường; `cap_hacthanh_totruong` hiện vẫn gắn mã phường. Tạo Tổ mới rồi dùng mã mới cho ca cấp tài khoản Tổ trưởng.

Các ca chi tiết bên dưới giữ nguyên trạng thái LIVE_UAT_PENDING cho đến khi agent ghi bằng chứng.

## Mẫu ghi kết quả
- Case ID:
- Ngày giờ (Asia/Saigon):
- FE/BE version + WIP liên quan:
- Môi trường:
- Actor/role/scope (không mật khẩu):
- Dữ liệu thử được phép:
- Thao tác:
- Kết quả thực:
- Bằng chứng UI/API/DB:
- Sai lệch so với yêu cầu:
- Ảnh/bài hướng dẫn phải sửa:
- Verdict: PASS / FAIL / BLOCKED

## Bổ sung tài liệu 0.3.0
- Kiểm phân hệ Người dùng bằng tài khoản cán bộ/chỉ huy/lãnh đạo và phân hệ Quản trị bằng tài khoản quản trị.
- Chín bài nghiệp vụ và case ID giữ nguyên. Bài thực hành đăng nhập riêng không xác nhận các ca tạo đơn vị/tài khoản.
- Nhóm Quản trị tài khoản / Quản trị người dùng trong tài liệu không thay nhãn tab Đơn vị / Người dùng.

## Bổ sung UAT import 0.4.0 — tất cả LIVE_UAT_PENDING
| ID | Ca kiểm thật cần agent UAT tổng chạy | Tiêu chí đối chiếu |
|---|---|---|
| UAT-I03 | Mỗi cấp phường/xã, phòng tải XLSX/CSV từ cả hai tab | Header đúng thứ tự; XLSX có trang dữ liệu đầu và trang tra mã; số 0 đầu mã được giữ. Thay/xóa dòng ví dụ; sau BE cập nhật đối chiếu unitCode/positionCode trong XLSX và CSV với danh mục và quy tắc loại đơn vị |
| UAT-I04 | Đơn vị: quantity=1/2, parent đúng/sai/ngoài scope, loại TO/DOI, trùng externalKey/symbol, expectedCode đúng/sai | Preview không tạo; sai dòng báo đúng cột; quantity khác1 bị chặn; xác nhận hợp lệ tạo đúng cha/loại/mã và tra lại được |
| UAT-I05 | Người dùng: trùng username; thiếu bắt buộc; sai đơn vị/chức vụ; đơn vị ảo/ngừng dùng/ngoài scope | Preview đúng dòng/cột; xác nhận không có lỗi mới cho phép; đăng nhập/tài khoản mới đúng quyền, không tự được quản trị |
| UAT-I06 | Hủy preview, sửa rồi chọn lại cùng tệp; nhập lại sau thành công | Hủy không ghi; input đọc phiên bản tệp mới; trùng bị chặn và không tạo thêm |
| UAT-I07 | roles có ADMIN/SYSTEM_ADMIN/quyền quản lý | Kiểm lỗ hổng giữa preview và commit: source dryRun chỉ chặn ADMIN; dịch vụ tạo còn chặn các quyền quản trị khác. Không cho cấp quyền quản trị qua import của tài khoản quản trị đơn vị |
| UAT-I08 | Lỗi/timeout giữa nhiều dòng khi xác nhận | Ghi rõ dòng nào đã tạo hoặc chưa tạo, kiểm DB/API trước khi retry. Source tạo tuần tự; chưa có bằng chứng hoàn tác toàn bộ |

Bố cục FE Người dùng đã kiểm trong fixture ở1280/1440/390; đưa thay đổi vào luồng UAT giao diện thật. Không dùng các ca model/DOM của tài liệu để đánh PASS cho bảng này.
Các cột note hiện chưa được truyền vào lệnh tạo; hướng dẫn để trống. Đây là giới hạn source hiện tại, không phải dữ liệu đã xác nhận lưu.

## Kiểm tài liệu bổ sung 0.5.1 — danh mục tải riêng
- Tại đầu bài Nhập danh sách từ tệp hoặc liên kết Tải danh mục tra mã, chọn Phường/xã rồi Phòng. Mỗi cấp có hai nút tải riêng mã đơn vị/mã chức vụ.
- Tải đủ bốn XLSX, mở và đối chiếu với sheet Units/Positions trong XLSX mẫu cùng cấp. Mã đơn vị giữ số 0 đầu; tệp không có sheet Users, tài khoản hay mật khẩu.
- Dùng mã trong danh mục để điền hai dòng của bài nhập thử. Khi UAT sản phẩm, lấy danh mục hiện hành từ XLSX tải tại phần mềm; danh mục mẫu offline không xác nhận dữ liệu DB.
- Ghi riêng PASS/FAIL tải file, nội dung file, mở Excel và chọn cấp; không dùng byte-check của HTML để đánh PASS native download hoặc UAT sản phẩm.

## Checklist bản ghép v0.6.0 — QA tài liệu, chưa phải biên bản PASS

Mở http://127.0.0.1:5187/#bieu-mau-dong. Một index.html có 4 phần, 15 bài; 9 bài cơ bản và 6 bước biểu mẫu. Kết quả từng ca phải ghi kèm phiên bản/thời điểm/bằng chứng của bản ghép; không dùng browser PASS chương riêng hoặc phiên bản trước.

1. Menu chỉ có một bộ; cả bốn phần cùng trang. Chín neo cũ vẫn đến đúng bài; #bieu-mau-dong và sáu neo bieu-mau- đến đúng phần IV. Reload URL có hash vẫn thấy đúng nội dung.
2. Hỏi–đáp cùng #hoi-dap: nhóm đăng nhập/quản trị có 33 câu, nhóm biểu mẫu có 10 câu. Chọn nhóm chỉ hiện câu/từ khóa của nhóm đó; chọn câu, bỏ chọn, tìm có/không dấu, xóa tìm và không có kết quả hoạt động; không làm mất bài hướng dẫn.
3. Câu cũ vẫn đổi ngữ cảnh phường/xã–phòng và mở đúng bài thử tài khoản. Câu biểu mẫu mở đúng bước của bài thử biểu mẫu; không mở nhầm dialog của guide cũ.
4. Kiểm đủ 9 ảnh phần IV, zoom/ảnh bổ sung/player trước–sau/tự chạy/phát lại/Escape. Player không tự chạy khi mở, dừng khi đóng hoặc ẩn tab. Đóng trả focus cho nút mở.
5. Bài thực hành biểu mẫu dùng tài khoản mẫu thietke_bieumau_mau. Kiểm 15 trường, không có STT nhập tay, SĐT giữ 0 đầu, ngày theo mẫu, vùng minh chứng ngoài Danh sách; thêm phần tử đúng STT tự động. Reset/đóng–mở không dùng lại dữ liệu phiên cũ.
6. Chọn tệp/lưu nháp trong fixture không gửi mạng, không lưu storage hoặc dữ liệu sản phẩm. Thông báo nháp mẫu nêu dữ liệu chỉ trong phiên; không có khẳng định Đã đối chiếu dữ liệu đã lưu cho fixture.
7. Sau ghép, bài nhập tệp cũ vẫn mở, đồng bộ cấp, tải mẫu/danh mục đúng; hỏi–đáp/tài khoản/iframe biểu mẫu không ảnh hưởng lẫn nhau.
8. Kiểm desktop và khung hẹp: menu, nhóm QA/select/input, bảng, ảnh và dialog không gây tràn ngang toàn trang; dùng bàn phím chọn nhóm/câu và đóng dialog.
9. Kiểm in toàn guide và một bài: đủ 4 phần/15 bài hoặc đúng bài chọn, không in QA/iframe/nút/menu. Ghi riêng kiểm print CSS với PDF/A4 thực; chưa xem PDF không đánh PASS phân trang.
10. Rà cả nội dung visible và JSON nhúng: dùng cán bộ; không có tên cá nhân duyệt/giao, prompt, handoff hoặc đường dẫn nguồn máy riêng. Không nhúng các sổ bằng chứng nội bộ.

Các bước trên chỉ kiểm guide/fixture, không xác nhận BE/ACL/DB hoặc UAT sản phẩm. npm run check và kiểm DOM mô phỏng không thay kết quả trình duyệt, download native hay print PDF. Tại thời điểm bổ sung checklist này chưa ghi browser PASS mới cho v0.6.

## Phạm vi UAT biểu mẫu đã được quyết định trực tiếp

Yud đã cho phép viết tài liệu sau khi duyệt bước tạo, lưu và xem nháp của bài [TẬP HUẤN] Theo dõi mô hình chuyển đổi số. Đây là phạm vi cụ thể do người dùng cung cấp, không phải verdict suy ra từ log PASS. Không suy rộng sang Công bố, tải tệp thật, nhập báo cáo đầy đủ, giao việc, nộp–duyệt hoặc tổng hợp.

Ảnh lịch sử của bản thật cho thấy còn STT nhập tay, và ảnh sau có trạng thái v1 đã công bố. Chênh lệch đó giữ trong handoff ở docs/training/tap-huan-3-gio/; bản ghép và fixture đúng 15 nghiệp vụ không sửa bản thật hoặc xác nhận đã xóa STT thừa. Nếu người phụ trách kiểm sản phẩm tiếp, cần ghi môi trường/bản form/thao tác/bằng chứng sau xử lý riêng. Agent biên soạn không tự chạy UAT mới, API/DB/seed để khớp bài mẫu.

## Bổ sung v0.7.0 — chương mới và mức bằng chứng

Yêu cầu mới thay điểm dừng nháp v0.6 bằng hướng dẫn đến Công bố. Đây là quyền biên soạn, không cấp quyền agent tài liệu tự công bố bản thật. Các ca cơ bản và lịch sử giữ nguyên; không sửa dữ liệu để khớp guide.

Handoff clone 06/10 ghi UI thật tại PV01: nguồn 6ac40b8e5e204bc3cab29496 → bản 6ac420045e204bc3cab2abf3; 5 trường chung + 15 trường List + 1 tệp chung, STT tự động; lưu Bản nháp v1 và mở Nhập thử. Chưa xác nhận Công bố, nhập/nộp, giao việc hoặc tổng hợp. Không đánh toàn bảng dưới là LIVE_UAT_PASSED.

| ID | Kiểm guide/bài thử v0.7 | Kết quả cần thấy | Mốc giao checklist |
|---|---|---|---|
| GUIDE-DF07-01 | Chương A/B/C, sơ đồ và hình đích | Quan hệ cấu trúc và hai vai rõ; mục lục13 mục tra, 14 bước | GUIDE_QA_PENDING |
| GUIDE-DF07-02 | Kiểu, nút/cấu hình, Nhập thử | 10 kiểu có ví dụ/bước/kết quả/nhầm; 6 kiểu List/7 kiểu Bảng đúng; thiếu ảnh nêu đúng mục | GUIDE_QA_PENDING |
| GUIDE-DF07-03 | Bài 21 trường |5 chung,15 List,1 tệp ngoài List, STT tự động; SĐT giữ 0 đầu; không tự đặt đơn vị tiền/required | FE_FIXTURE_PENDING |
| GUIDE-DF07-04 | Xác nhận/Lưu/Nhập thử/Công bố/Sao chép | Ý nghĩa riêng; lưu/readback; Công bố chuẩn bị bước sau; clone draft không đổi nguồn | GUIDE_QA_PENDING |
| GUIDE-DF07-05 | Tìm/QA/menu/neo | Câu và từ khóa có/không dấu đến đúng mục; một menu; neo cũ giữ đúng | GUIDE_QA_PENDING |
| GUIDE-DF07-06 | Ảnh/player/dialog/bài thử | Nhãn mẫu, ảnh đúng thao tác; reset phiên; dừng player/Escape/focus; không network/storage sản phẩm | GUIDE_QA_PENDING |
| GUIDE-DF07-07 | Desktop/khung hẹp/in | Không tràn ngang toàn trang; in đúng chương/mục; không in iframe/nút/QA | GUIDE_QA_PENDING |
| GUIDE-DF07-08 | Đồng bộ MD/HTML/QA/fixture/print | Không lời điều phối/internal; không sót dừng trước Công bố; giới hạn offline nêu đầu bài | GUIDE_QA_PENDING |

Pending là mốc giao checklist. Kết quả root kiểm ghi tại qa/FORM-GUIDE-V07-HANDOFF.md; chỉ cập nhật verdict khi có bằng chứng đúng phiên bản. Source hoặc DOM không chứng minh browser/print PDF.

| ID | UAT sản phẩm cần giao riêng | Tiêu chí và nguồn | Trạng thái |
|---|---|---|---|
| UAT-DF07-01 | Clone→bản riêng→lưu→đọc lại | Có handoff UI06/10 của một clone cụ thể; kiểm host/version và bằng chứng trước khi cần chạy lại | HANDOFF_UI_CONFIRMED_LIMITED |
| UAT-DF07-02 | Kiểu/khối và lưu–mở lại | Tên/schema/Phần/kiểu/nguồn đúng, dữ liệu được giữ; không suy từ ảnh mẫu | LIVE_UAT_PENDING |
| UAT-DF07-03 | Required khi lưu nháp/nộp | Nháp cho thiếu, nộp chặn thiếu; theo đúng kiểu | LIVE_UAT_PENDING |
| UAT-DF07-04 | Hiển thị trên tổng quan | Kiểm consumer, vị trí và giá trị thật; không coi là cấu hình phép tính | LIVE_UAT_PENDING |
| UAT-DF07-05 | Công bố bản mới đã lưu | Quyền/draft, đủ Phần và trường/khối; dirty/open/incomplete bị chặn; Xác nhận→Đã công bố, khóa cấu trúc; chưa giao việc | LIVE_UAT_PENDING |
| UAT-DF07-06 | Upload/download tệp báo cáo | Tệp chung ngoài List, đúng quyền; chọn tệp Nhập thử không chứng minh upload BE | LIVE_UAT_PENDING |

Không cấu hình hoặc chạy SUM/COUNT/CONCAT/Nối danh sách/truy nguồn trong lượt tài liệu. COUNT báo cáo nguồn khác số mô hình/số đơn vị. Lịch180 phút vẫn chờ rà; giữ 50 phút Tổng hợp và 10 phút nghỉ.
### v0.7 actual document QA result

Build/content/interactive/import/DOM: checked successfully with npm run build, npm run check and npm run check:dom. CUA browser: anchors, curated Q&A/keyword search, 51 loaded images, player stop/close, embedded practice and 1440/390 layouts checked. Print: 37-page A4 chapter PDF rendered through Poppler and visually reviewed; no near-empty pages or observed clipping. See FORM-GUIDE-V07-HANDOFF.md for evidence and limitations. Product UAT/publish/BE upload/overview consumer remain pending; no product changes or real publish were performed.

## Chụp quản trị v0.7.2 — 06/10/2026

| ID | Phạm vi | Kết quả | Bằng chứng/giới hạn |
|---|---|---|---|
| GUIDE-ADM072-01 | Ảnh Đơn vị + khung Đang chọn | GUIDE_QA_PASSED | assets/20-units-pv01.jpg; xem đúng vị trí, không tạo đơn vị |
| GUIDE-ADM072-02 | Ảnh Người dùng + hàng công cụ | GUIDE_QA_PASSED | assets/21-users-pv01.jpg;1280×840, hàng 40px/gap8px; không tạo/sửa tài khoản |
| GUIDE-ADM072-03 | Menu 3 mục, Users/Units/Positions, CSV | GUIDE_QA_PASSED | assets/22-user-import-menu-pv01.jpg; không bấm tải/nhập |
| GUIDE-ADM072-04 | Ảnh HTML/player/Markdown/in | GUIDE_QA_PASSED | QA-V072.md; PDF1+4 trang, đã render/xem; không chứng minh chức năng ghi |
| UAT-ADM072-BE | Sau BE cập nhật: XLSX/CSV cấp phòng và phường | SOURCE_UPDATED_RUNTIME_PENDING | Tệp09:54 theo handoff còn001; tải lại, kiểm mã/cặp chức vụ/scope, mở Excel và kiểm tra tệp chưa xác nhận |

Không đổi UAT-I03–I08 thành PASS từ việc chụp tài liệu. Runtime offline/danh mục mẫu giữ nguyên. Các kết quả TypeScript/lint FE ở handoff là bằng chứng kế thừa của người sửa sản phẩm; lượt này không chạy lại hoặc build/restart BE.

## Menu và số mục v0.7.3 — 06/10/2026

| GUIDE-NAV073-01 | Menu tam giác, không box, keyboard/aria | GUIDE_QA_PASSED | QA-V073.md, browser1280/390 |
| GUIDE-NAV073-02 | Số10/11/12,11.x/12.x, neo/hỏi–đáp | GUIDE_QA_PASSED |20 nhóm DOM và browser STT→12.8 |
| GUIDE-NAV073-03 | In mở nội dung đang thu gọn | PRINT_MEDIA_CHECKED | CSS browser; không xuất PDF hoặc kiểm phân trang mới |

## Menu rộng v0.7.4

| GUIDE-NAV074-01 | Chiều rộng310/280/mobile100% và không tràn ngang | GUIDE_QA_PASSED | QA-V074.md;4 kích thước browser |
| GUIDE-NAV074-02 | Scrollbar3px trái, nhóm con không cuộn riêng | GUIDE_QA_PASSED | clientLeft3px, ảnhguide-menu-v074.jpg |
| GUIDE-NAV074-03 | Bản in giữ lề0 | PRINT_MEDIA_CHECKED | Không xác nhận phân trangPDF mới |

## In chương / toàn bộ v0.7.5

| GUIDE-PRINT075-01 | Nút in đủ bốn chương, chọn đúng chương và giữ thu gọn | GUIDE_QA_PASSED | QA-V075.md; 21 nhóm DOM và IAB |
| GUIDE-PRINT075-02 | Đổi in chương/toàn bộ/in bài, dừng hai trình phát và cleanup | GUIDE_QA_PASSED | DOM và lời gọi window.print chặn tạm trong browser |
| GUIDE-PRINT075-03 | PDF riêng bốn chương + toàn bộ | PDF_RENDER_CHECKED | A4:3/3/10/37/54 trang; text scope và ảnh render; không kiểm máy in vật lý |

Không đổi trạng thái UAT sản phẩm từ kết quả kiểm guide này.

## Tra cứu v0.7.6

| GUIDE-NAV076-01 | Mục11 không còn chip,13 mục menu vẫn dẫn đúng neo | GUIDE_QA_PASSED | QA-V076.md; browser1280/390,21 nhóm DOM |

## Nhiệm vụ, giao việc và báo cáo v0.8.0 — 06/10/2026

| ID | Phạm vi | Trạng thái | Bằng chứng/giới hạn |
|---|---|---|---|
| GUIDE-REPORT080-01 | Nội dung, chuyển vai,3 nhómQ&A/neo,17 ảnh tự chứa | GUIDE_QA_PASSED | npm run build/check;23 thẻ mới,13 câu; hash/bytes nguyên bản |
| GUIDE-REPORT080-02 | Collapse/link/Q&A/zoom/player/in5 chương | GUIDE_QA_PASSED |22 nhómDOM; CUA1280/390/768; QA-V080.md |
| GUIDE-REPORT080-03 | InV và toàn bộ khi đang thu gọn | PDF_RENDER_CHECKED |36/90 trang A4; đã render/xem; không kiểm máy in |
| DOC-REPORT080-CONTENT | Hướng dẫn trọn luồng thủ công | DRAFT_WITH_EVIDENCE_GAPS | Đã viết từ lịch sử; nút/ảnh thiếu ghi cần kiểm; chưa sẵn sàng demo receiver hiện tại |
| LIVE-REPORT080-ENTRY | Receiver mở phần việc từ chuông | LIVE_UAT_BLOCKED_INDEPENDENT | Handoff độc lập06/10: FAIL cả HT/QP tại Công việc cần thực hiện |
| LIVE-REPORT080-LIFECYCLE | Nhập→nộp→return→resubmit→approve và số cuối | HISTORICAL_EVIDENCE_CURRENT_RECHECK_PENDING | Handoff trước có bằng chứng; độc lập chưa đến editor, không có PASS mới |
| LIVE-REPORT080-INDICATOR | Tạo và đi toàn vòng Chỉ tiêu | EVIDENCE_PENDING | Mẫu1 chỉ là Nhiệm vụ; không suy rộng |
| LIVE-REPORT080-NEARDUE | Demo nhắc hạn trực tiếp | CLASS_FIXTURE_PENDING | Case ảnh25 về sau đã nộp/duyệt; cần phần việc còn mở phù hợp lớp |

Các trạng thái UAT sản phẩm khác giữ nguyên. Chi tiết blocker và source tại QA-V080.md; cảnh báo người dùng ở chươngV. Không tự đổi dữ liệu/hạn/đồng hồ hay coi source test là kiểm runtime.

## Báo cáo v0.8.1 — từng thao tác và ảnh mẫu

| ID | Phạm vi | Trạng thái | Bằng chứng/giới hạn |
|---|---|---|---|
| GUIDE-REPORT081-01 | Ví dụ dễ hiểu,56 thao tác kèm ảnh khoanh/tên tài khoản | GUIDE_STATIC_CHECKED | QA-V081.md;29 ảnh FE fixture, bytes nguyên bản |
| GUIDE-REPORT081-02 |4 theme gốc, chuyển vai HT/QP cùng màu | GUIDE_BROWSER_CHECKED |1280/768/390; không tạo luồng giao4 cấp |
| GUIDE-REPORT081-03 | Menu/Q&A/zoom/player/neo/in | GUIDE_DOM_BROWSER_CHECKED |23 nhómDOM, thao tác browser trực tiếp; neo cũ giữ |
| GUIDE-REPORT081-04 | PDF chương/toàn bộ | PDF_RENDER_CHECKED_LIMITED |59/113 trang, scope và trang render đã xem; chưa máy in vật lý |
| LIVE-REPORT081-ENTRY | Nhận việc từ chuông tại runtime thật | LIVE_UAT_BLOCKED_INDEPENDENT | Handoff độc lập06/10 14:08; không có kết quả sửa mới |
| LIVE-REPORT081-LIFECYCLE | Lưu/readback/nộp/trả/sửa/duyệt thật | CURRENT_RECHECK_PENDING | Ảnh cảnh mẫu không chứng minh chuyển trạng thái trên hệ thống |
| LIVE-REPORT081-NEARDUE | Demo hạn trong lớp | CLASS_FIXTURE_PENDING | Cần phần việc còn mở; không đổi thời gian/dữ liệu thật |

Không thay verdict v0.8.0 hoặc các UAT sản phẩm khác. Handoff ngắn: REPORT-LIFECYCLE-V081-HANDOFF.md.

## Bổ sung v0.8.2 — lượt mẫu trong guide, không đổi verdict sản phẩm
| Mục | Trạng thái guide | Trạng thái sản phẩm |
| --- | --- | --- |
| Một phiên PV01→HT/QP→PV01; lưu/nộp/return/sửa/nộp lại/approve | FE_FIXTURE_CHECKED + GUIDE_QA_PASSED: 8 model tests, browser full run | CURRENT_RECHECK_PENDING / phụ thuộc blocker receiver trong handoff độc lập |
| Đổi vai/đóng–mở giữ dữ liệu, report riêng, reset | GUIDE_BROWSER_CHECKED + GUIDE_DOM_CHECKED | Không suy rộng sang phiên/token/persistence thật |
| Đọc chuông vẫn cần nộp; cuối2report/1kỳ/2account/0chờ | GUIDE_BROWSER_CHECKED | Không lấy mẫu thay cho nghiệm thu số đếm/thông báo thật |
| Mobile390/desktop1365; in chươngV 60trang | GUIDE_QA_PASSED phạm vi ghi ở QA-V082 | Không liên quan nghiệm thu product |
Không đánh lại toàn bộ matrix PASS. Đối chiếu độc lập06/10 vẫn CHƯA ĐỦ; chưa có handoff mới hơn ở thời điểm đọc.

## Bổ sung v0.8.3 — trình xem tự chạy

| Mục | Trạng thái guide | Trạng thái sản phẩm |
| --- | --- | --- |
|20cảnh tự chuyển vai và hiện sẵn dữ liệu; pause/previous/next/replay | FE_FIXTURE_CHECKED + GUIDE_QA_PASSED:13model/controller tests,20cảnh browser bằng Tiếp | Không đổi CURRENT_RECHECK_PENDING |
|Khoanh DOM trong hộp đang mở;6/10→10/10 và8/8, lý do trả lại | GUIDE_BROWSER_CHECKED:desktop1365/mobile390, QA-V083 | Không thay bằng chứng UAT thật |
|Đóng/mở giữ cảnh và dừng; Escape/in | GUIDE_DOM_BROWSER_CHECKED theo phạm vi QA-V083 | Không suy ra vòng đời phiên sản phẩm |

Kết quả kiểm độc lập06/10 14:08 vẫn CHƯA ĐỦ; chỉ thêm kiểm guide, không PASS lại lượt UAT.

Kiểm bổ sung v0.8.3: một lượt tự chạy đủ20cảnh theo timer tớiĐã xem hết; hai báo cáo đã duyệt/chờduyệt0. In chươngV đóng/dừng và CSSẩn entry/dialog; visibilitychange mô phỏng dừng/tiếp tục, không coi là thao tác tab hệ điều hành. Không đổi UAT sản phẩm.

## Bổ sung v0.8.4 — Mẫu 2 / M01, giữ các verdict lịch sử

| ID | Phạm vi | Trạng thái | Bằng chứng/giới hạn |
| --- | --- | --- | --- |
| GUIDE-M2-084-01 |11chức năng/7chặng/9Q&A, mộtmenu, đúngvai/scope | GUIDE_QA_PASSED | build/check;77thẻ/76câu; không simulatorM2 |
| GUIDE-M2-084-02 |menu/neo/collapse/Q&A/zoom/ảnh/mobile | GUIDE_DOM_BROWSER_CHECKED |25DOM;1365/768/390; QA-V084, viewportreset |
| GUIDE-M2-084-03 |inbài/chương/toànbộ | PDF_SCOPE_RENDER_CHECKED_LIMITED |2/89/143trang; xem2trangbài + Mẫu2trang60–89; không ràhình143trang hoặc máyin |
| GUIDE-M1-084-REGRESSION |artifact/content/autoplayMẫu1 | HASH_UNCHANGED_BOUNDED_REGRESSION |13model/controller; mở/timer/pause/next/close/in/replay; chưa full20timer lại |
| PRODUCT-M2-FORM |PX01 clone riêng/lưu/mởlại/Côngbốv1 | UI_EVIDENCE_IN_HANDOFF |main; không mở quyềnPV01 để ép dùng |
| PRODUCT-M2-ASSIGN |PV01 hai phòng, PX01 giaoM01đội1/4kỳ | UI_EVIDENCE_IN_HANDOFF |main; PA02 chưa giaođội;07:00thật,22:00chưa |
| PRODUCT-M2-REPORT04 |nhập/lưu/nộp/read + trả/ngày04/nộplại/duyệt | UI_API_EVIDENCE_IN_LATEST_HANDOFF |FOUR_FIXES cuối03:01, BE identity ghi nguồn; guide không chạy lại; hiệnApproved, không Workcompleted |
| PRODUCT-M2-DATE |kỳ05/07 và ngàyhoànthành | PARTIAL_UI_API_EVIDENCE |05bắtbuộc,07currentkhôngbắtbuộc đã mở; chưa nộp hai kỳ/tựduyệt |
| PRODUCT-M2-SEARCH |tìmPX01 | UI_SELECTION_CHECKED_SAVE_PENDING |feedback/select/unselect/tree-sync; hủyhộp, chưa lưu/ngườidaidiện qua search; guide dùngcây |
| PRODUCT-M2-AGGREGATE |reportPX011nguồn / ViewPV01r2 | PARTIAL_UI_API_EVIDENCE |PX01Nháp1Approvedsource, read/save/reopen; ViewPV01r2khácđối tượng0nguồn |
| PRODUCT-M2-LINEAGE |nguồn/từngdòng/haitầng | PARTIAL_SOURCE_PIN_DIRECT_ROW_PENDING |UIpin/API/source read đúngreport; bảngchưa nútmởreporttừngdòng, hai tầng chưa |
| PRODUCT-M2-FULL |M02–M04/4đội/tựduyệt/tệp/22:00 | EVIDENCE_PENDING |không lấy1M01/fixture/PASSlog thay toànluồng |

Bổsung riêng, không đổi các dòngLIVE-REPORT080/081 hay verdictMẫu1. Bằngchứng product là kếthừahandoff đã đọc, không UATmới của tácgiảHTML. Không quy1reportApproved thànhWork/assignmentcompleted hoặc tổnghợp đủ. [QA](QA-V084.md), [handoff](MAU2-M01-V084-HANDOFF.md).

## Bổ sung M01 v0.8.5 — không đổi verdict sản phẩm

| ID | Phạm vi | Trạng thái | Bằng chứng/giới hạn |
| --- | --- | --- | --- |
| GUIDE-M2-085-CONTENT | 17 chức năng, 11 chặng, 15 Q&A và 33 ảnh thật | GUIDE_QA_PASSED_LIMITED | Build/check v0.8.5; 24 chụp mới + 2 lịch sử + 7 giữ nguyên; không viết Mẫu 2 đạt toàn bộ |
| GUIDE-M2-085-BROWSER | Vai/neo/Q&A/zoom/desktop/mobile | GUIDE_DOM_BROWSER_CHECKED | 25 nhóm DOM; 1365/390; QA-V085; không thay UAT |
| GUIDE-M2-085-PRINT | In bài/chương/toàn bộ | PDF_SCOPE_RENDER_CHECKED_LIMITED | Bài nhập 4, bài duyệt 2, V 107, toàn bộ 161 trang; xem bài và M01 trang 60–107; chưa in giấy/rà hình toàn bộ |
| GUIDE-M1-085-REGRESSION | Artifact/Mẫu 1 tự chạy | HASH_UNCHANGED_BOUNDED_REGRESSION | 7 hashes; 13 ca model/controller; mở/pause/next/close-reopen; không full timer lại |
| GUIDE-M2-086-M01-RECHECK | Bản chung sau cập nhật đồng thời | SOURCE_DOM_BROWSER_CHECKED_LIMITED | npm check, 26 DOM, đọc M01/zoom/mobile; không nghiệm thu M02 hoặc bản in v0.8.6 |
| PRODUCT-M2-085-READ | PX01 Nháp một nguồn, nguồn M01 đã duyệt | READ_ONLY_OBSERVATION + INHERITED_HANDOFF | Mở/đọc/đóng sau cập nhật FOUR_FIXES; không lưu/nộp/duyệt, không LIVE_UAT mới |

Hồ sơ [QA-V085](QA-V085.md), [handoff](MAU2-M01-V085-HANDOFF.md). Các bước sản phẩm còn chờ và lỗi nhãn “-” ghi riêng. Kết quả M02 thuộc lượt v0.8.6, không lấy kiểm v0.8.5 thay bằng chứng.

## M02 v0.8.6 — kiểm tài liệu, không đổi verdict sản phẩm

Các hàng v0.8.4/v0.8.5 giữ như lịch sử. M02 không còn ngoài phạm vi biên soạn: quyết định trực tiếp đã cho phép chặng ngày 04. Yêu cầu 22:00 và cảnh báo nền cũ đã bị thay/sửa; không giữ làm lỗi hiện tại.

| ID | Phạm vi | Mức bằng chứng | Giới hạn |
| --- | --- | --- | --- |
| GUIDE-M2-086-CONTENT | 37 chức năng, 11 M01 + 12 M02, 23 Q&A; đủ dữ liệu và kết thúc | SOURCE_STATIC_DOM_CHECKED | Nguồn UI bàn giao; ảnh thao tác còn thiếu |
| GUIDE-M2-086-BROWSER | Một menu/neo, đổi vai, Q&A, ảnh/zoom gốc, 1365/768/390 | GUIDE_BROWSER_CHECKED | Trình duyệt guide, không runtime sản phẩm |
| GUIDE-M2-086-PRINT | Bài/chương V/toàn bộ | PDF_SCOPE_RENDER_CHECKED_LIMITED | Rà bài và chặng M02, trang đại diện toàn bộ; không in giấy/rà lại mọi bài cũ |
| GUIDE-M1-086-REGRESSION | Bảy hashes, 13 model/controller, 20 cảnh bằng Tiếp | HASH_UNCHANGED_GUIDE_BROWSER_CHECKED | Mở thấy auto chuyển, pause/next/close-reopen; chưa full timer lại |
| EVIDENCE-M2-M02-04 | Mẫu riêng/publish/giao daily/nhập/lưu/readback/nộp/xác nhận/duyệt/người nhận đọc | UI_EVIDENCE_IN_HANDOFF | HANDOFF_UAT_MAU_2_M02; không LIVE_UAT mới của guide |
| EVIDENCE-M2-M02-NOTICE | Sau tải lại có và mở đúng M02 | UI_EVIDENCE_IN_HANDOFF_PARTIAL | Phiên mở chưa tự hiện; độ trễ/tự cập nhật còn cần kiểm, chưa nghiệm thu toàn thông báo |
| EVIDENCE-M2-PX01 | Report Nháp, nguồn M01 đã lưu/mở được | UI_EVIDENCE_IN_HANDOFF | Chưa M02 mapping, chưa nộp phòng, không thay View tổng hợp |
| PENDING-M2-NEXT | PA02/M03/M04,05–07,tự duyệt hôm nay,đủ nguồn,truy hai tầng | EVIDENCE_PENDING | Chỉ thêm hướng dẫn khi có bằng chứng tiếp theo |

Chi tiết [QA-V086](QA-V086.md), [handoff M02](MAU2-M02-V086-HANDOFF.md), [ảnh và yêu cầu bổ sung](MAU2-M02-V086-IMAGES.md). Lịch 180/50/10 còn chờ rà.

Đối chiếu cuối: README và kịch bản nguồn được cập nhật thêm PA02 ngoài lượt guide, nêu bốn report ngày04/4trên12kỳ quá khứ. Đã đọc lại; hàng PENDING-M2-NEXT là phần chưa biên soạn/đối chiếu ảnh trong bản M02, không khẳng định PA02 chưa dựng/giao. Không thay verdict UAT hoặc thêm luồng M03/M04 từ lời tóm tắt chung. Hash trước/sau đọc trong manifest.

## v0.8.7 — tài liệu/giáo án, không thay verdict UAT

Quyết định mới thay lịch 180/50/10 bằng 180/50/20 chờ duyệt. PA02 ngày 04 đã có bằng chứng mới; không giữ PA02/M03/M04 chưa chạy làm hiện trạng. Chỉ bổ sung biến thể tra cứu, chưa gọi bài bốn mô hình hoàn tất.

| ID | Phạm vi | Mức bằng chứng | Giới hạn |
|---|---|---|---|
| GUIDE-087-CONTENT | 9 tra cứu; M01/M02 đọc lại; 7 nhóm Số hóa; giáo án | SOURCE_STATIC_CHECKED | 13 bảng Mẫu 2 nguyên vẹn; ảnh/nhãn còn thiếu |
| GUIDE-087-DOM | Menu/neo/role/zoom/Q&A/in chung | GUIDE_DOM_CHECKED | 27 nhóm; không thay browser thật |
| GUIDE-087-BROWSER | Desktop1365, mobile768/390, link cũ, M1/M2/ảnh | GUIDE_BROWSER_CHECKED | Chỉ guide; không thao tác sản phẩm |
| GUIDE-087-PRINT | Giáo án/M1/M02/chương/toàn bộ | PDF_SCOPE_RENDER_CHECKED_LIMITED | 2/17/16/135/189 trang; rà 95 trang render theo QA, chưa in giấy/rà toàn189 |
| GUIDE-087-M1-OLD | 7 artifact hash; 13 model/controller; 20 cảnh bằng Tiếp | HASH_UNCHANGED_BOUNDED_BROWSER_REGRESSION | Chưa chạy lại full20timer; không acceptance sản phẩm |
| EVIDENCE-087-DIGIT | PX210/180/60/8; PA150/123/41/8,5; gốc360/303/101/8,25 | INHERITED_UI_HANDOFF | Bộ gốc đã chạy theo handoff; không chạy bản deploy ở lượt guide |
| PENDING-087-FILTER | 01–30→01–29→khôi phục tháng9 | EXPECTED_VALUES_PENDING | Ca độc lập chưa có runtime/ảnh; không dùng nguồn tháng10 chưa duyệt thay proof |
| EVIDENCE-087-PA02 | M03/M04 ngày04 Đã duyệt, kết quả người nhận | INHERITED_UI_HANDOFF | Chỉ biến thể; thông báo lượt đọc sau chưa đo độ trễ/tự cập nhật |
| PENDING-087-FULL | Mẫu2 ngày05–07/tựduyệt/tổng hợp đủ/reportphòng/truyhaitầng | LIVE_UAT_PENDING | PX01 còn Nháp; View nhiệm vụ khác report gửi trên |
| PENDING-087-DASH | Dashboard/cây/hạn sau sửa | INHERITED_SOURCE_TEST_BROWSER_MIXED | KUI01–07 khác mức;08/09 còn mở;10 ONLY ngoài coreM1; ảnh mindmap cũ loại |
| PENDING-087-COURSE | Lịch/triển khai/dựng sau reset | USER_REVIEW_PENDING | Chưa chốt lịch; không reset/build/restart/seed/deploy |

[QA](QA-V087.md), [bàn giao](TRAINING-V087-HANDOFF.md), [ảnh](TRAINING-V087-IMAGES.md). Giữ nguyên verdict lịch sử và phân biệt QA tài liệu với UAT sản phẩm.
