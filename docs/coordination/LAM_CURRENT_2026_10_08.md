# Bàn giao hiện hành cho Lam Tổng và 5 Lam — 08/10/2026

> Navigation: `ARCHITECTURE_MAP.md` → `DOCUMENTATION_MAP.md` → tài liệu này.
> Type: Map / Work log.
> Work code: `P20261008-lam-intake`.
> Status: tiếp nhận source và thống nhất tài liệu; kết quả checkpoint cuối ở `LAM_CHECKPOINT_RESULT_2026_10_08.md` cùng thư mục.

## Cách dùng mốc này

Yud giao Lam Tổng tiếp nhận mốc code cuối, commit, push và thống nhất tài liệu cho năm Lam. Điểm làm việc chung là `D:\Job\CA\tdtd`; source hướng dẫn nằm riêng tại `D:\Job\CA\tdtd-guide`. Đọc tài liệu này trước khi dùng trạng thái, giới hạn hoặc prompt lịch sử. Quyết định trực tiếp mới nhất của Yud được ưu tiên; tiếp theo là contract đã chốt, bằng chứng có phạm vi, tài liệu hướng dẫn và cuối cùng là đề xuất/lịch sử.

FE và BE là hai Git repository riêng. Root, tài liệu chung, deploy/scripts và guide chưa có Git repository hoạt động. Checkpoint giữ nhánh đang chứa WIP `refactor/canvas-unified-form-2026-09-17` theo ngoại lệ đã ghi trong `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md`; nhánh này không tự trở thành bản production. Hai commit dùng chung mã việc. Source code, test có ý nghĩa và ba thay đổi cấu hình BE đã có bàn giao được đưa vào checkpoint; uploads, dữ liệu runtime, build/cache và bản `.orig` được giữ ngoài commit.

Bộ tài liệu chọn lọc được chụp vào `docs/project/workspace/` và `docs/project/guide/` trong cả FE và BE. Đây là snapshot tài liệu dùng khi đọc một checkout độc lập. `LAM_DOCUMENT_MANIFEST_2026_10_08.json` ghi đường dẫn nguồn và SHA-256. Nội dung chung ở root là nguồn bảo trì; hai bản `docs/coordination/` được sinh lại từ nguồn đó. Một số tài liệu snapshot còn dẫn tới hồ sơ lịch sử ngoài bộ chọn lọc; đọc hồ sơ đầy đủ ở workspace gốc khi cần.

## Phạm vi và thứ tự đọc của năm Lam

