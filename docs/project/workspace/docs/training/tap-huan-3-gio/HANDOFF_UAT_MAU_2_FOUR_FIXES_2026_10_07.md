# Mẫu 2 — xử lý bốn vướng mắc UAT, 07/10/2026

## Kết quả và điểm tiếp tục

Đã sửa nguồn cho vấn đề ngày, phản hồi chọn đơn vị và vòng đời kết quả xem trước. Sau khi người dùng cho phép restart, đã kiểm UI thật M01: trả lại → nhập ngày 04/10 → lưu nháp → nộp → duyệt → nhận thông báo Đã duyệt; report PX01 còn Nháp đã tổng hợp và lưu được 1 nguồn M01. Chưa nghiệm thu toàn bộ Mẫu 2.

**Trạng thái mới nhất:** đã bổ sung nút mở report nguồn ở từng dòng và kiểm bằng UI thật. BE chạy PID `200544`, DLL có SHA-256 `23DF12E951FE7E1AD800E36100C333A593DA02BB5B1C4F6F915D3BCBDF036AFA`. Người dùng đã cho phép restart; không restart FE, không ghi DB trực tiếp. Chi tiết mới nhất ở phần “Hoàn tất mở nguồn từng dòng và quyết định giờ hạn” cuối tài liệu. Các phần lịch sử bên dưới giữ bằng chứng của các lượt trước.

**Quyết định mới của người dùng:** “Bỏ qua giờ, chấp nhận giờ là 00UTC (7h00). Giờ sẽ làm sau.” Vì vậy 00:00 UTC / 07:00 Việt Nam được chấp nhận cho lượt này; cấu hình giờ khác (gồm 22:00) hoãn, không còn là việc chặn hoàn tất sửa bốn vướng mắc. Giữ nguyên hạn các kỳ và hạn cấp phòng.

## Môi trường và giới hạn bằng chứng

- Workspace `D:\Job\CA\tdtd`; FE và BE là hai repository riêng. Đã kiểm status/diff trước sửa; cả hai có nhiều WIP. Không reset, commit hoặc sửa master/board/issue.
- UI thật: `http://localhost:5173`; các request của tab gọi API `http://localhost:5164`. FE dùng Vite/module của checkout và cập nhật các thay đổi UI qua HMR.
- Không xác nhận được commit/build identity chính xác của BE đang phục vụ. Không đồng nhất mã nguồn vừa build với tiến trình BE đang chạy. API thật trước sửa vẫn trả policy ngày cũ như phần 1.
- Build BE dành cho test dùng `--artifacts-path D:/Job/CA/tdtd/artifacts/uat-four-fixes-20261007/dotnet`, không chạy host, không kết nối DB trong test.
- Có agent khác sửa Canvas và bộ chọn khoảng ngày trong lúc làm. Không sửa `AggregateRecipeCanvas.tsx`; giữ các thay đổi đồng thời trong `AggregateMappingEditor.tsx` và file test có sẵn. Phần thay đổi của phiên này trong editor chỉ là hủy preview sau receipt và không khôi phục job cũ trong quiet readback.
- Tài khoản UI đã dùng PX01 và PV01. Chưa chạy luồng nhập lại bằng px01_doi1 trên BE mới. Không lưu mật khẩu/token vào handoff hoặc bằng chứng.

## 1. Ngày hoàn thành lệch

### Tái hiện và bằng chứng runtime

PX01 mở report kỳ 04/10, mở hộp duyệt dữ liệu quá khứ; trước sửa hiển thị ngày hoàn thành 03/10. Hủy, không xác nhận duyệt.

Request thật `reports/search` và detail cho report `6ac543d8c4378171baa7c549`:

| Trường | Giá trị quan sát |
| --- | --- |
| PeriodId | `6ac542dfc4378171baa7c0fb` |
| periodKey | `20261004` |
| completedDate | `null` |
| periodStart / periodEnd / reportDate | `2026-10-03T17:00:00Z` |
| startedDate | `2026-10-03T00:00:00Z` |
| dueAtUtc | `2026-10-04T00:00:00Z` (07:00 Việt Nam) |
| isHistoricalData / historicalDataApproved | `true` / `false` |
| canEditCompletedDate / requiresCompletedDate | `false` / `false` |
| completedDatePolicyReason | `SCHEDULED_CURRENT` |
| payloadRevision / lifecycleRevision | `3` / `1` |

Assignment WA000005 bắt đầu `2026-10-04T00:00:00Z`, tạo `2026-10-06T18:50:05.361Z`, tức 07/10 01:50 Việt Nam. API chưa cho nhập ngày hoàn thành dù report được đánh dấu lịch sử.

Không đọc/ghi trực tiếp DB: các giá trị trên là giá trị được API trả về. `completedDate=null` chứng minh 03/10 không phải ngày hoàn thành người nhập đã khai; FE dùng `completedDate || periodEnd` để hiển thị. Không lấy trường Ngày triển khai trong biểu mẫu để gán vào ngày hoàn thành nghiệp vụ.

### Nguyên nhân và sửa nguồn

