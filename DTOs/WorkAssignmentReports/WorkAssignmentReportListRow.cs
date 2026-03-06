using tdtd_be.Models.Enums;

namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Dòng dữ liệu gọn để render danh sách report của một assignment.
/// </summary>
public sealed class WorkAssignmentReportListRow
{
    /// <summary>
    /// Id report.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Id work gốc.
    /// </summary>
    public string WorkId { get; set; } = string.Empty;

    /// <summary>
    /// Id assignment gốc.
    /// </summary>
    public string WorkAssignmentId { get; set; } = string.Empty;

    /// <summary>
    /// Kỳ báo cáo.
    /// </summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>
    /// Trạng thái report.
    /// </summary>
    public WorkAssignmentReportStatus Status { get; set; }

    /// <summary>
    /// Số phiên bản của report trong cùng kỳ.
    /// </summary>
    public int VersionNo { get; set; }

    /// <summary>
    /// Có phải bản hiện hành hay không.
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Id template đang dùng lúc tạo report.
    /// </summary>
    public string DynamicExcelTemplateId { get; set; } = string.Empty;

    /// <summary>
    /// Code template để hiển thị nhanh.
    /// </summary>
    public string DynamicExcelTemplateCode { get; set; } = string.Empty;

    /// <summary>
    /// Tên template để hiển thị nhanh.
    /// </summary>
    public string DynamicExcelTemplateName { get; set; } = string.Empty;

    /// <summary>
    /// Thời điểm submit nếu đã submit.
    /// </summary>
    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>
    /// Thời điểm update cuối.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}