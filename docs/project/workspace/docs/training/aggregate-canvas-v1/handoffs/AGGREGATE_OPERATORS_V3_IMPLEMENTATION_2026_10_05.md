# Bàn giao triển khai phép tổng hợp v3

Ngày 05/10/2026. Owner Lam — BE Aggregate/P05 và FE Tổng hợp. Căn cứ: Yud duyệt `AGGREGATE_OPERATORS_V3_PROPOSED_CONTRACT_2026_10_05.md` bằng câu “Duyệt contract. Tiếp tục.” Gói kỹ thuật đã nối BE/FE và kiểm các mức dưới đây. **Chưa nhận toàn bộ gói tập huấn/UAT/P05 hoàn tất; màn mới chưa có phản hồi duyệt của Yud.**

Không mở task/agent. Bảo toàn WIP, Work/Form nghiệm thu, Flow runtime/phase barrier. Không reset/drop DB, sửa blanket 90 pins, skip migration, mở Basic/Advanced, B12, join/dedup/GROUP BY, Table→List hoặc phase cache optimization. Chỉ tạo fixture `operators-v3-*` và cây tập huấn có nhãn DỮ LIỆU GIẢ; migrations provenance kiểm bình thường trước/sau seed và runtime.

**Cập nhật cây tập huấn:** đã tạo và chạy API/job thật cat_giamdoc → PV01 → PX01 → A/B, PV01 → C/D. PX có 10 phần tử/SUM 37/2 report; PV có 20/SUM 69/3 report; View Work Giám đốc có 20/SUM 69/1 report trực tiếp. Sáu report đã Submit/Approve qua HTTP thật. Fixture riêng đã kiểm Return/nộp lại; View root/assignment Apply/readback và popup truy nguồn PV đã kiểm browser. ID/URL/log/gap ở `AGGREGATE_TRAINING_TREE_RUNTIME_2026_10_05.md`. Không nhận toàn bộ plan/UAT đã đạt.

**Kiểm cuối sau integration:** 40 ca typed filters qua job/page/identity/report-count PASS. Ba View tập huấn dùng r2 tách khối List/Bảng/scalar, report đã nộp giữ r1. FE response guard nhận đúng View identity, BE capture picker/full listing kiểm freshness riêng; editor tại Giao việc mở được, browser preview hoàn tất 20 phần tử/SUM 69/3 report rồi hủy xác nhận, không ghi r3. Chi tiết source và log trong handoff cây.

## Hợp đồng đã triển khai

| Mục | Kiểu/chữ ký | Quy tắc thực tế |
| --- | --- | --- |
| BS02 Lọc Danh sách | FILTER: LIST SET/SINGLE → LIST giữ shape; predicate LIST_PIPELINE v2 COLLECT identity projection | Chỉ điều kiện cùng phần tử, AND/OR. Không sort/top/reduce/đổi field trong khối Lọc. Giữ report hợp lệ có List rỗng, envelope và truy nguồn từng ô. Lọc sau nối dùng schema kết quả |
| BS03 Ngày | DATE_MIN/MAX(DATE_ONLY SET/SINGLE) → DATE_ONLY SINGLE | Bỏ BLANK; không có giá trị → NO_RESULT. Không mở cực trị DATE_PARTIAL |
| BS04 Chọn nhiều | CHOICE_UNION/INTERSECTION(CHOICE_MANY SET/SINGLE) → CHOICE_MANY SINGLE | Trống hợp lệ là tập rỗng; thiếu/quyền lỗi. Hợp/giao tường minh; kiểm mã đích, thứ tự đích theo danh mục đã pin |
| BS05 Trung bình | AVG_PRESENT(NUMBER) và LIST_PIPELINE v2 AVG_PRESENT/WEIGHTED_AVG | AVG cũ giữ mẫu số. Weighted ghép điểm/trọng số cùng phần tử; trọng số trống/âm lỗi, 0 không góp, tổng 0 NO_RESULT, điểm trống với trọng số dương lỗi |
| BS06 Chuỗi | LEN(TEXT SINGLE) → NUMBER; TRIM(TEXT SINGLE) → TEXT | LEN đếm grapheme, nhận metadata native richText để đọc nội dung chữ; plain text chứa thẻ vẫn literal. Rich HTML đọc XML với DTD bị cấm, đúng canonicalizer native. TRIM chỉ đầu/cuối, không sửa khoảng giữa |
| BS07/BS10 Nối | LIST_MERGE v1 VERTICAL/HORIZONTAL: 2–8 LIST → LIST SINGLE | Dọc mapping đủ cấu trúc cùng kiểu, không dedup. Ngang mapping cột riêng, không ghi đè/join; số dòng lớn nhất, phía thiếu BLANK không trace giả, cảnh báo biến và số dòng thiếu |
| BS08 Đếm report | REPORT_COUNT(input, options.basis) → NUMBER SINGLE | ELIGIBLE_SOURCES: report đủ điều kiện kể cả List hợp lệ rỗng. SELECTED_ELEMENTS: report góp phần tử/giá trị sau chọn. Distinct reportId; không suy số phần tử thành số report, không giả missing/forbidden = 0. Table chỉ dùng envelope, giữ engine Bảng |
| BS11 Khác nhau | LIST_PIPELINE v2 COUNT_DISTINCT_FIELD(field) → NUMBER | Field scalar đã cho phép, không DISTINCT object/CHOICE_MANY, không đếm người theo họ tên. Text bắt buộc trim/caseSensitive tường minh; blank theo contract đã duyệt |
| BS12 IF/typed UI | IF/AND/OR/literal TEXT/BOOLEAN/DATE và helper trong formula editor | Tái dùng AST/BE; IF chọn nhánh khi evaluate, không dựng evaluator FE. Giữ nháp/IME/caret khi lỗi. Table bridge cũ còn deferred; không tự mở IF trả List hoặc Table bridge |

