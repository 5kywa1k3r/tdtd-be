# KHCN — danh sách vấn đề giao agent kiểm tra, 07/10/2026

## Trạng thái tiếp nhận bản sửa KUI-01–07

Yud đã chuyển kết quả agent sửa KUI-01–07. Lam đã đọc `outputs/khcn-ui-fixes-20261007/RESULTS.md`, đối chiếu một số điểm source (hạn mindmap, tooltip, routing và lỗi ONLY còn lại). **Lượt tiếp nhận này chỉ đọc source/tài liệu và cập nhật bàn giao; không chạy lại test/browser/API, không restart host, không đổi dữ liệu mẫu. M1/M2 vẫn tạm dừng theo yêu cầu Yud.**

| Mã | Trạng thái hiện tại | Phần chưa được bằng chứng này khép |
|---|---|---|
| KUI-01 | Đã sửa source hạn mindmap; agent báo 10 ca BE cô lập và build PASS | Chưa readback bằng binary mới. Sai lệch hạn cha ở popup giao việc M02 là quan sát riêng, chưa thấy bản sửa trong scope agent. |
| KUI-02 | Đã sửa context tương đương/refresh cùng actor; giữ nháp và revision gốc, kiểm lại quyền/schema. Agent báo 5 mounted PASS, baseline FAIL chứng minh trigger | Chưa kiểm refresh/cross-tab/HMR thực trên PX01/PA02; không đồng nhất mọi lần tự thoát dev với trigger đã sửa. |
| KUI-03 | Đã sửa tooltip che checkbox và giữ input/focus. Agent có trace trước/sau, 4 mounted PASS và browser mẫu 4/4 trường tại 100/84/71% | Chưa chạy PA02/M01B thật, chưa đo INP. Quan sát khối mới chồng nhau là vấn đề placement riêng, chưa khép bằng fix tooltip. |
| KUI-04 | Đã sửa hiệu lực kỳ/assignment/binding, nhãn lịch sử và chặn mở kỳ inactive chưa có report; agent báo test FE/BE PASS | Chưa đối chiếu WA3/WA4 và count bằng API/binary mới. |
| KUI-05 | Đã sửa popup theo URL; agent kiểm browser PX01 Back/Forward/Đóng WA5/WA6 đạt | Nhánh có branch param chưa kiểm browser riêng. |
| KUI-06 | Đã sửa identity Form ở DTO/FE từ snapshot đã ghim; agent báo mapping/mounted PASS | Chưa readback PV01/PA02 trên binary mới. |
| KUI-07 | Giữ fix lịch; agent kiểm hồi quy và browser mở–đóng–mở lại calendar trên modal đạt | Chưa kiểm mọi màn dùng component chung. |
| KUI-08–09 | Giữ mở: form giao việc tự đóng; banner projection pending khác kỳ | Không nằm trong bản sửa KUI-01–07; không tự suy đã được giải quyết. |
| KUI-10 | Giữ mở, chặn M2. Source hiện vẫn trả nguyên input Port và spread ghi đè output id | Cần sửa identity, kiểm ONLY/IF/dây/preview/save/readback; không bỏ guard BE. |

Thứ tự khi được tiếp tục: xử lý KUI-10; nạp binary BE theo lịch host dùng chung và kiểm các ca readback còn thiếu; kiểm KUI-08/09 và hai quan sát riêng của KUI-01/03. Bảng và mô tả bên dưới là **lịch sử phát hiện**, không thay thế trạng thái tiếp nhận mới này. Không coi 91+15 test FE và 20 ca BE do agent báo là lượt Lam vừa chạy lại hoặc toàn bộ M1/M2 đã nghiệm thu.

## Phạm vi bàn giao

Yud yêu cầu lập danh sách cho agent khác kiểm tra/xử lý. Tài liệu là bàn giao, chưa tự mở chat/agent hoặc gửi tin sang owner. Giữ WIP, dữ liệu nghiệm thu và fixture KHCN; không reset DB, đổi quyền hay xóa pending để đạt test. FE/BE đang được dùng chung. Không coi mô tả nguyên nhân nghi ngờ là kết luận.

