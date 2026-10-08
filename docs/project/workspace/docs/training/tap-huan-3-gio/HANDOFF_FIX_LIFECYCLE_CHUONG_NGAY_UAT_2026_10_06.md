# Sửa luồng giao việc → thông báo → nộp → trả lại → nộp lại → duyệt — 06/10/2026

## Kết quả và giới hạn bàn giao

Đã sửa sản phẩm, nạp bản BE sửa vào API local và UAT bằng ba tài khoản thật sẵn có: PV01, Hạc Thành, Quảng Phú. Đã xác nhận hai báo cáo riêng của hai đơn vị đi đến Đã duyệt; Hạc Thành qua cả vòng trả lại/sửa/nộp lại. Hộp đối chiếu kết thúc sau xác nhận đúng lượt lưu; retry mạng lỗi giữ nguyên command; thông báo từ các chuyển trạng thái đã lưu không mất khi job quét sau lúc duyệt.

**Chưa tuyên bố toàn bộ phạm vi đã được nghiệm thu.** Hạn của dữ liệu UAT cũ chưa sửa; ngày/lịch định kỳ và biên chuyển ngày của tiến độ chưa được nghiệm thu lại; mẫu đo tương tác có lượt chậm, chưa có kết luận INP đạt. Hai tab editor cũ chưa được khôi phục tại chỗ vì chúng không xuất hiện trong inventory trình duyệt của task này. Không đóng tab hoặc bỏ nháp các tab đó. Chưa gửi kết luận PASS cho agent tài liệu.

Quyết định trực tiếp mới nhất của người dùng: “Phải bật chuông chứ. Có trong danh sách việc thì phải bật chuông là hiển nhiên”. Đã áp dụng `WorkInbox:NotifyReviewRequired=true` **chỉ trong appsettings.Development.json của local tập huấn**. Mặc định chung và môi trường khác không đổi. Job vẫn dùng lịch quét hiện có khoảng 5 phút, cửa sổ sắp đến hạn 24 giờ; đây chưa phải thông báo push tức thời.

## Bằng chứng đầu vào, WIP và phạm vi

- Đã đọc `HANDOFF_UAT_GIAO_THONG_BAO_NOP_DUYET_TRA_LAI_2026_10_06.md`, thư mục 19 ảnh UAT gốc, các handoff mới về Công việc/gom nhóm/phân trang, thông báo, tự duyệt, vòng đời và phục hồi báo cáo/P05; đối chiếu source hiện tại. Đã xem ảnh lỗi nộp lại kẹt và ảnh trạng thái sau duyệt.
- Thư mục gốc không phải repo Git; `tdtd-fe` và `tdtd-be` là hai repo riêng, đều có nhiều WIP. Lưu status/diff trước sửa trong `outputs/uat-lifecycle-fix-20261006/{fe,be}-status-before.txt` và `{fe,be}-wip-before.patch`. Patch BE không thu các file upload xóa số lượng lớn trong App_Data. Không nhận toàn bộ diff hiện tại là thay đổi của lượt này.
- Không sửa master/board/issue; không seed/reset DB; không sửa biểu mẫu hoặc báo cáo UAT gốc; không đổi người duyệt, quyền duyệt hoặc bật tự duyệt. Tự duyệt ở các phần việc hồi quy đều Tắt.
- Tạo nhiệm vụ/phần việc hồi quy riêng qua UI thật, dùng nguyên biểu mẫu Mẫu 1 phiên bản 1. Có ghi dữ liệu UAT mới qua các thao tác bình thường; không phải DB không thay đổi. Không tạo thông báo giả, không đổi đồng hồ máy.
- Không commit/push. Không triển khai lên máy chủ LAN. Không động vào các API/host kiểm tra riêng của agent khác.

## Mô hình hiện tại và tên hiển thị

