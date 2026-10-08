# Hoàn thiện luồng giao–nộp–trả lại–duyệt và tương tác — 06/10/2026

## Phạm vi và quyết định mới nhất

Tiếp nối `HANDOFF_FIX_LIFECYCLE_CHUONG_NGAY_UAT_2026_10_06.md` và bằng chứng UAT gốc. Quyết định trực tiếp mới nhất của người dùng: “Phải bật chuông chứ. Có trong danh sách việc thì phải bật chuông là hiển nhiên”, sau đó “Hoàn thiện. Không cần quan tâm dữ liệu cũ. Inp lâu quá phải chú ý sửa”.

- Chuông cần duyệt đã bật cho Development local: `WorkInbox.NotifyReviewRequired=true`. Không đổi mặc định toàn hệ thống, tự duyệt, quyền reviewer, DueSoonHours=24 hoặc lịch job.
- Loại việc sửa/migrate hạn cũ khỏi phạm vi bàn giao theo chỉ đạo mới. Không cập nhật hàng loạt, sửa ngày hoặc trạng thái các báo cáo gốc.
- Không seed/reset DB, đổi form, sửa master/board/issue, commit/push, triển khai LAN hay đóng/bỏ nháp hai tab UAT cũ. Hai tab cũ không xuất hiện trong inventory browser khả dụng; không nhận là đã khôi phục chúng.
- Bảo toàn WIP chung. HEAD đối chiếu: FE `14edd2a`, BE `0ca2e93`; working tree có nhiều thay đổi/untracked từ trước. Snapshot status trước/sau trong `outputs/uat-inp-fix-20261006/`. Mỗi file liệt kê dưới có thể chứa hunk của agent khác.

## Mô hình giữ nguyên

| Đối tượng | Nguồn | Điều kiện/chủ thể | Diễn đạt |
| --- | --- | --- | --- |
| Report | Status, PayloadRevision, LifecycleRevision, nhật ký/outbox | Người nhận lưu/nộp; reviewer hiện hành trả lại/duyệt theo quyền server | Đang nhập, chờ duyệt, cần sửa/nộp lại, đã duyệt |
| Kỳ của tài khoản | Period.Status, CurrentReportId, report hiện hành | Projection sau chuyển trạng thái đã commit | Nhãn kỳ, hạn và nghĩa vụ của đúng tài khoản |
| Việc cần làm | RequiresAction/ProcessingState từ nghiệp vụ và scope hiện hành | Nộp → cần duyệt; trả lại → cần sửa/nộp lại; duyệt → hết nghĩa vụ tương ứng | Đếm kỳ/báo cáo/phần việc/tài khoản riêng; “không còn chờ duyệt” không thay cho “đã duyệt” |
| Thông báo | Sự kiện durable đã ghi, EventKey, OccurredAtUtc, ReadAtUtc | Job idempotent, người dùng đọc | Sự kiện có thời gian, ưu tiên tên phần việc, mở phạm vi/trạng thái hiện tại |
| Tiến độ | Work.Status, Assignment.ProgressStatus và ngày/facts theo service hiện có | Service projection và xác nhận hoàn thành theo contract | Tiến độ nhiệm vụ/phần việc, không tự nối Approved → Completed |

Trả lại vẫn là Draft + lịch sử/lý do theo contract hiện có. Lý do cũ sau duyệt được trình bày là lịch sử. Không thêm enum hoặc mô hình chấm điểm. Mô hình tiến độ rộng hơn đang được audit riêng; bản sửa này chỉ thống nhất nguồn hiển thị và chuyển calendar-date sang instant đúng tại chỗ so sánh.

## Nguyên nhân và sửa sản phẩm

### Đối chiếu lượt nộp

Nguyên nhân chính đã sửa ở handoff trước: invalidation của submit làm route tháo editor trước khi promise hoàn tất, phá phiên frozen save. Route giữ cùng editor/key khi xác minh lại quyền, ẩn và suspended trong thời gian đó. Hook lưu có pha gửi/chưa rõ kết quả/đã ACK cần đọc lại/đã đối chiếu; xác minh đúng report, schema, payload revision/hash và native values.

