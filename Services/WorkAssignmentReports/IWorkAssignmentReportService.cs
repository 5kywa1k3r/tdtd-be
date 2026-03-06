using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignmentReports;

namespace tdtd_be.Services.WorkAssignmentReports;

/// <summary>
/// Service xử lý dữ liệu báo cáo theo kỳ của WorkAssignment.
/// 
/// Phase 1 tập trung vào:
/// - khởi tạo draft từ assignment + template
/// - lấy detail
/// - lấy list/search
/// - lưu draft workbook + values1D
/// </summary>
public interface IWorkAssignmentReportService
{
    /// <summary>
    /// Khởi tạo 1 report draft mới cho 1 assignment tại 1 kỳ cụ thể.
    /// Report sẽ snapshot:
    /// - template hiện tại
    /// - schedule hiện tại
    /// - workbook gốc
    /// </summary>
    Task<WorkAssignmentReportResponse> InitDraftAsync(
        string workAssignmentId,
        InitWorkAssignmentReportRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy chi tiết 1 report theo id.
    /// </summary>
    Task<WorkAssignmentReportResponse> GetByIdAsync(
        string id,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy danh sách report theo assignment.
    /// Hữu ích cho tab Reports của node giao việc.
    /// </summary>
    Task<List<WorkAssignmentReportListRow>> GetByAssignmentAsync(
        string workAssignmentId,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Search/paging report.
    /// Dùng khi cần filter theo kỳ, trạng thái, current...
    /// </summary>
    Task<PagedResult<WorkAssignmentReportListRow>> SearchAsync(
        WorkAssignmentReportSearchRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lưu draft workbook của report.
    /// Backend sẽ tự extract values1D từ workbook.
    /// </summary>
    Task<WorkAssignmentReportResponse> SaveDraftAsync(
        string id,
        SaveWorkAssignmentReportDraftRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Danh sách ngoài cùng của user trong 1 Work, nhóm theo template.
    /// </summary>
    Task<PagedResult<MyReportTemplateRow>> SearchMyReportTemplatesAsync(
        string workId,
        MyReportTemplateSearchRequest req,
        string currentUserId,
        CancellationToken ct = default);
}