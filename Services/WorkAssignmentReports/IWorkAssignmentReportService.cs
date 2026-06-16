using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignmentReports;

namespace tdtd_be.Services.WorkAssignmentReports;

/// <summary>
/// Service xử lý runtime báo cáo theo kỳ của WorkAssignment.
/// 
/// Kiến trúc mới:
/// - WorkTemplateAssignee = binding runtime hiện hành
/// - WorkReportPeriod = kỳ báo cáo cần thực hiện
/// - WorkAssignmentReport = dữ liệu báo cáo thực tế của kỳ
/// - WorkAssignmentReportLog = audit log nghiệp vụ
/// </summary>
public interface IWorkAssignmentReportService
{
    /// <summary>
    /// Danh sách ngoài cùng của user trong 1 Work, nhóm theo template runtime hiện hành.
    /// </summary>
    Task<PagedResult<MyReportTemplateRow>> SearchMyReportTemplatesAsync(
        string workId,
        MyReportTemplateSearchRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Detail của 1 template report trong phạm vi 1 work:
    /// - trả template/spec/workbook
    /// - trả danh sách các kỳ phải báo cáo
    /// </summary>
    Task<MyReportTemplateDetailResponse> GetMyReportTemplateDetailAsync(
        string workId,
        string dynamicFormTemplateId,
        string currentUserId,
        string? scopeAssignmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Mở 1 kỳ báo cáo.
    /// Nếu chưa có current report thì backend sẽ tự khởi tạo draft mới cho kỳ đó.
    /// </summary>
    Task<WorkAssignmentReportResponse> OpenPeriodAsync(
        string workReportPeriodId,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Giữ để tương thích với luồng init cũ theo assignment + period.
    /// Có thể dùng nội bộ hoặc migrate dần sang OpenPeriodAsync.
    /// </summary>
    Task<WorkAssignmentReportResponse> InitDraftAsync(
        string workAssignmentId,
        InitWorkAssignmentReportRequest req,
        string currentUserId,
        CancellationToken ct = default);

    Task<WorkAssignmentReportResponse> CreateUserCreatedReportAsync(
        string workAssignmentId,
        CreateUserCreatedReportRequest req,
        string currentUserId,
        CancellationToken ct = default);

    Task DeleteUserCreatedReportAsync(
        string id,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy chi tiết một report theo id.
    /// </summary>
    Task<WorkAssignmentReportResponse> GetByIdAsync(
        string id,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy danh sách report theo assignment.
    /// Dùng cho màn quản trị/history nếu cần.
    /// </summary>
    Task<List<WorkAssignmentReportListRow>> GetByAssignmentAsync(
        string workAssignmentId,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Search/paging report cho các màn quản trị / history.
    /// </summary>
    Task<PagedResult<WorkAssignmentReportListRow>> SearchAsync(
        WorkAssignmentReportSearchRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lưu draft workbook + dữ liệu nghiệp vụ trải phẳng.
    /// </summary>
    Task<WorkAssignmentReportResponse> SaveDraftAsync(
        string id,
        SaveWorkAssignmentReportDraftRequest req,
        string currentUserId,
        CancellationToken ct = default);

    Task<WorkAssignmentReportResponse> ApplyDynamicFormAggregateDraftAsync(
        string id,
        ApplyDynamicFormAggregateDraftRequest req,
        string currentUserId,
        CancellationToken ct = default);

    Task<WorkAssignmentReportResponse> PreviewDynamicFormAggregateDraftAsync(
        string id,
        ApplyDynamicFormAggregateDraftRequest req,
        string currentUserId,
        CancellationToken ct = default);

    Task RefreshDynamicFormAggregateDependentsAsync(
        string sourceReportId,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Nộp báo cáo.
    /// Backend sẽ validate trễ hạn và yêu cầu lý do nếu cần.
    /// </summary>
    Task<WorkAssignmentReportResponse> SubmitAsync(
        string id,
        SubmitWorkAssignmentReportRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Lấy log nghiệp vụ của report.
    /// </summary>
    Task<List<WorkAssignmentReportLogRow>> GetLogsAsync(
        string reportId,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Chấp nhận báo cáo.
    /// Dành cho phase duyệt.
    /// </summary>
    Task<WorkAssignmentReportResponse> AcceptAsync(
        string id,
        AcceptWorkAssignmentReportRequest req,
        string currentUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Trả lại báo cáo để làm lại.
    /// Dành cho phase duyệt.
    /// </summary>
    Task<WorkAssignmentReportResponse> ReturnAsync(
        string id,
        ReturnWorkAssignmentReportRequest req,
        string currentUserId,
        CancellationToken ct = default);

    Task<WorkAssignmentReportResponse> WithdrawSubmittedAsync(
        string id,
        ReturnWorkAssignmentReportRequest req,
        string actorUserId,
        CancellationToken ct = default);
}
