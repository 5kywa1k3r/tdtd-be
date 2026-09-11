using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_periodic_schedules")]
public sealed class DynamicFlowPeriodicSchedule : BaseEntity
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("flowTemplateId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowTemplateId { get; set; } = default!;

    [BsonElement("flowTemplateVersionId"), BsonRepresentation(BsonType.ObjectId)]
    public string FlowTemplateVersionId { get; set; } = default!;

    [BsonElement("flowTemplateVersionNo")]
    public int FlowTemplateVersionNo { get; set; }

    [BsonElement("flowPayloadHash")]
    public string FlowPayloadHash { get; set; } = string.Empty;

    [BsonElement("catalogVersion")]
    public string CatalogVersion { get; set; } = string.Empty;

    [BsonElement("catalogSemanticHash")]
    public string CatalogSemanticHash { get; set; } = string.Empty;

    [BsonElement("definitionRevision")]
    public string DefinitionRevision { get; set; } = string.Empty;

    [BsonElement("topologySnapshotJson")]
    public string TopologySnapshotJson { get; set; } = string.Empty;

    [BsonElement("topologySnapshotHash")]
    public string TopologySnapshotHash { get; set; } = string.Empty;

    [BsonElement("scheduleKey")]
    public string ScheduleKey { get; set; } = string.Empty;

    [BsonElement("timeZoneId")]
    public string TimeZoneId { get; set; } = string.Empty;

    [BsonElement("normalizedTimeZoneId")]
    public string NormalizedTimeZoneId { get; set; } = string.Empty;

    [BsonElement("cadence")]
    public string Cadence { get; set; } = DynamicFlowPeriodicCadences.Daily;

    [BsonElement("localTime")]
    public string LocalTime { get; set; } = string.Empty;

    [BsonElement("policyVersion")]
    public string PolicyVersion { get; set; } = string.Empty;

    [BsonElement("targetUnitIds"), BsonRepresentation(BsonType.ObjectId)]
    public List<string> TargetUnitIds { get; set; } = new();

    [BsonElement("issuerUserId"), BsonRepresentation(BsonType.ObjectId)]
    public string IssuerUserId { get; set; } = default!;

    [BsonElement("issuerUnitId"), BsonRepresentation(BsonType.ObjectId)]
    public string IssuerUnitId { get; set; } = default!;

    [BsonElement("scheduleIdentityJson")]
    public string ScheduleIdentityJson { get; set; } = "{}";

    [BsonElement("scheduleIdentityHash")]
    public string ScheduleIdentityHash { get; set; } = string.Empty;

    [BsonElement("nextDueAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime NextDueAtUtc { get; set; }

    [BsonElement("lastObservedPeriodKey")]
    public string? LastObservedPeriodKey { get; set; }

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowPeriodicScheduleStates.Active;

    [BsonElement("leaseId")]
    public string? LeaseId { get; set; }

    [BsonElement("leaseUntilUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_flow_periodic_occurrences")]
public sealed class DynamicFlowPeriodicOccurrence : BaseEntity
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("scheduleId"), BsonRepresentation(BsonType.ObjectId)]
    public string ScheduleId { get; set; } = default!;

    [BsonElement("workId"), BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = string.Empty;

    [BsonElement("timeZoneId")]
    public string TimeZoneId { get; set; } = string.Empty;

    [BsonElement("policyVersion")]
    public string PolicyVersion { get; set; } = string.Empty;

    [BsonElement("scheduleIdentityHash")]
    public string ScheduleIdentityHash { get; set; } = string.Empty;

    [BsonElement("scheduledAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ScheduledAtUtc { get; set; }

    [BsonElement("observedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ObservedAtUtc { get; set; }

    [BsonElement("state")]
    public string State { get; set; } = DynamicFlowPeriodicOccurrenceStates.Pending;

    [BsonElement("reasonCode")]
    public string? ReasonCode { get; set; }

    [BsonElement("flowInstanceId"), BsonRepresentation(BsonType.ObjectId)]
    public string? FlowInstanceId { get; set; }

    [BsonElement("launchCommandId")]
    public string LaunchCommandId { get; set; } = string.Empty;

    [BsonElement("manualCommandIds")]
    public List<string> ManualCommandIds { get; set; } = new();

    [BsonElement("leaseId")]
    public string? LeaseId { get; set; }

    [BsonElement("leaseUntilUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LeaseUntilUtc { get; set; }

    [BsonElement("revision")]
    public long Revision { get; set; }
}

public static class DynamicFlowPeriodicCadences
{
    public const string Daily = "DAILY";
}

public static class DynamicFlowPeriodicScheduleStates
{
    public const string Active = "ACTIVE";
    public const string Paused = "PAUSED";
}

public static class DynamicFlowPeriodicOccurrenceStates
{
    public const string Pending = "PENDING";
    public const string Launching = "LAUNCHING";
    public const string Launched = "LAUNCHED";
    public const string Missed = "MISSED";
    public const string Failed = "FAILED";
}
