using System.Text.Json.Serialization;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentAdvancedSummaryConfigPayload(
    WorkAssignmentAdvancedSummarySourceScopePayload? SourceScope,
    IReadOnlyList<WorkAssignmentAdvancedSummarySectionPayload>? Sections,
    IReadOnlyList<string>? HierarchyGrains,
    IReadOnlyList<string>? Grouping,
    IReadOnlyList<WorkAssignmentAdvancedSummaryOrderingPayload>? Ordering,
    string? Description);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentAdvancedSummarySourceScopePayload(
    string? Mode,
    string? FlowInstanceId,
    string? FlowStepId,
    string? FlowBranchId,
    string? FlowEffectiveStatus);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentAdvancedSummarySectionPayload(
    string? SectionId,
    bool? IsCumulative,
    IReadOnlyList<WorkAssignmentAdvancedSummaryTargetPayload>? Targets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WorkAssignmentAdvancedSummaryNativeTargetPayload>? NativeTargets = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentAdvancedSummaryNativeTargetPayload(
    string? TableId, string? TargetId, string? OperationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentAdvancedSummaryTargetPayload(
    string? FieldId,
    string? DataType,
    string? Operation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkAssignmentAdvancedSummaryOrderingPayload(
    string? FieldId,
    string? Direction);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WorkAssignmentAdvancedSummaryEmptyCommandPayload
{
}

public sealed record WorkAssignmentAdvancedSummaryValidationReceipt(
    string ReceiptId,
    string ContractVersion,
    string ValidationMode,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    string SectionId,
    string SourceScopeMode,
    bool IsCumulative,
    int TargetCount,
    int TargetLimit,
    IReadOnlyList<string> HierarchyGrains,
    int HierarchyDepth,
    int MaxHierarchyDepth,
    int CanonicalPayloadBytes,
    int MaxCanonicalPayloadBytes,
    string RuntimeEligibility,
    bool PreviewRead,
    bool PreviewWrite,
    bool HierarchyRead,
    bool HierarchyWrite);

public sealed record WorkAssignmentAdvancedSummaryConfigVersionDto(
    StatConfigIdentity Identity,
    WorkAssignmentAdvancedSummaryConfigPayload Payload,
    string? PreviousVersionId,
    WorkAssignmentAdvancedSummaryValidationReceipt? ValidationReceipt,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LockedAtUtc,
    string? LockedByUserId,
    DateTime? ArchivedAtUtc,
    string? ArchivedByUserId);

public sealed record WorkAssignmentAdvancedSummaryConfigReadback(
    StatConfigIdentity Identity,
    WorkAssignmentAdvancedSummaryConfigPayload Payload,
    StatConfigPermissionSet Permissions,
    string RuntimeEligibility,
    bool IsVirtualEmpty,
    string? PreviousVersionId,
    IReadOnlyList<WorkAssignmentAdvancedSummaryConfigVersionDto> Versions,
    WorkAssignmentAdvancedSummaryValidationReceipt? ValidationReceipt,
    string? CommandReceiptId);

public sealed record WorkAssignmentAdvancedSummaryConfigVersionsResult(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    IReadOnlyList<WorkAssignmentAdvancedSummaryConfigVersionDto> Versions);

public static class WorkAssignmentAdvancedSummaryConfigContract
{
    public const string RuntimeBlockedUntilP9 = "BLOCKED_UNTIL_P9";
    public const string ValidationContractVersion =
        "P8_ADVANCED_CONFIG_VALIDATION_V1";
    public const string ValidationMode = "CONFIG_ONLY_NO_DATASET";
    public const int MaxCumulativeTargets = 249;
    public const int MaxNonCumulativeTargets = 1000;
    public const int MaxHierarchyDepth = 3;
    public const int MaxCanonicalPayloadBytes = 1_048_576;

    public static readonly IReadOnlyList<string> SourceScopeModes =
        new[]
        {
            "DIRECT_CHILDREN_OR_SELF",
            "DIRECT_CHILDREN",
            "SELF",
            "FLOW_BRANCH",
            "FLOW_STEP",
            "FLOW_EFFECTIVE_PATH",
            "FLOW_FINAL"
        };

    public static readonly IReadOnlyList<string> HierarchyPrefix =
        new[] { "DAY", "MONTH", "YEAR" };
}

public sealed class WorkAssignmentAdvancedSummaryConfigDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string? SectionTitle { get; set; }
    public string SourceScopeMode { get; set; } = "DIRECT_CHILDREN_OR_SELF";
    public string? SourceFlowInstanceId { get; set; }
    public string? SourceFlowStepId { get; set; }
    public string? SourceFlowBranchId { get; set; }
    public string? SourceFlowEffectiveStatus { get; set; }
    public string Status { get; set; } = "DRAFT";
    public int VersionNo { get; set; }
    public int DraftRevision { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string ConfigHash { get; set; } = string.Empty;
    public string PreviewStatus { get; set; } = "NOT_REQUESTED";
    public string? PreviewJobId { get; set; }
    public string? PreviewCorrelationId { get; set; }
    public List<string> PreviewPeriodKeys { get; set; } = new();
    public string? PreviewResultJson { get; set; }
    public string? PreviewError { get; set; }
    public DateTime? PreviewRequestedAtUtc { get; set; }
    public DateTime? PreviewFinishedAtUtc { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public string? LockedByUserId { get; set; }
    public string? LockTokenId { get; set; }
    public bool RequiresPreviewToLock { get; set; }
    public bool RequiresTokenToLock { get; set; }
    public bool CanLock { get; set; }
    public bool CanPreview { get; set; }
    public string FieldGateStatus { get; set; } = "UNKNOWN";
    public string? FieldGateReason { get; set; }
    public bool IsCumulative { get; set; }
    public int SectionFieldCount { get; set; }
    public int TargetFieldCount { get; set; }
    public int FieldLimit { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SaveWorkAssignmentAdvancedSummaryDraftRequest
{
    public string ConfigJson { get; set; } = "{}";
    public string? SourceScopeMode { get; set; }
    public string? SourceFlowInstanceId { get; set; }
    public string? SourceFlowStepId { get; set; }
    public string? SourceFlowBranchId { get; set; }
    public string? SourceFlowEffectiveStatus { get; set; }
}

public sealed class LockWorkAssignmentAdvancedSummaryConfigRequest
{
    public string? TokenId { get; set; }
}

public sealed class PreviewWorkAssignmentAdvancedSummaryConfigRequest
{
    public bool ForceRefresh { get; set; }
}

public sealed class BuildWorkAssignmentAdvancedSummaryDayNodeRequest
{
    public bool ForceRefresh { get; set; }
    public string? CommandId { get; set; }
    public long? ExpectedConfigRevision { get; set; }
    public string? ExpectedConfigHash { get; set; }
}

public sealed class BuildWorkAssignmentAdvancedSummaryMonthNodeRequest
{
    public bool ForceRefresh { get; set; }
    public string? CommandId { get; set; }
    public long? ExpectedConfigRevision { get; set; }
    public string? ExpectedConfigHash { get; set; }
}

public sealed class BuildWorkAssignmentAdvancedSummaryYearNodeRequest
{
    public bool ForceRefresh { get; set; }
    public string? CommandId { get; set; }
    public long? ExpectedConfigRevision { get; set; }
    public string? ExpectedConfigHash { get; set; }
}

public sealed class QueryWorkAssignmentAdvancedSummaryHierarchyRequest
{
    public string StartDayKey { get; set; } = string.Empty;
    public string EndDayKey { get; set; } = string.Empty;
    public bool EnqueueMissing { get; set; } = true;
    public string? CommandId { get; set; }
}

public sealed class DiagnoseWorkAssignmentAdvancedSummaryDayNodeRequest
{
    public string ConfigId { get; set; } = string.Empty;
    public string DayKey { get; set; } = string.Empty;
    public bool IncludeValueJson { get; set; }
}

public sealed class DiagnoseWorkAssignmentAdvancedSummaryMonthNodeRequest
{
    public string ConfigId { get; set; } = string.Empty;
    public string MonthKey { get; set; } = string.Empty;
    public bool IncludeValueJson { get; set; }
}

public sealed class DiagnoseWorkAssignmentAdvancedSummaryYearNodeRequest
{
    public string ConfigId { get; set; } = string.Empty;
    public string YearKey { get; set; } = string.Empty;
    public bool IncludeValueJson { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryDayNodeDiagnosticsResponse
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string DayKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Matches { get; set; }
    public string DiagnosticActorUserId { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
    public List<string> Differences { get; set; } = new();
    public WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot? Cache { get; set; }
    public WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot Direct { get; set; } = new();
}

public sealed class WorkAssignmentAdvancedSummaryMonthNodeDiagnosticsResponse
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string MonthKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Matches { get; set; }
    public string DiagnosticActorUserId { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
    public List<string> Differences { get; set; } = new();
    public WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot? Cache { get; set; }
    public WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot Direct { get; set; } = new();
}

public sealed class WorkAssignmentAdvancedSummaryYearNodeDiagnosticsResponse
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string YearKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Matches { get; set; }
    public string DiagnosticActorUserId { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
    public List<string> Differences { get; set; } = new();
    public WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot? Cache { get; set; }
    public WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot Direct { get; set; } = new();
}

public sealed class WorkAssignmentAdvancedSummaryNodeDiagnosticSnapshot
{
    public string? NodeId { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public long SourceReportCount { get; set; }
    public List<string> SourceReportIds { get; set; } = new();
    public List<string> InputNodeKeys { get; set; } = new();
    public string? SourceSignatureHash { get; set; }
    public string? ValueHash { get; set; }
    public string? ComparableValueHash { get; set; }
    public string? ComparableValueError { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public string? ValueJson { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryHierarchyQueryResponse
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string StartDayKey { get; set; } = string.Empty;
    public string EndDayKey { get; set; } = string.Empty;
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public string Status { get; set; } = "MISSING";
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> SelectedNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> MissingNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> DirtyNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> BuildingNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> EnqueuedNodes { get; set; } = new();
}

public sealed class WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto
{
    public string Grain { get; set; } = string.Empty;
    public string GrainKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public string? ValueHash { get; set; }
    public long SourceReportCount { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryDayNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public string Grain { get; set; } = "DAY";
    public string GrainKey { get; set; } = string.Empty;
    public string DayKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? DirtyReason { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string ValueJson { get; set; } = "{}";
    public string? ValueHash { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryMonthNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public string Grain { get; set; } = "MONTH";
    public string GrainKey { get; set; } = string.Empty;
    public string MonthKey { get; set; } = string.Empty;
    public string YearKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? DirtyReason { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string ValueJson { get; set; } = "{}";
    public string? ValueHash { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryYearNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public string Grain { get; set; } = "YEAR";
    public string GrainKey { get; set; } = string.Empty;
    public string YearKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? DirtyReason { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string ValueJson { get; set; } = "{}";
    public string? ValueHash { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
