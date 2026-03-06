namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Snapshot schedule của assignment tại thời điểm report được tạo.
/// Dùng để lưu lịch sử, tránh phụ thuộc vào assignment hiện tại.
/// </summary>
public sealed class ScheduleSnapshotDTO
{
    /// <summary>
    /// Loại chu kỳ: DAILY / WEEKLY / MONTHLY / QUARTERLY / SEMI_ANNUAL / YEARLY / ONCE...
    /// Giữ string cho linh hoạt theo kiến trúc hiện tại.
    /// </summary>
    public string CycleType { get; set; } = string.Empty;

    /// <summary>
    /// Ngày bắt đầu áp dụng schedule.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// Danh sách thứ trong tuần nếu dùng chu kỳ tuần.
    /// </summary>
    public int[] WeekDays { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Danh sách ngày trong tháng nếu dùng chu kỳ tháng.
    /// </summary>
    public int[] MonthDays { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Danh sách mốc ngày theo quý nếu có.
    /// </summary>
    public int[] QuarterDays { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Danh sách mốc ngày theo bán niên nếu có.
    /// </summary>
    public int[] SemiAnnualDays { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Ghi chú thêm cho schedule.
    /// </summary>
    public string? Note { get; set; }
}