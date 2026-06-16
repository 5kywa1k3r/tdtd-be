using tdtd_be.DTOs.WorkAssignments.AggregateTable;

namespace tdtd_be.DTOs.WorkAssignmentReports;

public sealed class ApplyDynamicFormAggregateDraftRequest
{
    public DynamicFormAggregateRequest AggregateRequest { get; set; } = new();

    public string? DataOrigin { get; set; }
    public string? CumulativeContributionMode { get; set; }
    public string? CumulativeContributionPolicyJson { get; set; }
    public string? TargetBlockId { get; set; }
    public string? ValueSelector { get; set; }
    public bool? ClearExistingValues { get; set; }
    public string? ReportMapConfigJson { get; set; }

    // Backward-compatible only. Aggregation applied to reports is always APPROVED_ONLY.
    public bool? AllowSubmittedSources { get; set; }
}
