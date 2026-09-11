using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowRuntimeService
{
    Task<DynamicFlowPreflightResponse> PreflightAsync(
        string workId,
        DynamicFlowPreflightRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowConfirmResponse> ConfirmAsync(
        string workId,
        DynamicFlowConfirmRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowSubflowLaunchResponse> LaunchSubflowAsync(
        string workId,
        string parentInstanceId,
        string parentStepInstanceId,
        DynamicFlowSubflowLaunchRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowSupplementalCommandResponse> AddSupplementalStepAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowSupplementalAddRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowSupplementalCommandResponse> CancelSupplementalStepAsync(
        string workId,
        string flowInstanceId,
        string supplementalStepId,
        DynamicFlowSupplementalCancelRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowEpochCommandResponse> FinalizeEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowEpochCommandResponse> RollbackEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowEpochCommandResponse> TerminateEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowEpochCommandResponse> RestartEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default);

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

    Task<DynamicFlowBranchActionResponse> ForwardBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default);
}
