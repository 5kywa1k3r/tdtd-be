using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_basic_summary_configs")]
public sealed class WorkAssignmentBasicSummaryConfig : BaseEntity
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
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("defaultMethodsJson")]
    public string DefaultMethodsJson { get; set; } = "{}";

    [BsonElement("rulesJson")]
    public string RulesJson { get; set; } = "[]";

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}
