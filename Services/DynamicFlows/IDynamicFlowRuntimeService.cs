using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowRuntimeService
{
    Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(
        string workId,
        CreateDynamicFlowInstanceRequest req,
        string actorUserId,
        CancellationToken ct = default);
}