Work tái hiện: http://localhost:5173/works/6ac5517cfb2c9c48e5a42b0d. Tài khoản thử: pv01, px01, pa02, cap_hacthanh, cap_samson; mật khẩu dùng phiên kiểm thử Yud cấp, không ghi trong file. Bằng chứng ở `outputs/khcn-ui-samples-20261007/`. Sau reset phải dùng logical key/đường thao tác trong `KHCN_SAMPLE_UI_REPLAY_2026_10_07.md`; các ID ở đây chỉ là bộ hiện tại.

| Mã | Ưu tiên / mức bằng chứng | Owner đề nghị | Vấn đề và tiêu chí xử lý |
|---|---|---|---|
| KUI-01 | Cao; lệch giữa hai màn đã xác nhận | Dashboard + Work contract hạn | Mindmap mọi assignment hiện 31/10. WA5 thật 30/9, WA6 14/10, WA1 30/10. Phải dùng đúng contract hạn phần việc; không lấy hạn Work hoặc hạn kỳ gần nhất thay ngầm. |
| KUI-02 | Cao; lặp lại M01/M02, có log Vite nhưng chưa chứng minh nguyên nhân | Aggregate FE / auth integration | Nháp sơ đồ chưa lưu tự mất source/calculation; M02 còn thoát về danh sách report. Xác định trigger; giữ nháp phù hợp scope, hoặc báo rõ khi phiên/quyền/context thật đổi. Không bỏ kiểm auth để giữ nháp. |
| KUI-03 | Vừa; đã tái gặp khi chọn tuần tự field | Aggregate Canvas FE | Checkbox field thứ 3/4 đôi lúc click không đổi sau khối đổi chiều cao/zoom. Vừa khung rồi chọn được. Kiểm hit target, overlay/transform và auto layout; không dùng force click làm nghiệm thu. |
| KUI-04 | Vừa; quan sát trong danh sách kỳ | Work report query + FE | WA3/WA4 đã ngừng vẫn có kỳ trong một số danh sách chi tiết; cần xác định contract đọc lịch sử và hiển thị hiệu lực rõ, không đưa kỳ inactive thành việc phải làm. Không xóa report/kỳ lịch sử. |
| KUI-05 | Vừa; cần tái hiện riêng | Work detail routing FE | Đóng popup chi tiết assignment có lúc mở lại khi URL còn detailAssignmentId. Phải đóng ổn định, vẫn giữ branch/tab đang xem, xử lý back/forward đúng. |
| KUI-06 | Vừa; đã thấy trong bảng duyệt | Work Review DTO/FE | Cột Biểu mẫu và tiêu đề danh sách kỳ là “-”, trong khi report editor đọc đúng Form. Hiện tên/mã/pin đã giao; không lấy bản Form mới nhất thay pin. |
| KUI-07 | Đã sửa, cần giữ hồi quy | Aggregate FE + date component | Popup lịch thấp hơn modal và portal cũ bị aria-hidden. Fix đã có; kiểm các màn dùng chung trước merge. |
| KUI-08 | Vừa; gặp một lần, chưa có tái hiện ổn định | Work assignment FE | Form giao việc PA02 tự đóng khi đang cấu hình kỳ, chưa bấm Tạo mới. Danh sách vẫn trống; nhập lại thành công. Chưa rõ HMR/refresh/context; cần xác định trigger trước khi sửa. |
| KUI-09 | Cao; quan sát cảnh báo runtime, chưa xác nhận backend pending còn tồn tại | Work lifecycle + FE report | Nộp kỳ tháng 9 M02B hiện projection pending; chuyển sang nháp tháng 10 vẫn thấy cảnh báo. Sau lưu nháp và mở lại tháng 9, không còn cảnh báo. Phải kiểm trạng thái quyết định đã commit và scope cảnh báo, không gửi lại lệnh nộp hoặc xóa pending thủ công. |
| KUI-10 | Cao; chặn UI preview/lưu nháp, có đường source tương ứng | Aggregate FE công thức | ONLY(A) làm output port thành A, trùng input A. BE báo AGG_PORT_ID. Giữ guard, sửa identity output FE; kiểm cả IF giữ kiểu và wires/expressions/readback. |

## KUI-01 — hạn trên mindmap

