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

    Task<WorkAssignmentAdvancedSummaryMonthNodeDto> RequestMonthNodeBuildAsync(
        string configId,
        string monthKey,
        BuildWorkAssignmentAdvancedSummaryMonthNodeRequest req,
        string actorUserId,
        CancellationToken ct);

    Task BuildMonthNodeJobAsync(
        string configId,
        string monthKey,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryYearNodeDto> RequestYearNodeBuildAsync(
        string configId,
        string yearKey,
        BuildWorkAssignmentAdvancedSummaryYearNodeRequest req,
        string actorUserId,
        CancellationToken ct);

    Task BuildYearNodeJobAsync(
        string configId,
        string yearKey,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryHierarchyQueryResponse> QueryHierarchyAsync(
        string configId,
        QueryWorkAssignmentAdvancedSummaryHierarchyRequest req,
        string actorUserId,
        CancellationToken ct);
}