| Đối tượng | Trường nguồn | Điều kiện chuyển | Chủ thể chuyển | Cách diễn đạt |
| --- | --- | --- | --- | --- |
| Báo cáo | WorkAssignmentReport.Status; PayloadRevision, LifecycleRevision; lifecycle outbox/log | Lưu nháp giữ Draft; nộp → Submitted; trả lại → Draft với lý do; nộp lại → Submitted mới; duyệt → Approved | Người nhận sửa/nộp khi contract cho phép; người duyệt hiện hành trả lại/duyệt | Đang nhập; Đã nộp · Chờ duyệt; Cần sửa và nộp lại; Báo cáo đã duyệt |
| Kỳ báo cáo của từng tài khoản | WorkReportPeriod.Status, CurrentReportId và báo cáo hiện hành | Theo projection vòng đời; kỳ trả lại vẫn được biểu diễn bằng contract hiện có | Projection của chuyển trạng thái đã commit | Nhãn kỳ + hạn; nghĩa vụ Cần nộp hoặc Cần sửa và nộp lại, không thêm enum Returned |
| Việc cần làm | Pipeline theo kỳ/assignment/report hiện hành và quyền actor; RequiresAction/ProcessingState | Người nhận còn phải nộp khi Pending/Draft; người duyệt còn việc khi Submitted; trả lại bỏ việc duyệt bản đang sửa; duyệt bỏ việc duyệt tương ứng | Suy ra từ nghiệp vụ, không có thao tác “đọc chuông = hoàn tất” | Chờ duyệt/Cần nộp; số đã xử lý được diễn đạt “không còn chờ duyệt/không còn cần nộp”; trạng thái thật hiển thị từng dòng |
| Thông báo | UserNotification + EventKey, OccurredAtUtc, ReadAtUtc; sự kiện đã lưu trong LifecycleProjectionOutbox.BusinessEvents | Job chuyển sự kiện đã commit thành notification idempotent; đọc chỉ đổi ReadAtUtc | Producer/job; người dùng đọc | Giao việc; Cần duyệt; Trả lại có lý do; Nộp lại; Đã duyệt. Ưu tiên tên phần việc, có thời gian và đường dẫn phạm vi |
| Tiến độ phần việc | WorkAssignment.ProgressStatus; StartDate, deadline, facts kỳ; CompletedAtUtc/CompletedDate | Service tính theo lịch/phạm vi kỳ và xác nhận hoàn thành riêng; duyệt báo cáo không tự hoàn thành | Service projection; người có quyền hoàn thành theo contract hiện có | Dùng cùng getWorkAssignmentProgressStatusLabel trên popup và bảng; tiền tố Tiến độ và tooltip giải thích |
| Tiến độ nhiệm vụ | Work.Status và projection các phần việc gốc | Tổng hợp tiến độ, xác nhận hoàn thành theo contract | Projection/chủ thể được phân quyền, không thay trong lượt sửa này | Chưa bắt đầu/Đang thực hiện/…; khác đối tượng báo cáo |

Mẫu 1 gốc và phần việc hồi quy hai đơn vị bắt đầu 07/10, nhưng UAT ngày 06/10. Vì vậy Work “Chưa bắt đầu”, assignment “Chưa thực hiện” sau duyệt **phù hợp source hiện hành tại thời điểm này**; không tự gán Completed. Popup trước đây suy OPEN thành “Đang thực hiện” là lỗi trình bày, đã chuyển sang ProgressStatus thật. Case bắt đầu hôm nay hiển thị Đang thực hiện trên popup theo đúng nguồn.

Danh sách có nhãn đối tượng đếm: phần việc ở trang gom nhóm; báo cáo hoặc kỳ báo cáo trong popup; tài khoản phối hợp đếm riêng; lịch sử phân trang theo nhóm thông báo. Tổng đầu trang là tổng bước xử lý của bốn nhóm và có tooltip giải thích, không gọi là tổng nhiệm vụ hoàn thành.

## Nguyên nhân và bản sửa

### Hộp đối chiếu kẹt và nháp báo bẩn khi vừa mở

`submitWorkAssignmentReport` invalidates dữ liệu trước khi continuation của promise xử lý xong. InboxReportRoute dùng isFetching để tháo editor khỏi cây. Frozen native save session bị hủy, async scope guard loại completion; editor remount đọc lại pending trong local storage và giữ hộp đối chiếu. Test tái hiện trước sửa thất bại vì editor unmount đúng lúc refetch.