1. PV01 → Dashboard → Độ ưu tiên/Trạng thái → Trung bình/Đang thực hiện → Xem chi tiết Work KHCN.
2. Bung hai nhánh PA02/PX01 → Vừa khung. WA1, WA5, WA6 đều ghi 31/10/2026.
3. PX01 → Work → Thuộc tính chung → Hoàn thành và xử lý lại phần việc → Hạc Thành: hạn 30/09/2026. Màn Giao việc PX01 có WA6 hạn 14/10; PV01 có WA1 hạn 30/10.
4. Evidence: `mindmap-1-2-4.png/.txt`, `hacthanh-auto-completed.png/.txt`, `px01-two-leaves.png`.

Source khoanh: `tdtd-be/DashboardModel/Services/DashboardMindMapQueryService.cs` Map assignment DTO gán `DueDate = assignment.DueDate` (lượt đọc tại line 2600); `tdtd-fe/src/pages/dashboard/mindmap/WorkMindMapPage.tsx` metadata dùng `node.dueDate` (line 435). Chưa xác nhận toàn đường runtime bằng API/DB trong lượt UI này. Đề xuất đối chiếu DueAtUtc ưu tiên của phần tiến độ; phân biệt hạn assignment với ngày nộp từng kỳ. Kiểm một lần, định kỳ, không hạn, fallback, sát nửa đêm VN và assignment sau thu hạn.

Bổ sung M02: Work `6ac562926710e17b1dc416c2`, WA1 PX01 hiển thị hạn 30/10/2026 23:59 trong danh sách; nhưng PX01 tạo con, chọn WA1 thì header nói “Hạn hiện có của cha trực tiếp: 31/10/2026”. Với WA2 PA02 định kỳ tới 30/10, header cha lại đúng 30/10. Chỉ xác nhận sai lệch UI ở nhánh một lần; chưa thử lưu con vượt hạn để kết luận validation BE.

## KUI-08–09 — phát sinh khi dựng M02

- KUI-08: PA02 → M02/Giao việc → chọn nhánh PA02 → Form M02B → Định kỳ → Tháng; trước chọn ngày lịch, dialog biến mất, danh sách vẫn “Chưa có công việc đã giao nào”. Không bấm Hủy/Tạo mới. Nhập lại cùng cấu hình sau đối chiếu danh sách, WA5 tạo thành công. Evidence `m02-pa02-unsaved-dialog-closed.png`. Không coi đây là lỗi mất bản đã lưu.
- KUI-09: cap_hacthanh → M02/REPORT → Form M02B → kỳ 29/09 → nhập bốn trường, Completed 29/09 → Nộp. Report đã hiện Đã nộp/read-only; cảnh báo “Lifecycle đã commit thành công; projection đang chờ hệ thống phục hồi” còn xuất hiện khi mở kỳ 29/10 chưa lưu. Lưu nháp tháng 10 rồi mở lại tháng 9: chỉ còn thông báo Đã nộp/chờ duyệt, không còn banner pending. Chưa kiểm queue/materializer nên không nhận phục hồi BE hoàn tất. Evidence `m02-hacthanh-sept-submitted.txt`, `m02-projection-pending-after-submit.png`, `m02-hacthanh-sept-reopen.txt`.
- Tên đăng nhập Sầm Sơn đã đối chiếu từ bảng Duyệt UI là **cap_samson**. Tên cap_sam_son ở bản nháp runbook là lỗi ghi chép của Lam, đã sửa; không phải lỗi đăng nhập sản phẩm, không reset mật khẩu.

## KUI-02 — mất nháp sơ đồ

Quan sát ở PX01 và PA02: thêm source M01/M01B, chọn field, chỉnh lọc/tính; lúc đọc lại chỉ còn target. Không bấm Tải lại hoặc Bỏ thay đổi. Dựng lại và lưu sau đó thành công. Chưa có trace xác định HMR, load completion, auth refresh hoặc đổi phiên từ tab khác.

