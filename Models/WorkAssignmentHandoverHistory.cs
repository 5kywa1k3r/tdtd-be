using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_handover_histories")]
public sealed class WorkAssignmentHandoverHistory : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("operationId")]
    public string OperationId { get; set; } = default!;

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    [BsonElement("assignmentCode")]
    public string AssignmentCode { get; set; } = string.Empty;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("fromAssigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string FromAssigneeUserId { get; set; } = default!;

    [BsonElement("toAssigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ToAssigneeUserId { get; set; } = default!;

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("fromAssignee")]
    public UserRef? FromAssignee { get; set; }

    [BsonElement("toAssignee")]
    public UserRef? ToAssignee { get; set; }

    [BsonElement("actor")]
    public UserRef? Actor { get; set; }

    [BsonElement("reason")]
    public string? Reason { get; set; }

    [BsonElement("comment")]
    public string? Comment { get; set; }

    [BsonElement("workTemplateAssigneeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkTemplateAssigneeId { get; set; }

    [BsonElement("periodCount")]
    public long PeriodCount { get; set; }

    [BsonElement("reportCount")]
    public long ReportCount { get; set; }

    [BsonElement("queueItemCount")]
    public long QueueItemCount { get; set; }

    [BsonElement("result")]
    public string Result { get; set; } = "SUCCESS";
}
