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

    [BsonElement("familyRevision")]
    public int FamilyRevision { get; set; } = 1;

    [BsonElement("ownerUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OwnerUserId { get; set; }

    [BsonElement("ownerUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OwnerUnitId { get; set; }

    [BsonElement("originFamilyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OriginFamilyId { get; set; }

    [BsonElement("originVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OriginVersionId { get; set; }

    [BsonElement("rootDynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RootDynamicFormTemplateId { get; set; }

    [BsonIgnore]
    public string? DynamicFormTemplateId
    {
        get => RootDynamicFormTemplateId;
        set => RootDynamicFormTemplateId = value;
    }

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFlowTemplateStatuses.Draft;

    [BsonElement("currentVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CurrentVersionId { get; set; }

    [BsonElement("currentVersionNo")]
    public int? CurrentVersionNo { get; set; }

    [BsonElement("currentVersionHash")]
    public string? CurrentVersionHash { get; set; }

    [BsonElement("hasLockedVersion")]
    public bool HasLockedVersion { get; set; }

    [BsonElement("archivedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ArchivedAtUtc { get; set; }

    [BsonElement("archivedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ArchivedByUserId { get; set; }
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

    [BsonElement("rootDynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RootDynamicFormTemplateId { get; set; }

    [BsonIgnore]
    public string? DynamicFormTemplateId
    {
        get => RootDynamicFormTemplateId;
        set => RootDynamicFormTemplateId = value;
    }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("status")]
    public string Status { get; set; } = DynamicFlowTemplateVersionStatuses.Draft;

    [BsonElement("draftRevision")]
    public int DraftRevision { get; set; } = 1;

    [BsonElement("schemaVersion")]
    public int SchemaVersion { get; set; } = DynamicFlowDefinitionSchema.CurrentVersion;

    [BsonElement("adapterVersion")]
    public int AdapterVersion { get; set; } = DynamicFlowDefinitionSchema.CurrentAdapterVersion;

    [BsonElement("catalogVersion")]
    public string CatalogVersion { get; set; } = string.Empty;

    [BsonElement("catalogSemanticHash")]
    public string CatalogSemanticHash { get; set; } = string.Empty;

    [BsonElement("originFamilyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OriginFamilyId { get; set; }

    [BsonElement("originVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? OriginVersionId { get; set; }

    [BsonElement("payloadJson")]
    public string PayloadJson { get; set; } = "{}";

    [BsonElement("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [BsonElement("contributionPolicy")]
    [BsonIgnoreIfNull]
    public string? ContributionPolicy { get; set; }

    [BsonElement("contributionPolicyHash")]
    [BsonIgnoreIfNull]
    public string? ContributionPolicyHash { get; set; }

    [BsonElement("contributionWarning")]
    [BsonIgnoreIfNull]
    public string? ContributionWarning { get; set; }

    [BsonElement("definitionLockable")]
    public bool DefinitionLockable { get; set; }

    [BsonElement("executionEligibility")]
    public string ExecutionEligibility { get; set; } = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;

    [BsonElement("executionBlockedReason")]
    public string? ExecutionBlockedReason { get; set; } = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;

    [BsonElement("blockedUntilPhase")]
    public string? BlockedUntilPhase { get; set; }

    [BsonElement("migrationState")]
    public string MigrationState { get; set; } = DynamicFlowDefinitionMigrationStates.Canonical;

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

public static class DynamicFlowDefinitionSchema
{
    public const int CurrentVersion = 2;
    public const int CurrentAdapterVersion = 1;
}

public static class DynamicFlowExecutionEligibilities
{
    public const string BlockedUntilTargetPhase = "BLOCKED_UNTIL_TARGET_PHASE";
}

public static class DynamicFlowExecutionBlockedReasons
{
    public const string TargetPhaseNotImplemented = "TARGET_PHASE_NOT_IMPLEMENTED";
}

public static class DynamicFlowDefinitionMigrationStates
{
    public const string Canonical = "CANONICAL";
    public const string RequiresReview = "REQUIRES_REVIEW";
}