Route giữ cùng editor/key và session trong lúc xác minh lại authority, nhưng ẩn/suspended/readOnly để không tương tác bằng quyền cache. Lỗi scope vẫn đóng quyền truy cập. Sau readback hợp lệ, editor chuyển trạng thái tương ứng, không tự điều hướng làm mất bước xác nhận.

Native save phân biệt sending, reading, uncertain, readback-required, confirmed. Đối chiếu report ID, payload revision/hash, schema và native values với đúng frozen request. Nếu đã ACK, retry chỉ đọc lại; nếu chưa rõ kết quả, gửi lại đúng command ID/payload đã giữ. Không sinh command nộp mới từ nút Tiếp tục. Không luôn đóng hộp, nuốt lỗi hay xóa local draft.

Chỉ clear dirty khi sequence khớp phần đã xác nhận; không clear thay đổi mới hơn. Native local values giống server được xem là sạch. Với biểu mẫu chỉ native, snapshot legacy tương đương không làm xuất hiện “Có thay đổi chưa lưu”; dữ liệu giữ trong storage, không xóa nháp cưỡng bức. Report trả lại vừa mở đã xác nhận trên UI là “Chưa thay đổi”.

### Chuông trả lại, nộp lại và cần duyệt

Producer quét trước đây đọc trạng thái hiện tại Submitted/Approved, nên bỏ qua lịch sử nộp rồi trả lại giữa hai lượt quét; không có nhánh phát trả lại trong đường producer đã kiểm. Đã dùng chính BusinessEvents durable ghi cùng CAS của report, không dựng outbox/hạ tầng mới.

Khóa mới chứa report + EventKey của chuyển trạng thái + recipient; hai SUBMIT có lifecycle khác nhau có khóa khác nhau. Retry cùng event có cùng khóa và dùng cơ chế CreateMany idempotent sẵn có. Giữ tương thích khóa cũ theo timestamp để không phát lại thông báo duyệt đã tồn tại; một khóa legacy chỉ phủ tối đa một chuyển trạng thái. Fallback status chỉ áp dụng báo cáo legacy thiếu event tương ứng, không suy ra toàn bộ lịch sử từ status.

Nộp/nộp lại gửi người duyệt hiện hành; trả lại/duyệt gửi người nhận. Nhánh tự duyệt không sinh việc cần duyệt. Nhãn return/resubmit và định tuyến mới được hỗ trợ trong chuông, history, nhóm và bộ lọc. Đọc thông báo không đổi nghĩa vụ. Sự kiện cũ mở trạng thái hiện tại, kể cả report đã được duyệt trước lúc chuông nộp lại được phát.

Bật config đã cho phép job phát cả một số sự kiện đã commit trước khi nâng cấp. Đây là lịch sử thật với thời gian sự kiện cũ, không phải lượt nộp mới. Qua nhiều lượt job, mỗi khóa lifecycle trong scope kiểm vẫn chỉ có một notification.

### Ngày nghiệp vụ và hạn

FE trước đây tạo cuối ngày UTC; BE lại EndOfUtcDate, nên ngày 07/10 thành `2026-10-07T23:59:59.999Z` và hiện 06:59 ngày 08/10 tại Việt Nam.

Luồng giao một lần mới: picker ngày `2026-10-07` → cuối ngày `+07:00` → request/lưu `2026-10-07T16:59:59.999Z`; BE giữ thời điểm thay vì đẩy về cuối ngày UTC. StartDate/Work calendar-date giữ token ngày, không bị cộng giờ trên popup. Định dạng instant trong các màn liên quan dùng Asia/Ho_Chi_Minh. Không trừ 7 giờ ở mọi nơi và không sửa giá trị readback để che dữ liệu cũ.

Case mới đã thấy nhất quán trên màn giao việc, kỳ, editor, cần làm và chuông. Bản ghi gốc vẫn sai hạn theo quyết định nghiệp vụ 07/10 và chưa được sửa dữ liệu; xem mục riêng bên dưới.

