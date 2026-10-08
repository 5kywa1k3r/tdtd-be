# Contract theo dõi tiến độ Work — assignment — report

Ngày 06/10/2026, cập nhật theo quyết định trực tiếp của người dùng sau audit; bổ sung lượt chốt từ 14:56 UTC+07 về duyệt hoàn thành và hạn duyệt. **Quyết định mới nhất “Chốt. Xử lý” đã cho phép triển khai source.** Phần lõi BE/UI đã thực hiện ở lượt 15:04–15:50; kết quả, phạm vi chưa tích hợp và UAT còn lại tại [handoff triển khai lõi](HANDOFF_TRIEN_KHAI_LOI_THEO_DOI_TIEN_DO_2026_10_06.md). Chưa triển khai host hoặc thay dữ liệu UAT. Đánh giá/chấm điểm tiếp tục hoãn.

Tài liệu này thay thế các lựa chọn nghiệp vụ chưa chốt và các đề xuất trái chiều trong [audit ban đầu](AUDIT_THEO_DOI_TIEN_DO_WORK_ASSIGNMENT_REPORT_2026_10_06.md). Audit tiếp tục là bằng chứng về code ở thời điểm đọc, không phải contract đích. Quyết định mới của người dùng ưu tiên hơn proposal, handoff và log PASS cũ.

## 1. Chỗ code đang dùng chung tiến độ và thời hạn

“Dùng chung” nghĩa là **một trường chứa các giá trị thuộc nhiều trục nghiệp vụ**, không phải Work và assignment dùng cùng một enum.

| Đối tượng / trường | Các giá trị cùng nằm trong trường | Bằng chứng source đọc lại ngày 06/10 |
| --- | --- | --- |
| Work, `Work.Status` | S1/S2/S3 mô tả chưa bắt đầu/đang thực hiện/hoàn thành; S4/S5 mô tả nguy cơ quá hạn/quá hạn | `tdtd-be/Models/Work.cs:6–12,50–51`; tên hiển thị `tdtd-fe/src/types/work.ts:119–124`. Work detail lấy một nhãn từ trường này tại `tdtd-fe/src/pages/works/WorkDetailPage.tsx:407`. |
| Assignment, `ProgressStatus` | NotStarted=0, InProgress=1, Completed=2, AtRiskOverdue=3, Overdue=4 | `tdtd-be/Enum/WorkAssignmentProgressStatus.cs:3–9`; cột **Tiến độ** nhận nguyên trường này tại `tdtd-fe/src/components/works/assignments/WorkAssignmentTable.tsx:494–500`; nhãn ở `tdtd-fe/src/types/reportStatus.ts:54–59`. |
| Kỳ báo cáo, `WorkReportPeriod.Status` | Pending/Draft/Submitted/Approved và bốn giá trị ghép OverduePending/OverdueDraft/OverdueSubmitted/OverdueApproved | `tdtd-be/Enum/WorkReportPeriodStatus.cs:7–16`; nhãn ở `tdtd-fe/src/types/reportStatus.ts:37–45`. |
| Hàm tính và truyền lên Work | Khi có kỳ đến hạn chưa Approved, `ProgressStatus` chuyển sang AtRiskOverdue hoặc Overdue, thay cho InProgress; Work sau đó nhận S4/S5 từ các count này | `tdtd-be/Services/WorkAssignments/Progress/WorkAssignmentProgressService.cs:113–135`; `tdtd-be/Services/WorkAssignments/Runtime/WorkAssignmentStatusSyncService.cs:292–299`. |

Report tự thân vẫn chỉ có Draft/Submitted/Approved; không báo nhầm rằng report dùng chung enum với Work/assignment. Hiệu lực IsActive/IsDeleted cũng đã có trường riêng. Contract yêu cầu phân biệt ý nghĩa nghiệp vụ, chưa phê duyệt cách tách enum/field hay migration cụ thể.

## 2. Quy tắc đã chốt

### C01 — Bắt đầu khi báo cáo đầu tiên được duyệt

