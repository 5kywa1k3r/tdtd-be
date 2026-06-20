using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public interface IWorkAssignmentAdvancedSummaryDirtyService
{
    Task MarkReportStatusMutationDirtyAsync(
        WorkAssignmentReport report,
        string operation,
        string fromStatus,
        string toStatus,
        string actorUserId,
        CancellationToken ct);

    Task MarkApprovedReportPayloadDirtyAsync(
        WorkAssignmentReport report,
        string operation,
        string actorUserId,
        CancellationToken ct);
}
