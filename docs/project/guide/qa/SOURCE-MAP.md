# Nguồn và phạm vi kiểm chứng
Mốc đối chiếu ban đầu: 05/10/2026. Cập nhật chương v0.7.0: 06/10/2026; các HEAD/hash cũ bên dưới là hồ sơ tại thời điểm ghi, không phải xác nhận source hiện tại.

## Nguồn trực tiếp
- TD-TD FE: ../tdtd/tdtd-fe, HEAD 14edd2ad1b62cc53f1515deb6ebb23ada2dbefd8.
- TD-TD BE: ../tdtd/tdtd-be, HEAD 0ca2e93cfe404450a304c1a5dc1941f6d4752fd1.
- Checkout có WIP ngoài phạm vi. Không coi HEAD là toàn bộ trạng thái source.
- qa/source-manifest.json ghi SHA-256 file nguồn tại lúc đối chiếu ảnh.
- Quy tắc Tổ/chức vụ đối chiếu thêm ../tdtd/seeds/foundation/tdtd-20260924/foundation.json.

## Đối chiếu theo bài
| Bài | File dưới tdtd-fe/src | BE bổ sung |
|---|---|---|
| Đăng nhập, menu | pages/auth/LoginPage.tsx, layouts/Header.tsx, pages/admin/AdminAccountPages.tsx | Chưa chạy login thật |
| Tự đổi mật khẩu | components/auth/ChangePasswordPanel.tsx | Chưa thử kết thúc phiên thật |
| Đơn vị con | features/admin/units/UnitsPanel.tsx, UnitEditorDrawer.tsx, accountAdministrationRules.ts | Common/Auth/AccountAdministrationRules.cs; Services/UnitService.cs |
| Tạo/sửa người dùng | features/admin/users/UsersPanel.tsx, UserEditorDialog.tsx; components/common/PositionSelect.tsx | Chưa chạy quyền HTTP/DB |
| Đặt lại/ngừng dùng | features/admin/users/UsersPanel.tsx; components/admin/UsersTable.tsx | Chưa đặt lại/ngừng dùng thật |
| Nhập tệp | components/common/ImportDataMenu.tsx; UnitsPanel.tsx; UsersPanel.tsx | Chưa gửi tệp thật |

## Nguồn ảnh
Công cụ tools/capture-fe.mjs mount các component FE thực trong fixture tối giản:
Header, LoginPage, AdminAccountsPage và các dialog/drawer thực.
Không dựng lại form sản phẩm bằng HTML viết tay.
Fixture không kèm sidebar/full application shell nên đây không phải ảnh toàn app production.
Dữ liệu người/đơn vị/API là giả và có watermark. Mật khẩu mặc định được che khi chụp.
Request ghi đều bị chặn; hub thông báo bị chặn là chủ ý. Không dùng credential/token thực.
Ảnh bao gồm màn mở, form và hộp xác nhận; không giả ảnh lưu thành công.

## Tách loại bằng chứng
- SOURCE_CHECKED: nhãn, handler, field, phạm vi được đối chiếu source trên.
- FE_FIXTURE_CHECKED: component thực mở được, chọn loại/chức vụ, form/dialog hiển thị trong fixture.
- GUIDE_QA_PASSED: xem browser-result.json và QA-REPORT.md.
- LIVE_UAT_PENDING: toàn bộ ma trận UAT của lượt kiểm lại trong dự án này.

Yud báo đã UAT full luồng trước khi giao việc. Thông tin đó được giữ là mốc do người dùng cung cấp;
không dùng để tự điền kết quả chi tiết cho lượt kiểm lại đang lên kế hoạch.

## Ảnh menu v0.3.0
assets/02-account-menu.jpg được chụp lại từ Header/AdminAccountPages hiện hành với quantri_xa_mau bằng CUA; dòng Quản trị tài khoản có focus bàn phím. Hồ sơ nguồn/ảnh riêng: qa/menu-capture-v03.json. source-manifest.json trước đây là hồ sơ lịch sử, không mô tả hash ảnh menu mới.

