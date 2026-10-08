# Tổng hợp P05 — Bàn giao cho agent UAT ứng dụng

**Revision 1 · 07/10/2026 · Lam → agent UAT do Yud giao.** Theo yêu cầu “Chốt tài liệu bàn giao phần này cho agent UAT ứng dụng”. Lượt này chỉ chốt tài liệu: không khởi động UAT/P06, không tạo/nhắn agent, không chạy thêm test/build/browser/API/job hoặc đổi DB/host.

## 1. Nhận bàn giao và cách đọc

**P05 đã chốt kỹ thuật cho báo cáo một lần/định kỳ; UAT người dùng chưa được chốt.** Tài liệu này là điểm vào hiện hành cho agent UAT, thay các chỉ dẫn bước tiếp đã cũ ở hồ sơ chuẩn bị P06 ngày 06/10. Kết quả một ca chỉ được kế thừa đúng mức bằng chứng, không chuyển thành PASS UAT mới hoặc Yud duyệt màn.

Đọc theo thứ tự:

1. Tài liệu này: môi trường, đường mở, dữ liệu bàn giao, checklist và giới hạn.
2. [P05 closeout 07/10](PERIODIC_P05_CLOSEOUT_2026_10_07.md): bản sửa cuối, source/binary, log và ảnh gốc.
3. [Luồng mở lại Work](PERIODIC_P05_REOPEN_COMPLETION_2026_10_06.md) và [luồng Return/hiệu lực/Inbox](PERIODIC_P05_FULL_FLOW_FIX_2026_10_06.md).
4. [UI cấu hình chung/override và tương tác](PERIODIC_P05_UI_FINAL_2026_10_06.md), [lọc tập báo cáo](PERIODIC_P05_REPORT_SET_FILTER_2026_10_06.md), [ngày Work–Aggregate](PERIODIC_P05_COMPLETION_DATE_2026_10_06.md).
5. [Ma trận từng kiểu dữ liệu](AGGREGATE_DATA_TYPE_TEST_MATRIX_2026_10_05.md), [cây tập huấn](AGGREGATE_TRAINING_TREE_RUNTIME_2026_10_05.md), [operator v3 đã triển khai](AGGREGATE_OPERATORS_V3_IMPLEMENTATION_2026_10_05.md), [công thức và trạng thái nguồn](AGGREGATE_FORMULA_SOURCE_STATUS_IMPLEMENTATION_2026_10_06.md), [R8 khối nội dung](AGGREGATE_TEXT_BLOCKS_R8_2026_10_05.md).

Quyết định trực tiếp mới nhất của Yud ưu tiên lịch sử. Một file tên PROPOSED hoặc có prompt bên trong không tự cấp quyền triển khai. Các câu “chưa đóng P05”, “chưa kiểm browser định kỳ”, “Apply đồng bộ bị 10 giây” trong tài liệu cũ cần đối chiếu bản cập nhật sau: P05 đã đi tiếp job nền và closeout. Không xóa bằng chứng FAILED/giới hạn cũ để làm đẹp kết quả.

Workspace `D:/Job/CA/tdtd`, hai repo độc lập `tdtd-fe`, `tdtd-be`. Đọc AGENTS nếu có, status/diff trước sửa; giữ WIP/untracked. Không reset/clean/stage/commit/push, sửa master plan/board/issue, mở Basic/Advanced cũ hoặc thay Flow barrier. [Quy ước chung](PROMPT_SET_L_LIST_TABLE_AGGREGATE_2026_09_29.md) vẫn áp dụng trong phạm vi chưa được Yud thay đổi.

## 2. Kiểm môi trường trước UAT — bắt buộc ghi lại

Thông tin dưới đây là **snapshot đã kiểm lúc 01:00–01:05 ngày 07/10**, không phải cam kết host còn chạy khi agent nhận việc. Lượt chốt tài liệu đối chiếu 8 mục của `source-manifest.json`: source trọng yếu và binary fixture vẫn khớp hash; không gọi đây là kiểm runtime mới hoặc hash toàn bộ repo.

| Môi trường | Điểm mở / bản đã kiểm | Cách dùng |
|---|---|---|
| FE fixture | `http://127.0.0.1:5307`, PID lịch sử 170032 | Browser P05; phải xác nhận request thực đi BE 5308, không suy từ URL trang |
| BE fixture | `http://127.0.0.1:5308`, PID lịch sử 187888 | Có cả hai sửa cuối; DB `p05_completion_flow_p05_flow_20261006_5c85ef9f` |
| FE chung | `http://localhost:5173`, PID lịch sử 147324 | Dữ liệu nghiệm thu/cây tập huấn dùng chung; không dùng làm nơi seed hoặc thử lỗi |
| BE chung | `http://localhost:5164`, PID lịch sử 173696 | Binary Dashboard mở 00:44, **chưa chứa sửa projection cuối tại thời điểm bàn giao** |

Binary fixture đã kiểm: `outputs/p05-close-20261007/projection-fix-artifacts/bin/tdtd-be/debug/tdtd-be.dll`, SHA256 `3205E9F662D41617EB96CA554472DAA6E0B1EFC1F5D87F5FABEA4257946F82FD`.

Agent ghi trước ca đầu:

- FE URL/build/dev hoặc production, BE URL/binary/hash, thời điểm, DB và tài khoản/vai trò; lưu diff nguồn liên quan nếu có thay đổi sau closeout.
- Readiness và startup migration/provenance bình thường; Mongo transaction/MinIO/worker theo cấu hình thật. `200` health không tự chứng minh binary đúng hoặc job đang xử lý.
- Bootstrap trả capability/quyền thật; thiếu capability phải ghi BLOCKED/UNSUPPORTED đúng nguyên nhân, không sửa response/client, không bật đường cũ để chạy qua.
- Nếu kiểm trên host chung: phối hợp lịch restart với owner đang dùng, build từ source hiện tại có cả WIP đã thống nhất. **Không chép binary fixture P05 cũ đè binary chung**, vì có thể mất sửa Dashboard sau đó; không dùng cấu hình DB Testing cho host chung. Sau nạp ghi hash mới, migration và smoke lại phần bị ảnh hưởng.
- Giữ khóa ký/xác nhận của phiên phù hợp. Helper fixture sinh khóa JWT/xác nhận mới khi restart: phải đăng nhập/xem trước lại; token cũ bị từ chối không phải lý do nới kiểm stale. Không công khai giá trị key/password/JWT trong log hoặc tài liệu.

Helper đã có để owner tham khảo: [start-fixture-v2.ps1](../../../../outputs/p05-close-20261007/start-fixture-v2.ps1), [Vite fixture config](../../../../outputs/p05-flow-fix-20261006/vite.fixture.config.mjs). **Không chạy lại mù**: chúng là script phiên kiểm, có thể ghi đè log/PID cũ; tách output UAT mới, kiểm cổng/process và cấu hình trước. Không tự đổi password hoặc reset fixture để có đăng nhập.

## 3. Tài khoản, dữ liệu và hai điểm vào

### 3.1 Vai trò fixture

Run `p05-flow-20261006-5c85ef9f`; không dùng tài khoản thật để thay đổi dữ liệu fixture bằng quyền admin.

| Nhóm | Tài khoản | Vai trò |
|---|---|---|
| Định kỳ | `p05-flow-20261006-5c85ef9f-periodic-reviewer` | Chủ Work/cấp duyệt PV01 |
| Định kỳ | `p05-flow-20261006-5c85ef9f-periodic-author` | PV01 lập tổng hợp, duyệt PX01 |
| Định kỳ | `p05-flow-20261006-5c85ef9f-periodic-source` | PX01 báo cáo nguồn |
| Một lần | Cùng tiền tố, hậu tố `-once-reviewer`, `-once-author`, `-once-source` | Ba vai trò tương ứng |

Mật khẩu fixture trước đây cấp qua biến môi trường phiên `P05_FLOW_FIXTURE_PASSWORD`, không lưu ở handoff. Tận dụng phiên được giao nếu còn; khi cần đăng nhập lại mà chưa được cấp credential, báo thiếu credential/chuyển Yud cấp qua kênh riêng, không đoán từ password tài khoản thật, không reset tài khoản. Mảng `users: []` trong manifest seed cũ là lỗi xuất manifest đã ghi nhận, không phải bằng chứng tài khoản không tồn tại.

Tài khoản chung Yud từng cấp: pv01, các vai trò cây tập huấn trong tài liệu cây. Quyền phiên chat khác không tự thành quyền agent nhận mọi tài khoản; xác minh phạm vi được giao và không ghi mật khẩu vào handoff.

### 3.2 Trạng thái baseline định kỳ đã chốt

Work `6ac50a77ea772ca23455cc9a`; assignment PV01 `6ac50a77ea772ca23455cc9b`; PX01 `6ac50a77ea772ca23455cc9c`.

Nguồn baseline: [reports-after.json](../../../../outputs/p05-close-20261007/reports-after.json), [final-readback.json](../../../../outputs/p05-close-20261007/final-readback.json). Đây là trạng thái cuối P05; đọc lại trước UAT nếu có người thao tác tiếp.

| Tháng / đối tượng | Report ID | Period ID | Trạng thái / nội dung cuối P05 |
|---|---|---|---|
| 9 / PV01 | `6ac50a78ea772ca23455cca3` | `6ac50a78ea772ca23455cca4` | Approved, total=41, chỉ xem |
| 9 / PX01 | `6ac50a78ea772ca23455cca6` | `6ac50a78ea772ca23455cca7` | Approved, n=41 |
| 10 / PV01 | `6ac50a78ea772ca23455cca9` | `6ac50a78ea772ca23455ccaa` | Approved, total=42, chỉ xem |
| 10 / PX01 | `6ac50a78ea772ca23455ccac` | `6ac50a78ea772ca23455ccad` | Approved, n=42; bị khóa phụ thuộc khi PV01 đã nộp |
| 11 / PV01 | `6ac50a78ea772ca23455ccaf` | `6ac50a78ea772ca23455ccb0` | Draft, field values `{}`; chưa nhận đã Apply tháng 11 |
| 11 / PX01 | `6ac50a78ea772ca23455ccb2` | `6ac50a78ea772ca23455ccb3` | Approved, n=40; không bị sửa trong vòng tháng 10 |

