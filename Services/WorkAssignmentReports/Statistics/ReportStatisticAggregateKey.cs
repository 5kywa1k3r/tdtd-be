namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed record ReportStatisticAggregateKey(
    string WorkId,
    string? PeriodInstanceKey,
    string? DynamicFormTemplateId);
