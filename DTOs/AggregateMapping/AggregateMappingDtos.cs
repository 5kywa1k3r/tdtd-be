using System.Text.Json;

namespace tdtd_be.DTOs.AggregateMapping;

// P01 wire contract only. No controller, persistence, evaluator or runtime activation.
public sealed record AggregateRecipeDto
{
    public required int SchemaVersion { get; init; }
    public required string SemanticProfile { get; init; }
    public required List<AggregateNodeDto> Nodes { get; init; }
    public required List<AggregateEdgeDto> Edges { get; init; }
    public required List<AggregateTimeRuleDto> TimeRules { get; init; }
}

public sealed record AggregateFormPinDto(string FormId, string FamilyId, int VersionNo, string SchemaHash);
public sealed record AggregatePortDto(string Id, string ValueType, string Shape, string? MemberId = null, string? TimeRuleId = null);
public sealed record AggregateEndpointDto(string NodeId, string PortId);
public sealed record AggregateEdgeDto(string Id, AggregateEndpointDto From, AggregateEndpointDto To);
public sealed record AggregateOutputExpressionDto(string PortId, AggregateExpressionDto Expression);

public sealed record AggregateNodeDto
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required List<AggregatePortDto> Inputs { get; init; }
    public required List<AggregatePortDto> Outputs { get; init; }
    public AggregateFormPinDto? Form { get; init; }
    public string? Origin { get; init; }
    public string? SourceCardinality { get; init; }
    public List<AggregateOutputExpressionDto>? Expressions { get; init; }
    public AggregateExpressionDto? Predicate { get; init; }
    public List<AggregateTableAssignmentDto>? TableAssignments { get; init; }
}

// Number literals are decimal strings, dates are ISO date-only strings. No executable text.
public sealed record AggregateReportFilterDto(string? FromDate = null, string? ToDate = null,
    string? DueFromDate = null, string? DueToDate = null, List<string>? PeriodKeys = null,
    List<string>? UnitIds = null, List<string>? Kinds = null);

