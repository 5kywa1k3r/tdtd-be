using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_aggregate_configs")]
public sealed class WorkAssignmentAggregateConfig : BaseEntity
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

    [BsonElement("sourceDynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SourceDynamicFormTemplateId { get; set; }

    [BsonElement("sourceBlockId")]
    public string? SourceBlockId { get; set; }

    [BsonElement("sourceTableMode")]
    public string? SourceTableMode { get; set; }

    [BsonElement("targetDynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? TargetDynamicFormTemplateId { get; set; }

    [BsonElement("targetBlockId")]
    public string? TargetBlockId { get; set; }

    [BsonElement("aggregateKind")]
    public string AggregateKind { get; set; } = "AUTO_MAP";

    [BsonElement("identityColumns")]
    public List<string> IdentityColumns { get; set; } = new();

    [BsonElement("periodAggregationRule")]
    public string PeriodAggregationRule { get; set; } = "STACK_SINGLE_PERIOD_SUM_RANGE";

    [BsonElement("metricMappingsJson")]
    public string? MetricMappingsJson { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}
