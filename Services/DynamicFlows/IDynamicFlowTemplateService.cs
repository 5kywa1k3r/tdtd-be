using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowTemplateService
{
    Task<PagedResult<DynamicFlowTemplateDto>> SearchAsync(
        DynamicFlowTemplateSearchRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateDto> GetAsync(
        string id,
        string actorUserId,
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
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateVersionDto> GetVersionAsync(
        string templateId,
        string versionId,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
        string templateId,
        string versionId,
        SaveDynamicFlowTemplateVersionDraftRequest req,
        string actorUserId,
        CancellationToken ct = default);

    // Compatibility alias for PUT /{familyId}/versions/draft. The service owns
    // draft resolution so the legacy route cannot diverge from the P4 handler.
    Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(
        string templateId,
        SaveDynamicFlowTemplateVersionDraftRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
        string templateId,
        string versionId,
        LockDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default);

    // Compatibility alias for POST /versions/{versionId}/lock.
    Task<DynamicFlowTemplateVersionDto> LockVersionAsync(
        string versionId,
        LockDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateDto> ArchiveAsync(
        string templateId,
        ArchiveDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateDto> CloneAsync(
        string templateId,
        CloneDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync(
        string templateId,
        string versionId,
        ReopenDynamicFlowTemplateVersionRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task<DiffDynamicFlowTemplateVersionsDto> DiffVersionsAsync(
        string templateId,
        DiffDynamicFlowTemplateVersionsRequest req,
        string actorUserId,
        CancellationToken ct = default);

    Task DeleteAsync(
        string id,
        DeleteDynamicFlowTemplateRequest req,
        string actorUserId,
        CancellationToken ct = default);

    // Retained temporarily for in-process callers compiled against the pre-P4
    // contract. Canonical HTTP deletion always uses the token-bearing overload.
    Task DeleteAsync(
        string id,
        string actorUserId,
        CancellationToken ct = default);
}
