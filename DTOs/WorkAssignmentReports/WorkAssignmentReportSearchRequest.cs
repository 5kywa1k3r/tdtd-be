namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Điều kiện tìm kiếm danh sách report theo kỳ của WorkAssignment.
/// 
/// Lưu ý:
/// - Page đang dùng chuẩn 0-based giống PagedResult.
/// - DTO này là request gửi từ FE lên backend để search/filter/sort.
/// </summary>
public sealed class WorkAssignmentReportSearchRequest
{
    /// <summary>
    /// Trang hiện tại, bắt đầu từ 0.
    /// </summary>
    public int Page { get; set; } = 0;

    /// <summary>
    /// Số bản ghi mỗi trang.
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Id Work gốc.
    /// Dùng khi cần search toàn bộ report thuộc một work.
    /// </summary>
    public string? WorkId { get; set; }

    /// <summary>
    /// Id WorkAssignment.
    /// Dùng khi chỉ lấy report của một node giao việc cụ thể.
    /// Đây sẽ là filter dùng nhiều nhất ở Phase 1.
    /// </summary>
    public string? WorkAssignmentId { get; set; }

    /// <summary>
    /// Từ khóa tìm kiếm tự do.
    /// Có thể match vào:
    /// - PeriodKey
    /// - DynamicExcelTemplateCode
    /// - DynamicExcelTemplateName
    /// </summary>
    public string? Q { get; set; }

    /// <summary>
    /// Kỳ báo cáo chính xác.
    /// Ví dụ:
    /// - 2026-03
    /// - 2026-Q1
    /// - 2026-W10
    /// </summary>
    public string? PeriodKey { get; set; }

    /// <summary>
    /// Trạng thái report.
    /// Map theo enum WorkAssignmentReportStatus:
    /// 1 = Draft
    /// 2 = Submitted
    /// 3 = Approved
    /// 4 = Rejected
    /// 5 = Locked
    /// </summary>
    public int? Status { get; set; }

    /// <summary>
    /// Chỉ lấy bản hiện hành của mỗi kỳ hay không.
    /// Nếu null thì không lọc theo điều kiện này.
    /// </summary>
    public bool? IsCurrent { get; set; }

    /// <summary>
    /// Trường sắp xếp.
    /// Gợi ý:
    /// - updatedAtUtc
    /// - createdAtUtc
    /// - periodKey
    /// - versionNo
    /// </summary>
    public string? SortField { get; set; } = "updatedAtUtc";

    /// <summary>
    /// Hướng sắp xếp: asc / desc.
    /// </summary>
    public string? SortDirection { get; set; } = "desc";
}