| Agent / chat | Phụ trách | Tài liệu đọc theo thứ tự, tính từ root TD-TD nếu không ghi guide |
|---|---|---|
| Lam Vận Hành | Phân quyền, báo cáo, nộp/nộp lại, duyệt/trả lại, readback và phục hồi sau commit | `docs/training/tap-huan-3-gio/CONTRACT_THEO_DOI_TIEN_DO_WORK_ASSIGNMENT_REPORT_2026_10_06.md`; `HANDOFF_HOAN_THIEN_LUONG_VA_INP_2026_10_06.md` và `HANDOFF_FIX_LIFECYCLE_CHUONG_NGAY_UAT_2026_10_06.md` cùng thư mục; `docs/features/FEATURE_12_REQUEST_CENTER_AND_ACTION_NOTIFICATIONS.md`; `HANDOFF_UAT_MAU_2_FOUR_FIXES_2026_10_07.md` đọc đến cập nhật cuối |
| Lam Tài Liệu | Nội dung, giáo án, ảnh, nguồn sinh HTML/Markdown và QA hướng dẫn | Guide: `AGENTS.md` → `REQUIREMENTS.md` → `PLAN.md` → `qa/TRAINING-V087-HANDOFF.md` → `qa/QA-V087.md` → `qa/training-v087/IMAGE-GAPS.md`; sau đó `qa/QA-HOSTED-GUIDE-20261008.md`; ở TD-TD: `docs/training/tap-huan-3-gio/README.md` và `HANDOFF_RA_SOAT_TAI_LIEU_VA_GIAO_AN_2026_10_07.md` |
| Lam Công Việc | Job nền/lập lịch TD-TD: Hangfire, worker, hàng đợi, retry, chống chạy chồng và vận hành job | `docs/features/FEATURE_10_OPERATIONS_HISTORY_AND_JOB_RUNS.md`; `docs/features/PATCH_2026_05_20_MONGO_PHASE4_JOB_RUN_OPS_OVERLAP_GUARD.md`; `docs/training/aggregate-canvas-v1/handoffs/PERIODIC_P05_QUEUE_MINIO_RETRY_2026_10_06.md`; `PERIODIC_P05_CLOSEOUT_2026_10_07.md` cùng thư mục; đối chiếu `tdtd-be/Jobs/HangfireRecurringJobRegistrar.cs` và `tdtd-be/Services/Common/JobRunManagementService.cs` |
| Lam Giao Diện | Bố cục, theme, tương tác/tooltip, Dashboard/mindmap, Inbox, texture nhỏ | `docs/training/tap-huan-3-gio/HANDOFF_THEME_REFACTOR_2026_10_06.md`; `HANDOFF_DASHBOARD_DB_PAGING_RECIPIENTS_2026_10_07.md`; `HANDOFF_MINDMAP_POPUP_NOTICE_2026_10_07.md`; `HANDOFF_CONG_VIEC_GOM_NHOM_THONG_BAO_PHAN_TRANG_2026_10_06.md` cùng thư mục; `docs/training/aggregate-canvas-v1/handoffs/KHCN_UI_ISSUES_FOR_OWNERS_2026_10_07.md` |
| Lam Thống Kê Tổng Hợp | Aggregate/Canvas, nguồn và quyền đọc, công thức/lọc, preview/apply, cập nhật sau lifecycle, thống kê | `docs/training/aggregate-canvas-v1/handoffs/PERIODIC_P05_CLOSEOUT_2026_10_07.md` → `PERIODIC_P05_APPLICATION_UAT_HANDOFF_2026_10_07.md` → `AGGREGATE_OPERATORS_V3_IMPLEMENTATION_2026_10_05.md` → `AGGREGATE_DATA_TYPE_TEST_MATRIX_2026_10_05.md` → `AGGREGATE_FORMULA_SOURCE_STATUS_IMPLEMENTATION_2026_10_06.md` → `KHCN_UI_ISSUES_FOR_OWNERS_2026_10_07.md`; bài M1 tại `M01_PRACTICE_HANDOFF_FOR_COORDINATOR_2026_10_07.md` cùng thư mục |

Lam Tổng giữ phạm vi, thứ tự ưu tiên, checkpoint và kết quả bàn giao. Một lỗi chạm nhiều lớp có một owner chính theo nguyên nhân và owner phối hợp cho các lớp còn lại. Lam Công Việc hiện phụ trách job nền; tên chat không giao toàn bộ tính năng Work cho Lam này. Agent nhận việc phải đọc status/diff, ghi các file nhận sửa và tránh ghi đè WIP; việc vượt phạm vi hoặc chưa rõ nghĩa nghiệp vụ trả về Lam Tổng/Yud để chốt.

## Những điểm đã thống nhất

