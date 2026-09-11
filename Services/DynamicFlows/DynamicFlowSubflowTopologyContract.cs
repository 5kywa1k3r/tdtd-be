using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowSubflowTopology(
    DynamicFlowTemplatePayloadDto Payload,
    DynamicFlowTopologyNodeDto ParentNode,
    DynamicFlowTopologyNodeDto SubflowGateway,
    DynamicFlowFormNodeDto ParentForm,
    string ChildFlowFamilyId,
    string ChildFlowVersionId,
    string CanonicalJson,
    string TopologyHash);

public sealed record DynamicFlowSubflowLaunchContext(
    string ParentInstanceId,
    string ParentStepInstanceId,
    string RootInstanceId,
    IReadOnlyList<string> AncestryPath,
    IReadOnlyList<string> AncestryFlowFamilyIds,
    long ExpectedParentInstanceRevision,
    long ExpectedParentStepRevision,
    long ParentEventSequence);

/// <summary>
/// Frozen P6-07 contract for one blocking, exact-version-pinned child flow.
/// The graph itself remains acyclic; ancestry is runtime state and is capped.
/// </summary>
public static class DynamicFlowSubflowTopologyContract
{
    public const string ArchetypeId = "FLOW-T09";
    public const int MaxDepth = 8;
    public const string LaunchCommand = "SUBFLOW_LAUNCH";
    public const string WaitingState = "WAITING_CHILD";
    public const string ChildLaunchedEvent = "SUBFLOW_CHILD_LAUNCHED";
    public const string ChildCompletedEvent = "SUBFLOW_CHILD_COMPLETED";
    public const string ChildBlockedEvent = "SUBFLOW_CHILD_BLOCKED";
    public const string DepthExceeded =
        "DYNAMIC_FLOW_SUBFLOW_MAX_DEPTH_EXCEEDED";
    public const string AncestryCycle =
        "DYNAMIC_FLOW_SUBFLOW_ANCESTRY_CYCLE";
    public const string ChildPinDrift =
        "DYNAMIC_FLOW_SUBFLOW_CHILD_PIN_DRIFT";
    public const string ChildFailed =
        "DYNAMIC_FLOW_SUBFLOW_CHILD_FAILED";
    public const string ChildTerminated =
        "DYNAMIC_FLOW_SUBFLOW_CHILD_TERMINATED";

    public static DynamicFlowSubflowTopology Require(
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
            payload.Nodes.Count != 2 ||
            payload.Edges.Count != 1 ||
            payload.FormNodes.Count != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_SUBFLOW_TOPOLOGY_INVALID");
        }

        var parent = payload.Nodes.Single(candidate =>
            candidate.NodeKind == DynamicFlowNodeKinds.FormStep);
        var gateway = payload.Nodes.Single(candidate =>
            candidate.NodeKind == DynamicFlowNodeKinds.Gateway);
        var edge = payload.Edges.Single();
        var definition = gateway.Gateway;
        if (payload.EntryStepId != parent.NodeId ||
            parent.Gateway is not null ||
            string.IsNullOrWhiteSpace(parent.FormNodeId) ||
            definition?.Kind != DynamicFlowGatewayKinds.Subflow ||
            edge.FromNodeId != parent.NodeId ||
            edge.ToNodeId != gateway.NodeId ||
            edge.Condition is not null ||
            payload.Edges.Any(candidate =>
                candidate.FromNodeId == gateway.NodeId) ||
            !ObjectId.TryParse(definition.SubflowFamilyId, out _) ||
            !ObjectId.TryParse(definition.SubflowVersionId, out _) ||
            definition.ExpectedIncomingNodeIds.Count != 0 ||
            definition.RequiredIncomingCount is not null ||
            definition.ReviewRole is not null ||
            definition.ScheduleKey is not null ||
            definition.RollbackTargetNodeId is not null)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_SUBFLOW_GATEWAY_INVALID");
        }

        var form = payload.FormNodes.Single();
        if (form.FormNodeId != parent.FormNodeId ||
            !ObjectId.TryParse(form.DynamicFormTemplateId, out _) ||
            !ObjectId.TryParse(form.DynamicFormFamilyId, out _) ||
            form.DynamicFormVersionNo is null or < 1 ||
            !IsSha256(form.DynamicFormSchemaHash) ||
            !IsSha256(form.DynamicFormSnapshotHash))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_SUBFLOW_FORM_PIN_INVALID");
        }

        return new DynamicFlowSubflowTopology(
            payload,
            parent,
            gateway,
            form,
            definition.SubflowFamilyId!,
            definition.SubflowVersionId!,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static DynamicFlowSubflowLaunchContext BuildLaunchContext(
        DynamicFlowInstance parent,
        DynamicFlowStepInstance parentStep,
        DynamicFlowSubflowTopology topology,
        string childFlowFamilyId,
        string childFlowVersionId)
    {
        if (topology.ChildFlowFamilyId != childFlowFamilyId ||
            topology.ChildFlowVersionId != childFlowVersionId)
        {
            throw new InvalidOperationException(ChildPinDrift);
        }
        var ancestryPath = parent.AncestryPath
            .Append(parent.Id)
            .ToArray();
        var ancestryFamilies = parent.AncestryFlowFamilyIds
            .Append(parent.FlowTemplateId)
            .ToArray();
        if (ancestryPath.Length > MaxDepth)
            throw new InvalidOperationException(DepthExceeded);
        if (ancestryFamilies.Contains(
                childFlowFamilyId,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(AncestryCycle);
        }
        return new DynamicFlowSubflowLaunchContext(
            parent.Id,
            parentStep.Id,
            string.IsNullOrWhiteSpace(parent.RootInstanceId)
                ? parent.Id
                : parent.RootInstanceId,
            ancestryPath,
            ancestryFamilies,
            parent.Revision,
            parentStep.Revision,
            parent.NextEventSequence);
    }

    public static string BuildChildInstanceId(
        string workId,
        string childFlowVersionId,
        string commandId)
        => StableObjectId(
            $"{workId}\n{childFlowVersionId}\n{commandId}");

    public static string BuildBranchId(
        string childInstanceId,
        string stepId,
        string targetUnitId,
        int attemptNo)
        => StableObjectId(
            $"{childInstanceId}\n{stepId}\n{targetUnitId}\n{attemptNo}\nbranch");

    public static string BuildStepInstanceId(
        string childInstanceId,
        string stepId,
        string targetUnitId,
        int attemptNo)
        => StableObjectId(
            $"{childInstanceId}\n{stepId}\n{targetUnitId}\n{attemptNo}\nstep");

    public static string BuildAssignmentId(string branchId)
        => StableObjectId($"{branchId}\nassignment");

    public static string BuildParentEventId(
        string parentInstanceId,
        string commandId,
        string eventType)
        => StableObjectId(
            $"{parentInstanceId}\n{commandId}\n{eventType}\nsubflow-event");

    public static string BuildOwnerIdentity(
        string parentInstanceId,
        string parentStepInstanceId,
        string childInstanceId,
        string ownerKind)
        => StableObjectId(
            $"{parentInstanceId}\n{parentStepInstanceId}\n{childInstanceId}\n{ownerKind}\nsubflow-owner");

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
