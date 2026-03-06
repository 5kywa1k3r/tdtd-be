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
    public string DynamicExcelId { get; set; } = default!;

    [BsonElement("dynamicExcelCode")]
    public string DynamicExcelCode { get; set; } = string.Empty;

    [BsonElement("dynamicExcelName")]
    public string DynamicExcelName { get; set; } = string.Empty;

    [BsonElement("workType")]
    public string WorkType { get; set; } = string.Empty;

    // ONCE / PERIODIC_REPORT
    [BsonElement("assignmentType")]
    public string AssignmentType { get; set; } = string.Empty;

    // MATRIX / UNIT_ROW_COL
    [BsonElement("aggregationType")]
    public string AggregationType { get; set; } = string.Empty;

    [BsonElement("schedule")]
    public AssignmentSchedule? Schedule { get; set; }

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
}