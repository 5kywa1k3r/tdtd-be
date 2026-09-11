using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Common.Capabilities;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public static class DynamicFlowRuntimeCatalogCandidate
{
    public const string Version = "1.2";
    public const string SemanticHash = "b26549d5de7a3d93bd6fc9bab7bfdfbdaffb66a01347039b2c3629692b60068f";
    public const bool ActivationEnabled = true;
}

/// <summary>
/// Exact P6 release pin. Before promotion it is available only to the bounded
/// Testing override. After promotion it executes only while the generated
/// CURRENT pointer still matches both sealed values, so a catalog rollback
/// closes v1.3 execution without making stored v1.3 definitions unreadable.
/// </summary>
public static class DynamicFlowP6CatalogCandidate
{
    public const string Version = "1.3";
    public const string SemanticHash =
        "55cfa0a4420e01db6707011ffc7a0271088c5b01b63edb8f2d21978edd3e2497";

    public static bool ActivationEnabled =>
        string.Equals(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            Version,
            StringComparison.Ordinal) &&
        string.Equals(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            SemanticHash,
            StringComparison.Ordinal);
}

/// <summary>
/// Exact P7 mapping-policy successor pin. The candidate can be exercised only
/// by the explicit Testing activation policy until CURRENT is promoted to the
/// sealed v1.4 semantic hash.
/// </summary>
public static class DynamicFlowP7CatalogCandidate
{
    public const string Version = "1.4";
    public const string SemanticHash =
        "d2ca56a4745380688e24c6926752643d2b023b2d47bc1578d3b3e2f9379457ee";

    public static bool ActivationEnabled =>
        IsExactCurrent(Version, SemanticHash) ||
        DynamicFlowP8CatalogCandidate.ActivationEnabled;

    private static bool IsExactCurrent(string version, string semanticHash) =>
        string.Equals(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            version,
            StringComparison.Ordinal) &&
        string.Equals(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            semanticHash,
            StringComparison.Ordinal);
}

/// <summary>
/// Exact P8 statistics-configuration release pin. The v1.5 release and its
/// exact v1.6/P10 v1.7 successors retain every P7 mapping capability while
/// activating only the P8 configuration mutation surface. Unknown versions,
/// crossed version/hash pairs and semantic tamper remain fail-closed.
/// </summary>
public static class DynamicFlowP8CatalogCandidate
{
    public const string Version = "1.5";
    public const string SemanticHash =
        "e3c335617721bbf8cd09c62c5e76377f23f3d024b20c848bf66c8c539f0c9d2f";
    private const string P9SuccessorVersion = "1.6";
    private const string P9SuccessorSemanticHash =
        "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";
    private const string P10SuccessorVersion = "1.7";
    private const string P10SuccessorSemanticHash =
        "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1";

    public static bool ActivationEnabled => IsCompatibleCurrent(
        DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
        DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256);

    internal static bool IsCompatibleCurrent(
        string? version,
        string? semanticHash)
        => IsExact(version, semanticHash, Version, SemanticHash) ||
           IsExact(version, semanticHash,
               P9SuccessorVersion, P9SuccessorSemanticHash) ||
           IsExact(version, semanticHash,
               P10SuccessorVersion, P10SuccessorSemanticHash);

    private static bool IsExact(
        string? version,
        string? semanticHash,
        string expectedVersion,
        string expectedSemanticHash)
        => string.Equals(version, expectedVersion, StringComparison.Ordinal) &&
           string.Equals(semanticHash, expectedSemanticHash,
               StringComparison.Ordinal);
}

public static class DynamicFlowRuntimeEligibilityPolicy
{
    public const string EligibleCandidate = "ELIGIBLE_CANDIDATE";
    public const string BlockedCatalog = "BLOCKED_CATALOG";
    public const string BlockedPhase = "BLOCKED_PHASE";

    public static (string Eligibility, string? BlockedUntilPhase) Evaluate(
        string? catalogVersion,
        string? catalogSemanticHash,
        string? archetypeId)
    {
        if (IsCurrentCatalog(catalogVersion, catalogSemanticHash))
        {
            return archetypeId is "FLOW-T01" or "FLOW-T02"
                ? (EligibleCandidate, null)
                : (BlockedPhase, "P6");
        }

        if (!IsP6CandidateCatalog(catalogVersion, catalogSemanticHash))
            return (BlockedCatalog, "P5");

        return archetypeId is
            "FLOW-T01" or
            "FLOW-T02" or
            "FLOW-T03" or
            "FLOW-T04" or
            "FLOW-T05" or
            "FLOW-T06" or
            "FLOW-T07" or
            "FLOW-T08" or
            "FLOW-T09" or
            "FLOW-T10" or
            "FLOW-T11" or
            "FLOW-T12"
            ? (EligibleCandidate, null)
            : (BlockedPhase, "P6");
    }

