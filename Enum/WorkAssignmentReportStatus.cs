namespace tdtd_be.Models.Enums;

/// <summary>
/// Trạng thái của 1 bản báo cáo theo kỳ của WorkAssignment.
/// Phase 1 mới cần DRAFT là chính, nhưng khai báo sẵn để đỡ sửa enum sau.
/// </summary>
public enum WorkAssignmentReportStatus
{
    /// <summary>
    /// Bản nháp, còn được sửa.
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Đã nộp báo cáo.
    /// </summary>
    Submitted = 2,

    /// <summary>
    /// Đã được duyệt.
    /// </summary>
    Approved = 3,

    /// <summary>
    /// Bị từ chối, có thể cần sửa/nộp lại tùy rule sau này.
    /// </summary>
    Rejected = 4,

    /// <summary>
    /// Đã khóa, không cho sửa nữa.
    /// </summary>
    Locked = 5
}