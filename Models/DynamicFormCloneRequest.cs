using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

public static class DynamicFormCloneRequestStatus
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_form_clone_requests")]
public sealed class DynamicFormCloneRequest : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

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
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("requesterUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RequesterUserId { get; set; } = default!;

    [BsonElement("assignmentOwnerUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssignmentOwnerUserId { get; set; } = default!;

    [BsonElement("requester")]
    public UserRef? Requester { get; set; }

    [BsonElement("assignmentOwner")]
    public UserRef? AssignmentOwner { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFormCloneRequestStatus.Pending;

    [BsonElement("requestReason")]
    public string? RequestReason { get; set; }

    [BsonElement("reviewComment")]
    public string? ReviewComment { get; set; }

    [BsonElement("reviewedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ReviewedAtUtc { get; set; }

    [BsonElement("reviewedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ReviewedByUserId { get; set; }
}
