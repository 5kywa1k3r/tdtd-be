# Tập huấn ba giờ — mục lục, cách đọc và từ khóa

> Điểm tiếp nhận 08/10/2026: [Lam Tổng và 5 Lam](../../coordination/LAM_CURRENT_2026_10_08.md). Guide hiện hành v0.8.7, chương Form tới Nhập thử/Công bố; giáo án 180/50/20 phút còn chờ duyệt. Các đoạn chỉ dừng Nháp, nghỉ 10 phút hoặc mô tả sáu bước/chín ảnh bên dưới là lịch sử của các phiên bản trước. Mốc mới được đọc trước giới hạn cũ; bằng chứng UAT lịch sử giữ nguyên phạm vi.

Ngày lập: 05/10/2026; cập nhật nội dung buổi: **07/10/2026**. [Giáo án hiện hành để duyệt](GIAO_AN_180_PHUT_2026_10_07.md) giữ 180 phút và Tổng hợp 50 phút, chọn **nghỉ 20 phút** theo yêu cầu mới 15–20 phút, thay mốc nghỉ 10 phút trước đây. Bài Mẫu 1 dùng `pv01` giao CAP Hạc Thành/CAP Quảng Phú để demo giao–trả lại–duyệt; [bài thực hành Số hóa hồ sơ M1 mới](bai-thuc-hanh-so-hoa-ho-so/README.md) là bài thực hành chính, có một lần/tháng, SUM/AVG, bộ lọc, tổng hợp hai cấp và Dashboard. Bộ bốn mô hình giữ làm nâng cao/tra cứu. Nội dung đang để Yud duyệt, tài liệu HTML viết song song; chưa reset DB hoặc chạy lại trên deploy. Xem [rà tài liệu/yêu cầu sửa](HANDOFF_RA_SOAT_TAI_LIEU_VA_GIAO_AN_2026_10_07.md) và [prompt agent tài liệu](PROMPT_AGENT_RUT_GON_TAI_LIEU_VA_GIAO_AN_2026_10_07.md).

Lịch sử UAT Mẫu 1: [đối chiếu bước với tài liệu](HANDOFF_DOI_CHIEU_BUOC_UAT_VA_TAI_LIEU_2026_10_06.md), [kịch bản sau reset tách bảy chặng](KICH_BAN_UAT_LAI_HAPPY_CASE_SAU_RESET_2026_10_06.md): đăng nhập → quản trị đơn vị → quản trị tài khoản → biểu mẫu động → Flow 1. Kịch bản này **chưa chạy**, không phải nghiệm thu.

Bài Mẫu 2 cập nhật ngày **07/10/2026**: [contract và kịch bản bốn mô hình, bốn ngày, cây 1 → 2 → 4](mau-2-bon-mo-hinh/README.md), đã tách cây giao, biểu mẫu, phép tổng hợp, thứ tự UAT và dữ liệu từng mô hình. Phần Bảng có SUM/AVG, CONCAT trong thống kê Bảng, nối Bảng và Bảng Đơn vị — Nội dung có nguồn; bỏ COPY. **Đang UAT từng chặng, chưa nghiệm thu**: mẫu phòng v2 và cả bốn mẫu riêng PX01/PA02 đã công bố; cây đã giao đủ hai phòng/bốn đội, bốn kỳ ngày 04–07. Ngày 04 của cả bốn mô hình đã duyệt đúng ngày hoàn thành, mới đủ **4/12 báo cáo quá khứ**. Giữ hạn cấp đội **00:00 UTC = 07:00 Việt Nam**; giờ khác làm sau. Report PX01 còn Nháp, bảng đã lưu mở đúng nguồn M01. Hộp nhập ô M03 có vướng UI đã khôi phục, tên mẫu ở danh sách duyệt thiếu; thông báo Đã duyệt M04 đã thấy ở lượt đọc sau và mở đúng phần việc/kỳ, còn cần đo độ trễ/tự cập nhật. Xem [handoff chuẩn bị](HANDOFF_CONTRACT_KICH_BAN_MAU_2_2026_10_07.md), [handoff sửa bốn vướng mắc](HANDOFF_UAT_MAU_2_FOUR_FIXES_2026_10_07.md), [chặng M02](HANDOFF_UAT_MAU_2_M02_2026_10_07.md), [chặng PA02 mới nhất](HANDOFF_UAT_MAU_2_PA02_2026_10_07.md) và [prompt viết cả Markdown/HTML với ảnh và demo nối luồng](PROMPT_AGENT_HTML_MAU_2_M01_M02_2026_10_07.md). [Handoff UAT đầu](HANDOFF_UAT_MAU_2_BANG_NOI_DUNG_2026_10_07.md) giữ làm lịch sử.

