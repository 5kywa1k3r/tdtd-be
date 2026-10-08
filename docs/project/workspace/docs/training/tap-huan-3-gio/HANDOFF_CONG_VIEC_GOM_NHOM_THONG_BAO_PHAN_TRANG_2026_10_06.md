# Handoff: gom nhóm báo cáo, thông báo và phân trang Công việc — 06/10/2026

## Kết quả và phạm vi

Nộp báo cáo và Duyệt báo cáo hiển thị một card cho mỗi lượt giao (`assignmentId` + chức năng). Một lượt giao cho 200 tài khoản phối hợp không tạo 200 card. Card lấy số lượng từ server trên toàn bộ phần việc thuộc quyền hiện hành và bộ lọc, trước khi phân trang. Các kỳ và tài khoản phối hợp nằm trong bảng chi tiết có phân trang riêng; người dùng chọn dòng cụ thể để mở báo cáo hoặc màn duyệt đang có.

Đây là phần tiếp nối [handoff card gọn](HANDOFF_CONG_VIEC_CARD_GON_2026_10_06.md) và [handoff bộ lọc, trạng thái và UI](HANDOFF_CONG_VIEC_FILTER_TRANG_THAI_UI_2026_10_06.md). Quyết định gom theo lượt giao thay thế cách chia card theo từng kỳ trong lượt sửa bố cục trước. Giữ thuật ngữ Chủ trì/Phối hợp, màu và hành động theo theme.

Không sửa auto-approve, nguồn biểu mẫu, quyền clone, dữ liệu UAT hoặc shared host. Người tạo lượt giao vẫn duyệt yêu cầu sao chép theo quyết định của Yud. Không cập nhật master/board/issue hay hướng dẫn đã được coi là nghiệm thu.

## Contract hiển thị

| Bề mặt | Đơn vị hiển thị và số lượng |
| --- | --- |
| Card Nộp báo cáo | Một lượt giao; tổng kỳ báo cáo trong scope/bộ lọc của chính actor |
| Card Duyệt báo cáo | Một lượt giao; tổng phần báo cáo của phối hợp thuộc quyền duyệt hiện hành |
| Chip card | Chưa làm/chờ duyệt, đã xử lý, quá hạn, sắp tới hạn, nộp lại; thời hạn chỉ đếm phần chưa xử lý |
| Kỳ / Phối hợp | Số kỳ khác nhau / số tài khoản phối hợp khác nhau trong bộ lọc; không gọi số tài khoản là số đơn vị |
| Bảng chi tiết | Chỉ truy vấn thành viên của lượt giao/chức năng đang chọn; mặc định 10 dòng, không tải toàn bộ 200 thành viên |
| Chuông và lịch sử | Nhóm theo lượt giao + loại sự kiện báo cáo; legacy thiếu lượt giao dùng Work + loại sự kiện. Clone và các hành động độc lập vẫn riêng theo notification ID |
| Số thông báo | Tổng event và số event chưa đọc trong nhóm; độc lập với số báo cáo và trạng thái xử lý |

Header và các chip chức năng vẫn phản ánh số phần việc của summary, vì vậy dùng chữ “mục”, không gọi 200 phần việc là 200 đầu việc. Nhóm được tính trước cursor/page/limit; FE không tự gom từ một trang đã tải. Đọc thông báo không hoàn thành nghĩa vụ.

## Routing và cache

- Nhóm thông báo báo cáo chuyển đến `/work-inbox` với `workId`, `assignmentId` nếu có, `function=REPORT|REVIEW`, `handled=all`. Bộ lọc được điền sẵn; không chọn ngầm một kỳ/tài khoản từ event mới nhất.
- Scope của route lấy từ group key chuẩn hóa. ID notification legacy viết hoa được chuẩn hóa để không tạo danh sách rỗng do so sánh chuỗi. Không dùng `actionUrl` để quyết định đích.
- Người dùng chọn một dòng trong popup để mở route báo cáo/màn duyệt hiện có. Context được đọc lại từ server; target Work/lượt giao/kỳ/tài khoản phải khớp. Cache fulfilled cũ không được mở editor trước lần xác minh khi mount; lỗi, tải lại hoặc quyền thay đổi ẩn nguồn hành động cũ.
- Đọc nhóm gửi `{groupKey, asOfUtc}`. `asOfUtc` là watermark thời gian tạo trên server, tách khỏi thời gian nghiệp vụ của event dùng làm cursor. Event tạo sau watermark giữ chưa đọc dù `OccurredAtUtc` bằng hoặc lùi trước event đã thấy. Watermark dùng millisecond đã hoàn tất để tránh đọc event mới trong cùng millisecond hiện tại; đây là cutoff theo thời gian tạo, không phải lịch sử membership của một transaction.

## Phân trang

Phân trang nằm trên vùng danh sách và canh phải: dropdown nhỏ Mục/trang, ô nhập Trang, hai icon trước/tiếp. Áp dụng cho list card, lịch sử thông báo, bảng nhóm báo cáo và bảng kỳ của chi tiết lượt giao. Enter hoặc rời ô Trang gửi nhảy trực tiếp đến server; không tải lần lượt các trang trung gian. Cursor sau nhảy/back được lưu đúng index; đổi bộ lọc hoặc số phần tử đưa về trang đầu.

Server kiểm page >= 1, giới hạn page size, tràn offset và không cho kết hợp page number với cursor. Nhảy sâu dùng offset sau sort/group; kiểm này không chứng minh năng lực tải ở quy mô production.

## Bằng chứng giao diện

