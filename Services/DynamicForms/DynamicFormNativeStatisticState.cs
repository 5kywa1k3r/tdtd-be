using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

internal static class DynamicFormNativeStatisticState
{
    // Structural equality deliberately excludes P8-owned statistic metadata.
    // No replacement/default metadata is returned or persisted by this view.
    internal static string StructureJson(string json)
    {
        var array = JsonNode.Parse(json)!.AsArray();
        foreach (var item in array)
        {
            var table = item!.AsObject();
            table.Remove("statisticPlan");
            foreach (var node in table["statisticTargets"]!.AsArray())
            {
                var target = node!.AsObject();
                target.Remove("isStatistic"); target.Remove("statistic"); target.Remove("statisticLabelCodes");
            }
        }
        return StatConfigCanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(array));
    }

    // Bind the captured config's metadata to the immutable schema axes/options.
    // The original published definition and configuration remain unchanged.
    internal static List<DynamicFormNativeTableDto> BindMetadata(IReadOnlyList<DynamicFormNativeTableDto> tables,
        DynamicFormNativeStatisticsPayload metadata)
    {
        if (metadata is null || metadata.Version != 2 || metadata.Tables is null || metadata.Tables.Count != tables.Count
            || metadata.Tables.Any(t => t is null || t.StatisticTargets is null)
            || metadata.Tables.Select(t => t.TableId).Distinct(StringComparer.Ordinal).Count() != tables.Count)
            Fail("PLAN_METADATA_TABLE_SET_MISMATCH");
        return tables.Select(table =>
        {
            var captured = metadata.Tables!.SingleOrDefault(t => t.TableId == table.Id);
            if (captured is null || captured.StatisticTargets!.Any(t => t is null)
                || StatConfigCanonicalJson.Canonicalize(captured.StatisticTargets!.Select(t => new { t.Id, t.Target }))
                    != StatConfigCanonicalJson.Canonicalize(table.StatisticTargets!.Select(t => new { t.Id, t.Target })))
                Fail("PLAN_METADATA_TARGET_STRUCTURE_MISMATCH");
            return table with { StatisticTargets = captured!.StatisticTargets!.ToList(), StatisticPlan = captured.StatisticPlan };
        }).ToList();
    }

    internal static bool HasPlan(IEnumerable<DynamicFormNativeTableDto> tables)
        => tables.Any(table => table.StatisticPlan is not null);

    internal static bool IsOverridden(DynamicFormNativeTableDto table, string id)
        => table.StatisticPlan?.Targets?.Any(target => target.Id == id) == true;

    // Validate the original JSON before optional-null DTO properties disappear.
    // Reuse complete-plan syntax for every enabled target, including nested options.
    internal static void ValidateWirePlans(JsonElement tables, bool metadata)
    {
        if (tables.ValueKind != JsonValueKind.Array) Fail("PLAN_TABLES_REQUIRED");
        foreach (var table in tables.EnumerateArray())
        {
            if (table.ValueKind != JsonValueKind.Object) Fail("PLAN_TABLE_REQUIRED");
            if (!table.TryGetProperty("statisticPlan", out var plan)) continue;
            if (!table.TryGetProperty(metadata ? "tableId" : "id", out var tableId)
                || tableId.ValueKind != JsonValueKind.String) Fail("PLAN_TABLE_ID_REQUIRED");
            if (plan.ValueKind != JsonValueKind.Object || plan.EnumerateObject().Count() != 2
                || !plan.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 2
                || !plan.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
                Fail("PLAN_STATE_INVALID");
            var complete = new JsonArray();
            foreach (var target in plan.GetProperty("targets").EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("id", out _)
                    || !target.TryGetProperty("isStatistic", out var flag)
                    || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("PLAN_STATE_INVALID");
                if (!target.GetProperty("isStatistic").GetBoolean())
                {
                    if (target.EnumerateObject().Count() != 2) Fail("PLAN_DISABLED_SETTINGS_MUST_REMAIN_LOCAL");
                    continue;
                }
                var config = JsonNode.Parse(target.GetRawText())!.AsObject();
                // These identities are supplied by the containing Table/state.
                if (config.ContainsKey("tableId") || config.ContainsKey("targetId")) Fail("PLAN_STATE_INVALID");
                config["targetId"] = config["id"]?.DeepClone();
                config.Remove("id"); config.Remove("isStatistic");
                config["tableId"] = JsonNode.Parse(tableId.GetRawText());
                complete.Add(config);
            }
            _ = DynamicFormNativeStatisticPlan.Read(new JsonObject { ["version"] = 2, ["targets"] = complete }.ToJsonString());
        }
    }

    internal static DynamicFormNativeStatisticsPayload Metadata(IReadOnlyList<DynamicFormNativeTableDto> tables)
        => new(2, tables.Select(table => new DynamicFormNativeTableStatisticsPayload(
            table.Id, table.StatisticTargets?.Select(target => target with { ShowOnOverview = null }).ToList(),
            table.StatisticPlan)).ToArray());

    internal static DynamicFormNativeStatisticPlanDto Plan(IReadOnlyList<DynamicFormNativeTableDto> tables)
        => new(2, tables.SelectMany(table => (table.StatisticPlan?.Targets
            ?? Array.Empty<DynamicFormNativeTableStatisticStateDto>()).Where(target => target.IsStatistic == true)
            .Select(target => new DynamicFormNativeStatisticPlanTargetDto(table.Id, target.Id,
                target.Selector, target.Grouping, target.Input, target.Order, target.Operations,
                target.StatisticLabelCodes, target.ShowInDetail, target.ShowInTree))).ToArray());

    // Called after every ordinary axis and v1 target ID has been collected.
    // Never calls the definition validator recursively or supplies defaults.
    internal static void ValidateShapeAndIds(IReadOnlyList<DynamicFormNativeTableDto> tables, HashSet<string> reserved)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var plan = table.StatisticPlan;
            if (plan is null) continue;
            if (plan.Version != 2 || plan.Targets is null || plan.Targets.Count > 30) Fail("PLAN_STATE_INVALID");
            foreach (var target in plan.Targets!)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.Id) || target.Id != target.Id.Trim()
                    || target.IsStatistic is null || !ids.Add(target.Id)
                    || reserved.Contains(target.Id) && !table.StatisticTargets!.Any(old => old.Id == target.Id))
                    Fail("PLAN_STATE_ID_INVALID");
                if (target!.IsStatistic == false && (target.Selector is not null || target.Grouping is not null
                    || target.Input is not null || target.Order is not null || target.Operations is not null
                    || target.StatisticLabelCodes is not null || target.ShowInDetail is not null || target.ShowInTree is not null))
                    Fail("PLAN_DISABLED_SETTINGS_MUST_REMAIN_LOCAL");
            }
        }
        if (HasPlan(tables)) _ = DynamicFormNativeStatisticPlan.Read(JsonSerializer.Serialize(Plan(tables), StatConfigCanonicalJson.StrictJsonOptions));
    }

    internal static DynamicFormNativeStatisticPlan.PreparedPlan Prepare(IReadOnlyList<DynamicFormNativeTableDto> tables,
        string sections, string fields, string blocks)
        => DynamicFormNativeStatisticPlan.Prepare(JsonSerializer.Serialize(Plan(tables), StatConfigCanonicalJson.StrictJsonOptions),
            tables, sections, fields, blocks);

    internal static void ValidateSemantics(IReadOnlyList<DynamicFormNativeTableDto> tables, string sections, string fields, string blocks)
    {
        if (HasPlan(tables)) _ = Prepare(tables, sections, fields, blocks);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string reason)
        => throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason, path = "tables.statisticPlan" });
}
