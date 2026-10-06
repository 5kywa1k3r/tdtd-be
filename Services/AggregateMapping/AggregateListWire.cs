using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateListWire
{
    internal const string Kind = "LIST_RECORDS_V1";
    internal const string CompositeKind = "LIST_RECORDS_V2";
    internal static bool Supported(string? kind) => kind is Kind or CompositeKind;
    internal static string KindFor(AggregateListValue list) => list.Records.Any(r => r.Contributors != null) ? CompositeKind : Kind;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static object Encode(AggregateListValue list) => new { kind = KindFor(list), schema = list.Schema,
        records = list.Records.Select(EncodeRow) };
    internal static object EncodeRow(AggregateListRow row)
    {
        var result = new Dictionary<string, object?> { ["key"] = row.Key, ["origin"] = row.Origin,
            ["cells"] = row.Cells.ToDictionary(p => p.Key, p => {
                var cell = new Dictionary<string, object?> { ["type"] = p.Value.Value.Type, ["state"] = p.Value.Value.State,
                    ["value"] = AggregatePreviewService.ToWire(p.Value.Value), ["exact"] = p.Value.Value.Number?.ExactKey, ["lineage"] = p.Value.Trace };
                if (p.Value.Value.TextFormat != null) cell["textFormat"] = p.Value.Value.TextFormat;
                if (p.Value.Value.LengthText != null) cell["lengthText"] = p.Value.Value.LengthText;
                return cell;
            }) };
        if (row.Contributors != null) result["contributors"] = row.Contributors;
        return result;
    }
    internal static AggregateListRow DecodeRow(JsonElement row)
    {
        var cells = new Dictionary<string, AggregateCell>(StringComparer.Ordinal);
        foreach (var property in row.GetProperty("cells").EnumerateObject())
        {
            var cell = property.Value; var type = cell.GetProperty("type").GetString()!; var state = cell.GetProperty("state").GetString()!;
            var value = cell.GetProperty("value");
            var decoded = new AggregateValue(type, state);
            if (state == "VALUE") decoded = type switch {
                "NUMBER" => decoded with { Number = Exact(cell.GetProperty("exact").GetString()!) },
                "BOOLEAN" => decoded with { Boolean = value.GetBoolean() },
                "CHOICE_MANY" => decoded with { Choices = value.EnumerateArray().Select(v => v.GetString()!).ToArray() },
                _ => decoded with { Text = value.GetString() } };
            if (cell.TryGetProperty("textFormat", out var format)) decoded = decoded with { TextFormat = format.GetString() };
            if (cell.TryGetProperty("lengthText", out var lengthText)) decoded = decoded with { LengthText = lengthText.GetString() };
            cells.Add(property.Name, new(decoded, cell.GetProperty("lineage").Deserialize<AggregateTrace[]>(Json)!));
        }
        return new(row.GetProperty("key").GetString()!, row.GetProperty("origin").Deserialize<AggregateListOrigin>(Json)!, cells)
            { Contributors = row.TryGetProperty("contributors", out var contributors) ? contributors.Deserialize<AggregateListContributor[]>(Json) : null };
    }
    internal static AggregateListValue Decode(JsonElement value)
    {
        var kind = value.GetProperty("kind").GetString();
        if (!Supported(kind)) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
        var list = new AggregateListValue(value.GetProperty("schema").Deserialize<AggregateListSchema>(Json)!, value.GetProperty("records").EnumerateArray().Select(DecodeRow).ToArray());
        if (KindFor(list) != kind || list.Records.Any(r => r.Contributors is { Count: 0 } or { Count: > 64 }))
            throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
        return list;
    }
    internal static string OutputId(string instanceId, string memberId, AggregateListRow row)
        => row.Contributors == null ? OutputId(instanceId, memberId, row.Origin)
            : Uuid(JsonSerializer.Serialize(new { instanceId, memberId, sources = row.Contributors.Select(c => new {
                c.InputId, c.Origin.SourceSlot, ReportId = c.Origin.Pin?.ReportId, c.Origin.ListId, c.Origin.RecordId }) }, Json));
    internal static string OutputId(string instanceId, string memberId, AggregateListOrigin origin)
    {
        var identity = JsonSerializer.Serialize(new { instanceId, memberId, origin.SourceSlot,
            ReportId = origin.Pin?.ReportId, origin.ListId, origin.RecordId }, Json);
        return Uuid(identity);
    }
    private static string Uuid(string identity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes).ToString("D");
    }
    private static AggregateNumber Exact(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 2 || key.Length > 2600) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID");
        return new(BigInteger.Parse(parts[0], CultureInfo.InvariantCulture), BigInteger.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