public sealed record AggregateExpressionDto
{
    public required string Kind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AggregateListPipelineDto? ListPipeline { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AggregateListMergeDto? ListMerge { get; init; }
    public string? Value { get; init; }
    public string? Ref { get; init; }
    public string? Name { get; init; }
    public List<AggregateExpressionDto>? Arguments { get; init; }
    public AggregateFunctionOptionsDto? Options { get; init; }
    public AggregateTableAreaDto? Area { get; init; }
    public int? ColumnIndex { get; init; }
    public AggregateExpressionDto? Predicate { get; init; }
}

public sealed record AggregateFunctionOptionsDto
{
    public string? Basis { get; init; }
    public bool? Trim { get; init; }
    public bool? CaseSensitive { get; init; }
    public string? Separator { get; init; }
    public string? Order { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UnitDisplay { get; init; }
    public int? ColumnIndex { get; init; }
}

public sealed record AggregateTimeRuleDto
{
    public AggregateReportSetDto? ReportSet { get; init; }
    public AggregateReportFilterDto? ReportFilter { get; init; }
    public required string Id { get; init; }
    public required string Mode { get; init; }
    public required string SourceDateBasis { get; init; }
    public required string Match { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? SourceDateMemberId { get; init; }
}

public sealed record AggregateReportSetDto(int Version, string Junction, List<AggregateReportConditionDto> Conditions);
public sealed record AggregateReportConditionDto(string Field, string Operator, List<string> Values,
    string? Mode = null);

// Excel-like 1-based coordinates within a pinned table schema (tracker section 49).
// Schema migration must preview reference changes; row identities are not business keys.
public sealed record AggregateTableAreaDto(string Kind, int? RowStart, int? RowEnd, int? ColumnStart, int? ColumnEnd);
public sealed record AggregateTableAssignmentDto(
    string Id, string? SourceInputPortId, string TargetOutputPortId,
    AggregateTableAreaDto? Source, AggregateTableAreaDto Target, AggregateExpressionDto? Expression);
public sealed record AggregateConfigScopeDto(string WorkId, string AssignmentId, string OwnerUserId);
public sealed record AggregateConfigRevisionDto(
    string ConfigId, long Revision, AggregateConfigScopeDto Scope, string TargetBindingId, AggregateFormPinDto TargetForm,
    AggregateRecipeDto Recipe, string RecipeHash, string CreatedByUserId, string CreatedAtUtc);
public sealed record AggregateConfigSaveRequestDto(
    string CommandId, long ExpectedHeadRevision, AggregateRecipeDto Recipe,
    string ImpactPreviewToken, List<AggregateInstanceRevisionPinDto> MigrateInstances);
public sealed record AggregateConfigCreateRequestDto(string CommandId, string TargetBindingId, AggregateFormPinDto TargetForm, AggregateRecipeDto Recipe);
public sealed record AggregateInstanceRevisionPinDto(string InstanceId, long ExpectedRevision);
public sealed record AggregateInstanceDto(
    string InstanceId, AggregatePeriodContextDto Context, string ConfigId, long ConfigRevision,
    long Revision, string State, AggregateInstanceOverrideDto? Overrides,
    AggregateInstanceSelectionDto Selection, string OverrideState, AggregateRawDraftDto? RawDraft);
public sealed record AggregateExpressionOverrideDto(string NodeId, string PortId, AggregateExpressionDto Expression);
public sealed record AggregateTableOverrideDto(string NodeId, List<AggregateTableAssignmentDto> Assignments);
public sealed record AggregateInstanceOverrideDto(
    string BaseRecipeHash, List<AggregateTimeRuleDto> TimeRules,
    List<AggregateExpressionOverrideDto> Expressions, List<AggregateTableOverrideDto> Tables);
public sealed record AggregateUnlinkRequestDto(
    string CommandId, AggregateExpectedRevisionsDto Expected, List<AggregateEndpointDto> Targets, string PreviewToken);
public sealed record AggregateSourceSearchRequestDto(
    AggregatePeriodContextDto Context, string InstanceId, string ConfigId, long ConfigRevision,
    string SourceNodeId, string? Cursor, int PageSize);
public sealed record AggregateErrorDto(string Code, string RequestId, List<AggregateIssueDto> Issues, bool Retryable);

public sealed record AggregatePeriodContextDto(
    string Kind, string WorkId, string AssignmentId, string BindingId,
    string? ReportId, string? WorkReportPeriodId, string? PeriodInstanceKey,
    string? PeriodKey, string? DataStartDate, string? DataEndDate,
    string? DueAtUtc, string ScheduleRevision)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AggregateViewOwnerDto? View { get; init; }
}
public sealed record AggregateViewOwnerDto(string Kind, string Id, string ViewId);

public sealed record AggregateExpectedRevisionsDto(
    int PayloadRevision, int LifecycleRevision, long InstanceRevision,
    long ConfigRevision, string TargetSchemaHash, string InputDigest);

public sealed record AggregateApplyRequestDto(
    string CommandId, AggregatePeriodContextDto Context, string InstanceId,
    string ConfigId, AggregateExpectedRevisionsDto Expected,
    AggregateRecipeDto Recipe, AggregateInstanceSelectionDto Selection, string PreviewToken);

// Concrete report IDs belong to one instance, never the reusable recipe.
public sealed record AggregateSourceSelectionDto(
    string SourceNodeId, string Mode, List<string> ReportIds, List<string> ExcludedReportIds);
public sealed record AggregateInstanceSelectionDto(List<AggregateSourceSelectionDto> Sources);

public sealed record AggregateSourcePinDto(
    string WorkId, string AssignmentId, string BindingId, string? WorkReportPeriodId,
    string? PeriodInstanceKey, string ReportId, int VersionNo, int PayloadRevision,
    int LifecycleRevision, string PayloadHash, string SchemaHash, string Status,
    bool IsCurrent, string RelationshipRevision, string AuthorizationRevision);

public sealed record AggregateCoverageSlotDto(
    string SlotKey, string BindingId, string ScheduleRevision, string OccurrenceKey,
    string? WorkReportPeriodId, string? ReportId, string State, string ReasonCode);

public sealed record AggregateResolvedWindowDto(
    string TimeRuleId, string StartDate, string EndDate, string SourceDateBasis,
    string Match, string TimeZone, string AuthorityRevision);

public sealed record AggregateIssueDto(string Code, string Path, string Reason);
public sealed record AggregateCapabilityDto(bool Allowed, string? ReasonCode);
public sealed record AggregateCapabilitiesDto(
    AggregateCapabilityDto ReadConfig, AggregateCapabilityDto EditConfig,
    AggregateCapabilityDto EditMapping, AggregateCapabilityDto Preview,
    AggregateCapabilityDto ApplyDraft, AggregateCapabilityDto Submit,
    AggregateCapabilityDto Approve, AggregateCapabilityDto Return,
    AggregateCapabilityDto ReadLineage);

public sealed record AggregatePreviewResponseDto(
    string RequestId, AggregatePeriodContextDto Context, string InstanceId,
    AggregateExpectedRevisionsDto Revisions, string State, string Completeness,
    List<AggregateTargetResultDto> Results, List<AggregateResolvedWindowDto> Windows,
    List<AggregateCoverageSlotDto> Coverage, List<AggregateSourcePinDto> ContributingSources,
    List<AggregateSourcePinDto> LinkedSources, List<AggregateIssueDto> Issues,
    List<AggregateChangeDto> Changes, AggregateLockImpactDto LockImpact,
    AggregateCapabilitiesDto Capabilities, string? PreviewToken, string? ExpiresAtUtc);
public sealed record AggregateChangeDto(AggregateEndpointDto Target, string Kind, string? PreviousResultRef, string? NextResultRef);
public sealed record AggregateLockImpactDto(List<string> LinkedReportIds, List<string> MissingSlotKeys, string State);
public sealed record AggregateDataWindowDeclarationDto(
    string StartDate, string EndDate, string ProvenanceKind, string ProvenanceRef, long Revision);
public sealed record AggregateDataWindowChangeRequestDto(
    string CommandId, AggregateExpectedRevisionsDto Expected, AggregateDataWindowDeclarationDto Declaration, string PreviewToken);

// Result data is response-only. State distinguishes an actual zero from NO_RESULT/UNAVAILABLE.
public sealed record AggregateTargetResultDto(
    string NodeId, string PortId, string ValueType, string State, JsonElement? Value,
    string? Numerator, string? Denominator, string LineageRef, string? PreviousValueRef);

public sealed record AggregateRawDraftDto(string RawJson, List<AggregateIssueDto> Issues, string? PreviousResultRef);
public sealed record AggregateLayoutDto(int Version, Dictionary<string, AggregatePositionDto> Nodes, double Zoom);
public sealed record AggregatePositionDto(double X, double Y);

// Missing slots are not source reports; business lock owners never expire like a lease.
public sealed record AggregateLockOwnerDto(
    string TargetReportId, int SubmissionLifecycleRevision, string SubmissionId,
    string SourceKind, string SourceIdentity, string InstanceId);

public sealed record AggregateResolveContextRequestDto(
    string WorkId, string AssignmentId, string BindingId, string? WorkReportPeriodId, string? ReportId);
public sealed record AggregatePreviewRequestDto(
    AggregatePeriodContextDto Context, string InstanceId, string ConfigId,
    AggregateExpectedRevisionsDto Expected, AggregateRecipeDto Recipe, AggregateInstanceSelectionDto Selection);
public sealed record AggregateConfigImpactRequestDto(
    long ExpectedHeadRevision, AggregateRecipeDto Recipe, List<AggregateInstanceRevisionPinDto> MigrateInstances,
    List<AggregateMigrationChoiceDto> Resolutions);
public sealed record AggregateMigrationChoiceDto(
    string InstanceId, string NodeId, string? PortId, string? TimeRuleId, string Resolution);
public sealed record AggregateMigrationConflictDto(
    string InstanceId, string NodeId, string? PortId, string? TimeRuleId, string Code, List<string> AllowedResolutions);
public sealed record AggregateConfigImpactResponseDto(
    string ConfigId, long ExpectedHeadRevision, List<AggregateInstanceRevisionPinDto> EligibleDrafts,
    List<AggregateMigrationConflictDto> Conflicts, List<AggregateIssueDto> Issues,
    string? PreviewToken, string? ExpiresAtUtc);
public sealed record AggregateSourceSearchResponseDto(
    List<AggregateSourcePinDto> Items, string? NextCursor, string CoverageState, List<AggregateIssueDto> Issues)
{
    public IReadOnlyDictionary<string, AggregateSourceLabelDto>? Labels { get; init; }
}
public sealed record AggregateSourceLabelDto(string UnitId, string UnitName, string? ReportTitle, string? PeriodKey);
public sealed record AggregateOverridePreviewRequestDto(
    AggregateExpectedRevisionsDto Expected, AggregateInstanceOverrideDto? Overrides,
    AggregateInstanceSelectionDto Selection, string ResetMode);
public sealed record AggregateCommandResponseDto(
    string CommandId, string InstanceId, long InstanceRevision, int PayloadRevision,
    int LifecycleRevision, string State, bool Replayed);
