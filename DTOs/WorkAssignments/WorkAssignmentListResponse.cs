using tdtd_be.DTOs.Users;

namespace tdtd_be.DTOs.WorkAssignments;

public sealed class WorkAssignmentListResponse
{
    public string Id { get; set; } = default!;
    public string WorkId { get; set; } = default!;

    public string DynamicExcelId { get; set; } = default!;
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateCode { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string? DynamicFormDataSourceRulesJson { get; set; }

    public string AssignmentType { get; set; } = string.Empty;
    public string AggregationType { get; set; } = string.Empty;

    public List<UserRefDTO> Assignees { get; set; } = new();
    public List<UserRefDTO> LeaderWatchers { get; set; } = new();

    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public bool AllowUserCreatedReports { get; set; }

    public int ProgressStatus { get; set; }
    public DateTime? ProgressStatusUpdatedAtUtc { get; set; }
    public string? LatestPeriodKey { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
    public bool HasAnyDuePeriod { get; set; }
    public bool HasOverduePeriod { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string? ParentAssignmentId { get; set; }
    public string RootAssignmentId { get; set; } = default!;
    public int Level { get; set; }
    public string Code { get; set; } = default!;
    public string Path { get; set; } = default!;
    public string? EvaluationCode { get; set; }
    public string? EvaluationLabel { get; set; }
    public string? EvaluationTemplateId { get; set; }
    public string? EvaluationTemplateCode { get; set; }
    public string? EvaluationTemplateLabel { get; set; }
    public int? WorstPeriodStatus { get; set; }
    public string? WorstOverdueReasonCode { get; set; }
    public string? WorstOverdueReasonLabel { get; set; }
    public bool HasManualEvaluations { get; set; }
    public int EvaluatedAssignmentCount { get; set; }
    public string? WorstEvaluationCode { get; set; }
    public string? WorstEvaluationLabel { get; set; }
    public DateTime? DueAtUtc { get; set; }
}
