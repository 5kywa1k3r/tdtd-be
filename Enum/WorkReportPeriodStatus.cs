namespace tdtd_be.Models.Enums;

/// <summary>
/// Trạng thái ngoài cùng của một kỳ báo cáo runtime.
/// FE nên bám enum này để vẽ badge/list kỳ.
/// </summary>
public enum WorkReportPeriodStatus
{
    Pending = 0,
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    OverduePending = 4,
    OverdueDraft = 5,
    OverdueSubmitted = 6,
    OverdueApproved = 7
}
