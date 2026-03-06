namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Dòng dữ liệu cho màn danh sách ngoài cùng của user trong 1 Work,
/// được nhóm theo DynamicExcelId (template).
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
    /// Tổng số assignment active của user hiện tại đang dùng template này trong work.
    /// </summary>
    public int AssignmentCount { get; set; }

    /// <summary>
    /// Tổng số report hiện có của các assignment thuộc template này trong work.
    /// </summary>
    public int ReportCount { get; set; }

    /// <summary>
    /// Kỳ gần nhất đã phát sinh report trong nhóm template này.
    /// Có thể null nếu chưa có report nào.
    /// </summary>
    public string? LatestPeriodKey { get; set; }

    /// <summary>
    /// Trạng thái của report mới nhất.
    /// Có thể null nếu chưa có report nào.
    /// </summary>
    public int? LatestReportStatus { get; set; }

    /// <summary>
    /// Thời điểm cập nhật report gần nhất.
    /// Có thể null nếu chưa có report nào.
    /// </summary>
    public DateTime? LatestUpdatedAtUtc { get; set; }

    /// <summary>
    /// Một report id đại diện mới nhất để FE mở nhanh nếu cần.
    /// Có thể null nếu chưa có report nào.
    /// </summary>
    public string? LatestReportId { get; set; }


}