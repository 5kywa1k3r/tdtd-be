namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request lưu draft report.
/// FE gửi workbook hiện tại + values1D đã flatten từ dataRect.
/// </summary>
public sealed class SaveWorkAssignmentReportDraftRequest
{
    /// <summary>
    /// Workbook JSON của FortuneSheet sau khi user nhập.
    /// FE sẽ stringify từ rawWorkbookData.
    /// </summary>
    public string RawWorkbookDataJson { get; set; } = string.Empty;

    /// <summary>
    /// Dữ liệu 1D đã trải phẳng từ vùng dataRect.
    /// FE dùng extractNumericValues1D(...) để tính trước.
    /// </summary>
    public List<decimal?> Values1D { get; set; } = new();

    /// <summary>
    /// Ghi chú báo cáo nếu có.
    /// </summary>
    public string? Note { get; set; }
}