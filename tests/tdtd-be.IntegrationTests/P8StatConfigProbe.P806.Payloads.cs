using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string DiffConfigsCollection =
        "work_report_statistic_diff_configs";
    private static readonly string[] DiffConfigWrites =
        [DiffConfigsCollection, ReceiptsCollection];

    private static string DiffConfigRoute(P8DiffFixture fixture)
        => $"api/work-report-statistic-diffs/assignments/" +
           $"{fixture.Assignment.Id}/templates/" +
           $"{fixture.DynamicFormTemplateId}/config";

    private static JsonObject DiffSelector(
        string conceptKind,
        string conceptKey,
        string conceptCode,
        string dataType = "NUMBER")
        => new()
        {
            ["conceptKind"] = conceptKind,
            ["conceptKey"] = conceptKey,
            ["conceptCode"] = conceptCode,
            ["dataType"] = dataType
        };

    private static JsonObject DiffExactPeriod(string periodKey)
        => new()
        {
            ["mode"] = "EXACT",
            ["periodKey"] = periodKey
        };

    private static JsonObject DiffRangePeriod(
        string periodKeyFrom,
        string periodKeyTo)
        => new()
        {
            ["mode"] = "RANGE",
            ["periodKeyFrom"] = periodKeyFrom,
            ["periodKeyTo"] = periodKeyTo
        };

    private static JsonObject DiffSourceScope(
        string mode = "DIRECT_CHILDREN_OR_SELF",
        string? flowInstanceId = null,
        string? flowStepId = null,
        string? flowBranchId = null,
        string? flowEffectiveStatus = null)
        => new()
        {
            ["mode"] = mode,
            ["flowInstanceId"] = flowInstanceId,
            ["flowStepId"] = flowStepId,
            ["flowBranchId"] = flowBranchId,
            ["flowEffectiveStatus"] = flowEffectiveStatus
        };

    private static JsonObject DiffSide(
        JsonObject selector,
        JsonObject period,
        JsonObject? sourceScope = null)
        => new()
        {
            ["selector"] = selector.DeepClone(),
            ["period"] = period.DeepClone(),
            ["sourceScope"] = (sourceScope ?? DiffSourceScope()).DeepClone()
        };

    private static JsonObject DiffPayload(
        JsonObject selector,
        JsonObject? rightSelector = null,
        JsonObject? leftPeriod = null,
        JsonObject? rightPeriod = null,
        JsonObject? leftScope = null,
        JsonObject? rightScope = null,
        string direction = "LEFT_TO_RIGHT",
        string missingPolicy = "REJECT",
        string emptyPolicy = "REJECT",
        string name = "P8 Diff configuration")
        => new()
        {
            ["name"] = name,
            ["left"] = DiffSide(
                selector,
                leftPeriod ?? DiffExactPeriod("2026-08"),
                leftScope),
            ["right"] = DiffSide(
                rightSelector ?? selector,
                rightPeriod ?? DiffExactPeriod("2026-07"),
                rightScope),
            ["direction"] = direction,
            ["missingPolicy"] = missingPolicy,
            ["emptyPolicy"] = emptyPolicy
        };
}
