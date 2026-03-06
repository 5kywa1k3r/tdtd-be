using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Domain;

public interface IWorkAssignmentTreeService
{
    Task<string> GenerateAssignmentCodeAsync(string workId, CancellationToken ct = default);

    Task EnsureNoDuplicateAssignmentAsync(
        string workId,
        string? parentAssignmentId,
        string dynamicExcelId,
        List<string> assigneeUserIds,
        string? excludeAssignmentId,
        CancellationToken ct = default);

    Task RebuildDescendantPathAsync(
        WorkAssignment parent,
        string oldParentPath,
        string oldRootAssignmentId,
        CancellationToken ct = default);
}