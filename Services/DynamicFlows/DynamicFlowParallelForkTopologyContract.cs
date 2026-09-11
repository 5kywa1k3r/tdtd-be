using System.Security.Cryptography;
using System.Text;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowForkBranch(
    DynamicFlowTopologyEdgeDto Edge,
    DynamicFlowTopologyNodeDto Node,
    DynamicFlowFormNodeDto Form,
    int StepOrder);

public sealed record DynamicFlowParallelForkTopology(
    DynamicFlowTemplatePayloadDto Payload,
    DynamicFlowTopologyNodeDto EntryNode,
    DynamicFlowFormNodeDto EntryForm,
    DynamicFlowTopologyEdgeDto EntryToForkEdge,
    DynamicFlowTopologyNodeDto ForkNode,
    IReadOnlyList<DynamicFlowForkBranch> Branches,
    string CanonicalJson,
    string TopologyHash);

/// <summary>
/// Frozen P6-02 execution contract for FLOW-T04. T04 is one exact form entry
/// followed by one exact FORK gateway and two terminal form branches. It is
/// intentionally not a JOIN contract; P6-03 owns join semantics.
/// </summary>
public static class DynamicFlowParallelForkTopologyContract
{
    public const string ArchetypeId = "FLOW-T04";
    public const int InitialExecutionEpoch = 1;

