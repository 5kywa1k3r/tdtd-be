using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowSupplementalTopology(
    DynamicFlowTemplatePayloadDto Payload,
    IReadOnlyDictionary<string, DynamicFlowTopologyNodeDto> NodesByFormNodeId,
    IReadOnlyDictionary<string, DynamicFlowFormNodeDto> FormsByFormNodeId,
    string CanonicalJson,
    string TopologyHash);

public static class DynamicFlowSupplementalTopologyContract
{
    public const string ArchetypeId = "FLOW-T11";
    public const int MaxStepsPerEpoch = 50;
    public const string AddCommand = "SUPPLEMENTAL_ADD";
    public const string CancelCommand = "SUPPLEMENTAL_CANCEL";
    public const string AddedEvent = "SUPPLEMENTAL_STEP_ADDED";
    public const string CancelledEvent = "SUPPLEMENTAL_STEP_CANCELLED";
    public const string MaterializedEvent = "SUPPLEMENTAL_ASSIGNMENT_MATERIALIZED";

    public static DynamicFlowSupplementalTopology Require(
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
        var formNodes = payload.Nodes
            .Where(node => node.NodeKind == DynamicFlowNodeKinds.FormStep)
            .ToArray();
        if (payload.ArchetypeId != ArchetypeId ||
            formNodes.Length == 0 ||
            formNodes.Any(node =>
                node.Gateway is not null ||
                string.IsNullOrWhiteSpace(node.FormNodeId) ||
                node.DeclaredRoles.Count == 0) ||
            payload.Nodes.Any(node =>
                node.NodeKind != DynamicFlowNodeKinds.FormStep &&
                node.NodeKind != DynamicFlowNodeKinds.Gateway))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_SUPPLEMENTAL_TOPOLOGY_INVALID");
        }

        var nodesByForm = formNodes.ToDictionary(
            node => node.FormNodeId!,
            StringComparer.Ordinal);
        var formsByForm = payload.FormNodes.ToDictionary(
            form => form.FormNodeId,
            StringComparer.Ordinal);
        if (nodesByForm.Count != formNodes.Length ||
            nodesByForm.Keys.Any(key => !formsByForm.ContainsKey(key)) ||
            formsByForm.Values.Any(form =>
                !ObjectId.TryParse(form.DynamicFormTemplateId, out _) ||
                !ObjectId.TryParse(form.DynamicFormFamilyId, out _) ||
                form.DynamicFormVersionNo is null or < 1 ||
                !IsSha256(form.DynamicFormSchemaHash) ||
                !IsSha256(form.DynamicFormSnapshotHash)))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_SUPPLEMENTAL_ALLOWLIST_INVALID");
        }

        return new DynamicFlowSupplementalTopology(
            payload,
            nodesByForm,
            formsByForm,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static string BuildSupplementalStepId(
        string instanceId,
        int executionEpoch,
        string commandId)
        => StableObjectId(
            $"{instanceId}\n{executionEpoch}\n{commandId.Trim()}\nsupplemental-step");

    public static string BuildBranchId(string supplementalStepId)
        => StableObjectId($"{supplementalStepId}\nbranch");

    public static string BuildAssignmentId(string supplementalStepId)
        => StableObjectId($"{supplementalStepId}\nassignment");

    public static bool CompletionSatisfied(
        IEnumerable<DynamicFlowStepInstance> steps,
        string? completingStepId = null)
        => steps.All(step =>
            step.Id == completingStepId ||
            (step.IsSupplemental
                ? !step.CompletionRequired ||
                  step.State is DynamicFlowStepStates.Completed or
                      DynamicFlowStepStates.CancelledByGateway
                : step.State == DynamicFlowStepStates.Completed));

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
