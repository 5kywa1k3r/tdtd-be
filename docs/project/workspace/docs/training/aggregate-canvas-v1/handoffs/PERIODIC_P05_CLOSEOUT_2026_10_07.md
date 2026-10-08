# P05 — Chốt triển khai và kiểm chứng một lần/định kỳ

Cập nhật: 07/10/2026, lượt kiểm cuối khoảng 01:05 giờ Việt Nam. Người thực hiện: Lam. Căn cứ yêu cầu trực tiếp của Yud: **“Ừ, làm nốt để chốt P05.”**

> **Đã chốt gói chuyển agent UAT:** [PERIODIC_P05_APPLICATION_UAT_HANDOFF_2026_10_07.md](PERIODIC_P05_APPLICATION_UAT_HANDOFF_2026_10_07.md) — checklist UA01–UA26, baseline, tài khoản/đường mở, gate môi trường và mẫu báo lỗi. Gói này kế thừa bằng chứng dưới đây; không tự ghi UAT/P06 đạt hoặc đã nạp bản sửa vào host chung.

**Kết luận: chốt kỹ thuật P05 trong phạm vi báo cáo một lần và định kỳ đã duyệt.** Các khoảng cuối về mở lại Work, nhiều kỳ, đồng bộ trạng thái Review, phục hồi hàng trăm nhánh và thông báo đã được kiểm như dưới đây. Bằng chứng mutation cuối chạy trên DB/host fixture riêng, qua API, writer, Mongo và worker thật. Không coi kết quả này là Yud đã duyệt UX hoặc nghiệm thu mọi màn của toàn sản phẩm. **Không mở P06.**

**Trạng thái triển khai cần phân biệt:** bản BE có sửa cuối đang chạy tại **5308**; FE fixture tại **5307**. Host chung **5164** đang chạy binary Dashboard được mở lúc 00:44, trước bản sửa projection cuối; không nhận host đó đã có sửa cuối chỉ vì source đã sửa. Giữ host chung đang hoạt động, bàn giao việc nạp lại cùng phiên bản source mới nhất trong lần restart phối hợp tiếp theo. Đây là việc đưa bản sửa vào host chung còn lại, không phải lỗi nghiệp vụ chưa có phương án.

## 1. Nguồn quyết định và bằng chứng nối tiếp

Giữ các quyết định trực tiếp của Yud: mở cha chỉ bỏ khóa kế thừa; khóa riêng của con giữ nguyên; không thêm Hủy mở lại; Work quá hạn mở để sửa được xét hoàn thành lại sau khi báo cáo được chọn sửa được duyệt lại; không đổi mẫu, cache hoặc version rollout trong lượt này.

Các tài liệu trước được giữ nguyên lịch sử; dòng “chưa đóng P05” và các gap đã được chứng minh ở tài liệu này được thay thế về trạng thái hiện tại, không sửa ngược bằng chứng cũ:

- [SL1/SL2: lưu cấu hình, job và tải](PERIODIC_P05_SL1_SL2_IMPLEMENTATION_2026_10_06.md).
- [UI, cấu hình chung/override, đo tương tác](PERIODIC_P05_UI_FINAL_2026_10_06.md).
- [Luồng Return/hiệu lực/khóa nguồn/Inbox](PERIODIC_P05_FULL_FLOW_FIX_2026_10_06.md).
- [Mở lại Work, một lần, Work chưa có report](PERIODIC_P05_REOPEN_COMPLETION_2026_10_06.md).

Evidence lượt cuối: [outputs/p05-close-20261007](../../../../outputs/p05-close-20261007/). Không cộng các lượt kiểm trùng thành tổng số ca độc lập.

## 2. Hai lỗi sản phẩm tìm được và đã sửa

### 2.1 Lưu nháp xong trường bị khóa do quyền đọc lại chưa đồng bộ

File: `tdtd-fe/src/pages/works/report/WorkReportEditorPage.tsx`, hai điểm sau writer response, hiện tại dòng 6649 và 6704.

