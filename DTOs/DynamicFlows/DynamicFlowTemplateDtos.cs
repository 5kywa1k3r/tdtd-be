using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Models;
using tdtd_be.OpenApi;

namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowTemplateSearchRequest
{
    public string? Query { get; set; }
    public string? Status { get; set; }
    public string? RootDynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 20;
}

public sealed class DynamicFlowTemplateDto
{
    public string Id { get; set; } = string.Empty;
    public string FamilyId
    {
        get => Id;
        set => Id = value;
    }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RootDynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public int FamilyRevision { get; set; }
    public string? OwnerUserId { get; set; }
    public string? OwnerUnitId { get; set; }
    public DynamicFlowDefinitionLineageDto Lineage { get; set; } = new();
    public string Status { get; set; } = "DRAFT";
    public string? CurrentVersionId { get; set; }
    public int? CurrentVersionNo { get; set; }
    public string? CurrentVersionHash { get; set; }
    public DynamicFlowTemplateVersionDto? CurrentVersion { get; set; }
    public DynamicFlowTemplateVersionDto? DraftVersion { get; set; }
    public List<DynamicFlowTemplateVersionDto> Versions { get; set; } = new();
    public bool CanRead { get; set; }
    public bool CanManage { get; set; }
    public bool ExecuteGrant { get; set; }
    public bool DefinitionLockable { get; set; }
    public string ExecutionEligibility { get; set; } = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
    public string? ExecutionBlockedReason { get; set; } = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
    public string? BlockedUntilPhase { get; set; }
    public bool CanExecute { get; set; }
    public bool HasLockedVersion { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public string? ArchivedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DynamicFlowTemplateVersionDto
{
    public string Id { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string FamilyId
    {
        get => TemplateId;
        set => TemplateId = value;
    }
    public string? RootDynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public int VersionNo { get; set; }
    public string Status { get; set; } = "DRAFT";
    public int DraftRevision { get; set; }
    public int SchemaVersion { get; set; } = DynamicFlowDefinitionSchema.CurrentVersion;
    public int AdapterVersion { get; set; } = DynamicFlowDefinitionSchema.CurrentAdapterVersion;
    public string CatalogVersion { get; set; } = string.Empty;
    public string CatalogSemanticHash { get; set; } = string.Empty;
    public DynamicFlowDefinitionLineageDto Lineage { get; set; } = new();
    public DynamicFlowTemplatePayloadDto Payload { get; set; } = new();
    [DeprecatedSchemaProperty("Use payload instead. Retained for legacy clients.")]
    public string PayloadJson { get; set; } = "{}";
    public string PayloadHash { get; set; } = string.Empty;
    public string? ContributionPolicy { get; set; }
    public string? ContributionPolicyHash { get; set; }
    public string? ContributionWarning { get; set; }
    public bool IsUsed { get; set; }
    public bool CanRead { get; set; }
    public bool CanManage { get; set; }
    public bool ExecuteGrant { get; set; }
    public bool DefinitionLockable { get; set; }
    public string ExecutionEligibility { get; set; } = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
    public string? ExecutionBlockedReason { get; set; } = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
    public string? BlockedUntilPhase { get; set; }
    public bool CanExecute { get; set; }
    public string MigrationState { get; set; } = DynamicFlowDefinitionMigrationStates.Canonical;
    public DateTime? LockedAtUtc { get; set; }
    public string? LockedByUserId { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public string? ArchivedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CreateDynamicFlowTemplateRequest
{
    public string CommandId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RootDynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    // Keep the request payload as raw JSON until the versioned P4 adapter and
    // strict validator run. Binding directly to the typed DTO would let the
    // ASP.NET JSON binder discard unknown nested members before we can return
    // the stable DYNAMIC_FLOW_PAYLOAD_UNKNOWN_FIELD contract.
    [SchemaPropertyType(typeof(DynamicFlowTemplatePayloadDto))]
    public JsonElement? Payload { get; set; }
    [DeprecatedSchemaProperty("Use payload instead. Retained for legacy clients.")]
    public string? PayloadJson { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class UpdateDynamicFlowTemplateRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedFamilyRevision { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? RootDynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class SaveDynamicFlowTemplateVersionDraftRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedDraftRevision { get; set; }
    public string ExpectedPayloadHash { get; set; } = string.Empty;
    [SchemaPropertyType(typeof(DynamicFlowTemplatePayloadDto))]
    public JsonElement? Payload { get; set; }
    [DeprecatedSchemaProperty("Use payload instead. Retained for legacy clients.")]
    public string? PayloadJson { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class LockDynamicFlowTemplateVersionRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedFamilyRevision { get; set; }
    public int ExpectedDraftRevision { get; set; }
    public string ExpectedPayloadHash { get; set; } = string.Empty;
    public string? ContributionPolicy { get; set; }
    public bool? AcknowledgeContributionWarning { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class DeleteDynamicFlowTemplateRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedFamilyRevision { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ArchiveDynamicFlowTemplateRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedFamilyRevision { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class CloneDynamicFlowTemplateRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedFamilyRevision { get; set; }
    public string SourceVersionId { get; set; } = string.Empty;
    public int SourceDraftRevision { get; set; }
    public string SourcePayloadHash { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Description { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ReopenDynamicFlowTemplateVersionRequest
{
    public string CommandId { get; set; } = string.Empty;
    public int ExpectedFamilyRevision { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class DiffDynamicFlowTemplateVersionsRequest
{
    public string FromVersionId { get; set; } = string.Empty;
    public string ToVersionId { get; set; } = string.Empty;
}

public sealed class DiffDynamicFlowTemplateVersionsDto
{
    public string FamilyId { get; set; } = string.Empty;
    public string FromVersionId { get; set; } = string.Empty;
    public string ToVersionId { get; set; } = string.Empty;
    public string FromPayloadHash { get; set; } = string.Empty;
    public string ToPayloadHash { get; set; } = string.Empty;
    public List<DynamicFlowDefinitionDiffOperationDto> Operations { get; set; } = new();
}

public sealed class DynamicFlowDefinitionDiffOperationDto
{
    public string Op { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public JsonElement? FromValue { get; set; }
    public JsonElement? ToValue { get; set; }
}

public sealed class DynamicFlowDefinitionLineageDto
{
    public string? OriginFamilyId { get; set; }
    public string? OriginVersionId { get; set; }
}

public static class DynamicFlowDefinitionDiffOperations
{
    public const string Add = "ADD";
    public const string Remove = "REMOVE";
    public const string Replace = "REPLACE";
}
