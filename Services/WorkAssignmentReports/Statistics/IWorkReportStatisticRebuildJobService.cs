using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed record StatisticRebuildJobEnqueueResult(
    string JobId,
    long QueuedReportCount,
    DateTime? ScheduledAtUtc,
    bool RunsImmediately);

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

    Task<int> ProcessPendingJobsAsync(
        int maxJobs = 3,
        int batchSize = 25,
        CancellationToken ct = default);
}
