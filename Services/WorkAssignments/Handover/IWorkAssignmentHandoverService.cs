using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Pickers;

namespace tdtd_be.Services.WorkAssignments.Handover;

public interface IWorkAssignmentHandoverService
{
    Task<WorkAssignmentHandoverUnitScopeDto> ReadUnitScopeAsync(string assignmentId, string actorUserId, CancellationToken ct = default);
    Task<PagedResult<UserPickRow>> SearchCandidatesAsync(
        string assignmentId,
        string fromAssigneeUserId,
        string unitId,
        string? query,
        int page,
        int pageSize,
        string actorUserId,
        CancellationToken ct = default);

    Task<WorkAssignmentHandoverResponse> HandoverAsync(
        string assignmentId,
        HandoverWorkAssignmentRequest request,
        string actorUserId,
        CancellationToken ct = default);

    Task<PagedResult<WorkAssignmentHandoverHistoryRow>> SearchHistoryAsync(
        string workId,
        WorkAssignmentHandoverHistorySearchRequest request,
        string actorUserId,
        CancellationToken ct = default);
}
