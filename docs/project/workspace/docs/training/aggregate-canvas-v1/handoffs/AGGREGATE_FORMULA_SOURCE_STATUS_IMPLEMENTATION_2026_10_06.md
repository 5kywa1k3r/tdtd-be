# Công thức gọn, gợi ý nhập và cập nhật khi nguồn đổi trạng thái — 06/10/2026

Owner: Lam — Aggregate FE/BE. Cơ sở: Yud duyệt phương án trong `AGGREGATE_FORMULA_SOURCE_STATUS_AUDIT_2026_10_06.md`, yêu cầu gợi ý như Excel, chú ý INP, triển khai cây công thức/job và các khoảng hở đã chỉ ra. Đây là bàn giao một phần triển khai tiếp AS-J1; không đóng P05 hoặc mở P06.

## 1. Kết quả triển khai

### Công thức

- Một kết quả mặc định; phần thêm kết quả khác ở mục thu gọn **Nhiều kết quả (tùy chọn)**. Giữ ID đầu ra/dây nối cũ, không gộp hoặc xóa kết quả cũ.
- Gợi ý tên hàm/biến tại vị trí con trỏ, mô tả tiếng Việt, chữ ký hàm và tham số đang nhập. ↑/↓ chọn, Enter/Tab chèn, Esc đóng; chèn thay đúng token, không viết đè phần còn lại của công thức.
- Textarea thật giữ IME, paste, selection/caret, nháp sai và ký tự cuối lúc xác nhận. Trong composition không bắt Enter/Tab làm thao tác chọn gợi ý.
- Không debounce giá trị hiển thị. Gợi ý chỉ quét từ vựng có giới hạn; dựng cây/options được đưa sang render trì hoãn; xác nhận dùng nháp mới nhất. Chuỗi dài hơn 8.192 ký tự bỏ các span tô màu trang trí; giới hạn parser 65.536 ký tự, AST tối đa 16 cấp.
- Dùng parser/AST hiện có, không `eval`, không chạy JavaScript hoặc dùng regex để tự tính công thức. Regex chỉ tách token; bộ phân tích xử lý ngoặc, thứ tự toán tử, hàm lồng, `;`, chuỗi `"..."` và ký tự escape.

| Cú pháp | AST / ý nghĩa |
| --- | --- |
| `GET(A)` | `INPUT(ref=A)`; giữ type/shape của biến. Nguồn SET vẫn là tập, không chọn dòng đầu. |
| `CONCAT(GET(A))` | CALL CONCAT nhận tập TEXT; giữ options ghép đã cấu hình, lineage và chính sách text hiện hành. |
| `IF(COUNT_REPORT(GET(A))>0;SUM(GET(A));0)` | IF + COUNT cơ sở REPORTS + SUM, kiểm kiểu; engine chỉ tính nhánh được chọn. A phải là nguồn số cho SUM. |
| `ONLY(A)` | CALL ONLY hiện hữu, yêu cầu một giá trị theo contract scalar. Công thức cũ lưu ONLY vẫn mở lại bằng ONLY, không bị đổi sang GET mới. |
| `IF(IS_PRESENT(ONLY(B));ONLY(B);"Chưa có; nội dung")` | Dấu `;` trong chuỗi được giữ nguyên; các điều kiện scalar không tự lấy một dòng từ tập. |

Không đổi COUNT/AVG/mẫu số/rounding, Table, Danh sách hay payload Form. GET mới ở đây là cú pháp truy cập biến, không thay AST `kind=GET` cũ của phép lấy roster. Tham số separator của CONCAT vẫn ở phần options đã chốt; chưa mở chữ ký CONCAT hai tham số hoặc tự mặc định thứ tự ghép mới. Khi mở lại, `GET(A)` có thể in gọn thành `A` vì cùng một AST INPUT.

### Nguồn và job

