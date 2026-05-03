using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

/// <summary>
/// Một kỳ báo cáo runtime phải thực hiện của 1 binding WorkTemplateAssignee.
///
/// Vai trò:
/// - FE list được các kỳ cần báo cáo kể cả khi chưa có report
/// - quản lý trạng thái ngoài cùng theo kỳ
/// - nối sang bản report hiện hành của kỳ
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("work_report_periods")]
public sealed class WorkReportPeriod : BaseEntity
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

    [BsonElement("workTemplateAssigneeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkTemplateAssigneeId { get; set; } = default!;

    [BsonElement("dynamicExcelId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicExcelId { get; set; } = default!;

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? DynamicFormTemplateId { get; set; }

    [BsonElement("dynamicFormTemplateCode")]
    public string? DynamicFormTemplateCode { get; set; }

    [BsonElement("dynamicFormTemplateName")]
    public string? DynamicFormTemplateName { get; set; }

    [BsonElement("assigneeUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string AssigneeUserId { get; set; } = default!;

    [BsonElement("assigneeUnitId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? AssigneeUnitId { get; set; }

    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    [BsonElement("periodInstanceKey")]
    public string PeriodInstanceKey { get; set; } = default!;

    [BsonElement("periodKind")]
    public string PeriodKind { get; set; } = WorkReportPeriodKind.Scheduled;

    [BsonElement("reportTitle")]
    public string? ReportTitle { get; set; }

    [BsonElement("reportDate")]
    public DateTime? ReportDate { get; set; }

    [BsonElement("linkedScheduledPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? LinkedScheduledPeriodId { get; set; }

    [BsonElement("periodStart")]
    public DateTime? PeriodStart { get; set; }

    [BsonElement("periodEnd")]
    public DateTime? PeriodEnd { get; set; }

    /// <summary>
    /// Hạn cuối nộp của kỳ. Có thể bằng PeriodEnd hoặc được tính riêng.
    /// </summary>
    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }

    [BsonElement("status")]
    public WorkReportPeriodStatus Status { get; set; } = WorkReportPeriodStatus.Pending;

    [BsonElement("isOverdue")]
    public bool IsOverdue { get; set; }

    [BsonElement("currentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CurrentReportId { get; set; }

    [BsonElement("reportVersionCount")]
    public int ReportVersionCount { get; set; }

    [BsonElement("lastDraftSavedAtUtc")]
    public DateTime? LastDraftSavedAtUtc { get; set; }

    [BsonElement("lastSubmittedAtUtc")]
    public DateTime? LastSubmittedAtUtc { get; set; }

    [BsonElement("lastReviewedAtUtc")]
    public DateTime? LastReviewedAtUtc { get; set; }

    [BsonElement("requiresLateReason")]
    public bool RequiresLateReason { get; set; }

    [BsonElement("acceptedLateReason")]
    public string? AcceptedLateReason { get; set; }

    /// <summary>
    /// Các field trải phẳng để list nhanh ngoài detail.
    /// </summary>
    [BsonElement("currentProgressStatus")]
    public string? CurrentProgressStatus { get; set; }

    [BsonElement("reportReason")]
    public string? ReportReason { get; set; }

    [BsonElement("difficulties")]
    public string? Difficulties { get; set; }

    [BsonElement("proposedSolution")]
    public string? ProposedSolution { get; set; }

    [BsonElement("lateReason")]
    public string? LateReason { get; set; }

    [BsonElement("reviewerComment")]
    public string? ReviewerComment { get; set; }

    [BsonElement("reviewerEvaluation")]
    public string? ReviewerEvaluation { get; set; }

    [BsonElement("returnReason")]
    public string? ReturnReason { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}