Lượt hoàn thiện bổ sung:

1. Báo cáo có thể đã chuyển chỉ xem khi ACK bị mất. Nút khôi phục không còn bị chặn chỉ vì `forceReadOnly`; vẫn chặn khi suspended, preview hoặc đường native chưa được hỗ trợ. Chỉ gửi frozen request trong storage tách theo account/report, giữ nguyên command ID, revision và nội dung. API vẫn kiểm quyền người nhận hiện hành, trạng thái, CAS và replay command đã commit. Không mở lại chỉnh sửa hoặc tạo SUBMIT mới.
2. Lượt lưu được khôi phục từ lần mount trước có sequence không thể so với sequence hiện tại. Sau đối chiếu, chỉ bỏ dấu chưa lưu chung khi không có thay đổi mới trong lúc chờ, không có Excel chưa xác minh/lỗi nháp, và toàn bộ fields/lý do chậm/ngày hoàn thành hiện tại khớp response. Nếu còn Excel hoặc phần nháp khác chưa xác minh, giữ dấu và dữ liệu đó. Test đã tái hiện chặn rời trang trước sửa và kiểm cả hai nhánh sau sửa.

### Tương tác chuông, popup và bộ lọc

- Gõ nháp bộ lọc trước đây render lại tất cả card báo cáo dù dữ liệu server không đổi. Memo card và callback ổn định; dữ liệu/refetch/scope mới vẫn cập nhật bằng tham chiếu mới, không có custom comparator bỏ qua thay đổi.
- Mở popup trước đây cũng render lại toàn bộ trường lọc. Memo InboxFilterBar, giữ callback Áp dụng/Xóa lọc ổn định theo đúng draft và URL hiện hành.
- Chuông dựng lại danh sách khi mở/đóng. Giữ menu được mount trong trạng thái ẩn và memo nội dung; query/realtime/unread tiếp tục cập nhật. Bỏ thời gian exit của chuông để lớp phủ đóng không nuốt cú click tiếp theo. Không đọc thông báo tự động khi mở/đóng.
- Popup dựng cả bảng/phân trang bị vô hiệu hóa ngay lúc đang tải; khi mở từ cache còn dựng dòng cũ rồi tháo ra khi mandatory refetch bắt đầu. Hiện tiêu đề/phạm vi và trạng thái tải trước, dựng bảng/phân trang sau read hiện hành. Không hiển thị action của dòng cache trong khoảng chờ xác minh. Giữ phân trang/cursor và kiểm lỗi/quyền cũ.

### Ngày nghiệp vụ

Handoff trước đã sửa giao một lần: ngày 07/10 → `2026-10-07T16:59:59.999Z` → 23:59 ngày 07/10 tại Việt Nam; BE không kéo ngược về cuối ngày UTC.

Lượt này bổ sung `WorkAssignmentProgressService`: ngày bắt đầu/kết thúc dạng calendar-date được đổi sang đầu/cuối ngày Việt Nam tại chỗ so sánh với nowUtc. UTC deadline thực giữ nguyên. Schedule generator vẫn nhận calendar key; không dịch ngày key thành ngày UTC trước đó. Không đổi đồng hồ, cập nhật ngày cũ hoặc nối duyệt báo cáo với hoàn thành phần việc. Các biên nửa đêm được kiểm bằng test với clock truyền vào hàm, chưa chờ qua nửa đêm thật trên UI.

## UAT thật đã xác nhận

### Vòng hai đơn vị (giữ bằng chứng 01–29 của handoff trước)

Work `6ac47a3ee237dd6def37b498`, assignment `6ac47a8de237dd6def37b6dc`, dùng Mẫu 1 đang có, không sửa schema:

- Hạc Thành: nhập → lưu nháp → mở lại → nộp có mạng chậm → trả lại có lý do → mở thấy lý do và “Chưa thay đổi” → sửa bảng, nộp lại trực tiếp → reviewer đọc đúng 10/10 và giải thích 4 hồ sơ → duyệt.
- Quảng Phú: dữ liệu riêng, bảng riêng, nhập → nộp trực tiếp; offline/retry giữ đúng một command → reviewer đọc đúng 8/8 → duyệt.
- Chuông giao/cần duyệt/trả lại/nộp lại/đã duyệt đúng người, sự kiện và scope. Đọc chuông không bỏ nghĩa vụ. Lý do cũ là lịch sử sau duyệt. Cả hai chỉ xem sau nộp/duyệt.
- Cuối luồng: 2 báo cáo đã duyệt, 1 kỳ, 2 tài khoản; scope này không còn báo cáo ở bộ lọc chưa xử lý. UI bản production cuối được kiểm lại với hai báo cáo này.

### Mất ACK sau commit, sau đó mất riêng readback (lượt hoàn thiện)

Dùng case UAT hạn hôm nay đã tạo qua UI trước đó, không sửa hạn để tạo kết quả. Work `6ac47e95e237dd6def37be7b`, assignment `6ac47eb5e237dd6def37be99`, kỳ `6ac47eede237dd6def37beb1`; hạn giữ `2026-10-06T16:59:59.999Z`.

1. Hạc Thành nhập dòng `UAT-ACK-LOST-01`, tên `[UAT] Khôi phục phản hồi nộp bị mất`, kết quả “Đã đối chiếu 12/12 hồ sơ. Giữ nguyên dữ liệu khi mất phản hồi sau commit.”; nộp trực tiếp qua UI.
2. CDP chặn đúng response XHR sau server xử lý. Response thật HTTP 200, report `6ac48f018b193ca620881c66`, payload=2, lifecycle=1, COMMITTED, projectionPending=false, canEditPayload=false, canSubmit=false. Sau đó mới làm mất response; không tạo response giả hoặc sửa dữ liệu server.
3. UI giữ pending. Bấm Tiếp tục đối chiếu: request byte-for-byte giống lần đầu, command `b2e0a76b-232a-4446-b1e3-7fc551ec1c42`, replay trả HTTP 200.
4. Cho ACK đến trình duyệt rồi cắt riêng GET đọc lại. UI hiển thị Network Error/Thử lại, giữ ACK và frozen session.
5. Bỏ chặn mạng, Thử lại → Tiếp tục đối chiếu. Trace chỉ có GET report và GET sections, không POST submit. Hộp đóng; người nhận chỉ xem; dữ liệu còn đủ.
6. PV01 thấy một thông báo cần duyệt lúc 13:03. Mở chuông → đúng scope; đọc chuông xong vẫn có 1 chờ duyệt. Đọc đúng dòng 12/12, duyệt qua UI. Chọn Đã nộp: Tổng 0.
7. Hạc Thành thấy chuông Đã duyệt lúc 13:09, mở đúng scope và report chỉ xem. Thông báo Sắp đến hạn 11:55 vẫn là lịch sử; nghĩa vụ hiện tại không còn cần nộp.

Lưu ý bằng chứng: thử reload khi pending đã bị guard chưa lưu chặn, không tính là UI proof cho remount mất ACK. Nhánh remount/read-only và bỏ đúng dấu chưa lưu được kiểm bằng mounted editor test. Không bỏ guard hoặc xóa nháp để ép reload. Case hạn hôm nay nay đã được nộp/duyệt trong UAT này, không còn để mở cho bài thực hành sắp đến hạn. Ảnh sắp đến hạn trước nộp vẫn hợp lệ tại thời điểm chụp.

Read-only Mongo audit 13:13: ba report UAT đều Approved; report mạng lỗi chỉ có một SUBMIT và một REVIEW_APPROVE; mọi cặp event/recipient kiểm có Count=DistinctEvents=1. Hai lần SUBMIT của Hạc Thành ở vòng trả lại có lifecycle 1 và 3 riêng. Không đụng record để kiểm này.

## Đo tương tác

Production preview, PV01, viewport 1280×720, tab visible. Không reset DB/cache để tạo PASS. Baseline 7 nhóm/9 báo cáo; sau UAT mạng lỗi danh sách tự tăng thành 8 nhóm/10 báo cáo. Trước/sau cùng máy, viewport và vai trò; dataset sau lớn hơn một nhóm, không phải thí nghiệm trên dữ liệu tuyệt đối giống nhau.