Giới hạn: recipe 3/profile `REPORT_MAPPING_EXTENDED_V1` chỉ nâng khi dùng phép mới và BE có capability đúng version. Recipe 1/2 không migration hoặc đổi semantics. Tối đa 200 phần tử đích, 200 trường chiếu, 8 đầu vào nối; Lọc quét tối đa 40.000 phần tử kể cả phần tử bị loại; giữ kết quả có budget 16 MiB, budget operation/thời gian hiện có vẫn áp dụng. Trang tối đa 50; text trên trang rút gọn, toàn văn/truy nguồn tải ở detail. Sáu số lẻ, ties-to-even, không làm tròn giữa phép.

## File và ranh giới owner

| Nhóm/file thật | Method/điểm thực hiện | Side effect |
| --- | --- | --- |
| BE `DTOs/AggregateMapping/AggregateMappingDtos.cs`, `AggregateListDtos.cs`; `AggregateMappingValidator.cs` | Expression.ListMerge, pipeline2, options basis; Parse/extended profile gate | Additive DTO; từ chối phép mới trong recipe cũ |
| BE `AggregateExtendedFunctions.cs`, `AggregateEvaluator.cs` | Infer/Evaluate/EvaluateWithListsAsync, signatures; IF nhánh được chọn; source envelope | Chỉ tính khi preview/job hoặc refresh hợp lệ; không ghi report từ preview |
| BE `AggregateListFilter.cs`, `AggregateListMerge.cs`, `AggregateListPipeline.cs` | Validate/Infer/EvaluateAsync/Matches | Filter, nối, weighted/distinct/present AVG; budget/paging/lineage |
| BE `AggregateSourceResolver.cs`, `AggregatePreviewService.cs`, `AggregateNativePayloadAdapter.cs` | Resolver giữ EligibleSources, Preview warning/đích, metadata richText | Quyền/current Approved/con trực tiếp/pin vẫn ở resolver; không lấy quyền Form làm quyền nguồn |
| BE `AggregateListWire.cs`, `AggregateListContracts.cs` | Encode/Decode/OutputId/CellSources | V1 giữ shape không chèn contributors:null; composite V2 có contributors từng dòng và nguồn từng cell |
| BE `Persistence/AggregateListStore.cs`, `AggregateListReadScopes.cs`, `AggregatePreviewJobs.cs`, `AggregateListDisplayMetadata.cs` | Capture/read page/detail, wire-kind fence, metadata bounded | Snapshot/list grant kiểm lại quyền/pins; nhãn theo source pin và mã trên trang, không tải toàn bộ enum |
| BE `AggregateMongoPreviewReader.cs`, `Persistence/AggregateTargetPayload.cs` | UUID cho native records, Apply native payload | Đi qua authoritative writer; target schema/type/choice/constraints tiếp tục kiểm |
| BE `Controllers/AggregateMappingPreviewController.cs` | Bootstrap extendedOperators với version/profile/operators/signatures/limits | Không API evaluator mới; activation gate hiện có |
| FE `aggregateMapping.types.ts`, `aggregateEditor.types.ts`, `aggregatePreview.types.ts`, `aggregateResponseGuard.ts`, `aggregateExtendedOperators.ts` | DTO/guard/capability upgrade, composite read guard | Từ chối version/capability không khớp; không fake success |
| FE `aggregateEditorModel.ts`, `aggregateFormulaEditorModel.ts`, `AggregateFormulaDialog.tsx` | Parser/typed output, count basis theo occurrence, literal/IF; giữ opaque List khi sửa output khác | Không JSON input/evaluator FE. Count trên nested List giữ nguyên phép nguồn, chỉ đổi basis qua UI |
| FE `AggregateListCalculationDialog.tsx`, `AggregateListMergeDialog.tsx`, `AggregateRecipeCanvas.tsx` | Popup List, nối field cùng kiểu; List filter; +Biến; công thức/count; destination qua post-filter; cổng đích dùng portId thật | Sửa nháp sơ đồ; Apply vẫn diff/confirmation. Không đổi đích/kiểu qua post-filter trong override kỳ |
| FE `AggregateMappingEditor.tsx`, `AggregateListResultView.tsx`, `AggregateListOperationPreview.tsx`, `src/api/aggregateMappingApi.ts` | Version capability, lỗi/cảnh báo Việt, paging/detail/contributors/labels | Hiện dữ liệu API thật, snapshot read theo scope thật |
| BE `AggregateNativePayloadAdapter.cs`, `Persistence/AggregateNativeTargetWriter.cs`; test `NativeUntouchedTargetChecks.cs` | Nháp chưa nhập revision ≤1: native previous NO_RESULT; first Apply khởi tạo đủ definition từ pinned Form | Tái dùng EmptyTables hiện có; authoritative validator/writer vẫn bắt buộc. Source Approved, saved/malformed target không tự được sửa |
| BE `AggregateMongoPreviewReader.cs`; FE `aggregateResponseGuard.ts`, test `view-response.mounted.test.tsx` | Capture picker/full listing độc lập; guard bootstrap View theo work/assignment/binding/view identity | Giữ freshness/auth ở cả hai capture; không chấp nhận đổi context hoặc report→View. Khắc phục editor View bị chặn sai |
| FE `AggregateTableCalculationDialog.tsx` | Điểm mở COUNT report theo capability | Không đổi Table operator, vùng/tọa độ hoặc payload |
| Tests BE `ExtendedOperatorChecks.cs`, `ListMergeChecks.cs`, runtime `ExtendedRuntimeChecks.cs`/`ExtendedBrowserFixture.cs`, hooks Program/ApiChecks/ListRuntimeChecks | Fixture final schema trước pin; migration/checks; runtime/revision/cancel/ACL/lifecycle | Chỉ fixture riêng; không tự sửa dữ liệu nghiệm thu |

