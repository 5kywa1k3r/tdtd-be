using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using tdtd_be.Common.Time;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.Notifications;
using tdtd_be.Uploads;

namespace tdtd_be.Jobs;

public static class HangfireRecurringJobRegistrar
{
    private static int _recurringRegistrationReady;

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
    public const string StatRunFoundationDirectJobId =
        "statistics-run:foundation-direct";
    public const string StatisticReconciliationProductionJobId =
        "statistics:reconciliation-production";
    public const string WorkReportLifecycleProjectionOutboxJobId = "work-report:lifecycle-projection-outbox";
    public const string DynamicFlowMappingOutboxJobId =
        "dynamic-flow:mapping-outbox";
    public const string DynamicFlowRuntimeOutboxJobId = "dynamic-flow:runtime-outbox";

    public static void Register(IConfiguration cfg, IAppTimeService time)
    {
        Volatile.Write(ref _recurringRegistrationReady, 0);
        var tz = time.ApplicationTimeZone;
        var hour = Math.Clamp(cfg.GetValue<int?>("UploadCleanup:LocalHour") ?? 21, 0, 23);
        var minute = Math.Clamp(cfg.GetValue<int?>("UploadCleanup:LocalMinute") ?? 0, 0, 59);

        // Run every Sunday at configured local time; inside the jobs we guard to only execute on the last Sunday.
        var weeklySundayCron = $"{minute} {hour} * * 0";

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            MinioCleanupJobId,
            job => job.RunMinioCleanupAsync(CancellationToken.None),
            weeklySundayCron,
            new RecurringJobOptions { TimeZone = tz });

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            TusTempCleanupJobId,
            job => job.RunTusTempCleanupAsync(CancellationToken.None),
            weeklySundayCron,
            new RecurringJobOptions { TimeZone = tz });

        var hangfireHistoryArchiveCron = cfg["HangfireHistoryArchive:Cron"] ?? "30 22 * * 0";
        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            HangfireHistoryArchiveJobId,
            job => job.RunHangfireHistoryArchiveAsync(CancellationToken.None),
            hangfireHistoryArchiveCron,
            new RecurringJobOptions { TimeZone = tz });

        var queueHour = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentQueue:LocalHour") ?? 0, 0, 23);
        var queueMinute = Math.Clamp(cfg.GetValue<int?>("WorkAssignmentQueue:LocalMinute") ?? 10, 0, 59);
        var dailyQueueCron = $"{queueMinute} {queueHour} * * *";

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            WorkAssignmentQueueScanJobId,
            job => job.RunWorkAssignmentQueueScanAsync(CancellationToken.None),
            dailyQueueCron,
            new RecurringJobOptions { TimeZone = tz });

        var materializeCron = cfg["WorkAssignmentMaterialize:Cron"] ?? "*/1 * * * *";
        var materializeMaxJobs = Math.Clamp(
            cfg.GetValue<int?>("WorkAssignmentMaterialize:MaxJobsPerRun") ?? 50,
            1,
            200);
        var materializeBatchSize = Math.Clamp(
            cfg.GetValue<int?>("WorkAssignmentMaterialize:BatchSize") ?? 500,
            1,
            1000);

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            WorkAssignmentMaterializeJobId,
            job => job.ProcessWorkAssignmentMaterializeJobsAsync(materializeMaxJobs, materializeBatchSize, CancellationToken.None),
            materializeCron,
            new RecurringJobOptions { TimeZone = tz });

        var notificationDueCron = cfg["Notifications:DueScanCron"] ?? "*/5 * * * *";
        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            NotificationDueScanJobId,
            job => job.RunNotificationDueScanAsync(CancellationToken.None),
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

            RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
                DocRoleProjectionRetryJobId,
                job => job.ProcessDocRoleProjectionRetryJobsAsync(projectionRetryMaxJobs, CancellationToken.None),
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

            RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
                DocRoleProjectionRetryDayJobId,
                job => job.ProcessDocRoleProjectionRetryJobsAsync(projectionRetryDayMaxJobs, CancellationToken.None),
                projectionRetryDayCron,
                new RecurringJobOptions { TimeZone = tz });

            RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
                DocRoleProjectionRetryNightJobId,
                job => job.ProcessDocRoleProjectionRetryJobsAsync(projectionRetryNightMaxJobs, CancellationToken.None),
                projectionRetryNightCron,
                new RecurringJobOptions { TimeZone = tz });
        }

        var actionLogRetryCron = cfg["UserActionLogRetry:Cron"] ?? "*/1 * * * *";
        var actionLogRetryMaxJobs = Math.Clamp(
            cfg.GetValue<int?>("UserActionLogRetry:MaxJobsPerRun") ?? 20,
            1,
            200);

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            UserActionLogRetryJobId,
            job => job.ProcessUserActionLogRetriesAsync(actionLogRetryMaxJobs, CancellationToken.None),
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

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            DynamicFormStatisticRebuildJobId,
            job => job.ProcessDynamicFormStatisticRebuildJobsAsync(statisticRebuildMaxJobs, statisticRebuildBatchSize, CancellationToken.None),
            statisticRebuildCron,
            new RecurringJobOptions { TimeZone = tz });

        var foundationCron =
            cfg["StatRunFoundationWorker:Cron"] ?? "*/1 * * * *";
        var foundationMaxJobs = Math.Clamp(
            cfg.GetValue<int?>(
                "StatRunFoundationWorker:MaxJobsPerRun") ?? 3,
            1,
            20);
        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            StatRunFoundationDirectJobId,
            job => job.ProcessStatRunFoundationDirectJobsAsync(
                foundationMaxJobs,
                CancellationToken.None),
            foundationCron,
            new RecurringJobOptions { TimeZone = tz });

        var reconciliationCron =
            cfg["StatisticReconciliation:ProductionWorker:Cron"] ??
            "*/1 * * * *";
        var reconciliationMaxJobs = Math.Clamp(
            cfg.GetValue<int?>(
                "StatisticReconciliation:ProductionWorker:MaxJobsPerRun") ?? 20,
            1,
            200);
        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            StatisticReconciliationProductionJobId,
            job => job.ProcessStatisticReconciliationAsync(
                reconciliationMaxJobs,
                CancellationToken.None),
            reconciliationCron,
            new RecurringJobOptions { TimeZone = tz });

        var lifecycleProjectionCron = cfg["WorkReportLifecycleProjectionOutbox:Cron"] ?? "*/1 * * * *";
        var lifecycleProjectionMaxReports = Math.Clamp(
            cfg.GetValue<int?>("WorkReportLifecycleProjectionOutbox:MaxReportsPerRun") ?? 20,
            1,
            200);

        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            WorkReportLifecycleProjectionOutboxJobId,
            job => job.ProcessWorkReportLifecycleProjectionOutboxAsync(lifecycleProjectionMaxReports, CancellationToken.None),
            lifecycleProjectionCron,
            new RecurringJobOptions { TimeZone = tz });

        var mappingOutboxCron =
            cfg["DynamicFlowMapping:OutboxCron"] ??
            "*/1 * * * *";
        var mappingOutboxMaxItems = Math.Clamp(
            cfg.GetValue<int?>(
                "DynamicFlowMapping:MaxOutboxItemsPerRun") ??
            20,
            1,
            200);
        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            DynamicFlowMappingOutboxJobId,
            job => job.ProcessDynamicFlowMappingOutboxAsync(
                mappingOutboxMaxItems,
                CancellationToken.None),
            mappingOutboxCron,
            new RecurringJobOptions { TimeZone = tz });

        var dynamicFlowRuntimeCron = cfg["DynamicFlowRuntime:OutboxCron"] ?? "*/1 * * * *";
        var dynamicFlowRuntimeMaxItems = Math.Clamp(
            cfg.GetValue<int?>("DynamicFlowRuntime:MaxOutboxItemsPerRun") ?? 50,
            1,
            200);
        RecurringJob.AddOrUpdate<NonOverlappingRecurringJobRunner>(
            DynamicFlowRuntimeOutboxJobId,
            job => job.ProcessDynamicFlowRuntimeOutboxAsync(
                dynamicFlowRuntimeMaxItems,
                CancellationToken.None),
            dynamicFlowRuntimeCron,
            new RecurringJobOptions { TimeZone = tz });

        Volatile.Write(ref _recurringRegistrationReady, 1);
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
    public static void TriggerWorkReportLifecycleProjectionOutboxNow()
        => RecurringJob.TriggerJob(WorkReportLifecycleProjectionOutboxJobId);
    public static void TriggerDynamicFlowMappingOutboxNow()
        => RecurringJob.TriggerJob(DynamicFlowMappingOutboxJobId);
    public static bool TryTriggerDynamicFormStatisticRebuildNow(
        bool recurringRegistrationEnabled,
        ILogger? logger = null)
        => TryTriggerDynamicFormStatisticRebuildNow(
            recurringRegistrationEnabled,
            Volatile.Read(ref _recurringRegistrationReady) == 1,
            TriggerRecurringJobIfRegistered,
            (reason, exception) =>
            {
                if (logger is null)
                    return;

                if (exception is null)
                {
                    logger.LogWarning(
                        "Skipped best-effort trigger for recurring job {RecurringJobId}: {Reason}",
                        DynamicFormStatisticRebuildJobId,
                        reason);
                }
                else
                {
                    logger.LogWarning(
                        exception,
                        "Best-effort trigger failed for recurring job {RecurringJobId}: {Reason}",
                        DynamicFormStatisticRebuildJobId,
                        reason);
                }
            });

    internal static bool TryTriggerDynamicFormStatisticRebuildNow(
        bool recurringRegistrationEnabled,
        bool recurringRegistrationReady,
        Func<string, bool> triggerIfRegistered,
        Action<string, Exception?>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(triggerIfRegistered);
        if (!recurringRegistrationEnabled || !recurringRegistrationReady)
            return false;

        try
        {
            if (triggerIfRegistered(DynamicFormStatisticRebuildJobId))
                return true;

            onFailure?.Invoke("RECURRING_JOB_NOT_REGISTERED", null);
            return false;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke("RECURRING_JOB_TRIGGER_FAILED", ex);
            return false;
        }
    }

    public static void EnqueueTusTempCleanupNow(IBackgroundJobClient client)
    {
        client.Create(
            Job.FromExpression<ITusTempCleanupJob>(x => x.RunAsync(CancellationToken.None)),
            new EnqueuedState("default"));
    }

    private static bool TriggerRecurringJobIfRegistered(string recurringJobId)
    {
        using var connection = JobStorage.Current.GetConnection();
        if (!connection.GetRecurringJobs().Any(x => string.Equals(x.Id, recurringJobId, StringComparison.Ordinal)))
            return false;

        RecurringJob.TriggerJob(recurringJobId);
        return true;
    }
}