Giữ lịch sử đề xuất trước: [cây PV01/PX01 và hai báo cáo mỗi xã](KICH_BAN_DINH_KY_DA_CAP_P1_CAY_GIAO_2026_10_06.md), [bản nháp Mẫu 2 và lịch hai ngày](KICH_BAN_DINH_KY_DA_CAP_P2_BIEU_MAU_LICH_2026_10_06.md), [handoff chuẩn bị bản nháp](HANDOFF_CHUAN_BI_MAU_2_DINH_KY_2026_10_06.md). Cây/lịch/điều kiện Tổng điểm ở các đề xuất này đã được thay trong bài chính mới; bản nháp từng lưu vẫn chưa công bố và chưa bị chỉnh theo kịch bản mới.

## 1. Sáu phần tài liệu

| Phần | Tài liệu | Người thực hành | Kết quả cần đạt |
|---|---|---|---|
| 01 | [Đăng nhập và định hướng](01_DANG_NHAP_VA_DINH_HUONG.md) | Tất cả; tạo tài khoản chỉ dành cho quản trị | Đúng tài khoản/đơn vị, tìm được việc, phân biệt nhiệm vụ–giao việc–báo cáo |
| 02 | [Tạo biểu mẫu động](02_BIEU_MAU_VA_DANH_MUC.md) | Người thiết kế; bài dùng pv01 | Hai Phần, 5 trường chung, Danh sách 15 trường nghiệp vụ, đính kèm chung; lưu, Nhập thử và Công bố |
| 03 | [Tạo nhiệm vụ và giao việc](03_TAO_NHIEM_VU_VA_GIAO_VIEC.md) | Chủ trì/chỉ huy có quyền | Hai đơn vị nhận đúng mẫu và cách báo cáo |
| 04 | [Nhập, nộp và duyệt](04_NHAP_NOP_DUYET_BAO_CAO.md) | Người nhập/người duyệt | Hai report đã duyệt, hiểu trả lại khác bàn giao |
| 05 | [Tổng hợp tự động](05_TONG_HOP_TU_DONG.md) | Người tổng hợp | Chọn nguồn, lọc, tính, xem thay đổi, xác nhận và đọc lại |
| 06 | [Tổng quan và vướng mắc](06_TONG_QUAN_VA_XU_LY_VUONG_MAC.md) | Tất cả; cấp trưởng xem phạm vi đơn vị | Đọc đúng tiến độ/số liệu, biết làm mới và báo lỗi |

Sáu tệp là bộ tài liệu có nguồn tra; phần 02 đã cập nhật theo bài Yud duyệt, phần 03–04 đã khớp lại với bài Mẫu 1 và bằng chứng UAT có phạm vi; các phần còn lại tiếp tục chờ rà. Ba ảnh nguồn đã được sao chép vào thư mục ảnh trong bộ tài liệu, dùng đường dẫn tương đối và chú thích đúng mốc; chưa có ảnh đầy đủ cho mọi bước hoặc xác nhận mọi nhãn trên host lớp. Không sửa tài liệu gốc để làm mất lịch sử; bộ này là điểm vào cho người học và agent viết tài liệu.

