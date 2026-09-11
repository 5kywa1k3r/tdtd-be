using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowFinalizeTopology(
    DynamicFlowTemplatePayloadDto Payload,
    DynamicFlowTopologyNodeDto EntryNode,
    DynamicFlowTopologyNodeDto GatewayNode,
    DynamicFlowTopologyNodeDto FinalNode,
    DynamicFlowFormNodeDto EntryForm,
    string RollbackTargetNodeId,
    string CanonicalJson,
    string TopologyHash);

public static class DynamicFlowFinalizeTopologyContract
{
    public const string ArchetypeId = "FLOW-T12";
    public const string FinalizeCommand = "EPOCH_FINALIZE";
    public const string RollbackCommand = "EPOCH_ROLLBACK";
    public const string TerminateCommand = "EPOCH_TERMINATE";
    public const string RestartCommand = "EPOCH_RESTART";
    public const string FinalizedEvent = "EXECUTION_EPOCH_FINALIZED";
    public const string RolledBackEvent = "EXECUTION_EPOCH_ROLLED_BACK";
    public const string TerminatedEvent = "EXECUTION_EPOCH_TERMINATED";
    public const string RestartedEvent = "EXECUTION_EPOCH_RESTARTED";
    public const string RebuildIntentOperation =
        "PUBLISH_EXECUTION_EPOCH_REBUILD_INTENT";
    public const string RebuildIntentPolicyVersion = "P6-EPOCH-INVALIDATION-1";

    public static DynamicFlowFinalizeTopology Require(
        string lockedDefinitionJson,
        string expectedHash)
    {
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                lockedDefinitionJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        if (!FixedEquals(canonical.PayloadHash, expectedHash))
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_TOPOLOGY_SNAPSHOT_HASH_MISMATCH");

        var payload = canonical.Payload;
        if (payload.ArchetypeId != ArchetypeId ||
            payload.Nodes.Count != 3 ||
            payload.Edges.Count != 2 ||
            payload.FormNodes.Count != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_FINALIZE_TOPOLOGY_INVALID");
        }

        var entry = payload.Nodes.SingleOrDefault(node =>
            node.NodeKind == DynamicFlowNodeKinds.FormStep);
        var gateway = payload.Nodes.SingleOrDefault(node =>
            node.NodeKind == DynamicFlowNodeKinds.Gateway);
        var final = payload.Nodes.SingleOrDefault(node =>
            node.NodeKind == DynamicFlowNodeKinds.Final);
        if (entry is null || gateway is null || final is null)
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_FINALIZE_TOPOLOGY_INVALID");

        var definition = gateway.Gateway;
        var first = payload.Edges.SingleOrDefault(edge =>
            edge.FromNodeId == entry.NodeId &&
            edge.ToNodeId == gateway.NodeId);
        var second = payload.Edges.SingleOrDefault(edge =>
            edge.FromNodeId == gateway.NodeId &&
            edge.ToNodeId == final.NodeId);
        if (payload.EntryStepId != entry.NodeId ||
            entry.Gateway is not null ||
            string.IsNullOrWhiteSpace(entry.FormNodeId) ||
            definition?.Kind != DynamicFlowGatewayKinds.RollbackFinalize ||
            definition.RollbackTargetNodeId != entry.NodeId ||
            first is null ||
            second is null ||
            first.Condition is not null ||
            second.Condition is not null ||
            definition.ExpectedIncomingNodeIds.Count != 0 ||
            definition.RequiredIncomingCount is not null ||
            definition.ReviewRole is not null ||
            definition.SubflowFamilyId is not null ||
            definition.SubflowVersionId is not null ||
            definition.ScheduleKey is not null)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_FINALIZE_GATEWAY_INVALID");
        }

        var form = payload.FormNodes.Single();
        if (form.FormNodeId != entry.FormNodeId ||
            !ObjectId.TryParse(form.DynamicFormTemplateId, out _) ||
            !ObjectId.TryParse(form.DynamicFormFamilyId, out _) ||
            form.DynamicFormVersionNo is null or < 1 ||
            !IsSha256(form.DynamicFormSchemaHash) ||
            !IsSha256(form.DynamicFormSnapshotHash))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_FINALIZE_FORM_PIN_INVALID");
        }

        return new DynamicFlowFinalizeTopology(
            payload,
            entry,
            gateway,
            final,
            form,
            entry.NodeId,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static string BuildEpochId(string instanceId, int executionEpoch)
        => StableObjectId($"{instanceId}\n{executionEpoch}\nexecution-epoch");

    public static string BuildBranchId(
        string instanceId,
        int executionEpoch,
        string targetUnitId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{targetUnitId}\nbranch");

    public static string BuildStepInstanceId(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string branchId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{nodeId}\n{branchId}\nstep");

    public static string BuildAssignmentId(string stepInstanceId)
        => StableObjectId($"{stepInstanceId}\nassignment");

    public static string BuildOwnerIdentity(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string branchId,
        string ownerKind)
        => $"{ownerKind}:{instanceId}:{executionEpoch}:{nodeId}:{branchId}";

    public static string BuildEventId(
        string instanceId,
        int executionEpoch,
        string commandType,
        string commandId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{commandType}\n{commandId.Trim()}\nevent");

    public static string BuildRebuildIntentId(
        string instanceId,
        int invalidatedEpoch,
        int? replacementEpoch,
        string eventId)
        => StableObjectId(
            $"{instanceId}\n{invalidatedEpoch}\n{replacementEpoch}\n{eventId}\nrebuild-intent");

    private static string StableObjectId(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new ObjectId(bytes[..12]).ToString();
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' or
                   >= 'a' and <= 'f');

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
