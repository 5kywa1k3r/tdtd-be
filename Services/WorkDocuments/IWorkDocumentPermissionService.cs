using tdtd_be.Models;

namespace tdtd_be.Services.WorkDocuments;

public interface IWorkDocumentPermissionService
{
    Task EnsureCanCreateWorkDocumentAsync(string workId, string userId, CancellationToken ct);
    Task<WorkAssignment> EnsureCanCreateAssignmentDocumentAsync(string workId, string assignmentId, string userId, CancellationToken ct);
    Task<bool> CanReadFileAsync(FileDoc file, string userId, CancellationToken ct);
    Task EnsureCanReadFileAsync(FileDoc file, string userId, CancellationToken ct);
    Task<bool> CanDeleteFileAsync(FileDoc file, string userId, CancellationToken ct);
    Task EnsureCanDeleteFileAsync(FileDoc file, string userId, CancellationToken ct);
    Task<List<WorkAssignment>> GetAssignmentUploadTargetsAsync(string workId, string userId, CancellationToken ct);
}