Work và PV01 đang thực hiện, PV01 2/3 kỳ Approved; hold/pending đã hết. Kỳ tháng 10 có khoảng dữ liệu khai báo 01–31/10, PeriodKey/hạn là 30/10. **Không đồng nhất hai mốc.** Tháng 9 được xử lý có chủ đích để đáp ứng thứ tự duyệt lần đầu; không tuyên bố nó còn nguyên so với seed.

Đường mở:

- [Report PV01 tháng 10](http://127.0.0.1:5307/works/6ac50a77ea772ca23455cc9a/inbox/report/6ac50a78ea772ca23455ccaa): đối chiếu Approved/42/chỉ xem.
- [Work định kỳ — thuộc tính chung](http://127.0.0.1:5307/works/6ac50a77ea772ca23455cc9a?tab=COMMON): quyền/tiến độ/kết thúc/mở lại.
- [Work một lần](http://127.0.0.1:5307/works/6ac50a78ea772ca23455ccb9?tab=COMMON): assignment PV01 `6ac50a78ea772ca23455ccba`, PX01 `6ac50a78ea772ca23455ccbb`; report đích `6ac50a78ea772ca23455ccc2`, period `6ac50a78ea772ca23455ccc3`; nguồn `6ac50a78ea772ca23455ccc5`, period `6ac50a78ea772ca23455ccc6`. Trạng thái cuối trong handoff mở Work, đọc lại trước dùng, không gán trạng thái định kỳ cho fixture này.
- [Work chưa có report cấp gốc](http://127.0.0.1:5307/works/6ac52868d10be74b490c2138?tab=COMMON): ca mở lại bằng lý do, không bắt chọn report; đã mở trong vòng trước, không chờ nút Mở lại còn hiện khi Work đang mở.

Manifest nền: [fixture.json](../../../../outputs/p05-flow-fix-20261006/fixture.json). Cây/Work tải 166 nhánh `6ac535746e154ebc647d70fd` chỉ dành tải riêng; không dùng số liệu của nó làm nội dung baseline nghiệp vụ.

### 3.3 Hai chức năng tổng hợp khác nhau

| Điểm vào | Mục đích / expected |
|---|---|
| Giao việc → **Biểu mẫu tổng hợp** cạnh Giao việc | Một biểu mẫu xem dùng chung cho **mỗi cấp sở hữu**: A tại Work, B tại assignment, C tại assignment con. Lấy báo cáo con trực tiếp, phục vụ View/Dashboard/mindmap. Không có Submit như một report gửi cha |
| Báo cáo → report đang mở → **Tổng hợp báo cáo** | Điền mẫu được giao của report đó; một lần dùng đúng instance; định kỳ dùng cấu hình chung và sửa riêng kỳ. Nộp report thì khóa kết quả/nguồn theo lifecycle |

Đọc cây tập huấn dùng chung: [nhánh PV01](http://localhost:5173/works/6ac305a1251fbfc0d07720c4?tab=ASSIGN&assignmentId=6ac305a1251fbfc0d07720c8). Baseline lịch sử: PX01 lấy A/B → 2 report, 10 phần tử, SUM 37; PV01 lấy PX01/C/D → 3 report, 20 phần tử, SUM 69, matrix `[[8,12],[11,14]]`; Giám đốc lấy PV01 → 1 report, SUM 69. Không cộng lại A/B tại PV01 hoặc Giám đốc. View và report có cấu hình/snapshot riêng.

Cây tập huấn nằm DB chung: **chỉ đọc baseline trừ khi Yud giao rõ mutation**. Ca sửa/Return/ngừng/revoke/lỗi mạng dùng fixture UAT riêng, có nhãn và manifest. Không dọn baseline P05; trước thay đổi fixture được giao phải chụp trạng thái và dùng API lifecycle; không restore DB trực tiếp để ép kết quả.

## 4. Quy tắc đã chốt — không hỏi lại hoặc tự đổi

| Chủ đề | Quy tắc UAT phải giữ |
|---|---|
| Eligibility | Chỉ bản hiện hành Approved/tự duyệt hợp lệ của con trực tiếp và đủ quyền; không fallback Approved cũ, không lấy Submitted/Draft/ẩn/nhánh ngừng. Trạng thái duyệt là điều kiện hệ thống, không đưa thành bộ lọc tự chọn |
| Quyền | Quyền Form không tự cấp quyền nguồn report/catalog; người xem không tự thành người sửa. Tập phụ thuộc/khóa có thể rộng hơn tập góp số; lỗi quyền/missing không giả empty/0 |
| Lọc | Bộ lọc **tập báo cáo** áp metadata; khối Lọc giá trị/phần tử và khối Tính toán là lớp khác. AND/OR phần tử phải cùng phần tử; không ghép điểm dòng A với lựa chọn dòng B |
| Ngày | Ngày hoàn thành/nộp: report quá khứ dùng CompletedDate, hiện tại dùng SubmittedAtUtc theo ngày Việt Nam. Ngày tạo lần giao, bắt đầu/hoàn thành assignment, Start/End Work, hạn report và khoảng dữ liệu là các khái niệm riêng |
| Khoảng và thiếu metadata | Ngày cố định, khoảng khai báo của kỳ đích, lũy kế từ mốc tới cuối khoảng đích; không mặc định ngầm, không dùng hạn làm dữ liệu. Khoảng mở cho trống một đầu; thiếu mốc cần thiết báo rõ, không tự điền. PRESENT/ABSENT là điều kiện tường minh |
| Khác lịch | Nằm trọn khoảng hoặc lấy nguyên report giao khoảng theo lựa chọn; không chia số tuần cắt tháng. So sánh kỳ trước và lũy kế tương đối giữa kỳ là phần hoãn |
| Ngày không đầy đủ | So theo độ chi tiết mốc lọc; nguồn không đủ độ chi tiết báo thiếu, không điền ngày. Cực trị ngày không tự mở rộng sang partial-date nếu API chưa hỗ trợ |
| Cấu hình/revision | Owner tại cấp đang nhận giao tự quản lý. Lưu revision mới: xem thay đổi, chọn nháp chuyển; kỳ mới theo revision chung mới; đã nộp giữ pin. Sửa riêng nằm ở mapping kỳ, không tạo hàng trăm Form phụ; reset override phải xem trước/xác nhận |
| Tính số | Phép chia tối đa sáu số lẻ, gần nhất về số chẵn. Không tự đổi mẫu số AVG; AVG_PRESENT là lựa chọn khác đã có contract. Trọng số trống/âm lỗi, 0 không góp; tổng weight=0 không kết quả; điểm trống/weight dương báo thiếu |
| Đếm | UI nêu rõ đếm report/đơn vị/phần tử/giá trị/mã khác nhau. Không thêm phép đếm hàng Table đã bị Yud bỏ; không đếm người duy nhất bằng họ tên |
| Danh sách | itemId/fieldId ổn định, không identity theo index/mã hiển thị. Top-N lấy đúng N, hòa dùng ID nguồn/phần tử, trống cuối; bị cắt có cảnh báo và scope. ONLY_ITEM: 0 không kết quả, >1 lỗi; không lấy dòng đầu |
| Table | Giữ engine hiện có; không ép matrix thành records/List. Lọc vùng trong khối Tính toán Bảng, giữ header/hàng/cột/tọa độ; ô không kết quả để trống. Không coi List có phép nối là Table cũng đã mở phép đó |
| Text | Ngưỡng 1.000 ký tự hiển thị: dài hơn chỉ ghép nội dung theo policy đã chốt. Rich text không COUNT_DISTINCT; tìm kiếm text ngắn phải xét chữ đã bỏ markup. StringList là khối của một report, không List object |
| Nội dung dài | Hai cách xem: khối chữ ghép và bảng Đơn vị báo cáo—Nội dung; một report một khối/dòng, nội dung trống giữ dòng. Chọn kiểu tên đơn vị hoặc không hiển thị; không lấy đơn vị công tác của phần tử thay đơn vị báo cáo |
| Job/nguồn thay đổi | Lưu cấu hình/ý định trước, job tính nền; kết quả cũ phải hiện đang xử lý/stale, không giả cuối. Return/ẩn/ngừng hợp lệ làm tính lại các nháp phụ thuộc; bật/duyệt lại chỉ góp một lần. Không dùng phép trừ số mù cho AVG/MIN/MAX/CONCAT |
| Nộp và khóa | Đã nộp giữ bất biến; cha đã nộp sử dụng nguồn thì nguồn bị khóa tới khi gỡ đúng lock owner qua lifecycle. “Inbox đã xử lý” không đồng nghĩa chỉ xem; BE trả capability hiện tại |
| Hoàn thành/mở lại | Hoàn thành khác deactive. Mở cha chỉ bỏ khóa do cha, giữ khóa riêng con. Sửa kỳ dưới phạm vi đã hoàn thành đi qua mở lại hiện có. Work quá hạn không tự đóng trước khi sửa xong; xét lại khi report chọn sửa được duyệt lại. Không thêm Hủy mở lại |

## 5. Checklist giao agent UAT

**Tất cả ca dưới đây có trạng thái lượt UAT mới: CHƯA CHẠY.** “Có bằng chứng P05” chỉ là reference. Ghi PASS/FAIL/BLOCKED/N/A kèm lý do sau thao tác; Yud duyệt UI là một cột riêng, không tự điền. Chọn fixture mutation riêng và thứ tự để không sửa nền của ca sau.

| Mã / ưu tiên | Thao tác cụ thể | Kết quả cần đối chiếu |
|---|---|---|
| UA01 / P0 | Ghi host/build/DB/role, mở hai điểm vào mục 3.3 | Đúng scope, capability; không màn trắng hoặc rơi Basic/Advanced cũ |
| UA02 / P0 | Đọc baseline PV01 tháng 10, lịch sử và khóa nguồn | Approved/42/chỉ xem; không còn nút sửa/lưu được bằng URL vòng; không thay payload khi đọc |
| UA03 / P0 | Nháp scalar riêng → nhập tiếng Việt → Save → tiếp tục gõ khi đang chờ | Đọc lại quyền/revision mới, không khóa ô nhầm, không mất nội dung gõ thêm |
| UA04 / P0 | Trên fixture, tạo trường hợp server đã lưu nhưng đọc lại lỗi/mạng chậm | Giữ nháp/đối chiếu; không ghi lặp, không báo đã xong giả. Tiêm lỗi chỉ trên phiên fixture, có kế hoạch phục hồi |
| UA05 / P0 | Cấu hình trước khi có Approved → duyệt lần lượt nguồn 1/2/3 → Return nguồn 3/2/1 | SUM không nguồn là không kết quả; sau duyệt 1→3→6; sau Return 3→1→không kết quả. Cấu hình không phải lưu lại; UI báo chờ tới khi job hoàn tất |
| UA06 / P0 | Nguồn tự duyệt, lặp cùng lệnh; ẩn/hiện report; ngừng/bật assignment | Chỉ góp một lần; loại/nhận lại đúng nhánh; không bỏ khóa riêng. Nếu cha đã dùng và nộp phải chặn đúng lock owner |
| UA07 / P0 | Cấp tổng hợp preview/nộp; nguồn đổi trước khi xác nhận hoặc quyền bị thu hồi | Stale/forbidden chặn ghi, lỗi tiếng Việt đúng vị trí, giữ nháp. Không bỏ lượt đối chiếu với kết quả commit chưa rõ |
| UA08 / P0 | Trả report đã duyệt từ Inbox HANDLED; sau đó cấp dưới sửa/nộp/duyệt lại | Capability hiện hành quyết định thao tác; không khóa UI chỉ vì HANDLED; snapshot nháp cập nhật, đã nộp bất biến |
| UA09 / P0 | Cha đã nộp dùng nguồn, thử Return/ẩn/ngừng nguồn; trả report cha theo quyền rồi thử lại | Ban đầu chặn phụ thuộc; sau giải phóng đúng owner mới được làm. Không tự gỡ owner khác hoặc cả chuỗi kỳ |
| UA10 / P0 | Hoàn thành/mở lại Work và assignment có kỳ; có con ngừng/hoàn thành riêng | Chọn đúng report sửa; con chỉ mất khóa kế thừa. Review mọi kỳ, cây, Dashboard đồng nhất; pending chỉ hết sau hội tụ |
| UA11 / P0 | Work không có report cấp gốc → hoàn thành/mở lại theo capability | Popup chỉ cần lý do; không tạo report hoặc hold giả. Khi thực tế đã có report không được gửi thiếu lựa chọn để vượt kiểm |
| UA12 / P0 | Work quá hạn đã mở để sửa → chưa sửa/chưa duyệt → duyệt đúng report sửa | Không tự đóng ngay; quá hạn vẫn hiển thị; sau duyệt mới xét lại hoàn thành theo điều kiện. Duyệt kỳ khác không nhả hold sai |
| UA13 / P0 | Chỉnh kỳ cũ khi kỳ sau đã Approved trong phạm vi đang mở | Không bị chặn chỉ vì có kỳ sau; kiểm cửa sổ sửa dựa cập nhật, quyền/khóa/hoàn thành thực. Không nhầm với thứ tự duyệt lần đầu |
| UA14 / P0 | Một lần qua Save→job→đọc lại→Submit→Return→duyệt; thêm report không mapping | ONCE giữ identity thực; không sinh lịch giả; luồng không mapping không bị buộc tính tổng hợp |
| UA15 / P0 | Ba kỳ: chung r1 → preview r2 → chọn chỉ kỳ nháp giữa → override/reset | Kỳ không chọn giữ pin; đã nộp không đổi; kỳ mới dùng chung mới; sửa riêng không tạo Form phụ. Đóng/mở editor và đọc report xác nhận |
| UA16 / P1 | Metadata ngày: quá khứ hoàn thành tháng 1/nộp tháng 9; khoảng mở; ngày VN và mốc thiếu | Hoàn thành/nộp dùng đúng loại. Mốc tháng/ngày/PeriodKey/hạn không thay nhau; PRESENT/ABSENT và lỗi thiếu rõ; không giả 0 |
| UA17 / P1 | Tuần 28/9–4/10 so khoảng tháng 9, thử hai lựa chọn khác lịch | Nằm trọn loại tuần giao tháng; giao khoảng lấy cả report, cảnh báo trùng phù hợp; không tự chia số. Không suy đã qua browser từ test tháng đơn |
| UA18 / P1 | Kéo/nối/gỡ dây, chèn Lọc trước/Tính toán/Lọc sau, chọn dây xem chi tiết | Dây và khối tự bố trí/nối theo UX; đầu nối rõ; sai kiểu báo và ngắt; thả đầu đã nhấc ra khoảng không gỡ dây. Table dùng vùng trong khối Bảng |
| UA19 / P1 | Nhập công thức lồng, nháy kép, dấu `;`, ngoặc; chọn ba nguồn thật để tính thử | Màu/gợi ý/caret đúng, lỗi có vị trí; tính thử không ghi report. Ví dụ `=IF(SUM(S)>35;"Đạt";"Chưa đủ")` với S đã nối; không tự tìm biến trong DB |
| UA20 / P1 | List lọc cùng dòng; sort/top-3 từng report và toàn nguồn; nối List | Trên baseline PX01: top-3 từng report 6 phần tử/SUM36; toàn nguồn 3/SUM24. PV01: 9/SUM56 và 3/SUM27. ID/lineage đúng; có cảnh báo cắt; không join/dedup |
| UA21 / P1 | List 0/1/>1 cho lấy một; choice/multi blank/false/0/nhãn/mã bị thu hồi | 0 không kết quả; >1 lỗi; giá trị 0/false không bị coi trống. Option đúng quyền/pin; không lộ mã thô thay nhãn có quyền |
| UA22 / P1 | Chữ 1.000/1.001 ký tự, rich markup/entity, stringList, bảng nội dung dài | Đúng policy độ dài; ghép cả khối, dòng trống giữ; page/part và Đọc đầy đủ, không tải mọi text/trace khi mở trang |
| UA23 / P1 | Table ngang/dọc/matrix, chọn vùng và lọc vùng rồi gán đích | Hồi quy capability hiện có; giữ header/tọa độ/missing blank. Cell richText/stringList kiểm riêng, không nâng scalar PASS thành cell PASS |
| UA24 / P1 | Job đang chờ/đang chạy → đóng/mở lại → cancel/retry → đọc report | Kết quả tạm/cuối phân biệt; job cũ không ghi đè cấu hình/submit mới; lỗi có đường xử lý hợp lệ, không endless spinner hoặc thành công giả |
| UA25 / P1 | Replay notification/convergence theo harness đúng fixture | Không sinh kỳ/thông báo trùng; đối chiếu đúng cấu hình notify. Fixture bàn giao tắt NotifyReviewRequired nên không đòi notification nộp tại đây |
| UA26 / P1 | IME Windows thật, paste nhiều dòng, đổi focus, lỗi xác nhận; đo tương tác | Không mất âm cuối/con trỏ/nháp; ghi browser/thiết bị/đường thao tác và Event Timing. Không lấy mounted hoặc thời gian tool làm INP người dùng |

Các expected số của cây chỉ dùng khi readback baseline còn khớp. Nếu đã bị chỉnh, ghi phiên mới và tính expected độc lập trước chạy; không sửa số liệu về expected hoặc coi tài liệu là nguồn số hiện tại.

## 6. Phủ kiểu dữ liệu và khoảng thiếu cần báo

Đối chiếu ma trận datatype theo **kiểu × operator × màn × lifecycle**, không đánh dấu toàn kiểu đạt từ một SUM scalar. Bảng gọn để agent không bỏ sót:

| Kiểu / cấu trúc | Điểm UAT cần bổ sung, không tự nhận P05 đã phủ hết |
|---|---|
| number | 0/blank/no-result, chia/làm tròn, AVG so AVG_PRESENT, IF lồng; COUNT phải rõ cơ sở |
| longText | Ngắn/dài 1.000/1.001, ghép khối/paging/IME; giữ dấu `<` literal |
| richText | Nhập editor thật→lưu/nộp/duyệt/Return, markup/entity/HTML table; tìm chữ ngắn qua markup; không COUNT_DISTINCT |
| stringList | Ghép khối của report, native readback; khác List object, không lọc từng dòng |
| date | Năm/tháng/ngày thiếu độ chi tiết, khoảng mở; có engine evidence chưa thay API/browser đủ biến thể |
| fullDate | Ngày Việt Nam, cực trị ngày/IF/Apply, không lệch 05/10↔06/10 khi đọc BSON |
| singleSelect | Mã/nhãn, list options theo quyền, catalog bị thu hồi/deactivate, target code/pin |
| shortText cũ | Alias Chọn một; cần sample Form/report cũ đọc–lưu thật; không chuyển thành text tự do |
| multiSelect | ANY/ALL/NONE/EQUAL_SET, Hợp/Giao với trống hợp lệ, đúng mã/nhãn đích |
| boolean | false khác blank, công thức/lọc/render đúng |
| evidence | Không thuộc capability tính tổng hợp; không thêm gom file/đếm file để làm ca “đạt” |
| List object | Sáu kiểu con hiện hỗ trợ; ID ổn định, lineage/collision, cùng dòng, top-N, output/page/detail |
| Table horizontal/vertical/matrix | Chỉ hồi quy engine hiện có và vùng/tọa độ; không mở parity mới. Cell richText có đường adapter khác; cell stringList UNSUPPORTED theo audit trước — xác minh capability và báo riêng |
| Bảng nội dung | Khối/report/đơn vị/page/part/nhãn/note, snapshot đã nộp; chưa nhận xuất Word/Excel toàn bộ là tính năng đã hoàn tất |

Nếu metadata tiến độ/đánh giá hoặc operator cần dùng chưa có capability thật, ghi **chưa hỗ trợ/chưa nối** và chuyển owner; không dựng evaluator/ACL mock, không tự coi dữ liệu thiếu là đã đánh giá hoặc kết quả thành công. HTML Table trong richText không phải native Table.

## 7. Bằng chứng kế thừa và các giới hạn đã biết

| Bằng chứng P05 | Giá trị dùng cho bàn giao | Không suy rộng thành |
|---|---|---|
| [Closeout](PERIODIC_P05_CLOSEOUT_2026_10_07.md) | 103 BE checks, 18 FE Save, 6 decoder, TypeScript/build, 11 readback, browser periodic, job/notification | UAT mới, Yud duyệt UX hoặc mọi datatype |
| [SL1/SL2](PERIODIC_P05_SL1_SL2_IMPLEMENTATION_2026_10_06.md) | 274 API/job/writer, 196 cô lập; 166×200 và nhiều kỳ tối đa 1.992 report | Benchmark độc quyền/SLA hoặc 166×365 |
| [UI cuối](PERIODIC_P05_UI_FINAL_2026_10_06.md) | Chuyển revision/override/reset, đọc text dài/ngày; production Event Timing canvas 88 ms, công thức 120 ms, gõ 16–56 ms | Field INP p75, native IME Windows, phản hồi duyệt mọi màn |
| [Mở Work](PERIODIC_P05_REOPEN_COMPLETION_2026_10_06.md) | Một lần, Work chưa có report, pending/job thật; no-report 24 checks, UI reason-only | Trạng thái host hiện tại hoặc quyền mở lại mọi Work |

Giới hạn phải giữ trong báo cáo UAT:

- Work 166 nhánh ba cấp mất **12,1 giây** để mở lại/phục hồi đồng bộ; 156 queue/materializer đúng khóa, không sinh lặp kỳ. Đây là hạn chế tải đã biết, không gọi là lazy compute của Aggregate. Chưa có SLA Yud chốt; ghi trải nghiệm actual, không tự nới timeout để làm PASS.
- Tổng pool nguồn trước lọc vẫn tối đa 10.000 report; chưa kiểm 60.590 report. List target/top-N theo giới hạn API/contract hiện hành (contract v3: output 200, top-N 1–200); không dùng UI paging để cắt tập tính chính thức.
- Bộ lọc metadata AND/OR phẳng tối đa 32 điều kiện; set tối đa 1.000 giá trị. Thiếu ngày cần thiết báo lỗi hoặc xử lý PRESENT/ABSENT tường minh; không mở nhóm lồng tùy ý.
- Báo cáo chủ động chưa xác nhận identity/create API riêng; không đặt nhãn “chủ động” lên Scheduled để nhận đã test.
- Đổi mẫu A giao B, migration pin/report/mapping, version rollout mới, cache/write-behind, so sánh kỳ trước/lũy kế tương đối: **đã hoãn**, không mở trong UAT này.
- Ba bản snapshot kỹ thuật gồm bản hiện tại dành nháp; không phải backup DB/rollback release. Bản đã nộp phải giữ bất biến/readable theo contract; không purge dữ liệu để thử giới hạn.
- Báo cáo đi qua preview/job/submit một lần không có nghĩa mọi ca mất mạng/kill process/MinIO outage đều đã mô phỏng. Đánh dấu riêng fault injection nào thực sự chạy.

## 8. API/source để định vị lỗi

Chỉ dẫn này để đọc Network và chuyển đúng owner; không thay DTO bằng payload tự đoán. Tham chiếu source đang dùng và capability/token/revision mới mỗi phiên.

| Miền | API/source thật | Owner đầu mối / ranh giới |
|---|---|---|
| Bootstrap/schema/nguồn | POST `/api/aggregate-v2/editor/bootstrap`, `editor/source-schema`, `source-forms/query`, `sources/query`; `AggregateMappingPreviewController.cs` | Lam / Aggregate |
| Cấu hình/ghi nền | POST `/api/aggregate-v2/instances/{id}/mapping/queue-preview`, `mapping/queue`; `/configs/{id}/queue-preview`, `queue`; `AggregateMappingCommandsController.cs` | Lam / Aggregate; writer authoritative bắt buộc |
| Job | POST `/api/aggregate-v2/preview-jobs/current|start`, `/{id}/read|cancel`; `/instances/{id}/computation/status|cancel|retry` | Preview job khác job ghi; preview không ghi payload |
| Đọc/Submit | POST `/api/aggregate-v2/instances/{id}/read`, `/reports/{id}/submission-preview`; lifecycle report dùng controller hiện hành | Lam / Aggregate + owner Report/Work |
| View | POST `/api/aggregate-v2/views/read`, `/binding/preview`, `/binding/apply`; `AggregateViewsController.cs` | Một View/cấp, khác report nộp |
| Dữ liệu dài | POST `/api/aggregate-v2/lists/page|detail|counts`, `/content/page|part` | Quyền/read reference/stale/lineage, không tải full text bằng frontend workaround |
| Hoàn thành/mở lại | GET `/api/works/{id}/completion`, POST `.../completion/reopen`; GET `/api/work-assignments/{id}/completion`, POST `.../completion/requests`, `.../completion/reopen`; `WorkCompletionController.cs` | Owner Work/tiến độ; Lam có phần tích hợp đã giao, không sửa xuyên WIP mới |
| Nhập/lưu report | `tdtd-fe/src/pages/works/report/WorkReportEditorPage.tsx`; runtime/native payload writer dùng chung | Owner Report/Dynamic Form; hai điểm readback sửa cuối đã có test |
| Đồng bộ projection | `tdtd-be/Services/WorkAssignments/Progress/WorkCompletionWorkflowService.cs`, `TryConvergeAsync` | Giữ pending đến sync/projection/CAS thành công; không xóa cờ thủ công |
| UI tổng hợp | `tdtd-fe/src/features/aggregateMapping/` | Lam / Aggregate; lỗi lưu/schema/ACL báo BE, không che bằng UI |

Tên lỗi cần phân biệt: `AGG_INPUT_STALE` (nguồn/quyền/cấu hình đổi), `AGG_COMPUTATION_REQUIRED` (chưa có kết quả hợp lệ để nộp), `AGG_METADATA_DATE_UNAVAILABLE` (mốc metadata cần dùng không có), `AGG_CONTEXT_STALE` (context/revision không còn đúng). Ghi HTTP/status/body có che token; đừng đổi thành 0/rỗng hoặc lặp mutation nếu chưa rõ commit.

## 9. Báo lỗi, tiêu chí chốt UAT và quay lại

Agent ghi kết quả trong **file riêng** `docs/training/aggregate-canvas-v1/handoffs/PERIODIC_APPLICATION_UAT_RESULTS_YYYY_MM_DD.md`; evidence `outputs/periodic-application-uat-<run>/`. Không ghi đè log/manifest P05. Cấu trúc tối thiểu mỗi ca:

| Mã | Build/DB/role | Work/assignment/report/period/job | Trước → thao tác | Expected | Actual | PASS/FAIL/BLOCKED/N/A | Ảnh/API/log | Yud duyệt |
|---|---|---|---|---|---|---|---|---|
| UAxx | Điền sau khi kiểm môi trường | ID thật, không token | Các bước tái hiện | Chốt trước thao tác | Quan sát thực tế | Chưa chạy cho tới khi có bằng chứng | Đường dẫn riêng | Chờ phản hồi thực tế |

Lỗi cần kèm schema hash, payload/lifecycle/config revision khi liên quan; nêu source vs deployed có khớp không, đã commit hay mới pending, khả năng thao tác tiếp của người dùng. Không dừng ở “API PASS” nếu FE không có đường thực hiện; không nhận “browser PASS” khi dữ liệu do mock cung cấp.

Ngưỡng chuyển sửa: mất dữ liệu/lộ quyền/ghi sai report-kỳ/đổi bản đã nộp hoặc sai pin → dừng ca ảnh hưởng, giữ chứng cứ; lỗi UX có thể tái hiện → chuyển đúng component/owner. Business chưa rõ hỏi Yud với ví dụ; không tự đặt chính sách mới. Báo sớm nếu user bị kẹt mà không có FE/BE tương ứng.

Chốt UAT chỉ khi các ca P0 áp dụng có actual và không còn lỗi chặn/mất dữ liệu/quyền; P1 thiếu ghi rõ phạm vi và người chấp thuận để lại. Duyệt UI phải có phản hồi Yud cụ thể, không lấy hướng trình bày đã được duyệt trước làm phê duyệt mọi màn mới. Ngưỡng hiệu năng chưa thống nhất phải ghi đo thực tế, không tự nhận SLA.

Nếu cần dừng bản lỗi: giữ nháp/snapshot/lock/history; dùng cancel/retry/lifecycle có capability để phục hồi, không clear pending hoặc đổi status DB. Không bật lại Basic/Advanced. Rollback binary phải kiểm nó đọc được recipe/schema đang lưu; không xóa config mới hoặc sửa hash để chạy binary cũ. Restart/rollout chung phải phối hợp owner và phạm vi Yud giao.

## 10. Đoạn giao việc ngắn để Yud chuyển tiếp

> Nhận UAT ứng dụng phần Tổng hợp tại `D:/Job/CA/tdtd`. Đọc `docs/training/aggregate-canvas-v1/handoffs/PERIODIC_P05_APPLICATION_UAT_HANDOFF_2026_10_07.md` trước, rồi P05 closeout và các link theo ca. Kiểm build/DB/capability trước runtime; BE chung tại thời điểm bàn giao chưa nạp sửa projection cuối. Giữ WIP và dữ liệu nghiệm thu, dùng fixture riêng cho mutation, không reset/skip migration hoặc mở đường cũ. Đi hai điểm vào View và Tổng hợp báo cáo, hoàn thiện checklist UA01–UA26 theo phạm vi được giao; ghi expected/actual/bằng chứng và màn Yud đã/chưa duyệt. Lỗi hợp đồng/ACL/storage/Work chuyển đúng owner, không tự sửa nghiệp vụ. Không mở cache/đổi mẫu/version rollout/so sánh kỳ. Báo ngay chỗ người dùng không thao tác được, không đánh dấu UAT đạt từ source/mounted/P05 kế thừa. Kết quả ghi file riêng theo mục 9.

Đây là đoạn bàn giao để Yud gửi/điều phối; Lam chưa nhắn agent khác hoặc chạy P06 trong lượt chốt tài liệu này.

## 11. Bổ sung FE canvas sau bàn giao (07/10)

Yud giao sắp xếp thứ tự trường để giảm dây giao cắt và cho phép dây đi dưới nền khối. Đã sửa FE, kiểm 107 ca mục tiêu và đo Event Timing production với 200 trường/200 dây; chưa nhận UAT ứng dụng hoặc Yud duyệt màn mới. Agent đọc [AGGREGATE_CANVAS_FIELD_ORDER_2026_10_07.md](AGGREGATE_CANVAS_FIELD_ORDER_2026_10_07.md) khi chạy UA18/UA26. Kiểm đúng build có thay đổi mới này; không dùng ảnh hoặc số đo fixture dữ liệu giả thay nghiệm thu dữ liệu thật.