- Save scalar/đường Table legacy đã tăng revision ở mutation response, nhưng report query và `aggregateEditHint` còn ở revision cũ. Guard đúng vì vậy khóa trường dù người dùng vẫn có quyền sửa.
- Sau khi nhận mutation thành công, đọc lại report bằng `readNativeReportAfterSave()` để đồng bộ payload, lifecycle và hint. Native active đã có readback ở đường writer riêng nên không gọi lặp.
- Giữ kiểm actor/report/schema/payload/lifecycle; không mở quyền bằng client. Nếu đã ghi nhưng đọc lại lỗi thì vẫn giữ nháp và trạng thái cần đối chiếu, không giả lưu thành công. Nội dung người dùng tiếp tục nhập trong lúc chờ Save được giữ.
- Mounted test mới kiểm cả đọc lại thành công với nội dung đang nhập, và đã ghi nhưng readback lỗi. Browser thật kiểm Save scalar `1` xong vẫn nhập tiếp được; sau job tổng hợp giá trị `42` trở thành trường do mapping quản lý và chỉ xem đúng contract.

### 2.2 Đã mở lại nhưng Review vẫn ghi “Đã hoàn thành”

File: `tdtd-be/Services/WorkAssignments/Progress/WorkCompletionWorkflowService.cs`, `TryConvergeAsync`, hiện tại dòng 279–286.

- Review summary lấy trạng thái thực hiện từ read model kỳ mới nhất. Chỉ rebuild assignment/kỳ đang sửa không đủ: kỳ tháng 11 còn trạng thái Completed cũ dù assignment đã mở.
- Sau đồng bộ trạng thái và projection assignment, rebuild **mọi period đang tồn tại, chưa xóa của đúng assignment** trước CAS xóa `CompletionProjectionPending`. Không tạo kỳ, không đổi payload hoặc quyết định duyệt.
- Lỗi giữa chừng, hủy hoặc cạnh tranh revision vẫn giữ pending; retry đọc lại trạng thái/actor quyết định đã commit. Không dọn pending thủ công.
- Kiểm cô lập thêm lỗi giữa chừng tại period projection và retry; runtime gọi job thật, đối chiếu 3 period Review + 1 summary đều cùng trạng thái Đang thực hiện với dữ liệu gốc.

## 3. Vòng định kỳ cuối đã đi qua

Fixture run `p05-flow-20261006-5c85ef9f`, DB riêng `p05_completion_flow_p05_flow_20261006_5c85ef9f`.

| Đối tượng | ID |
|---|---|
| Work định kỳ | `6ac50a77ea772ca23455cc9a` |
| PV01 nhận/gom | `6ac50a77ea772ca23455cc9b` |
| PX01 nguồn trực tiếp | `6ac50a77ea772ca23455cc9c` |
| Report PV01 tháng 9 / period | `6ac50a78ea772ca23455cca3` / `6ac50a78ea772ca23455cca4` |
| Report PV01 tháng 10 / period | `6ac50a78ea772ca23455cca9` / `6ac50a78ea772ca23455ccaa` |
| Report PX01 tháng 10 / period | `6ac50a78ea772ca23455ccac` / `6ac50a78ea772ca23455ccad` |
| Report PV01 / PX01 tháng 11 | `6ac50a78ea772ca23455ccaf` / `6ac50a78ea772ca23455ccb2` |