## FE và import v0.4.0
- Yud giao kiểm lỗi bộ lọc/nút từ source rồi sửa trước khi chụp. UsersPanel sạch trước việc này; các file nguồn khác đang WIP được giữ.
- Ảnh05–10 thay bằng chụpCUA sau sửa, dữ liệu hư cấu. users-capture-v04.json có trước/sau, hash và giới hạnmobile.
- ImportDataMenu, ImportPreviewDialog và navyTheme được bundle trực tiếp. UsersPanel/UnitsPanel/BE trong manifest là nguồn đối chiếu, không giả rằng toàn màn quản trị được chạy trong bài thử.
- Runtime offline assets/import-practice.html; adapter riêng chỉ kiểm bộ mẫu. Nhúng srcdoc, sandbox allow-scripts allow-downloads. Không API/storage/token.
- Ảnh11–13 hai cấp chụp cùng runtimeFE+adapter, không phải ảnh nhập thành công trên môi trường thật; watermark nêu rõ bài thực hành.
- Hash/nguồn tải tệp: import-source-v04.json. Kết quả và khoảng trống: QA-V04.md. Ma trận thật: UAT-MATRIX.md.

## Tài khoản, mã và cấu trúc v0.5.0
- Đối chiếu AccountAdministrationRules, UserAdminService (đơn vị đích/chức vụ), UnitService (loại con theo đơn vị quản trị), Header/RequireRole (menu/quyền) và UserEditorDialog (đơn vị/chức vụ). Không sửa các nguồn sản phẩm trong lượt v0.5.
- Gói nền seeds/foundation/tdtd-20260924/foundation.json: pv01, pv01_doi1, cap_hacthanh; mã PV01 100001002, Đội1 100001002001, phường Hạc Thành 100001001127.
- Gói tập huấn seeds/training/tdtd-20260924/training-accounts.json: tài khoản cá nhân tương ứng. cap_hacthanh_totruong/canbo hiện gắn mã phường; chưa có Tổ con trong gói mẫu. Không xác nhận lại DB đang chạy.
- AdminImportService CreateUserTemplate (130–151): Users/Positions/Units; Positions là mọi chức vụ đang dùng, Units lọc scope/đang dùng/không ảo (tối đa500). Thao tác nhập vẫn kiểm chức vụ theo đơn vị đích.
- Bảng tra mã offline lấy mã/đơn vị từ tools/import-practice/model.mjs và tên tham chiếu giống bộ tạo XLSX; tên chức vụ tương ứng được giải thích riêng. Không dùng mã001001/002001 thay cho mã seed UAT.
- Ảnh10 FE menu đã có đủ nút và ba lựa chọn; chỉnh focus và dùng đúng ở bước4. Ảnh14–15 là nội dung ô của XLSX thực, xem xlsx-preview-v04.json; công cụ render tạo bản chiếu đọc để giữ ô chuỗi rỗng và số0 đầu. Không giả ảnh cửa sổ Microsoft Excel.

## Danh mục tải riêng v0.5.1
- assets/catalogues/ có bốn XLSX, tách Units/Positions từ assets/import-samples/xa-user-good.xlsx và phong-user-good.xlsx; không xuất trang Users.
- tools/build-catalogues.mjs tạo tệp; qa/catalogues-v05.json ghi nguồn/hash, headers/rows, kết quả đối chiếu XML và render. Không đọc DB hoặc token thật.
- content/import-guide.json khai báo nhãn, cấp và đường dẫn; tools/build.mjs nhúng bytes file; tools/render-import-guide.mjs tạo liên kết tải; src/import-guide.css xử lý hiển thị/in.
- Đây là danh mục mẫu của bài thử. Không chứng minh danh mục hiện hành hay quyền import trên sản phẩm. Kết quả và giới hạn browser: QA-V051.md.

## Biểu mẫu động và bản ghép v0.6.0

Nguồn nội dung/chín ảnh fixture/runtime và handoff của chương độc lập nằm tại ../tdtd/docs/training/tap-huan-3-gio/. Handoff HANDOFF_TAI_LIEU_TAO_BIEU_MAU_DONG_2026_10_06.md ghi rõ mốc ảnh sản phẩm cũ, bản HTML đầu và lần thay bản công khai bằng fixture FE. Các mốc đó giữ nguyên khi ghép; không dùng kết quả kiểm chương độc lập để khẳng định bản ghép đã qua browser/print.

