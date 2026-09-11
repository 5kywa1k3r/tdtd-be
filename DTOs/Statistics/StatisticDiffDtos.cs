using System.Text.Json.Serialization;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.Statistics;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkReportStatisticDiffConfigPayload(
    string? Name,
    WorkReportStatisticDiffSidePayload? Left,
    WorkReportStatisticDiffSidePayload? Right,
    string? Direction,
    string? MissingPolicy,
    string? EmptyPolicy);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkReportStatisticDiffSidePayload(
    WorkReportStatisticDiffSelectorPayload? Selector,
    WorkReportStatisticDiffPeriodPayload? Period,
    WorkReportStatisticDiffSourceScopePayload? SourceScope);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkReportStatisticDiffSelectorPayload(
    string? ConceptKind,
    string? ConceptKey,
    string? ConceptCode,
    string? DataType);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkReportStatisticDiffPeriodPayload(
    string? Mode,
    string? PeriodKey,
    string? PeriodKeyFrom,
    string? PeriodKeyTo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkReportStatisticDiffSourceScopePayload(
    string? Mode,
    string? FlowInstanceId,
    string? FlowStepId,
    string? FlowBranchId,
    string? FlowEffectiveStatus);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkReportStatisticDiffEmptyCommandPayload();

public sealed record WorkReportStatisticDiffConfigReadback(
    StatConfigIdentity Identity,
    WorkReportStatisticDiffConfigPayload Payload,
    StatConfigPermissionSet Permissions,
    string RuntimeEligibility,
    bool IsVirtualEmpty,
    string? PreviousVersionId,
    IReadOnlyList<WorkReportStatisticDiffConfigVersionDto> Versions,
    string? ReceiptId);

public sealed record WorkReportStatisticDiffConfigVersionDto(
    StatConfigIdentity Identity,
    WorkReportStatisticDiffConfigPayload Payload,
    string? PreviousVersionId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LockedAtUtc,
    string? LockedByUserId);

public sealed record WorkReportStatisticDiffConfigVersionsResult(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    IReadOnlyList<WorkReportStatisticDiffConfigVersionDto> Items);

public static class WorkReportStatisticDiffConfigContract
{
    public const string RuntimeBlockedUntilP9 = "BLOCKED_UNTIL_P9";

    public const string Field = "FIELD";
    public const string TableMetric = "TABLE_METRIC";
    public const string RowLabel = "ROW_LABEL";

    public const string Number = "NUMBER";
    public const string Date = "DATE";
    public const string Boolean = "BOOLEAN";
    public const string Choice = "CHOICE";
    public const string Text = "TEXT";

    public const string Exact = "EXACT";
    public const string Range = "RANGE";

    public const string LeftToRight = "LEFT_TO_RIGHT";
    public const string RightToLeft = "RIGHT_TO_LEFT";

    public const string Reject = "REJECT";
    public const string Include = "INCLUDE";
    public const string AsZero = "AS_ZERO";
    public const string AsMissing = "AS_MISSING";

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

    public static readonly IReadOnlyList<string> ConceptKinds =
        new[] { Field, TableMetric, RowLabel };

    public static readonly IReadOnlyList<string> DataTypes =
        new[] { Number, Date, Boolean, Choice, Text };

    public static readonly IReadOnlyList<string> SourceScopeModes =
        new[]
        {
            DirectChildrenOrSelf,
            DirectChildren,
            Self,
            FlowBranch,
            FlowStep,
            FlowEffectivePath,
            FlowFinal
        };
}

public sealed class WorkReportStatisticDiffTargetDto
{
    public string SourceKind { get; set; } = "FIELD";
    public string? DynamicFormTemplateId { get; set; }
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? StatisticLabelCode { get; set; }
    public string? BlockId { get; set; }
    public string? MetricKey { get; set; }
    public string? MetricLabelCode { get; set; }
    public string? RowKey { get; set; }
    public string? ColumnKey { get; set; }
    public string? ConceptCode { get; set; }
    public string? BucketKey { get; set; }
    public string? SourceScopeMode { get; set; }
    public string? SourceFlowInstanceId { get; set; }
    public string? SourceFlowStepId { get; set; }
    public string? SourceFlowBranchId { get; set; }
    public string? SourceFlowEffectiveStatus { get; set; }
}

public sealed class WorkReportStatisticDiffRunRequest
{
    public string? ConfigId { get; set; }
    public string? WorkId { get; set; }
    public string? AssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public WorkReportStatisticDiffTargetDto? Current { get; set; }
    public WorkReportStatisticDiffTargetDto? Comparison { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodCompareMode { get; set; }
    public string? Operator { get; set; }
    public string? JoinKey { get; set; }
    public bool? RequireSameConcept { get; set; }
    public List<string>? SelectedUnitIds { get; set; }
    public string? AssigneeUserId { get; set; }
    public int? ReportStatus { get; set; }
    public int? Limit { get; set; }
}

public sealed class WorkReportStatisticDiffSaveRequest
{
    public string? Id { get; set; }
    public string? WorkId { get; set; }
    public string? AssignmentId { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? Name { get; set; }
    public WorkReportStatisticDiffTargetDto? Current { get; set; }
    public WorkReportStatisticDiffTargetDto? Comparison { get; set; }
    public string? PeriodCompareMode { get; set; }
    public string? Operator { get; set; }
    public string? JoinKey { get; set; }
    public bool? RequireSameConcept { get; set; }
    public string? ConfigJson { get; set; }
}

public sealed class WorkReportStatisticDiffConfigDto
{
    public string Id { get; set; } = default!;
    public string WorkId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string? DynamicFormTemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkReportStatisticDiffTargetDto Current { get; set; } = new();
    public WorkReportStatisticDiffTargetDto Comparison { get; set; } = new();
    public string PeriodCompareMode { get; set; } = "SAME_PERIOD";
    public string Operator { get; set; } = "CHANGED";
    public string JoinKey { get; set; } = "PERIOD";
    public bool RequireSameConcept { get; set; } = true;
    public string ConfigJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkReportStatisticDiffRunResponse
{
    public string WorkId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string? ConfigId { get; set; }
    public string CurrentPeriodKey { get; set; } = default!;
    public string ComparisonPeriodKey { get; set; } = default!;
    public string PeriodCompareMode { get; set; } = "SAME_PERIOD";
    public string Operator { get; set; } = "CHANGED";
    public string JoinKey { get; set; } = "PERIOD";
    public bool RequireSameConcept { get; set; } = true;
    public int CurrentSourceAssignmentCount { get; set; }
    public int ComparisonSourceAssignmentCount { get; set; }
    public long CurrentProjectionCount { get; set; }
    public long ComparisonProjectionCount { get; set; }
    public int ComparedRowCount { get; set; }
    public int MatchedOperatorCount { get; set; }
    public bool Truncated { get; set; }
    public List<WorkReportStatisticDiffRowDto> Rows { get; set; } = new();
}

public sealed class WorkReportStatisticDiffRowDto
{
    public string Key { get; set; } = default!;
    public string CurrentPeriodKey { get; set; } = default!;
    public string ComparisonPeriodKey { get; set; } = default!;
    public string? RowKey { get; set; }
    public string? ConceptCode { get; set; }
    public string? DataCategory { get; set; }
    public WorkReportStatisticDiffValueDto? Current { get; set; }
    public WorkReportStatisticDiffValueDto? Comparison { get; set; }
    public decimal? Delta { get; set; }
    public bool Changed { get; set; }
    public bool MatchesOperator { get; set; }
    public string? MissingSide { get; set; }
}

public sealed class WorkReportStatisticDiffValueDto
{
    public string SourceKind { get; set; } = "FIELD";
    public string? Label { get; set; }
    public string? ConceptCode { get; set; }
    public string? DataCategory { get; set; }
    public string? ValueSignature { get; set; }
    public decimal? NumericValue { get; set; }
    public long ValueCount { get; set; }
    public long ReportCount { get; set; }
    public string? BucketKey { get; set; }
    public string? BucketLabel { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record P9StatisticDiffRunRequest(
    string? ConfigVersionId,
    string? CommandId,
    long? ExpectedConfigRevision,
    string? ExpectedConfigHash,
    int? Limit);

public sealed record P9StatisticDiffResultResponse(
    string ResultId,
    string RunId,
    string Status,
    string AssignmentId,
    string DynamicFormTemplateId,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigHash,
    string CommandId,
    string RequestHash,
    string ReceiptId,
    string TimeAxis,
    int TotalRowCount,
    int EqualRowCount,
    int ChangedRowCount,
    int Page,
    int PageSize,
    int TotalPages,
    string? ResultHash,
    bool IsCurrent,
    bool IsFresh,
    bool IsDirty,
    DateTime? CompletedAtUtc,
    string? FailureCode,
    IReadOnlyList<P9StatisticDiffRowResponse> Rows);

public sealed record P9StatisticDiffRowResponse(
    string RowId,
    int Ordinal,
    string Key,
    string ConceptKind,
    string ConceptKey,
    P9StatisticDiffTypedValueResponse Left,
    P9StatisticDiffTypedValueResponse Right,
    bool Equal,
    string DifferenceKind,
    decimal? NumericDelta);

public sealed record P9StatisticDiffTypedValueResponse(
    string State,
    string DataType,
    string? CanonicalValue,
    decimal? NumericValue,
    bool? BooleanValue,
    DateTime? DateValueUtc,
    IReadOnlyList<string> ChoiceIds);
