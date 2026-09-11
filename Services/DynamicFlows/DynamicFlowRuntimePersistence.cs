using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowRuntimePersistence
{
    Task InsertInstanceAsync(IClientSessionHandle session, DynamicFlowInstance instance, CancellationToken ct);
    Task InsertStepAsync(IClientSessionHandle session, DynamicFlowStepInstance step, CancellationToken ct);
    Task InsertParticipantSnapshotAsync(
        IClientSessionHandle session,
        DynamicFlowParticipantSnapshot snapshot,
        CancellationToken ct);
    Task InsertReceiptAsync(
        IClientSessionHandle session,
        DynamicFlowRuntimeCommandReceipt receipt,
        CancellationToken ct);
    Task AppendEventAsync(IClientSessionHandle session, DynamicFlowRuntimeEvent flowEvent, CancellationToken ct);
    Task EnqueueAsync(IClientSessionHandle session, DynamicFlowRuntimeOutboxItem item, CancellationToken ct);
    Task<bool> TryAdvanceInstanceAsync(
        string instanceId,
        long expectedRevision,
        string expectedState,
        string nextState,
        CancellationToken ct);
    Task<bool> TryAdvanceStepAsync(
        string stepInstanceId,
        long expectedRevision,
        string expectedState,
        string nextState,
        CancellationToken ct);
}

public sealed class DynamicFlowRuntimePersistence : IDynamicFlowRuntimePersistence
{
    private readonly MongoDbContext _ctx;

    public DynamicFlowRuntimePersistence(MongoDbContext ctx) => _ctx = ctx;

    public Task InsertInstanceAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        CancellationToken ct)
        => _ctx.DynamicFlowInstances.InsertOneAsync(session, instance, cancellationToken: ct);

    public Task InsertStepAsync(
        IClientSessionHandle session,
        DynamicFlowStepInstance step,
        CancellationToken ct)
        => _ctx.DynamicFlowStepInstances.InsertOneAsync(session, step, cancellationToken: ct);

    public Task InsertParticipantSnapshotAsync(
        IClientSessionHandle session,
        DynamicFlowParticipantSnapshot snapshot,
        CancellationToken ct)
        => _ctx.DynamicFlowParticipantSnapshots.InsertOneAsync(session, snapshot, cancellationToken: ct);

    public Task InsertReceiptAsync(
        IClientSessionHandle session,
        DynamicFlowRuntimeCommandReceipt receipt,
        CancellationToken ct)
        => _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(session, receipt, cancellationToken: ct);

    public Task AppendEventAsync(
        IClientSessionHandle session,
        DynamicFlowRuntimeEvent flowEvent,
        CancellationToken ct)
        => _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(session, flowEvent, cancellationToken: ct);

    public Task EnqueueAsync(
        IClientSessionHandle session,
        DynamicFlowRuntimeOutboxItem item,
        CancellationToken ct)
        => _ctx.DynamicFlowRuntimeOutbox.InsertOneAsync(session, item, cancellationToken: ct);

    public async Task<bool> TryAdvanceInstanceAsync(
        string instanceId,
        long expectedRevision,
        string expectedState,
        string nextState,
        CancellationToken ct)
    {
        DynamicFlowRuntimeStateContract.RequireInstanceTransition(expectedState, nextState);
        var result = await _ctx.DynamicFlowInstances.UpdateOneAsync(
            DynamicFlowRuntimeRevisionContract.InstanceCas(instanceId, expectedRevision, expectedState),
            Builders<DynamicFlowInstance>.Update
                .Set(x => x.State, nextState)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Inc(x => x.Revision, 1),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> TryAdvanceStepAsync(
        string stepInstanceId,
        long expectedRevision,
        string expectedState,
        string nextState,
        CancellationToken ct)
    {
        DynamicFlowRuntimeStateContract.RequireStepTransition(expectedState, nextState);
        var result = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
            DynamicFlowRuntimeRevisionContract.StepCas(stepInstanceId, expectedRevision, expectedState),
            Builders<DynamicFlowStepInstance>.Update
                .Set(x => x.State, nextState)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Inc(x => x.Revision, 1),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}

public static class DynamicFlowRuntimeRevisionContract
{
    public static FilterDefinition<DynamicFlowInstance> InstanceCas(
        string instanceId,
        long expectedRevision,
        string expectedState)
    {
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!DynamicFlowInstanceStates.All.Contains(expectedState))
            throw new ArgumentException("DYNAMIC_FLOW_INSTANCE_STATE_INVALID", nameof(expectedState));
        var fb = Builders<DynamicFlowInstance>.Filter;
        var revision = expectedRevision == 0
            ? fb.Eq(x => x.Revision, 0) | fb.Exists(x => x.Revision, false)
            : fb.Eq(x => x.Revision, expectedRevision);
        return fb.Eq(x => x.Id, instanceId) &
               fb.Eq(x => x.State, expectedState) &
               fb.Eq(x => x.IsDeleted, false) &
               revision;
    }

    public static FilterDefinition<DynamicFlowStepInstance> StepCas(
        string stepInstanceId,
        long expectedRevision,
        string expectedState)
    {
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!DynamicFlowStepStates.All.Contains(expectedState))
            throw new ArgumentException("DYNAMIC_FLOW_STEP_STATE_INVALID", nameof(expectedState));
        var fb = Builders<DynamicFlowStepInstance>.Filter;
        var revision = expectedRevision == 0
            ? fb.Eq(x => x.Revision, 0) | fb.Exists(x => x.Revision, false)
            : fb.Eq(x => x.Revision, expectedRevision);
        return fb.Eq(x => x.Id, stepInstanceId) &
               fb.Eq(x => x.State, expectedState) &
               fb.Eq(x => x.IsDeleted, false) &
               revision;
    }
}