| Điểm | Xử lý |
| --- | --- |
| Ẩn/trả lại/thu hồi duyệt | Dùng invalidation bền cùng transaction lifecycle hiện có. Gọi dispatcher ngay khi reconciler tiếp nhận và sau khi cập nhật period; không tính nội dung trên request ẩn/hiện. |
| Ẩn report được chọn đích danh | Đọc metadata đúng các ID explicit/excluded đang thiếu trong tập current. Chỉ chấp nhận metadata thuộc con trực tiếp, đúng Form/pin/binding, đủ quyền, inactive + non-current + non-deleted. Giữ selection; báo `AGG_SOURCE_INACTIVE`; không đọc payload, không góp dữ liệu. |
| ID lạ/mất quyền/xóa/ngoài nhánh | Vẫn `AGG_SOURCE_UNAVAILABLE`, không coi là nguồn trống hoặc số 0. Không mở fallback Approved cũ. |
| Period projection chậm hơn report | Slot lấy current ID từ report flags hiện hành có kiểm duy nhất. Header kiểm scope/period/binding và truy vấn authoritative current khi pointer projection chưa khớp; không dùng pointer cũ làm nguồn góp. |
| Nguồn thay đổi giữa lượt tính | Giữ lease/generation/fence và kiểm input stamp trước/sau. Có retry tức thời giới hạn trước khi trả về dispatcher định kỳ hiện hữu; không thêm scheduler/cache. |
| Metadata hidden thay đổi | Recheck trong preview reader, pin tại commit, input stamp của materialization và stamp preview job; không phát lại preview cũ như kết quả hiện hành. |
| Đọc kết quả cũ | Kiểm quyền/current metadata của tập liên kết; chỉ tập góp dữ liệu phải còn Approved/active và đúng pin. Linked pending không bị nhầm thành contributor. Kết quả stale/không đủ quyền được ẩn, không hiển thị thành 0. |
| FE sau khi job hoàn tất | Tiếp tục quan sát khi màn mở; khi focus/hiện lại tab kiểm ngay. Poll status nhẹ mỗi 5 giây khi idle, theo dõi 1,5 giây lúc đang tính. Tab ẩn không gọi mạng. Không thay recipe/selection/nháp bằng readback; giữ revision của bản đang sửa để máy chủ có thể từ chối save stale. |
| Thông báo review | Sửa thông báo ẩn/hiện thành sẽ cập nhật nền, không nói thống kê/tổng hợp đã xong ngay sau thao tác. |

Job vẫn tính lại phần bị ảnh hưởng bằng evaluator hiện có, chưa làm delta trừ/cộng cho mọi hàm. Chỉ DRAFT/view còn sửa được tự cập nhật; source lock và report đã nộp giữ nguyên. Recurring dispatcher là đường phục hồi khi enqueue tạm lỗi hoặc cạnh tranh kéo dài.

## 2. API thêm và ranh giới đọc

`POST /api/aggregate-v2/instances/{id}/computation/status`

Request: `{ "context": <PeriodContext hiện hữu> }`

Response ví dụ:

```json
{"revision":19,"generation":13,"state":"DRAFT","refreshState":"COMPLETED"}
```

- Kiểm target context/identity/quyền bằng metadata; không đọc payload đích hoặc nội dung nguồn, không tính công thức.
- Không trả source IDs/counts, trace, input fingerprint, kết quả tạm/cuối. FE kiểm enum/revision của response.
- Khi trạng thái/revision thay đổi, FE đọc endpoint instance hiện hữu để lấy progress/kết quả đã kiểm quyền. Đọc đầy đủ định kỳ tối đa mỗi 30 giây khi idle để kiểm lại quyền kết quả đang hiện. Chưa có SSE/WebSocket.
- Với BE cũ chưa có status endpoint (404), FE dùng read endpoint hiện hữu; không giả trạng thái thành công.
- Read/full-result vẫn giữ kiểm nguồn, payload pins và quyền. Status không là bằng chứng dữ liệu hiện tại đủ quyền để đọc.

## 3. File sửa và ownership

Các file đã có WIP được sửa tiếp có giới hạn; không reset/stage/commit, không sửa master/issue board. Không mở agent/chat.