Kiểm bằng trình duyệt trên source UI thật với adapter dữ liệu giả, không gọi DB/host UAT. Đã quan sát một card có 200 báo cáo, popup 10 dòng, nhảy trang 3 tới dòng 21–30 và bấm icon tiếp tới 31–40. Chuông có một dòng 200 thông báo/100 chưa đọc; bấm nhóm chuyển UI sang scope đầu việc + Duyệt báo cáo + Mọi trạng thái. Lịch sử sau đọc vẫn có chip Chưa xử lý. Preview dùng MemoryRouter; query serialization và context route thật được kiểm riêng bằng React/RTK/router tests.

Màn 390px: document không tràn ngang; pager rộng 334px trong vùng list và card rộng 310px. Popup có vùng bảng cuộn ngang riêng (khung 351px, bảng khoảng 763px); document rộng đúng 390px.

![Một card có đủ 200 báo cáo và phân trang canh phải](images/inbox-report-groups-20261006/01-review-groups.jpg)

![Bảng nhóm báo cáo ở trang 3](images/inbox-report-groups-20261006/02-review-table-page-3.jpg)

![Chuông gom nhóm 200 thông báo](images/inbox-report-groups-20261006/03-notification-groups.jpg)

![Bộ lọc tự điền khi mở thông báo](images/inbox-report-groups-20261006/04-notification-prefilled-filters.jpg)

![Đã đọc thông báo và chưa xử lý báo cáo là hai trạng thái riêng](images/inbox-report-groups-20261006/05-history-group-read-vs-processing.jpg)

![Phân trang trên màn 390px](images/inbox-report-groups-20261006/06-mobile-group-pagination.jpg)

![Bảng chi tiết trên màn 390px](images/inbox-report-groups-20261006/07-mobile-report-table.jpg)

## Kiểm tra source và phạm vi còn lại

Kết quả cuối tại [outputs/inbox-report-groups-20261006](../../../outputs/inbox-report-groups-20261006/):

| Kiểm | Kết quả | Log |
| --- | --- | --- |
| FE: 7 file contract, UI, React/RTK/router | 100/100 đạt trên source cuối | `fe-final-tests.log` |
| FE: full app TypeScript | Exit 0 | `fe-final-typecheck.log` |
| FE: ESLint 19 file trong phạm vi | Exit 0 | `fe-final-eslint.log` |
| FE: production Vite bundle | Exit 0; bundle riêng `fe-final-bundle` | `fe-production-final-build.log` |
| BE: assignment groups, 200 thành viên + HTTP/JWT riêng | 37/37 đạt | `be-assignment-group-tests.log` |
| BE: notification groups, snapshot đọc nhóm + HTTP/JWT riêng | 47/47 đạt trên source cuối | `be-notification-snapshot-tests.log` |
| BE: filter/notification regression sau sửa snapshot | 60/60 đạt trên source cuối | `be-snapshot-filter-tests.log` |
| BE: WorkInboxChecks / InboxUxChecks | 43/43 và 51/51 đạt trước sửa cutoff đọc nhóm cuối; không chạy lặp lại các nhánh không đổi | `be-work-tests.log`, `be-ux-tests.log` |
| BE build | 0 lỗi; 92 warning có sẵn ngoài phạm vi | `be-notification-snapshot-build.log` |

Các nhóm kiểm bao gồm count/cursor, nhóm trước phân trang, scope actor hiện hành, cache/quyền thay đổi, page jump, canonical route legacy và cutoff đọc nhóm theo creation time. Hai transport case FE và harness BE giữ unread cho event mới tạo với business time trùng hoặc backdated. Build dùng thư mục output riêng; không ghi đè bundle/shared dependency cache của host chạy. Vite báo output nằm ngoài FE project root nên không tự xóa thư mục; đây là output mới trong workspace, build đạt. Diff check trong phạm vi đạt; không coi warning chuyển CRLF là lỗi sản phẩm.

FE/BE cần được triển khai cùng contract endpoint mới trước khi nghiệm thu trên shared host. Tại thời điểm kết thúc lượt sửa source, chưa restart/deploy shared host, chưa thực hiện UAT bằng các tài khoản/dữ liệu thực và chưa có xác nhận nghiệm thu của Yud. Browser dùng fake data; các harness BE dùng DB/artifact riêng. Không suy rộng fixture 200 thành viên hay lượt HTTP nhỏ thành INP/năng lực production.

Cập nhật runtime sau phản ánh list trống: Yud đã cho phép thay/restart BE local; endpoint mới đã hiện diện và readiness 200. Xem [handoff khôi phục API local](HANDOFF_CONG_VIEC_KHOI_PHUC_API_LOCAL_2026_10_06.md) cho nguyên nhân 404, backup và giới hạn kiểm bằng actor.

## Các file trong lượt sửa này

FE: `WorkInboxPage.tsx`, `InboxReportGroupDialog.tsx`, `InboxPagination.tsx`, `InboxAssignmentDialog.tsx`, `InboxReportRoute.tsx`, `InboxReviewRoute.tsx`, `workInbox.routes.ts`, `workInbox.types.ts`, `NotificationBell.tsx`, `workInboxApi.ts`, `workReadContracts.ts`, `types/notification.ts`; các test/preview riêng trong `tdtd-fe/tests`.

BE: `WorkInboxController.cs`, `WorkInboxDtos.cs`, `NotificationDtos.cs`, `WorkInboxService.cs`, `WorkInboxPipeline.cs`, `WorkInboxNotificationGroups.cs`, phần paging/DTO notification trong `NotificationService.cs`; harness `InboxAssignmentGroupChecks` và `InboxNotificationGroupChecks`.

Workspace còn nhiều WIP ngoài phạm vi từ trước. Không coi toàn bộ git diff là thay đổi của lượt này; không reset, commit hay push.
