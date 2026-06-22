using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_events")]
public sealed class DynamicFlowEvent : BaseEntity
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

    [BsonElement("flowTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowTemplateId { get; set; }

    [BsonElement("flowTemplateVersionNo")]
    public int? FlowTemplateVersionNo { get; set; }

    [BsonElement("flowInstanceId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string FlowInstanceId { get; set; } = default!;

    [BsonElement("flowStepId")]
    public string? FlowStepId { get; set; }

    [BsonElement("flowStepCode")]
    public string? FlowStepCode { get; set; }

    [BsonElement("flowBranchId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FlowBranchId { get; set; }

    [BsonElement("parentFlowBranchId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ParentFlowBranchId { get; set; }

    [BsonElement("action")]
    public string Action { get; set; } = string.Empty;

    [BsonElement("fromStatus")]
    public string? FromStatus { get; set; }

    [BsonElement("toStatus")]
    public string? ToStatus { get; set; }

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActorUserId { get; set; } = default!;

    [BsonElement("actorUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ActorUnitId { get; set; }

    [BsonElement("visibleUnitIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> VisibleUnitIds { get; set; } = new();

    [BsonElement("reason")]
    public string? Reason { get; set; }

    [BsonElement("snapshotJson")]
    public string? SnapshotJson { get; set; }

    [BsonElement("snapshotHash")]
    public string? SnapshotHash { get; set; }

    [BsonElement("affectedAssignmentIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> AffectedAssignmentIds { get; set; } = new();

    [BsonElement("actionAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ActionAtUtc { get; set; } = DateTime.UtcNow;
}

public static class DynamicFlowEventActions
{
    public const string FlowCreated = "FLOW_CREATED";
    public const string StepAssigned = "STEP_ASSIGNED";
    public const string DraftSaved = "DRAFT_SAVED";
    public const string Submitted = "SUBMITTED";
    public const string Accepted = "ACCEPTED";
    public const string Returned = "RETURNED";
    public const string Rollback = "ROLLBACK";
    public const string BranchInvalidated = "BRANCH_INVALIDATED";
    public const string Terminated = "TERMINATED";
    public const string Restarted = "RESTARTED";
    public const string Finalized = "FINALIZED";
}

public static class DynamicFlowEffectiveStatuses
{
    public const string Effective = "EFFECTIVE";
    public const string Invalidated = "INVALIDATED";
    public const string Terminated = "TERMINATED";
}
