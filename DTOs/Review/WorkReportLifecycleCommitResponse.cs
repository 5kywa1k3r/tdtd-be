using tdtd_be.DTOs.WorkAssignmentReports;

namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed class WorkReportLifecycleCommitResponse
{
    public string ReportId { get; init; } = string.Empty;
    public bool Committed { get; init; }
    public bool Replay { get; init; }
    public int LifecycleRevision { get; init; }
    public string LifecycleCommitState { get; init; } = WorkReportLifecycleCommitStates.Committed;
    public bool LifecycleProjectionPending { get; init; }
}