1. FE fallback `periodEnd` khiến ngày cuối kỳ trông như ngày hoàn thành đã khai. Đã bỏ fallback; hiển thị **Chưa khai báo**, hướng dẫn trả lại cho người báo cáo, vô hiệu hóa xác nhận khi thiếu ngày.
2. Ngày kỳ cũ được serialize từ `DateTimeKind.Unspecified` qua múi giờ máy. Giá trị 00:00 ngày 04/10 trở thành 17:00 UTC ngày 03/10. Policy cũ lấy `.Date`, so với StartDate ngày 04/10 và loại kỳ đầu khỏi backfill. Việc dùng ngày UTC của CreatedAt/now cũng phân loại sai quanh nửa đêm Việt Nam.
3. Thêm `ReportCivilDate`: ngày nghiệp vụ ghi dưới dạng UTC-midnight marker; thời điểm thật chuyển qua múi giờ ứng dụng khi cần lấy ngày. Chỉ nhận diện legacy period ở đúng nửa đêm ứng dụng; không cộng giờ đại trà, không dùng sửa legacy period cho input ngày hoàn thành.
4. Policy đọc ngày kỳ cũ theo ngày nghiệp vụ; ngày tạo và ngày hiện tại theo múi giờ ứng dụng. `SchedulePeriodHelper` ghi start/end với Kind UTC và materializer hiện hành ghi reportDate cùng quy ước. Không migrate các bản ghi cũ.
5. Ngày hoàn thành khi normalize được giữ là ngày nghiệp vụ qua BSON. So sánh hoàn thành với hạn theo ngày Việt Nam ở cả FE/BE. Hai đường duyệt BE chặn report lịch sử còn thiếu ngày hoàn thành.

Các file: `tdtd-be/Common/Time/ReportCivilDate.cs`, `SchedulePeriodHelper.cs`; `WorkAssignmentBackfillPeriodPolicy.cs`; `WorkAssignmentMaterializeJobService.cs`; `WorkAssignmentReportService.cs`; `WorkAssignmentReportHistoricalDataHelper.cs`; `WorkAssignmentReviewService.cs`; FE `WorkReviewTab.tsx`, `WorkReportEditorPage.tsx`.

### Đã kiểm và còn thiếu

- UI sau HMR: hộp duyệt đúng report hiện Chưa khai báo, có thông báo thiếu ngày và không cho xác nhận. Đã Hủy, không đổi lifecycle.
- Test cô lập gọi policy thật với dữ liệu legacy 04/05/06/07: 04–06 yêu cầu ngày hoàn thành, 07 là kỳ hiện tại; min đúng ngày kỳ, max đúng 07/10 dù UTC còn 06/10. Test BSON roundtrip năm loại kỳ và ngày hoàn thành, so hạn qua nửa đêm: **25 assertions qua**.
- Chưa kiểm runtime việc hiện ô ngày hoàn thành trên px01_doi1, lưu/đọc lại ngày, nộp và duyệt. Chưa kiểm notification Đã duyệt. Guards BE đã build nhưng chưa chạy HTTP trên host mới.
- `startedDate` legacy 03/10 được ghi nhận, không sửa dữ liệu trực tiếp trong phiên này. Khi tiếp tục cần kiểm readback toàn report và chặn nếu các ngày nghiệp vụ vẫn không thống nhất.

Ảnh thật [trước](images/uat-four-fixes-20261007/01-completion-before.png) / [sau](images/uat-four-fixes-20261007/02-completion-after.png). JSON đã lọc: `artifacts/uat-four-fixes-20261007/tdtd-review-before.json`, `tdtd-date-runtime.json`.

## 2. Giờ hạn định kỳ

### Đã chứng minh

- `AssignmentScheduleDto` / `AssignmentSchedule` hiện có kiểu chu kỳ và cấu hình ngày, không có trường giờ hạn định kỳ.
- `AssignmentScheduleDueHelper` (`Common/Time/AssignmentScheduleDueItem.cs`) lấy occurrence UTC midnight làm DueAtUtc. API kỳ đầu thực tế trả `2026-10-04T00:00:00Z`; UI hiển thị 07:00 là chuyển múi giờ đúng từ mốc đó.
- Đường danh sách/nhập/duyệt và nhắc hạn lấy hạn kỳ đã lưu (`period.DueAtUtc`). Không tìm thấy cấu hình 22:00 bị UI bỏ sót. Đây là khoảng thiếu cấu hình, chưa có bằng chứng 07:00 là quyết định nghiệp vụ đã chốt.
- Khoảng bốn kỳ 04–07/10 là trạng thái nhận bàn giao; phiên này không sinh lại kỳ hoặc sửa lịch. UI PV01 vẫn đọc hạn hai phần việc cấp phòng là 07/10/2026 23:59.

### Đề xuất cần chốt, chưa triển khai

Thêm giờ hạn địa phương `HH:mm` vào cấu hình lịch kèm quy ước múi giờ ứng dụng. Giá trị null giữ hành vi legacy; giá trị 22:00 cho kỳ ngày D sẽ lưu D 15:00 UTC khi múi giờ là Việt Nam. Dùng một hàm tính hạn chung cho sinh kỳ và kiểm hạn cha. Các màn và nhắc hạn đọc cùng DueAtUtc đã lưu. Phải chốt phạm vi áp dụng đối với kỳ tương lai và việc thay đổi kỳ đã tồn tại bằng nghiệp vụ có audit; không tự backfill DB.

Chưa kiểm case đặt 22:00 vì sản phẩm chưa hỗ trợ. Không đổi mặc định toàn hệ thống thành 22:00 để khớp bài tập huấn.

## 3. Chọn đơn vị từ tìm kiếm

### Tái hiện và nguyên nhân

