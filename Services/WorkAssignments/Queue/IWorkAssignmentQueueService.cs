using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Queue;

public interface IWorkAssignmentQueueService
{
    Task UpsertPeriodAsync(WorkReportPeriod period, string actorUserId, CancellationToken ct = default);
    Task DisableByPeriodAsync(string workAssignmentId, string assigneeUserId, string periodKey, string actorUserId, CancellationToken ct = default);
    Task DisableByAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default);
}
