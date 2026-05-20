using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.WorkAssignments.Review;

namespace tdtd_be.Services.WorkAssignments;

public interface IWorkAssignmentService
{
    Task<List<WorkAssignmentListResponse>> GetByWorkIdAsync(string workId, string actorUserId, CancellationToken ct = default);
    Task<List<WorkAssignmentListResponse>> GetMyReportAssignmentsAsync(string workId, string actorUserId, CancellationToken ct = default);
    Task<List<WorkAssignmentListResponse>> GetMyReviewParentAssignmentsAsync(string workId, string actorUserId, CancellationToken ct = default);
    Task<List<WorkAssignmentListResponse>> GetMyParentCandidatesAsync(string workId, string actorUserId, CancellationToken ct = default);
    Task<WorkAssignmentResponse?> GetByIdAsync(string id, string actorUserId, CancellationToken ct = default);
    Task<List<WorkAssignmentListResponse>> GetChildrenAsync(string parentAssignmentId, string actorUserId, CancellationToken ct = default);
    Task<WorkAssignmentResponse> CreateAsync(string workId, SaveWorkAssignmentRequest req, string actorUserId, CancellationToken ct = default);
    Task<WorkAssignmentResponse?> UpdateDataSourceRulesAsync(string id, UpdateWorkAssignmentDataSourceRulesRequest req, string actorUserId, CancellationToken ct = default);
    Task<WorkAssignmentResponse?> UpdateAutoApproveConditionAsync(string id, UpdateWorkAssignmentAutoApproveConditionRequest req, string actorUserId, CancellationToken ct = default);
    Task<WorkAssignmentResponse?> CompleteAsync(string id, CompleteWorkAssignmentRequest req, string actorUserId, CancellationToken ct = default);
    Task<bool> DeactivateAsync(string id, string actorUserId, CancellationToken ct = default);
    Task<bool> ActivateAsync(string id, string actorUserId, CancellationToken ct = default);
}