Các file chung phía trên thuộc slice Aggregate được giao. WIP ngoài scope trong WorkAssignTab/TaskView, Dynamic Form, appsettings/launchSettings và App_Data được giữ. Không stage/commit/reset/clean. Không chỉnh master/board/issue. Retention current + hai history ở View thuộc slice cơ sở trước v3; không áp retention để xóa report đã nộp hoặc config revisions.

## API và JSON mẫu

Tái dùng `/api/aggregate-v2/editor/bootstrap`, `configs`, `configs/{id}/impact-preview`, `configs/{id}/revisions`, `instances`, `preview-jobs/start`, `preview-jobs/{id}/read|cancel`, `instances/{id}/apply|read`, `reports/{id}/submission-preview`, `lists/page|detail`. Không endpoint tính mới hoặc đường save bỏ writer. Context/expectedRevision/config hash/pins/input fingerprint/auth/session/confirmation vẫn bắt buộc theo API hiện hành. Preview không ghi payload; Apply mới ghi vào nháp; submitted có frozen snapshot và lock owner.

AST nối dọc (không phải toàn bộ request):

```json
{"kind":"LIST_MERGE","listMerge":{"version":1,"mode":"VERTICAL","inputs":[{"ref":"A","project":[{"fieldId":"name","outputFieldId":"name"},{"fieldId":"score","outputFieldId":"score"}]},{"ref":"B","project":[{"fieldId":"name","outputFieldId":"name"},{"fieldId":"score","outputFieldId":"score"}]}]}}
```

