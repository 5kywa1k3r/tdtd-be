using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_templates")]
public sealed class DynamicFlowTemplate : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = string.Empty;

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFlowTemplateStatuses.Draft;

    [BsonElement("currentVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CurrentVersionId { get; set; }

    [BsonElement("currentVersionNo")]
    public int? CurrentVersionNo { get; set; }

    [BsonElement("currentVersionHash")]
    public string? CurrentVersionHash { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_template_versions")]
public sealed class DynamicFlowTemplateVersion : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("templateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string TemplateId { get; set; } = default!;

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFlowTemplateVersionStatuses.Draft;

    [BsonElement("draftRevision")]
    public int DraftRevision { get; set; } = 1;

    [BsonElement("payloadJson")]
    public string PayloadJson { get; set; } = "{}";

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("lockedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LockedAtUtc { get; set; }

    [BsonElement("lockedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LockedByUserId { get; set; }

    [BsonElement("archivedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ArchivedAtUtc { get; set; }

    [BsonElement("archivedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ArchivedByUserId { get; set; }
}

public static class DynamicFlowTemplateStatuses
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Archived = "ARCHIVED";
}

public static class DynamicFlowTemplateVersionStatuses
{
    public const string Draft = "DRAFT";
    public const string Locked = "LOCKED";
    public const string Archived = "ARCHIVED";
}