1. Browser người nguồn: sửa report tháng 10 sau Return, `20 → 42`, lưu nháp, nộp. Browser PV01 duyệt nguồn.
2. Đóng Work fixture qua API; browser chủ Work mở lại, chọn đúng kỳ tháng 10 và nhập lý do. Con tự hoàn thành vẫn giữ trạng thái riêng; tiếp tục mở đúng kỳ của PV01 bằng quyền hiện có.
3. Browser PV01 kiểm Save scalar không bị khóa nhầm. Chuẩn bị cấu hình chung SUM nguồn `n` vào đích `total` bằng **API thật**; không nhận thao tác chuẩn bị này là đã author toàn bộ sơ đồ bằng browser.
4. Cấu hình chọn rõ `TARGET_DATA_WINDOW`, `DECLARED_DATA_WINDOW`, `CONTAINED`; khoảng tháng 10 khai báo 01–31/10, khác PeriodKey/hạn 30/10. Lưu cấu hình và enqueue job; worker ghi kết quả 42 qua writer. Không suy khoảng dữ liệu từ hạn/kỳ.
5. Browser đọc lại 42 → Nộp → xem xác nhận nguồn đầy đủ → xác nhận. Report chuyển Submitted và chỉ xem.
6. Lần duyệt đầu bị chặn vì **kỳ tháng 9 của PV01 còn Draft**. Hoàn thiện đúng prerequisite bằng cùng cấu hình chung, job tháng 9 ra 41; nộp qua API với ngày hoàn thành lịch sử 30/09 rồi duyệt. Không bỏ kiểm thứ tự duyệt lần đầu. Duyệt tháng 9 không nhả hold đang chờ tháng 10.
7. Browser chủ Work duyệt tháng 10 thành công. Job đồng bộ xong: Work vẫn Đang thực hiện vì chưa đến hạn, PV01 đạt 2/3 kỳ; hold và pending được nhả. Report tháng 10 Approved, giá trị 42, chỉ xem.
8. API xác nhận cấp trên đã dùng nguồn thì PX01 tháng 10 không được Return/ngừng; có lock owner cụ thể. Ngược lại, tháng 9 PV01 vẫn có capability Return dù tháng 10 đã Approved: không còn chặn sửa kỳ cũ chỉ vì có kỳ sau duyệt.
9. Readback đối chiếu hash/revision/status/payload: nguồn tháng 9, đích và nguồn tháng 11 giữ nguyên. **Đích tháng 9 có thay đổi có chủ đích ở bước 6**, không tuyên bố mọi kỳ ngoài tháng 10 đều nguyên.

