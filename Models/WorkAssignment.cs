using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonCollection("work_assignments")]
public sealed class WorkAssignment : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonElement("workId")]
    public string WorkId { get; set; } = default!;

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonElement("dynamicExcelId")]
    public string? DynamicExcelId { get; set; }

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonElement("dynamicFormTemplateId")]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("dynamicFormDataSourceRulesJson")]
    public string? DynamicFormDataSourceRulesJson { get; set; }

    [BsonElement("workType")]
    public string WorkType { get; set; } = string.Empty;

    [BsonElement("assignmentType")]
    public string AssignmentType { get; set; } = string.Empty;

    [BsonElement("aggregationType")]
    public string AggregationType { get; set; } = string.Empty;

    [BsonElement("schedule")]
    public AssignmentSchedule? Schedule { get; set; }

    [BsonElement("allowUserCreatedReports")]
    public bool AllowUserCreatedReports { get; set; } = true;

    [BsonElement("assignees")]
    public List<UserRef> Assignees { get; set; } = new();

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("parentAssignmentId")]
    public string? ParentAssignmentId { get; set; }

    [BsonElement("rootAssignmentId")]
    public string RootAssignmentId { get; set; } = default!;

    [BsonElement("level")]
    public int Level { get; set; }

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("path")]
    public string Path { get; set; } = default!;

    [BsonElement("leaderWatcherUserIds")]
    public List<string> LeaderWatcherUserIds { get; set; } = new();

    [BsonElement("leaderWatchers")]
    public List<UserRef> LeaderWatchers { get; set; } = new();

    [BsonElement("progressStatus")]
    public int ProgressStatus { get; set; } = 0;

    [BsonElement("progressStatusUpdatedAtUtc")]
    public DateTime? ProgressStatusUpdatedAtUtc { get; set; }

    [BsonElement("latestPeriodKey")]
    public string? LatestPeriodKey { get; set; }

    [BsonElement("latestDueAtUtc")]
    public DateTime? LatestDueAtUtc { get; set; }

    [BsonElement("hasAnyDuePeriod")]
    public bool HasAnyDuePeriod { get; set; }

    [BsonElement("hasOverduePeriod")]
    public bool HasOverduePeriod { get; set; }

    // Phase 5: aggregate histogram của direct children active
    [BsonElement("activeChildCount")]
    public int ActiveChildCount { get; set; }

    [BsonElement("childProgressCounts")]
    public WorkProgressCountSnapshot ChildProgressCounts { get; set; } = new();

    [BsonElement("worstChildProgressStatus")]
    public int? WorstChildProgressStatus { get; set; }

    // Đánh giá chốt do reviewer quyết định, dùng option lấy từ Work.EvaluationOptions
    [BsonElement("evaluationCode")]
    public string? EvaluationCode { get; set; }
    [BsonElement("evaluationLabel")]
    public string? EvaluationLabel { get; set; }
    [BsonElement("evaluationTemplateCode")]
    public string? EvaluationTemplateCode { get; set; }
    [BsonElement("evaluationTemplateId")]
    public string? EvaluationTemplateId { get; set; }
    [BsonElement("evaluationTemplateLabel")]
    public string? EvaluationTemplateLabel { get; set; }
    [BsonElement("evaluationNote")]
    public string? EvaluationNote { get; set; }
    [BsonElement("evaluatedAtUtc")]
    public DateTime? EvaluatedAtUtc { get; set; }
    [BsonElement("evaluatedByUserId")]
    public string? EvaluatedByUserId { get; set; }

    // Phân biệt rõ loại trễ ở tầng assignment để FE đọc được
    [BsonElement("worstPeriodStatus")]
    public int? WorstPeriodStatus { get; set; }
    [BsonElement("worstOverdueReasonCode")]
    public string? WorstOverdueReasonCode { get; set; }
    [BsonElement("worstOverdueReasonLabel")]
    public string? WorstOverdueReasonLabel { get; set; }

    [BsonElement("hasManualEvaluations")]
    public bool HasManualEvaluations { get; set; }
    [BsonElement("evaluatedAssignmentCount")]
    public int EvaluatedAssignmentCount { get; set; }
    [BsonElement("worstEvaluationCode")]
    public string? WorstEvaluationCode { get; set; }
    [BsonElement("worstEvaluationLabel")]
    public string? WorstEvaluationLabel { get; set; }
    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }
}
