using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Common;

public interface IDocRoleReadModelFreshnessService
{
    Task EnsureWorkFreshAsync(Work work, string actorUserId, CancellationToken ct = default);

    Task EnsureAssignmentFreshAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default);

    Task EnsureReportPeriodFreshAsync(
        WorkReportPeriod? period,
        WorkAssignmentReport? report,
        string actorUserId,
        CancellationToken ct = default);
}

public sealed class DocRoleReadModelFreshnessService : IDocRoleReadModelFreshnessService
{
    private static readonly ConcurrentDictionary<string, DateTime> _localGateUntilUtc = new(StringComparer.Ordinal);
    private static readonly TimeSpan _localGateTtl = TimeSpan.FromSeconds(30);

    private readonly MongoDbContext _ctx;
    private readonly IDocRoleReadModelProjectionRetryJobService _retryJobs;
    private readonly ILogger<DocRoleReadModelFreshnessService> _log;

    public DocRoleReadModelFreshnessService(
        MongoDbContext ctx,
        IDocRoleReadModelProjectionRetryJobService retryJobs,
        ILogger<DocRoleReadModelFreshnessService> log)
    {
        _ctx = ctx;
        _retryJobs = retryJobs;
        _log = log;
    }

    public async Task EnsureWorkFreshAsync(Work work, string actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(work?.Id) || string.IsNullOrWhiteSpace(actorUserId))
            return;

        try
        {
            var projectionUpdatedAt = await _ctx.WorkListDocRoles
                .Find(x => x.WorkId == work.Id && x.UserId == actorUserId && !x.IsDeleted)
                .Project(x => (DateTime?)x.UpdatedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (IsFresh(projectionUpdatedAt, work.UpdatedAtUtc))
                return;

            var reason = projectionUpdatedAt.HasValue ? "stale" : "missing";
            var gateKey = $"work:{work.Id}:{actorUserId}";
            if (!TryEnterLocalGate(gateKey))
            {
                _log.LogDebug(
                    "DocRole read-model freshness skipped by local gate. scope=work workId={workId} actorUserId={actorUserId}",
                    work.Id,
                    actorUserId);
                return;
            }

            await _retryJobs.EnqueueRebuildWorkAsync(
                work.Id,
                actorUserId,
                $"read-model-{reason}",
                CreateQueuedFreshnessException("work", work.Id, reason),
                CancellationToken.None);

            _log.LogInformation(
                "DocRole read-model freshness repair queued. scope=work reason={reason} workId={workId} actorUserId={actorUserId} sourceUpdatedAtUtc={sourceUpdatedAtUtc} projectionUpdatedAtUtc={projectionUpdatedAtUtc}",
                reason,
                work.Id,
                actorUserId,
                work.UpdatedAtUtc,
                projectionUpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "DocRole read-model freshness repair enqueue failed. scope=work workId={workId} actorUserId={actorUserId}",
                work.Id,
                actorUserId);
        }
    }

    public async Task EnsureAssignmentFreshAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assignment?.Id) || string.IsNullOrWhiteSpace(actorUserId))
            return;

        try
        {
            var projectionUpdatedAt = await _ctx.AssignmentListDocRoles
                .Find(x => x.AssignmentId == assignment.Id && x.UserId == actorUserId && !x.IsDeleted)
                .Project(x => (DateTime?)x.UpdatedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (IsFresh(projectionUpdatedAt, assignment.UpdatedAtUtc))
                return;

            var reason = projectionUpdatedAt.HasValue ? "stale" : "missing";
            var gateKey = $"assignment:{assignment.Id}:{actorUserId}";
            if (!TryEnterLocalGate(gateKey))
            {
                _log.LogDebug(
                    "DocRole read-model freshness skipped by local gate. scope=assignment assignmentId={assignmentId} actorUserId={actorUserId}",
                    assignment.Id,
                    actorUserId);
                return;
            }

            await _retryJobs.EnqueueRebuildAssignmentAsync(
                assignment.Id,
                actorUserId,
                $"read-model-{reason}",
                CreateQueuedFreshnessException("assignment", assignment.Id, reason),
                CancellationToken.None);

            _log.LogInformation(
                "DocRole read-model freshness repair queued. scope=assignment reason={reason} assignmentId={assignmentId} actorUserId={actorUserId} sourceUpdatedAtUtc={sourceUpdatedAtUtc} projectionUpdatedAtUtc={projectionUpdatedAtUtc}",
                reason,
                assignment.Id,
                actorUserId,
                assignment.UpdatedAtUtc,
                projectionUpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "DocRole read-model freshness repair enqueue failed. scope=assignment assignmentId={assignmentId} actorUserId={actorUserId}",
                assignment.Id,
                actorUserId);
        }
    }

    public async Task EnsureReportPeriodFreshAsync(
        WorkReportPeriod? period,
        WorkAssignmentReport? report,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (period is null || string.IsNullOrWhiteSpace(period.Id) || string.IsNullOrWhiteSpace(actorUserId))
            return;

        try
        {
            var sourceUpdatedAt = MaxDate(period.UpdatedAtUtc, report?.UpdatedAtUtc);
            var myReportUpdatedAt = await _ctx.MyReportPeriodListDocRoles
                .Find(x => x.WorkReportPeriodId == period.Id && x.UserId == actorUserId && !x.IsDeleted)
                .Project(x => (DateTime?)x.UpdatedAtUtc)
                .FirstOrDefaultAsync(ct);

            var reviewUpdatedAt = await _ctx.ReviewReportListDocRoles
                .Find(x => x.WorkReportPeriodId == period.Id && x.ReviewerUserId == actorUserId && !x.IsDeleted)
                .Project(x => (DateTime?)x.UpdatedAtUtc)
                .FirstOrDefaultAsync(ct);

            var projectionUpdatedAt = MaxNullableDate(myReportUpdatedAt, reviewUpdatedAt);
            if (IsFresh(projectionUpdatedAt, sourceUpdatedAt))
                return;

            var reason = projectionUpdatedAt.HasValue ? "stale" : "missing";
            var gateKey = $"period:{period.Id}:{actorUserId}";
            if (!TryEnterLocalGate(gateKey))
            {
                _log.LogDebug(
                    "DocRole read-model freshness skipped by local gate. scope=period periodId={periodId} actorUserId={actorUserId}",
                    period.Id,
                    actorUserId);
                return;
            }

            await _retryJobs.EnqueueRebuildReportPeriodAsync(
                period.Id,
                actorUserId,
                $"read-model-{reason}",
                CreateQueuedFreshnessException("period", period.Id, reason),
                CancellationToken.None);

            _log.LogInformation(
                "DocRole read-model freshness repair queued. scope=period reason={reason} periodId={periodId} reportId={reportId} actorUserId={actorUserId} sourceUpdatedAtUtc={sourceUpdatedAtUtc} projectionUpdatedAtUtc={projectionUpdatedAtUtc}",
                reason,
                period.Id,
                report?.Id,
                actorUserId,
                sourceUpdatedAt,
                projectionUpdatedAt);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "DocRole read-model freshness repair enqueue failed. scope=period periodId={periodId} reportId={reportId} actorUserId={actorUserId}",
                period.Id,
                report?.Id,
                actorUserId);
        }
    }

    private static Exception CreateQueuedFreshnessException(string scope, string targetId, string reason)
        => new InvalidOperationException(
            $"DocRole read-model freshness repair queued. scope={scope};targetId={targetId};reason={reason}");

    private static bool IsFresh(DateTime? projectionUpdatedAtUtc, DateTime sourceUpdatedAtUtc)
        => projectionUpdatedAtUtc.HasValue && projectionUpdatedAtUtc.Value >= sourceUpdatedAtUtc;

    private static DateTime MaxDate(DateTime left, DateTime? right)
        => right.HasValue && right.Value > left ? right.Value : left;

    private static DateTime? MaxNullableDate(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;

        if (!right.HasValue)
            return left;

        return left.Value >= right.Value ? left : right;
    }

    private static bool TryEnterLocalGate(string key)
    {
        var now = DateTime.UtcNow;
        if (_localGateUntilUtc.TryGetValue(key, out var untilUtc) && untilUtc > now)
            return false;

        _localGateUntilUtc[key] = now.Add(_localGateTtl);
        return true;
    }
}