PV01 mở mới Giao việc → Đơn vị nhận việc → tìm PX01. Trước sửa bấm kết quả chưa thấy phản hồi, nhưng khi xóa từ khóa trạng thái chọn đã có. Directory thực tế cho tìm kiếm/cây cùng identity:

- id `6a32451c8c256ef34ea82eee`, mã `100001004`.
- PX01, Phòng Tổ chức - Cán bộ; parent `6a32451c8c256ef34ea82ee0`; selectable=true.
- Không có bằng chứng quyền BE từ chối PX01. Directory đủ điều kiện không đồng nghĩa phiên này đã kiểm toàn bộ lưu giao việc/resolve tài khoản đại diện.

Nguồn FE: cây đã có trạng thái chọn optimistic trong transition; hàng kết quả tìm kiếm chờ cập nhật từ form cha, không có phản hồi tương đương. Mounted test với form cha suspended tái hiện đúng khoảng trống phản hồi này.

### Sửa và kiểm

`UnitSearchPanel.tsx` thêm phản hồi chọn ngay theo cơ chế optimistic của React, `aria-pressed`, dấu chọn và chữ Đã chọn. Đơn vị không hợp lệ/nhóm ảo có lý do; vẫn dùng trạng thái lựa chọn và callback hiện hành, không nới quyền, không tự loại người nhận khác. Lựa chọn không còn hợp lệ nhưng đã chọn vẫn có thể bỏ chọn.

- UI thật: tìm PX01, chọn và bỏ chọn có phản hồi ngay; xóa tìm kiếm thấy cây CAT → PX01 đồng bộ. Đã bỏ chọn rồi hủy dialog tạo việc; không tạo assignment mới.
- **13 test qua** trong `Mau2UnitSearch.test.tsx`, `LazyUnitMultiSelect.selection.test.tsx`, `HybridUnitUserPicker.selection.test.tsx`: chọn nhanh khi parent suspended, đổi/xóa query, search/tree cùng ID, lý do không chọn được; regression cây 166 đơn vị, cache và scope.
- Test fixture kiểm không tải lại directory khi chuyển search/tree. Không có phép đo INP thực tế người dùng; không coi số test này là bằng chứng hiệu năng production hoặc xác nhận người đại diện qua save.

Ảnh thật [trước](images/uat-four-fixes-20261007/03-picker-before.png) / [sau](images/uat-four-fixes-20261007/04-picker-after.png).

## 4. Kết quả xem trước sau lưu

### Tái hiện và nguyên nhân

PV01 mở View cấp Work, form v2, cấu hình r1 có REPORT_TEXT_TABLE(A). Preview có kết quả, sau đó request `aggregate-v2/content/page` trả **409 AGG_INPUT_STALE**. Có lần stale ngay trước thao tác lưu khi đọc lại preview. Không kết luận đây là lỗi thiếu quyền: API trả stale, không phải quyền được mở rộng hay cấu hình mất.

Job quan sát `F34A213610DDB9B7B8F7255F77A61615738803E22BB936C16D99F963BF29C62F` là CONFIG preview. Kết quả dự kiến có configRevision=2, instanceRevision=4; instance đang áp dụng lúc đó configRevision=1, instanceRevision=3. BE kiểm owner/session và stamp/version hiện hành khi cấp quyền đọc nội dung. Các kiểm tra này được giữ nguyên.

Nguồn FE chứng minh sau receipt lưu, preview/job cũ còn tồn tại trong thời gian readback; quiet load còn có thể phục hồi job preview cũ. Ngoài ra object scope/reference dựng lại theo render khiến effect đọc nội dung lặp, dù ngữ cảnh không đổi.

### Sửa

- Hủy preview, prepared/impact và job/lastJob ngay khi receipt xác nhận lưu, trước khi chờ readback. Readback chậm hoặc lỗi không giữ kết quả xem trước thuộc revision cũ.
- Quiet readback sau lưu không tự gắn lại currentPreviewJob cũ. Mở mới vẫn dùng luồng đọc job được server cấp quyền như trước.
- `AggregateContentTableView` giữ reference/scope ổn định khi giá trị không đổi. Key vẫn chứa auth generation, report/view/table, revision/job và reference. Thay tài khoản/ngữ cảnh vẫn hủy đọc cũ và kiểm lại quyền.
- Lỗi stale có thông báo kết quả đã cũ, yêu cầu xem trước/tải lại. Lỗi quyền thật vẫn hiển thị lỗi, không giả thành bảng 0 nguồn.

### Runtime/UI sau sửa

Đã thao tác **Xem trước → Lưu và cập nhật nền** bằng PV01 với recipe hiện có, không đổi nội dung recipe. Lưu làm View tăng **r1 → r2**. Sau receipt, mở Kết quả lần lưu trước đọc được bảng 0 nguồn; đóng trình cấu hình về View cũng đọc được; mở lại trình cấu hình r2 và mở kết quả vẫn đọc được, không cần bấm Tải lại để thoát lỗi preview cũ.

Ảnh [lỗi trước](images/uat-four-fixes-20261007/05-preview-before.png), [sau lưu](images/uat-four-fixes-20261007/06-preview-after.png), [mở lại](images/uat-four-fixes-20261007/07-preview-reopen.png).

**Chỉ chứng minh View PV01 0 nguồn.** Chưa chứng minh tổng hợp report PX01 có M01 đã duyệt, vì M01 vẫn Submitted. Không lấy kết quả View làm bằng chứng tổng hợp báo cáo gửi cấp trên.

