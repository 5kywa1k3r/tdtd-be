namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Dòng dữ liệu cho màn danh sách ngoài cùng của user trong 1 Work,
/// được nhóm theo DynamicExcelId (template runtime hiện hành).
/// 
/// Đây KHÔNG phải report record.
/// Đây là "nhóm biểu mẫu báo cáo" mà user hiện tại đang phải xử lý.
/// </summary>
public sealed class MyReportTemplateRow
{
    /// <summary>
    /// Id template (DynamicExcel).
    /// </summary>
    public string DynamicExcelId { get; set; } = string.Empty;

    /// <summary>
    /// Code template.
    /// </summary>
    public string DynamicExcelCode { get; set; } = string.Empty;

    /// <summary>
    /// Tên template.
    /// </summary>
    public string DynamicExcelName { get; set; } = string.Empty;

    /// <summary>
    /// Số binding runtime active hiện hành của user cho template này trong work.
    /// Thường trong kiến trúc mới phần lớn sẽ là 1, nhưng vẫn để mở rộng.
    /// </summary>
    public int BindingCount { get; set; }

    /// <summary>
    /// Số kỳ đã materialize cho template này.
    /// </summary>
    public int PeriodCount { get; set; }

    /// <summary>
    /// Số kỳ đã có report.
    /// </summary>
    public int ReportCount { get; set; }

    /// <summary>
    /// Kỳ gần nhất theo runtime.
    /// </summary>
    public string? LatestPeriodKey { get; set; }

    /// <summary>
    /// Trạng thái kỳ gần nhất.
    /// Đây là status ngoài cùng của kỳ, không phải status của bản report.
    /// </summary>
    public int? LatestPeriodStatus { get; set; }

    /// <summary>
    /// Hạn nộp gần nhất.
    /// </summary>
    public DateTime? LatestDueAtUtc { get; set; }

    /// <summary>
    /// Report id hiện hành gần nhất để FE mở nhanh nếu cần.
    /// </summary>
    public string? LatestReportId { get; set; }

    /// <summary>
    /// Period id hiện hành gần nhất để FE mở nhanh nếu cần.
    /// </summary>
    public string? LatestPeriodId { get; set; }

    /// <summary>
    /// Thời điểm cập nhật gần nhất từ period/report.
    /// </summary>
    public DateTime? LatestUpdatedAtUtc { get; set; }

    /// <summary>
    /// Có kỳ quá hạn hay không.
    /// </summary>
    public bool HasOverduePeriod { get; set; }
}