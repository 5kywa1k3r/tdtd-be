namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Request khởi tạo một report draft mới cho một WorkAssignment theo kỳ.
///
/// Lưu ý:
/// - DTO này giữ lại để tương thích với luồng cũ nếu cần.
/// - Luồng mới ưu tiên đi qua WorkReportPeriod.
/// </summary>
public sealed class InitWorkAssignmentReportRequest
{
    /// <summary>
    /// Kỳ báo cáo cần tạo.
    /// Ví dụ:
    /// - 2026-03
    /// - 2026-Q1
    /// - 2026-W10
    /// - ONCE
    /// </summary>
    public string PeriodKey { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian bắt đầu của kỳ báo cáo.
    /// Có thể FE truyền lên hoặc BE tự suy từ schedule.
    /// </summary>
    public DateTime? PeriodStart { get; set; }

    /// <summary>
    /// Thời gian kết thúc của kỳ báo cáo.
    /// </summary>
    public DateTime? PeriodEnd { get; set; }

    /// <summary>
    /// Hạn cuối nộp của kỳ.
    /// Nếu null thì BE có thể tự tính theo periodEnd/schedule.
    /// </summary>
    public DateTime? DueAtUtc { get; set; }

    /// <summary>
    /// Ghi chú khi khởi tạo report nếu cần.
    /// </summary>
    public string? Note { get; set; }
}