## Kiểm thử đã chạy

| Nhóm | Lệnh / kết quả |
| --- | --- |
| BE ngày | `dotnet run --project tdtd-be/tests/Mau2DateChecks/Mau2DateChecks.csproj --artifacts-path D:/Job/CA/tdtd/artifacts/uat-four-fixes-20261007/dotnet -p:NuGetAudit=false` → exit 0, 25 assertions; có nullable warnings trong nguồn hiện hữu |
| Picker FE | `npx vitest run tests/Mau2UnitSearch.test.tsx tests/LazyUnitMultiSelect.selection.test.tsx tests/HybridUnitUserPicker.selection.test.tsx` → 13 passed |
| Aggregate FE | Config `src/features/aggregateMapping/contractChecks/vitest.p04.config.ts`, ba file `mau2.content.mounted.test.tsx`, `p05.content.mounted.test.tsx`, `p04.mounted.test.tsx`; filter save/ASJ1/content → 17 passed, 68 skipped |
| TypeScript FE | `npx tsc -p tsconfig.app.json --noEmit` → exit 0 sau các sửa cuối |
| Định dạng diff | `git diff --check` các file liên quan ở FE/BE → không có whitespace errors; có thông báo LF/CRLF |

Aggregate tests gồm receipt thành công nhưng readback thất bại vẫn bỏ preview cũ; props tương đương không refetch, đổi auth hoặc revision có read lại; 409 stale và 403 không bị trình bày thành 0 nguồn; đọc đoạn nội dung dài, giữ dòng trống thật và loại phản hồi context cũ. Các test dùng fixture, không phải HTTP/JWT/DB UAT. Không chạy production FE build, không deploy.

## Dữ liệu bàn giao và bước kiểm tiếp

| Đối tượng | Trạng thái |
| --- | --- |
| Work `6ac53a44c4378171baa7a1f8` / `pv012026000006` | Giữ nguyên Mẫu 2; chưa xác nhận hoàn thành |
| PX01 WA000003 `6ac53db8c4378171baa7af06` | Giữ nguyên; hạn UI 07/10 23:59 |
| M01 WA000005 `6ac542ddc4378171baa7c0ef` | Giữ lịch 04–07/10, không sửa hạn/kỳ bằng DB |
| Period `6ac542dfc4378171baa7c0fb` | Kỳ 20261004, không dùng thay ReportId |
| Report `6ac543d8c4378171baa7c549` | Lần đọc thật: Submitted, completedDate=null; phiên này không trả lại, nộp lại hay duyệt |
| Kỳ tổng hợp PX01 `6ac53dc3c4378171baa7af22` | Nháp theo mốc bàn giao; đây là PeriodId, ReportId thật đọc được ở lượt tiếp tục là `6ac53e63c4378171baa7b346` |
| View PV01 | Cùng recipe, cấu hình r2 sau save thử; 0 nguồn, đọc được sau mở lại |

ViewId `5146C1594634AC5C9451A657667E25E39A22502DBACF0C2D2D7DE2CB541540B0`; configId `5D7E35BC8EE895A5A1A830C073388D58F6E12A8B802C8840B0C731BB8C9E5033`; instanceId `6E0C29487BC76EE8B215266DC323FE37F4F29C6BFB460F36BA6B75A3D4024E67`.

1. Chủ host cho phép và nạp BE mới; ghi build/process identity. Không tự restart theo handoff này.
2. PX01 trả lại report thiếu ngày qua UI nếu workflow yêu cầu. px01_doi1 mở lại, khai ngày hoàn thành đúng nghiệp vụ, kiểm lưu/readback/nộp. Không lấy Ngày triển khai tự gán.
3. Kiểm kỳ đầu 04/10, kỳ giữa 05 hoặc 06/10 và kỳ hiện tại 07/10; không đổi fixture/flag DB để ép policy.
4. PX01 duyệt khi ngày đúng; kiểm thông báo Đã duyệt. Approved không đồng nghĩa assignment/work hoàn thành.
5. Từ report PX01 còn Nháp, tổng hợp M01 đã duyệt và xác minh dòng **Đội 1 PX01 — Tên hồ sơ thử chưa thống nhất.** Mở nguồn phải đúng ReportId, đơn vị và kỳ 04/10. Kiểm preview → lưu → đọc ngay → đóng/mở lại với nguồn thật.
6. Chốt contract giờ hạn định kỳ trước khi làm 22:00; khi có chức năng, kiểm bốn kỳ, hạn cha và nhắc hạn đồng nhất.

Các bước chưa chạy trên đây là việc còn thiếu, không phải PASS hay nghiệm thu của Lam.

## Tiếp tục sau khi được phép restart — trạng thái cuối 07/10/2026

### Nạp BE có kiểm chứng

Người dùng cho phép restart BE để nạp sửa và kiểm tiếp. Xác minh tiến trình cũ PID `173696` đang giữ cổng 5164 và chạy DLL ở `outputs/dashboard-db-paging-20261007/be-final`; giữ môi trường Development, working directory `tdtd-be`, cổng 5164 như script khởi động hiện hữu. Build trước khi dừng host. Hai script restart chỉ dừng PID đã đối chiếu đúng command line và chủ cổng, chạy tiến trình mới ẩn cửa sổ.

