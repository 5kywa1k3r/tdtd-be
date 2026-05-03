using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Domain;

public interface IWorkAssignmentTreeService
{
    Task<string> GenerateAssignmentCodeAsync(string workId, CancellationToken ct = default);
}