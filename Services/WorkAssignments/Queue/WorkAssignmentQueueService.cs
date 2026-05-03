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
        var now = DateTime.UtcNow;
        var nextScanAt = period.DueAtUtc ?? now;
        var shouldKeepActive = period.IsActive &&
                               WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status);

        var filter = Builders<WorkAssignmentQueueItem>.Filter.And(
            Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.WorkAssignmentId, period.WorkAssignmentId),
            Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.AssigneeUserId, period.AssigneeUserId),
            Builders<WorkAssignmentQueueItem>.Filter.Eq(x => x.PeriodKey, period.PeriodKey)
        );

        var update = Builders<WorkAssignmentQueueItem>.Update
            .SetOnInsert(x => x.Id, MongoDB.Bson.ObjectId.GenerateNewId().ToString())
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, actorUserId)
            .Set(x => x.WorkId, period.WorkId)
            .Set(x => x.WorkAssignmentId, period.WorkAssignmentId)
            .Set(x => x.AssigneeUserId, period.AssigneeUserId)
            .Set(x => x.PeriodKey, period.PeriodKey)
            .Set(x => x.DueAtUtc, period.DueAtUtc)
            .Set(x => x.NextScanAtUtc, nextScanAt)
            .Set(x => x.IsActive, shouldKeepActive)
            .Set(x => x.LastObservedPeriodStatus, (int)period.Status)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

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
