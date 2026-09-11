using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public sealed partial class DynamicFlowRuntimeService
{
    private async Task<IReadOnlyList<string>>
        InvalidateDynamicFlowMappingProvenanceAsync(
            IClientSessionHandle session,
            DynamicFlowInstance instance,
            string eventId,
            string operation,
            DateTime invalidatedAtUtc,
            bool canLifecycleMapping,
            CancellationToken ct)
    {
        var current = await _ctx.DynamicFlowMappingProvenanceRecords
            .Find(
                session,
                item =>
                    ((item.RuntimePin.FlowInstanceId == instance.Id &&
                      item.RuntimePin.ExecutionEpoch ==
                      instance.ExecutionEpoch) ||
                     item.SourcePins.Any(pin =>
                         pin.SourceFlowInstanceId == instance.Id &&
                         pin.SourceExecutionEpoch ==
                         instance.ExecutionEpoch)) &&
                    item.State ==
                    DynamicFlowMappingProvenanceStates.Current)
            .SortBy(item => item.Id)
            .ToListAsync(ct);
        if (current.Count == 0)
            return Array.Empty<string>();
        DynamicFlowMappingLifecycleContract.EnsureP7LifecyclePhase(
            canLifecycleMapping,
            operation,
            DynamicFlowMappingLifecycleContract
                .InvalidationBlockedReason,
            flowInstanceId: instance.Id);

        var ids = current
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var update =
            await _ctx.DynamicFlowMappingProvenanceRecords
                .UpdateManyAsync(
                    session,
                    item =>
                        ids.Contains(item.Id) &&
                        item.State ==
                        DynamicFlowMappingProvenanceStates.Current &&
                        item.SupersededByProvenanceId == null,
                    Builders<DynamicFlowMappingProvenanceRecord>.Update
                        .Set(
                            item => item.State,
                            DynamicFlowMappingProvenanceStates.Invalidated)
                        .Set(
                            item => item.InvalidatedByEventId,
                            eventId)
                        .Set(
                            item => item.InvalidationReason,
                            DynamicFlowMappingLifecycleContract
                                .EpochInvalidatedReason)
                        .Set(
                            item => item.InvalidatedAtUtc,
                            invalidatedAtUtc),
                    cancellationToken: ct);
        if (update.ModifiedCount == ids.Length)
            return ids;

        throw new InvalidOperationException(
            "DYNAMIC_FLOW_MAPPING_EPOCH_INVALIDATION_CAS_LOST");
    }
}