| Phần | File |
| --- | --- |
| FE parser/model | `tdtd-fe/src/features/aggregateMapping/aggregateEditorModel.ts`, `aggregateFormulaEditorModel.ts` |
| FE nhập/gợi ý mới | `AggregateFormulaInput.tsx`, `aggregateFormulaSuggestions.ts` |
| FE dialog/poll/response | `AggregateFormulaDialog.tsx`, `AggregateMappingEditor.tsx`, `aggregateResponseGuard.ts`, `tdtd-fe/src/api/aggregateMappingApi.ts` |
| Shared review UI | `tdtd-fe/src/components/works/review/WorkReviewTab.tsx`: chỉ hai câu thông báo; diff trước sửa sạch tại file này. |
| BE nguồn | `AggregatePreviewContracts.cs`, `AggregateSourceResolver.cs`, `AggregateMongoPreviewReader.cs`, `.Views.cs`, mới `.Inactive.cs` |
| BE persistence/job | `Persistence/AggregateMongoCommandReader.cs`, `AggregateMaterializedInputs.cs`, `AggregatePersistenceContracts.cs`, `AggregateReadCommands.cs`, `AggregateViewPersistence.cs`, `AggregateHostMembership.cs`, `AggregateMaterializationWorker.cs`, `AggregatePreviewJobs.cs` |
| API | `tdtd-be/Controllers/AggregateMappingCommandsController.cs` |
| Shared lifecycle | `WorkReportLifecycleProjectionReconciler.cs`: hook gọi queue, không thay policy/Flow/statistics. Diff đã đối chiếu; chỉ phần hook là thay đổi đợt này. |
| Kiểm | FE formula-completion/formula-variants/p04/text-block-policy; BE preview Program; mới `TextSourceLifecycleChecks.cs` và hook test trong `TextPeriodicLifecycleChecks.cs`. |

Đường đầy đủ cho BE nguồn là `tdtd-be/Services/AggregateMapping/`; FE feature là `tdtd-fe/src/features/aggregateMapping/`. Không nhận toàn bộ diff các file dirty là công sửa trong đợt này.

## 4. Bằng chứng

Evidence root: `outputs/aggregate-operators-20261005/`.

| Phạm vi | Kết quả |
| --- | --- |
| Preview core | **377 PASS**, `formula-job-preview.log`; gồm explicit hidden, excluded hidden, không đọc payload hidden, mất quyền/ID không có fail closed; IF lazy, six decimals, text/list/table hồi quy. In-memory, không nhận là runtime. |
| Persistence core | **172 PASS**, `formula-job-persistence.log`; nguồn đổi lúc đang tính, supersede, cancel, lease/duplicate và immutable frozen. In-memory. |
| FE | **125 PASS / 5 file** `formula-job-fe-delivery.log` và **20 PASS / 2 file** `formula-job-fe-text.log`; tổng 145 ca riêng. Gồm autocomplete/caret/IME, GET/ONLY, legacy options/output, stale revision, quan sát source refresh sau COMPLETED, typed text/extended operators. |
| TypeScript/lint | `formula-job-tsc-delivery.log`, `formula-job-lint-delivery.log`: PASS. |
| Build BE | Full `formula-job-final-build.log`: 0 errors, 92 warnings của cây WIP. Incremental delivery/proof 0 errors; không gọi toàn repo warning-free. |
| Native API/job | `formula-job-native-final.log`: **208 PASS**, normal Development host + startup migration + login thật + real HTTP + Hangfire queue riêng. Có metadata status 74 bytes và hide/reactivate/recall/approve tự tính, không recurring/manual worker trong các ca mới. Lượt cuối thêm kiểm preview hidden/stale đang được chốt evidence bên dưới. |
| Tải | `formula-job-wide.log`, manifest `formula-job-runtime-bin/asj1-wide-r8-text-20261005-d0809900.json`: 166 reports × 200 field/cell, 4 text dài/report, 160 ô matrix, 36 số; tổng 33.200 giá trị. PASS native writer/all values/4 snapshot MinIO/submit-return seam/frozen. |
| Browser component | Production build của **component thật** trong trang kiểm riêng có nhãn dữ liệu giả; không mock evaluator/ACL. Gõ hàm, Tab, cây lồng, dán 12.603 ký tự + sửa cuối/xác nhận giữ nguyên. Chưa là browser UAT toàn Work/canvas. |

### Số đo tải mới

Fixture Work `6ac466a4b33c403f62555505`, report `6ac466a4b33c403f6255550e`, instance `7C66F7DAB4929EFBEBA7B9BD0228494D5F5F56EC2BDFA837E1100825DEC13CD8`.

