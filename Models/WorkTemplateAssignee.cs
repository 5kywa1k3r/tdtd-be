using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonCollection("work_template_assignees")]
public sealed class WorkTemplateAssignee : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonElement("workId")]
    public string WorkId { get; set; } = default!;

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonElement("workAssignmentId")]
    public string WorkAssignmentId { get; set; } = default!;

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

    [BsonElement("assigneeUserId")]
    public string AssigneeUserId { get; set; } = default!;

    [BsonElement("assigneeUsername")]
    public string AssigneeUsername { get; set; } = string.Empty;

    [BsonElement("assigneeFullName")]
    public string AssigneeFullName { get; set; } = string.Empty;

    [BsonElement("assigneeUnitId")]
    public string? AssigneeUnitId { get; set; }

    [BsonElement("assigneeUnitSymbol")]
    public string? AssigneeUnitSymbol { get; set; }

    [BsonElement("assigneeUnitShortName")]
    public string? AssigneeUnitShortName { get; set; }

    [BsonElement("assigneeUnitName")]
    public string? AssigneeUnitName { get; set; }

    [BsonElement("assignmentType")]
    public string AssignmentType { get; set; } = string.Empty;

    [BsonElement("aggregationType")]
    public string AggregationType { get; set; } = string.Empty;

    [BsonElement("schedule")]
    public AssignmentSchedule? Schedule { get; set; }

    [BsonElement("startDate")]
    public DateTime? StartDate { get; set; }

    [BsonElement("completedDate")]
    public DateTime? CompletedDate { get; set; }

    [BsonElement("allowUserCreatedReports")]
    public bool AllowUserCreatedReports { get; set; } = true;

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}
