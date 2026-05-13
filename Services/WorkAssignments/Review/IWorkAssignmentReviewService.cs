using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignments.Review;

namespace tdtd_be.Services.WorkAssignments.Review;

public interface IWorkAssignmentReviewService
{
    Task<PagedResult<ReviewChildRowDto>> SearchChildrenForReviewAsync(ReviewChildSearchRequest req, CancellationToken ct = default);
    Task<PagedResult<ReviewSummaryRowDto>> SearchSummaryForReviewAsync(ReviewSummarySearchRequest req, CancellationToken ct = default);
    Task<PagedResult<ReviewReportFlatRowDto>> SearchReportsForReviewAsync(ReviewReportFlatSearchRequest req, CancellationToken ct = default);
    Task ApproveReportAsync(string reportId, ApproveReportRequest req, CancellationToken ct = default);
    Task ReturnReportAsync(string reportId, ReturnReportRequest req, CancellationToken ct = default);
    Task DeactivateReportAsync(string reportId, ReportActiveRequest req, CancellationToken ct = default);
    Task ReactivateReportAsync(string reportId, ReportActiveRequest req, CancellationToken ct = default);
    Task<bool> EvaluateAssignmentAsync(string assignmentId, EvaluateAssignmentRequest req, CancellationToken ct = default);
    Task<PagedResult<WorkAssignmentEvaluationLogRow>> GetEvaluationLogsAsync(string assignmentId, int page, int pageSize, CancellationToken ct = default);
    Task RecallApprovedReportAsync(string reportId, ReturnReportRequest req, CancellationToken ct);
}