- Mốc bắt đầu thực hiện là khi **báo cáo đầu tiên thuộc phạm vi báo cáo của đối tượng đang theo dõi được duyệt**.
- Không dùng ngày bắt đầu dự kiến, mở màn hình, lưu nháp, nộp chưa được duyệt hoặc chỉ giao xuống làm mốc này.
- Giao xuống phải có báo cáo lên. Không cần một luồng “nhận việc/bắt đầu thủ công” để thay thế điều kiện đã chốt.
- Duyệt trước ngày bắt đầu dự kiến vẫn là sự kiện bắt đầu; không giữ NotStarted chỉ vì lịch chưa tới.
- Phạm vi là báo cáo cấp hiện tại. Báo cáo của một nhánh sâu chưa được báo lên/duyệt tại cấp này không thay thế báo cáo cấp hiện tại.
- **Lần duyệt đầu tiên là mốc đã xảy ra:** trả lại/thu hồi duyệt về sau không xóa lịch sử bắt đầu, không tự đưa tiến độ về “chưa từng thực hiện”. Trạng thái report hiện hành vẫn thay đổi theo lifecycle; không dùng report bị trả lại để tiếp tục tính là đã duyệt đủ.

### C02 — Work owner hoàn thành; assignment owner xin cấp trên duyệt hoàn thành

- **Work là cấp cao nhất. Chủ Work có quyền hoàn thành Work**, không phải xin thêm một cấp phía trên Work.
- **Owner assignment muốn hoàn thành assignment của mình thì gửi yêu cầu lên cấp quản lý trực tiếp**, kèm lý do.
- Assignment có cha: người duyệt yêu cầu là owner/cấp quản lý của assignment cha. Assignment root: người duyệt là **owner Work**.
- Luồng nghiệp vụ: **gửi yêu cầu + lý do → chờ cấp trên duyệt → chấp thuận thì hoàn thành; từ chối thì chưa hoàn thành theo yêu cầu đó**. Gửi yêu cầu không tự chuyển Completed.
- Lưu lý do và lịch sử yêu cầu/quyết định: ai yêu cầu, đối tượng/phạm vi yêu cầu, ai duyệt, thời điểm, kết quả. Trạng thái xử lý yêu cầu là trục riêng, không thay thế tiến độ assignment hay trạng thái report.
- Một người là owner assignment A có quyền quản lý các assignment con do cấp A phụ trách, nhưng việc hoàn thành chính A phải theo cấp quản lý phía trên A. Một người nhận trong assignment nhiều người nhận không được tự đóng assignment chung.
- **Hai đường hoàn thành:** người có thẩm quyền quyết định theo luồng này, hoặc hệ thống tự đóng khi đủ điều kiện C03. Không bắt yêu cầu thủ công phải đi qua toàn bộ điều kiện tự đóng rồi mới cho cấp trên quyết định; việc cho hoàn thành được ghi bằng lý do và quyết định duyệt.
- Quyết định hoàn thành không biến các report còn nháp/chưa duyệt thành Approved và không xóa lịch sử nghĩa vụ còn lại.

**Sai lệch hiện tại đã xác nhận bằng source:** `WorkAssignmentService.cs:898` dùng `CanConfigureDataSourceRules`; helper tại :1845–1851 cho current reviewer **hoặc bất kỳ assignee**. Nhánh cho assignee tự kết thúc trái với quyết định mới, phải sửa sau khi duyệt kế hoạch kỹ thuật. Không chỉ sửa/ẩn nút FE.

Model còn phân biệt `CreatedByUserId` (người giao gốc/lịch sử) và `CurrentReviewerUserId` (trách nhiệm duyệt sau bàn giao), theo `tdtd-be/Models/WorkAssignment.cs:103–107`. Khi lập kế hoạch phải ánh xạ owner/cấp quản lý của quan hệ hiện hành và handover; không lấy người tạo lịch sử hoặc một role chung thay cho cấp duyệt nêu trên. Contract này không cấp một quyền bàn giao/ủy quyền mới. Luồng yêu cầu duyệt hoàn thành là yêu cầu nghiệp vụ mới, chưa có triển khai/UAT trong lượt này.

### C03 — Đủ báo cáo và hết hạn thì tự hoàn thành

