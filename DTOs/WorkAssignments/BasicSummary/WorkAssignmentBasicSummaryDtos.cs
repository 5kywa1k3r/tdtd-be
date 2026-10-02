using System.Text.Json.Serialization;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.WorkAssignments.BasicSummary;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummaryConfigPayload(
    WorkAssignmentBasicSummarySourceScopePayload? SourceScope,
    WorkAssignmentBasicSummaryPeriodRulePayload? PeriodRule,
    IReadOnlyList<string>? GroupingHints,
    WorkAssignmentBasicSummaryDetailHintsPayload? DetailHints,
    IReadOnlyList<WorkAssignmentBasicSummaryTargetPayload>? Targets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WorkAssignmentBasicSummaryNativeTargetPayload>? NativeTargets = null);

// Exact references into the locked Form plan; options/types are never copied
// or defaulted by Basic. Omission preserves canonical hashes of legacy configs.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummaryNativeTargetPayload(
    string? TableId, string? TargetId, string? OperationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummarySourceScopePayload(
    string? Mode,
    string? FlowInstanceId,
    string? FlowStepId,
    string? FlowBranchId,
    string? FlowEffectiveStatus);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummaryPeriodRulePayload(
    string? Mode,
    string? PeriodKey,
    string? PeriodKeyFrom,
    string? PeriodKeyTo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummaryDetailHintsPayload(
    bool? IncludeSourceRows,
    int? MaxTextChars);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummaryTargetPayload(
    string? ConceptKind,
    string? ConceptKey,
    string? DataType,
    string? Operation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentBasicSummaryEmptyCommandPayload();

public sealed record WorkAssignmentBasicSummaryConfigReadback(
    StatConfigIdentity Identity,
    WorkAssignmentBasicSummaryConfigPayload Payload,
    StatConfigPermissionSet Permissions,
    string RuntimeEligibility,
    bool IsVirtualEmpty,
    string? PreviousVersionId,
    WorkAssignmentBasicSummaryMeanContract MeanContract,
    IReadOnlyList<WorkAssignmentBasicSummaryConfigVersionDto> Versions,
    string? ReceiptId);

public sealed record WorkAssignmentBasicSummaryConfigVersionDto(
    string VersionId,
    string? PreviousVersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    WorkAssignmentBasicSummaryConfigPayload Payload,
    DateTime CreatedAtUtc,
    DateTime? LockedAtUtc);

public sealed record WorkAssignmentBasicSummaryConfigVersionsResult(
    IReadOnlyList<WorkAssignmentBasicSummaryConfigVersionDto> Items);

public sealed record WorkAssignmentBasicSummaryMeanContract(
    string Formula,
    bool MetadataOnly);

public static class WorkAssignmentBasicSummaryConfigContract
{
    public const string RuntimeEligibilityBlockedUntilP9 =
        "BLOCKED_UNTIL_P9";
    public const string MeanFormula = "sum/numericValueCount";
    public const int MinimumMaxTextChars = 1000;
    public const int MaximumMaxTextChars = 100000;

    public const string DirectChildrenOrSelf =
        "DIRECT_CHILDREN_OR_SELF";
    public const string DirectChildren = "DIRECT_CHILDREN";
    public const string Self = "SELF";
    public const string FlowBranch = "FLOW_BRANCH";
    public const string FlowStep = "FLOW_STEP";
    public const string FlowEffectivePath = "FLOW_EFFECTIVE_PATH";
    public const string FlowFinal = "FLOW_FINAL";

    public const string Effective = "EFFECTIVE";
    public const string Invalidated = "INVALIDATED";
    public const string Terminated = "TERMINATED";
    public const string Any = "ANY";

    public const string AllPeriods = "ALL_PERIODS";
    public const string SinglePeriod = "SINGLE_PERIOD";
    public const string PeriodRange = "PERIOD_RANGE";

    public const string Unit = "UNIT";
    public const string Assignment = "ASSIGNMENT";
    public const string Period = "PERIOD";

    public const string Field = "FIELD";
    public const string TableMetric = "TABLE_METRIC";
    public const string RowLabel = "ROW_LABEL";

    public const string Number = "NUMBER";
    public const string Date = "DATE";
    public const string Boolean = "BOOLEAN";
    public const string Choice = "CHOICE";
    public const string Text = "TEXT";

    public const string Sum = "SUM";
    public const string Minimum = "MIN";
    public const string Maximum = "MAX";
    public const string Mean = "MEAN";
    public const string Count = "COUNT";
    public const string MinimumDate = "MIN_DATE";
    public const string MaximumDate = "MAX_DATE";
    public const string TrueCount = "TRUE_COUNT";
    public const string FalseCount = "FALSE_COUNT";
    public const string BucketCount = "BUCKET_COUNT";
    public const string Join = "JOIN";
}

public sealed class WorkAssignmentBasicSummaryRequest
{
    public string ScopeAssignmentId { get; set; } = default!;
    public string? DynamicFormTemplateId { get; set; }
    public List<string>? SelectedUnitIds { get; set; }
    public string? PeriodScopeMode { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }
    public string? SourceScopeMode { get; set; }
    public string? SourceFlowInstanceId { get; set; }
    public string? SourceFlowStepId { get; set; }
    public string? SourceFlowBranchId { get; set; }
    public string? SourceFlowEffectiveStatus { get; set; }
    public WorkAssignmentBasicSummaryDefaultMethodsDto? DefaultMethods { get; set; }
    public List<WorkAssignmentBasicSummaryRuleDto>? Rules { get; set; }
    public WorkAssignmentBasicSummarySourceViewRequestDto? SourceView { get; set; }
    public bool ForceRefresh { get; set; }
    public bool IncludeSourceRows { get; set; } = true;
    public int MaxTextChars { get; set; } = 12000;
}

public sealed class WorkAssignmentBasicSummaryDefaultMethodsDto
{
    public string? Number { get; set; }
    public string? Date { get; set; }
    public string? Boolean { get; set; }
    public string? Text { get; set; }
    public string? Selection { get; set; }
}

public sealed class WorkAssignmentBasicSummaryRuleDto
{
    public string TargetKind { get; set; } = "FIELD";
    public string TargetKey { get; set; } = default!;
    public string Operation { get; set; } = default!;
}

public sealed class WorkAssignmentBasicSummarySourceViewRequestDto
{
    public string? Q { get; set; }
    public string? PeriodKey { get; set; }
    public string? UnitId { get; set; }
    public string? AssigneeUserId { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class WorkAssignmentBasicSummaryResponse
{
    public WorkAssignmentBasicSummaryMetaDto Meta { get; set; } = new();
    public List<WorkAssignmentBasicSummaryItemDto> Fields { get; set; } = new();
    public List<WorkAssignmentBasicSummaryItemDto> Tables { get; set; } = new();
    public List<WorkAssignmentBasicSummarySourceDto> Sources { get; set; } = new();
    public WorkAssignmentBasicSummarySourcePageDto SourcesPage { get; set; } = new();
    public WorkAssignmentBasicSummaryValuesDto SummaryValues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class WorkAssignmentBasicSummaryConfigDto
{
    public string? Id { get; set; }
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public WorkAssignmentBasicSummaryDefaultMethodsDto DefaultMethods { get; set; } = new();
    public List<WorkAssignmentBasicSummaryRuleDto> Rules { get; set; } = new();
    public int VersionNo { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public sealed class SaveWorkAssignmentBasicSummaryConfigRequest
{
    public WorkAssignmentBasicSummaryDefaultMethodsDto? DefaultMethods { get; set; }
    public List<WorkAssignmentBasicSummaryRuleDto>? Rules { get; set; }
}

public sealed class WorkAssignmentBasicSummaryJobResetResultDto
{
    public string SnapshotId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string Status { get; set; } = "QUEUED";
    public DateTime QueuedAtUtc { get; set; }
}

public sealed class WorkAssignmentBasicSummaryMetaDto
{
    public string SummaryType { get; set; } = "BASIC";
    public string ContractVersion { get; set; } = "basic-fixed-scope-v2";
    public string SnapshotPayloadKind { get; set; } = "COMPACT_VALUES1D_OPTIMIZED";
    public string SnapshotId { get; set; } = string.Empty;
    public string ScopeAssignmentId { get; set; } = default!;
    public string ScopeMode { get; set; } = "DIRECT_CHILDREN_OR_SELF";
    public string AssignmentType { get; set; } = "ONCE";
    public string DynamicFormTemplateId { get; set; } = default!;
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public List<string> SelectedUnitIds { get; set; } = new();
    public string PeriodScopeMode { get; set; } = "ALL_PERIODS";
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }
    public string SourceScopeMode { get; set; } = "DIRECT_CHILDREN_OR_SELF";
    public string? SourceFlowInstanceId { get; set; }
    public string? SourceFlowStepId { get; set; }
    public string? SourceFlowBranchId { get; set; }
    public string? SourceFlowEffectiveStatus { get; set; }
    public int SourceAssignmentCount { get; set; }
    public int SourceReportCount { get; set; }
    public bool FromSnapshot { get; set; }
    public bool SnapshotDirty { get; set; }
    public DateTime? SnapshotDirtyAtUtc { get; set; }
    public DateTime? SnapshotRefreshedAtUtc { get; set; }
    public string? SourceSignatureHash { get; set; }
    public bool IsCalculating { get; set; }
    public string? CalculationStatus { get; set; }
    public string? CalculationJobId { get; set; }
    public string? CalculationCorrelationId { get; set; }
    public DateTime? CalculationQueuedAtUtc { get; set; }
    public DateTime? CalculationStartedAtUtc { get; set; }
    public DateTime? CalculationFinishedAtUtc { get; set; }
    public string? CalculationError { get; set; }
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigVersionId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public long ConfigRevision { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public List<string> ConfigDependencyPins { get; set; } = new();
    public string CandidateChainId { get; set; } = string.Empty;
    public string CandidatePromptId { get; set; } = string.Empty;
    public int CandidateStage { get; set; }
    public string CandidateCatalogRawSha256 { get; set; } = string.Empty;
    public string CandidateCatalogSemanticSha256 { get; set; } = string.Empty;
    public string CandidateStageLockSha256 { get; set; } = string.Empty;
}

public sealed class WorkAssignmentBasicSummaryItemDto
{
    public string TargetKind { get; set; } = "FIELD";
    public string TargetKey { get; set; } = default!;
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? BlockId { get; set; }
    public string? TableMode { get; set; }
    public string? MetricKey { get; set; }
    public string? RowKey { get; set; }
    public string? ColumnKey { get; set; }
    public int? Index { get; set; }
    public string Label { get; set; } = default!;
    public string DataType { get; set; } = default!;
    public string Operation { get; set; } = default!;
    public object? Value { get; set; }
    public int ValueCount { get; set; }
    public int ReportCount { get; set; }
    public decimal? Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Mean { get; set; }
    public int? TrueCount { get; set; }
    public int? FalseCount { get; set; }
    public DateTime? MinDateUtc { get; set; }
    public DateTime? MaxDateUtc { get; set; }
    public string? Text { get; set; }
    public int? TextCharCount { get; set; }
    public bool TextTruncated { get; set; }
    public List<WorkAssignmentBasicSummaryBucketDto> Buckets { get; set; } = new();
}

public sealed class WorkAssignmentBasicSummaryBucketDto
{
    public string Key { get; set; } = default!;
    public string Label { get; set; } = default!;
    public int Count { get; set; }
}

public sealed class WorkAssignmentBasicSummarySourceDto
{
    public string WorkAssignmentId { get; set; } = default!;
    public string WorkAssignmentReportId { get; set; } = default!;
    public string WorkReportPeriodId { get; set; } = default!;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? UnitId { get; set; }
    public string? UnitSymbol { get; set; }
    public string? UnitShortName { get; set; }
    public string? UnitName { get; set; }
    public string PeriodKey { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string PeriodKind { get; set; } = default!;
    public int ReportStatus { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? PayloadUpdatedAtUtc { get; set; }
    public int PayloadRevision { get; set; }
    public string? PayloadHash { get; set; }
}

public sealed class WorkAssignmentBasicSummarySourcePageDto
{
    public List<WorkAssignmentBasicSummarySourceDto> Rows { get; set; } = new();
    public int TotalRows { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class WorkAssignmentBasicSummaryValuesDto
{
    public Dictionary<string, WorkAssignmentBasicSummaryValueDto> Fields { get; set; } = new(StringComparer.Ordinal);
    public List<WorkAssignmentBasicSummaryTableValuesDto> Tables { get; set; } = new();
}

public sealed class WorkAssignmentBasicSummaryValueDto
{
    public object? Value { get; set; }
    public string? DisplayValue { get; set; }
    public string DataType { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
}

public sealed class WorkAssignmentBasicSummaryTableValuesDto
{
    public string BlockId { get; set; } = default!;
    public string TableMode { get; set; } = "FIXED_GRID";
    public List<object?> Values1D { get; set; } = new();
    public List<WorkAssignmentBasicSummaryTableCellValueDto> Cells { get; set; } = new();
}

public sealed class WorkAssignmentBasicSummaryTableCellValueDto
{
    public string MetricKey { get; set; } = default!;
    public string? RowKey { get; set; }
    public string? ColumnKey { get; set; }
    public int? Index { get; set; }
    public object? Value { get; set; }
    public string? DisplayValue { get; set; }
    public string DataType { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
}
