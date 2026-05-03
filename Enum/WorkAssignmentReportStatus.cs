namespace tdtd_be.Models.Enums;

/// <summary>
/// Trạng thái của một bản ghi báo cáo cụ thể.
///
/// Lưu ý:
/// - Đây là status của record WorkAssignmentReport
/// - Không phải status ngoài cùng của kỳ
/// </summary>
public enum WorkAssignmentReportStatus
{
    Draft = 0,
    Submitted = 1,
    Approved = 2
}