| Mục | Quy tắc hiện hành | Phần cũ đã được thay / ảnh hưởng |
|---|---|---|
| Phân biệt trạng thái | Work, assignment, kỳ báo cáo, report, obligation, tiến độ thực hiện, hiệu quả và nguy cơ hạn có nghĩa riêng. `Approved` là trạng thái report. | Không dùng job `Completed` hoặc report Đã duyệt để kết luận Work/assignment hoàn thành. COUNT phải ghi đối tượng, phạm vi và cơ sở đếm. |
| Phục hồi sau commit | Giữ `CompletionProjectionPending` tới khi mọi side effect, queue/materializer, projection và CAS thành công. Retry đọc actor của quyết định đã commit. | Không xóa pending bằng tay, không dùng actor của job/retry để thay actor nghiệp vụ; không nộp lần nữa chỉ vì readback lỗi. |
| P05 / P06 | P05 đã chốt triển khai/kiểm chứng kỹ thuật trong phạm vi closeout. Bàn giao UAT ứng dụng 07/10 là điểm tiếp tục. | `PERIODIC_P06_UAT_PREPARATION_2026_10_06.md` là chuẩn bị lịch sử. P06/nghiệm thu người dùng chưa đạt. |
| M1 và Mẫu 2 | KHCN M1/M2 đang tạm dừng theo Yud. Không dựng thêm hoặc reset. M1 Số hóa hồ sơ khác M01 Kho hồ sơ. | Kết quả sửa UI không tự cho phép tiếp tục bài thực hành. M1 core dùng bốn trường Số, SUM/AVG/GET, không có ONLY/IF hoặc Bảng/Danh sách. |
| Bằng chứng Mẫu 2 | Ngày 04 của cả bốn mô hình đã duyệt theo PA02 mới nhất; mới có 4/12 report quá khứ. Hạn cấp đội 07:00 Việt Nam = 00:00 UTC; quá khứ cần xác nhận. | Không giữ trạng thái PA02/M03/M04 chưa chạy ở các handoff đầu. Ngày 05–07, tự duyệt hôm nay, đủ nguồn, report phòng và truy nguồn hai tầng còn UAT. |
| Giáo án / guide | Guide offline v0.8.7; 180 phút, Tổng hợp 50 phút, nghỉ 20 phút, đang chờ duyệt. Một trang/một menu; sửa nguồn rồi sinh HTML/Markdown. | Bản lịch nghỉ 10 phút và điểm dừng chỉ Nháp là lịch sử; chương Form hiện hướng dẫn tới Nhập thử/Công bố theo v0.7. Không suy GUIDE_QA thành LIVE_UAT. |
| Hướng dẫn web | FE phục vụ `public/huong-dan/`; script `scripts/sync-user-guide.mjs` sinh assets/metadata. Sửa đóng gói 08/10 bỏ Base64/srcdoc của loader web, tách JS/CSS/XLSX và thêm CSP. | Quyết định ban đầu chưa tích hợp đã được thay bởi tích hợp tĩnh hiện có. Guide offline vẫn là nguồn tự chứa. Gói qa2 07/10 chưa mang sửa web 08/10. |
| Theme/mindmap | Dùng handoff theme refactor thay dark-only cũ. Popup từ mindmap chỉ xem kết quả (`resultOnly`), không mở cấu hình editor. Danh sách đã chuyển count/sort/skip/limit xuống DB theo handoff 07/10. | Compact cards/in-memory paging và mở cấu hình từ mindmap trong handoff cũ không còn là chỉ dẫn hiện hành. |
| Cấu hình BE được checkpoint | Mongo URI dùng `replicaSet=tdtd-rs`; HTTPS local 7443 thay 7232; `WorkInbox.NotifyReviewRequired=true` trong Development. | Ba delta đã có bàn giao. Mặc định cấu hình trong source không xác nhận cấu hình hiệu lực trên host; không ghi đè env/key/config triển khai. |
| Job | Quản trị job yêu cầu `SYSTEM_ADMIN`; recurring có wrapper chống chồng, manual gọi trực tiếp service. Đối chiếu lease/CAS/idempotency của từng queue. | F10 có batch 5/20 cũ trong khi fallback source là 50/500. Kiểm cấu hình hiệu lực trước kết luận; không mở Flow/thống kê đang bị chặn phát hành. |

## Công việc còn mở và owner tiếp nhận