| Thao tác | ms |
| --- | ---: |
| Queue metadata lần đầu | 109 |
| Lưu phương pháp/intent lần đầu | 259 |
| Job đầu, gồm polling | 42.206 |
| Read progress lớn nhất | 2.437 |
| Queue metadata lần hai | 595 |
| Lưu khi đã có kết quả | 1.206 |
| Job tính lại lần hai | 45.963 |
| Submit metadata | 3.833 |
| Lifecycle/Return + refresh | 53.346 |

Read progress khoảng 8.145–8.563 bytes, tiến độ 0/64/128/166; không mang toàn bộ text/trace. Lượt tải trước endpoint status mới; evaluator/writer/job không đổi sau lượt đo. Không nhận số này là SLA hoặc tốc độ field production.

### Nguồn đổi trạng thái

Lượt 208: run `r8-text-20261005-24410a58`, target `6ac467bf8b7ee731acc6b7b4`, source `6ac467bf8b7ee731acc6b7b7`; manifest `formula-job-final-bin/formula-source-r8-text-20261005-24410a58.json`.

- Ẩn: HTTP ack 498 ms, job hoàn tất sau bước ack/poll 481 ms; selection giữ nguyên, nguồn ngừng góp.
- Hiện lại: ack 570 ms, hoàn tất 2.193 ms, attempt 3 do recheck metadata; đúng current Approved được góp lại.
- Thu hồi duyệt: hoàn tất sau ack/poll 325 ms; vẫn linked, không contributor; duyệt lại 1.251 ms.
- Status metadata 74 bytes, 49 ms **bao gồm bootstrap Context trong harness**; không nhận 49 ms là chỉ latency status route.
- Có xử lý hàng đợi thật tại prefix riêng của normal host; không bật recurring để làm ca immediate wake pass.

### INP / UI

- `formula-inp-short-final.json`: 39 interaction IDs được Event Timing ghi (ngưỡng 16 ms), max/p98 **40 ms** trên lượt gõ + Tab + xác nhận.
- `formula-inp-long-final.json`: 7 interaction IDs được ghi, max/p98 **32 ms** cho Ctrl+V 12.603 ký tự, sửa cuối và xác nhận; chữ `cuoi` cuối được giữ.
- Số đo local component production build, không CPU throttle; không phải CrUX/INP toàn canvas hoặc IME native đầy đủ. Các event dưới 16 ms không nằm trong sample. IME composition/focus/caret/nháp lỗi có mounted checks; paste tiếng Việt có browser evidence.
- URL kiểm riêng: `http://127.0.0.1:18948/.tmp/formula-perf.html`. File harness trong `tdtd-fe/.tmp/formula-perf*`; không mount thành route sản phẩm, không sửa evaluator/ACL.
- Ảnh: `formula-autocomplete-20261006.jpg`. Đã gửi Yud xem; chưa coi hình/mounted test là Yud duyệt UI nếu chưa có phản hồi.

## 5. Giới hạn và phần giữ lại

- Không restart/stop FE/BE Yud. Probe 5173 không kết nối; không tự mở lại. BE mới kiểm ở host/process do harness tạo; host đó chạy migration bình thường và tự dừng đúng process của nó. Chưa xác nhận binary đang dùng chung đã nạp sửa này.
- Source race/supersede có in-memory và native lifecycle/job evidence; chưa kiểm toàn bộ ma trận hide/reactivate liên tục khi 166×200 đang RUNNING, process kill/recovery và queue outage thật. Giữ là gap P05, không đổi thành PASS từ source audit.
- Chưa browser toàn luồng report đa vai trò trên phiên dùng chung; không nhận UI UAT cuối, P05 hoặc P06 hoàn tất.
- Cache, tối ưu delta, version rollout/đổi Form và so sánh kỳ tiếp tục hoãn theo quyết định Yud. Table engine/Flow/ACL không mở thêm; không drop/reset DB hoặc đổi pin nghiệm thu.
- Phần tổng quản có thể cập nhật tracker từ handoff này; Lam không sửa tracker/master của owner khác.

## 6. Bổ sung theo quyết định Yud: tính thử với ba báo cáo nguồn thật

Yud chọn **báo cáo nguồn thật**, thay cho dữ liệu mẫu tự sửa. Triển khai trong popup Công thức, mục **Tính thử với báo cáo thật**. Giới hạn tối đa ba report; UI hiện riêng số report và số đơn vị, không đánh đồng ba kỳ của một đơn vị với ba đơn vị. Nguồn phải là bản hiện hành Approved, còn active và đủ quyền đọc; giữ điều kiện lọc và khoảng dữ liệu đã cấu hình trên đường tới công thức.