- Lượt đầu 02:41:44 giờ Việt Nam: PID `196660`, DLL `artifacts/uat-four-fixes-20261007/be-runtime/tdtd-be.dll`.
- Trong UAT phát hiện thêm cùng lỗi ngày kỳ ở bộ lọc nguồn tổng hợp, sửa tối thiểu và kiểm lại trước khi nạp bản cuối.
- Lượt cuối 02:54:20 giờ Việt Nam: PID `207144`, DLL `artifacts/uat-four-fixes-20261007/be-runtime-final/tdtd-be.dll`, SHA-256 `A677DFE74AB6CC3A2ABFF57131F26BF5157B977E4C61887FA85CE76BC3FFC3BF`. Log xác nhận Application started, listening localhost:5164; listener đúng PID.
- Metadata lưu ở `artifacts/uat-four-fixes-20261007/restart.json`, `restart-final.json`; log build cuối `be-final-build.log`. Build 0 lỗi, 91 warning nullable/nguồn hiện hữu. Không seed/reset, không thay quyền hoặc cấu hình hạn.

### Ngày hoàn thành và lifecycle: đã chạy UI/API thật

1. PX01 mở đúng report M01 `6ac543d8c4378171baa7c549`, dùng **Trả lại để sửa** với lý do bổ sung ngày hoàn thành; API thành công.
2. px01_doi1 mở lại đúng kỳ `6ac542dfc4378171baa7c0fb`, thấy ô Ngày hoàn thành bắt buộc. Nhập thủ công **04/10/2026** cho dữ liệu UAT, không tự sao chép trường Ngày triển khai. Giữ toàn bộ nội dung, bảng và danh sách M01.
3. Lưu nháp → đọc lại API: `completedDate=2026-10-04T00:00:00Z`, min 04/10, max 07/10, `requiresCompletedDate=true`, reason `ASSIGNMENT_BACKFILL_PERIOD`; payloadRevision=4, lifecycleRevision=2.
4. Nộp lại → Submitted; ngày trên màn xem vẫn 04/10. PX01 mở hộp xác nhận: **Kỳ 04/10/2026, Ngày hoàn thành 04/10/2026**. Chỉ lúc đó mới bấm Xác nhận và duyệt.
5. API approve trả 200. Đội 1 nhận thông báo **02:47 07/10/2026 — M01 — Đã duyệt**; bấm thông báo tới danh sách đúng phần việc, kỳ 04/10 hiện Báo cáo đã duyệt.
6. Mở lại report nguồn bằng px01_doi1: trạng thái Đã duyệt, chỉ xem, nội dung khó khăn giữ đúng. API cuối: `status=2`, `historicalDataApproved=true`, `completedDate=2026-10-04T00:00:00Z`, `isLateSubmission=false`, payloadRevision=5, lifecycleRevision=4. Approved lúc `2026-10-06T19:47:17.502Z` = 07/10 02:47:17 Việt Nam.

Ảnh: [nhập ngày](images/uat-four-fixes-20261007/08-day-input.png), [hộp duyệt đúng ngày](images/uat-four-fixes-20261007/09-day-review-correct.png), [thông báo Đã duyệt](images/uat-four-fixes-20261007/14-approved-notification.png), [đọc lại nguồn đã duyệt](images/uat-four-fixes-20261007/16-source-approved-readback.png). JSON: `tdtd-policies-after.json`, `tdtd-approved-after.json` trong thư mục artifacts của lượt này.

### Kỳ giữa và hiện tại

- 05/10: UI có Ngày hoàn thành bắt buộc; API min 05/10, max 07/10, `canEditCompletedDate=true`, reason backfill. Không nhập nội dung hoặc nộp kỳ này.
- 07/10: API `isHistoricalData=false`, `requiresCompletedDate=false`, `canEditCompletedDate=false`, reason `SCHEDULED_CURRENT`. Phát hiện FE vẫn cảnh báo lịch sử do tự suy lại từ UTC period cũ dù server trả false. Sửa `isHistoricalReportDetail` để tôn trọng boolean server (kể cả false), chỉ fallback khi server không cung cấp. TypeScript FE sau sửa exit 0; UI kiểm lại không còn cảnh báo lịch sử và không có ô ngày hoàn thành.
- Mở kỳ qua UI tự tạo report nháp theo nghiệp vụ: kỳ 05/10 có ReportId `6ac54fc0fb2c9c48e5a4225e`, PeriodId `6ac542dfc4378171baa7c0fd`; kỳ 07/10 có ReportId `6ac54fd9fb2c9c48e5a424af`, PeriodId `6ac542dfc4378171baa7c101`. Giữ nguyên các nháp này, không xóa để làm sạch UAT.
- Danh sách sau duyệt đọc được đúng bốn kỳ 04/05/06/07, không có kỳ thứ năm. Hạn mỗi kỳ vẫn 07:00; 04 đã duyệt, 05 và 07 đang nhập, 06 cần nộp. [Ảnh bốn kỳ](images/uat-four-fixes-20261007/15-four-periods-after.png).

### Tổng hợp có nguồn: lỗi ngày kỳ bổ sung và kết quả cuối

Report đích đúng là **`6ac53e63c4378171baa7b346`**, còn `6ac53dc3c4378171baa7af22` là **PeriodId** dùng trên URL inbox. Không dùng hai ID thay nhau.