    public static DynamicFlowParallelForkTopology Require(
        string canonicalDefinitionJson,
        string expectedPayloadHash)
    {
        if (!IsSha256(expectedPayloadHash))
            throw new InvalidOperationException("DYNAMIC_FLOW_TOPOLOGY_HASH_INVALID");

        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            canonicalDefinitionJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        if (!FixedEquals(canonical.PayloadHash, expectedPayloadHash))
            throw new InvalidOperationException("DYNAMIC_FLOW_TOPOLOGY_PIN_STALE");
        if (!string.Equals(canonical.Payload.ArchetypeId, ArchetypeId, StringComparison.Ordinal))
            throw new InvalidOperationException("DYNAMIC_FLOW_ARCHETYPE_BLOCKED_UNTIL_TARGET_PROMPT");

        var payload = canonical.Payload;
        if (payload.Nodes.Count != 4 ||
            payload.Edges.Count != 3 ||
            payload.Nodes.Count(node => node.NodeKind == DynamicFlowNodeKinds.FormStep) != 3 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.Gateway &&
                node.Gateway?.Kind == DynamicFlowGatewayKinds.Fork) != 1 ||
            payload.Nodes.Any(node => node.NodeKind == DynamicFlowNodeKinds.Final))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T04_PARALLEL_FORK_TOPOLOGY_REQUIRED");
        }

        var entry = payload.Nodes.SingleOrDefault(node =>
                        string.Equals(node.NodeId, payload.EntryStepId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("DYNAMIC_FLOW_T04_ENTRY_PIN_MISSING");
        var fork = payload.Nodes.Single(node => node.NodeKind == DynamicFlowNodeKinds.Gateway);
        if (entry.NodeKind != DynamicFlowNodeKinds.FormStep ||
            !string.Equals(entry.NodeCode, "A", StringComparison.Ordinal) ||
            !string.Equals(fork.NodeCode, "F", StringComparison.Ordinal) ||
            fork.Gateway?.ExpectedIncomingNodeIds.Count > 0 ||
            fork.Gateway?.RequiredIncomingCount is not null)
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T04_PARALLEL_FORK_TOPOLOGY_REQUIRED");
        }

        var incoming = payload.Nodes.ToDictionary(node => node.NodeId, _ => 0, StringComparer.Ordinal);
        var outgoing = payload.Nodes.ToDictionary(
            node => node.NodeId,
            _ => new List<DynamicFlowTopologyEdgeDto>(),
            StringComparer.Ordinal);
        foreach (var edge in payload.Edges)
        {
            incoming[edge.ToNodeId]++;
            outgoing[edge.FromNodeId].Add(edge);
        }

        var entryEdges = outgoing[entry.NodeId];
        var forkEdges = outgoing[fork.NodeId]
            .OrderBy(edge => edge.TransitionId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal)
            .ToList();
        var branchNodes = forkEdges
            .Select(edge => payload.Nodes.Single(node =>
                string.Equals(node.NodeId, edge.ToNodeId, StringComparison.Ordinal)))
            .ToList();
        if (incoming[entry.NodeId] != 0 ||
            entryEdges.Count != 1 ||
            !string.Equals(entryEdges[0].ToNodeId, fork.NodeId, StringComparison.Ordinal) ||
            incoming[fork.NodeId] != 1 ||
            forkEdges.Count != 2 ||
            branchNodes.DistinctBy(node => node.NodeId).Count() != 2 ||
            branchNodes.Any(node =>
                node.NodeKind != DynamicFlowNodeKinds.FormStep ||
                incoming[node.NodeId] != 1 ||
                outgoing[node.NodeId].Count != 0) ||
            payload.Nodes.Any(node =>
                !string.Equals(node.NodeId, entry.NodeId, StringComparison.Ordinal) &&
                !string.Equals(node.NodeId, fork.NodeId, StringComparison.Ordinal) &&
                branchNodes.All(branch => branch.NodeId != node.NodeId)))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T04_PARALLEL_FORK_TOPOLOGY_REQUIRED");
        }

        var orderedBranchNodes = branchNodes
            .OrderBy(node => node.NodeCode, StringComparer.Ordinal)
            .ToList();
        if (!orderedBranchNodes.Select(node => node.NodeCode).SequenceEqual(
                new[] { "B", "C" },
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T04_PARALLEL_FORK_TOPOLOGY_REQUIRED");
        }

        var entryForm = RequireForm(payload, entry);
        var branches = orderedBranchNodes
            .Select((node, index) =>
            {
                var edge = forkEdges.Single(item =>
                    string.Equals(item.ToNodeId, node.NodeId, StringComparison.Ordinal));
                if (edge.Condition is not null)
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_T04_UNCONDITIONAL_FORK_REQUIRED");
                return new DynamicFlowForkBranch(
                    edge,
                    node,
                    RequireForm(payload, node),
                    index + 2);
            })
            .ToList();
        if (entryEdges[0].Condition is not null)
            throw new InvalidOperationException("DYNAMIC_FLOW_T04_UNCONDITIONAL_FORK_REQUIRED");

        return new DynamicFlowParallelForkTopology(
            payload,
            entry,
            entryForm,
            entryEdges[0],
            fork,
            branches,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static string BuildRootBranchId(
        string instanceId,
        int executionEpoch,
        string targetUnitId)
        => StableObjectId($"{instanceId}\n{executionEpoch}\n{targetUnitId}\nroot-branch");

    public static string BuildGatewayInstanceId(
        string instanceId,
        int executionEpoch,
        string gatewayNodeId,
        string rootBranchId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{gatewayNodeId}\n{rootBranchId}\ngateway");

    public static string BuildBranchId(
        string instanceId,
        int executionEpoch,
        string gatewayInstanceId,
        string transitionId,
        string targetUnitId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{gatewayInstanceId}\n{transitionId}\n{targetUnitId}\nbranch");

    public static string BuildContributionId(
        string gatewayInstanceId,
        string branchId,
        string transitionId)
        => StableObjectId(
            $"{gatewayInstanceId}\n{branchId}\n{transitionId}\ncontribution");

    public static string BuildStepInstanceId(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string branchId,
        int attemptNo)
        => DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
            instanceId,
            executionEpoch,
            nodeId,
            branchId,
            attemptNo);

    public static string BuildAssignmentId(string stepInstanceId)
        => DynamicFlowSequentialTopologyContract.BuildAssignmentId(stepInstanceId);

    public static string BuildStepOwnerIdentity(
        string instanceId,
        int executionEpoch,
        string ownerNodeId,
        string branchId,
        string ownerKind)
        => DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
            instanceId,
            executionEpoch,
            ownerNodeId,
            branchId,
            ownerKind);

    public static string BuildForwardReceiptId(string instanceId, string commandId)
        => DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(instanceId, commandId);

    public static string Hash(string value)
        => DynamicFlowSequentialTopologyContract.Hash(value);

    private static DynamicFlowFormNodeDto RequireForm(
        DynamicFlowTemplatePayloadDto payload,
        DynamicFlowTopologyNodeDto node)
    {
        var formNodeId = node.FormNodeId
                         ?? throw new InvalidOperationException(
                             "DYNAMIC_FLOW_T04_FORM_PIN_MISSING");
        return payload.FormNodes.SingleOrDefault(form =>
                   string.Equals(form.FormNodeId, formNodeId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException("DYNAMIC_FLOW_T04_FORM_PIN_MISSING");
    }

    private static string StableObjectId(string seed)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant()[..24];

    private static bool IsSha256(string? value)
        => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
