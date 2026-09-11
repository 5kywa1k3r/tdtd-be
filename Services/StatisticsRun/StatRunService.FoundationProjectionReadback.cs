using MongoDB.Driver;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunService
{
    private async Task<string?> ResolveFoundationProjectionRunIdAsync(
        WorkReportStatisticRebuildJob job,
        CancellationToken ct)
    {
        if (!string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation,
                StringComparison.Ordinal) ||
            !string.Equals(
                job.Status,
                WorkReportStatisticRebuildJobStatuses.Completed,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationHash))
        {
            return null;
        }

        var matches = await _ctx.WorkReportStatisticRebuildJobs
            .Find(x =>
                !x.IsDeleted &&
                x.RunKind == WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection &&
                x.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                x.IsCurrentPublication &&
                x.GenerationId == job.GenerationId &&
                x.GenerationHash == job.GenerationHash &&
                x.SourceReportId == job.SourceReportId &&
                x.WorkId == job.WorkId &&
                x.WorkAssignmentId == job.WorkAssignmentId &&
                x.ConfigHash == job.ConfigHash &&
                x.DynamicFormTemplateId == job.DynamicFormTemplateId &&
                x.PeriodKey == job.PeriodKey &&
                x.PeriodInstanceKey == job.PeriodInstanceKey &&
                x.SourceLifecycleRevision == job.SourceLifecycleRevision)
            .Project(x => x.Id)
            .Limit(2)
            .ToListAsync(ct);

        return matches.Count == 1 ? matches[0] : null;
    }
}