- Áp dụng cho báo cáo một lần và định kỳ: **toàn bộ báo cáo bắt buộc của cấp hiện tại đã được duyệt**, và **đã tới/hết hạn kết thúc của Work/assignment đang xét**, thì tự chuyển hoàn thành.
- “Toàn bộ” theo nghĩa vụ của lịch giao và người nhận thuộc phạm vi hiện hành; không đồng nghĩa mọi row report/period tình cờ đã sinh trong DB.
- Với định kỳ, hạn kết thúc là mốc kết thúc phần việc/lịch cần theo dõi, không phải hạn một kỳ giữa lịch còn tiếp tục.
- Nếu đủ báo cáo nhưng chưa hết hạn thì chưa tự hoàn thành theo quy tắc thời hạn; đây là trường hợp an toàn, **không cảnh báo thiếu/chậm báo cáo chỉ vì chưa có thao tác kết thúc thủ công**.
- Hết hạn và đủ báo cáo: hoàn thành tự động, không yêu cầu người giao xác nhận lại và không cảnh báo “chưa hoàn thành” cho trường hợp này.
- Hết hạn nhưng chưa đủ báo cáo được duyệt: chưa đạt điều kiện hoàn thành tự động. Trễ nộp/trễ duyệt/quá hạn thực hiện thể hiện theo C07, không lấy deadline đơn lẻ để tự hoàn thành.
- Job phải có mốc hạn kết thúc hợp lệ để áp dụng điều kiện này; không tự phát minh một mốc cho lịch không có ngày kết thúc.

Quy tắc không cho phép coi tập báo cáo rỗng do lỗi materialize/binding là “đã duyệt hết”. C02 là đường quản lý xét duyệt hoàn thành; C03 là đường tự động khi đã hoàn thành nghĩa vụ báo cáo và hết hạn, không yêu cầu tạo thêm yêu cầu duyệt hoàn thành. Trong cả hai đường phải giữ nguyên trạng thái/lịch sử report thực tế.

### C04 — Mỗi cấp chịu trách nhiệm bằng báo cáo của chính cấp đó

- **Cha là cấp quản lý trực tiếp của con. Work là cấp cao nhất**; assignment root thuộc quyền quản lý của owner Work. Không tách cha khỏi trách nhiệm quản lý con và không đặt thêm cấp phê duyệt phía trên Work.
- Cấp trên của cha chỉ cần nhận và duyệt báo cáo của cha. Khi báo cáo cấp hiện tại đã đạt/được duyệt, không bắt cấp trên tiếp tục kiểm trạng thái các báo cáo cấp con của cha.
- Không bắt buộc mọi assignment con/grandchild Completed hoặc mọi report bên dưới Approved để công nhận báo cáo và tiến độ hợp lệ ở cấp hiện tại.
- Không kéo trạng thái xấu của một nhánh sâu lên ghi đè việc cấp hiện tại đã đạt bằng báo cáo được duyệt. Trạng thái sâu vẫn có thể được xem để theo dõi cấp dưới, nhưng không là điều kiện chặn cấp trên.
- Báo cáo cấp hiện tại được duyệt không tự duyệt, xóa nghĩa vụ hay sửa lịch sử báo cáo cấp con. Đây là độc lập trách nhiệm theo cấp, không phải cascade Approved xuống dưới.

Ví dụ **PV01 → A → B**: PV01 duyệt báo cáo A thì A được ghi nhận theo báo cáo A. PV01 không cần chờ B xong. A vẫn là cấp quản lý B và có quyền theo dõi, mở khóa/trả báo cáo B theo C06. Hoạt động đó không mặc nhiên hủy kết quả PV01 đã duyệt của A.

Về yêu cầu hoàn thành trong ví dụ: owner B gửi yêu cầu/lý do lên owner A; nếu A là assignment root thì owner A gửi yêu cầu/lý do lên owner Work (PV01 trong ví dụ). Owner Work tự quyết định hoàn thành Work. Work theo dõi nghĩa vụ ở tầng giao trực tiếp của mình; không quét mọi tầng sâu để đặt thêm điều kiện chặn theo trạng thái report con.

### C05 — Loại con ngừng hiệu lực, giữ lịch sử và lý do