Đếm nguồn hoặc đếm report còn phần tử dùng AST `{"kind":"CALL","name":"REPORT_COUNT","arguments":[{"kind":"INPUT","ref":"A"}],"options":{"basis":"ELIGIBLE_SOURCES"}}`; basis thứ hai `SELECTED_ELEMENTS` phải do người dùng chọn, không default ngầm. Weighted dùng `{"kind":"LIST_PIPELINE","ref":"A","listPipeline":{"version":2,"scope":"ALL_SOURCES","take":"ALL","sort":[],"project":[{"fieldId":"score","outputFieldId":"score"},{"fieldId":"weight","outputFieldId":"weight"}],"operation":"WEIGHTED_AVG","valueFieldId":"score","weightFieldId":"weight"}}`.

Kết quả List là reference `kind: LIST_RECORDS_V1|LIST_RECORDS_V2`, `id/hash/count/schema`; page request có `reportId`, `jobId` hoặc `readId`, `snapshotId/hash`, `offset/limit`. Detail thêm `rowKey/fieldId/includeLineage`. Page không trả toàn văn/trace; detail có `contributors` và cell lineage `reportId/version/recordId/fieldId` thực. Đơn vị báo cáo không thay Đơn vị công tác trong field choice.

Lỗi chính: `AGG_EXTENDED_PROFILE_REQUIRED`, `AGG_EXPRESSION_TYPE`, `AGG_COUNT_BASIS_REQUIRED`, `AGG_LIST_MERGE_SCHEMA|TYPE`, `AGG_LIST_FILTER_SCHEMA`, `AGG_LIST_ID_COLLISION`, `AGG_LIST_TARGET_LIMIT`, `AGG_LIST_RECORD_LIMIT`, `AGG_LIST_SELECTION_BUDGET`, `AGG_LIST_WEIGHT_INVALID`, `AGG_LIST_SCORE_MISSING`, `AGG_INPUT_STALE`, `AGG_REVISION_CONFLICT`, `AGG_CONTEXT_UNAVAILABLE`. HTTP 409 cho stale/revision; ACL/context thất hiệu lực không trả bảng rỗng/0 giả. FE có nhãn Việt cho lỗi operator chính.

UUID composite được suy từ instance/member và các identity nguồn đóng góp: inputId/sourceSlot/reportId/listId/recordId. Không dựa ordinal, nội dung, nhãn hoặc revision/hash. Thay cặp phần tử khi nối ngang đổi identity; chỉ thay dữ liệu/pin revision của cùng phần tử không đổi UUID. Collision lỗi, không overwrite. SourceSlot lấy node/port ID ổn định. Composite source/cell index không biến thành khóa join.

## Bằng chứng hiện tại

Evidence ở `outputs/aggregate-operators-20261005/`. Các log lượt lỗi còn giữ để đối chiếu, chỉ log PASS liệt kê dưới là bằng chứng đạt.