| Việc | Owner chính / phối hợp | Bằng chứng và điểm dừng |
|---|---|---|
| KUI-08/09 | Lam Giao Diện / Lam Vận Hành | Giữ mở theo KHCN handoff; cần kiểm trên đúng bản chạy. |
| KUI-10 `ONLY(A)` trùng identity port | Lam Thống Kê Tổng Hợp / Lam Giao Diện | Source hiện còn trả cả input Port và spread ghi đè output id. Giữ `AGG_PORT_ID`; cần kiểm ONLY/IF, wires, preview/save/readback. Chưa sửa trong checkpoint này. |
| Ca lọc ngày độc lập | Lam Thống Kê Tổng Hợp / Lam Tài Liệu | Nháp/xem trước riêng: 01–30/09 → 01–29/09 → khôi phục. Kỳ vọng 150/123/41/8,5 → 70/63/21/9 → 150/123/41/8,5; chưa có bằng chứng đạt. |
| UAT P06 và nghiệp vụ rộng | Lam Vận Hành / Lam Thống Kê Tổng Hợp / Lam Công Việc | Đọc ma trận UAT ứng dụng, giữ đúng nguồn/role/revision/MinIO/proxy; các ca kỹ thuật cô lập không thay UAT người dùng. |
| Ảnh, Dashboard/hạn và thông báo | Lam Tài Liệu / Lam Giao Diện / Lam Vận Hành | Giữ manifest ảnh và gaps v0.8.7. Thông báo đã thấy sau reload/lượt đọc sau; chưa đo tự cập nhật hoặc độ trễ. |
| Guide web trên server / antivirus | Lam Tài Liệu / Lam Giao Diện, Lam Tổng điều phối host | Đóng gói/QA 08/10 có bằng chứng; chưa deploy, so bytes online hoặc quét lại antivirus đã báo. Không kết luận cảnh báo đã hết. |
| Cửa sổ mở report khi query cũ đang chạy | Lam Vận Hành / Lam Giao Diện | Audit source thấy gate `startedTimeStamp >= mount` có thể giữ loading khi RTK tái dùng request bắt đầu trước mount. Đây là nghi vấn từ source, chưa tái hiện browser/runtime; đưa vào lát sửa tiếp, không tuyên bố đã sửa. |
| Kiểm kiểu mã test / full FE suite | Lam Giao Diện / các owner module | Lượt checkpoint có 9 lỗi `test:typecheck`; app TypeScript/build được ghi riêng. Full FE 1.090 PASS / 256 FAIL / 240 SKIP là lượt thăm dò trước sửa cuối qa2, không phải kết quả source hiện tại. |

## Mốc source và bằng chứng

Mốc đầu vào FE `b4ab9100806db9bfc3e2d3d6a742429ed54866b3`, BE `3a1c0f411c037a298a5adb8d581753bc61b6d24c`, cùng nhánh đã nêu. Đối chiếu `artifacts/tdtd-release-20261007-qa2/source-final.json` với `artifacts/lam-checkpoint-20261008/source-intake.json`: BE khớp toàn bộ 1.743 file; FE có 7 file khác hash, đều thuộc đóng gói guide, ngoài ra khớp source qa2. Manifest qa2 giữ bằng chứng riêng của lần đóng gói đó.

Guide web hiện có HTML 750.953 bytes, 170 assets, SHA-256 `6a17a46173825c4cd3740fd203f9c3cf2570df256261ce0d4ef8f5b71746fbe8`; HTML offline nguồn SHA-256 `3519c1470200951916c64060a1ad9bb3d47ac58f9e2ec76df33dafed85c67b5b`. Deploy phải dùng cả thư mục guide và metadata/bundle FE tương ứng. Không gọi qa2 là gói chứa toàn bộ checkpoint 08/10.

Kết quả build/test mới, allowlist, commit và push được ghi ở `LAM_CHECKPOINT_RESULT_2026_10_08.md`; logs dưới `artifacts/lam-checkpoint-20261008/`. Phân biệt kết quả source/diff, test cô lập, build, browser QA guide và UAT thật. Lượt tiếp nhận này không tạo thêm bằng chứng API/JWT/DB, dữ liệu UAT, triển khai hoặc chấp thuận sản phẩm.