### Hành vi và API

- Chọn nguồn theo trang 20 report; chỉ tải khi mở bộ chọn. API nguồn bổ sung nhãn đơn vị, tên report, `periodKey` của trang hiện tại. Không giải mã `periodInstanceKey` thành ngày hiển thị.
- Bootstrap trả `formulaProbe: { supported, version: 1, maxReports: 3 }`. BE cũ hoặc thiếu quyền truy nguồn thì hiện chưa hỗ trợ; không giả kết quả thành công.
- Dùng endpoint preview-job hiện hữu, `kind: "FORMULA"`, thêm `formulaNodeId`; request chứa recipe thu gọn đúng tổ tiên của nút đang sửa và selection `EXPLICIT_REPORTS` từ 1–3 report. Đích giữ schema nhưng không có cổng nhận dữ liệu trong lượt thử. Validator thường không chấp nhận graph đặc biệt này để lưu cấu hình.
- Lượt thử chạy qua Hangfire và **chính AggregatePreviewService/evaluator**, không có evaluator FE riêng. Có progress/cancel, chặn late completion khi công thức, nguồn, phiên hoặc context đổi. Tính sau khi bấm nút, không gọi API theo từng phím gõ.
- Stamp trước/sau job kiểm quyền, trạng thái nguồn, context, schema, payload/lifecycle và revision config/instance thực đã lưu. Reuse `PinnedReader` để kết hợp revision config/instance đã đối chiếu với metadata native vừa đọc; không hạ revision về 0 để vượt kiểm.
- Kết quả có đầu vào sau lọc, đầu ra, report/đơn vị/kỳ và các hàm thực sự chạy từ trace của BE. `GET` có thể được thử như một tập; quy tắc output khi lưu công thức vẫn giữ nguyên.
- Không trả Apply token, không ghi cấu hình/report, không thay preview chính đang mở. Chỉ lưu dữ liệu tạm của preview job và các reference phân trang theo cơ chế hiện có.
- Tối đa 20 giá trị mỗi cổng và 60 trace mỗi giá trị trong response; text trình bày tối đa 2.000 ký tự, có nhãn rút gọn. Phép tính dùng toàn bộ nội dung nguồn, không tính trên đoạn rút gọn. Danh sách/bảng nội dung dùng reader/reference phân trang hiện hữu. Giữ cap response job 8 MB và budget engine hiện hành.
- Kết quả đang hiện được kiểm lại qua read job sau 10 giây, khi focus/hiện lại tab; đổi auth scope thì bỏ kết quả/selection. Thiếu nguồn/quyền/stale là lỗi, không biến thành tập rỗng hoặc số 0.

### Màu và chuẩn hóa công thức

- Hàm, biến, chuỗi, số và toán tử phân biệt bằng màu; ngoặc có màu theo tầng, cặp cạnh con trỏ cùng được đánh dấu; ngoặc không khớp báo màu lỗi.
- Giữ textarea thật cho IME/paste/caret; lớp màu không sửa giá trị người dùng. Trang trí giới hạn 8.192 ký tự rồi chuyển cách hiển thị đơn giản, vẫn giữ toàn bộ nháp. Không parse cây hoặc chạy phép tính đồng bộ theo từng phím.
- Khoảng trắng ngoài chuỗi được parser bỏ qua; khoảng trắng, dấu `;` và ngoặc bên trong `"..."` được giữ. `TRIM(...)` là phép xử lý nội dung do người dùng chọn, không được tự áp dụng lên mọi literal hoặc dữ liệu nguồn.
- Giữ giới hạn cây 16 cấp; công thức quá sâu báo lỗi và giữ nháp để sửa.

### File bổ sung của đợt này

