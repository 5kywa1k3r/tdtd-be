using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.WorkAssignments.Queue;

public sealed class WorkAssignmentQueueService : IWorkAssignmentQueueService
{
    private readonly MongoDbContext _ctx;

    public WorkAssignmentQueueService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task UpsertPeriodAsync(WorkReportPeriod period, string actorUserId, CancellationToken ct = default)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var canonicalPeriod = await _ctx.WorkReportPeriods
                .Find(x => x.Id == period.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (canonicalPeriod is null)
            {
                await DisableByPeriodAsync(
                    period.WorkAssignmentId,
                    period.AssigneeUserId,
                    period.PeriodKey,
                    actorUserId,
                    ct);
                return;
            }
            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == canonicalPeriod.WorkAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var now = DateTime.UtcNow;
            var nextScanAt = canonicalPeriod.DueAtUtc ?? now;
            var shouldKeepActive =
                assignment is { IsActive: true, CompletedAtUtc: null } &&
                canonicalPeriod.IsActive &&
                !canonicalPeriod.IsHistoricalData &&
                WorkReportPeriodStatusHelper.ShouldKeepQueueActive(
                    canonicalPeriod.Status);

            var filter = Builders<WorkAssignmentQueueItem>.Filter.And(
                Builders<WorkAssignmentQueueItem>.Filter.Eq(
                    x => x.WorkAssignmentId,
                    canonicalPeriod.WorkAssignmentId),
                Builders<WorkAssignmentQueueItem>.Filter.Eq(
                    x => x.AssigneeUserId,
                    canonicalPeriod.AssigneeUserId),
                Builders<WorkAssignmentQueueItem>.Filter.Eq(
                    x => x.PeriodKey,
                    canonicalPeriod.PeriodKey),
                Builders<WorkAssignmentQueueItem>.Filter.Eq(
                    x => x.IsDeleted,
                    false));

            var update = Builders<WorkAssignmentQueueItem>.Update
                .SetOnInsert(x => x.Id, MongoDB.Bson.ObjectId.GenerateNewId().ToString())
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .SetOnInsert(x => x.CreatedByUserId, actorUserId)
                .Set(x => x.WorkId, canonicalPeriod.WorkId)
                .Set(x => x.WorkAssignmentId, canonicalPeriod.WorkAssignmentId)
                .Set(x => x.AssigneeUserId, canonicalPeriod.AssigneeUserId)
                .Set(x => x.PeriodKey, canonicalPeriod.PeriodKey)
                .Set(x => x.DueAtUtc, canonicalPeriod.DueAtUtc)
                .Set(x => x.NextScanAtUtc, nextScanAt)
                .Set(x => x.IsActive, shouldKeepActive)
                .Set(x => x.IsDeleted, false)
                .Set(x => x.LastObservedPeriodStatus, (int)canonicalPeriod.Status)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId);

            await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                ct);

            var observedPeriod = await _ctx.WorkReportPeriods
                .Find(x => x.Id == canonicalPeriod.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var observedAssignment = await _ctx.WorkAssignments
                .Find(x => x.Id == canonicalPeriod.WorkAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (SameQueueSource(canonicalPeriod, observedPeriod) &&
                SameQueueAssignmentSource(assignment, observedAssignment))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "WORK_ASSIGNMENT_QUEUE_CANONICAL_SOURCE_DID_NOT_CONVERGE");
    }

    private static bool SameQueueSource(
        WorkReportPeriod expected,
        WorkReportPeriod? observed)
        => observed is not null &&
           expected.UpdatedAtUtc == observed.UpdatedAtUtc &&
           expected.IsActive == observed.IsActive &&
           expected.IsHistoricalData == observed.IsHistoricalData &&
           expected.Status == observed.Status &&
           expected.DueAtUtc == observed.DueAtUtc &&
           expected.CurrentReportId == observed.CurrentReportId &&
           expected.SourceLifecycleReportId == observed.SourceLifecycleReportId &&
           expected.SourceLifecycleRevision == observed.SourceLifecycleRevision;

    private static bool SameQueueAssignmentSource(
        WorkAssignment? expected,
        WorkAssignment? observed)
        => expected?.Id == observed?.Id &&
           expected?.IsActive == observed?.IsActive &&
           expected?.CompletedAtUtc == observed?.CompletedAtUtc &&
           expected?.DynamicFlowMaterializationRevision ==
           observed?.DynamicFlowMaterializationRevision;

    public async Task DisableByPeriodAsync(string workAssignmentId, string assigneeUserId, string periodKey, string actorUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
            x => x.WorkAssignmentId == workAssignmentId &&
                 x.AssigneeUserId == assigneeUserId &&
                 x.PeriodKey == periodKey &&
                 !x.IsDeleted,
            Builders<WorkAssignmentQueueItem>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    public async Task DisableByAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
            x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted && x.IsActive,
            Builders<WorkAssignmentQueueItem>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }
}