### Sửa bổ sung nhỏ phát hiện trong UAT

- Lỗi BE giao trùng biểu mẫu trước đây chỉ toast phía sau dialog. Đưa message vào ngay dialog và giữ bản đang nhập. Đã UAT lại với lỗi thật `WORK_ASSIGNMENT_TEMPLATE_REUSE_CONFLICT`, ảnh 28; không tạo bản ghi trùng và không bỏ validation.
- Sau tạo assignment, Work header có thể giữ status cache cũ dù BE đã chiếu tiến độ mới. Mutation thành công nay invalidates đúng Work id. Nhánh này đã qua source/typecheck/build; chưa tạo thêm một assignment mới để chứng minh riêng cache invalidation trên UI sau chỉnh cuối.

## Runtime và cấu hình đã áp dụng

- FE dev Vite ở localhost:5173 sử dụng source/HMR; production build cũng thành công nhưng UAT là dev runtime.
- Chỉ dừng PID local cũ 34420 sau khi kiểm executable path; thay DLL/PDB đã build riêng vào `tdtd-be/bin/Debug/net8.0`, giữ apphost/deps/runtimeconfig tương thích. API mới PID 77180, Development, HTTP 5164/HTTPS 7443. Không dừng host kiểm tra riêng của agent khác.
- Backup DLL/PDB và config trước áp dụng trong `outputs/uat-lifecycle-fix-20261006/runtime-before/`. Không chia sẻ nội dung config vì chứa cấu hình kết nối.
- Config thay: `appsettings.Development.json`, `WorkInbox.NotifyReviewRequired=true`. `appsettings.json` và mặc định thiếu key (=false theo GetValue<bool>) không đổi. AutoApprove và quyền người duyệt không đổi. DueSoonHours=24 và lịch job hiện có giữ nguyên.
- `apply-local-runtime.ps1` là bản ghi thao tác một lần với PID cũ cố định, không phải script có thể chạy lại tùy ý. Nếu rollback cần kiểm PID/path/config hiện tại trước; không reset code hoặc dữ liệu.

## Dữ liệu và UAT thực tế

Phạm vi gốc được bảo toàn: Work `6ac40f855e204bc3cab298b5`; assignment `6ac411b45e204bc3cab29b4a`; HT report `6ac413bf5e204bc3cab29dee`; QP report `6ac470a1066462d646de278a`. Cả hai vẫn Approved và giữ payload/lifecycle revisions của snapshot đầu vào.

Vòng hồi quy mới (tạo qua UI):

- Work `6ac47a3ee237dd6def37b498` — `[UAT hồi quy 06/10] Vòng giao–nộp–trả lại–duyệt`.
- Assignment `6ac47a8de237dd6def37b6dc` — `[UAT hồi quy] Mẫu 1 — hai đơn vị`; start 07/10, hạn 07/10 23:59 Việt Nam; tự duyệt Tắt.
- Form giữ nguyên `6ac40b8e5e204bc3cab29496`, FORM-pv01-2026-000006 v1.
- HT period `6ac47abce237dd6def37b6f8`, report `6ac47adde237dd6def37b707`; cuối luồng Approved, PayloadRevision=4, LifecycleRevision=4.
- QP period `6ac47abce237dd6def37b6fa`, report `6ac47b51e237dd6def37b748`; cuối luồng Approved, PayloadRevision=2, LifecycleRevision=2.