- Con ngừng hiệu lực được loại khỏi tập con/nhánh đang hoạt động dùng để theo dõi và các điều kiện liên quan.
- Giữ lịch sử cùng lý do ngừng hiệu lực; inactive không được đổi thành Completed để né guard và không bị xóa mất dấu vết.
- Không dùng con inactive để tiếp tục chặn hoàn thành cha. Đồng thời C04 còn loại việc kiểm bắt buộc mọi nhánh con kể cả active nếu cấp hiện tại đã đạt bằng báo cáo của chính mình.

### C06 — Cấp trên có quyền mở khóa rồi trả lại để quản lý cấp dưới

- Reviewer là cấp trên chịu trách nhiệm tổng hợp, theo dõi cấp dưới; **mở khóa rồi trả lại** là nghiệp vụ hợp lệ.
- Không áp dụng đề xuất cũ “khóa cứng mọi reviewer mutation sau khi scope hoàn thành”.
- Khi báo cáo của chính cấp trên đã đạt, việc quản lý lại nhánh bên dưới không tự làm mất kết quả đã đạt của cấp trên và không bắt cấp cao hơn quan tâm lại nhánh đó.
- Phải phân biệt thao tác mở khóa phần việc/phạm vi xử lý với recall duyệt và return report. Không mặc định coi ba thao tác là một trạng thái.
- Quyền nghiệp vụ này phải được thực hiện nhất quán với completion job, report current/version, projection và Aggregate; không coi đường reviewer đi qua guard thiếu là đã có đủ luồng mở khóa.
- **Đã đồng ý bảo vệ luồng mở khóa rồi trả lại:** job tự hoàn thành không được đóng chen vào giữa hai bước hoặc ghi đè bằng snapshot cũ. Sau khi báo cáo bị trả lại, nó không còn thỏa điều kiện đã được duyệt để tự đóng.
- Cấp cha quyết định xử lý cấp con trong quyền quản lý của mình. Việc dừng/phục hồi queue, job, guard sau hoàn thành phải bám đúng phạm vi mà cấp quản lý đóng/mở; khi mở để xử lý lại con thì con phải thực sự xử lý được. Không suy chỉ từ một báo cáo được duyệt rằng mọi nhánh đều tự dừng hoặc tự chạy lại.

### C07 — Tách tình trạng thời hạn

Phân biệt rõ:

1. **Nộp muộn**: quan hệ giữa mốc nộp thực tế và hạn nộp.
2. **Chờ duyệt quá hạn/trễ duyệt**: **hạn duyệt bằng hạn assignment**. Nghĩa vụ xử lý của reviewer dùng mốc này, không quy thành người nộp nộp muộn.
3. **Phần việc quá hạn**: quan hệ giữa tiến độ thực hiện và hạn kết thúc phần việc.

Không dùng thời điểm job/projection chạy để thay cho thời điểm sự kiện đã xảy ra. Không tạo thêm SLA duyệt hoặc cộng thêm số ngày sau hạn assignment. Chi tiết các lần trả lại/duyệt đã có lịch sử để đối chiếu; không mở thêm bài toán phân bổ trách nhiệm, trừ thời gian hay chấm điểm từ chuỗi lịch sử đó. Mapping trường hạn đang lưu và cách hiển thị sẽ trình trong kế hoạch kỹ thuật.

**Không cảnh báo khi đã đủ báo cáo và hoàn thành an toàn:** vẫn giữ lịch sử nộp muộn nếu từng xảy ra, nhưng không tiếp tục cảnh báo rằng phần việc còn chưa hoàn thành. Lịch sử trễ và cảnh báo hành động hiện tại phải được phân biệt.

### C08 — Luôn yêu cầu báo cáo

- Chốt contract **việc được giao luôn yêu cầu báo cáo**. Bỏ đề xuất bổ sung loại assignment “không yêu cầu báo cáo”.
- Nếu cần theo dõi đơn vị, có thể thêm lãnh đạo vào phạm vi theo dõi theo quyền phù hợp; không vì vậy bỏ nghĩa vụ báo cáo.
- Việc tham gia để theo dõi không tự cấp quyền kết thúc, duyệt hoặc sửa báo cáo ngoài thẩm quyền của quan hệ giao việc.

### C09 — Không mở phạm vi đánh giá/chấm điểm

Không thiết kế điểm, tiêu chí, trọng số hoặc phần trăm hoàn thành. Số báo cáo/đơn vị/kỳ phải có đối tượng và phạm vi rõ; không quy tỷ lệ Approved thành điểm hay phần trăm tiến độ.

