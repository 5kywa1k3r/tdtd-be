using System.Security.Cryptography;
using System.Text;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowSequentialTopology(
    DynamicFlowTemplatePayloadDto Payload,
    IReadOnlyList<DynamicFlowTopologyNodeDto> OrderedNodes,
    IReadOnlyDictionary<string, DynamicFlowTopologyEdgeDto> OutgoingByNodeId,
    IReadOnlyDictionary<string, DynamicFlowFormNodeDto> FormsByNodeId,
    string CanonicalJson,
    string TopologyHash);

/// <summary>
/// Frozen P6-01 execution contract for FLOW-T03. The definition contract owns
/// graph syntax; this contract narrows the executable shape to one immutable
/// linear chain and derives every durable identity from pinned inputs.
/// </summary>
public static class DynamicFlowSequentialTopologyContract
{
    public const string ArchetypeId = "FLOW-T03";
    public const int InitialExecutionEpoch = 1;

    public static DynamicFlowSequentialTopology Require(
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
        if (payload.Nodes.Count != 3 ||
            payload.Nodes.Any(node => node.NodeKind != DynamicFlowNodeKinds.FormStep) ||
            payload.Edges.Count != payload.Nodes.Count - 1)
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T03_LINEAR_TOPOLOGY_REQUIRED");
        }

        var incoming = payload.Nodes.ToDictionary(node => node.NodeId, _ => 0, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, DynamicFlowTopologyEdgeDto>(StringComparer.Ordinal);
        foreach (var edge in payload.Edges)
        {
            incoming[edge.ToNodeId]++;
            if (!outgoing.TryAdd(edge.FromNodeId, edge))
                throw new InvalidOperationException("DYNAMIC_FLOW_T03_LINEAR_TOPOLOGY_REQUIRED");
        }

        if (incoming[payload.EntryStepId] != 0 ||
            incoming.Where(pair => pair.Key != payload.EntryStepId).Any(pair => pair.Value != 1) ||
            payload.Nodes.Count(node => !outgoing.ContainsKey(node.NodeId)) != 1)
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T03_LINEAR_TOPOLOGY_REQUIRED");
        }

        var ordered = new List<DynamicFlowTopologyNodeDto>(payload.Nodes.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = payload.EntryStepId;
        while (true)
        {
            var current = payload.Nodes.SingleOrDefault(node =>
                              string.Equals(node.NodeId, currentId, StringComparison.Ordinal))
                          ?? throw new InvalidOperationException("DYNAMIC_FLOW_T03_NODE_PIN_MISSING");
            if (!visited.Add(current.NodeId))
                throw new InvalidOperationException("DYNAMIC_FLOW_T03_LINEAR_TOPOLOGY_REQUIRED");
            ordered.Add(current);
            if (!outgoing.TryGetValue(current.NodeId, out var edge))
                break;
            currentId = edge.ToNodeId;
        }
        if (ordered.Count != payload.Nodes.Count)
            throw new InvalidOperationException("DYNAMIC_FLOW_T03_LINEAR_TOPOLOGY_REQUIRED");
        if (!ordered.Select(node => node.NodeCode).SequenceEqual(
                new[] { "A", "B", "C" },
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_T03_LINEAR_TOPOLOGY_REQUIRED");
        }

        var formsByNodeId = ordered.ToDictionary(
            node => node.NodeId,
            node =>
            {
                var formNodeId = node.FormNodeId
                                 ?? throw new InvalidOperationException(
                                     "DYNAMIC_FLOW_T03_FORM_PIN_MISSING");
                return payload.FormNodes.SingleOrDefault(form =>
                           string.Equals(form.FormNodeId, formNodeId, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException("DYNAMIC_FLOW_T03_FORM_PIN_MISSING");
            },
            StringComparer.Ordinal);

        return new DynamicFlowSequentialTopology(
            payload,
            ordered,
            outgoing,
            formsByNodeId,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static string BuildDefinitionRevision(
        string flowTemplateVersionId,
        int flowTemplateVersionNo,
        string payloadHash)
        => $"{flowTemplateVersionId}:{flowTemplateVersionNo}:{payloadHash}";

    public static string BuildRootBranchId(
        string instanceId,
        int executionEpoch,
        string targetUnitId)
        => StableObjectId($"{instanceId}\n{executionEpoch}\n{targetUnitId}\nbranch");

    public static string BuildStepInstanceId(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string branchId,
        int attemptNo)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{nodeId}\n{branchId}\n{attemptNo}\nstep");

    public static string BuildAssignmentId(string stepInstanceId)
        => StableObjectId($"{stepInstanceId}\nassignment");

    public static string BuildStepOwnerIdentity(
        string instanceId,
        int executionEpoch,
        string ownerNodeId,
        string branchId,
        string ownerKind)
        => $"{ownerKind}:step:" +
           BuildStepInstanceId(
               instanceId,
               executionEpoch,
               ownerNodeId,
               branchId,
               1);

    public static string BuildForwardReceiptId(string instanceId, string commandId)
        => StableObjectId(
            $"{Models.DynamicFlowCommandScopeKinds.Instance}\n{instanceId}\nFORWARD\n{commandId}");

    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

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
