using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

public static class P9StatisticDiffResultStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

public static class P9StatisticDiffValueStates
{
    public const string Missing = "MISSING";
    public const string Null = "NULL";
    public const string Empty = "EMPTY";
    public const string Redacted = "REDACTED";
    public const string Value = "VALUE";
}

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_diff_results")]
public sealed class WorkReportStatisticDiffResult : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("runId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RunId { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("assignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("configId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ConfigId { get; set; } = default!;

    [BsonElement("configVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ConfigVersionId { get; set; } = default!;

    [BsonElement("configVersionNo")]
    public int ConfigVersionNo { get; set; }

    [BsonElement("configRevision")]
    public long ConfigRevision { get; set; }

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("dependencyPins")]
    public List<string> DependencyPins { get; set; } = new();

    [BsonElement("candidateChainId")]
    public string CandidateChainId { get; set; } = default!;

    [BsonElement("candidatePromptId")]
    public string CandidatePromptId { get; set; } = default!;

    [BsonElement("candidateStage")]
    public int CandidateStage { get; set; }

    [BsonElement("candidateCatalogRawSha256")]
    public string CandidateCatalogRawSha256 { get; set; } = default!;

    [BsonElement("candidateCatalogSemanticSha256")]
    public string CandidateCatalogSemanticSha256 { get; set; } = default!;

    [BsonElement("candidateStageLockSha256")]
    public string CandidateStageLockSha256 { get; set; } = default!;

    [BsonElement("leftConceptKind")]
    public string LeftConceptKind { get; set; } = default!;

    [BsonElement("leftConceptKey")]
    public string LeftConceptKey { get; set; } = default!;

    [BsonElement("leftConceptCode")]
    public string LeftConceptCode { get; set; } = default!;

    [BsonElement("leftDataType")]
    public string LeftDataType { get; set; } = default!;

    [BsonElement("leftPeriodJson")]
    public string LeftPeriodJson { get; set; } = default!;

    [BsonElement("rightConceptKind")]
    public string RightConceptKind { get; set; } = default!;

    [BsonElement("rightConceptKey")]
    public string RightConceptKey { get; set; } = default!;

    [BsonElement("rightConceptCode")]
    public string RightConceptCode { get; set; } = default!;

    [BsonElement("rightDataType")]
    public string RightDataType { get; set; } = default!;

    [BsonElement("rightPeriodJson")]
    public string RightPeriodJson { get; set; } = default!;

    [BsonElement("direction")]
    public string Direction { get; set; } = default!;

    [BsonElement("missingPolicy")]
    public string MissingPolicy { get; set; } = default!;

    [BsonElement("emptyPolicy")]
    public string EmptyPolicy { get; set; } = default!;

    [BsonElement("timeAxis")]
    public string TimeAxis { get; set; } = "UTC_GREGORIAN";

    [BsonElement("commandId")]
    public string CommandId { get; set; } = default!;

    [BsonElement("requestHash")]
    public string RequestHash { get; set; } = default!;

    [BsonElement("receiptId")]
    public string ReceiptId { get; set; } = default!;

    [BsonElement("requestedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RequestedByUserId { get; set; } = default!;

    [BsonElement("status")]
    public string Status { get; set; } = P9StatisticDiffResultStatuses.Pending;

    [BsonElement("jobId")]
    public string JobId { get; set; } = default!;

    [BsonElement("attemptNo")]
    public int AttemptNo { get; set; }

    [BsonElement("leaseOwner")]
    public string? LeaseOwner { get; set; }

    [BsonElement("leaseExpiresAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseExpiresAtUtc { get; set; }

    [BsonElement("fenceToken")]
    public long FenceToken { get; set; }

    [BsonElement("sourcePins")]
    public List<P9StatisticDiffSourcePin> SourcePins { get; set; } = new();

    [BsonElement("rows")]
    public List<P9StatisticDiffResultRow> Rows { get; set; } = new();

    [BsonElement("totalRowCount")]
    public int TotalRowCount { get; set; }

    [BsonElement("equalRowCount")]
    public int EqualRowCount { get; set; }

    [BsonElement("changedRowCount")]
    public int ChangedRowCount { get; set; }

    [BsonElement("resultHash")]
    public string? ResultHash { get; set; }

    [BsonElement("isCurrent")]
    public bool IsCurrent { get; set; }

    [BsonElement("isFresh")]
    public bool IsFresh { get; set; }

    [BsonElement("isDirty")]
    public bool IsDirty { get; set; }

    [BsonElement("failureCode")]
    public string? FailureCode { get; set; }

    [BsonElement("failureMessage")]
    public string? FailureMessage { get; set; }

    [BsonElement("completedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? CompletedAtUtc { get; set; }

    [BsonElement("expiresAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ExpiresAtUtc { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class P9StatisticDiffSourcePin
{
    [BsonElement("side")]
    public string Side { get; set; } = default!;

    [BsonElement("sourceReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string SourcePayloadHash { get; set; } = default!;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("directRunId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DirectRunId { get; set; } = default!;

    [BsonElement("directGenerationId")]
    public string DirectGenerationId { get; set; } = default!;
}

[BsonIgnoreExtraElements]
public sealed class P9StatisticDiffResultRow
{
    [BsonElement("rowId")]
    public string RowId { get; set; } = default!;

    [BsonElement("ordinal")]
    public int Ordinal { get; set; }

    [BsonElement("key")]
    public string Key { get; set; } = default!;

    [BsonElement("conceptKind")]
    public string ConceptKind { get; set; } = default!;

    [BsonElement("conceptKey")]
    public string ConceptKey { get; set; } = default!;

    [BsonElement("left")]
    public P9StatisticDiffTypedValue Left { get; set; } = new();

    [BsonElement("right")]
    public P9StatisticDiffTypedValue Right { get; set; } = new();

    [BsonElement("equal")]
    public bool Equal { get; set; }

    [BsonElement("differenceKind")]
    public string DifferenceKind { get; set; } = default!;

    [BsonElement("numericDelta")]
    [BsonIgnoreIfNull]
    public decimal? NumericDelta { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class P9StatisticDiffTypedValue
{
    [BsonElement("state")]
    public string State { get; set; } = P9StatisticDiffValueStates.Missing;

    [BsonElement("dataType")]
    public string DataType { get; set; } = default!;

    [BsonElement("canonicalValue")]
    public string? CanonicalValue { get; set; }

    [BsonElement("numericValue")]
    [BsonIgnoreIfNull]
    public decimal? NumericValue { get; set; }

    [BsonElement("booleanValue")]
    [BsonIgnoreIfNull]
    public bool? BooleanValue { get; set; }

    [BsonElement("dateValueUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? DateValueUtc { get; set; }

    [BsonElement("choiceIds")]
    public List<string> ChoiceIds { get; set; } = new();
}
