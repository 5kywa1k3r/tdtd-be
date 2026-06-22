using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models.Statistics;

[BsonIgnoreExtraElements]
[BsonCollection("work_report_statistic_diff_configs")]
public sealed class WorkReportStatisticDiffConfig : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("assignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("current")]
    public WorkReportStatisticDiffSourceConfig Current { get; set; } = new();

    [BsonElement("comparison")]
    public WorkReportStatisticDiffSourceConfig Comparison { get; set; } = new();

    [BsonElement("periodCompareMode")]
    public string PeriodCompareMode { get; set; } = WorkReportStatisticDiffPeriodCompareModes.SamePeriod;

    [BsonElement("operator")]
    public string Operator { get; set; } = WorkReportStatisticDiffOperators.Changed;

    [BsonElement("joinKey")]
    public string JoinKey { get; set; } = WorkReportStatisticDiffJoinKeys.Period;

    [BsonElement("requireSameConcept")]
    public bool RequireSameConcept { get; set; } = true;

    [BsonElement("configJson")]
    public string ConfigJson { get; set; } = "{}";

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

[BsonIgnoreExtraElements]
public sealed class WorkReportStatisticDiffSourceConfig
{
    [BsonElement("sourceKind")]
    public string SourceKind { get; set; } = WorkReportStatisticDiffSourceKinds.Field;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("fieldId")]
    public string? FieldId { get; set; }

    [BsonElement("fieldKey")]
    public string? FieldKey { get; set; }

    [BsonElement("blockId")]
    public string? BlockId { get; set; }

    [BsonElement("metricKey")]
    public string? MetricKey { get; set; }

    [BsonElement("metricLabelCode")]
    public string? MetricLabelCode { get; set; }

    [BsonElement("rowKey")]
    public string? RowKey { get; set; }

    [BsonElement("columnKey")]
    public string? ColumnKey { get; set; }

    [BsonElement("conceptCode")]
    public string? ConceptCode { get; set; }

    [BsonElement("bucketKey")]
    public string? BucketKey { get; set; }

    [BsonElement("sourceScopeMode")]
    public string SourceScopeMode { get; set; } = "DIRECT_CHILDREN_OR_SELF";

    [BsonElement("sourceFlowInstanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SourceFlowInstanceId { get; set; }

    [BsonElement("sourceFlowStepId")]
    public string? SourceFlowStepId { get; set; }

    [BsonElement("sourceFlowBranchId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SourceFlowBranchId { get; set; }

    [BsonElement("sourceFlowEffectiveStatus")]
    public string? SourceFlowEffectiveStatus { get; set; }
}

public static class WorkReportStatisticDiffSourceKinds
{
    public const string Field = "FIELD";
    public const string Table = "TABLE";
}

public static class WorkReportStatisticDiffPeriodCompareModes
{
    public const string SamePeriod = "SAME_PERIOD";
    public const string PreviousPeriod = "PREVIOUS_PERIOD";
}

public static class WorkReportStatisticDiffOperators
{
    public const string Changed = "CHANGED";
    public const string Delta = "DELTA";
    public const string GreaterThan = "GREATER_THAN";
    public const string LessThan = "LESS_THAN";
    public const string BucketChanged = "BUCKET_CHANGED";
    public const string Missing = "MISSING";
}

public static class WorkReportStatisticDiffJoinKeys
{
    public const string Period = "PERIOD";
    public const string RowKey = "ROW_KEY";
}