| Case UI thật | Kết quả quan sát | Ảnh trong evidence/uat-lifecycle-fix-2026-10-06/ |
| --- | --- | --- |
| Giao cho hai đơn vị | Đúng người nhận, dùng cùng form, tự duyệt Tắt | 02, 03 |
| Hạc Thành mở chuông giao | Đúng phần việc/kỳ; đọc chuông không mất việc cần nộp | 04, 05 |
| HT nhập → lưu nháp → mở lại | Mã UAT-REG-HT-01, kinh phí 1100000, kết quả 6/10; đọc lại đủ và sạch | 06 |
| HT nộp với network latency 1500 ms | Gửi/readback chậm; hộp kết thúc, báo cáo chỉ xem | 07 |
| QP mở báo cáo riêng, nhập bảng/List → nộp trực tiếp | Không có dữ liệu HT; mã UAT-REG-QP-01, 2200000, kết quả 8/8 | 08, 09, 12 |
| QP offline khi gửi → phục hồi mạng → Tiếp tục đối chiếu | Giữ nội dung; hai request có cùng commandId và body; chỉ một SUBMIT đã commit | 08, 09 |
| PV01 nhìn hai bản nộp | 2 báo cáo, 1 kỳ, 2 tài khoản; đọc đúng dữ liệu từng đơn vị | 10, 12 |
| Trả HT có lý do | HT về Draft, bỏ việc duyệt bản đang sửa; QP còn chờ duyệt | 11 |
| Chuông cần duyệt | Hai sự kiện nộp thật, nhóm 2; số chưa đọc giảm tương ứng khi mở | 13 |
| HT nhận chuông trả lại | Có lý do, thời gian; mở đúng nghĩa vụ sửa/nộp lại, đọc không làm mất nghĩa vụ | 14, 15 |
| HT mở report trả lại chưa chỉnh | “Chưa thay đổi”, lý do đang yêu cầu sửa, còn dữ liệu 6/10 | 16 |
| HT sửa bảng/List và nộp lại trực tiếp | 10/10, giải thích 4 hồ sơ; kết thúc đối chiếu và chỉ xem | 17 |
| PV01 đọc bản nộp lại | Có nhãn Nộp lại; xem đúng kết quả 10/10 và giải thích mới | 18, 19 |
| PV01 duyệt cả hai | Bộ lọc chưa xử lý không còn báo cáo trong scope; mọi trạng thái có hai Đã duyệt | 20, 21, 29 |
| Nộp lại rồi duyệt trước job quét | Job vẫn phát riêng sự kiện nộp lại đã commit, không lẫn nộp đầu | 22 |
| HT mở chuông đã duyệt | Đúng report, chỉ xem; lý do cũ thành “Lịch sử trả lại ở lượt trước” | 23, 24 |
| QP mở chuông đã duyệt | Đúng report QP, giữ 8/8 và 2200000, chỉ xem | 27 |
| Giao việc bị trùng | Lỗi hiện trong dialog, dữ liệu đang nhập giữ lại, không tạo trùng | 28 |

Command HT nộp đầu: `99a0d799-1514-4528-821f-7495d6e66c15`. QP offline/retry: `21af92fa-e81d-4646-99a6-f4b4f9014054`. HTTP capture đã bỏ request auth/header; buffer capture không liên tục hoàn hảo nên không dùng nó làm log toàn bộ phiên. Hai request QP cần đối chiếu được giữ trong `uat-http.json`; outbox/read audit xác nhận chỉ một SUBMIT của QP.

ACK HT nộp lại lưu tại `hacthanh-resubmit-ack.json`: đúng report, status=Submitted, PayloadRevision=4, LifecycleRevision=3, lifecycle=COMMITTED, projectionPending=false, canEditPayload=false, canSubmit=false. Sau đó reviewer đọc đúng dữ liệu mới trước khi duyệt.

### Case sắp đến hạn thật

Work riêng `6ac47e95e237dd6def37be7b` — `[UAT hạn 06/10] Kiểm cửa sổ sắp đến hạn`; assignment `6ac47eb5e237dd6def37be99` — `[UAT hạn] Sắp đến hạn hôm nay`; period HT `6ac47eede237dd6def37beb1`. Start 06/10, hạn `2026-10-06T16:59:59.999Z` = 23:59 hôm nay. Tạo qua UI, form cũ giữ nguyên. Job thật lúc 11:55 gửi REPORT_DUE_SOON. Chuông mở đúng phạm vi, đọc vẫn còn một kỳ cần nộp/sắp tới hạn; popup tiến độ Đang thực hiện. Ảnh 23, 25, 26.

