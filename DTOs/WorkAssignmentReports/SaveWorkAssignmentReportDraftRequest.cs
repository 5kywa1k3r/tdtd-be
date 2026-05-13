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
