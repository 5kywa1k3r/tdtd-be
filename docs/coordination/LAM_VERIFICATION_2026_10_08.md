# Kiểm chứng checkpoint Lam — 08/10/2026

> Navigation: `LAM_CURRENT_2026_10_08.md`.
> Type: Work log.
> Work code: `P20261008-lam-intake`.
> Status: kiểm kỹ thuật hoàn tất trong phạm vi dưới đây; chưa chấp thuận triển khai hoặc UAT toàn sản phẩm.

| Kiểm tại source tiếp nhận | Kết quả | Evidence từ root TD-TD |
|---|---|---|
| FE app TypeScript và Vite production build | PASS, 9.401 modules; output riêng trong artifacts | `artifacts/lam-checkpoint-20261008/fe-build.log` |
| FE 13 file Vitest chọn theo source thay đổi | 117 PASS | `artifacts/lam-checkpoint-20261008/fe-targeted-tests.log` |
| FE ba file Aggregate mounted thay đổi | 51 PASS | `artifacts/lam-checkpoint-20261008/fe-aggregate-tests.log` |
| Choice table mounted | PASS; JSDOM có warning layout MUI | `artifacts/lam-checkpoint-20261008/fe-choice-mounted.log` |
| Guide hash/metadata/170 assets và isolated formula checks | PASS | `artifacts/lam-checkpoint-20261008/fe-guide-formula-checks.log` |
| FE `test:typecheck` | FAIL, 9 diagnostics có sẵn trong baseline; app TypeScript/build PASS riêng | `artifacts/lam-checkpoint-20261008/fe-test-typecheck.log` |
| BE build thư mục artifacts riêng | PASS, 0 lỗi, 91 warnings hiện hữu | `artifacts/lam-checkpoint-20261008/be/build.log` |
| BE unit/contract runner | 231 PASS | `artifacts/lam-checkpoint-20261008/be/unit.log` |
| BE Aggregate in-memory preview / report-set core | 440 + 31 PASS | `artifacts/lam-checkpoint-20261008/be/aggregate-preview.log` |
| Synthetic TUS/multipart | PASS: create 5.120 bytes, PATCH/HEAD/token gate, multipart 5 KB tới stub storage; trên 5 MiB bị chặn | `artifacts/lam-checkpoint-20261008/be/tus.log` |
| Source diff whitespace / tài liệu snapshot | Source PASS; 42 nguồn tài liệu copy nguyên bytes và kiểm SHA-256 | `LAM_DOCUMENT_MANIFEST_2026_10_08.json` |

BE unit runner đọc source bằng cách tìm lên từ thư mục binary. Lượt đầu ở artifacts có ba lỗi tìm source root; các assertion khác qua. Đã copy tám file source nguyên bytes vào artifact test, xác minh hash rồi chạy lại cùng runner được 231 PASS. Giữ log đầu `be/unit-first-path-failure.log` và hash `be/unit-source-snapshot-hashes.json`; không sửa test/sản phẩm để che lỗi.

Lượt đầu FE qua `npm.ps1` bị mất native arguments `--outDir`/`--config`. Đã sửa cách gọi thành `npm.cmd`/Vitest `.cmd` và chạy lại thành công. Logs bảng trên là lượt cuối. Các lỗi này thuộc cách gọi công cụ, tách khỏi lỗi `test:typecheck` có sẵn.

Chín lỗi kiểm kiểu test thuộc các file `AssignmentCompletionDialog`, `browser/aggregate-layout/preview`, `WorkReportEditorRuntimeSafety`, `khcn-canvas-preview` và `workInbox.review`. Bốn file đầu không đổi trong checkpoint; các lời gọi `exact: true` gây lỗi trong `workInbox.review` đã có ở HEAD đầu vào. Không báo toàn bộ mã test đã qua typecheck.

Kiểm toàn bộ staged diff có whitespace trong assets guide sinh sẵn và Markdown snapshot lịch sử. Các tệp đó giữ nguyên bytes để bảo toàn hash/bằng chứng; không chỉnh bundle hoặc nguồn lịch sử chỉ để xóa whitespace. Source code và tài liệu điều phối mới qua `diff --check` khi loại hai cây `public/huong-dan/` và `docs/project/`. Log đầy đủ ở `fe-full-staged-whitespace.log`; không báo toàn bộ payload qua whitespace check.

Audit đã đối chiếu nghi vấn lineage của bảng đếm trong task view: API `AggregateViewsController.ReadInstanceAsync` dùng `AggregateReadCommands.WithoutSourceLineage`, xóa mọi `TABLE` row note, cell lineage và `LineageRef`; do đó persisted task read không cung cấp các ID nguồn cho provider FE. `Applied` cũng được bỏ ở ranh giới đọc này. Nghi vấn từ FE được giải thích bằng contract BE; không gọi đó là sự cố rò rỉ đã xác nhận.

Kiểm tĩnh dấu hiệu JWT/private key/GitHub/OpenAI/AWS key trong gói hướng dẫn và bộ tài liệu không thấy pattern có độ chắc cao. Không rà từng pixel ảnh hoặc xác nhận antivirus sạch. Browser QA guide 08/10 là bằng chứng kế thừa trong `guide/qa/QA-HOSTED-GUIDE-20261008.md`, không phải browser sản phẩm vừa chạy lại.

Không chạy full FE suite, shared-host browser, HTTP/JWT/DB UAT thật, product jobs, MinIO/proxy thật hoặc deploy trong lượt checkpoint này. KUI-08/09/10, ca lọc thời gian và UAT P06 giữ trạng thái mở. Lỗi loading khi query bắt đầu trước mount là nghi vấn source cần lát tái hiện riêng. Các kết quả trên phục vụ checkpoint code đang làm, không chốt release acceptance.
