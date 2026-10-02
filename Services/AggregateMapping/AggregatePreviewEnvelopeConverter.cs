using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// Lossless trace dictionary for wide previews and their frozen snapshots. Array positions
// and repeated contributions are retained; this is storage/transport encoding, not deduplication
// of arithmetic evidence. Old envelopes without a dictionary remain readable.
internal sealed class AggregatePreviewEnvelopeConverter : JsonConverter<AggregatePreviewEnvelope>
{
    private sealed record Payload(AggregatePreviewResponseDto Preview,
        IReadOnlyDictionary<string, IReadOnlyList<AggregateTrace>> Lineage,
        IReadOnlyList<AggregatePreviewValueDiff> Diff, IReadOnlyList<AggregateFunctionTrace> Functions,
        IReadOnlyList<string> CurrentUnitIds, IReadOnlyList<AggregateSourceValueEvidence> SourceValues,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AggregateListPipelineTrace>? ListOperations = null);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    internal const int MaxEntries = 100_000, MaxReferences = 500_000;
    public override void Write(Utf8JsonWriter writer, AggregatePreviewEnvelope value, JsonSerializerOptions options)
    {
        var root = JsonSerializer.SerializeToElement(new Payload(value.Preview, value.Lineage, value.Diff,
            value.Functions, value.CurrentUnitIds, value.SourceValues, value.ListOperations.Count == 0 ? null : value.ListOperations), Web);
        // Leave small envelopes in the established format.
        if (value.Lineage.Values.Sum(t => (long)t.Count) < 512) { root.WriteTo(writer); return; }
        var sources = new List<string[]>(); var sourceIds = new Dictionary<(string, string, string), int>();
        var entries = new List<object?[]>(); var ids = new Dictionary<AggregateTrace, int>();
        var forms = new List<JsonElement>(); var formIds = new Dictionary<string, int>();
        var reportSets = new List<JsonElement>(); var reportSetIds = new Dictionary<string, int>();
        var references = 0;
        void Write(JsonElement item, string? property = null)
        {
            if (property == "form" && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("schemaHash", out _))
            {
                var key = item.GetRawText();
                if (!formIds.TryGetValue(key, out var id)) { id = forms.Count; formIds.Add(key, id); forms.Add(item); }
                writer.WriteStartObject(); writer.WriteNumber("$form", id); writer.WriteEndObject(); return;
            }
            if (property == "reportIds" && item.ValueKind == JsonValueKind.Array)
            {
                var key = item.GetRawText();
                if (!reportSetIds.TryGetValue(key, out var id)) { id = reportSets.Count; reportSetIds.Add(key, id); reportSets.Add(item); }
                writer.WriteStartObject(); writer.WriteNumber("$reportSet", id); writer.WriteEndObject(); return;
            }
            if (TryTrace(item, out var trace))
            {
                if (++references > MaxReferences) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
                if (!ids.TryGetValue(trace!, out var id))
                {
                    if (ids.Count >= MaxEntries) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
                    var source = (trace!.ReportId, trace.UnitId, trace.OccurrenceKey);
                    if (!sourceIds.TryGetValue(source, out var sourceId))
                    { sourceId = sources.Count; sourceIds.Add(source, sourceId); sources.Add([source.Item1, source.Item2, source.Item3]); }
                    id = entries.Count; ids.Add(trace, id); entries.Add([sourceId, trace.MemberId, trace.Row, trace.Column]);
                }
                writer.WriteStartObject(); writer.WriteNumber("$trace", id); writer.WriteEndObject(); return;
            }
            if (item.ValueKind == JsonValueKind.Object)
            { writer.WriteStartObject(); foreach (var p in item.EnumerateObject()) { writer.WritePropertyName(p.Name); Write(p.Value, p.Name); } writer.WriteEndObject(); }
            else if (item.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var i in item.EnumerateArray()) Write(i); writer.WriteEndArray(); }
            else item.WriteTo(writer);
        }
        writer.WriteStartObject();
        foreach (var p in root.EnumerateObject()) { writer.WritePropertyName(p.Name); Write(p.Value, p.Name); }
        writer.WritePropertyName("traceDictionary");
        JsonSerializer.Serialize(writer, new { version = 1, sources, entries, forms, reportSets }, Web);
        writer.WriteEndObject();
    }
    public override AggregatePreviewEnvelope Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var expanded = Expand(root);
        var value = expanded.Deserialize<Payload>(Web) ?? throw new JsonException("AGG_TRACE_DICTIONARY_INVALID");
        return new(value.Preview, value.Lineage, value.Diff, value.Functions, value.CurrentUnitIds) { SourceValues = value.SourceValues ?? [], ListOperations = value.ListOperations ?? [] };
    }
    internal static JsonElement Expand(JsonElement root)
    {
        if (!root.TryGetProperty("traceDictionary", out var dictionary)) return root.Clone();
        try
        {
            if (dictionary.GetProperty("version").GetInt32() != 1) throw new JsonException();
            var sources = dictionary.GetProperty("sources"); var entries = dictionary.GetProperty("entries");
            var forms = dictionary.GetProperty("forms"); var reportSets = dictionary.GetProperty("reportSets");
            if (sources.GetArrayLength() > MaxEntries || entries.GetArrayLength() > MaxEntries || forms.GetArrayLength() > MaxEntries || reportSets.GetArrayLength() > MaxEntries) throw new JsonException();
            var traces = entries.EnumerateArray().Select(entry => {
                if (entry.GetArrayLength() != 4) throw new JsonException();
                var source = sources[entry[0].GetInt32()];
                if (source.GetArrayLength() != 3) throw new JsonException();
                string Text(JsonElement v) => v.GetString() ?? throw new JsonException();
                int? Coordinate(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : v.GetInt32() is var n && n > 0 ? n : throw new JsonException();
                return JsonSerializer.SerializeToElement(new AggregateTrace(Text(source[0]), Text(source[1]), Text(source[2]), Text(entry[1]), Coordinate(entry[2]), Coordinate(entry[3])), Web);
            }).ToArray();
            using var stream = new MemoryStream(); var references = 0;
            using (var writer = new Utf8JsonWriter(stream))
            {
                void Write(JsonElement item, int depth)
                {
                    if (depth > 64 || stream.Length + writer.BytesPending > 64 * 1024 * 1024) throw new JsonException();
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("$trace", out var id))
                    {
                        if (item.EnumerateObject().Count() != 1 || ++references > MaxReferences) throw new JsonException();
                        traces[id.GetInt32()].WriteTo(writer); return;
                    }
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var pair in new[] { ("$form", forms), ("$reportSet", reportSets) })
                            if (item.TryGetProperty(pair.Item1, out var key))
                            {
                                if (item.EnumerateObject().Count() != 1 || ++references > MaxReferences) throw new JsonException();
                                pair.Item2[key.GetInt32()].WriteTo(writer); return;
                            }
                    }
                    if (item.ValueKind == JsonValueKind.Object)
                    { writer.WriteStartObject(); foreach (var p in item.EnumerateObject()) { if (depth == 0 && p.Name == "traceDictionary") continue; writer.WritePropertyName(p.Name); Write(p.Value, depth + 1); } writer.WriteEndObject(); }
                    else if (item.ValueKind == JsonValueKind.Array)
                    { writer.WriteStartArray(); foreach (var i in item.EnumerateArray()) Write(i, depth + 1); writer.WriteEndArray(); }
                    else item.WriteTo(writer);
                }
                Write(root, 0);
            }
            if (stream.Length > 64 * 1024 * 1024) throw new JsonException();
            using var decoded = JsonDocument.Parse(stream.ToArray()); return decoded.RootElement.Clone();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException or KeyNotFoundException or FormatException or OverflowException)
        { throw new JsonException("AGG_TRACE_DICTIONARY_INVALID", ex); }
    }
    private static bool TryTrace(JsonElement value, out AggregateTrace? trace)
    {
        trace = null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("reportId", out var report)
            || !value.TryGetProperty("unitId", out var unit) || !value.TryGetProperty("occurrenceKey", out var occurrence)
            || !value.TryGetProperty("memberId", out var member) || value.EnumerateObject().Any(p => p.Name is not ("reportId" or "unitId" or "occurrenceKey" or "memberId" or "row" or "column"))) return false;
        int? Coordinate(string key) => value.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt32() : null;
        trace = new(report.GetString()!, unit.GetString()!, occurrence.GetString()!, member.GetString()!, Coordinate("row"), Coordinate("column")); return true;
    }
}
