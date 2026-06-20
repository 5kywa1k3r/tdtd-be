using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public interface IWorkAssignmentAdvancedSummaryHierarchyService
{
    Task<WorkAssignmentAdvancedSummaryDayNodeDto> RequestDayNodeBuildAsync(
        string configId,
        string dayKey,
        BuildWorkAssignmentAdvancedSummaryDayNodeRequest req,
        string actorUserId,
        CancellationToken ct);

    Task BuildDayNodeJobAsync(
        string configId,
        string dayKey,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct);
}
