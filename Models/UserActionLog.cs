using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("user_action_logs")]
public sealed class UserActionLog : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("action")]
    public string Action { get; set; } = string.Empty;

    [BsonElement("scope")]
    public string Scope { get; set; } = string.Empty;

    [BsonElement("result")]
    public string Result { get; set; } = UserActionLogResults.Success;

    [BsonElement("occurredAtUtc")]
    public DateTime OccurredAtUtc { get; set; }

    [BsonElement("actorUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ActorUserId { get; set; }

    [BsonElement("actor")]
    public UserActionLogUserSnapshot? Actor { get; set; }

    [BsonElement("targetUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? TargetUserId { get; set; }

    [BsonElement("targetUser")]
    public UserActionLogUserSnapshot? TargetUser { get; set; }

    [BsonElement("fromUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? FromUserId { get; set; }

    [BsonElement("fromUser")]
    public UserActionLogUserSnapshot? FromUser { get; set; }

    [BsonElement("toUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ToUserId { get; set; }

    [BsonElement("toUser")]
    public UserActionLogUserSnapshot? ToUser { get; set; }

    [BsonElement("userIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> UserIds { get; set; } = new();

    [BsonElement("users")]
    public List<UserActionLogUserSnapshot> Users { get; set; } = new();

    [BsonElement("unitIds")]
    [BsonRepresentation(BsonType.ObjectId)]
    public List<string> UnitIds { get; set; } = new();

    [BsonElement("unitScopes")]
    public List<UserActionLogUnitScope> UnitScopes { get; set; } = new();

    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkId { get; set; }

    [BsonElement("workAutoCode")]
    public string? WorkAutoCode { get; set; }

    [BsonElement("workCode")]
    public string? WorkCode { get; set; }

    [BsonElement("workName")]
    public string? WorkName { get; set; }

    [BsonElement("workType")]
    public string? WorkType { get; set; }

    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkAssignmentId { get; set; }

    [BsonElement("workAssignmentCode")]
    public string? WorkAssignmentCode { get; set; }

    [BsonElement("rootAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? RootAssignmentId { get; set; }

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkReportPeriodId { get; set; }

    [BsonElement("periodKey")]
    public string? PeriodKey { get; set; }

    [BsonElement("periodInstanceKey")]
    public string? PeriodInstanceKey { get; set; }

    [BsonElement("periodStatus")]
    public string? PeriodStatus { get; set; }

    [BsonElement("workAssignmentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? WorkAssignmentReportId { get; set; }

    [BsonElement("reportStatus")]
    public string? ReportStatus { get; set; }

    [BsonElement("summary")]
    public string? Summary { get; set; }

    [BsonElement("data")]
    public Dictionary<string, string>? Data { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class UserActionLogUserSnapshot
{
    [BsonElement("userId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("username")]
    public string? Username { get; set; }

    [BsonElement("fullName")]
    public string? FullName { get; set; }

    [BsonElement("unitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? UnitId { get; set; }

    [BsonElement("unitCode")]
    public string? UnitCode { get; set; }

    [BsonElement("unitName")]
    public string? UnitName { get; set; }

    [BsonElement("unitLevel")]
    public int? UnitLevel { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class UserActionLogUnitScope
{
    [BsonElement("unitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string UnitId { get; set; } = string.Empty;

    [BsonElement("unitCode")]
    public string? UnitCode { get; set; }

    [BsonElement("unitName")]
    public string? UnitName { get; set; }

    [BsonElement("unitLevel")]
    public int UnitLevel { get; set; }
}

public static class UserActionLogActions
{
    public const string WorkCreated = "WORK_CREATED";
    public const string AssignmentCreated = "ASSIGNMENT_CREATED";
    public const string AssignmentHandover = "ASSIGNMENT_HANDOVER";
    public const string ReportSubmitted = "REPORT_SUBMITTED";
    public const string ReportApproved = "REPORT_APPROVED";
    public const string ReportReturned = "REPORT_RETURNED";
    public const string ReportDeactivated = "REPORT_DEACTIVATED";
    public const string ReportReactivated = "REPORT_REACTIVATED";
}

public static class UserActionLogResults
{
    public const string Success = "SUCCESS";
}
