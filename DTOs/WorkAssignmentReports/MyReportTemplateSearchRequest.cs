namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Điều kiện tìm kiếm danh sách ngoài cùng của user,
/// nhóm theo DynamicFormTemplateId trong phạm vi 1 Work.
/// </summary>
public sealed class MyReportTemplateSearchRequest
{
    /// <summary>
    /// Trang hiện tại, 0-based.
    /// </summary>
    public int Page { get; set; } = 0;

    /// <summary>
    /// Kích thước trang.
    /// </summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// Từ khóa tìm theo code/name template.
    /// </summary>
    public string? Q { get; set; }

    /// <summary>
    /// Legacy field giữ để tương thích request cũ.
    /// Report list không lọc active tại binding/template/period; active chỉ thuộc Work/WorkAssignment.
    /// </summary>
    public bool? IsActive { get; set; }

    /// <summary>
    /// Chỉ lấy nhóm đã có report hay chưa có report.
    /// null = tất cả
    /// true = đã có ít nhất 1 report
    /// false = chưa có report nào
    /// </summary>
    public bool? HasReport { get; set; }

    /// <summary>
    /// Chỉ lấy nhóm có kỳ quá hạn.
    /// </summary>
    public bool? HasOverduePeriod { get; set; }

    /// <summary>
    /// Trường sắp xếp.
    /// Hỗ trợ:
    /// - dynamicFormTemplateCode
    /// - dynamicFormTemplateName
    /// - latestUpdatedAtUtc
    /// - latestDueAtUtc
    /// - periodCount
    /// </summary>
    public string? SortField { get; set; } = "latestUpdatedAtUtc";

    /// <summary>
    /// Hướng sắp xếp: asc / desc.
    /// </summary>
    public string? SortDirection { get; set; } = "desc";
}
