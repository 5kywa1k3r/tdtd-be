using Hangfire;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.StatisticsReconciliation.Production;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Uploads;

namespace tdtd_be.Jobs;

public sealed class NonOverlappingRecurringJobRunner
{
    private const int ShortJobLockSeconds = 30 * 60;
    private const int LongJobLockSeconds = 6 * 60 * 60;

    private readonly IMinioFileDocCleanupJob _minioCleanup;
    private readonly ITusTempCleanupJob _tusTempCleanup;
    private readonly IHangfireHistoryArchiveJob _hangfireHistoryArchive;
    private readonly IWorkAssignmentQueueJobService _queueScan;
    private readonly IWorkAssignmentMaterializeJobService _materialize;
    private readonly INotificationDueScanJobService _notificationDueScan;
    private readonly IDocRoleReadModelProjectionRetryJobService _projectionRetry;
    private readonly IUserActionLogService _userActionLog;
    private readonly IWorkReportStatisticRebuildJobService _statisticRebuild;
    private readonly IStatRunFoundationWorker _statRunFoundation;
    private readonly IStatisticReconciliationProductionWorker _reconciliation;
    private readonly IWorkReportLifecycleProjectionReconciler _lifecycleProjectionReconciler;
    private readonly IDynamicFlowMappingOutboxReconciler
        _dynamicFlowMappingOutboxReconciler;
    private readonly IDynamicFlowRuntimeMaterializer _dynamicFlowRuntimeMaterializer;

    public NonOverlappingRecurringJobRunner(
        IMinioFileDocCleanupJob minioCleanup,
        ITusTempCleanupJob tusTempCleanup,
        IHangfireHistoryArchiveJob hangfireHistoryArchive,
        IWorkAssignmentQueueJobService queueScan,
        IWorkAssignmentMaterializeJobService materialize,
        INotificationDueScanJobService notificationDueScan,
        IDocRoleReadModelProjectionRetryJobService projectionRetry,
        IUserActionLogService userActionLog,
        IWorkReportStatisticRebuildJobService statisticRebuild,
        IStatRunFoundationWorker statRunFoundation,
        IStatisticReconciliationProductionWorker reconciliation,
        IWorkReportLifecycleProjectionReconciler lifecycleProjectionReconciler,
        IDynamicFlowMappingOutboxReconciler
            dynamicFlowMappingOutboxReconciler,
        IDynamicFlowRuntimeMaterializer dynamicFlowRuntimeMaterializer)
    {
        _minioCleanup = minioCleanup;
        _tusTempCleanup = tusTempCleanup;
        _hangfireHistoryArchive = hangfireHistoryArchive;
        _queueScan = queueScan;
        _materialize = materialize;
        _notificationDueScan = notificationDueScan;
        _projectionRetry = projectionRetry;
        _userActionLog = userActionLog;
        _statisticRebuild = statisticRebuild;
        _statRunFoundation = statRunFoundation;
        _reconciliation = reconciliation;
        _lifecycleProjectionReconciler = lifecycleProjectionReconciler;
        _dynamicFlowMappingOutboxReconciler =
            dynamicFlowMappingOutboxReconciler;
        _dynamicFlowRuntimeMaterializer = dynamicFlowRuntimeMaterializer;
    }

    [DisableConcurrentExecution(LongJobLockSeconds)]
    public Task RunMinioCleanupAsync(CancellationToken ct = default)
        => _minioCleanup.RunAsync(ct);

    [DisableConcurrentExecution(LongJobLockSeconds)]
    public Task RunTusTempCleanupAsync(CancellationToken ct = default)
        => _tusTempCleanup.RunAsync(ct);

    [DisableConcurrentExecution(LongJobLockSeconds)]
    public Task RunHangfireHistoryArchiveAsync(CancellationToken ct = default)
        => _hangfireHistoryArchive.RunAsync(ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task RunWorkAssignmentQueueScanAsync(CancellationToken ct = default)
        => _queueScan.ScanDuePeriodsAsync(ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessWorkAssignmentMaterializeJobsAsync(
        int maxJobs,
        int batchSize,
        CancellationToken ct = default)
        => _materialize.ProcessPendingJobsAsync(maxJobs, batchSize, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task RunNotificationDueScanAsync(CancellationToken ct = default)
        => _notificationDueScan.ScanDueNotificationsAsync(ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessDocRoleProjectionRetryJobsAsync(
        int maxJobs,
        CancellationToken ct = default)
        => _projectionRetry.ProcessPendingJobsAsync(maxJobs, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessUserActionLogRetriesAsync(
        int maxJobs,
        CancellationToken ct = default)
        => _userActionLog.ProcessPendingRetriesAsync(maxJobs, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessDynamicFormStatisticRebuildJobsAsync(
        int maxJobs,
        int batchSize,
        CancellationToken ct = default)
        => _statisticRebuild.ProcessPendingJobsAsync(maxJobs, batchSize, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessStatRunFoundationDirectJobsAsync(
        int maxJobs,
        CancellationToken ct = default)
        => _statRunFoundation.ProcessPendingAsync(maxJobs, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessStatisticReconciliationAsync(
        int maxJobs,
        CancellationToken ct = default)
        => _reconciliation.ProcessPendingAsync(maxJobs, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessWorkReportLifecycleProjectionOutboxAsync(
        int maxReports,
        CancellationToken ct = default)
        => _lifecycleProjectionReconciler.ProcessPendingAsync(maxReports, ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessDynamicFlowMappingOutboxAsync(
        int maxItems,
        CancellationToken ct = default)
        => _dynamicFlowMappingOutboxReconciler.ProcessPendingAsync(
            maxItems,
            ct);

    [DisableConcurrentExecution(ShortJobLockSeconds)]
    public Task<int> ProcessDynamicFlowRuntimeOutboxAsync(
        int maxItems,
        CancellationToken ct = default)
        => _dynamicFlowRuntimeMaterializer.ProcessPendingAsync(maxItems, ct);
}
