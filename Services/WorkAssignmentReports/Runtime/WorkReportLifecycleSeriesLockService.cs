using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IWorkReportLifecycleSeriesLockService
{
    Task<WorkReportLifecycleSeriesLease> AcquireAsync(
        string workAssignmentId,
        string operation,
        CancellationToken ct = default);
}

public static class WorkReportLifecycleSeriesOperations
{
    public const string AssignmentComplete = "P5_ASSIGNMENT_COMPLETE";
    public const string AggregateRefresh = "P5_AGGREGATE_REFRESH";
    public const string QueueDueScan = "P5_QUEUE_DUE_SCAN";
    public const string RuntimeReconcile = "P5_RUNTIME_RECONCILE";
}

/// <summary>
/// Serializes the read-guard + report-CAS boundary across every report in one
/// assignment. A report-local lifecycle revision cannot protect invariants that
/// compare two different period reports, so those checks also take this lease.
/// </summary>
public sealed class WorkReportLifecycleSeriesLockService : IWorkReportLifecycleSeriesLockService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly MongoDbContext _ctx;

    public WorkReportLifecycleSeriesLockService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<WorkReportLifecycleSeriesLease> AcquireAsync(
        string workAssignmentId,
        string operation,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workAssignmentId))
            throw new ArgumentException("Work assignment id is required.", nameof(workAssignmentId));

        var now = DateTime.UtcNow;
        var leaseId = Guid.NewGuid().ToString("N");
        var fb = Builders<WorkAssignment>.Filter;
        var available = fb.Eq(x => x.ReportLifecycleLeaseId, null) |
                        fb.Exists(x => x.ReportLifecycleLeaseId, false) |
                        fb.Lte(x => x.ReportLifecycleLeaseExpiresAtUtc, now);
        var filter = fb.Eq(x => x.Id, workAssignmentId) &
                     fb.Eq(x => x.IsDeleted, false) &
                     available;
        if (!string.Equals(
                operation,
                WorkReportLifecycleSeriesOperations.AssignmentComplete,
                StringComparison.Ordinal) &&
            !string.Equals(
                operation,
                WorkReportLifecycleSeriesOperations.RuntimeReconcile,
                StringComparison.Ordinal))
        {
            var nonFlowAssignment =
                fb.Eq(x => x.FlowInstanceId, null) |
                fb.Exists(x => x.FlowInstanceId, false);
            filter &= nonFlowAssignment |
                      fb.Eq(x => x.CompletedAtUtc, null);
        }
        var update = Builders<WorkAssignment>.Update
            .Set(x => x.ReportLifecycleLeaseId, leaseId)
            .Set(x => x.ReportLifecycleLeaseExpiresAtUtc, now.Add(LeaseDuration));

        var assignment = await _ctx.WorkAssignments.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<WorkAssignment>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);

        if (assignment is null)
        {
            var current = await _ctx.WorkAssignments
                .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
                .Project(x => new
                {
                    x.FlowInstanceId,
                    x.CompletedAtUtc,
                    x.ReportLifecycleLeaseExpiresAtUtc,
                    x.ReportLifecycleSeriesRevision
                })
                .FirstOrDefaultAsync(ct);

            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
                new
                {
                    workAssignmentId,
                    operation,
                    reason = current is null
                        ? "ASSIGNMENT_NOT_AVAILABLE"
                        : !string.IsNullOrWhiteSpace(current.FlowInstanceId) &&
                          current.CompletedAtUtc.HasValue
                            ? "DYNAMIC_FLOW_ASSIGNMENT_COMPLETED"
                            : "SERIES_MUTATION_IN_PROGRESS",
                    current?.ReportLifecycleSeriesRevision,
                    current?.ReportLifecycleLeaseExpiresAtUtc
                });
        }

        return new WorkReportLifecycleSeriesLease(
            _ctx.WorkAssignments,
            assignment.Id,
            leaseId,
            operation,
            assignment.ReportLifecycleSeriesRevision);
    }
}

public sealed class WorkReportLifecycleSeriesLease : IAsyncDisposable
{
    private readonly IMongoCollection<WorkAssignment> _assignments;
    private readonly string _assignmentId;
    private readonly string _leaseId;
    private readonly string _operation;
    private int _released;

    internal WorkReportLifecycleSeriesLease(
        IMongoCollection<WorkAssignment> assignments,
        string assignmentId,
        string leaseId,
        string operation,
        long seriesRevision)
    {
        _assignments = assignments;
        _assignmentId = assignmentId;
        _leaseId = leaseId;
        _operation = operation;
        SeriesRevision = seriesRevision;
    }

    public long SeriesRevision { get; }
    internal string AssignmentId => _assignmentId;
    internal string LeaseId => _leaseId;

    public async Task RenewAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _released) != 0)
            throw LifecycleConflict("SERIES_LEASE_ALREADY_RELEASED");

        var now = DateTime.UtcNow;
        var renewal = await _assignments.UpdateOneAsync(
            Builders<WorkAssignment>.Filter.Eq(x => x.Id, _assignmentId) &
            Builders<WorkAssignment>.Filter.Eq(x => x.ReportLifecycleLeaseId, _leaseId) &
            Builders<WorkAssignment>.Filter.Gt(
                x => x.ReportLifecycleLeaseExpiresAtUtc,
                now),
            Builders<WorkAssignment>.Update.Set(
                x => x.ReportLifecycleLeaseExpiresAtUtc,
                now.Add(WorkReportLifecycleSeriesLockService.LeaseDuration)),
            cancellationToken: ct);
        if (renewal.MatchedCount != 1)
            throw LifecycleConflict("SERIES_LEASE_LOST");
    }

    private Exception LifecycleConflict(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
            new
            {
                workAssignmentId = _assignmentId,
                operation = _operation,
                reason
            });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        var fb = Builders<WorkAssignment>.Filter;
        await _assignments.UpdateOneAsync(
            fb.Eq(x => x.Id, _assignmentId) &
            fb.Eq(x => x.ReportLifecycleLeaseId, _leaseId),
            Builders<WorkAssignment>.Update
                .Unset(x => x.ReportLifecycleLeaseId)
                .Unset(x => x.ReportLifecycleLeaseExpiresAtUtc),
            cancellationToken: CancellationToken.None);
    }
}
