using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public interface IWorkTemplateAssigneeBindingService
{
    Task RebuildForAssignmentAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default);
    Task DisableByAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default);

    Task<List<WorkTemplateAssignee>> GetActiveByWorkAndAssigneeAsync(
        string workId,
        string assigneeUserId,
        CancellationToken ct = default);

    Task<List<WorkTemplateAssignee>> GetActiveByWorkTemplateAndAssigneeAsync(
        string workId,
        string dynamicExcelId,
        string assigneeUserId,
        CancellationToken ct = default);
}
