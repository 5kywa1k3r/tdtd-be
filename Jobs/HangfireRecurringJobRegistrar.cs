using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Uploads;

namespace tdtd_be.Jobs;

public static class HangfireRecurringJobRegistrar
{
    public const string WorkAssignmentMaterializeJobId = "work-assignment:materialize-scan";
    public const string MinioCleanupJobId = "uploads:minio-filedoc-cleanup";
    public const string TusTempCleanupJobId = "uploads:tus-temp-cleanup";
    public const string WorkAssignmentQueueScanJobId = "work-assignment:queue-daily-scan";

    public static void Register(IConfiguration cfg)
    {
        var tz = HangfireJobTimeHelper.ResolveBangkokTimeZone();
        var hour = Math.Clamp(cfg.GetValue<int?>("UploadCleanup:LocalHour") ?? 21, 0, 23);
        var minute = Math.Clamp(cfg.GetValue<int?>("UploadCleanup:LocalMinute") ?? 0, 0, 59);

        // Run every Sunday at configured local time; inside the jobs we guard to only execute on the last Sunday.
        var weeklySundayCron = $"{minute} {hour} * * 0";

        RecurringJob.AddOrUpdate<IMinioFileDocCleanupJob>(
            MinioCleanupJobId,
            job => job.RunAsync(CancellationToken.None),
            weeklySundayCron,
            new RecurringJobOptions { TimeZone = tz });

        RecurringJob.AddOrUpdate<ITusTempCleanupJob>(
            TusTempCleanupJobId,
            job => job.RunAsync(CancellationToken.None),
            weeklySundayCron,
            new RecurringJobOptions { TimeZone = tz });

        var queueHour = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentQueue:LocalHour") ?? 0, 0, 23);
        var queueMinute = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentQueue:LocalMinute") ?? 10, 0, 59);
        var dailyQueueCron = $"{queueMinute} {queueHour} * * *";

        RecurringJob.AddOrUpdate<IWorkAssignmentQueueJobService>(
            WorkAssignmentQueueScanJobId,
            job => job.ScanDuePeriodsAsync(CancellationToken.None),
            dailyQueueCron,
            new RecurringJobOptions { TimeZone = tz });

        RecurringJob.AddOrUpdate<IWorkAssignmentMaterializeJobService>(
            WorkAssignmentMaterializeJobId,
            job => job.ProcessPendingJobsAsync(5, 20, CancellationToken.None),
            "*/1 * * * *",
            new RecurringJobOptions { TimeZone = tz });

    }

    public static void TriggerMinioCleanupNow()
        => RecurringJob.TriggerJob(MinioCleanupJobId);

    public static void TriggerTusTempCleanupNow()
        => RecurringJob.TriggerJob(TusTempCleanupJobId);
    public static void TriggerWorkAssignmentQueueScanNow()
        => RecurringJob.TriggerJob(WorkAssignmentQueueScanJobId);
    public static void TriggerWorkAssignmentMaterializeNow()
        => RecurringJob.TriggerJob(WorkAssignmentMaterializeJobId);

    public static void EnqueueTusTempCleanupNow(IBackgroundJobClient client)
    {
        client.Create(
            Job.FromExpression<ITusTempCleanupJob>(x => x.RunAsync(CancellationToken.None)),
            new EnqueuedState("default"));
    }
}
