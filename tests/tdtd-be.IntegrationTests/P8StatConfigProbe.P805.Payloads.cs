using System.Text;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string AdvancedConfigsCollection =
        "work_assignment_advanced_summary_configs";
    private const string TokenLedgersCollection =
        "work_summary_token_ledgers";
    private static readonly string[] AdvancedConfigWrites =
        [AdvancedConfigsCollection, ReceiptsCollection];
    private static readonly string[] AdvancedLockWrites =
        [AdvancedConfigsCollection, ReceiptsCollection, TokenLedgersCollection];
    private static readonly string[] TokenMutationWrites =
        [TokenLedgersCollection, ReceiptsCollection];

    private static string AdvancedConfigRoute(P8AdvancedFixture fixture)
        => $"api/work-assignment-advanced-summary/assignments/" +
           $"{fixture.Assignment.Id}/templates/{fixture.DynamicFormTemplateId}/" +
           $"sections/{fixture.SectionId}/config";

    private static string TokenPoolGrantRoute(string ownerUnitId)
        => $"api/work-summary-tokens/pools/{ownerUnitId}/grants";

    private static string TokenCompensationRoute(string ledgerId)
        => $"api/work-summary-tokens/entries/{ledgerId}/compensations";

    private static JsonObject AdvancedSourceScope(
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

    private static JsonObject AdvancedTarget(
        string fieldId,
        string dataType = "NUMBER",
        string operation = "SUM")
        => new()
        {
            ["fieldId"] = fieldId,
            ["dataType"] = dataType,
            ["operation"] = operation
        };

    private static JsonObject AdvancedSection(
        string sectionId,
        bool isCumulative,
        IEnumerable<JsonObject> targets)
        => new()
        {
            ["sectionId"] = sectionId,
            ["isCumulative"] = isCumulative,
            ["targets"] = new JsonArray(
                targets.Select(target => target.DeepClone()).ToArray())
        };

    private static JsonObject AdvancedPayload(
        P8AdvancedFixture fixture,
        int targetCount = 1,
        bool isCumulative = false,
        IEnumerable<string>? hierarchyGrains = null,
        IEnumerable<JsonObject>? sections = null,
        string? description = null,
        JsonObject? sourceScope = null)
    {
        var sectionPayloads = sections?.ToArray() ??
        [
            AdvancedSection(
                fixture.SectionId,
                isCumulative,
                Enumerable.Range(1, targetCount)
                    .Select(index => AdvancedTarget(
                        AdvancedFieldId(index))))
        ];
        var result = new JsonObject
        {
            ["sourceScope"] = (sourceScope ?? AdvancedSourceScope()).DeepClone(),
            ["sections"] = new JsonArray(
                sectionPayloads.Select(section => section.DeepClone()).ToArray()),
            ["hierarchyGrains"] = new JsonArray(
                (hierarchyGrains ?? ["DAY", "MONTH", "YEAR"])
                .Select(value => JsonValue.Create(value)).ToArray()),
            ["grouping"] = new JsonArray(
                JsonValue.Create("ASSIGNMENT"),
                JsonValue.Create("PERIOD"),
                JsonValue.Create("UNIT")),
            ["ordering"] = new JsonArray(),
            ["description"] = description
        };
        return result;
    }

    private static JsonObject AdvancedPayloadAtCanonicalUtf8Bytes(
        P8AdvancedFixture fixture,
        int exactBytes)
    {
        var payload = AdvancedPayload(
            fixture,
            description: string.Empty,
            sourceScope: new JsonObject
            {
                ["mode"] = "DIRECT_CHILDREN_OR_SELF",
                ["flowInstanceId"] = null,
                ["flowStepId"] = null,
                ["flowBranchId"] = null,
                ["flowEffectiveStatus"] = null
            });
        var emptyBytes = Encoding.UTF8.GetByteCount(Canonicalize(payload));
        HarnessAssert.True(emptyBytes <= exactBytes,
            $"Advanced canonical payload base {emptyBytes} exceeds {exactBytes}");
        payload["description"] = new string('x', exactBytes - emptyBytes);
        HarnessAssert.Equal(exactBytes,
            Encoding.UTF8.GetByteCount(Canonicalize(payload)),
            "Advanced exact canonical UTF-8 fixture size mismatch");
        return payload;
    }

    private static string AdvancedFieldId(int index)
        => $"adv_number_{index:0000}";

    private static JsonObject AdvancedActionPayload(string? reason = null)
    {
        var payload = new JsonObject();
        if (reason is not null)
            payload["reason"] = reason;
        return payload;
    }

    private static JsonObject TokenGrantPayload(int units, string reason)
        => new()
        {
            ["units"] = units,
            ["reason"] = reason
        };

    private static JsonObject TokenCompensationPayload(string reason)
        => new() { ["reason"] = reason };
}

