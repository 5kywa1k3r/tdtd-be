using System.Security.Cryptography;
using System.Text;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

/// <summary>
/// Frozen P6-03 execution contract for FLOW-T05. The executable work topology is
/// one entry form that activates exactly two contribution forms. Both
/// contributions feed one versioned JOIN_ALL gateway whose sole downstream node
/// is FINAL. JOIN_ANY and JOIN_N_OF_M remain owned by P6-04.
/// </summary>
public static class DynamicFlowJoinAllTopologyContract
{
    public const string ArchetypeId = "FLOW-T05";
    public const int GatewayVersion = 1;

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
        if (!string.Equals(
                canonical.Payload.ArchetypeId,
                ArchetypeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_ARCHETYPE_BLOCKED_UNTIL_TARGET_PROMPT");
        }

        var payload = canonical.Payload;
        if (payload.Nodes.Count != 5 ||
            payload.Edges.Count != 5 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.FormStep) != 3 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.Gateway &&
                node.Gateway?.Kind == DynamicFlowGatewayKinds.JoinAll) != 1 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.Final) != 1 ||
            payload.Nodes.Any(node =>
                node.NodeKind == DynamicFlowNodeKinds.Gateway &&
                node.Gateway?.Kind is
                    DynamicFlowGatewayKinds.JoinAny or
                    DynamicFlowGatewayKinds.JoinNOfM or
                    DynamicFlowGatewayKinds.Fork))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T05_JOIN_ALL_TOPOLOGY_REQUIRED");
        }

        var entry = payload.Nodes.SingleOrDefault(node =>
                        string.Equals(
                            node.NodeId,
                            payload.EntryStepId,
                            StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        "DYNAMIC_FLOW_T05_ENTRY_PIN_MISSING");
        var join = payload.Nodes.Single(node =>
            node.NodeKind == DynamicFlowNodeKinds.Gateway);
        var final = payload.Nodes.Single(node =>
            node.NodeKind == DynamicFlowNodeKinds.Final);
        if (entry.NodeKind != DynamicFlowNodeKinds.FormStep ||
            join.Gateway is null ||
            join.Gateway.RequiredIncomingCount is not null)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T05_JOIN_ALL_TOPOLOGY_REQUIRED");
        }

        var incoming = payload.Nodes.ToDictionary(
            node => node.NodeId,
            _ => 0,
            StringComparer.Ordinal);
        var outgoing = payload.Nodes.ToDictionary(
            node => node.NodeId,
            _ => new List<DynamicFlowTopologyEdgeDto>(),
            StringComparer.Ordinal);
        foreach (var edge in payload.Edges)
        {
            incoming[edge.ToNodeId]++;
            outgoing[edge.FromNodeId].Add(edge);
        }

        var fanOutEdges = outgoing[entry.NodeId]
            .OrderBy(edge => edge.TransitionId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal)
            .ToArray();
        var branches = fanOutEdges
            .Select(edge => payload.Nodes.Single(node =>
                string.Equals(node.NodeId, edge.ToNodeId, StringComparison.Ordinal)))
            .OrderBy(node => node.NodeCode, StringComparer.Ordinal)
            .ToArray();
        var expectedIncoming = join.Gateway.ExpectedIncomingNodeIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var branchNodeIds = branches
            .Select(node => node.NodeId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var joinToFinal = outgoing[join.NodeId].SingleOrDefault();
        if (incoming[entry.NodeId] != 0 ||
            fanOutEdges.Length != 2 ||
            fanOutEdges.Any(edge => edge.Condition is not null) ||
            branches.Length != 2 ||
            branches.DistinctBy(node => node.NodeId).Count() != 2 ||
            branches.Any(node =>
                node.NodeKind != DynamicFlowNodeKinds.FormStep ||
                incoming[node.NodeId] != 1 ||
                outgoing[node.NodeId].Count != 1 ||
                !string.Equals(
                    outgoing[node.NodeId][0].ToNodeId,
                    join.NodeId,
                    StringComparison.Ordinal) ||
                outgoing[node.NodeId][0].Condition is not null) ||
            incoming[join.NodeId] != 2 ||
            expectedIncoming.Length != 2 ||
            !expectedIncoming.SequenceEqual(branchNodeIds, StringComparer.Ordinal) ||
            joinToFinal is null ||
            joinToFinal.Condition is not null ||
            !string.Equals(
                joinToFinal.ToNodeId,
                final.NodeId,
                StringComparison.Ordinal) ||
            incoming[final.NodeId] != 1 ||
            outgoing[final.NodeId].Count != 0)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T05_JOIN_ALL_TOPOLOGY_REQUIRED");
        }

        var entryForm = RequireForm(payload, entry);
        var runtimeBranches = branches
            .Select((node, index) =>
            {
                var edge = fanOutEdges.Single(item =>
                    string.Equals(
                        item.ToNodeId,
                        node.NodeId,
                        StringComparison.Ordinal));
                return new DynamicFlowForkBranch(
                    edge,
                    node,
                    RequireForm(payload, node),
                    index + 2);
            })
            .ToArray();

        // The shared fan-out runtime record names this field EntryToForkEdge.
        // FLOW-T05 has no FORK node, so this deterministic controller edge is a
        // runtime-only identity and is never written back to the definition.
        var controllerEdge = new DynamicFlowTopologyEdgeDto
        {
            TransitionId = $"join-all-controller:{entry.NodeId}:{join.NodeId}",
            FromNodeId = entry.NodeId,
            ToNodeId = join.NodeId
        };
        return new DynamicFlowParallelForkTopology(
            payload,
            entry,
            entryForm,
            controllerEdge,
            join,
            runtimeBranches,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static string BuildGatewayInstanceId(
        string instanceId,
        int executionEpoch,
        string gatewayNodeId,
        string rootBranchId)
        => DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
            instanceId,
            executionEpoch,
            gatewayNodeId,
            rootBranchId);

    public static string BuildGatewayLedgerId(
        string gatewayInstanceId,
        int gatewayVersion)
        => StableObjectId($"{gatewayInstanceId}\n{gatewayVersion}\njoin-gateway-ledger");

    public static string BuildContributionLedgerId(
        string gatewayInstanceId,
        int gatewayVersion,
        string contributionId)
        => StableObjectId(
            $"{gatewayInstanceId}\n{gatewayVersion}\n{contributionId}\njoin-contribution-ledger");

    public static string BuildReleaseEventId(
        string gatewayInstanceId,
        int gatewayVersion)
        => StableObjectId(
            $"{gatewayInstanceId}\n{gatewayVersion}\njoin-all-satisfied");

    public static string BuildContributionEventId(
        string gatewayInstanceId,
        int gatewayVersion,
        string contributionId)
        => StableObjectId(
            $"{gatewayInstanceId}\n{gatewayVersion}\n{contributionId}\njoin-contribution-accepted");

    private static DynamicFlowFormNodeDto RequireForm(
        DynamicFlowTemplatePayloadDto payload,
        DynamicFlowTopologyNodeDto node)
    {
        var formNodeId = node.FormNodeId
                         ?? throw new InvalidOperationException(
                             "DYNAMIC_FLOW_T05_FORM_PIN_MISSING");
        return payload.FormNodes.SingleOrDefault(form =>
                   string.Equals(
                       form.FormNodeId,
                       formNodeId,
                       StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   "DYNAMIC_FLOW_T05_FORM_PIN_MISSING");
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
