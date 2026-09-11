using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.DynamicFlows;

public sealed class DynamicFlowMappingRequest
{
    public int? ExpectedPayloadRevision { get; set; }
    public int? ExpectedLifecycleRevision { get; set; }
    public string? ExpectedPayloadHash { get; set; }
    public string? CommandId { get; set; }
    public string? PreviewToken { get; set; }
    public string? SourceSignature { get; set; }
    public string? ResultSemanticHash { get; set; }

    // Optional caller assertions. The server always resolves the canonical values
    // from the locked P6 runtime snapshot and rejects any supplied mismatch.
    public string? FlowFamilyId { get; set; }
    public string? FlowVersionId { get; set; }
    public string? FlowPayloadHash { get; set; }
    public string? CatalogVersion { get; set; }
    public string? CatalogSemanticHash { get; set; }
    public string? MappingRuleSetHash { get; set; }
    public string? EvaluatorVersion { get; set; }
    public string? FunctionRegistryVersion { get; set; }
    public string? FunctionRegistryHash { get; set; }
    public string? FlowInstanceId { get; set; }
    public int? ExecutionEpoch { get; set; }
    public string? StepInstanceId { get; set; }
    public string? StepId { get; set; }
    public string? BranchId { get; set; }
    public int? AttemptNo { get; set; }
    public string? TargetAssignmentId { get; set; }
    public string? TargetReportId { get; set; }
    public string? FormFamilyId { get; set; }
    public string? FormVersionId { get; set; }
    public int? FormVersionNo { get; set; }
    public string? FormSchemaHash { get; set; }

    // Legacy request-owned selectors/configuration remain deserializable so old
    // clients receive a stable validation error instead of being trusted.
    public string? SourceMode { get; set; }
    public List<string>? SourceReportIds { get; set; }
    public string? FlowTemplateVersionId { get; set; }
    public string? FlowTemplateId { get; set; }
    public int? FlowTemplateVersionNo { get; set; }
    public string? MappingRulesJson { get; set; }
    public List<DynamicFlowMappingRuleDto>? MappingRules { get; set; }
    public string? ConflictPolicy { get; set; }
    public string? ContributionPolicy { get; set; }
    public bool? RequireSourceReport { get; set; }

    // Historical/forged caller-owned authority and response surfaces stay
    // deserializable so the server can reject them explicitly. They are never
    // trusted as runtime inputs.
    public string? ActorRole { get; set; }
    public JsonNode? Provenance { get; set; }
    public List<DynamicFlowMappingChangeDto>? Changes { get; set; }
    public List<DynamicFlowMappingSourceReportDto>? SourceReports { get; set; }

    // Fail closed on any additional selector/configuration dialect instead of
    // silently ignoring a field that could acquire meaning in another layer.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalCallerInputs { get; set; }
}

public sealed class DynamicFlowMappingRuleDto
{
    public string MappingId { get; set; } = string.Empty;
    public int MappingVersion { get; set; } = 1;
    public string? MappingKind { get; set; }
    public string? SourceDynamicFormTemplateId { get; set; }
    public string? SourceStepId { get; set; }
    public string? SourceStepCode { get; set; }
    public string? SourceSectionId { get; set; }
    public string? SourceSectionCode { get; set; }
    public string? SourceFieldId { get; set; }
    public string? SourceFieldKey { get; set; }
    public string? SourceBlockId { get; set; }
    public string? SourceColumnKey { get; set; }
    public string? TargetDynamicFormTemplateId { get; set; }
    public string? TargetStepId { get; set; }
    public string? TargetStepCode { get; set; }
    public string? TargetSectionId { get; set; }
    public string? TargetSectionCode { get; set; }
    public string? TargetFieldId { get; set; }
    public string? TargetFieldKey { get; set; }
    public string? TargetBlockId { get; set; }
    public string? TargetColumnKey { get; set; }
    public string? ConceptCode { get; set; }
    public string? DataType { get; set; }
    public string? JoinKey { get; set; }
    public string? ValueTransform { get; set; }
    public string? ConflictPolicy { get; set; }
    public string? ContributionPolicy { get; set; }
    public string? EvaluationGrain { get; set; }
    public string? ErrorPolicy { get; set; }
    public List<DynamicFlowMappingInputDto>? Inputs { get; set; }
    public DynamicFlowMappingEndpointDto? Target { get; set; }
    public DynamicFlowMappingCalculationDto? Calculation { get; set; }
}

public sealed class DynamicFlowMappingInputDto
{
    public string InputKey { get; set; } = string.Empty;
    public DynamicFlowMappingEndpointDto Source { get; set; } = new();
    public string? DataType { get; set; }
    public string? Cardinality { get; set; }
    public string? NullPolicy { get; set; }
    public JsonNode? ConstantValue { get; set; }
}

