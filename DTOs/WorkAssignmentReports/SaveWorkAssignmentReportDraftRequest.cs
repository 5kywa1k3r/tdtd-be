namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request lưu draft report.
/// FE gửi workbook hiện tại + values1D đã flatten từ dataRect,
/// đồng thời gửi thêm các field nghiệp vụ trải phẳng mà lãnh đạo quan tâm.
/// </summary>
public sealed class SaveWorkAssignmentReportDraftRequest
{
    /// <summary>
    /// Dữ liệu 1D đã trải phẳng từ vùng dataRect.
    /// FE dùng extractNumericValues1D(...) để tính trước.
    /// </summary>
    public List<decimal?> Values1D { get; set; } = new();

    /// <summary>
    /// Dynamic Form field values JSON. Kept raw as source of truth for non-Excel fields.
    /// </summary>
    public string? FieldValuesJson { get; set; }

    /// <summary>
    /// Dynamic table metadata JSON, including normalized runtime row labels.
    /// </summary>
    public string? TableValuesJson { get; set; }

    /// <summary>
    /// Tình trạng hiện tại / trạng thái thực tế của công việc theo góc nhìn nghiệp vụ.
    /// Đây là text nghiệp vụ, không phải enum lifecycle.
    /// </summary>
    public string? CurrentProgressStatus { get; set; }

    /// <summary>
    /// Lý do / diễn giải chính của báo cáo.
    /// </summary>
    public string? ReportReason { get; set; }

    /// <summary>
    /// Khó khăn, vướng mắc.
    /// </summary>
    public string? Difficulties { get; set; }

    /// <summary>
    /// Phương án giải quyết / đề xuất.
    /// </summary>
    public string? ProposedSolution { get; set; }

    /// <summary>
    /// Lý do trễ hạn nếu có.
    /// </summary>
    public string? LateReason { get; set; }

    /// <summary>
    /// Ghi chú chung nếu có.
    /// </summary>
    public string? Note { get; set; }
}
