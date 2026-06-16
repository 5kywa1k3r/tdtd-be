namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request lưu draft report.
/// FE gửi Dynamic Form payload hiện tại; nội dung nghiệp vụ nằm trong FieldValuesJson/TableValuesJson.
/// </summary>
public sealed class SaveWorkAssignmentReportDraftRequest
{
    /// <summary>
    /// Dữ liệu 1D đã trải phẳng từ vùng dataRect.
    /// Giá trị giữ theo kiểu cấu hình của Dynamic Excel: number/text/date/boolean/null.
    /// </summary>
    public List<object?> Values1D { get; set; } = new();

    /// <summary>
    /// Dynamic Form field values JSON. Kept raw as source of truth for non-Excel fields.
    /// </summary>
    public string? FieldValuesJson { get; set; }

    /// <summary>
    /// Dynamic table metadata JSON, including normalized runtime row labels.
    /// </summary>
    public string? TableValuesJson { get; set; }

    /// <summary>
    /// Nguồn dữ liệu của report: nhập tay, tự tổng hợp, copy tổng hợp, hoặc mapping một phần.
    /// Nếu đổi nguồn mà không gửi CumulativeContributionMode, BE tự áp default theo nguồn.
    /// </summary>
    public string? DataOrigin { get; set; }

    /// <summary>
    /// INCLUDE: report này được tính vào thống kê/lũy kế; EXCLUDE: bỏ khỏi thống kê/lũy kế.
    /// </summary>
    public string? CumulativeContributionMode { get; set; }

    /// <summary>
    /// JSON policy override theo field/cell/metric cho luồng partial mapping.
    /// </summary>
    public string? CumulativeContributionPolicyJson { get; set; }

    /// <summary>
    /// JSON mô tả nguồn tổng hợp/mapping để audit và mở lại draft.
    /// </summary>
    public string? SummarySourceJson { get; set; }

    public DateTime? CompletedDate { get; set; }

    /// <summary>
    /// Lý do trễ hạn nếu có.
    /// </summary>
    public string? LateReason { get; set; }

    /// <summary>
    /// Ghi chú chung nếu có.
    /// </summary>
    public string? Note { get; set; }
}