URL đọc kết quả: [PV01 tháng 10 đã duyệt](http://127.0.0.1:5307/works/6ac50a77ea772ca23455cc9a/inbox/report/6ac50a78ea772ca23455ccaa). Tài khoản fixture có hậu tố `-periodic-author`, `-periodic-reviewer`, `-periodic-source`; không ghi credential/token vào handoff.

![Báo cáo tháng 10 đã duyệt, tổng hợp 42 và chỉ xem](../../../../outputs/p05-close-20261007/browser-periodic-approved.png)

Ảnh khác: [mở lại Work](../../../../outputs/p05-close-20261007/browser-periodic-work-reopen.png), [Save còn nhập được](../../../../outputs/p05-close-20261007/browser-scalar-save-editable.png), [xác nhận nộp](../../../../outputs/p05-close-20261007/browser-periodic-submit-preview.png), [thông báo](../../../../outputs/p05-close-20261007/browser-notifications-final.png), [tổng quan](../../../../outputs/p05-close-20261007/browser-dashboard-final.png).

## 4. Bảng kiểm chốt cuối

| Lớp kiểm | Kết quả, giới hạn | Evidence |
|---|---|---|
| FE Save/readback | **18/18 PASS**, có hai ca hồi quy mới nêu trên | `scalar-save-fe.log` |
| Decoder tải lớn | **6/6 PASS**, bỏ được gap SKIP do thiếu response tải lớn | `large-response-fe.log` |
| TypeScript toàn FE | `tsc -b --pretty false`, exit 0 | `typecheck.log` |
| BE policy/convergence/BSON | **103 checks PASS**; đây là kiểm cô lập, không phải 103 API | `projection-checks.log` |
| Build BE | 0 errors, 91 warnings hiện có; build output riêng | `projection-build.log` |
| Build harness cuối | 0 errors/0 warnings trong lượt incremental cuối | `runtime-v4-build.log` |
| API đọc lại cuối | **11 assertions PASS**: payload, source locks, holds, Inbox, cây/Dashboard | `final-readback.log/json` |
| Job thật/projection | 3 period Review + 1 summary đồng nhất với assignment; pending=false | `projection-runtime.log`, `post-approval-audit.log`, `notification-audit.json` |
| Thông báo lifecycle | Return/Approved đúng recipient/event; chạy notification job lại không nhân đôi | `notification-audit.json` |
| Số kỳ | 6 kỳ của hai assignment vẫn đúng 6 sau reopen/job/retry | cùng audit |
| Browser thật | Save, nộp, duyệt, mở Work/đúng kỳ, đọc kết quả/readonly, thông báo và Dashboard đã quan sát | các ảnh mục 3 |
| Migration provenance | PASS trước/sau seed, sau sửa fixture và job; startup bình thường | log fixture/load/runtime |
| Host chính | FE/BE HTTP 200, authenticated read Review/Inbox/Aggregate; không gửi mutation nghiệp vụ | `health-final.json`, `main-read-smoke.json` |

Decoder dùng response thật đã thu ở lượt tải trước: 41 target, 26.560 lineage ô matrix (166 × 160). Đây là replay response thực trong FE test; **không phải chạy mới toàn bộ tải 166 × 200** ở lượt này.

Fixture hiện `NotifyReviewRequired=false`. Audit đọc flag thật, không tự bật để test; không nhận fixture này chứng minh thông báo Submitted/Resubmitted khi bật cấu hình đó. Return/Approved được kiểm theo event bắt buộc thực tế. Browser PV01 thấy hai thông báo duyệt (tháng 9 và 10) cùng nhóm; không phải hai bản trùng của một sự kiện.

## 5. Đo tải bổ sung và giới hạn

Work tải riêng: `6ac535746e154ebc647d70fd`, **166 assignment, ba cấp, 10 nhánh gốc**.

- Mở lại Work qua HTTP mất **12.061 ms**, tức khoảng **12,1 giây**.
- Đúng **156 queue item + 156 materializer** được phục hồi. Các nhánh ngừng/hoàn thành riêng và con chịu khóa của chúng vẫn giữ nguyên; đối chiếu tập ID, không chỉ so tổng.
- Giữ **166 period**, không tạo lặp; mọi cờ riêng của con không đổi. Migration trước/sau PASS.
- Đọc completion 11 ms; Work 126 ms; mindmap 91 ms; Dashboard 33 ms. Cây và Dashboard đối chiếu cùng 10 nhánh gốc.
- Log chuẩn: `load-166-final-pass.log`, `load-166.json`, `load-166-fixture.json`. Các log thất bại/trung gian được giữ để truy vết nhưng không dùng làm kết quả cuối.

**12,1 giây là độ trễ đồng bộ của việc mở lại/phục hồi cây Work lớn.** Đây là hạn chế hiệu năng đã đo, chưa có ngưỡng nghiệm thu SLA được Yud chốt; không gọi là nhanh và không tự mở một phase tối ưu Work mới. Nó khác đường lưu cấu hình Tổng hợp và worker tính nền.

Bằng chứng Aggregate tải đã có ở SL1/SL2: 166 report × 200 field/ô, lưu đầu 232 ms/lưu lại 939 ms, job 36,272/50,923 giây; ba job nhiều kỳ với tối đa 1.992 report hoàn tất. Các số đó thuộc lượt kiểm trước, máy có tác vụ đồng thời; không biến thành SLA hoặc số đo mới của bản cuối.

Giới hạn tổng tập nguồn trước lọc vẫn **10.000 report**. Không nhận đã kiểm 166 × 365; cache, tính so sánh kỳ trước/lũy kế tương đối và đổi Form đã giao/version migration vẫn là phần hoãn theo Yud.

## 6. Fixture/harness và WIP

Chỉ sửa sản phẩm tại hai file mục 2. Các file kiểm hỗ trợ:

- `tdtd-fe/src/features/aggregateMapping/contractChecks/p04.native-submit.mounted.test.tsx`.
- `tdtd-be/tests/WorkExecutionProgressChecks/CompletionProjectionChecks.cs`.
- `tdtd-be/tests/WorkCompletionFlowRuntimeChecks/Program.cs`, `CompletionLoadChecks.cs`, `CloseoutAuditChecks.cs`.
- `tdtd-be/tests/AggregateMappingRuntimeChecks/RuntimeFixture.cs`: source assignment có parent phải seed `Level=1`, root `Level=0`.

Đã kiểm status/diff FE/BE, lưu baseline ở `be-inherited.diff`, `fe-inherited.diff` và các file `.before.*`. Repo có nhiều WIP/untracked của owner khác; không coi cả diff hiện tại là thay đổi của lượt này, không reset/commit/push.

Fixture định kỳ cũ có Parent/Root đúng nhưng child `Level=0`; điều này làm cây hiểu nhầm số nhánh gốc. Đã backup toàn document và CAS sửa **đúng source assignment `6ac50a77ea772ca23455cc9c`** trong DB fixture, giữ pin/payload/parent; chạy migration. Bằng chứng `fixture-child-level-backup.json`, `fixture-level.log`. Không sửa product để bù fixture sai.

Các lỗi harness trung gian đã phân biệt: clone period trạng thái lịch sử không phù hợp; kiểm nhầm shape DTO; unique identity/level của fixture; test thông báo kỳ vọng khi flag tắt; PowerShell làm mất biểu diễn chính xác datetime context. Đã sửa harness/fixture riêng và chạy lại, không nới guard/policy hoặc sửa hàng loạt dữ liệu nghiệm thu.

Không drop/reset DB, không bỏ startup migration/provenance, không sửa blanket 90 pin, không tắt Flow barrier, không dọn pending trực tiếp. Seed/tạo/sửa/duyệt chỉ trong DB riêng. Host chính chỉ đọc nghiệp vụ; các job nền vốn có vẫn vận hành bình thường.

## 7. Host và bàn giao tiếp

| Host | Trạng thái tại 01:00 ngày 07/10 | Phạm vi bằng chứng |
|---|---|---|
| BE chung 5164 | PID 173696, binary `outputs/dashboard-db-paging-20261007/be-final/tdtd-be.dll`, mở 00:44 | Health/read smoke đạt; **chưa có sửa projection cuối** |
| FE chung 5173 | PID 147324, giữ host cũ | Source FE có HMR; không nhận đã browser mutation trên DB chính |
| BE fixture 5308 | PID 187888, `outputs/p05-close-20261007/projection-fix-artifacts/bin/tdtd-be/debug/tdtd-be.dll` | Có sửa cuối, startup/migration và API/job đã kiểm |
| FE fixture 5307 | PID 170032, cấu hình fixture hiện có | Browser thật mục 3–4 |

SHA256 BE fixture: `3205E9F662D41617EB96CA554472DAA6E0B1EFC1F5D87F5FABEA4257946F82FD`. Manifest source/binary: `source-manifest.json`. PID là thông tin phiên, không phải cấu hình triển khai bền vững.

Đề xuất owner tổng cập nhật tracker: **P05 chốt kỹ thuật cho once/periodic**, bằng chứng cuối ở tài liệu này; ghi riêng việc nạp bản BE cuối vào host chung khi phối hợp restart. Không ghi P06/UAT người dùng PASS. Lam không sửa issue/master plan/board.

Phần còn lại thuộc nghiệm thu/phase sau: Yud duyệt popup và thao tác thực tế; Windows IME thật (Unicode/paste/con trỏ đã kiểm, mounted composition không thay IME hệ điều hành); rà checklist vai trò/mọi kiểu biểu mẫu theo ma trận tổng; báo cáo chủ động với identity/create API riêng chưa xác nhận; tối ưu Work reopen lớn nếu cần. Những phần đã hoãn không được gọi là đã triển khai.

**Dừng ở bàn giao P05. Không tự mở P06 hoặc phase tối ưu.**
