using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public enum DynamicFlowTypedFactKind
{
    String,
    Decimal,
    Bool,
    Date,
    Null
}

public sealed record DynamicFlowTypedFact(
    DynamicFlowTypedFactKind Kind,
    object? Value);

public sealed record DynamicFlowTypedFactSnapshot(
    IReadOnlyDictionary<string, DynamicFlowTypedFact> Facts,
    string CanonicalJson,
    string SnapshotHash);

public sealed record DynamicFlowConditionEvaluation(
    bool Matched,
    string ReasonCode,
    string? ErrorCode = null);

public sealed record DynamicFlowConditionalDecision(
    string SelectedEdgeId,
    string SelectedNodeId,
    string ReasonCode,
    string EvaluatorVersion,
    string InputSnapshotHash);

/// <summary>
/// Frozen P6-05 contract. Facts are a small server-owned projection; raw form
/// payloads, arbitrary paths, scripts and mapping sources are intentionally
/// absent.
/// </summary>
public static class DynamicFlowTypedConditionalTopologyContract
{
    public const string ArchetypeId = "FLOW-T07";
    public const int GatewayVersion = 1;
    public const string EvaluatorVersion = "P6-TYPED-AST-1";
    public const string TypeMismatch =
        "DYNAMIC_FLOW_CONDITION_TYPE_MISMATCH";
    public const string OperatorUnsupported =
        "DYNAMIC_FLOW_CONDITION_OPERATOR_UNSUPPORTED";
    public const string FactPathForbidden =
        "DYNAMIC_FLOW_CONDITION_FACT_PATH_FORBIDDEN";
    public const string NoBranchMatched =
        "DYNAMIC_FLOW_CONDITION_NO_BRANCH_MATCHED";

    private static readonly IReadOnlyDictionary<string, DynamicFlowTypedFactKind>
        FactSchema = new Dictionary<string, DynamicFlowTypedFactKind>(
            StringComparer.Ordinal)
        {
            ["assignment.isActive"] = DynamicFlowTypedFactKind.Bool,
            ["assignment.flowAttemptNo"] = DynamicFlowTypedFactKind.Decimal,
            ["step.attemptNo"] = DynamicFlowTypedFactKind.Decimal,
            ["step.state"] = DynamicFlowTypedFactKind.String,
            ["instance.periodKey"] = DynamicFlowTypedFactKind.String,
            ["report.exists"] = DynamicFlowTypedFactKind.Bool,
            ["report.status"] = DynamicFlowTypedFactKind.String,
            ["report.payloadRevision"] = DynamicFlowTypedFactKind.Decimal,
            ["report.reportDate"] = DynamicFlowTypedFactKind.Date,
            ["review.outcome"] = DynamicFlowTypedFactKind.String
        };

    public static IReadOnlySet<string> AllowedFactPaths { get; } =
        FactSchema.Keys.ToHashSet(StringComparer.Ordinal);