    public static bool IsCurrentCatalog(string? version, string? semanticHash)
        => string.Equals(
               version,
               DynamicFlowRuntimeCatalogCandidate.Version,
               StringComparison.Ordinal) &&
           string.Equals(
               semanticHash,
               DynamicFlowRuntimeCatalogCandidate.SemanticHash,
               StringComparison.Ordinal);

    public static bool IsP6CandidateCatalog(string? version, string? semanticHash)
        => IsExactP6Catalog(version, semanticHash) ||
           IsP7MappingCatalog(version, semanticHash);

    public static bool IsExactP6Catalog(string? version, string? semanticHash)
        => string.Equals(version, DynamicFlowP6CatalogCandidate.Version, StringComparison.Ordinal) &&
           string.Equals(semanticHash, DynamicFlowP6CatalogCandidate.SemanticHash, StringComparison.Ordinal);

    public static bool IsP7MappingCatalog(string? version, string? semanticHash)
        => string.Equals(version, DynamicFlowP7CatalogCandidate.Version, StringComparison.Ordinal) &&
           string.Equals(semanticHash, DynamicFlowP7CatalogCandidate.SemanticHash, StringComparison.Ordinal) ||
           DynamicFlowP8CatalogCandidate.IsCompatibleCurrent(version, semanticHash);
}

