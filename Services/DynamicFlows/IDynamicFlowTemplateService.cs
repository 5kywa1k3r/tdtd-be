using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowTemplateService
{
    Task<PagedResult<DynamicFlowTemplateDto>> SearchAsync(
        DynamicFlowTemplateSearchRequest req,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateDto> GetAsync(
        string id,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateDto> CreateAsync(
        CreateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateDto> UpdateAsync(
        string id,
        UpdateDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<List<DynamicFlowTemplateVersionDto>> ListVersionsAsync(
        string templateId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
        string templateId,
        SaveDynamicFlowTemplateVersionDraftRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
        string versionId,
        LockDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task DeleteAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default);
}