    public static DynamicFlowParallelForkTopology Require(
        string canonicalDefinitionJson,
        string expectedPayloadHash)
    {
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                canonicalDefinitionJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        if (!FixedEquals(canonical.PayloadHash, expectedPayloadHash))
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_TOPOLOGY_PIN_STALE");

        var payload = canonical.Payload;
        if (payload.ArchetypeId != ArchetypeId ||
            payload.Nodes.Count != 4 ||
            payload.Edges.Count != 3 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.FormStep) != 3 ||
            payload.Nodes.Count(node =>
                node.NodeKind == DynamicFlowNodeKinds.Gateway &&
                node.Gateway?.Kind == DynamicFlowGatewayKinds.Condition) != 1 ||
            payload.Nodes.Any(node =>
                node.NodeKind == DynamicFlowNodeKinds.Final))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T07_CONDITIONAL_TOPOLOGY_REQUIRED");
        }

        var entry = payload.Nodes.SingleOrDefault(node =>
                        node.NodeId == payload.EntryStepId)
                    ?? throw new InvalidOperationException(
                        "DYNAMIC_FLOW_T07_ENTRY_PIN_MISSING");
        var gateway = payload.Nodes.Single(node =>
            node.NodeKind == DynamicFlowNodeKinds.Gateway);
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

        var entryEdges = outgoing[entry.NodeId];
        var orderedEdges = outgoing[gateway.NodeId]
            .OrderBy(edge => edge.TransitionId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal)
            .ToArray();
        var branchNodes = orderedEdges
            .Select(edge => payload.Nodes.Single(node =>
                node.NodeId == edge.ToNodeId))
            .ToArray();
        if (entry.NodeKind != DynamicFlowNodeKinds.FormStep ||
            incoming[entry.NodeId] != 0 ||
            entryEdges.Count != 1 ||
            entryEdges[0].Condition is not null ||
            entryEdges[0].ToNodeId != gateway.NodeId ||
            incoming[gateway.NodeId] != 1 ||
            gateway.Gateway?.ExpectedIncomingNodeIds.Count > 0 ||
            gateway.Gateway?.RequiredIncomingCount is not null ||
            orderedEdges.Length != 2 ||
            orderedEdges.Any(edge => edge.Condition is null) ||
            orderedEdges[0].Condition!.Operator == "TRUE" ||
            orderedEdges[1].Condition!.Operator != "TRUE" ||
            branchNodes.DistinctBy(node => node.NodeId).Count() != 2 ||
            branchNodes.Any(node =>
                node.NodeKind != DynamicFlowNodeKinds.FormStep ||
                incoming[node.NodeId] != 1 ||
                outgoing[node.NodeId].Count != 0))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T07_CONDITIONAL_TOPOLOGY_REQUIRED");
        }

        foreach (var edge in orderedEdges)
            RequireAuthorizedCondition(edge.Condition!);

        var orderedBranches = orderedEdges
            .Select((edge, index) =>
            {
                var node = payload.Nodes.Single(candidate =>
                    candidate.NodeId == edge.ToNodeId);
                return new DynamicFlowForkBranch(
                    edge,
                    node,
                    RequireForm(payload, node),
                    index + 2);
            })
            .ToArray();
        return new DynamicFlowParallelForkTopology(
            payload,
            entry,
            RequireForm(payload, entry),
            entryEdges[0],
            gateway,
            orderedBranches,
            canonical.CanonicalJson,
            canonical.PayloadHash);
    }

    public static DynamicFlowTypedFactSnapshot BuildFactSnapshot(
        WorkAssignment assignment,
        DynamicFlowStepInstance step,
        DynamicFlowInstance instance,
        WorkAssignmentReport? report)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(instance);
        var facts = new SortedDictionary<string, DynamicFlowTypedFact>(
            StringComparer.Ordinal)
        {
            ["assignment.isActive"] = new(
                DynamicFlowTypedFactKind.Bool,
                assignment.IsActive),
            ["assignment.flowAttemptNo"] = new(
                DynamicFlowTypedFactKind.Decimal,
                Convert.ToDecimal(assignment.FlowAttemptNo ?? 0)),
            ["step.attemptNo"] = new(
                DynamicFlowTypedFactKind.Decimal,
                Convert.ToDecimal(step.AttemptNo)),
            ["step.state"] = new(
                DynamicFlowTypedFactKind.String,
                step.State),
            ["instance.periodKey"] = new(
                DynamicFlowTypedFactKind.String,
                instance.PeriodKey),
            ["report.exists"] = new(
                DynamicFlowTypedFactKind.Bool,
                report is not null),
            ["report.status"] = report is null
                ? new(DynamicFlowTypedFactKind.Null, null)
                : new(
                    DynamicFlowTypedFactKind.String,
                    report.Status.ToString().ToUpperInvariant()),
            ["report.payloadRevision"] = report is null
                ? new(DynamicFlowTypedFactKind.Null, null)
                : new(
                    DynamicFlowTypedFactKind.Decimal,
                    Convert.ToDecimal(report.PayloadRevision)),
            ["report.reportDate"] = report?.ReportDate is { } reportDate
                ? new(
                    DynamicFlowTypedFactKind.Date,
                    NormalizeDate(reportDate))
                : new(DynamicFlowTypedFactKind.Null, null),
            ["review.outcome"] = string.IsNullOrWhiteSpace(
                report?.ReviewerEvaluation)
                ? new(DynamicFlowTypedFactKind.Null, null)
                : new(
                    DynamicFlowTypedFactKind.String,
                    report!.ReviewerEvaluation!.Trim())
        };
        var canonical = JsonSerializer.Serialize(
            facts.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    type = pair.Value.Kind.ToString().ToLowerInvariant(),
                    value = CanonicalFactValue(pair.Value)
                },
                StringComparer.Ordinal));
        return new DynamicFlowTypedFactSnapshot(
            facts,
            canonical,
            Hash(canonical));
    }

    public static DynamicFlowConditionalDecision SelectBranch(
        DynamicFlowParallelForkTopology topology,
        DynamicFlowTypedFactSnapshot snapshot)
    {
        if (topology.Payload.ArchetypeId != ArchetypeId)
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_T07_CONDITIONAL_TOPOLOGY_REQUIRED");
        var ordered = topology.Branches
            .OrderBy(branch => branch.Edge.TransitionId, StringComparer.Ordinal)
            .ThenBy(branch => branch.Edge.ToNodeId, StringComparer.Ordinal)
            .ToArray();
        foreach (var branch in ordered)
        {
            var condition = branch.Edge.Condition
                            ?? throw new InvalidOperationException(
                                "DYNAMIC_FLOW_T07_CONDITION_REQUIRED");
            var evaluated = Evaluate(condition, snapshot);
            if (evaluated.ErrorCode is not null)
                throw new InvalidOperationException(evaluated.ErrorCode);
            if (!evaluated.Matched)
                continue;
            return new DynamicFlowConditionalDecision(
                branch.Edge.TransitionId,
                branch.Node.NodeId,
                condition.Operator == "TRUE"
                    ? "DEFAULT_BRANCH_SELECTED"
                    : "RULE_MATCHED",
                EvaluatorVersion,
                snapshot.SnapshotHash);
        }
        throw new InvalidOperationException(NoBranchMatched);
    }

    public static DynamicFlowConditionEvaluation Evaluate(
        DynamicFlowConditionDto condition,
        DynamicFlowTypedFactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(snapshot);
        return EvaluateNode(condition, snapshot.Facts);
    }

    public static string BuildDecisionLedgerId(
        string gatewayInstanceId,
        int gatewayVersion)
        => DynamicFlowParallelForkTopologyContract.Hash(
            $"{gatewayInstanceId}\n{gatewayVersion}\nconditional-decision")[..24];

    private static DynamicFlowConditionEvaluation EvaluateNode(
        DynamicFlowConditionDto condition,
        IReadOnlyDictionary<string, DynamicFlowTypedFact> facts)
    {
        switch (condition.Operator)
        {
            case "TRUE":
                return Match(true, "CONSTANT_TRUE");
            case "FALSE":
                return Match(false, "CONSTANT_FALSE");
            case "AND":
            case "OR":
            {
                var children = condition.Children
                    .Select(child => EvaluateNode(child, facts))
                    .ToArray();
                var error = children.FirstOrDefault(child =>
                    child.ErrorCode is not null);
                if (error is not null)
                    return error;
                return condition.Operator == "AND"
                    ? Match(children.All(child => child.Matched), "AND")
                    : Match(children.Any(child => child.Matched), "OR");
            }
            case "NOT":
            {
                var child = EvaluateNode(condition.Children.Single(), facts);
                return child.ErrorCode is null
                    ? Match(!child.Matched, "NOT")
                    : child;
            }
        }

        if (condition.Field is null ||
            !FactSchema.TryGetValue(condition.Field, out var expectedKind))
        {
            return Error(FactPathForbidden);
        }
        var fact = facts.TryGetValue(condition.Field, out var found)
            ? found
            : new DynamicFlowTypedFact(DynamicFlowTypedFactKind.Null, null);
        switch (condition.Operator)
        {
            case "EXISTS":
                return Match(fact.Kind != DynamicFlowTypedFactKind.Null, "EXISTS");
            case "NOT_EXISTS":
                return Match(fact.Kind == DynamicFlowTypedFactKind.Null, "NOT_EXISTS");
            case "IS_NULL":
                return Match(fact.Kind == DynamicFlowTypedFactKind.Null, "IS_NULL");
            case "IS_NOT_NULL":
                return Match(fact.Kind != DynamicFlowTypedFactKind.Null, "IS_NOT_NULL");
        }

        if (condition.Operator is "IN" or "NOT_IN")
        {
            var converted = condition.Values
                .Select(value => ConvertLiteral(value, expectedKind))
                .ToArray();
            if (converted.Any(value => value.ErrorCode is not null))
                return Error(TypeMismatch);
            var matched = converted.Any(value =>
                ValuesEqual(fact, value.Value!));
            return Match(
                condition.Operator == "IN" ? matched : !matched,
                condition.Operator);
        }

        if (!condition.Value.HasValue)
            return Error(TypeMismatch);
        var literal = ConvertLiteral(condition.Value.Value, expectedKind);
        if (literal.ErrorCode is not null)
            return Error(literal.ErrorCode);
        if (condition.Operator is "EQ" or "NE")
        {
            var equal = ValuesEqual(fact, literal.Value!);
            return Match(condition.Operator == "EQ" ? equal : !equal, condition.Operator);
        }
        if (condition.Operator is not ("GT" or "GTE" or "LT" or "LTE"))
            return Error(OperatorUnsupported);
        if (fact.Kind == DynamicFlowTypedFactKind.Null ||
            literal.Value!.Kind == DynamicFlowTypedFactKind.Null)
        {
            return Match(false, "NULL_NO_MATCH");
        }
        if (fact.Kind != expectedKind || literal.Value.Kind != expectedKind)
            return Error(TypeMismatch);
        var comparison = Compare(fact, literal.Value);
        if (comparison is null)
            return Error(TypeMismatch);
        return Match(condition.Operator switch
        {
            "GT" => comparison > 0,
            "GTE" => comparison >= 0,
            "LT" => comparison < 0,
            "LTE" => comparison <= 0,
            _ => false
        }, condition.Operator);
    }

    private static (DynamicFlowTypedFact? Value, string? ErrorCode)
        ConvertLiteral(
            JsonElement value,
            DynamicFlowTypedFactKind expectedKind)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return (new DynamicFlowTypedFact(
                DynamicFlowTypedFactKind.Null,
                null), null);
        try
        {
            return expectedKind switch
            {
                DynamicFlowTypedFactKind.Bool
                    when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    (new(expectedKind, value.GetBoolean()), null),
                DynamicFlowTypedFactKind.Decimal
                    when value.ValueKind == JsonValueKind.Number &&
                         value.TryGetDecimal(out var number) =>
                    (new(expectedKind, number), null),
                DynamicFlowTypedFactKind.String
                    when value.ValueKind == JsonValueKind.String =>
                    (new(expectedKind, value.GetString() ?? string.Empty), null),
                DynamicFlowTypedFactKind.Date
                    when value.ValueKind == JsonValueKind.String &&
                         DateTimeOffset.TryParse(
                             value.GetString(),
                             CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal |
                             DateTimeStyles.AdjustToUniversal,
                             out var date) =>
                    (new(expectedKind, date.UtcDateTime), null),
                _ => (null, TypeMismatch)
            };
        }
        catch (Exception error) when (
            error is FormatException or
            InvalidOperationException or
            OverflowException)
        {
            return (null, TypeMismatch);
        }
    }

    private static bool ValuesEqual(
        DynamicFlowTypedFact left,
        DynamicFlowTypedFact right)
    {
        if (left.Kind == DynamicFlowTypedFactKind.Null ||
            right.Kind == DynamicFlowTypedFactKind.Null)
        {
            return left.Kind == right.Kind;
        }
        if (left.Kind != right.Kind)
            return false;
        return Compare(left, right) == 0;
    }

    private static int? Compare(
        DynamicFlowTypedFact left,
        DynamicFlowTypedFact right)
        => left.Kind switch
        {
            DynamicFlowTypedFactKind.String =>
                string.Compare(
                    (string)left.Value!,
                    (string)right.Value!,
                    StringComparison.Ordinal),
            DynamicFlowTypedFactKind.Decimal =>
                ((decimal)left.Value!).CompareTo((decimal)right.Value!),
            DynamicFlowTypedFactKind.Bool =>
                ((bool)left.Value!).CompareTo((bool)right.Value!),
            DynamicFlowTypedFactKind.Date =>
                ((DateTime)left.Value!).CompareTo((DateTime)right.Value!),
            DynamicFlowTypedFactKind.Null => 0,
            _ => null
        };

    private static DynamicFlowConditionEvaluation Match(
        bool matched,
        string reason)
        => new(matched, matched ? reason : $"{reason}_NO_MATCH");

    private static DynamicFlowConditionEvaluation Error(string code)
        => new(false, "FAIL_CLOSED", code);

    private static void RequireAuthorizedCondition(
        DynamicFlowConditionDto condition)
    {
        if (condition.Field is not null &&
            !AllowedFactPaths.Contains(condition.Field))
        {
            throw new InvalidOperationException(FactPathForbidden);
        }
        foreach (var child in condition.Children)
            RequireAuthorizedCondition(child);
    }

    private static DynamicFlowFormNodeDto RequireForm(
        DynamicFlowTemplatePayloadDto payload,
        DynamicFlowTopologyNodeDto node)
        => payload.FormNodes.SingleOrDefault(form =>
               form.FormNodeId == node.FormNodeId)
           ?? throw new InvalidOperationException(
               "DYNAMIC_FLOW_T07_FORM_PIN_MISSING");

    private static string? CanonicalFactValue(DynamicFlowTypedFact fact)
        => fact.Kind switch
        {
            DynamicFlowTypedFactKind.Null => null,
            DynamicFlowTypedFactKind.Bool =>
                ((bool)fact.Value!).ToString().ToLowerInvariant(),
            DynamicFlowTypedFactKind.Decimal =>
                ((decimal)fact.Value!).ToString(
                    "G29",
                    CultureInfo.InvariantCulture),
            DynamicFlowTypedFactKind.Date =>
                NormalizeDate((DateTime)fact.Value!).ToString(
                    "O",
                    CultureInfo.InvariantCulture),
            _ => (string?)fact.Value
        };

    private static DateTime NormalizeDate(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
