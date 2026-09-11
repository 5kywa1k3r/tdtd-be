using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string BasicConfigsCollection =
        "work_assignment_basic_summary_configs";
    private static readonly string[] BasicMutationWrites =
        [BasicConfigsCollection, ReceiptsCollection];

    private static string BasicConfigRoute(P8BasicFixture fixture)
        => $"api/work-assignment-basic-summary/assignments/" +
           $"{fixture.Assignment.Id}/templates/" +
           $"{fixture.DynamicFormTemplateId}/config";

    private static JsonObject BasicSourceScope(
        string mode,
        string? flowInstanceId = null,
        string? flowStepId = null,
        string? flowBranchId = null,
        string? flowEffectiveStatus = null)
    {
        var result = new JsonObject { ["mode"] = mode };
        if (flowInstanceId is not null)
            result["flowInstanceId"] = flowInstanceId;
        if (flowStepId is not null)
            result["flowStepId"] = flowStepId;
        if (flowBranchId is not null)
            result["flowBranchId"] = flowBranchId;
        if (flowEffectiveStatus is not null)
            result["flowEffectiveStatus"] = flowEffectiveStatus;
        return result;
    }

    private static JsonObject BasicFlowSourceScope(
        P8BasicFixture fixture,
        string mode,
        string effectiveStatus = "EFFECTIVE")
        => BasicSourceScope(
            mode,
            fixture.FlowInstanceId,
            string.Equals(mode, "FLOW_STEP", StringComparison.Ordinal)
                ? fixture.FlowStepId
                : null,
            string.Equals(mode, "FLOW_BRANCH", StringComparison.Ordinal)
                ? fixture.FlowBranchId
                : null,
            effectiveStatus);

    private static JsonObject BasicPeriodRule(
        string mode,
        string? periodKey = null,
        string? periodKeyFrom = null,
        string? periodKeyTo = null)
    {
        var result = new JsonObject { ["mode"] = mode };
        if (periodKey is not null)
            result["periodKey"] = periodKey;
        if (periodKeyFrom is not null)
            result["periodKeyFrom"] = periodKeyFrom;
        if (periodKeyTo is not null)
            result["periodKeyTo"] = periodKeyTo;
        return result;
    }

    private static JsonObject BasicDetailHints(
        bool includeSourceRows = false,
        int maxTextChars = 12000)
        => new()
        {
            ["includeSourceRows"] = includeSourceRows,
            ["maxTextChars"] = maxTextChars
        };

    private static JsonObject BasicTarget(
        string conceptKind,
        string conceptKey,
        string dataType,
        string operation)
        => new()
        {
            ["conceptKind"] = conceptKind,
            ["conceptKey"] = conceptKey,
            ["dataType"] = dataType,
            ["operation"] = operation
        };

    private static JsonObject BasicPayload(
        JsonObject sourceScope,
        JsonObject periodRule,
        IEnumerable<string> groupingHints,
        JsonObject detailHints,
        IEnumerable<JsonObject> targets)
        => new()
        {
            ["sourceScope"] = sourceScope.DeepClone(),
            ["periodRule"] = periodRule.DeepClone(),
            ["groupingHints"] = new JsonArray(
                groupingHints.Select(value => JsonValue.Create(value)).ToArray()),
            ["detailHints"] = detailHints.DeepClone(),
            ["targets"] = new JsonArray(
                targets.Select(value => value.DeepClone()).ToArray())
        };

    private static JsonObject BasicDirectPayload(
        IEnumerable<JsonObject> targets,
        IEnumerable<string>? groupingHints = null,
        JsonObject? periodRule = null,
        JsonObject? detailHints = null,
        string sourceMode = "DIRECT_CHILDREN_OR_SELF")
        => BasicPayload(
            BasicSourceScope(sourceMode),
            periodRule ?? BasicPeriodRule("ALL_PERIODS"),
            groupingHints ?? Array.Empty<string>(),
            detailHints ?? BasicDetailHints(),
            targets);

    private static JsonObject BasicActionPayload()
        => new();
}