## 3. Ma trận hành vi nghiệp vụ đích

| Tình huống | Kết quả theo quyết định mới |
| --- | --- |
| Chưa có báo cáo nào được duyệt ở cấp hiện tại | Chưa ghi nhận bắt đầu theo mốc duyệt; thời hạn vẫn theo dõi riêng. |
| Báo cáo đầu tiên được duyệt trước ngày dự kiến | Ghi nhận bắt đầu; không giữ NotStarted vì lịch tương lai. |
| Báo cáo đã từng được duyệt bị trả lại | Giữ mốc bắt đầu đã xảy ra; report hiện hành thay đổi, không còn được đếm là Approved cho điều kiện tự đóng. |
| Một trong hai người nhận có báo cáo đầu tiên được duyệt | Assignment chung đã bắt đầu; từng nghĩa vụ của người còn lại vẫn theo dõi riêng. Không ai nhận có quyền tự đóng cả assignment. |
| Mọi báo cáo một lần/định kỳ của cấp hiện tại đã duyệt, chưa hết hạn | An toàn về nghĩa vụ báo cáo; chưa tự hoàn thành do deadline chưa tới, không cảnh báo cần hoàn thành thủ công. |
| Mọi báo cáo đã duyệt, đã hết hạn kết thúc | Tự hoàn thành, không cần bấm kết thúc và không cảnh báo cho case an toàn này. |
| Hết hạn nhưng còn nghĩa vụ chưa được duyệt | Không đủ điều kiện auto-complete; phân loại đúng trễ nộp, chờ duyệt, quá hạn thực hiện. |
| Báo cáo cha được cấp trên duyệt nhưng báo cáo con chưa xong | Không dùng nhánh con để phủ nhận kết quả/tiến độ theo báo cáo cha. |
| Con ngừng hiệu lực | Loại khỏi phạm vi active; giữ lịch sử và lý do. |
| Owner Work hoàn thành Work | Được quyền quyết định ở cấp cao nhất, không xin thêm cấp trên Work. |
| Owner assignment có cha muốn hoàn thành | Gửi yêu cầu kèm lý do lên owner/cấp quản lý của assignment cha; chỉ chấp thuận mới hoàn thành theo yêu cầu. |
| Owner assignment root muốn hoàn thành | Gửi yêu cầu kèm lý do lên owner Work để duyệt. |
| Yêu cầu hoàn thành đang chờ hoặc bị từ chối | Không tự hoàn thành theo yêu cầu đó; hệ thống vẫn xét đường tự đóng độc lập nếu đủ C03. |
| Người nhận bỏ qua luồng duyệt và gọi Complete assignment của mình | Không được phép; đây là lỗi logic của đường quyền hiện tại cần sửa. |
| Cấp trên mở khóa rồi trả lại báo cáo nhánh dưới | Là thao tác hợp lệ theo authority; không tự làm mất phê duyệt báo cáo của chính cấp trên. |
| Job auto-complete chạy lúc cấp trên đang mở khóa/trả lại | Không được đóng chen/ghi đè luồng quản lý; phải đọc lại đúng trạng thái sau thao tác. |
| Xét quá hạn duyệt | Dùng hạn assignment; lịch sử duyệt/trả lại phục vụ đối chiếu chi tiết. |
| Có nhu cầu theo dõi mà không muốn làm báo cáo | Không tạo loại giao việc miễn báo cáo; có thể đưa lãnh đạo vào phạm vi theo dõi phù hợp. |

## 4. Đối chiếu audit cũ và tác động bắt buộc khi lập kế hoạch

