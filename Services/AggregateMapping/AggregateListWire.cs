using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateListWire
{
    internal const string Kind = "LIST_RECORDS_V1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static object Encode(AggregateListValue list) => new { kind = Kind, schema = list.Schema,
        records = list.Records.Select(EncodeRow) };
    internal static object EncodeRow(AggregateListRow row) => new { row.Key, row.Origin,
        cells = row.Cells.ToDictionary(p => p.Key, p => new { p.Value.Value.Type, p.Value.Value.State,
            value = AggregatePreviewService.ToWire(p.Value.Value),
            exact = p.Value.Value.Number?.ExactKey, lineage = p.Value.Trace }) };
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
            cells.Add(property.Name, new(decoded, cell.GetProperty("lineage").Deserialize<AggregateTrace[]>(Json)!));
        }
        return new(row.GetProperty("key").GetString()!, row.GetProperty("origin").Deserialize<AggregateListOrigin>(Json)!, cells);
    }
    internal static AggregateListValue Decode(JsonElement value)
        => value.GetProperty("kind").GetString() != Kind ? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_INVALID")
            : new(value.GetProperty("schema").Deserialize<AggregateListSchema>(Json)!, value.GetProperty("records").EnumerateArray().Select(DecodeRow).ToArray());
    internal static string OutputId(string instanceId, string memberId, AggregateListOrigin origin)
    {
        var identity = JsonSerializer.Serialize(new { instanceId, memberId, origin.SourceSlot,
            ReportId = origin.Pin?.ReportId, origin.ListId, origin.RecordId }, Json);
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
