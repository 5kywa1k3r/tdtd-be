using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowReviewLoopTopology(
    DynamicFlowTemplatePayloadDto Payload,
    DynamicFlowTopologyNodeDto ReviewNode,
    DynamicFlowTopologyNodeDto ReviewGateway,
    DynamicFlowFormNodeDto ReviewForm,
    int MaxReviewCycles,
    string CanonicalJson,
    string TopologyHash);

/// <summary>
/// Frozen P6-06 contract for FLOW-T08. The definition owns one logical form
/// step. Review returns create a new attempt of that exact node; no graph edge
/// can introduce an arbitrary cycle.
/// </summary>
public static class DynamicFlowReviewLoopTopologyContract
{
    public const string ArchetypeId = "FLOW-T08";
    public const int DefaultMaxReviewCycles = 10;
    public const int MinMaxReviewCycles = 1;
    public const int MaxMaxReviewCycles = 50;
    public const string CycleExhausted =
        "DYNAMIC_FLOW_REVIEW_CYCLE_EXHAUSTED";
    public const string LineageDrift =
        "DYNAMIC_FLOW_REVIEW_ATTEMPT_LINEAGE_DRIFT";
    public const string ReviewAttemptCreatedEvent =
        "REVIEW_RETURN_ATTEMPT_CREATED";
    public const string ReviewCycleExhaustedEvent =
        "REVIEW_CYCLE_EXHAUSTED";
    public const string ReviewAdvanceCommand = "REVIEW_RETURN_ADVANCE";
    public const int InitialReviewCycleNo = 1;

    public static DynamicFlowReviewLoopTopology Require(
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
        if (!string.Equals(
                payload.ArchetypeId,
                ArchetypeId,
                StringComparison.Ordinal) ||
            payload.Nodes.Count != 2 ||
            payload.Edges.Count != 1 ||
            payload.FormNodes.Count != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_REVIEW_LOOP_TOPOLOGY_INVALID");
        }
        var node = payload.Nodes.Single(candidate =>
            candidate.NodeKind == DynamicFlowNodeKinds.FormStep);
        var gateway = payload.Nodes.Single(candidate =>
            candidate.NodeKind == DynamicFlowNodeKinds.Gateway);
        if (node.NodeKind != DynamicFlowNodeKinds.FormStep ||
            node.Gateway is not null ||
            string.IsNullOrWhiteSpace(node.NodeId) ||
            string.IsNullOrWhiteSpace(node.NodeCode) ||
            string.IsNullOrWhiteSpace(node.FormNodeId) ||
            !string.Equals(
                payload.EntryStepId,
                node.NodeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_REVIEW_LOOP_TARGET_INVALID");
        }
        var edge = payload.Edges.Single();
        if (gateway.Gateway?.Kind !=
                DynamicFlowGatewayKinds.Review ||
            edge.FromNodeId != node.NodeId ||
            edge.ToNodeId != gateway.NodeId ||
            edge.Condition is not null ||
            payload.Edges.Any(candidate =>
                candidate.FromNodeId == gateway.NodeId))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_REVIEW_LOOP_GATEWAY_INVALID");
        }
        var form = payload.FormNodes.Single();
        if (!string.Equals(
                form.FormNodeId,
                node.FormNodeId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(form.DynamicFormTemplateId) ||
            string.IsNullOrWhiteSpace(form.DynamicFormFamilyId) ||
            form.DynamicFormVersionNo is null or < 1 ||
            !IsSha256(form.DynamicFormSchemaHash) ||
            !IsSha256(form.DynamicFormSnapshotHash))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_REVIEW_LOOP_FORM_PIN_INVALID");
        }
        var maxReviewCycles = ReadMaxReviewCycles(payload);
        return new DynamicFlowReviewLoopTopology(
            payload,
            node,
            gateway,
            form,
            maxReviewCycles,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static int ReadMaxReviewCycles(
        DynamicFlowTemplatePayloadDto payload)
    {
        if (!payload.FinalResultPolicy.TryGetValue(
                "maxReviewCycles",
                out var value))
        {
            return DefaultMaxReviewCycles;
        }
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result) ||
            result is < MinMaxReviewCycles or > MaxMaxReviewCycles)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_MAX_REVIEW_CYCLES_INVALID");
        }
        return result;
    }

    public static string BuildBranchId(
        string instanceId,
        int executionEpoch,
        string targetUnitId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{targetUnitId}\nreview-branch");

    public static string BuildStepInstanceId(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string branchId,
        int attemptNo)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{nodeId}\n{branchId}\n{attemptNo}\nreview-step");

    public static string BuildAssignmentId(string stepInstanceId)
        => StableObjectId($"{stepInstanceId}\nreview-assignment");

    public static string BuildOwnerIdentity(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string branchId,
        int attemptNo,
        string ownerKind)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{nodeId}\n{branchId}\n{attemptNo}\n{ownerKind}\nreview-owner");

    public static string BuildAdvanceReceiptId(
        string instanceId,
        string sourceEventKey)
        => StableObjectId(
            $"{instanceId}\nreview-return\n{sourceEventKey}\nreceipt");

    public static string BuildAdvanceEventId(
        string instanceId,
        string sourceEventKey)
        => StableObjectId(
            $"{instanceId}\nreview-return\n{sourceEventKey}\nevent");

    public static string Hash(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

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
               CryptographicOperations.FixedTimeEquals(
                   leftBytes,
                   rightBytes);
    }
}
