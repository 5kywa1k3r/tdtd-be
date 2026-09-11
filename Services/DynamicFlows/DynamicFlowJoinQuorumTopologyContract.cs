using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

/// <summary>
/// Frozen P6-04 execution contract for FLOW-T06. The executable shape is the
/// same immutable two-contributor fan-in used by T05, but the gateway is
/// JOIN_ANY or JOIN_N_OF_M and owns an explicit quorum.
/// </summary>
public static class DynamicFlowJoinQuorumTopologyContract
{
    public const string ArchetypeId = "FLOW-T06";
    public const int GatewayVersion = 1;

    public static DynamicFlowParallelForkTopology Require(
        string canonicalDefinitionJson,
        string expectedPayloadHash)
    {
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            canonicalDefinitionJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        if (!string.Equals(
                canonical.PayloadHash,
                expectedPayloadHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_TOPOLOGY_PIN_STALE");
        }
        var payload = canonical.Payload;
        if (payload.ArchetypeId != ArchetypeId ||
            payload.Nodes.Count != 5 ||
            payload.Edges.Count != 5 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.FormStep) != 3 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.Gateway &&
                node.Gateway?.Kind is
                    DynamicFlowGatewayKinds.JoinAny or
                    DynamicFlowGatewayKinds.JoinNOfM) != 1 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.Final) != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T06_JOIN_QUORUM_TOPOLOGY_REQUIRED");
        }

        var entry = payload.Nodes.SingleOrDefault(node =>
                        node.NodeId == payload.EntryStepId)
                    ?? throw new InvalidOperationException(
                        "DYNAMIC_FLOW_T06_ENTRY_PIN_MISSING");
        var join = payload.Nodes.Single(node =>
            node.NodeKind == DynamicFlowNodeKinds.Gateway);
        var final = payload.Nodes.Single(node =>
            node.NodeKind == DynamicFlowNodeKinds.Final);
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
                node.NodeId == edge.ToNodeId))
            .OrderBy(node => node.NodeCode, StringComparer.Ordinal)
            .ToArray();
        var expected = join.Gateway!.ExpectedIncomingNodeIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var branchIds = branches.Select(node => node.NodeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var required = ResolveRequiredContributionCount(join.Gateway);
        var joinToFinal = outgoing[join.NodeId].SingleOrDefault();
        if (entry.NodeKind != DynamicFlowNodeKinds.FormStep ||
            incoming[entry.NodeId] != 0 ||
            fanOutEdges.Length != 2 ||
            fanOutEdges.Any(edge => edge.Condition is not null) ||
            branches.Length != 2 ||
            branches.Any(node =>
                node.NodeKind != DynamicFlowNodeKinds.FormStep ||
                incoming[node.NodeId] != 1 ||
                outgoing[node.NodeId].Count != 1 ||
                outgoing[node.NodeId][0].ToNodeId != join.NodeId ||
                outgoing[node.NodeId][0].Condition is not null) ||
            incoming[join.NodeId] != 2 ||
            expected.Length != 2 ||
            !expected.SequenceEqual(branchIds, StringComparer.Ordinal) ||
            required < 1 ||
            required > expected.Length ||
            joinToFinal is null ||
            joinToFinal.ToNodeId != final.NodeId ||
            joinToFinal.Condition is not null ||
            incoming[final.NodeId] != 1 ||
            outgoing[final.NodeId].Count != 0)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T06_JOIN_QUORUM_TOPOLOGY_REQUIRED");
        }

        var controllerEdge = new DynamicFlowTopologyEdgeDto
        {
            TransitionId = $"join-quorum-controller:{entry.NodeId}:{join.NodeId}",
            FromNodeId = entry.NodeId,
            ToNodeId = join.NodeId
        };
        return new DynamicFlowParallelForkTopology(
            payload,
            entry,
            RequireForm(payload, entry),
            controllerEdge,
            join,
            branches.Select((node, index) =>
            {
                var edge = fanOutEdges.Single(item => item.ToNodeId == node.NodeId);
                return new DynamicFlowForkBranch(
                    edge,
                    node,
                    RequireForm(payload, node),
                    index + 2);
            }).ToArray(),
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static int ResolveRequiredContributionCount(
        DynamicFlowGatewayDefinitionDto gateway)
        => gateway.Kind switch
        {
            DynamicFlowGatewayKinds.JoinAny
                when gateway.RequiredIncomingCount is null or 1 => 1,
            DynamicFlowGatewayKinds.JoinNOfM
                when gateway.RequiredIncomingCount is >= 1 =>
                gateway.RequiredIncomingCount.Value,
            _ => throw new InvalidOperationException(
                "DYNAMIC_FLOW_T06_QUORUM_INVALID")
        };

    public static string BuildGatewayLedgerId(
        string gatewayInstanceId,
        int gatewayVersion)
        => DynamicFlowJoinAllTopologyContract.BuildGatewayLedgerId(
            gatewayInstanceId,
            gatewayVersion);

    public static string BuildContributionLedgerId(
        string gatewayInstanceId,
        int gatewayVersion,
        string contributionId)
        => DynamicFlowJoinAllTopologyContract.BuildContributionLedgerId(
            gatewayInstanceId,
            gatewayVersion,
            contributionId);

    public static string BuildContributionEventId(
        string gatewayInstanceId,
        int gatewayVersion,
        string contributionId)
        => DynamicFlowJoinAllTopologyContract.BuildContributionEventId(
            gatewayInstanceId,
            gatewayVersion,
            contributionId);

    public static string BuildReleaseEventId(
        string gatewayInstanceId,
        int gatewayVersion)
        => DynamicFlowJoinAllTopologyContract.BuildReleaseEventId(
            gatewayInstanceId,
            gatewayVersion);

    public static string BuildLateEventId(
        string gatewayInstanceId,
        int gatewayVersion,
        string contributionId)
        => DynamicFlowParallelForkTopologyContract.Hash(
            $"{gatewayInstanceId}\n{gatewayVersion}\n{contributionId}\nlate-ignored")[..24];

    public static string BuildImpossibleEventId(
        string gatewayInstanceId,
        int gatewayVersion)
        => DynamicFlowParallelForkTopologyContract.Hash(
            $"{gatewayInstanceId}\n{gatewayVersion}\nquorum-impossible")[..24];

    public static bool IsQuorumImpossible(
        int requiredContributionCount,
        IEnumerable<string> arrivedContributionIds,
        IEnumerable<string> stillPossibleContributionIds)
    {
        if (requiredContributionCount < 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T06_QUORUM_INVALID");
        }
        var reachable = arrivedContributionIds
            .Concat(stillPossibleContributionIds)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return reachable < requiredContributionCount;
    }

    private static DynamicFlowFormNodeDto RequireForm(
        DynamicFlowTemplatePayloadDto payload,
        DynamicFlowTopologyNodeDto node)
        => payload.FormNodes.SingleOrDefault(form =>
               form.FormNodeId == node.FormNodeId)
           ?? throw new InvalidOperationException(
               "DYNAMIC_FLOW_T06_FORM_PIN_MISSING");
}
