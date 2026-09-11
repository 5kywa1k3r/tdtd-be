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

    [BsonElement("versionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? VersionId { get; set; }

    [BsonElement("previousVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? PreviousVersionId { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("status")]
    public string? Status { get; set; }

    [BsonElement("configHash")]
    public string? ConfigHash { get; set; }

    [BsonElement("configJson")]
    public string ConfigJson { get; set; } = "{}";

    [BsonElement("dependencyPins")]
    public List<string> DependencyPins { get; set; } = new();

    [BsonElement("versions")]
    public List<WorkAssignmentBasicSummaryConfigVersion> Versions { get; set; } = new();

    [BsonElement("lockedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LockedAtUtc { get; set; }

    [BsonElement("lockedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LockedByUserId { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

[BsonIgnoreExtraElements]
public sealed class WorkAssignmentBasicSummaryConfigVersion
{
    [BsonElement("versionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string VersionId { get; set; } = default!;

    [BsonElement("previousVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? PreviousVersionId { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = string.Empty;

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = string.Empty;

    [BsonElement("configJson")]
    public string ConfigJson { get; set; } = "{}";

    [BsonElement("dependencyPins")]
    public List<string> DependencyPins { get; set; } = new();

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("createdByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CreatedByUserId { get; set; }

    [BsonElement("lockedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LockedAtUtc { get; set; }

    [BsonElement("lockedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LockedByUserId { get; set; }
}