public static class DynamicFlowRuntimePreflightContract
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static DynamicFlowPreflightResponse Build(
        string workId,
        string workType,
        DynamicFlowTemplate family,
        DynamicFlowTemplateVersion version,
        DynamicFlowPreflightRequest request,
        string actorUserId,
        string issuerUnitId,
        IReadOnlyDictionary<string, IReadOnlyList<DynamicFlowParticipantUserSnapshotDto>> participantsByUnit,
        Func<string, bool>? isP6CandidateArchetypeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(request);
        RequireToken(request.CommandId, nameof(request.CommandId));
        RequireToken(request.PeriodKey, nameof(request.PeriodKey));

        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            version.PayloadJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        var payload = canonical.Payload;
        DynamicFlowSequentialTopology? sequentialTopology = null;
        DynamicFlowParallelForkTopology? parallelForkTopology = null;
        DynamicFlowReviewLoopTopology? reviewLoopTopology = null;
        DynamicFlowSubflowTopology? subflowTopology = null;
        DynamicFlowPeriodicTopology? periodicTopology = null;
        DynamicFlowFinalizeTopology? finalizeTopology = null;
        var isP6CandidateCatalog =
            DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                version.CatalogVersion,
                version.CatalogSemanticHash);
        var p6CandidateArchetypeEnabled =
            isP6CandidateCatalog &&
            isP6CandidateArchetypeEnabled?.Invoke(payload.ArchetypeId) == true;
        if (p6CandidateArchetypeEnabled)
        {
            if (string.Equals(
                    payload.ArchetypeId,
                    DynamicFlowSequentialTopologyContract.ArchetypeId,
                    StringComparison.Ordinal))
            {
                sequentialTopology =
                    DynamicFlowSequentialTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowParallelForkTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                parallelForkTopology =
                    DynamicFlowParallelForkTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowJoinAllTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                parallelForkTopology =
                    DynamicFlowJoinAllTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowJoinQuorumTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                parallelForkTopology =
                    DynamicFlowJoinQuorumTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowTypedConditionalTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                parallelForkTopology =
                    DynamicFlowTypedConditionalTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowReviewLoopTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                reviewLoopTopology =
                    DynamicFlowReviewLoopTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowSubflowTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                subflowTopology =
                    DynamicFlowSubflowTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowPeriodicTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                periodicTopology =
                    DynamicFlowPeriodicTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
            else if (string.Equals(
                         payload.ArchetypeId,
                         DynamicFlowFinalizeTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                finalizeTopology =
                    DynamicFlowFinalizeTopologyContract.Require(
                        canonical.CanonicalJson,
                        canonical.PayloadHash);
            }
        }
        var entry = payload.Nodes.SingleOrDefault(node =>
                        string.Equals(node.NodeId, payload.EntryStepId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("DYNAMIC_FLOW_ENTRY_STEP_NOT_FOUND");
        if (entry.NodeKind != DynamicFlowNodeKinds.FormStep || string.IsNullOrWhiteSpace(entry.FormNodeId))
            throw new InvalidOperationException("DYNAMIC_FLOW_ENTRY_STEP_FORM_REQUIRED");

        var targets = NormalizeTargets(request.TargetUnitIds)
            .Select(unitId =>
            {
                if (!participantsByUnit.TryGetValue(unitId, out var users))
                    throw new InvalidOperationException("DYNAMIC_FLOW_TARGET_UNIT_NOT_BINDABLE");
                var participants = users
                    .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
                    .GroupBy(user => user.UserId.Trim(), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(user => user.UserId, StringComparer.Ordinal)
                    .ToList();
                return new DynamicFlowTargetSnapshotDto
                {
                    TargetUnitId = unitId,
                    AssigneeUserIds = participants.Select(user => user.UserId.Trim()).ToList(),
                    Participants = participants
                };
            })
            .ToList();
        if (targets.Any(target => target.AssigneeUserIds.Count == 0))
            throw new InvalidOperationException("DYNAMIC_FLOW_TARGET_ASSIGNEE_REQUIRED");

        var scheduleCanonical = CanonicalJson(request.ScheduleIdentityJson);
        var scheduleHash = Hash(scheduleCanonical);
        var formPins = payload.FormNodes
            .OrderBy(form => form.FormNodeId, StringComparer.Ordinal)
            .Select(form => new DynamicFlowFormPinDto
            {
                FormNodeId = form.FormNodeId,
                DynamicFormTemplateId = form.DynamicFormTemplateId,
                DynamicFormFamilyId = form.DynamicFormFamilyId ?? string.Empty,
                DynamicFormVersionNo = form.DynamicFormVersionNo ?? 0,
                DynamicFormSchemaHash = form.DynamicFormSchemaHash ?? string.Empty,
                DynamicFormSnapshotHash = form.DynamicFormSnapshotHash ?? string.Empty
            })
            .ToList();
        var (eligibility, blockedUntilPhase) = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            version.CatalogVersion,
            version.CatalogSemanticHash,
            payload.ArchetypeId);
        if (isP6CandidateCatalog && !p6CandidateArchetypeEnabled)
        {
            eligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedPhase;
            blockedUntilPhase = "P6";
        }

        var requestCanonical = JsonSerializer.Serialize(new
        {
            workId,
            workType,
            flowTemplateVersionId = version.Id,
            commandId = request.CommandId.Trim(),
            actorUserId,
            issuerUnitId,
            periodKey = request.PeriodKey.Trim(),
            scheduleIdentityHash = scheduleHash,
            targets = targets.Select(target => new
            {
                target.TargetUnitId,
                participants = target.Participants.Select(user => new
                {
                    user.UserId,
                    user.Username,
                    user.FullName,
                    user.UnitId,
                    user.UnitSymbol,
                    user.UnitShortName,
                    user.UnitName,
                    user.PositionCode,
                    user.PositionName
                })
            }),
            flowPayloadHash = version.PayloadHash,
            forms = formPins
        }, Json);
        var requestHash = Hash(requestCanonical);
        var snapshotToken = Hash($"{requestHash}\n{version.PayloadHash}\n{scheduleHash}");
        var commandIdentityHash = BuildCommandIdentityHash(
            workId,
            version.Id,
            request,
            actorUserId);

        return new DynamicFlowPreflightResponse
        {
            WorkId = workId,
            WorkType = workType,
            CommandId = request.CommandId.Trim(),
            IssuerUserId = actorUserId,
            IssuerUnitId = issuerUnitId,
            CommandIdentityHash = commandIdentityHash,
            RequestHash = requestHash,
            SnapshotToken = snapshotToken,
            Eligibility = eligibility,
            BlockedUntilPhase = blockedUntilPhase,
            FlowPin = new DynamicFlowExactPinDto
            {
                FlowTemplateId = family.Id,
                FlowTemplateVersionId = version.Id,
                FlowTemplateVersionNo = version.VersionNo,
                PayloadHash = version.PayloadHash ?? string.Empty,
                CatalogVersion = version.CatalogVersion ?? string.Empty,
                CatalogSemanticHash = version.CatalogSemanticHash ?? string.Empty,
                ArchetypeId = payload.ArchetypeId,
                DefinitionRevision = DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                    version.Id,
                    version.VersionNo,
                    version.PayloadHash ?? string.Empty),
                TopologyHash = canonical.PayloadHash
            },
            EntryStep = new DynamicFlowRuntimeStepDto
            {
                StepId = entry.NodeId,
                StepCode = entry.NodeCode,
                StepOrder = sequentialTopology is null && parallelForkTopology is null
                    ? payload.Nodes.IndexOf(entry) + 1
                    : sequentialTopology is not null
                        ? sequentialTopology.OrderedNodes
                        .Select((node, index) => new
                        {
                            node.NodeId,
                            Order = index + 1
                        })
                        .Single(item =>
                            string.Equals(
                                item.NodeId,
                                entry.NodeId,
                                StringComparison.Ordinal))
                        .Order
                        : 1,
                FormNodeId = entry.FormNodeId,
                NextStepIds = sequentialTopology is not null &&
                               sequentialTopology.OutgoingByNodeId.TryGetValue(
                                  entry.NodeId,
                                  out var sequentialEdge)
                    ? new List<string> { sequentialEdge.ToNodeId }
                    : parallelForkTopology is not null
                        ? new List<string> { parallelForkTopology.ForkNode.NodeId }
                        : new List<string>(),
                IsTerminalNode = sequentialTopology is not null &&
                                 !sequentialTopology.OutgoingByNodeId.ContainsKey(entry.NodeId) ||
                                 reviewLoopTopology is not null ||
                                 subflowTopology is not null ||
                                 periodicTopology is not null
            },
            FormPins = formPins,
            Targets = targets,
            PeriodKey = request.PeriodKey.Trim(),
            ScheduleIdentityJson = scheduleCanonical,
            ScheduleIdentityHash = scheduleHash,
            LockedDefinitionJson = canonical.CanonicalJson
        };
    }

    public static string BuildCommandIdentityHash(
        string workId,
        string flowTemplateVersionId,
        DynamicFlowPreflightRequest request,
        string actorUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireToken(request.CommandId, nameof(request.CommandId));
        RequireToken(request.PeriodKey, nameof(request.PeriodKey));
        RequireToken(actorUserId, nameof(actorUserId));
        var scheduleHash = Hash(CanonicalJson(request.ScheduleIdentityJson));
        var canonical = JsonSerializer.Serialize(new
        {
            workId,
            flowTemplateVersionId,
            commandType = "LAUNCH",
            commandId = request.CommandId.Trim(),
            actorUserId = actorUserId.Trim(),
            targetUnitIds = NormalizeTargets(request.TargetUnitIds),
            periodKey = request.PeriodKey.Trim(),
            scheduleIdentityHash = scheduleHash
        }, Json);
        return Hash(canonical);
    }

    public static DynamicFlowConfirmResponse Confirm(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request)
    {
        if (!FixedEquals(preflight.SnapshotToken, request.SnapshotToken?.Trim()))
            throw new InvalidOperationException("DYNAMIC_FLOW_PREFLIGHT_STALE");
        var blocked = !string.Equals(
            preflight.Eligibility,
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            StringComparison.Ordinal);
        return new DynamicFlowConfirmResponse
        {
            CommandId = preflight.CommandId,
            RequestHash = preflight.RequestHash,
            SnapshotToken = preflight.SnapshotToken,
            Status = blocked
                ? "BLOCKED_UNTIL_TARGET_PHASE"
                : "PENDING_MATERIALIZATION_NOT_READY",
            BusinessWritePerformed = false
        };
    }

    public static string ResolveReplay(
        DynamicFlowRuntimeCommandReceipt existing,
        string requestHash)
    {
        if (!FixedEquals(existing.RequestHash, requestHash))
            throw new InvalidOperationException("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT");
        return existing.ResultSnapshot?.ToJson() ?? "{}";
    }

    public static IReadOnlyList<string> NormalizeTargets(IEnumerable<string>? targets)
    {
        var normalized = (targets ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();
        if (normalized.Any(value => !ObjectId.TryParse(value, out _)))
            throw new InvalidOperationException("DYNAMIC_FLOW_TARGET_UNIT_INVALID");
        var result = normalized.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        if (result.Count == 0)
            throw new InvalidOperationException("DYNAMIC_FLOW_TARGET_UNIT_REQUIRED");
        return result;
    }

    private static string CanonicalJson(string? value)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void RequireToken(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("DYNAMIC_FLOW_COMMAND_FIELD_REQUIRED", name);
    }
}