| Thành phần trong tdtd-guide | Vai trò |
|---|---|
| content/dynamic-form-guide.json | Nội dung public: 6 bước, 15 trường, dữ liệu mẫu và 10 câu hỏi; không chứa prompt/handoff |
| tools/render-dynamic-form-guide.mjs | Chuyển dữ liệu thành phần IV, 6 neo bieu-mau-, menu, dialog và dữ liệu QA để tools/build.mjs ghép một index.html |
| src/dynamic-form-guide.js/.css | Ảnh lớn, player, in và mở/đóng phiên iframe của phần biểu mẫu; namespace df- |
| assets/dynamic-form/ | 9 ảnh fixture FE được sao chép, nhúng inline khi build; không thay ảnh lịch sử của bản thật |
| assets/form-practice.html | Runtime FE đã đóng gói, dữ liệu mẫu/session trong bộ nhớ; sandbox allow-scripts, không upload/API/DB/token/storage |
| qa/dynamic-form-source.json | Manifest nguồn/runtime/ảnh của fixture; giữ nguồn và hash ngoài nội dung public |
| src/qa-ui.js, src/qa.html, src/qa-groups.css | Một bảng tra cứu, hai nhóm 33 + 10 câu, tìm từ khóa/chọn câu và gọi đúng bài thử |

Fixture biểu mẫu dùng component FE thực cho màn thiết kế, Thông tin biểu mẫu, Danh sách, Thêm trường và Nhập thử. Các trường Văn bản dùng lựa chọn Nội dung, được xem cấu hình là Văn bản tự do. Cấu trúc mẫu gồm 15 nghiệp vụ, STT tự động, một vùng Đính kèm tệp chung ngoài Danh sách. Chọn tệp/lưu nháp của fixture chỉ là thao tác trong phiên; không xác nhận upload hoặc lưu/readback trên hệ thống.

Tài khoản thietke_bieumau_mau là nhãn mẫu, không phải credential. Form thật [TẬP HUẤN] Theo dõi mô hình chuyển đổi số có các ảnh lịch sử nháp và v1 đã công bố; vẫn còn STT nhập tay trong mốc đã ghi. Việc ghép không sửa form thật, không khẳng định trường thừa đã xóa, không dùng trạng thái đã công bố làm bằng chứng luồng báo cáo/tổng hợp.

URL phân phối chuẩn của phần này: http://127.0.0.1:5187/#bieu-mau-dong. URL cổng 5191 và HTML chương riêng là lịch sử bản xem. Normal npm run build/preview/check chỉ dùng Node.js và artifact đã lưu; jsdom của FE chỉ cần cho kiểm DOM tùy chọn. Browser/print/LIVE_UAT của bản ghép phải ghi riêng; báo cáo phiên bản trước không tự nâng trạng thái v0.6.

## Biểu mẫu động v0.7.0 — nguồn mới và phạm vi thay thế

qa/FORM-SOURCE-V07.md ghi file:dòng cụ thể của WIP hiện hành. Yêu cầu mới thay sáu bước và điểm dừng nháp v0.6 bằng ba phần A/B/C,13 mục tra cứu và14 bước đến Công bố. Không sửa sản phẩm hoặc tự đổi bản thật.

