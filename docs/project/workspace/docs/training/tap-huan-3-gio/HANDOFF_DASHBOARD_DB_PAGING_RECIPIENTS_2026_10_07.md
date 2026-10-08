# Phân trang DB và người được giao trong sơ đồ — 07/10/2026

## Yêu cầu đã triển khai

Yud yêu cầu sửa việc BE tải cả tập dữ liệu rồi mới phân trang; đồng thời cần xem người được giao từ mindmap.

POST `/api/dashboard/overview/items/search` giữ request/response và toàn bộ selection hiện có, chuyển sang Mongo aggregation: lọc quyền hiện hành, lọc nghiệp vụ, `$facet` gồm `$count` và nhánh `$sort` → `$skip` → `$limit`. Không còn `ToList()` cả tập kết quả rồi LINQ Skip/Take trong endpoint này. Mỗi lần chỉ đưa một trang metadata và tổng số về BE; FE vẫn 10 mục/trang, API chặn tối đa 50.

`DashboardLeadershipQueryAccess.cs` đọc actor hiện hành; khóa quyền gồm ID Work/role/unit/user và ID/WorkId/Path của seed nhánh cần thiết. Không gọi `LoadLeadershipScopeAsync` hoặc `DashboardUnitReadScope.ResolveAsync` vốn tải toàn bộ payload cây để tìm trang. Giữ phân biệt global, quyền cả Work, quyền nhánh, quyền người nhận của đơn vị quản lý. Unit-head chỉ có quyền theo người nhận vẫn bị mask báo cáo ngoài đơn vị; không gộp quyền xem Work với quyền xem mọi assignment/report. Trường hợp đường dẫn nhánh có slash cuối giữ đúng semantics của resolver cũ.

`DashboardLeadershipItemsQuery.cs` dùng cùng điều kiện đọc để lọc, đếm và phân trang. WorkId được kiểm quyền trước query; request sai selection/filter/date/page tiếp tục bị từ chối. Work/assignment join metadata Work; report join đúng kỳ–Work–assignment–người nhận, yêu cầu đúng current pointer và duy nhất một current candidate. Chỉ lấy tối đa 2 candidate metadata để phát hiện trùng; không đọc nội dung report. Kỳ mất pointer và pointer không xác minh được giữ hai trạng thái riêng. Số nhiệm vụ con bổ sung sau phân trang bằng group trong DB, chỉ cho các Work xuất hiện trên trang (không N+1 theo dòng).

`DashboardLeadershipDbProjection.cs` giữ enum và thời gian cũ: Work 1–5, assignment 0–4, report Draft/Submitted/Approved cộng trạng thái trả lại. Ngày nghiệp vụ +07:00, hạn Work fallback EndDate; nộp đúng hạn dựa thời điểm nộp, không dựa thời điểm duyệt. Có native prefilter cho ưu tiên/trạng thái/6 nhóm hạn trước join. Sort ổn định: Work trạng thái 5 trước, ưu tiên giảm dần, hạn tăng dần (null cuối), ID tăng dần. `$facet` lấy cả count và page từ cùng luồng match.

## Xem người được giao

Thêm icon nhóm người, tooltip **Người được giao** trên card công việc giao/phối hợp. Popup có tên, tài khoản, đơn vị, tìm theo người/đơn vị, 10 người/trang và nút đóng ×. Không sinh thêm node người/đơn vị. Dùng `assignment.assignees` đã được API cây lọc quyền, không suy từ người đã nộp báo cáo; người chưa có báo cáo vẫn hiện. Dedup theo userId, tìm kiếm đưa về trang 1, cập nhật dữ liệu quyền loại người cũ khỏi popup.

Phân trang popup người nhận là phân trang hiển thị trên danh sách assignees của card đã có; không giới thiệu nhầm đây là endpoint truy vấn DB mới. Work mở công việc con rồi xem người nhận trên card tương ứng; không gán người của nhánh này sang nhánh khác.

## Bằng chứng và giới hạn

- 35 selection HTTP dưới tài khoản pv01 được chụp trước/sau: total, ID, thứ tự, trạng thái, hạn, số nhiệm vụ con khớp hoàn toàn.
- HTTP Work/Assignment/Report: ghép 3 trang × 2 dòng trùng chính xác trang 6 dòng, không trùng/bỏ sót ID; total giữ nguyên. Tập quan sát: 8 Work, 20 assignment, 7 report.
- 328 kiểm tra biểu thức thực thi trên Mongo qua `$documents` (không insert/seed): trạng thái, hoàn thành, trả lại, ngày +07:00, fallback hạn, cả 6 nhóm hạn, count/skip/limit.
- 74 kiểm tra projection/contract BE cũ pass; BE build không error, 91 warning hiện có. FE 28 test pass; app TypeScript, scoped lint và Vite build pass.
- Đã nạp BE local 5164 theo quyền restart đã cấp, không restart FE. Health/API đang hoạt động. Evidence/baseline/build/restart đặt trong `outputs/dashboard-db-paging-20261007/`.
- Chưa kiểm trực quan browser vì tool tab localhost bị policy chặn từ lượt trước; không đi đường vòng. Chưa chạy UAT HTTP từng vai trò lãnh đạo/thu hồi quyền trong lượt này; không tạo dữ liệu UAT. Không claim đo INP hoặc benchmark tải lớn.

Phạm vi tối ưu là endpoint danh sách. API biểu đồ tổng quan vẫn giữ đường tổng hợp/cache hiện có; cache + CDC vẫn phase sau. Count chính xác vẫn cần xét tập phù hợp trong DB; phân trang không có nghĩa mọi truy vấn chỉ đọc đúng 10 document. Không đổi index/flush cache, dữ liệu nghiệp vụ, engine tổng hợp hay vòng đời báo cáo; không commit/push.
