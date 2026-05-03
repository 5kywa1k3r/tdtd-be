using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

/// <summary>
/// Dữ liệu báo cáo thực tế của một WorkAssignment tại một kỳ cụ thể.
/// 
/// Hiểu ngắn gọn:
/// - WorkTemplateAssignee = binding runtime hiện hành
/// - WorkReportPeriod = 1 kỳ phải báo cáo
/// - WorkAssignmentReport = 1 bản dữ liệu thực tế của kỳ đó
/// 
/// Report luôn snapshot lại template + schedule tại thời điểm phát sinh,
/// để sau này assignment đổi template/schedule thì report cũ vẫn giữ nguyên lịch sử.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_report")]
public sealed class WorkAssignmentReport : BaseEntity
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

    [BsonElement("workReportPeriodId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkReportPeriodId { get; set; } = default!;

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

    [BsonElement("dueAtUtc")]
    public DateTime? DueAtUtc { get; set; }

    [BsonElement("status")]
    public WorkAssignmentReportStatus Status { get; set; } = WorkAssignmentReportStatus.Draft;

    [BsonElement("submittedAtUtc")]
    public DateTime? SubmittedAtUtc { get; set; }

    [BsonElement("approvedAtUtc")]
    public DateTime? ApprovedAtUtc { get; set; }

    [BsonElement("approvedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ApprovedByUserId { get; set; }

    [BsonElement("data")]
    public object? Data { get; set; }

    [BsonElement("scheduleSnapshotJson")]
    public string ScheduleSnapshotJson { get; set; } = default!;

    [BsonElement("dynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicExcelTemplateId { get; set; } = default!;

    [BsonElement("dynamicExcelTemplateCode")]
    public string DynamicExcelTemplateCode { get; set; } = default!;

    [BsonElement("dynamicExcelTemplateName")]
    public string DynamicExcelTemplateName { get; set; } = default!;

    [BsonElement("specJson")]
    public string SpecJson { get; set; } = default!;

    [BsonElement("dataRectR0")]
    public int DataRectR0 { get; set; }

    [BsonElement("dataRectC0")]
    public int DataRectC0 { get; set; }

    [BsonElement("dataRectR1")]
    public int DataRectR1 { get; set; }

    [BsonElement("dataRectC1")]
    public int DataRectC1 { get; set; }

    [BsonElement("w")]
    public int W { get; set; }

    [BsonElement("h")]
    public int H { get; set; }

    [BsonElement("values1DJson")]
    public string Values1DJson { get; set; } = default!;

    [BsonElement("fieldValuesJson")]
    public string? FieldValuesJson { get; set; }

    [BsonElement("tableValuesJson")]
    public string? TableValuesJson { get; set; }

    /// <summary>
    /// Các trường trải phẳng mà lãnh đạo quan tâm.
    /// Không nhốt riêng trong workbook.
    /// </summary>
    [BsonElement("currentProgressStatus")]
    public string? CurrentProgressStatus { get; set; }

    [BsonElement("reportReason")]
    public string? ReportReason { get; set; }

    [BsonElement("difficulties")]
    public string? Difficulties { get; set; }

    [BsonElement("proposedSolution")]
    public string? ProposedSolution { get; set; }

    [BsonElement("isLateSubmission")]
    public bool IsLateSubmission { get; set; }

    [BsonElement("lateReason")]
    public string? LateReason { get; set; }

    [BsonElement("reviewerComment")]
    public string? ReviewerComment { get; set; }

    [BsonElement("reviewerEvaluation")]
    public string? ReviewerEvaluation { get; set; }

    [BsonElement("returnReason")]
    public string? ReturnReason { get; set; }

    [BsonElement("submittedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SubmittedByUserId { get; set; }

    [BsonElement("returnedAtUtc")]
    public DateTime? ReturnedAtUtc { get; set; }

    [BsonElement("returnedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ReturnedByUserId { get; set; }

    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    [BsonElement("isCurrent")]
    public bool IsCurrent { get; set; } = true;
}
