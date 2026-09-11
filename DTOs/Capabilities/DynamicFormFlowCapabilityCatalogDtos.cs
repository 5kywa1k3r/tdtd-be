using System.Text.Json.Serialization;
using tdtd_be.Common.Capabilities;

namespace tdtd_be.DTOs.Capabilities;

public sealed record DynamicFormFlowCapabilityCatalogDto(
    string CatalogVersion,
    string CatalogSha256,
    string SchemaSha256,
    string ApprovedAt,
    DynamicFormFlowCapabilityTerminologyDto Terminology,
    DynamicFormFlowCapabilityDefaultsMetadata Defaults,
    DynamicFormFlowCapabilityUiGateDto UiGate,
    DynamicFormFlowCapabilityDomainsDto Domains);

public sealed record DynamicFormFlowCapabilityTerminologyDto(
    string FlowScopeName,
    bool FullBpmnClaimAllowed);

public sealed record DynamicFormFlowCapabilityUiGateDto(
    bool Continuous,
    IReadOnlyList<string> RequiredStates,
    IReadOnlyList<string> RequiredActors,
    IReadOnlyList<string> RequiredEvidence);

public sealed record DynamicFormFlowCapabilityDomainsDto(
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> DynamicFormFieldTypes,
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> DynamicFormValueSources,
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> DynamicFormTableModes,
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> DynamicFlowArchetypes,
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> StatisticsCapabilities,
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata> DynamicFlowMappingCapabilities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<StatisticsConfigurationCapabilityMetadata>? StatisticsConfigurationCapabilities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DynamicFormFlowCapabilityItemMetadata>? StatisticsReconciliationCapabilities);