| Kết luận/đề xuất cũ | Quyết định mới | Tác động / việc còn phải chứng minh |
| --- | --- | --- |
| Bắt đầu theo ngày hoặc cân nhắc lưu/nộp làm mốc | Báo cáo đầu tiên được duyệt | Phải thay công thức progress và các trigger tương ứng; F05 chuyển trọng tâm sang sự kiện duyệt + mốc tự kết thúc, không làm job ngày bắt đầu theo đề xuất cũ. |
| Completion manual; cân nhắc cho mọi assignee | Work owner có quyền hoàn thành Work; assignment owner gửi yêu cầu/lý do lên cấp cha hoặc Work owner nếu root; đủ báo cáo và hết hạn thì tự đóng | F08 là lỗi quyền; bổ sung luồng duyệt hoàn thành. Rà cả API/FE/helper/job/replay và lịch sử quyết định, không chỉ đổi nhãn. |
| Cân nhắc mọi child/period subtree phải đủ mới kết thúc cha | Chỉ báo cáo của cấp hiện tại quyết định tại cấp đó | Loại ràng buộc subtree không phù hợp; sửa phối hợp progress, readiness, Aggregate participant, snapshots/counts và cache. Không xóa trạng thái cấp dưới. |
| F03 đề xuất khóa cứng reviewer sau complete | Cấp trên được mở khóa rồi trả lại | Không dùng audit cũ để siết reviewer. Cần có luồng mở khóa đúng authority và thống nhất job/projection/lock; sự thiếu guard hiện tại không tự chứng minh luồng này đã hoàn chỉnh. |
| Inactive child cần lựa chọn policy | Loại active scope, giữ lịch sử và lý do | F04 không còn câu hỏi chọn loại trừ hay không. Đồng thời không để các guard subtree còn sót trái C04. |
| Duyệt hết vẫn cần thao tác hoàn thành; cảnh báo khi chưa bấm | Hết hạn và duyệt hết thì tự hoàn thành, không cảnh báo | Sửa auto-completion/scheduling và cách biểu diễn case an toàn; không auto-complete giữa lịch định kỳ còn tiếp tục. |
| Bổ sung luồng không yêu cầu report | Luôn yêu cầu report | Bỏ đề xuất và ca UAT mở loại no-report; giữ khả năng theo dõi lãnh đạo riêng quyền mutation. |
| Trễ là nhãn chung, chưa có căn cứ hạn duyệt | Tách trễ nộp / trễ duyệt / quá hạn thực hiện; hạn duyệt = hạn assignment | F12 tiếp tục cần sửa mốc thời gian; không dùng clock projection làm event time, không thêm SLA hoặc cách tính trách nhiệm qua lịch sử. |
| Chưa rõ trả lại có xóa mốc bắt đầu không | Mốc duyệt đầu đã xảy ra và được giữ | Không recompute NotStarted chỉ vì hiện không còn report Approved. |
| Mở khóa có thể bị job tự đóng lại | Bảo vệ trọn luồng mở khóa/trả lại đã được đồng ý | Tính nhất quán command/job phải được kiểm trong kế hoạch và UAT; không đổi quyền cấp cha. |

Các lỗi kỹ thuật F01/F02/F06/F09/F10/F11/F13 trong audit vẫn cần kiểm và xử lý theo contract mới: cạnh tranh ghi Work, snapshot rỗng, cache, quyền hiện hành, legacy date, completeness kỳ và retry/hội tụ. Không suy việc chốt nghiệp vụ đã sửa được những lỗi này.

### Ranh giới job, tổng hợp và lịch sử

- Completion thủ công và tự động phải có cùng kết quả hợp lệ đối với status, dấu kết thúc, queue/materialize, projection, DocRole và cache. Job không được ghi đè ngược thao tác vừa mở khóa/trả lại của cấp trên do dùng snapshot cũ.
- Quy tắc “duyệt hết” phải đọc đúng current/version, phạm vi người nhận, lịch và hiệu lực; kiểm đủ kỳ, không đếm toàn bộ bản nháp/lịch sử hoặc chỉ các row đã sinh.
- Handoff P05 hiện ghi source còn owner bị chặn recall (`PERIODIC_P05_LIFECYCLE_2026_10_06.md:47`), recall Approved→Submitted vẫn frozen (:51), return giải phóng đúng owner (:52–53). Đây là **điểm tương tác phải thiết kế lại/đối chiếu**, không được lấy PASS cũ để từ chối quyền nghiệp vụ mới của cấp trên.
- Đồng thời, quyền mở khóa không cho phép sửa mất bằng chứng đã duyệt: phải giữ lịch sử/frozen snapshot/pin của báo cáo cấp trên đã đạt khi xử lý lại nhánh dưới. Cách chọn version/owner-lock và lệnh mở khóa cụ thể chưa được duyệt trong lượt này; không tự phá khóa toàn bộ hoặc sửa engine.
- **Phân công mới nhất:** người dùng sẽ báo agent tổng hợp bổ sung phần khóa/phiên bản Aggregate. Lượt này chỉ ghi contract giao tiếp và giữ ranh giới; không sửa engine, không gửi chỉ đạo sang agent đó và không tự chọn giải pháp version/lock thay owner.
- Không tự mở lại hay thu hồi duyệt các cấp cao hơn vì nhánh dưới thay đổi. Nếu có thay đổi report ở chính cấp hiện tại, phải xử lý lifecycle của report đó đúng authority thay vì suy từ báo cáo cấp con.

