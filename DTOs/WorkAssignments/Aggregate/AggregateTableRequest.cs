namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class AggregateTableRequest
{
    public string WorkId { get; set; } = default!;
    public string ParentAssignmentId { get; set; } = default!;
    public string DynamicExcelId { get; set; } = default!;

    // SINGLE_PERIOD | PERIOD_RANGE | ALL_PERIODS
    public string? PeriodScopeMode { get; set; }

    // dùng khi SINGLE_PERIOD
    public string? PeriodKey { get; set; }

    // dùng khi PERIOD_RANGE
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }

    // APPROVED_ONLY | APPROVED_AND_SUBMITTED
    public string? SourceStatusMode { get; set; }

    public List<string>? SelectedUnitIds { get; set; }

    // GROUP_BY_USER_SUM | SUM_ALL | ...
    public string? AggregateMode { get; set; }
}