| Tương tác | Baseline CPU 4× | Bản cuối CPU 4× | Bản cuối tốc độ máy thực |
| --- | --- | --- | --- |
| Gõ bộ lọc | 184–288 ms; 17 interaction có Event Timing | 48–88 ms; 30 interaction | 16 ms ở 26 interaction được ghi (dưới 16 ms không được observer ghi) |
| Mở chuông | 168 ms | 88–96 ms, 3 lượt | 16–24 ms, 3 lượt |
| Mở popup | 296 ms | 176–192 ms, 3 lượt | 32–40 ms, 3 lượt |
| Chọn Chưa xử lý | 192 ms | 96 ms | Không dùng mẫu cuối để kết luận riêng thao tác này |
| Áp dụng | 216 ms | 112 ms | Không dùng mẫu cuối để kết luận riêng thao tác này |

Baseline tốc độ máy thực: chuông 72 ms, popup 88 ms. Mẫu cuối tốc độ thật có 37 interaction ID, max 40 ms, không long-animation-frame được ghi. Mẫu cuối CPU 4× có 45 interaction ID, max 192 ms; vẫn có long-animation-frame sau tác vụ/đọc dữ liệu, lớn nhất 305 ms. Không gọi đây là đạt INP field p75: đây là Event Timing có attribution input-delay/processing/presentation trên các thao tác local, threshold 16 ms, chưa có phân bố nhiều người dùng/thiết bị. CPU 4× là mô phỏng chậm CPU của tab, không đổi clock máy.

Lưu đầy đủ các vòng trung gian, gồm spike 328/368 ms đã dẫn tới sửa tiếp; không chỉ giữ mẫu đẹp. File cuối: `production-accepted-normal.json`, `production-accepted-cpu4x.json`, `performance-summary.json`; script `summarize-performance.mjs` nhóm theo interactionId để không đếm đôi keydown/keypress. CPU profiles baseline giữ trong outputs. Resource observer của lượt baseline ghi API khoảng 48–146 ms; tool wall-clock không phải INP hoặc thời gian API. Mẫu Vite dev cũ 3096 ms chưa tái hiện đúng nguyên nhân, không trình bày là đã đo trực tiếp 3096 → 40 ms.

## Kiểm tra source/test/build

- FE suite 7 file: 102 tests pass trước hai kiểm bổ sung cuối. Sau sửa popup/filter: 62 tests pass trong 4 file liên quan (bao gồm cached reopen); sau sửa khôi phục: 35 tests pass trong 2 file editor/native. Tổng 104 test khác nhau trong 7 file liên quan, gồm 7 test trình bày lifecycle giữ nguyên.
- TypeScript build, ESLint các file sửa, Vite production build; xem log cuối trong thư mục output. Không lấy kết quả test làm bằng chứng UI.
- BE build output riêng: 0 errors, 92 warnings hiện có. 28 pure checks + 7 expression checks thực trên Mongo `$documents` không ghi collection = 35 pass. 10 pure checks mới bao phủ biên ngày tiến độ, ngày kế thừa schedule và không convert UTC hai lần.
- Read-only completion audit lưu `completion-audit.log`; trace mạng không lưu auth header/token.
- Git diff --check theo file liên quan; không reset hoặc làm sạch repo chung.

## Runtime và cấu hình

API Development local đang chạy từ `tdtd-be/bin/Debug/net8.0/tdtd-be.exe`, PID sau cập nhật 80084, HTTP 5164/HTTPS 7443. DLL đã kiểm và DLL runtime cùng SHA256 `745849D0A996099AAA08C3A9E4637643A50FA933CB84353F5C5546C7D80E9DFD`. Chỉ thay DLL/PDB của đúng API sau kiểm PID/path/hash; khóa Windows giải phóng chậm ở lần copy đầu, đã copy lại thành công rồi mở đúng host. Không dừng các host aggregate/test của agent khác.

FE dev 5173 dùng source/HMR. Đo bằng production preview riêng 4176, proxy API local; không sửa cấu hình CORS, auth hoặc Vite chung. Temporary observer, CPU throttle, network interception đều được dọn sau kiểm. Không thay đồng hồ máy. Preview kiểm thử được dừng khi bàn giao; build giữ trong outputs để chạy lại.

