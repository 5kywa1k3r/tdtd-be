using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowRuntimeService
{
    Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(
        string workId,
        CreateDynamicFlowInstanceRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowBranchActionResponse> RollbackBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowBranchActionResponse> TerminateBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowBranchActionResponse> RestartBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default);
}