### Phần nghiệp vụ không mở lại thành câu hỏi

- Ai quyết định hoàn thành: theo C02, gồm luồng yêu cầu/lý do/duyệt của assignment và quyền cao nhất của Work owner.
- Nếu không đi đường quyết định thủ công: đủ các kỳ báo cáo đã duyệt và hết hạn thì tự đóng theo C03.
- Cha quản lý con, Work cao nhất; mở khóa/trả lại nhánh dưới không tự hủy kết quả báo cáo của cha đã được cấp trên duyệt.
- Hạn duyệt bằng hạn assignment; chi tiết các vòng xử lý đã có lịch sử.
- Giữ mốc bắt đầu đã xảy ra và dừng cảnh báo chưa hoàn thành cho case hoàn thành an toàn.

Những việc còn lại là thiết kế kỹ thuật và kiểm chứng: mapping owner/quyền hiện hành, nơi lưu yêu cầu/lý do/lịch sử, command/job đồng thời, phiên bản/khóa Aggregate do owner bổ sung, queue/projection/cache và migration dữ liệu cũ. Không suy việc đã chốt contract là đã duyệt các bước triển khai này.

## 5. Mẫu 1 theo contract mới và giới hạn triển khai

Work `6ac40f855e204bc3cab298b5`; assignment `6ac411b45e204bc3cab29b4a`. Với bằng chứng handoff hai report Hạc Thành và Quảng Phú đã được PV01 duyệt ngày 06/10:

- **Expected mới:** đã bắt đầu/đang thực hiện từ báo cáo đầu tiên được duyệt, dù ngày bắt đầu dự kiến là 07/10.
- Khi đã duyệt đủ toàn bộ nghĩa vụ cấp hiện tại, case này an toàn; chưa cần bấm hoàn thành hoặc cảnh báo vì thiếu xác nhận thủ công.
- Khi đến/hết hạn kết thúc hợp lệ thì tự hoàn thành. Không sửa ngày sai đang lưu của fixture để ép kết quả trong lượt chốt contract này.
- Trạng thái NotStarted hiện tại từng giải thích được bằng source cũ; **không còn là expected nghiệp vụ sau quyết định này**.

Đã đọc lại status/diff mục tiêu ngày 06/10/2026 từ 14:33 UTC+07. HEAD BE `0ca2e93cfe404450a304c1a5dc1941f6d4752fd1`, FE `14edd2ad1b62cc53f1515deb6ebb23ada2dbefd8`; có WIP đồng thời (status 276 mục BE, 262 mục FE tại đầu lượt đối chiếu này). Không coi số mục tăng là do tài liệu này hay đồng nhất HEAD với working tree.

Lượt bổ sung contract từ **14:56:23 UTC+07 ngày 06/10/2026** đọc lại tài liệu và status/diff mục tiêu: BE 280 mục, FE 265 mục. Có các agent đang làm WIP; lượt này chỉ sửa contract và phần chỉ dẫn quyết định mới trong audit, không thay source theo các diff đó. Không tái sử dụng kết luận hash của audit đầu ngày để nói toàn bộ source hiện tại chưa đổi.

**Stop gate của lượt chốt tài liệu 14:56 đã được thay bởi “Chốt. Xử lý”.** Source và build/test cô lập được thực hiện ở lượt sau, xem handoff. Vẫn không restart host, API/DB write, thay UAT, chạy job/repair trên dữ liệu thật, sửa master/board/issue hay giao agent khác. Các ca UAT cũ phải đổi expected theo contract này trước khi chạy; chưa tuyên bố runtime/UAT đạt.