PX01 mở report Nháp → Tổng hợp vào báo cáo. Tạo cấu hình nguồn M01 v1 → Khó khăn, vướng mắc → A → `REPORT_TEXT_TABLE(A)` → Bảng Đơn vị — Khó khăn; thứ tự Đơn vị rồi kỳ, Tên đầy đủ. Bộ lọc **Ngày bắt đầu kỳ báo cáo trong 04–07/10**.

Lần preview đầu sau duyệt vẫn 0 nguồn. Đọc mã thấy `AggregateReportSetFilter` lấy ngày UTC thẳng từ `PeriodStart/PeriodEnd`; legacy 04/10 là 03/10 17:00 UTC nên bị loại khỏi khoảng. Đã sửa riêng hai thuộc tính kỳ để đọc qua `ReportCivilDate.ReadPeriodDay`, giữ nguyên ranh giới lọc. `AggregateMongoPreviewReader.Header` cũng dùng cùng chuẩn ngày để phân loại backfill. Không đổi dữ liệu nguồn hoặc nới khoảng thành 03/10. Thêm 6 assertions gồm legacy/new-marker được lấy đúng và 08/10 vẫn bị loại: tổng **31 assertions BE qua**.

Sau nạp BE cuối, chạy lại đúng recipe/bộ lọc:

- Preview hợp lệ, **1 dòng**, nhãn đơn vị thật là **Đội 1**, kỳ **04/10/2026**, nội dung **Tên hồ sơ thử chưa thống nhất.** Không sửa tên đơn vị thành Đội 1 PX01 chỉ để khớp chuỗi kỳ vọng; quan hệ PX01 xác minh bằng assignment/binding và source pin.
- Nguồn góp tính hiển thị ReportId `6ac543d8c4378171baa7c549` v1 Đã duyệt. API lineage chỉ đúng report này, unitId `6ab4cfd73633fb260f57da9d`, occurrenceKey `20261004`, memberId `text`. Source pin Approved, payloadRevision=5, lifecycleRevision=4.
- Bấm **Lưu và cập nhật nền** → cấu hình report PX01 r1; worker `refreshState=COMPLETED`, instance revision=3, generation=2, appliedGeneration=2, `resultReadError=null`.
- Đọc ngay Kết quả lần lưu trước: 1 dòng; đóng cấu hình về màn nhập report: bảng được ghi vào report; mở lại cấu hình và đọc kết quả: vẫn 1 dòng, không báo lỗi stale/quyền, không phải bấm Tải lại để chữa lỗi.
- Report đích **vẫn Nháp** (`status=0`, payloadRevision=2). Chưa nộp report phòng, không đi tiếp PV01 duyệt.

InstanceId `15A45F8D5F816DB3216F4195DD61C7A73E7E56472B8DC78EA7DB42B05D0D8780`; configId `71B0AB313D4D14689D7FF1BCFE8EE1E1B503965294D8AAC231927B3F3C3941C6`.

Ảnh [trước sửa bộ lọc kỳ](images/uat-four-fixes-20261007/10-period-filter-before.png), [preview 1 nguồn](images/uat-four-fixes-20261007/11-source-preview.png), [sau lưu](images/uat-four-fixes-20261007/12-source-saved.png), [mở lại](images/uat-four-fixes-20261007/13-source-reopen.png). API evidence `tdtd-source-saved.json`, `tdtd-source-lineage.json`.

### Phần còn thiếu và giới hạn nghiệm thu

- Giờ hạn 22:00 vẫn chưa hỗ trợ cấu hình và chưa được chốt/triển khai; không thay hạn cũ. Kỳ M01 vẫn 07:00, cấp phòng vẫn 07/10 23:59.
- Đã xác minh lineage/ReportId nguồn bằng UI phần Nguồn góp tính và API, rồi mở report M01 đọc lại. **Bảng nội dung hiện không có nút mở report nguồn trực tiếp trên từng dòng**; C02 “bấm mở nguồn từng dòng” chưa đạt. Không coi đọc API lineage là bằng chứng nút UI đó tồn tại. Cần một phần triển khai riêng nếu yêu cầu điều hướng trực tiếp từ dòng bảng.
- Chỉ 1 báo cáo đội được duyệt; chưa đủ hai đội/tám nguồn/phòng, chưa nghiệm thu nội dung dài, dòng trống thật, thu hồi nguồn hoặc truy nguồn lồng hai tầng.
- View PV01 vẫn là cấu hình r2 0 nguồn đã kiểm ở lượt trước; kết quả 1 nguồn lần này thuộc report PX01, không phải View PV01.
- Phiên kết thúc ở tab px01_doi1 xem report M01 đã duyệt. Không tuyên bố work/assignment đã hoàn thành; không đổi quyền, auto-approval, seed, cờ DB hoặc dữ liệu nghiệm thu.

## Hoàn tất mở nguồn từng dòng và quyết định giờ hạn — lượt tiếp tục 07/10/2026

Phần này thay thế hai điểm còn thiếu về giờ hạn và nút mở nguồn ở phần trước.