| Mức | Kết quả | Giới hạn |
| --- | --- | --- |
| BE engine | `be-v3-checks-wide-final.log`: **299 checks PASS** | In-memory, không API/DB/job. Có old AVG vs AVG_PRESENT; rich/plain LEN; weighted zero/blank/negative; 2+3 vertical/horizontal; same-row AND; post-filter composite; UUID qua pin revision; 200 fields/100 rows/text 100.000 ký tự; budget 16 MiB |
| BE persistence | `be-v3-persistence.log`: **127 PASS** | In-memory store/retention/command checks; không tự coi là transaction/runtime ACL |
| BE build | `be-view-editor-build.log`: bản reader cuối 0 errors/93 warnings hiện có | Lượt trước bị khóa DLL khi host chạy; dừng chỉ host Lam rồi build đạt. Không nói đã sửa 93 warnings |
| FE suite | `fe-training-release-checks-final.log`: **15 files, 218 PASS, 1 skip** sau fixes popup/response guard | Không cộng các lượt. Sau suite chỉ sửa câu hướng dẫn Lọc, kiểm type/lint/build cuối. Skip thiếu AGG_WIDE_RESPONSE là ca decoder response rộng thực; chưa nhận đã đạt. Mounted không thay UAT |
| FE type/lint/build | `fe-training-final-release-typecheck.log`, `fe-training-final-release-lint.log`: PASS. `fe-training-final-release-build.log`: PASS, **16,96 giây** | Kiểm source, không deploy; output build riêng `.tmp/aggregate-operators-v3-training-release-build` |
| API/job/DB | `be-v3-training-release-runtime.log`, run **operators-v3-20261005T025742-057ded**, PASS một lần + định kỳ sau reader fix | Scoped host với JWT/confirmation key riêng, native payload writer và Mongo/Hangfire thật. Không phải toàn bộ Submit/Return HTTP controller policy |
| Runtime case | Nối dọc 5/horizontal 3; page bounded; detail text 8k+; nhãn source pin từng ô; Apply/readback UUID; r1→r2 chọn draft qua impact preview; weighted 225/21 = 10,714286; stale source-current; target authority revoked; stale token; cancel/reopen không ghi | Cancel kiểm queued job; chưa nhận cancel đúng giữa một lượt dài đã đạt |
| Lifecycle seam | Submit/freeze/Approve/Return transaction, nguồn bị lock; frozen composite page đọc được; snapshot lịch sử nguyên; draft refresh native writer và retry không ghi hai lần | Gọi host lifecycle participant trong transaction của fixture, không giả là browser Submit/Return UAT |
| Table hồi quy | Matrix và vertical native Apply giữ header/tọa độ, vùng lọc/blank không giả 0 | Table engine không sửa; không nhận Table→List parity/new horizontal Table |
| Provenance/startup | Migration trước/sau fixture/runtime PASS; `be-v3-host-final.log`, health/ready 200 | Normal Program startup; không skip guard/migration hoặc sửa 90 pin |
| Browser thật | Work fixture `6ac2f9614972619dfa1fbbf6`; mở r2, popup nối ngang, preview/count2, warning thiếu A1, row BLANK, detail hai origins, Xác nhận Apply rồi tải lại; đủ bốn dây tới source/calc/target | Tài khoản riêng fixture qua auth thật. Chưa Yud duyệt màn, chưa đủ drag/touch/IME trên browser cho mọi phép |
| Cây thật/API | `training-populate6.log`, aggregate/variants/submit/views logs trong handoff cây: sáu report, 4 Form, PX/PV/Director Views | Existing actors/picker/review policy thật; không nhận toàn cây định kỳ đã kiểm |
| HTTP Return và native nháp mới | `training-return-final.log`: Submit/Return/resubmit/Approve; `training-fresh-target-final.log`: chưa SaveDraft → preview/Apply; `be-native-untouched-target-release.log`: 11 PASS | Giữ UUID/payload/tọa độ, chặn sửa Submitted; không lifecycle seam. Không sửa dữ liệu cũ để làm ca |
| Kiểm integration View | `fe-training-view-detail.log`: 21 PASS; suite cuối có 8 ca bootstrap View identity; `training-view-editor-read-final.log`: API sequence PASS; `training-provenance-startup.log`: startup/migration 200 | Popup giữ View identity; browser nguồn PX và text 17.550 ký tự đã đọc đúng. Không cộng số tests các lượt |
| Typed filters/canvas hiện tại | `training-typed-filters.log`/JSON: **40 ca PASS**; `training-view-typed-blocks.log`: PX/PV/Work View r2; browser preview 20/69/3 | Job/page xác minh danh tính/số report; browser hủy xác nhận không ghi r3. Chưa đủ UI01–08 hoặc mọi Table popup kế thừa |

Lượt harness lỗi đã xử lý: đầu tiên tạo config thứ hai cùng binding bị `AGG_CONFIG_EXISTS`; đổi sang revision của cùng config. Kiểm stale ban đầu đổi request.ExpectedRevision trước khi dùng token cũ nên nhận lỗi fingerprint; dùng đúng input preview cũ mới kiểm revision conflict. Cancel khi cùng session/input bị dedup tới job COMPLETED; ca queued cancel dùng session kiểm riêng để tạo job mới. Các lượt fail không nhận đạt; fixtures riêng còn giữ, không xóa/đổi pins để che lỗi.

## Fixture và đường Yud xem

Browser fixture giữ nguyên để xem:

- URL: `http://127.0.0.1:5173/works/6ac2f9614972619dfa1fbbf6?tab=REPORT`.
- Actor riêng: `operators-v3-20261005t011159-792a85-once-6ac2f9614972619dfa1fbbf2`; thông tin đăng nhập phiên kiểm do Yud đã cấp, không ghi token/mật khẩu vào handoff.
- Report đích `6ac2f9614972619dfa1fbbff`; nguồn `6ac2f9614972619dfa1fbc02`, `6ac2f9614972619dfa1fbc05`; Form đích `6ac2f9614972619dfa1fbbf9`.
- Config `24EF6F65D88C26EA65F6CA153358E92873FB8C448DD686101C88C93F2B6E9F2E`, instance `1F3822F9637CBA361337C68F46967B96FA5DDB51ACC8EA13697CD2EE9812266D`, r2 nối ngang. Có 2+3 phần tử, 6 trường nguồn/12 trường đích; left_/right_ là **tên field fixture**, không đổi schema published để Việt hóa fixture cũ.
- Thao tác: mở Tổng hợp vào báo cáo → Thiết lập tính toán → Nối Danh sách; Công thức và đếm báo cáo chỉnh basis của output count, giữ phép nguồn; Xem trước → đọc warning → Xem nguồn/Đọc đầy đủ → Xác nhận. Tải lại đọc snapshot đã lưu. Chuyển Phạm vi sửa sang cấu hình chung mới được thay cấu trúc; override không mở thêm cấu trúc.
- Ảnh: `v3-browser-canvas.png`, `v3-browser-lineage.png`, `v3-browser-count-basis.png`; dữ liệu giả rõ ở nhãn nguồn/đích. Chưa phản hồi Yud duyệt màn.

Fixture kiểm lifecycle lượt trước khác browser fixture, không dùng URL trên để suy ID của lượt khác. Lượt rerun cuối `operators-v3-20261005T025742-057ded` có manifests riêng cùng thư mục evidence; bảng sau giữ IDs lượt `014153-497d09` để tra lịch sử:

| Mode | Work / report / sources | Manifest đầy đủ |
| --- | --- | --- |
| Một lần | `6ac3006396570ab3e3360c2b` / `6ac3006396570ab3e3360c34` / `6ac3006396570ab3e3360c37`, `6ac3006396570ab3e3360c3a` | `fixtures-operators-v3-20261005T014153-497d09once.json` |
| Định kỳ | `6ac3006b96570ab3e3360c5b` / `6ac3006b96570ab3e3360c64` / `6ac3006b96570ab3e3360c67`, `6ac3006b96570ab3e3360c6a` | `fixtures-operators-v3-20261005T014153-497d09periodic.json` |

Manifests giữ config/instance/recipe IDs, không chứa credential/token. Harness final schema trước assignment/report pin, authoritative native writer; không sửa published tại chỗ để sửa dữ liệu.

## Việc tiếp và phần chưa nghiệm thu

1. Cây tập huấn một lần và 40 ca typed filters API/job đã tạo/chạy, giữ fixture cho Yud; xem handoff cây. Các bước UI chưa có evidence cuối giữ mở. Không mở phase khác/P05 closure.
2. Chưa chạy đủ UI01–UI08, mọi tổ hợp IF/filter/operator ở mọi cấp, browser Submit/Return đầy đủ hoặc mọi kiểu lịch theo cây. View root/assignment và HTTP Return có evidence scoped thật; không lấy chúng thay toàn bộ browser UAT.
3. Probe engine 166×200×4.000 ký tự chỉ đo in-memory; 200 fields và text 100.000 ký tự cũng là engine check. Chưa SLA DB/API/MinIO/export, chưa tối ưu cache/write-behind. V3 có giới hạn rõ thay vì thành công giả khi vượt budget.
4. Quyết định business cần hỏi lại: **không có** cho contract v3 đã duyệt. Nếu bộ tập huấn phát hiện thiếu capability/ACL hoặc nghiệp vụ ngoài contract, ghi case cụ thể để Yud chốt; không tự mở rộng Table/IF List/join.
5. Đề xuất cập nhật cho owner tổng: ghi contract đã duyệt, operator v3 có source/runtime scoped evidence; bộ tập huấn/UAT vẫn mở; không lấy các log này đóng P05/P06 hoặc sửa issue/master/board của owner khác.