Bổ sung M02/PX01: đang dựng `DATE_MIN(GET(A))`, nối vào đích Ngày vận hành, thêm bộ lọc Kỳ nghiệm thu `=09/2026`, thêm khối Tính toán thứ hai; sau thao tác Vừa khung, ứng dụng trở về danh sách nhóm report. Mở lại nháp 0/4 trường, tổng hợp không còn source/công thức chưa lưu. Log có hot update `AggregateRecipeCanvas.tsx` lúc 04:23:48 và 04:24:28, `AggregateMappingEditor.tsx` 04:24:27; Vite connecting/connected lại 04:25:10 và 04:29:49 giờ Việt Nam. Đây là bằng chứng môi trường dev đang cập nhật trong lượt UI, **chưa đủ kết luận mọi lần mất nháp đều do HMR**. Không sửa auth hay tắt guard. Evidence `m02-canvas-reset-to-report-list.png/.txt`, `m02-canvas-vite-events.json`. Tách ca trên bản build ổn định khỏi ca hot-update dev; không lấy sự cố dev này làm kết luận regression production.

Source gợi ý kiểm: `AggregateMappingEditor.tsx` load/useEffect phụ thuộc auth/viewContext; `stores/pickerAuthScope.ts` nhận BroadcastChannel/storage; `stores/authStorage.ts` phát generation. Không log token/mật khẩu. Instrument lý do load bằng generation/context ID và dirty flag; kiểm refresh cùng actor, logout/đổi actor, mở menu lọc, job polling, onApplied và HMR riêng. Khi quyền đổi phải fail closed nhưng không âm thầm mất công sức. Fixture đã lưu không được làm hỏng để chứng minh.

## KUI-03 — checkbox/khung canvas

PA02 tháng 9 → Tổng hợp → nguồn M01B → chọn tiếp nhận, số hóa, giờ công, điểm. Khi hai field đầu xuất hiện ports/filter block, checkbox giờ công có lúc không đổi. Bấm Vừa khung rồi chọn được; lặp với field thứ 4. Kiểm viewport 1280×720, zoom 100%/84%/71%, chuyển nhóm unselected→selected làm đổi chiều cao; pointer và keyboard. Evidence quá trình nằm trong handoff mẫu; chưa có video/event trace, không nhận timing/INP đã đạt.

M02 bổ sung: thêm Lọc rồi Tính toán mới có thể đặt chồng lên khối cũ, điểm nối nhìn như không hoạt động. Screenshot kiểm tại zoom 73% thấy các khối chồng nhau. Bấm **Sắp xếp lại → Vừa khung**, sau đó Lọc → Tính toán nối được. Phân biệt vấn đề placement này với việc checkbox chưa đổi; chưa kết luận cùng nguyên nhân. Replay nên sắp xếp sau mỗi lần thêm khối để không click xuyên/nhầm khối.

## KUI-10 — ONLY làm trùng mã điểm nối

1. PX01 → Work M02 `6ac562926710e17b1dc416c2` → REPORT → Form M02 → một lần → Tổng hợp → Cấu hình chung. Report đích `6ac567606710e17b1dc42b56` đang Nháp.
2. Trường Kỳ nghiệm thu → khối Lọc `=09/2026` → Tính toán có input A → nhập `=ONLY(GET(A))` → Xong. Output vốn là Kết quả đổi nhãn thành A.
3. Nối output A vào Kỳ nghiệm thu đích. Tương tự Có/Không, lọc Bằng/Có → ONLY. DATE_MIN và CONCAT còn output Kết quả.
4. Xem trước: mục lỗi báo “Mã điểm nối bị thiếu hoặc trùng trong khối.” Bấm Lưu nháp sơ đồ vẫn gặp cùng lỗi; **chưa xác nhận nháp bốn trường lưu được**. Không bấm nộp report, không gỡ guard.
5. Bản đã áp dụng an toàn vẫn là r1 chỉ DATE_MIN = 15/09/2026; đã đóng/mở lại xác nhận giá trị còn nguyên. Evidence `m02-preview-duplicate-port.png/.txt`, `m02-px01-date-min-readback.png/.txt`.

