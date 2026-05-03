using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

/// <summary>
/// Audit log nghiệp vụ của báo cáo.
///
/// Ghi lại toàn bộ các action quan trọng:
/// - INIT_DRAFT
/// - SAVE_DRAFT
/// - SUBMIT
/// - RETURN
/// - ACCEPT
/// - UPDATE_LATE_REASON
/// - REOPEN
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_report_logs")]
public sealed class WorkAssignmentReportLog : BaseEntity
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

    [BsonElement("workAssignmentReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentReportId { get; set; } = default!;

    [BsonElement("action")]
    public string Action { get; set; } = string.Empty;

    [BsonElement("fromStatus")]
    public string FromStatus { get; set; } = string.Empty;

    [BsonElement("toStatus")]
    public string ToStatus { get; set; } = string.Empty;

    [BsonElement("actionByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ActionByUserId { get; set; } = default!;

    [BsonElement("actionAtUtc")]
    public DateTime ActionAtUtc { get; set; }

    [BsonElement("reason")]
    public string? Reason { get; set; }

    [BsonElement("comment")]
    public string? Comment { get; set; }

    [BsonElement("snapshotJson")]
    public string? SnapshotJson { get; set; }
}
