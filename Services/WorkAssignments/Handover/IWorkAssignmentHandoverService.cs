using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.Common;

namespace tdtd_be.Services.WorkAssignments.Handover;

public interface IWorkAssignmentHandoverService
{
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