| Nội dung | Nguồn đọc hiện hành | Mức bằng chứng |
|---|---|---|
| Bài 21 trường, nguồn và bản sao | HANDOFF_UAT_CLONE_BIEU_MAU_2026_10_06.md trong bộ tập huấn | Handoff UI thật: sao chép, thêm 5 trường chung, giữ 15 trường List, bỏ STT tay trong bản sao, giữ tệp chung; lưu nháp và Nhập thử. Chưa Công bố/tổng hợp |
| 10 kiểu độc lập và hộp cấu hình | DynamicFormComponentPalette, DynamicFormFieldDialog, DynamicFormFieldNameInput, DynamicFormSchema | SOURCE_CHECKED; ảnh và fixture phải có manifest riêng |
| Ngày/kỳ, Ngày đầy đủ | dateInputFormat, DynamicFormRuntimeFields, DynamicFormRuntimeFieldCanonicalizer BE | SOURCE_CHECKED: kỳ nhận ngày/tháng/năm, Ngày đầy đủ cần đủ ngày |
| Soạn thảo, Danh sách nội dung | LexicalRichDocumentEditor, StringListRuntimeEditor | SOURCE_CHECKED: định dạng khác nhiều ý; không phải List nhiều trường |
| 6 kiểu List, 7 kiểu Bảng chọn mới | DynamicFormListDialog, DynamicFormTableCellConfig, DynamicFormTableTypeEditor, DynamicFormNativeTableDefinition BE | SOURCE_CHECKED; đọc cấu hình Bảng cũ không có nghĩa được chọn mới |
| Tên thiết kế và bắt buộc khi nộp | Các dialog; WorkAssignmentReportService draft/submit, runtime canonicalizer | SOURCE_CHECKED; lưu nháp báo cáo cho thiếu, nộp kiểm required |
| Hiển thị trên tổng quan | DynamicFormStatisticSettingsEditor, DynamicFormStatisticMetadata, save/validator | SOURCE_CHECKED cờ riêng, không tạo phép tính; consumer/kết quả tại Dashboard cần kiểm |
| Lưu/đối chiếu và Công bố canvas | DynamicFormCanvasPage, useCanvasFormSave, CanvasVersionActions, dynamicFormCanvasLifecycle; DynamicFormService/NativePublish BE | SOURCE_CHECKED nhãn/hành vi, chưa UAT Công bố bản mới |
| Sao chép toàn biểu mẫu | DynamicFormListTable/ListPage; DynamicFormService.CloneAsync | SOURCE_CHECKED và handoff clone; ID/mã/family riêng, draft v1, không thay nguồn |
| Sắp xếp | DynamicFormLayoutEditor | SOURCE_CHECKED; thứ tự Phần và trường độc lập, List/Bảng giữ dưới trường đơn |

Nội dung public là content/dynamic-form-guide.json schemaVersion 2 và Markdown 02_BIEU_MAU_VA_DANH_MUC.md. tools/render-dynamic-form-guide.mjs sinh phần IV; runtime và ảnh nhúng từ artifact của chương. Manifest/bằng chứng ở qa, không đưa sourcepath, hash hoặc endpoint vào hướng dẫn. Hỏi đáp vẫn một panel hai nhóm; câu chương IV dẫn đến neo tương ứng của nội dung mới.

Phân phối một index.html tại 5187; HTML cũ 5191 chỉ redirect. Ảnh trạng thái đã công bố của nguồn cũ không chứng minh đã công bố clone mới hoặc hoàn tất bước trước. Ảnh 21 trường mẫu không chứng minh DB thật thay đổi. Số câu/runtime/ảnh v0.6 phía trên là hồ sơ lịch sử, không tự cấp PASS cho v0.7.
## Chụp quản trị v0.7.2 — 06/10/2026

Ảnh20–23 mới chụp read-only tại ứng dụng localhost:5173 bằng pv01; theme/kích thước/hash/giới hạn ở admin-capture-v072.json và QA-V072.md. FE Đơn vị đưa nút vào Đang chọn; FE Người dùng dùng hàng công cụ chung và menu XLSX kèm danh mục. BE source bỏ dòng ví dụ001 cố định và cap500 của Units; chưa kiểm phiên bản đang chạy. Không thay hồ sơ ảnh fixture trước đây bằng source hiện tại.

## Giao việc và báo cáo v0.8.0

Nguồn và mức bằng chứng: QA-V080.md. content/report-lifecycle-guide.json sở hữu8 tra cứu/15 bước Mẫu1/13Q&A; tools/render-report-guide.mjs tạo chươngV/menu, dùng chung viewer/in của guide. Không thêm runtime nghiệp vụ giả lập.

Đọc handoff hoàn thiện→handoff lifecycle→Markdown03/04→UAT-MATRIX; đối chiếu handoff độc lập khi xuất hiện. Lượt trước chứng minh vòng thủ công của hai đơn vị; lượt độc lập chỉ đến tạo/giao/chuông, lỗi mở màn receiver. HTML và matrix phân biệt hai mức này. Việc chưa có ảnh/nút đúng màn ghi cần kiểm, không suy từ source component.