**Cố ý để case này chưa nộp** để tập huấn còn thao tác. Vì vậy không tuyên bố mọi queue toàn hệ thống bằng 0. Trước đó thử tạo cùng form trong Work hồi quy bị BE chặn reuse conflict; giữ nguyên ràng buộc, tạo Work độc lập qua UI để có case hạn hôm nay.

## Kiểm tra source/test/build, tách khỏi UAT

Logs tại `outputs/uat-lifecycle-fix-20261006/`.

- FE test hồi quy trước sửa: `route-repro-before.log` chứng minh editor bị unmount khi authority refetch.
- FE bộ cuối: 7 file / 99 test, gồm mounted inbox route, native save host, editor runtime safety, WorkInbox UX, grouping/pagination, date/presentation, create dialog input. Lệnh: `node node_modules/vitest/vitest.mjs run tests/workInbox.mounted.test.tsx tests/WorkInboxUx.test.tsx tests/InboxReportGroups.test.tsx tests/reportLifecyclePresentation.test.tsx tests/WorkAssignmentCreateDialog.input.test.tsx tests/canvasWorkspace/NativeReportScope.nativeHost.test.tsx tests/dynamicForms/WorkReportEditorRuntimeSafety.test.tsx --reporter=dot`. Xem `fe-regression-final5.log`; bộ trước đổi nhãn cuối cũng 99/99 tại final3.
- TypeScript FE: `node node_modules/typescript/bin/tsc -b --pretty false`; lint tập file sửa; Vite production build. Xem các log final2/final3 và final5. Final4 từng vấp assertion nhãn cũ “phần báo cáo”, đã cập nhật expectation theo nhãn rõ “báo cáo”, không bỏ kiểm tra chức năng.
- BE build .NET 8 riêng obj/output: 0 error, 92 warnings hiện có. Lượt build shared obj đầu bị CS2012 file đang khóa; build cô lập giải quyết, không dừng process agent khác. Xem `be-build-final.log`.
- `tests/ReportLifecycleNotificationChecks`: 18 checks thuần về event order, recipients, stable keys, hai lần nộp, legacy dedup, autoapproval, thiếu commit không sinh event và giữ UTC instant.
- Cờ `--deadline-read-query` chạy chính BSON expression tính hạn của sản phẩm qua Mongo `$documents`, collectionless/read-only: trước cửa sổ, đúng 24h, trong cửa sổ, đúng hạn, quá hạn, Submitted/Approved không còn nghĩa vụ nộp. Tổng 25 checks; `be-boundaries.log`. Không lưu dữ liệu mô phỏng vào DB và không cho job phát notification từ các case này.
- Cờ `--uat-read-audit` chỉ đọc dữ liệu thật trong scope nêu trên. `uat-read-audit.log` và `uat-read-audit-repeat.log`: original và new lifecycle notifications mỗi khóa chỉ một bản; trạng thái/revisions/due phù hợp UI sau nhiều scan. Đây là bổ sung cho UI, không thay nghiệm thu UI.
- `git diff --check` trong các file liên quan không có whitespace error; Git có cảnh báo LF/CRLF. Không kiểm/reset sạch toàn repo do WIP chung.

Kiểm slow network/ack mismatch/uncertain retry/không clear newer edit có test riêng. UI thật đã kiểm request và readback chậm cùng lúc, offline/retry, direct submit và resubmit. Chưa mô phỏng trên UI trường hợp server đã commit nhưng chỉ mất response ACK, chỉ riêng readback đứt mạng, đóng trình duyệt giữa commit hoặc nhiều tab tranh sửa; các phần đó không được gắn nhãn UI PASS.

## Hiệu năng tương tác

Đã thao tác chuông, popup, đổi lọc/áp dụng trên IAB viewport hiện tại và Vite dev. Mẫu PerformanceEventTiming trong `interaction-sample.json` có 9 interaction ID; duration lớn nhất khoảng 3096 ms, lượt khác 1184 ms, các lượt còn lại 16–176 ms. Mẫu nhỏ, không có attribution/production trace, không đủ để quy nguyên nhân hoặc chứng nhận INP p75.