public sealed class DynamicFlowMappingEndpointDto
{
    public string? Kind { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? StepId { get; set; }
    public string? StepCode { get; set; }
    public string? SectionId { get; set; }
    public string? SectionCode { get; set; }
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? BlockId { get; set; }
    public string? ColumnKey { get; set; }
    public string? RowKey { get; set; }
    public string? DataType { get; set; }
}

public sealed class DynamicFlowMappingCalculationDto
{
    public string? Kind { get; set; }
    public string? Operation { get; set; }
    public string? ResultDataType { get; set; }
    public JsonNode? Expression { get; set; }
    public string? FunctionCode { get; set; }
    public int? FunctionVersion { get; set; }
    public Dictionary<string, JsonNode?>? Arguments { get; set; }
    public JsonNode? DefaultValue { get; set; }
}

public sealed class DynamicFlowMappingPreviewResponse
{
    public string TargetReportId { get; set; } = string.Empty;
    public string TargetAssignmentId { get; set; } = string.Empty;
    public string FlowFamilyId { get; set; } = string.Empty;
    public string FlowVersionId { get; set; } = string.Empty;
    public string FlowPayloadHash { get; set; } = string.Empty;
    public string CatalogVersion { get; set; } = string.Empty;
    public string CatalogSemanticHash { get; set; } = string.Empty;
    public string MappingRuleSetHash { get; set; } = string.Empty;
    public string EvaluatorVersion { get; set; } = string.Empty;
    public string FunctionRegistryVersion { get; set; } = string.Empty;
    public string FunctionRegistryHash { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public int ExecutionEpoch { get; set; }
    public string StepInstanceId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public int AttemptNo { get; set; }
    public string FormFamilyId { get; set; } = string.Empty;
    public string FormVersionId { get; set; } = string.Empty;
    public int FormVersionNo { get; set; }
    public string FormSchemaHash { get; set; } = string.Empty;
    public int TargetPayloadRevision { get; set; }
    public string TargetPayloadHash { get; set; } = string.Empty;
    public int TargetLifecycleRevision { get; set; }
    public string SourceSignature { get; set; } = string.Empty;
    public string ResultSemanticHash { get; set; } = string.Empty;
    public string? PreviewToken { get; set; }
    public DateTime? PreviewIssuedAtUtc { get; set; }
    public DateTime? PreviewExpiresAtUtc { get; set; }
    public string MappingCapability { get; set; } = "BLOCKED";
    public string MappingCapabilityReason { get; set; } = string.Empty;
    public bool CanPreview { get; set; }
    public bool CanApply { get; set; }
    public string Freshness { get; set; } = "UNKNOWN";
    public string? ApplyState { get; set; }
    public string? ReceiptId { get; set; }
    public string? CommandId { get; set; }
    public string DataOrigin { get; set; } = string.Empty;
    public string CumulativeContributionMode { get; set; } = string.Empty;
    public string? CumulativeContributionPolicyJson { get; set; }
    public string? SummarySourceJson { get; set; }
    public string? FieldValuesJson { get; set; }
    public string? TableValuesJson { get; set; }
    public List<DynamicFlowMappingSourceReportDto> SourceReports { get; set; } = new();
    public List<DynamicFlowMappingChangeDto> Changes { get; set; } = new();
    public bool HasBlockingConflicts { get; set; }
}

public sealed class DynamicFlowMappingSourceReportDto
{
    public string? ReportId { get; set; }
    public string? WorkAssignmentId { get; set; }
    public string? FlowInstanceId { get; set; }
    public int? ExecutionEpoch { get; set; }
    public string? StepInstanceId { get; set; }
    public string? BranchId { get; set; }
    public int? AttemptNo { get; set; }
    public string? FlowStepId { get; set; }
    public string? FlowStepCode { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? FormFamilyId { get; set; }
    public string? FormVersionId { get; set; }
    public int? FormVersionNo { get; set; }
    public string? FormSchemaHash { get; set; }
    public int? PayloadRevision { get; set; }
    public string? PayloadHash { get; set; }
    public int? LifecycleRevision { get; set; }
    public string? LifecycleStatus { get; set; }
    public string? PeriodInstanceKey { get; set; }
    public bool IdentityRedacted { get; set; }
}

public sealed class DynamicFlowMappingChangeDto
{
    public string MappingId { get; set; } = string.Empty;
    public int MappingVersion { get; set; }
    public string TargetKind { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;
    public string? SourceReportId { get; set; }
    public string? SourceKey { get; set; }
    public string? PreviousValueJson { get; set; }
    public string? NextValueJson { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? ConceptCode { get; set; }
    public string? ContributionPolicy { get; set; }
    public List<DynamicFlowMappingInputProvenanceDto> Sources { get; set; } = new();
}

public sealed class DynamicFlowMappingInputProvenanceDto
{
    public string? InputKey { get; set; } = string.Empty;
    public string? SourceDynamicFormTemplateId { get; set; }
    public string? SourceStepId { get; set; }
    public string? SourceStepCode { get; set; }
    public string? SourceAssignmentId { get; set; }
    public string? SourceReportId { get; set; }
    public int? SourcePayloadRevision { get; set; }
    public string? SourcePayloadHash { get; set; }
    public int? SourceLifecycleRevision { get; set; }
    public string? SourceKey { get; set; }
    public string? RowKey { get; set; }
    public string? ValueJson { get; set; }
}