Ảnh gốc17 tệp đã xem và sao chép nguyên bản vào assets/report-lifecycle/. Manifest report-lifecycle-images.json lưu source/hash/dimensions; sourcepath/hash chỉ ởQA. Ảnh39–40 là kết quả lịch sử hai báo cáo đã duyệt/1 kỳ/2 tài khoản và0 pending; ảnh25 là lịch sử trước nộp của case về sau đã duyệt.

Canonical: http://127.0.0.1:5187/#bao-cao-bai-mau-1. Một index.html,5 chương,59 thẻ,67Q&A trong3 nhóm. Bản inV36 trang/toàn bộ90 trang đã render; không tự nâng UAT sản phẩm hoặc nhận xét hiệu năng INP.

## Báo cáo v0.8.1 — ảnh component FE với ví dụ dễ hiểu

content/report-lifecycle-guide.json schema2 sở hữu8 tra cứu/15 mục bài/56 thao tác; từng thao tác có ảnh và focus phần trăm. tools/render-report-guide.mjs không nhúng manifest nội bộ, chỉ đưa tên tài khoản/đơn vị và theme an toàn vào HTML. src/report-guide.css khoanh ảnh ở nội dung/in; src/guide.js giữ focus đúng tỉ lệ ảnh ở zoom/player.

29 ảnh assets/report-lifecycle-v081/ chụp từ tools/lifecycle-fixture/ dùng component FE gốc với adapter bộ nhớ. Không có API/lưu/nộp/duyệt thật. Source/hash/thay hook/khung tài khoản ở report-lifecycle-source-v081.json. Manifest ảnh cũ giữ tại report-lifecycle-images-v080.json; tài sản v0.8.0 không bị xóa.

QA-V081.md ghi kiểm23 nhómDOM, browser1280/768/390 và PDF59/113 trang; REPORT-LIFECYCLE-V081-HANDOFF.md ghi UAT cần kiểm lại. Handoff độc lập14:08 vẫn bị chặn ở /work-inbox; ảnh fixture không phủ nhận hoặc giải quyết blocker. Không đưa kỹ thuật, prompt, tên agent hoặc ghi chép kiểm vào nội dung cán bộ.

## v0.8.2 — nguồn bài thử liền mạch
Manifest report-practice-source-v082.json ghi260 source FE được bundle và hashes adapter/artifact. Các component chính: WorkForm, WorkAssignmentCreateDialog, NotificationBell, InboxAssignmentDialog, InboxReportGroupDialog, WorkReportEditorPage, WorkReviewTab, native report values runtime. Base query/auth/realtime được thay local để cô lập. Actor/theme/schema sample tại tools/report-practice; không phải UAT FE/BE live. 29 ảnh v081 không thay. QA-V082.md ghi bằng chứng mới.

## v0.8.3 — trình xem bài Mẫu 1

Entry watch.tsx dùng WorkForm/WorkAssignmentCreateDialog/NotificationBell/InboxAssignmentDialog/WorkReportEditorPage/WorkReviewTab từ FE gốc, manifest report-practice-source-v083.json. playback.mjs sở hữu sceneState/controller; adapter.installScene thay snapshot bộ nhớ, native draft của cảnh nhập/sửa không coi là nộp. Dùng handler gốc mở chi tiết mô hình và hộp trả lại; overlay khoanh DOM, không sửa pixel ảnh. 29ảnh chươngV vẫn nguyên bản. Thay đổi chỉ guide; không sửa sourceMarkdown/product hoặc dùng mẫu tự chạy để đổi nghiệm thu.

## Mẫu 2 / M01 v0.8.4 — 07/10/2026

Nguồn main HANDOFF_UAT_MAU_2_BANG_NOI_DUNG_2026_10_07.md (02:44) và kịchbản mau-2-bon-mo-hinh, bổsung FOUR_FIXES (03:01). Mốc main là nộp/đọc/dừng; phần cậpnhật đã nạpBE, sửa ngày/ngàykỳ, trả–nộplại–duyệt và reportPX01 tổnghợp1nguồn cònNháp. Manifest mau2-images-v084.json ghi hash/mtime; QA-V084.md ghi đốichiếu và giới hạn. Đây là evidencehandedoff, không lượtUAT của guide.

