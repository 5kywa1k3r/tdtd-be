using tdtd_be.Models.Enums;

namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// DTO chi tiết của một report.
/// FE dùng để mở màn editor hoặc màn xem report.
/// </summary>
public sealed class WorkAssignmentReportResponse
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
    /// Kỳ báo cáo của bản này.
    /// </summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian bắt đầu kỳ.
    /// </summary>
    public DateTime? PeriodStart { get; set; }

    /// <summary>
    /// Thời gian kết thúc kỳ.
    /// </summary>
    public DateTime? PeriodEnd { get; set; }

    /// <summary>
    /// Trạng thái hiện tại của report.
    /// </summary>
    public WorkAssignmentReportStatus Status { get; set; }

    /// <summary>
    /// Snapshot template để FE/debug có thể dùng nếu cần.
    /// Thường FE không cần parse nếu các field rời đã đủ.
    /// </summary>
    public string TemplateSnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot schedule tại thời điểm khởi tạo report.
    /// </summary>
    public string ScheduleSnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// Id template dùng lúc tạo report.
    /// </summary>
    public string DynamicExcelTemplateId { get; set; } = string.Empty;

    /// <summary>
    /// Code template dùng lúc tạo report.
    /// </summary>
    public string DynamicExcelTemplateCode { get; set; } = string.Empty;

    /// <summary>
    /// Tên template dùng lúc tạo report.
    /// </summary>
    public string DynamicExcelTemplateName { get; set; } = string.Empty;

    /// <summary>
    /// Workbook JSON để FE mở Fortune Sheet.
    /// </summary>
    public string RawWorkbookDataJson { get; set; } = string.Empty;

    /// <summary>
    /// Spec JSON để FE render đúng vùng header/data.
    /// </summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>
    /// Hàng bắt đầu của dataRect.
    /// </summary>
    public int DataRectR0 { get; set; }

    /// <summary>
    /// Cột bắt đầu của dataRect.
    /// </summary>
    public int DataRectC0 { get; set; }

    /// <summary>
    /// Hàng kết thúc của dataRect.
    /// </summary>
    public int DataRectR1 { get; set; }

    /// <summary>
    /// Cột kết thúc của dataRect.
    /// </summary>
    public int DataRectC1 { get; set; }

    /// <summary>
    /// Số cột vùng dữ liệu.
    /// </summary>
    public int W { get; set; }

    /// <summary>
    /// Số hàng vùng dữ liệu.
    /// </summary>
    public int H { get; set; }

    /// <summary>
    /// Dữ liệu 1D đã trải phẳng.
    /// FE Phase 1 có thể chưa dùng nhiều, nhưng trả ra để debug/verify rất tiện.
    /// </summary>
    public string Values1DJson { get; set; } = string.Empty;

    /// <summary>
    /// Ghi chú report.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Số phiên bản của report trong cùng kỳ.
    /// </summary>
    public int VersionNo { get; set; }

    /// <summary>
    /// Có phải bản hiện hành không.
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Thời điểm submit nếu có.
    /// </summary>
    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>
    /// User submit nếu có.
    /// </summary>
    public string? SubmittedByUserId { get; set; }

    /// <summary>
    /// Thời điểm tạo bản ghi.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Thời điểm cập nhật cuối.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}