Source đối chiếu 07/10: `aggregateFormulaEditorModel.ts:55-56` khai báo chỉ trả valueType/shape nhưng nhánh INPUT trả cả object Port; ONLY tại line 85 spread lại object đó (có id A); IF tại line 83 cũng trả args[1]. `AggregateFormulaDialog.tsx:69` xây output bằng `{...oldOutput,id:e.portId,...output}`, nên id trong output suy kiểu ghi đè identity kết quả. `AggregateMappingValidator.cs:99` yêu cầu IDs duy nhất trong cả node; đây là guard hợp lệ.

Đề xuất owner: giới hạn object suy kiểu ở metadata đúng contract, giữ identity output do editor cấp; rà chỗ khác dùng helper (cả Table descriptor nếu có), không chỉ đổi nhãn A. Kiểm meaningful ONLY cho partialDate/bool/choice, IF giữ kiểu, nhiều output, sửa công thức đã nối, expression.portId khớp output.id, preview/save/readback. Không đổi operator/storage hoặc bỏ AGG_PORT_ID. Source hiện có WIP/cập nhật nóng từ owner khác; lượt mẫu này chỉ bàn giao, chưa sửa các file công thức.

## KUI-04–06 — routing/danh sách

- WA3 `6ac5531bbc221d56c6851d45`, WA4 là hai giao thử nhiều người nhận đã ngừng trước nhập report. Mindmap đã loại đúng; đối chiếu report group count với period detail. Cho phép đọc lịch sử khác việc nhắc người dùng phải làm.
- Popup detail: mở qua Xem chi tiết ở Giao việc; Đóng; kiểm URL và việc tự mở lại. Không dùng goto root làm cách sửa sản phẩm (đó chỉ là cách thoát trong lượt thử).
- PV01/PA02 → Duyệt biểu mẫu: row Form “-”; bấm Xem danh sách cần duyệt, heading cũng “-”. Evidence `pa02-periodic-approved.txt`, `pa02-approval-statuses.png`. Khoanh mapping group Form identity, không suy đoán ACL là nguyên nhân khi chưa kiểm.

## KUI-07 — đã sửa trong lượt dựng mẫu

- `AggregateMappingEditor.tsx`: `useTheme`, truyền lịch `zIndex={theme.zIndex.modal+60}` để trên popup +50.
- `components/common/dateRanger/MantineDateHierarchyFilter.tsx`: `portalProps={{reuseTargetNode:false}}`, tránh dùng portal có aria-hidden từ modal trước.
- Browser: mở popup thật, đọc accessible labels, nhập 01/09–30/09, xem trước/xác nhận lưu thành công. Screenshot before/after `period-data-range-hidden.png`, `period-data-range-fixed.png`.
- Lint hai file PASS; `node node_modules/typescript/bin/tsc -b --pretty false` PASS. Không restart/build host, không đo INP trong lượt sửa này.

## Các quan sát đã làm rõ — không đưa vào danh sách lỗi đang mở

- Hạc Thành/Sầm Sơn đăng nhập được bằng tài khoản đã cấp; không cần reset mật khẩu.
- Một lần giao chọn hai đơn vị tạo một assignment nhiều người nhận; muốn cây 1–2–4 phải tạo riêng từng lá. Không phải lỗi số lượng node.
- Trùng Form/người nhận giữa hai nhánh bị chặn; đã giữ rule và tạo M01B bằng Designer. Owner có thể rà nghiệp vụ riêng nếu cần, không nới rule trong task mẫu.
- PV01 không có nút Duyệt kết thúc khi PX01 chưa đề nghị. PX01 có Gửi đề nghị kết thúc trong Thuộc tính chung. Không thiếu thao tác BE/FE ở case này.
- Lưu thêm ngày hoàn thành sau tính tổng hợp làm lần nộp báo stale; tính lại qua UI rồi nộp thành công. Giữ guard; chỉ tối ưu UX nếu owner chứng minh không ảnh hưởng fingerprint.

## Mẫu báo kết quả sửa cho owner

Mã lỗi → nguyên nhân đã chứng minh → file/diff thuộc scope → ca kiểm cô lập → browser URL/role/fixture → before/after → tác động WIP và chưa kiểm. Giữ số liệu chuẩn M01: PX01 210/180/60/8, PA02 tháng 9 150/123/41/8.5, Work 360/303/101/8.25. Không gọi P05 hoặc Dashboard UAT toàn bộ đạt từ các ca này.