| Phần | File (ngoài các file ở mục 3) |
| --- | --- |
| FE nhập/cặp ngoặc | `aggregateFormulaSyntax.ts`; cập nhật `AggregateFormulaInput.tsx` |
| FE tính thử | `AggregateFormulaProbePanel.tsx`, `aggregateFormulaProbe.ts`; nối qua `AggregateFormulaDialog.tsx`, `AggregateRecipeCanvas.tsx`, `AggregateMappingEditor.tsx` |
| FE contract/guard | `aggregateEditor.types.ts`, `aggregateMapping.types.ts`, `aggregatePreview.types.ts`, `aggregatePreviewJob.ts`, `aggregateResponseGuard.ts` |
| BE tính thử | `Services/AggregateMapping/AggregateFormulaProbe.cs`, `AggregatePreviewService.cs`, preview envelope/converter, validator; `Persistence/AggregatePreviewJobs.cs` và reuse `AggregateMongoCommandReader.PinnedReader` |
| API nguồn/capability | `Controllers/AggregateMappingPreviewController.cs`, `DTOs/AggregateMapping/AggregateMappingDtos.cs` |
| Kiểm | `formula-completion.mounted.test.tsx`, `formula-probe.mounted.test.tsx`, preview core và `TextFormulaProbeChecks.cs` trong harness runtime |

### Bằng chứng mới nhất (06/10)

Không cộng các lượt chạy lặp thành tổng số ca độc lập.

| Kiểm | Kết quả và evidence |
| --- | --- |
| Bộ tính | **385 PASS** `formula-probe-core.log`: thêm trial cùng evaluator, GET giữ tập, IF/COUNT/SUM, giới hạn, không target write/token, graph thử không lưu được qua validator thường và stale source. In-memory; fixture ba đơn vị ở mức này. |
| FE cuối | **98 PASS / 5 file** `formula-trial-fe-final.log`, gồm 9 ca syntax/completion/probe; không cộng lại 9 ca từ lượt riêng. Nháp đổi/late result/cancel/nguồn bị từ chối được kiểm. |
| TypeScript/lint | Full `formula-trial-tsc-final.log` exit 0; lint năm file formula/probe cuối exit 0, `formula-trial-lint-final.log`. |
| BE cuối | `formula-trial-build-final.log`: **0 errors / 92 warnings** của cây WIP, gồm source label `periodKey` mới. |
| Native API/job/writer | **216 PASS**, `formula-trial-native-browser.log`: normal Program/startup migration, login/HTTP/Hangfire thật, queue riêng; có kiểm provenance sau các ca lỗi. Không skip migration hoặc sửa pin nghiệm thu. |
| Trial native | Run `r8-text-20261005-99a62830`, manifest `formula-trial-bin/formula-trial-r8-text-20261005-99a62830.json`: ack **106 ms**, tổng start/poll **508 ms**, response **6.997 bytes**, kết quả **60**. Ba report là ba kỳ của **một đơn vị**, không nhận là ba đơn vị native. |
| Browser với BE thật | Component Công thức sản phẩm trên trang kiểm riêng, proxy loopback chỉ đọc context/nguồn và thao tác preview-job vào host của harness. Chọn ba report thật: `SUM(A)` và `IF(COUNT_REPORT(GET(A))>0;SUM(GET(A));0)` đều **60**; hiện đang xử lý, sửa nháp ẩn kết quả cũ. `formula-trial-live-20261006.jpg`. Sau ảnh này đã chỉnh nhãn đầu ra thành “Kết quả” và kỳ nguồn dùng `periodKey`, kiểm type/build/FE; chưa chụp lại hai nhãn ấy với BE thật. |
| Event Timing màu công thức | `formula-inp-syntax-final.json`: 43 interaction IDs, max/p98 **72 ms**. `formula-inp-dense-final.json`: công thức 5.602 ký tự, 5 interaction IDs, max/p98 **72 ms**, chặn sâu quá 16 cấp và giữ nháp. |
| Event Timing dán dài | `formula-inp-long-syntax-final.json`: 6 interaction IDs, max/p98 **24 ms**, dán 12.603 ký tự, sửa thêm bốn ký tự cuối và xác nhận giữ đủ 12.607 ký tự. |

Các số Event Timing là local component production build, ngưỡng ghi event 16 ms, không CPU throttle; không nhận là INP thực địa/toàn canvas. Ảnh lớp màu cuối: `formula-syntax-colors-final-20261006.jpg` (trang kiểm riêng ghi rõ dữ liệu giả, không dùng làm chứng cứ evaluator thật).