Không gọi một lần click nhanh là INP đạt. Cần đo tiếp trên build production với cùng dữ liệu/phạm vi, gắn attribution cho chuông/popup/Áp dụng, tách mạng/render/long task, rồi sửa đúng điểm nghẽn. Không flush cache/reset DB/đổi dữ liệu để tạo số đẹp.

## Hạn cũ: phạm vi ảnh hưởng và cách xử lý đề xuất, chưa thực thi

Read-only audit đếm 13 assignments, 179 periods, 13 reports có pattern UTC cuối ngày `23:59:59.999Z`. Đây chỉ là **ứng viên**, không chứng minh tất cả sai 7 giờ. Những bản ghi có thời điểm cụ thể hợp lệ phải giữ nguyên.

Đối với Mẫu 1 gốc đã có quyết định ngày 07/10, allowlist ban đầu để rà kế hoạch sửa là:

- Assignment `6ac411b45e204bc3cab29b4a`.
- Period HT `6ac411c15e204bc3cab29b6d`, QP `6ac411c15e204bc3cab29b6f`.
- Report HT `6ac413bf5e204bc3cab29dee`, QP `6ac470a1066462d646de278a`.
- Expected old DueAtUtc `2026-10-07T23:59:59.999Z`; proposed corrected instant `2026-10-07T16:59:59.999Z`, nếu được cho phép sửa các bản ghi đã duyệt này.

Trước khi apply: đọc lại đủ revision/current bindings, so sánh chính xác expected old value, kiểm các trường derived LatestDueAtUtc và đường projection/audit được phép; xuất bản sao chỉ các record trong allowlist; lập diff dry-run trước/sau rồi mới chốt thao tác. Dừng khi revision/quan hệ khác snapshot. Không sửa payload/status, không tạo SUBMIT/APPROVE giả, không xóa lịch sử notification. Sau sửa phải kiểm lại tất cả màn và nghĩa vụ/hạn, không cập nhật mỗi assignment làm period/report lệch.

Chưa có script migration apply và chưa sửa dữ liệu cũ. Không áp dụng phép trừ bảy giờ cho 205 ứng viên. Cần quyết định phạm vi sửa lịch sử và cách ghi audit trước khi thực hiện.

Ngày bắt đầu/hoàn thành dạng calendar-date vẫn theo contract lưu token ngày. `WorkAssignmentProgressService.ResolveAssignmentStartUtc` hiện lấy token ngày để so với now UTC; vùng biên 00:00–07:00 Việt Nam cần kiểm/chốt trong phạm vi ngày/lịch chung. Không nhận lần sửa DueAtUtc ONCE là đã sửa toàn bộ lịch định kỳ/P05, Work due-date hay tất cả phép so sánh calendar-date của hệ thống.

## Danh sách file có thay đổi của lượt này

Các file có sẵn có thể chứa WIP trước đó. Danh sách dưới chỉ chỉ ra vị trí có hunk của lượt này, không phải quyền sở hữu toàn file.

FE (`tdtd-fe/`):

- `src/features/workInbox/InboxReportRoute.tsx`
- `src/pages/works/report/WorkReportEditorPage.tsx`
- `src/features/dynamicForms/runtime/useNativeReportValues.ts`
- `src/utils/workDates.ts`
- `src/utils/notificationUi.ts`
- `src/api/workAssignmentApi.ts`
- `src/api/contracts/workReadContracts.ts`
- `src/components/common/CommonDateText.tsx`
- `src/components/works/assignments/WorkAssignmentCreateDialog.tsx`
- `src/components/works/assignments/WorkAssignTab.tsx`
- `src/components/works/assignments/WorkAssignmentTable.tsx`
- `src/components/works/review/WorkReviewTab.tsx`
- `src/features/workInbox/workInbox.types.ts`
- `src/features/workInbox/workInboxPresentation.ts`
- `src/features/workInbox/InboxAssignmentDialog.tsx`
- `src/features/workInbox/InboxStatusChip.tsx`
- `src/features/workInbox/InboxReportGroupDialog.tsx`
- `src/features/workInbox/InboxPagination.tsx`
- `src/features/workInbox/WorkInboxPage.tsx`
- `src/features/workInbox/workInbox.routes.ts`
- `src/features/workInbox/inboxFilters.ts`
- `tests/workInbox.mounted.test.tsx`
- `tests/canvasWorkspace/NativeReportScope.nativeHost.test.tsx`
- `tests/WorkInboxUx.test.tsx`
- `tests/InboxReportGroups.test.tsx`
- `tests/reportLifecyclePresentation.test.tsx` (mới)

