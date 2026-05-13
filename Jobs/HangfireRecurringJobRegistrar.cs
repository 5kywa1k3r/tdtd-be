using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using tdtd_be.Common.Time;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.Notifications;
using tdtd_be.Uploads;

namespace tdtd_be.Jobs;

public static class HangfireRecurringJobRegistrar
{
    public const string WorkAssignmentMaterializeJobId = "work-assignment:materialize-scan";
    public const string HangfireHistoryArchiveJobId = "hangfire:history-archive";
    public const string MinioCleanupJobId = "uploads:minio-filedoc-cleanup";
    public const string TusTempCleanupJobId = "uploads:tus-temp-cleanup";
    public const string WorkAssignmentQueueScanJobId = "work-assignment:queue-daily-scan";
    public const string NotificationDueScanJobId = "notifications:due-scan";
    public const string DocRoleProjectionRetryJobId = "docrole:projection-retry";
    public const string DocRoleProjectionRetryDayJobId = "docrole:projection-retry:day";
    public const string DocRoleProjectionRetryNightJobId = "docrole:projection-retry:night";
    public const string UserActionLogRetryJobId = "user-action-log:retry";
    public const string DynamicFormStatisticRebuildJobId = "dynamic-form:statistic-rebuild";

    public static void Register(IConfiguration cfg, IAppTimeService time)
    {
        var tz = time.ApplicationTimeZone;
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

        var hangfireHistoryArchiveCron = cfg["HangfireHistoryArchive:Cron"] ?? "30 22 * * 0";
        RecurringJob.AddOrUpdate<IHangfireHistoryArchiveJob>(
            HangfireHistoryArchiveJobId,
            job => job.RunAsync(CancellationToken.None),
            hangfireHistoryArchiveCron,
            new RecurringJobOptions { TimeZone = tz });

        var queueHour = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentQueue:LocalHour") ?? 0, 0, 23);
        var queueMinute = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentQueue:LocalMinute") ?? 10, 0, 59);
        var dailyQueueCron = $"{queueMinute} {queueHour} * * *";

        RecurringJob.AddOrUpdate<IWorkAssignmentQueueJobService>(
            WorkAssignmentQueueScanJobId,
            job => job.ScanDuePeriodsAsync(CancellationToken.None),
            dailyQueueCron,
            new RecurringJobOptions { TimeZone = tz });

        var materializeCron = cfg["WorkAssignmentMaterialize:Cron"] ?? "*/1 * * * *";
        var materializeMaxJobs = Math.Clamp(
            cfg.GetValue<int?>("WorkAssignmentMaterialize:MaxJobsPerRun") ?? 5,
            1,
            50);
        var materializeBatchSize = Math.Clamp(
            cfg.GetValue<int?>("WorkAssignmentMaterialize:BatchSize") ?? 20,
            1,
            200);

        RecurringJob.AddOrUpdate<IWorkAssignmentMaterializeJobService>(
            WorkAssignmentMaterializeJobId,
            job => job.ProcessPendingJobsAsync(materializeMaxJobs, materializeBatchSize, CancellationToken.None),
            materializeCron,
            new RecurringJobOptions { TimeZone = tz });

        var notificationDueCron = cfg["Notifications:DueScanCron"] ?? "*/5 * * * *";
        RecurringJob.AddOrUpdate<INotificationDueScanJobService>(
            NotificationDueScanJobId,
            job => job.ScanDueNotificationsAsync(CancellationToken.None),
            notificationDueCron,
            new RecurringJobOptions { TimeZone = tz });

        var projectionRetryScheduleMode = cfg["DocRoleProjectionRetry:ScheduleMode"] ?? "Split";
        if (string.Equals(projectionRetryScheduleMode, "Single", StringComparison.OrdinalIgnoreCase))
        {
            RecurringJob.RemoveIfExists(DocRoleProjectionRetryDayJobId);
            RecurringJob.RemoveIfExists(DocRoleProjectionRetryNightJobId);

            var projectionRetryCron = cfg["DocRoleProjectionRetry:Cron"] ?? "*/1 * * * *";
            var projectionRetryMaxJobs = Math.Clamp(
                cfg.GetValue<int?>("DocRoleProjectionRetry:MaxJobsPerRun") ?? 20,
                1,
                200);

            RecurringJob.AddOrUpdate<IDocRoleReadModelProjectionRetryJobService>(
                DocRoleProjectionRetryJobId,
                job => job.ProcessPendingJobsAsync(projectionRetryMaxJobs, CancellationToken.None),
                projectionRetryCron,
                new RecurringJobOptions { TimeZone = tz });
        }
        else
        {
            RecurringJob.RemoveIfExists(DocRoleProjectionRetryJobId);

            var projectionRetryDayCron = cfg["DocRoleProjectionRetry:DayCron"] ?? "17 6-21 * * *";
            var projectionRetryDayMaxJobs = Math.Clamp(
                cfg.GetValue<int?>("DocRoleProjectionRetry:DayMaxJobsPerRun") ?? 5,
                1,
                200);
            var projectionRetryNightCron = cfg["DocRoleProjectionRetry:NightCron"] ?? "*/5 22-23,0-5 * * *";
            var projectionRetryNightMaxJobs = Math.Clamp(
                cfg.GetValue<int?>("DocRoleProjectionRetry:NightMaxJobsPerRun") ?? 20,
                1,
                200);

            RecurringJob.AddOrUpdate<IDocRoleReadModelProjectionRetryJobService>(
                DocRoleProjectionRetryDayJobId,
                job => job.ProcessPendingJobsAsync(projectionRetryDayMaxJobs, CancellationToken.None),
                projectionRetryDayCron,
                new RecurringJobOptions { TimeZone = tz });

            RecurringJob.AddOrUpdate<IDocRoleReadModelProjectionRetryJobService>(
                DocRoleProjectionRetryNightJobId,
                job => job.ProcessPendingJobsAsync(projectionRetryNightMaxJobs, CancellationToken.None),
                projectionRetryNightCron,
                new RecurringJobOptions { TimeZone = tz });
        }

        var actionLogRetryCron = cfg["UserActionLogRetry:Cron"] ?? "*/1 * * * *";
        var actionLogRetryMaxJobs = Math.Clamp(
            cfg.GetValue<int?>("UserActionLogRetry:MaxJobsPerRun") ?? 20,
            1,
            200);

        RecurringJob.AddOrUpdate<IUserActionLogService>(
            UserActionLogRetryJobId,
            job => job.ProcessPendingRetriesAsync(actionLogRetryMaxJobs, CancellationToken.None),
            actionLogRetryCron,
            new RecurringJobOptions { TimeZone = tz });

        var statisticRebuildCron = cfg["DynamicFormStatisticRebuild:Cron"] ?? "0 0 * * *";
        var statisticRebuildMaxJobs = Math.Clamp(
            cfg.GetValue<int?>("DynamicFormStatisticRebuild:MaxJobsPerRun") ?? 3,
            1,
            20);
        var statisticRebuildBatchSize = Math.Clamp(
            cfg.GetValue<int?>("DynamicFormStatisticRebuild:BatchSize") ?? 25,
            1,
            100);

        RecurringJob.AddOrUpdate<IWorkReportStatisticRebuildJobService>(
            DynamicFormStatisticRebuildJobId,
            job => job.ProcessPendingJobsAsync(statisticRebuildMaxJobs, statisticRebuildBatchSize, CancellationToken.None),
            statisticRebuildCron,
            new RecurringJobOptions { TimeZone = tz });

    }

