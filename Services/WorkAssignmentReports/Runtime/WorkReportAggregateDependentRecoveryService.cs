using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IWorkReportAggregateDependentRecoveryService
{
    Task RecoverPendingAsync(
        string sourceReportId,
        string actorUserId,
        CancellationToken ct = default);

    Task RecoverPendingAsync(
        WorkAssignmentReport source,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending,
        string actorUserId,
        CancellationToken ct = default);
}

/// <summary>
/// Reuses the report aggregate refresh engine while keeping the lifecycle reconciler free of a
/// constructor dependency on <see cref="IWorkAssignmentReportService"/>. The report service is
/// resolved only after the current scoped object graph has been built.
/// </summary>
public sealed class WorkReportAggregateDependentRecoveryService : IWorkReportAggregateDependentRecoveryService
{
    private readonly MongoDbContext _ctx;
    private readonly IServiceProvider _services;

    public WorkReportAggregateDependentRecoveryService(
        MongoDbContext ctx,
        IServiceProvider services)
    {
        _ctx = ctx;
        _services = services;
    }

    public async Task RecoverPendingAsync(
        string sourceReportId,
        string actorUserId,
        CancellationToken ct = default)
    {
        // Luồng v2 được dispatch riêng trong lifecycle reconciler.
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return;
        // An approved aggregate can itself be a source for another aggregate. Its lifecycle
        // outbox stays pending and the recurring runner continues the chain without recursion.
        if (WorkReportAggregateDependentRecoveryExecution.IsActive)
            return;

        if (string.IsNullOrWhiteSpace(sourceReportId))
            return;

        var source = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == sourceReportId.Trim() && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (source is null)
            return;

        var pending = (source.LifecycleProjectionOutbox ?? new List<WorkReportLifecycleProjectionOutboxEntry>())
            .Where(x => string.Equals(
                x.State,
                WorkReportLifecycleProjectionOutboxStates.Pending,
                StringComparison.Ordinal))
            .ToList();
        await RecoverCoreAsync(source, pending, actorUserId, ct);
    }

    public Task RecoverPendingAsync(
        WorkAssignmentReport source,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (tdtd_be.Services.WorkAssignments.LegacyAggregateRetirement.IsDisabled) return Task.CompletedTask;
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pending);

        if (WorkReportAggregateDependentRecoveryExecution.IsActive)
        {
            throw new InvalidOperationException(
                $"Nested aggregate-dependent lifecycle reconciliation is not allowed for report '{source.Id}'.");
        }

        return RecoverCoreAsync(source, pending, actorUserId, ct);
    }

    private async Task RecoverCoreAsync(
        WorkAssignmentReport source,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending,
        string actorUserId,
        CancellationToken ct)
    {
        var targetRevision = WorkReportAggregateDependentRecoveryPolicy.ResolveTargetLifecycleRevision(pending);
        if (!targetRevision.HasValue ||
            source.AggregateDependentsLastRecoveredLifecycleRevision >= targetRevision.Value)
        {
            return;
        }

        using (WorkReportAggregateDependentRecoveryExecution.Enter())
        {
            var reportService = _services.GetRequiredService<IWorkAssignmentReportService>();
            await reportService.RefreshDynamicFormAggregateDependentsAsync(source.Id, actorUserId, ct);
        }

        var now = DateTime.UtcNow;
        var markerUpdate = await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == source.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Max(x => x.AggregateDependentsLastRecoveredLifecycleRevision, targetRevision.Value)
                .Set(x => x.AggregateDependentsLastRecoveredAtUtc, now),
            cancellationToken: ct);
        if (markerUpdate.MatchedCount != 1)
        {
            throw new InvalidOperationException(
                $"Could not persist aggregate-dependent recovery revision for report '{source.Id}'.");
        }

        source.AggregateDependentsLastRecoveredLifecycleRevision = Math.Max(
            source.AggregateDependentsLastRecoveredLifecycleRevision,
            targetRevision.Value);
        source.AggregateDependentsLastRecoveredAtUtc = now;
    }
}

public static class WorkReportAggregateDependentRecoveryPolicy
{
    public static int? ResolveTargetLifecycleRevision(
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var revisions = pending
            .Where(x => string.Equals(
                x.State,
                WorkReportLifecycleProjectionOutboxStates.Pending,
                StringComparison.Ordinal))
            .Where(ShouldRecover)
            .Select(x => (int?)x.LifecycleRevision);
        return revisions.Max();
    }

    public static bool ShouldRecover(WorkReportLifecycleProjectionOutboxEntry transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var crossesApprovedStatus =
            !string.Equals(transition.FromStatus, transition.ToStatus, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(
                 transition.FromStatus,
                 WorkAssignmentReportStatus.Approved.ToString(),
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(
                 transition.ToStatus,
                 WorkAssignmentReportStatus.Approved.ToString(),
                 StringComparison.OrdinalIgnoreCase));
        return crossesApprovedStatus || transition.FromIsActive != transition.ToIsActive;
    }
}

/// <summary>
/// Prevents an aggregate refresh chain from recursively entering lifecycle reconciliation.
/// Dependent lifecycle outboxes remain durable and are picked up by the recurring runner.
/// </summary>
public static class WorkReportAggregateDependentRecoveryExecution
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsActive => Depth.Value > 0;

    internal static IDisposable Enter()
    {
        Depth.Value++;
        return new RecoveryScope();
    }

    private sealed class RecoveryScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