BE (`tdtd-be/`):

- `Services/WorkInbox/WorkReportNotificationEvents.cs` (mới)
- `Services/WorkInbox/WorkInboxNotificationJob.cs`
- `Services/WorkInbox/WorkInboxService.cs`
- `Services/WorkInbox/WorkInboxNotificationGroups.cs`
- `Services/WorkInbox/WorkInboxPipeline.cs`
- `DTOs/WorkInbox/WorkInboxDtos.cs`
- `Services/WorkAssignments/WorkAssignmentService.cs`
- `appsettings.Development.json`
- `tests/ReportLifecycleNotificationChecks/ReportLifecycleNotificationChecks.csproj` (mới)
- `tests/ReportLifecycleNotificationChecks/Program.cs` (mới)

Không sửa `WorkAssignmentProgressService`, lịch P05 hoặc contract completion; chúng chỉ là source đối chiếu. Test create dialog input/editor runtime safety có sẵn đã chạy, không nhận là test mới của lượt này.

## Chạy lại và bàn giao tiếp

1. Giữ hai tab editor UAT cũ. Nếu tiếp cận được chúng, ghi nhận report/command/revision và nội dung đang giữ trước; thử Tiếp tục đối chiếu trên bản sửa, không bỏ nháp hoặc tạo lượt nộp mới chỉ để thoát hộp.
2. PV01 mở Work hồi quy `6ac47a3ee237dd6def37b498`, Công việc cần thực hiện → Duyệt báo cáo → chưa xử lý phải trống; Mọi trạng thái có 2 báo cáo đã duyệt, 1 kỳ, 2 tài khoản. HT/QP vào từ chuông đã duyệt chỉ được xem đúng bản của mình.
3. Với vòng ghi dữ liệu mới, tạo Work hồi quy riêng qua UI, dùng form hiện có và hai đơn vị. Không trả lại hay sửa hai báo cáo gốc để làm mới một case. Không bypass quy tắc reuse của form trong cùng Work.
4. Case hạn hôm nay còn mở như trên; dùng để tập huấn hoặc kiểm theo thời gian thật. Ranh giới trước/trong/sau 24 giờ hiện được chứng minh bởi expression test, chưa chờ qua ngày trên UI. Nếu đã qua 06/10, đọc trạng thái quá hạn thật của chính case, không đổi deadline/đồng hồ để quay về ảnh cũ.
5. Kiểm riêng response ACK bị mất sau commit, readback-only failure, recovery tab cũ, tiến độ tại biên ngày Việt Nam, lịch định kỳ, cache Work sau tạo mới, quyền reviewer thay đổi và chi phí job trên dữ liệu lớn trước khi tuyên bố nghiệm thu rộng hơn.
6. Chốt kế hoạch dữ liệu hạn cũ và đo hiệu năng còn mở. Chỉ sau khi UAT/phạm vi tồn đọng được xác nhận mới gửi agent tài liệu các bước và ảnh đã kiểm để viết Markdown + HTML. Tách tài liệu người dùng khỏi ghi chép kỹ thuật này.

Ảnh thật 01–29 nằm trong `evidence/uat-lifecycle-fix-2026-10-06/`; ảnh 29 là nhãn đếm/trạng thái cuối sau sửa diễn đạt. Các ảnh trước 29 ghi đúng UI tại thời điểm chụp, có một số nhãn tổng “Đã xử lý” trước lần tinh chỉnh cuối, không phải ảnh dựng.