**Xem phần 02 bằng HTML:** mở [Tạo biểu mẫu động trong hướng dẫn chung](http://127.0.0.1:5187/#bieu-mau-dong). Bản phân phối chính thức của tài liệu HTML là `D:/Job/CA/tdtd-guide/index.html`: một trang chung với đăng nhập, quản trị đơn vị và quản trị tài khoản, có cùng mục lục; hỏi đáp chọn theo nhóm chủ đề. Tệp HTML tự chứa ảnh, CSS, tương tác và bài thực hành, có thể gửi riêng để đọc ngoại tuyến. [Đường dẫn HTML cũ](02_TAO_BIEU_MAU_DONG.html) chỉ chuyển tiếp tới phần IV của trang chung. Bản Markdown và nguồn ảnh/fixture tại bộ tập huấn vẫn được giữ để biên soạn, đối chiếu.

## 2. Chương trình đề xuất đúng 180 phút

| Phút từ đầu buổi | Thời lượng | Nội dung | Cách hướng dẫn |
|---|---:|---|---|
| 0–15 | 15 phút | Đăng nhập, menu, tài khoản/đơn vị và tìm việc | Học viên đăng nhập; quản trị cây/tài khoản chỉ minh họa ngắn |
| 15–35 | 20 phút | Biểu mẫu bốn trường Số, lưu, Nhập thử, Công bố | Bài Số hóa hồ sơ; mẫu riêng PA02 chuẩn bị trước, kiểu khác tra cứu |
| 35–55 | 20 phút | Tạo nhiệm vụ/giao việc, cây 1 → 2 → 4, một lần/tháng | Một phần việc được demo; cây đủ sáu phần việc chuẩn bị trước |
| 55–95 | 40 phút | Nhập–lưu–nộp, trả lại/sửa/duyệt, chọn kỳ và thông báo | Mẫu 1 giảng viên demo; mỗi nhóm nhập một report bài Số hóa |
| 95–115 | 20 phút | Giải lao | Mốc mới 15–20 phút; chọn 20, kiểm nguồn đã chuẩn bị |
| 115–165 | 50 phút | Tổng hợp và bộ lọc | SUM/AVG vào report phòng; lọc Ngày hoàn thành/nộp; View gốc trước/sau |
| 165–180 | 15 phút | Dashboard/cây, hỏi đáp và hỗ trợ | 7 phút đọc phạm vi/tháng/trạng thái; 3 hỏi đáp; 5 dự phòng |
| **Tổng** | **180 phút** | | |

Nếu chậm, dùng mẫu/cây/cấu hình đã chuẩn bị; giữ nhập–nộp–duyệt, đọc lại và kết quả Tổng hợp. Không cố dạy toàn bộ hàm hoặc kiểm hết ca lỗi. [Giáo án chi tiết](GIAO_AN_180_PHUT_2026_10_07.md) có mốc giờ, đáp án, ca bộ lọc cần kiểm và thứ tự chuẩn bị/reset/deploy. Các module chi tiết dưới đây là sổ tay tham khảo, không phải danh sách bắt giảng tuần tự toàn bộ.

## 3. Bài tạo biểu mẫu đã duyệt và bài xuyên suốt đề xuất

### 3.1. Bài Tạo biểu mẫu động

- Tài khoản: `pv01`. Tên biểu mẫu: **[TẬP HUẤN] Theo dõi mô hình chuyển đổi số**.
- [Biểu mẫu bài đã tạo](http://localhost:5173/design/forms/6ac40b8e5e204bc3cab29496/canvas): tại mốc tạo/lưu là **Bản nháp v1**, đã hiển thị **“Đã đối chiếu dữ liệu đã lưu”**. Ảnh sau đó cho thấy **v1 đã công bố**; bài học vẫn dừng trước Công bố. Không kiểm sống lại URL trong lượt tài liệu.
- Hướng dẫn tạo mới: Danh sách mô hình/sản phẩm/nhiệm vụ có 15 trường nghiệp vụ; STT tự hiển thị; Tài liệu minh chứng là Đính kèm tệp chung bên ngoài Danh sách.
- Ảnh nháp và ảnh công bố vẫn có 16 trường trong Danh sách vì còn STT nhập tay. Ảnh hộp Danh sách xác nhận lời nhắc STT tự động đã bổ sung và kiểm trên UI; không chứng minh đã xóa STT thừa. Xem [handoff tài liệu 06/10](HANDOFF_TAI_LIEU_TAO_BIEU_MAU_DONG_2026_10_06.md).
- Yud duyệt tạo, lưu và xem nháp. Không dùng kết quả này để xác nhận công bố, giao việc, nhập đủ dữ liệu/tải tệp, nộp–duyệt hoặc tổng hợp.

### 3.2. Bài xuyên suốt Mẫu 1 và ngữ cảnh tổng hợp

`pv01` tạo nhiệm vụ Mẫu 1 về theo dõi mô hình chuyển đổi số và giao một lần cho CAP Hạc Thành, CAP Quảng Phú; fixture lịch sử có hạn nộp 07/10/2026 23:59 giờ Việt Nam, tự duyệt Tắt. UAT cũ đã có vòng Hạc Thành nhập nội dung 6/10 → bị trả lại → sửa 10/10 → nộp lại → duyệt; Quảng Phú nhập 8/8 → nộp → duyệt. Đây là **bằng chứng lịch sử**. Kịch bản sau reset dùng dữ liệu mới: Hạc Thành 2 mô hình, Quảng Phú 3; kết quả kỳ vọng SUM Tổng số mô hình = 5, COUNT báo cáo = 2, nối Danh sách = 5 dòng. Chưa chứng minh Tổng hợp đã chạy đạt trên fixture mới. Các tỷ lệ 6/10, 10/10, 8/8 là chữ trong nội dung, không phải trường Số để SUM.

**`pv01` tự tạo nhiệm vụ** và xem tổng hợp của nhiệm vụ qua View: đó không phải báo cáo của `pv01` để nộp. **`pv01` nhận phần việc từ cấp trên** mới có báo cáo của mình để lấy báo cáo con rồi gửi lên; cần bài nhiều cấp riêng. Không tạo báo cáo cha giả hoặc mặc định View của nhiệm vụ là báo cáo gửi cấp trên.

[Cây tập huấn ba cấp có sẵn](../aggregate-canvas-v1/handoffs/AGGREGATE_TRAINING_TREE_RUNTIME_2026_10_05.md) là phương án minh họa nâng cao/dự phòng: Work 6ac305a1251fbfc0d07720c4; PX01 tổng 37 từ 2 report trực tiếp; PV01 tổng 69 từ 3 report trực tiếp; Work View lấy 1 report trực tiếp. Report đã Approved, không dùng để tập nhập hoặc tự trả lại. URL/ID là tham chiếu tại thời điểm handoff, phải kiểm host trước buổi.

## 4. Cách đọc

**Người học/giảng viên:** sáu phần 01 → 06. Tra ảnh/thao tác cũ trong [hướng dẫn 30/09](../HUONG_DAN_SU_DUNG_UAT_2026_09_30.md). Không đưa log/hash/DTO/evaluator vào lời hướng dẫn.

**Agent viết tài liệu:** README → [prompt tiếp nhận](PROMPT_TIEP_NHAN_TAI_LIEU_2026_10_05.md) → hướng dẫn 30/09 → handoff mới nhất đúng module. Phần Tạo biểu mẫu động đọc [handoff tên/lưu 06/10](HANDOFF_DYNAMIC_FORM_NAMES_REQUIRED_SAVE_2026_10_06.md), rồi [handoff tài liệu 06/10](HANDOFF_TAI_LIEU_TAO_BIEU_MAU_DONG_2026_10_06.md) để phân biệt kiểm mã với bài đã được Yud duyệt và chênh lệch còn lại. Riêng Tổng hợp đọc:

1. [Cây runtime 05/10](../aggregate-canvas-v1/handoffs/AGGREGATE_TRAINING_TREE_RUNTIME_2026_10_05.md): dữ liệu, ngữ cảnh, kết quả chuẩn và gap.
2. [Triển khai operator v3](../aggregate-canvas-v1/handoffs/AGGREGATE_OPERATORS_V3_IMPLEMENTATION_2026_10_05.md): contract đã được Yud duyệt và capability có evidence. Không kết luận chưa duyệt chỉ từ tên PROPOSED của tệp contract.
3. [Ma trận datatype](../aggregate-canvas-v1/handoffs/AGGREGATE_DATA_TYPE_TEST_MATRIX_2026_10_05.md): có hỗ trợ/chưa hỗ trợ, mức kiểm và phần thiếu.
4. [R5 UX](../aggregate-canvas-v1/handoffs/AGGREGATE_UI_REVIEW_R5_2026_10_05.md), [R6 lưu bảng nội dung](../aggregate-canvas-v1/handoffs/AGGREGATE_CONTENT_PUBLICATION_R6_2026_10_05.md), [R7 điều hướng lỗi](../aggregate-canvas-v1/handoffs/AGGREGATE_ERROR_NAVIGATION_R7_2026_10_05.md): đọc theo vấn đề; revision mới chỉ thay phần liên quan.
5. [R8 văn bản — đề xuất](../aggregate-canvas-v1/handoffs/AGGREGATE_TEXT_BLOCKS_R8_PROPOSAL_2026_10_05.md): tách quyết định Yud đã chốt khỏi ngưỡng/trình bày đang trình và capability chưa có evidence hoàn tất.

**Agent kỹ thuật:** sau đó mới lần từ component → API trang gọi → validator/quyền/service/storage và evidence. [Plan tập huấn 02/10, cập nhật 05/10](../aggregate-canvas-v1/handoffs/AGGREGATE_ASSIGNMENT_TRAINING_UAT_PLAN_2026_10_02.md) có lịch sử dài: đọc cập nhật đầu tệp và handoff được dẫn trước. [Master 27/09](../UAT_FULL_FLOW_MASTER_PLAN_2026_09_27.md) chỉ giải thích hành trình/dependency, không xác nhận tính năng hiện hành.

Ưu tiên: quyết định trực tiếp mới nhất của Yud → contract có xác nhận duyệt → handoff/evidence mới nhất đúng phạm vi → hướng dẫn → kế hoạch/đề xuất/lịch sử. Source mô tả hành vi code, không tự biến thành quyết định nghiệp vụ. Tên tệp ngày cũ không đủ xác định độ mới; đọc phần cập nhật/chốt cuối.

## 5. Map từ khóa

| Câu hỏi tiếng Việt | Từ khóa tìm source | Tài liệu vào |
|---|---|---|
| Đăng nhập, đổi mật khẩu, tạo tài khoản | Auth, ChangePassword, UsersPanel, UNIT_MANAGER | Hướng dẫn §1; [quản trị PA02](../UAT_PA02_ACCOUNT_UNIT_SCOPE_2026_09_30.md) |
| Chức vụ, đơn vị, đội, mã đơn vị | UnitManagementScope, UnitsPanel, Position, unitCode | Quản trị PA02; source tương ứng |
| Cấu hình danh mục, chọn một/nhiều | LabelEnumCatalog, ENUM_CATALOG, singleSelect, multiSelect | [Danh mục lựa chọn](../UAT_ENUM_CATALOG_CHOICES_HANDOFF_2026_09_28.md) |
| Tạo biểu mẫu, lưu nháp, mã tự cấp, Nhập thử | DynamicForm, DynamicFormInformationDialog, save, readback | [Phần 02](02_BIEU_MAU_VA_DANH_MUC.md); [tên/lưu 06/10](HANDOFF_DYNAMIC_FORM_NAMES_REQUIRED_SAVE_2026_10_06.md) |
| Danh sách mô hình, STT tự động, tệp chung | DynamicFormListDialog, itemLabel, addLabel, evidence | [Phần 02](02_BIEU_MAU_VA_DANH_MUC.md); [chênh lệch bản nháp](HANDOFF_TAI_LIEU_TAO_BIEU_MAU_DONG_2026_10_06.md) |
| Bố cục phần và công bố ở bước sau | DynamicForm, section, native-definitions/v2, publish | Hướng dẫn §4.1; [bố cục Form](../aggregate-canvas-v1/handoffs/DYNAMIC_FORM_LAYOUT_HANDOFF_2026_09_29.md); không Công bố trong bài 02 hiện tại |
| Từ ngày, đến ngày, hạn | WorkDatePolicy, WorkAssignmentDatePolicy, DueAtUtc, EndAtUtc | [Ngày Work 30/09, chốt cuối](../aggregate-canvas-v1/handoffs/WORK_DATES_SOURCE_AUDIT_2026_09_30.md) |
| Giao đơn vị/nhóm, tài khoản | WorkAssignTab, WorkAssignmentCreateDialog, UnitAccountPicker | Hướng dẫn §4.3 |
| Bàn giao, cán bộ làm thay, giao con | Handover, CurrentReviewerUserId, AssigneeUserId | [H2, phần cuối](../aggregate-canvas-v1/handoffs/HANDOVER_DIRECT_H2_2026_09_29.md); [đơn vị báo cáo](../aggregate-canvas-v1/handoffs/HANDOVER_UNIT_SCOPE_2026_09_30.md) |
| Nháp, nộp, duyệt, trả lại | SaveDraft, Submit, Approve, Return, Approved | Cây runtime; module 04 |
| Cấu hình tổng hợp, nguồn con | AggregateMappingEditor, AggregateSourceResolver, direct child | Cây runtime; operator v3 |
| Tổng hợp nhiệm vụ/report | AggregateTaskView, viewId, reportId, View binding | Cây runtime; module 05 |
| Nối trường, Lọc, Tính toán | AggregateRecipeCanvas, AggregateFilterDialog, AggregateFormulaDialog | R5; operator v3 |
| Đếm report/phần tử/giá trị | REPORT_COUNT, ELIGIBLE_SOURCES, SELECTED_ELEMENTS, COUNT_RECORDS, COUNT_VALUES | Operator v3; ma trận |
| Danh sách, cùng phần tử, nối dọc/ngang | LIST_PIPELINE, LIST_MERGE, FILTER, AND, OR | Operator v3; [biến thể công thức](../aggregate-canvas-v1/handoffs/AGGREGATE_FORMULA_VARIANTS_2026_10_05.md) |
| Bảng, vùng tính, header, tọa độ | AggregateTableCalculationDialog, APPEND_TABLE, matrix | Ma trận; cây runtime; không suy mọi phép List có trên Table |
| Ghép chữ, văn bản có định dạng | CONCAT, richText, RICH_HTML, REPORT_TEXT_TABLE | Ma trận; R6; R8 cho quyết định mới |
| Danh sách nội dung khác Danh sách phần tử | stringList, LIST, unsupported | Ma trận; R8 |
| Khoảng dữ liệu, lũy kế, nguồn khác lịch | CompletedDate, SubmittedAtUtc, period override, cumulative | [Handoff định kỳ/UX](../aggregate-canvas-v1/handoffs/PERIODIC_UX_UI_SELF_TEST_HANDOFF_2026_09_28.md); module 05 |
| Xem trước, hủy, mở lại, xác nhận | preview job, confirmation token, Apply, diff | Runtime; R6/R7 |
| Bấm lỗi về khối | ONLY_ITEM, location, nodeId, outputId | R7 |
| Tổng quan, cấp trưởng, cache/làm mới | DashboardUnitReadScope, scopeLabel, generatedAtUtc, refresh | [Dashboard 05/10](../UAT_DASHBOARD_UNIT_ACCESS_CACHE_2026_10_05.md) |

Tìm tên tệp trước, rồi từ khóa trong đúng nhóm. Không quét rộng cấu hình/log chứa credential.

## 6. Diễn giải cũ cần tránh

| Diễn giải cũ | Cách dùng hiện tại |
|---|---|
| Mọi bài đều bắt đầu PA02 hoặc đều dùng cây Giám đốc/PV01 | Bài Mẫu 1 hiện dùng `pv01` → Hạc Thành/Quảng Phú; cây nhiều cấp và tổng hợp có ngữ cảnh riêng, chưa lấy luồng một cấp làm bằng chứng cho các phần đó |
| Bài 02 tạo bốn trường, hai số bắt buộc, rồi Công bố | Bài được duyệt dùng 15 trường nghiệp vụ trong Danh sách và tệp chung; chưa chốt nghiệp vụ bắt buộc; dừng ở Bản nháp |
| Có câu nhắc STT hoặc ảnh công bố nghĩa là đã bỏ STT thừa | Câu nhắc đã có ảnh/kiểm UI; cả ảnh nháp và công bố còn STT nhập tay. Hướng dẫn mới dùng STT tự động, chờ xử lý trường thừa |
| Cấu hình localFormula/thống kê tại field | Authoring cũ đã rút; hiển thị Tổng quan khác phép tính tại ngữ cảnh tổng hợp |
| Quyền Form cho đọc mọi report | Quyền riêng; nguồn đúng scope/quyền và hiện hành Approved |
| Một report bằng một assignment | Assignment có thể có nhiều kỳ/report |
| Cùng Form tự map 1–1 | Phải chọn nguồn/phép hợp lệ, xem trước và xác nhận |
| RichText Apply bảng vẫn chưa sửa WriteConflict | R6 đã sửa/kiểm fixture riêng; không suy shared host đã nạp hoặc full UAT đã đạt |
| StringList/ngưỡng 500 ký tự đã chạy | Handoff R8 còn phần trình/triển khai chưa có evidence hoàn tất; không đưa vào bài bắt buộc |
| PASS/build nghĩa là Yud đã nghiệm thu | Ghi đúng source/engine/mounted/API/browser/Yud duyệt, không thay thế nhau |

## 7. Chuẩn bị ngoài 180 phút và phần để sau

Kiểm đúng FE/BE/capability trên host lớp; chốt học viên/tài khoản; chuẩn bị bài ngắn, Form đích đúng ngữ cảnh và dữ liệu dự phòng đã duyệt; chạy trước bài bằng browser khi được giao. Không lưu mật khẩu/token. Bộ tài liệu không tự cấp quyền tạo fixture/drop/seed/restart hoặc sửa cây có sẵn.

Buổi chính dạy luồng thành công, dữ liệu và cấu hình được chuẩn bị trước. Contract Mẫu 2 ngày 07/10 đã đưa cây 1 → 2 → 4 và IF đơn giản vào bài thực hành; lịch thực hiện 180 phút vẫn chờ rà, không coi mọi ca đã UAT. Để bài nâng cao: công thức lồng sâu, top-N/nối ngang/weighted, khóa nhiều owner, đổi schema/chuyển revision phức tạp, tải lớn. Flow, quota, full-text/ETL, export toàn bộ và join/dedup không đưa thành chức năng sẵn sàng cho lớp.

## 8. Trạng thái bàn giao ban đầu — 05/10

Lượt này chỉ tạo README, sáu module và [prompt](PROMPT_TIEP_NHAN_TAI_LIEU_2026_10_05.md); đọc/đối chiếu tài liệu, status hai repo và kiểm liên kết/thời lượng bộ mới. Không sửa sản phẩm/master/board/issue/tài liệu gốc, không chạy test/build/browser/API/DB/job để tạo bằng chứng mới, không tạo task/agent. Các module và bài mới chờ Yud rà; agent tiếp nhận hoàn thiện theo prompt.

## 9. Cập nhật phần Tạo biểu mẫu động — 06/10

Phần 02 có [HTML trong hướng dẫn chung](http://127.0.0.1:5187/#bieu-mau-dong) và [Markdown](02_BIEU_MAU_VA_DANH_MUC.md) công khai cho cán bộ. HTML có sáu bước, chín ảnh chụp FE bằng dữ liệu mẫu, mười câu hỏi cố định thuộc nhóm Biểu mẫu động, tìm từ khóa và bài thực hành không kết nối hệ thống. Từ yêu cầu ghép hai hướng dẫn ngày 06/10, bản HTML phân phối chỉ còn ở `D:/Job/CA/tdtd-guide/index.html`; tệp HTML trước đây tại bộ tập huấn là trang chuyển tiếp. Nội dung, trình bày và fixture được tách riêng; xem [quy ước bảo trì](tools/README.md).

README, prompt và [handoff tài liệu](HANDOFF_TAI_LIEU_TAO_BIEU_MAU_DONG_2026_10_06.md) dành cho người biên soạn, không chèn hoặc liên kết vào bản HTML công khai. Ba ảnh sản phẩm đã nhận trước đó được giữ làm bằng chứng lịch sử trong handoff; bài tạo mới dùng ảnh fixture đủ 15 trường, không STT nhập tay. Điều này không chứng minh bản sản phẩm thật đã xử lý trường STT thừa.

Giữ chương trình 180 phút là đề xuất chờ Yud rà, gồm Tổng hợp 50 phút và nghỉ 10 phút. Lượt cập nhật HTML chỉ kiểm tài liệu và fixture FE trong bộ nhớ; không sửa sản phẩm hoặc bản biểu mẫu thật, không gọi API/DB, không seed, build sản phẩm hoặc nghiệm thu UAT mới. Các nhãn Xác nhận, Thêm trường, Thêm vào biểu mẫu và Nhập thử đã được quan sát trên fixture dùng component gốc; phạm vi nghiệm thu sản phẩm vẫn theo quyết định trực tiếp đã ghi trong handoff.

## 10. Bản phân phối chung và đường dẫn cũ — 06/10

Yêu cầu mới nhất là ghép hướng dẫn tài khoản và biểu mẫu động vào một trang chung, một menu; bộ hỏi đáp có thể chia theo chủ đề. Điểm vào cho cán bộ là [hướng dẫn chung](http://127.0.0.1:5187/) hoặc đi thẳng [phần IV. Tạo biểu mẫu động](http://127.0.0.1:5187/#bieu-mau-dong). Khi gửi ngoại tuyến, gửi `D:/Job/CA/tdtd-guide/index.html`, không gửi bản chuyển tiếp tại thư mục này làm tài liệu chính.

Đã bảo toàn HTML tách và renderer trước ghép tại `qa/legacy-html-2026-10-06/` để tra lịch sử. Không phục vụ snapshot này qua bản xem công khai. `tools/render-form-guide.mjs` hiện chỉ sinh lại trang chuyển tiếp; chạy công cụ cũ không khôi phục guide tách. Trong thư mục tập huấn không có `package.json` hoặc lệnh `npm run build:html`; build và preview tài liệu chung dùng dự án `tdtd-guide`. JSON, Markdown, ảnh và fixture nguồn trong bộ tập huấn vẫn được giữ.


### Chương Biểu mẫu động v0.7 — 06/10/2026

Khi tiếp tục chương này, đọc HANDOFF_TAI_LIEU_BIEU_MAU_V07_2026_10_06.md sau handoff clone. Yêu cầu viết lại đã thay điểm dừng Bản nháp bằng hướng dẫn đến Công bố; không phải quyền tự công bố dữ liệu thật. Nguồn public hiện là content/form-guide.json schema 2 và 02_BIEU_MAU_VA_DANH_MUC.md. HTML cũ do tools/render-form-guide.mjs sinh là trang chuyển tới guide chung D:/Job/CA/tdtd-guide/index.html tại 5187/#bieu-mau-dong. Không ép nội dung mới vào sáu bước cũ.
