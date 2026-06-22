using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed record StatisticRebuildJobEnqueueResult(
    string JobId,
    long QueuedReportCount,
    DateTime? ScheduledAtUtc,
    bool RunsImmediately);

public sealed class StatisticRebuildScopeRequest
{
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? FlowInstanceId { get; init; }
    public string? FlowEffectiveStatus { get; init; }
    public string? PeriodInstanceKey { get; init; }
}

public interface IWorkReportStatisticRebuildJobService
{
    Task<StatisticRebuildJobEnqueueResult> EnqueueForTemplateStatisticConfigAsync(
        DynamicFormTemplate template,
        string requestedByUserId,
        bool highPriority,
        CancellationToken ct = default);

    Task<IReadOnlyList<StatisticRebuildJobEnqueueResult>> EnqueueForLabelChangeAsync(
        LabelCatalogItem label,
        string requestedByUserId,
        bool highPriority,
        CancellationToken ct = default);

    Task<StatisticRebuildJobEnqueueResult> EnqueueForBoundedScopeAsync(
        StatisticRebuildScopeRequest scope,
        string requestedByUserId,
        bool highPriority,
        CancellationToken ct = default);

    Task<int> ProcessPendingJobsAsync(
        int maxJobs = 3,
        int batchSize = 25,
        CancellationToken ct = default);
}