- Người dùng chấp nhận **00:00 UTC = 07:00 Việt Nam**; cấu hình giờ khác làm sau. Không đổi lịch 04–07/10, hạn cấp phòng hoặc dữ liệu các kỳ.
- Thêm **Mở báo cáo nguồn** trên mọi dòng Bảng Đơn vị — Nội dung, kể cả nội dung ngắn/trống. Hộp chỉ đọc dùng màn report hiện có, đóng hộp trở lại bảng đang nhập.
- API `POST /api/aggregate-v2/content/source` dùng chung kiểm quyền/ngữ cảnh/phiên bản với page/part. Request chỉ chọn `rowKey`; report nguồn do server lấy từ dòng thuộc manifest đã chốt, kiểm hash nội dung dòng, rồi kiểm quyền đọc report nguồn riêng. Không nhận SourceReportId từ client để điều hướng tùy ý. Response `no-store`; không dispatch snapshot job trong nhánh mở nguồn.
- Hộp ghi rõ đang xem **report nguồn hiện tại**; nội dung bảng vẫn thuộc lần cập nhật bảng. Không trình bày report hiện tại như bản snapshot lịch sử nếu nguồn đã thay đổi. Cơ chế kiểm phiên bản của preview và quyền đọc kết quả cũ được giữ nguyên.
- Khi token đọc kết quả đã lưu được cấp lại, giữ hộp đang mở và kiểm quyền nguồn lại. Đổi tài khoản/ngữ cảnh/phiên bản vẫn hủy reader; nguồn không đọc được hiện lỗi, không hiển thị thành 0 nguồn.

### Kiểm tra mới

| Lớp bằng chứng | Kết quả |
| --- | --- |
| FE cô lập | `mau2.content.mounted.test.tsx` + `p05.content.mounted.test.tsx`, config `vitest.p04.config.ts`: **16 passed**. Bao gồm nguồn ngắn/trống, đóng về bảng, stale/forbidden/unavailable, bỏ phản hồi phiên cũ, token đọc luân chuyển, nội dung dài/đọc từng đoạn. |
| BE controller + snapshot DB chỉ đọc | `tests/Mau2ContentSourceChecks --read-only-mau2`: **12 passed**. Dùng snapshot Mẫu 2 có sẵn, không seed/ghi DB; quyền report là spy để kiểm thứ tự và từ chối, **không phải test JWT/ACL thật**. Kiểm đúng report nguồn, phiên bắt buộc, sai dòng/sai bảng/stale revision bị chặn, quyền đích và nguồn riêng, hai report không đổi revision. Log `artifacts/uat-four-fixes-20261007/source-checks.log`. |
| TypeScript | `npx tsc -p tsconfig.app.json --noEmit`: exit 0 sau sửa cuối. |
| BE build | Build thư mục riêng `source-build` → `be-source-runtime`: **0 lỗi, 91 warnings** hiện hữu. Lần đầu `--no-restore` vướng cache assets của tài khoản sandbox, đã build lại bằng thư mục riêng và cache NuGet sẵn có. |
| Runtime host | Restart đã được phép: 03:21:17 Việt Nam, PID **200544**, localhost:5164, DLL `artifacts/uat-four-fixes-20261007/be-source-runtime/tdtd-be.dll`, SHA-256 **23DF12E951FE7E1AD800E36100C333A593DA02BB5B1C4F6F915D3BCBDF036AFA**. Metadata `restart-source.json`; script chỉ dừng đúng PID/DLL/cổng trước đó. |
| UI/API thật PX01 | Từ bảng đã ghi vào report Nháp, bấm nút ở dòng Đội 1 / 04/10 → source API 200 trả ReportId **6ac543d8c4378171baa7c549**; report detail 200, M01 v1, Đã duyệt, ngày hoàn thành **04/10/2026**, khó khăn **Tên hồ sơ thử chưa thống nhất.** Đóng hộp về đúng report PX01 Nháp. Cũng mở được nguồn từ Kết quả lần lưu trước trong Canvas. |

Kiểm riêng sau HMR ổn định: giữ hộp nguồn trong Canvas hơn **62 giây**, qua hai lần đọc lại instance/token và hai request `/content/source` **200**; hộp vẫn mở đúng M01. Evidence `tdtd-row-source-polling.json`. Không dùng một lần mở tức thì làm bằng chứng đã qua polling.

Lượt **Xem trước thay đổi** mới cũng hợp lệ, 1 dòng; bấm Mở báo cáo nguồn từ chính kết quả preview mở đúng M01. Request có `jobId` (không lấy nhầm kết quả lần lưu trước), source API **200**, ReportId vẫn `6ac543d8c4378171baa7c549`. Evidence `tdtd-preview-row-source.json`. Không bấm lưu thêm cấu hình; đóng preview và để tab tài khoản **PX01** ở report tổng hợp còn Nháp cho người dùng tiếp tục.

Ảnh [mở nguồn trực tiếp](images/uat-four-fixes-20261007/17-row-source-open.png); API evidence `artifacts/uat-four-fixes-20261007/tdtd-row-source-evidence.json` chỉ chứa ID/trạng thái/revision, không chứa thông tin đăng nhập.

Report PX01 giữ **Nháp**, payloadRevision **2**, lifecycleRevision **0**. M01 giữ **Approved**, payloadRevision **5**, lifecycleRevision **4**; ngày hoàn thành `2026-10-04T00:00:00Z`. Lượt bổ sung chỉ mở đọc nguồn, không nộp hoặc duyệt thêm dữ liệu.

Chưa nghiệm thu toàn bộ Mẫu 2: các luồng nhiều đội/nhiều nguồn, thu hồi nguồn và truy nguồn hai tầng vẫn thuộc UAT rộng hơn. Các giới hạn này không phải lỗi còn tồn tại đã được chứng minh trong bốn vướng mắc đang sửa. Không production FE build/deploy, không sửa quyền hay master/board/issue.