Fixture target `6ac46ffeb85c07e36e488984`; ba report nguồn `6ac46ffeb85c07e36e488987`, `6ac46ffeb85c07e36e48898d`, `6ac46ffeb85c07e36e488993`. Lượt kiểm giữ nguyên target payload/hash/lifecycle, không Apply. Fixture được tạo riêng, không phải dữ liệu Yud nghiệm thu.

Lượt thử trước đó phát hiện raw reader không chứa persisted instance/config revision, gây `AGG_REVISION_CONFLICT`; đã sửa bằng đối chiếu persisted head/instance và reuse PinnedReader nêu trên. Những lượt stale do projection đang đổi metadata không được nhận là pass; harness chờ hàng đợi của chính nó ổn định trước lượt thử mới, không nới stamp.

### Bàn giao và giới hạn còn lại

- Đường mở sản phẩm: **Tổng hợp báo cáo → chọn khối Tính toán → Công thức → Tính thử với báo cáo thật**. Máy chủ cần bản build có capability mới. Không restart/stop FE/BE dùng chung theo yêu cầu Yud; chưa xác nhận binary của phiên chung đã nạp code này.
- Private normal host đã kết thúc, cầu nối credential tạm đã xóa (kiểm không còn file), tab kiểm riêng đã đóng. Không đưa credential vào tài liệu/ảnh/assets. Không mở thêm agent/task, không cập nhật issue/master.
- Đã kiểm component với API/job thật; chưa nhận browser UAT toàn Work nhiều vai trò, ba đơn vị native, hay toàn bộ P05/P06 hoàn tất. UI mới chờ phản hồi Yud, không tự ghi đã duyệt.
- Tiếp theo kiểm trên phiên sản phẩm sau khi BE được nạp bản mới ở thời điểm phù hợp: ba đơn vị thật, đổi nguồn/quyền trong lúc thử, nối lại công thức vào đích và luồng lưu/Submit/Return hiện hữu. Cache, đổi Form/version và so sánh kỳ vẫn hoãn.

## 7. Chỉnh thao tác nối khác kiểu theo Yud (06/10)

Quyết định trực tiếp mới: nối hai trường khác kiểu dữ liệu phải **thông báo ngay và ngắt dây**. Thay hành vi UI cũ giữ dây đang nhấc/khôi phục dây cũ khi đổi đầu sai kiểu; không đổi evaluator, storage hoặc ACL.

- Nối mới sai kiểu: không thêm edge, bỏ dây tạm/con trỏ, hiện thông báo tiếng Việt trong 6 giây và giữ lỗi trong nút “Lỗi và cảnh báo”.
- Đổi đầu nguồn/đích sai kiểu: ngắt chính dây đang nhấc và bỏ target binding tương ứng; giữ các dây khác. Áp dụng cho bấm nối, bàn phím, pointer kéo thả và native drop.
- Tooltip nhãn trường không chặn chuột; khi đang nối/đổi dây thì ẩn tooltip để không che trường đích. Đây là lỗi được phát hiện thêm bằng browser thật sau mounted checks.
- Nối cùng kiểu, Escape hủy thao tác, quyền khóa/read-only và Lọc chưa có phép xử lý vẫn giữ contract hiện hữu. Không tự xóa toàn bộ dây nháp lịch sử khi mở cấu hình; thay đổi này xử lý thao tác nối của người dùng.
- File sửa: `AggregateRecipeCanvas.tsx`, `aggregateGraphEditor.ts` (phân loại lỗi khác kiểu), `AggregateFormMemberLabel.tsx`, hai test `canvas-wire-filter.mounted.test.tsx` và `p04.mounted.test.tsx`. Đã đối chiếu/preserve WIP; không sửa BE, DB, shared GraphCanvas hoặc master/issue.
- Kiểm: `wire-type-disconnect-tests.log` (101 ca / 2 file), `wire-type-disconnect-tsc.log`, `wire-type-disconnect-lint.log`; browser component thật trên fixture riêng có nhãn dữ liệu giả, không API/DB. Kiểm nối mới, nối cùng kiểu rồi đổi đích sang kiểu khác: thông báo hiện ngay, `wires=0`, `pending=0`; nối lại được.
- Ảnh `outputs/aggregate-operators-20261005/wire-type-disconnect-20261006.jpg`; bằng chứng DOM `wire-type-disconnect-browser.json`. Không nhận kiểm component là browser UAT toàn Work. FE/BE dùng chung giữ nguyên, không restart.
