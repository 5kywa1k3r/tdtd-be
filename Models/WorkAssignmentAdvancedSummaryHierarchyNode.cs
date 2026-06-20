using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

public abstract class WorkAssignmentAdvancedSummaryHierarchyNodeBase : BaseEntity
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

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("sectionId")]
    public string SectionId { get; set; } = default!;

    [BsonElement("configId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ConfigId { get; set; } = default!;

    [BsonElement("configVersionNo")]
    public int ConfigVersionNo { get; set; }

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("grain")]
    public string Grain { get; set; } = default!;

    [BsonElement("grainKey")]
    public string GrainKey { get; set; } = default!;

    [BsonElement("windowStartUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime WindowStartUtc { get; set; }

    [BsonElement("windowEndExclusiveUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime WindowEndExclusiveUtc { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean;

    [BsonElement("isDirty")]
    public bool IsDirty { get; set; }

    [BsonElement("dirtyReason")]
    public string? DirtyReason { get; set; }

    [BsonElement("sourceSignatureHash")]
    public string? SourceSignatureHash { get; set; }

    [BsonElement("sourceReportCount")]
    public long SourceReportCount { get; set; }

    [BsonElement("sourceReportIds")]
    public List<string> SourceReportIds { get; set; } = new();

    [BsonElement("inputNodeKeys")]
    public List<string> InputNodeKeys { get; set; } = new();

    [BsonElement("valueJson")]
    public string ValueJson { get; set; } = "{}";

    [BsonElement("valueHash")]
    public string? ValueHash { get; set; }

    [BsonElement("builtAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? BuiltAtUtc { get; set; }

    [BsonElement("buildJobId")]
    public string? BuildJobId { get; set; }

    [BsonElement("buildCorrelationId")]
    public string? BuildCorrelationId { get; set; }

    [BsonElement("buildError")]
    public string? BuildError { get; set; }
}

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_advanced_summary_day_nodes")]
public sealed class WorkAssignmentAdvancedSummaryDayNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
{
    [BsonElement("dayKey")]
    public string DayKey { get; set; } = default!;
}

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_advanced_summary_month_nodes")]
public sealed class WorkAssignmentAdvancedSummaryMonthNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
{
    [BsonElement("monthKey")]
    public string MonthKey { get; set; } = default!;

    [BsonElement("yearKey")]
    public string YearKey { get; set; } = default!;
}

[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_advanced_summary_year_nodes")]
public sealed class WorkAssignmentAdvancedSummaryYearNode : WorkAssignmentAdvancedSummaryHierarchyNodeBase
{
    [BsonElement("yearKey")]
    public string YearKey { get; set; } = default!;
}

public static class WorkAssignmentAdvancedSummaryHierarchyGrains
{
    public const string Day = "DAY";
    public const string Month = "MONTH";
    public const string Year = "YEAR";
}

public static class WorkAssignmentAdvancedSummaryHierarchyNodeStatuses
{
    public const string Clean = "CLEAN";
    public const string Dirty = "DIRTY";
    public const string Building = "BUILDING";
    public const string Failed = "FAILED";
}