content/mau2-guide.json public tách khỏi manifest:11references/7practice/9qa/tables. tools/render-mau2-guide.mjs ghép sharedchapterV vàQ&A; assets/mau2-v084 chứa7ảnh thật nguyênbyte, nhúngoffline khi build. Nhập thửPV01/ảnhlỗingày không làmproofreport. Các ảnh sauFIXES chưa dùngpublic tronglượt này; phần hướngdẫn mới giữ tới đọc và ghi mẫu hiệnđãduyệt. Mẫu1v083 hashes giữnguyên, không thay verdictđộclập cũ.

Kiểm guide: npmrunbuild/check/25DOM; desktop/tablet/mobile/zoom/Q&A/print. Chi tiết/link: [QA-V084](QA-V084.md), [handoff](MAU2-M01-V084-HANDOFF.md). Không thay nguồnMarkdown sảnphẩm hoặc lịch180/50/10.

## M01 v0.8.5 — ảnh và luồng bổ sung

Nguồn chính/main và FOUR_FIXES, gồm cập nhật cảnh báo bảng nền 03:41–03:54, được ghi hash trong mau2-images-v085.json. 24 ảnh chụp UI thật chỉ đọc và 2 ảnh lịch sử ngày hoàn thành sao chép vào assets/mau2-v085/, giữ 7 ảnh v0.8.4. Ảnh/bytes không chỉnh trạng thái; lớp CSS khoanh vùng và tên tài khoản. Nút nháp kỳ07 không đại diện report04 đã duyệt; hộp trả lại chụp rồi Hủy. Không có UAT tải tệp thật.

v0.8.5 đã bổ sung 17 chức năng/11 chặng/15 Q&A và hỗ trợ nhiều ảnh/ảnh dọc. Source chung được tác vụ khác mở rộng v0.8.6/M02 và giữ nguyên; snapshot kiểm hiện tại ở mau2-v085/current-m01-v086.json. QA/PDF lịch sử tách riêng theo phiên bản. [Handoff](MAU2-M01-V085-HANDOFF.md), [QA](QA-V085.md).

## M02 v0.8.6 — nguồn và đối chiếu hiện hành

Đọc đủ AGENTS/README/REQUIREMENTS/PLAN/content JSON/UAT-MATRIX/handoff M01 v084 và README training; FOUR_FIXES tới cập nhật cuối; HANDOFF_UAT_MAU_2_M02_2026_10_07.md; contract/tree/forms/UAT và data01/data02. Bản đọc và hashes lưu trong mau2-images-v086.json.

M02 evidence là bước UI được bàn giao, không thao tác sản phẩm ở lượt tài liệu này. Bỏ blocker lịch sử về ngày, 22:00 và lỗi bảng nền đã sửa. 07:00 cấp đội đã chốt; PX01 Nháp chỉ có M01, nguồn mở lại được. Không suy kết quả M01/PX01 thành View PV01 hoặc report cấp trên đã nộp.

content/mau2-guide.json → render-mau2-guide.mjs → content/mau2-guide.md/index.html. Manifest 41 ảnh gồm 33 ảnh WIP M01 được giữ nguyên và tám ảnh M02 đã mở xem, copy byte. Ảnh ca thiếu ngày không nhìn thấy lỗi: dùng ở Lưu nháp, yêu cầu ảnh lỗi riêng. Bảng ảnh/gaps: MAU2-M02-V086-IMAGES.md. Mẫu 1 có bảy hashes và dữ liệu/bài cũ giữ nguyên. Không link hồ sơ nội bộ vào public.

Đã đọc lại README và bốn tệp contract/cây/biểu mẫu/UAT khi phát hiện cập nhật PA02 từ ngoài lượt này. Giữ hash đọc đầu bằng initialReadSha256 và hash đọc lại trong manifest; không sửa nguồn. Hướng dẫn public giữ M01/M02, bỏ cách nói PA02 chưa giao đội. Chặng PA02 chưa được biên soạn từ bộ handoff/ảnh riêng ở lượt này.