    public static void TriggerMinioCleanupNow()
        => RecurringJob.TriggerJob(MinioCleanupJobId);

    public static void TriggerTusTempCleanupNow()
        => RecurringJob.TriggerJob(TusTempCleanupJobId);
    public static void TriggerHangfireHistoryArchiveNow()
        => RecurringJob.TriggerJob(HangfireHistoryArchiveJobId);
    public static void TriggerWorkAssignmentQueueScanNow()
        => RecurringJob.TriggerJob(WorkAssignmentQueueScanJobId);
    public static void TriggerWorkAssignmentMaterializeNow()
        => RecurringJob.TriggerJob(WorkAssignmentMaterializeJobId);
    public static void TriggerNotificationDueScanNow()
        => RecurringJob.TriggerJob(NotificationDueScanJobId);
    public static void TriggerDocRoleProjectionRetryNow()
    {
        TriggerRecurringJobIfRegistered(DocRoleProjectionRetryJobId);
        TriggerRecurringJobIfRegistered(DocRoleProjectionRetryDayJobId);
        TriggerRecurringJobIfRegistered(DocRoleProjectionRetryNightJobId);
    }
    public static void TriggerUserActionLogRetryNow()
        => RecurringJob.TriggerJob(UserActionLogRetryJobId);
    public static void TriggerDynamicFormStatisticRebuildNow()
        => RecurringJob.TriggerJob(DynamicFormStatisticRebuildJobId);

    public static void EnqueueTusTempCleanupNow(IBackgroundJobClient client)
    {
        client.Create(
            Job.FromExpression<ITusTempCleanupJob>(x => x.RunAsync(CancellationToken.None)),
            new EnqueuedState("default"));
    }

    private static void TriggerRecurringJobIfRegistered(string recurringJobId)
    {
        using var connection = JobStorage.Current.GetConnection();
        if (connection.GetRecurringJobs().Any(x => string.Equals(x.Id, recurringJobId, StringComparison.Ordinal)))
            RecurringJob.TriggerJob(recurringJobId);
    }
}
