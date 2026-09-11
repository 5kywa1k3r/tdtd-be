using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.StatisticsReconciliation;

[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationActualCapturePlan
{
    [BsonElement("schemaVersion")]
    public string SchemaVersion { get; set; } =
        StatisticReconciliationActualCapturePlanVersions.V1;

    [BsonElement("boundaryRegistryVersion")]
    public string BoundaryRegistryVersion { get; set; } = default!;

    [BsonElement("actualConfigurationBundleSha256")]
    public string ActualConfigurationBundleSha256 { get; set; } = default!;

    [BsonElement("p8ConfigurationOwnerId")]
    [BsonIgnoreIfNull]
    public string? P8ConfigurationOwnerId { get; set; }

    [BsonElement("p8ConfigurationBundleSha256")]
    [BsonIgnoreIfNull]
    public string? P8ConfigurationBundleSha256 { get; set; }

    [BsonElement("basic")]
    public StatisticReconciliationActualBasicTargetPlan Basic { get; set; } = new();

    [BsonElement("advanced")]
    public StatisticReconciliationActualAdvancedTargetPlan Advanced { get; set; } = new();

    [BsonElement("diff")]
    public StatisticReconciliationActualDiffTargetPlan Diff { get; set; } = new();

    [BsonElement("api")]
    public StatisticReconciliationActualApiTargetPlan Api { get; set; } = new();

    [BsonElement("export")]
    public StatisticReconciliationActualExportTargetPlan Export { get; set; } = new();

    [BsonElement("planSha256")]
    public string PlanSha256 { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationActualBasicTargetPlan
{
    [BsonElement("disposition")]
    [BsonDefaultValue(StatisticReconciliationActualSummaryTargetDispositions.Configured)]
    [BsonIgnoreIfDefault]
    public string Disposition { get; set; } =
        StatisticReconciliationActualSummaryTargetDispositions.Configured;

    [BsonElement("applicabilityProofSha256")]
    [BsonIgnoreIfNull]
    public string? ApplicabilityProofSha256 { get; set; }

    [BsonElement("snapshotId")]
    public string SnapshotId { get; set; } = default!;

    [BsonElement("mode")]
    public string Mode { get; set; } = default!;

    [BsonElement("immutableSelectorSha256")]
    public string ImmutableSelectorSha256 { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationActualAdvancedTargetPlan
{
    [BsonElement("disposition")]
    [BsonDefaultValue(StatisticReconciliationActualSummaryTargetDispositions.Configured)]
    [BsonIgnoreIfDefault]
    public string Disposition { get; set; } =
        StatisticReconciliationActualSummaryTargetDispositions.Configured;

    [BsonElement("applicabilityProofSha256")]
    [BsonIgnoreIfNull]
    public string? ApplicabilityProofSha256 { get; set; }

    [BsonElement("sectionId")]
    public string SectionId { get; set; } = default!;

    [BsonElement("dayNodeIds")]
    public List<string> DayNodeIds { get; set; } = [];

    [BsonElement("monthNodeIds")]
    public List<string> MonthNodeIds { get; set; } = [];

    [BsonElement("yearNodeIds")]
    public List<string> YearNodeIds { get; set; } = [];

    [BsonElement("immutableSelectorSha256")]
    public string ImmutableSelectorSha256 { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationActualDiffTargetPlan
{
    [BsonElement("disposition")]
    [BsonDefaultValue(StatisticReconciliationActualSummaryTargetDispositions.Configured)]
    [BsonIgnoreIfDefault]
    public string Disposition { get; set; } =
        StatisticReconciliationActualSummaryTargetDispositions.Configured;

    [BsonElement("applicabilityProofSha256")]
    [BsonIgnoreIfNull]
    public string? ApplicabilityProofSha256 { get; set; }

    [BsonElement("resultId")]
    public string ResultId { get; set; } = default!;

    [BsonElement("runId")]
    public string RunId { get; set; } = default!;

    [BsonElement("immutableSelectorSha256")]
    public string ImmutableSelectorSha256 { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationActualApiTargetPlan
{
    [BsonElement("surface")]
    public string Surface { get; set; } = default!;

    [BsonElement("ownerResultId")]
    public string? OwnerResultId { get; set; }

    [BsonElement("expectedTotalRows")]
    public long ExpectedTotalRows { get; set; }

    [BsonElement("pageSize")]
    public int PageSize { get; set; }

    [BsonElement("pageCount")]
    public int PageCount { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class StatisticReconciliationActualExportTargetPlan
{
    [BsonElement("exportId")]
    public string ExportId { get; set; } = default!;

    [BsonElement("resultKind")]
    public string ResultKind { get; set; } = default!;

    [BsonElement("workId")]
    public string WorkId { get; set; } = default!;

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = default!;

    [BsonElement("scopeId")]
    public string ScopeId { get; set; } = default!;

    [BsonElement("resultId")]
    public string ResultId { get; set; } = default!;

    [BsonElement("filterSha256")]
    [BsonIgnoreIfNull]
    public string? FilterSha256 { get; set; }

    [BsonElement("requestSha256")]
    public string RequestSha256 { get; set; } = default!;

    [BsonElement("authorizationSnapshotSha256")]
    public string AuthorizationSnapshotSha256 { get; set; } = default!;

    [BsonElement("contentSha256")]
    public string ContentSha256 { get; set; } = default!;

    [BsonElement("columnManifestSha256")]
    public string ColumnManifestSha256 { get; set; } = default!;

    [BsonElement("ownerSemanticSha256")]
    public string OwnerSemanticSha256 { get; set; } = default!;
}

public static class StatisticReconciliationActualCapturePlanVersions
{
    public const string V1 = "P10_ACTUAL_CAPTURE_PLAN_V1";
    public const string V2 = "P10_ACTUAL_CAPTURE_PLAN_V2";
    public const string V3 = "P10_ACTUAL_CAPTURE_PLAN_V3";
    public const string V4 = "P10_ACTUAL_CAPTURE_PLAN_V4";
}

public static class StatisticReconciliationActualSummaryTargetDispositions
{
    public const string Configured = "CONFIGURED";
    public const string NotApplicable = "NOT_APPLICABLE";
}