`WorkInbox.NotifyReviewRequired=true` của Development giữ nguyên quyết định đã chốt. Không có cấu hình nào còn chờ người dùng duyệt trong phạm vi này. Default toàn hệ thống vẫn như trước; autoapprove và reviewer rights không đổi. Dữ liệu cũ không còn là stop gate.

## File sửa thêm ở lượt hoàn thiện

Ngoài các file đầy đủ đã liệt kê trong handoff trước:

- FE `src/features/workInbox/WorkInboxPage.tsx`: memo card, callback nhóm/lọc.
- FE `src/features/workInbox/InboxFilterBar.tsx`: memo bộ lọc.
- FE `src/features/workInbox/InboxReportGroupDialog.tsx`: chờ read mới, tránh dựng bảng cache rồi tháo; phản hồi tải.
- FE `src/components/notifications/NotificationBell.tsx`: giữ menu ẩn, memo danh sách, bỏ lớp phủ chặn click lúc đóng.
- FE `src/pages/works/report/WorkReportEditorPage.tsx`: phục hồi frozen command sau chuyển chỉ xem; bỏ đúng dấu chưa lưu đã xác minh.
- FE `tests/InboxFilters.test.tsx`, `tests/InboxReportGroups.test.tsx`, `tests/dynamicForms/WorkReportEditorRuntimeSafety.test.tsx`: hồi quy không render card thừa, vẫn nhận dữ liệu mới, cache không dùng action cũ, khôi phục read-only và bảo toàn Excel chưa xác minh.
- BE `Services/WorkAssignments/Progress/WorkAssignmentProgressService.cs`: biên calendar-date Việt Nam cho progress.
- BE `tests/ReportLifecycleNotificationChecks/Program.cs`: thêm kiểm biên ngày và read-only audit scope UAT mới.

## Bằng chứng và chuyển tài liệu

Thư mục kỹ thuật: `outputs/uat-inp-fix-20261006/`. Không đưa config runtime, token hoặc ghi chép lỗi kỹ thuật vào tài liệu cán bộ.

Ảnh thật bổ sung trong `evidence/uat-hoan-thien-inp-2026-10-06/`:

- 30: pending khi mất ACK thật.
- 32: lỗi chỉ ở đọc lại.
- 33: đối chiếu xong, người nhận chỉ xem bản đã nộp.
- 34: một chuông cần duyệt của case mạng lỗi.
- 35: người duyệt đọc đúng dữ liệu 12/12.
- 36: bộ lọc Đã nộp sau duyệt Tổng 0.
- 37: người nhận chỉ xem báo cáo đã duyệt.
- 38: chuông Đã duyệt cùng lịch sử Sắp đến hạn/giao việc.
- 39: popup cuối hai đơn vị, 2 báo cáo đã duyệt, 1 kỳ, 2 tài khoản.
- 40: scope hai đơn vị ở bộ lọc Chưa xử lý, tổng 0 và không có phần việc phù hợp.

Chuyển agent tài liệu bước/ảnh đã kiểm để cập nhật Markdown và HTML, tách hướng dẫn người dùng khỏi ghi chép kỹ thuật. Chỉ gọi phạm vi manual report/Mẫu 1/hai đơn vị trên đây là đã UAT; không suy sang toàn bộ P05/Flow/tổng hợp, tất cả lịch định kỳ, quyền reviewer thay đổi giữa request hoặc quy tắc hoàn thành Work.

Để chạy lại: dùng Work UAT mới qua UI, cùng form đang có, hai đơn vị; không trả lại/sửa báo cáo gốc chỉ để mở lại case. Giữ notifyReviewRequired của local bật. Nhập/lưu/mở/nộp, trả lại có lý do, nộp lại/duyệt, kiểm dữ liệu hai bên và scope chờ về 0. Với lỗi mạng, chỉ ngắt request/response của tab kiểm thử, lưu đúng command/hash/revision, khôi phục mạng rồi kiểm số event bền vững. Biên 24 giờ và nửa đêm dùng test xác định; không đổi giờ hoặc phát thông báo giả.
