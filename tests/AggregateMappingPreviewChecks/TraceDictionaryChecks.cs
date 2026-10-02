using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Services.AggregateMapping;

internal static class TraceDictionaryChecks
{
    internal static void Run(AggregatePreviewEnvelope sample, Action<bool, string> check)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var trace = new AggregateTrace("report", "unit", "202609", "table", 2, 3);
        var second = trace with { Row = 4 };
        var traces = Enumerable.Range(0, 600).Select(i => i % 2 == 0 ? trace : second).ToArray();
        var wide = sample with { Lineage = new Dictionary<string, IReadOnlyList<AggregateTrace>> { ["wide"] = traces } };
        var json = JsonSerializer.Serialize(wide, options);
        using var doc = JsonDocument.Parse(json);
        check(doc.RootElement.GetProperty("traceDictionary").GetProperty("entries").GetArrayLength() >= 2, "wide envelope uses trace dictionary");
        var decoded = JsonSerializer.Deserialize<AggregatePreviewEnvelope>(json, options)!;
        check(decoded.Lineage["wide"].SequenceEqual(traces), "trace dictionary preserves order, coordinates and repeated contributions");
        check(JsonSerializer.Serialize(decoded, options) == json, "trace dictionary round trip is deterministic for confirmation hashing");
        var legacy = sample with { Lineage = new Dictionary<string, IReadOnlyList<AggregateTrace>> { ["small"] = [trace, trace] } };
        var oldJson = JsonSerializer.Serialize(legacy, options);
        check(!oldJson.Contains("traceDictionary") && JsonSerializer.Deserialize<AggregatePreviewEnvelope>(oldJson, options)!.Lineage["small"].Count == 2, "legacy envelope remains readable");
        void Invalid(Action<JsonObject> edit)
        {
            var root = JsonNode.Parse(json)!.AsObject(); edit(root);
            try { JsonSerializer.Deserialize<AggregatePreviewEnvelope>(root.ToJsonString(), options); throw new InvalidOperationException("malformed dictionary accepted"); }
            catch (JsonException) { check(true, "malformed trace dictionary fails closed"); }
        }
        Invalid(r => r["traceDictionary"]!["version"] = 2);
        Invalid(r => r["lineage"]!["wide"]![0]!["$trace"] = -1);
        Invalid(r => r["traceDictionary"]!["entries"]![0]![0] = 100000);
        Invalid(r => r["lineage"]!["wide"]![0]!["extra"] = true);
        var longValue = new AggregateValue("TEXT", "VALUE", Text: new string('x', 1000));
        try { AggregateValueBudget.Charge(longValue, new(default, maxBytes: 100)); throw new InvalidOperationException("value budget bypassed"); }
        catch (AggregatePreviewException e) when (e.Code == "AGG_BUDGET_EXCEEDED") { check(true, "retained value accounting still rejects oversized values"); }
    }
}