## QA bổ sung: bảng nền báo không đọc được khi mở Tổng hợp — 07/10, khoảng 03:41–03:54

Người dùng chuyển phản ánh của agent tài liệu: bảng report phía sau từng báo “Không thể đọc nội dung…”, trong khi Kết quả lần lưu trước có 1 dòng. Cảnh báo này được coi là lỗi QA, không phải ảnh thao tác thành công.

### Bằng chứng và nguyên nhân

- Khi tiếp nhận, DOM của tab PX01 đang hiện đúng cảnh báo tại Bảng Đơn vị — Khó khăn. Report chưa thay đổi ở UI. Tải lại thì request đọc bảng trả 200, 1 dòng.
- Trong lượt mở hộp tiếp theo, bắt được **409 `AGG_INPUT_STALE`** ở request đọc kết quả lưu trước có `listReadId`, payloadRevision=7. Polling cấp lại quyền đọc rồi request trả 200. Không gán request này cho bảng nền: cảnh báo nền ban đầu không còn request cũ trong buffer để đối chiếu ID. Đây là bằng chứng lỗi stale thực tế trong cùng luồng, không phải bằng chứng lỗi quyền.
- Đã chứng minh lỗi phân loại FE: axios interceptor chuyển lỗi thành `{status,errorCode,...}`, nhưng ContentTableView chỉ đọc `response.data.code`. Vì thế stale 409 rơi vào thông báo chung. Test cũ mock trực tiếp cấu trúc Axios thô nên bỏ sót đường normalize thật.
- Bảng nền dùng `payloadRevision` của trạng thái lệnh lưu, tách khỏi snapshot `nativeValues.value`; mở Canvas cũng chưa đọc lại report nền. Kết quả lưu trước dùng quyền đọc riêng được polling làm mới, nên hai vùng có thể lệch trạng thái. Không nới kiểm revision hoặc lấy quyền preview để đọc thay bảng nền.

### Sửa FE

1. Đọc cả `errorCode` đã normalize và cấu trúc lỗi Axios thô; stale được phân biệt với lỗi đọc/quyền thật, kể cả nút mở nguồn.
2. Bảng native dùng `baseRevision` đi cùng đúng snapshot nội dung của nó; không trộn reference cũ với revision của trạng thái lệnh mới.
3. Mở hộp Tổng hợp chủ động đọc lại report. Khi đọc bảng gặp stale, đọc lại report qua host, kiểm đúng tài khoản/report/schema/revision và chỉ tự thử lại tối đa một lần. Trong lúc đó hiện “Đang đọc lại phiên bản báo cáo…”.
4. Lỗi còn lại có nút **Đọc lại bảng**, không cần reload trang. Lỗi quyền thật không tự bị biến thành stale hoặc 0 nguồn. Nháp chưa lưu giữ nội dung và base revision cũ, không âm thầm rebase hoặc ghi đè.

### Kiểm lại, tách từng lớp

- FE content tests: **20 passed** (mau2 + p05). Có lỗi đi qua `normalizeApiError`, phục hồi stale giới hạn một lần, lỗi quyền trực tiếp và lỗi quyền lúc đọc lại report.
- Native scope tests: **19 passed**, gồm snapshot/revision cập nhật cùng nhau và nháp chưa lưu không bị rebase. Test ban đầu giả định dirty draft phải đổi cờ `blocked`; đã sửa đúng bất biến cần giữ là value/base revision, không đổi cơ chế blocked hiện hữu chỉ để đạt test.
- WorkReportEditorRuntimeSafety: **24 passed**. TypeScript app noEmit: exit 0. Diff check không có whitespace errors.
- **Kiểm UI có giả lập lỗi riêng:** dùng CDP thay đúng 1 phản hồi XHR đọc bảng đã được server cho phép bằng 409 `AGG_INPUT_STALE`. UI đi qua trạng thái đang đọc lại → GET report 200 → POST content/page 200 → 1 dòng. Không sửa DB/quyền; đã gỡ toàn bộ interception và cho request preflight đang chờ tiếp tục. Không coi 409 giả lập này là tái hiện backend tự phát sinh lỗi.
- **UI/API thật sau khi gỡ giả lập:** mở Tổng hợp → mở Kết quả lần lưu trước → mở nguồn M01 → đóng nguồn → đóng hộp. Bảng nền và bảng lưu trước đều có 1 dòng Đội 1 / 04/10 / “Tên hồ sơ thử chưa thống nhất.”, không có cảnh báo đọc nội dung. Các request đọc report/bảng/nguồn đều 200. Nguồn vẫn đúng M01 đã duyệt.
- Ảnh thành công mới: [bảng nền sau đóng hộp](images/uat-four-fixes-20261007/18-content-background-after.png), [kết quả lưu trước trong hộp](images/uat-four-fixes-20261007/19-content-dialog-after.png). DOM hai vùng: `artifacts/uat-four-fixes-20261007/tdtd-content-both-after.txt`; trace rút gọn không token: `tdtd-content-stale-qa.json`.

Lượt này chỉ sửa FE, Vite nhận qua HMR; **không restart FE hoặc BE**, không deploy. Không bấm lưu/nộp/duyệt thêm trong QA này. Report PX01 đọc cuối vẫn Nháp, payloadRevision=8, lifecycleRevision=0; đây là trạng thái đọc thật hiện tại, không ép quay lại revision=2 của lượt trước. Tab để lại ở bảng PX01 đọc được để tiếp tục UAT.
