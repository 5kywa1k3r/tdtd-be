using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicForms;

namespace tdtd_be.Services.DynamicForms;

// Diagnostic traversal only: no model defaults, normalization, canonical rewrite or inferred IDs.
internal sealed class CanvasStoredDependencyInspector(CancellationToken ct)
{
    internal sealed record Pin(string Path, string LabelId, string VersionId, int VersionNo, string Hash, JsonElement? Snapshot);
    internal List<CanvasDependencySlot> Slots { get; } = [];
    internal List<CanvasDependencyIssue> Issues { get; } = [];
    internal List<Pin> Pins { get; } = [];
    internal bool Truncated { get; private set; }
    private int nodes = 50_000;
    private int bytes = 4 * 1024 * 1024;
    private static readonly string[] SectionKeys = ["fieldSectionJson", "tableSectionJson", "nativeTargetSectionJson", "nativePlanSectionJson"];
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static bool Id(string? value) => value is { Length: 24 } && ObjectId.TryParse(value, out var parsed) && parsed.ToString() == value;
    internal static bool Sha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal void Issue(string path, string code) { if (Issues.Count < 128) Issues.Add(new(path, code)); else Truncated = true; }
    internal void Limit(string path) { Truncated = true; Issue(path, "LIMIT_REACHED"); }

    internal void Read(BsonDocument owner)
    {
        foreach (var key in new[] { "sectionsJson", "fieldsJson", "blocksJson", "tablesJson", "excelBlockJson", "publishedSchemaSnapshotJson" })
            Slot(owner, key, key, key is "excelBlockJson" or "publishedSchemaSnapshotJson", false);
        Config(owner, "statisticConfigSections", "statisticConfigDependencyPins", "current");
        var history = owner.GetValue("statisticConfigSnapshots", BsonNull.Value);
        if (!history.IsBsonArray) { Issue("statisticConfigSnapshots", "HISTORY_MISSING_OR_WRONG_TYPE"); return; }
        if (history.AsBsonArray.Count > 100) Limit("statisticConfigSnapshots");
        for (var i = 0; i < Math.Min(100, history.AsBsonArray.Count); i++)
        {
            ct.ThrowIfCancellationRequested();
            if (history[i] is BsonDocument row) Config(row, "sections", "dependencyPins", $"history[{i}]");
            else Issue($"history[{i}]", "HISTORY_ENTRY_WRONG_TYPE");
        }
    }

    private void Config(BsonDocument row, string sectionKey, string pinKey, string path)
    {
        var start = Pins.Count;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (row.GetValue(pinKey, BsonNull.Value) is BsonArray values)
        {
            for (var i = 0; i < Math.Min(values.Count, 256); i++)
            {
                var value = values[i]; var location = $"{path}.{pinKey}[{i}]";
                var pin = value.IsString ? ParsePin(value.AsString, location, null) : null;
                if (pin is null) { Issue(location, "PIN_FORMAT_INVALID"); continue; }
                if (!declared.Add(Key(pin))) Issue(location, "PIN_DUPLICATE");
                Add(pin);
            }
            if (values.Count > 256) Limit(path + "." + pinKey);
        }
        else Issue(path + "." + pinKey, "PIN_COLLECTION_MISSING_OR_WRONG_TYPE");
        if (row.GetValue(sectionKey, BsonNull.Value) is BsonDocument sections)
            foreach (var key in SectionKeys) Slot(sections, key, path + "." + sectionKey + "." + key, key == "nativePlanSectionJson", true);
        else Issue(path + "." + sectionKey, "SECTIONS_MISSING_OR_WRONG_TYPE");
        var embedded = Pins.Skip(start).Where(pin => pin.Snapshot.HasValue).ToArray();
        foreach (var pin in embedded) if (!declared.Contains(Key(pin))) Issue(pin.Path, "SNAPSHOT_PIN_NOT_DECLARED");
        var embeddedKeys = embedded.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var pin in Pins.Skip(start).Where(pin => !pin.Snapshot.HasValue))
            if (!embeddedKeys.Contains(Key(pin))) Issue(pin.Path, "PIN_WITHOUT_LABEL_SNAPSHOT");
    }
    private static string Key(Pin pin) => $"LABEL:{pin.LabelId}:{pin.VersionId}:{pin.VersionNo}:{pin.Hash}";
    private Pin? ParsePin(string text, string path, JsonElement? snapshot)
    {
        var parts = text.Split(':');
        if (parts.Length != 5 || parts[0] != "LABEL" || !Id(parts[1]) || !Id(parts[2]) || !Sha(parts[4])
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1
            || number.ToString(CultureInfo.InvariantCulture) != parts[3]) return null;
        return new(path, parts[1], parts[2], number, parts[4], snapshot);
    }
    private void Add(Pin pin) { if (Pins.Count < 256) Pins.Add(pin); else Limit(pin.Path); }

    private void Slot(BsonDocument source, string key, string path, bool expectObject, bool extract)
    {
        ct.ThrowIfCancellationRequested();
        if (!source.TryGetValue(key, out var raw)) { Slots.Add(new(path, "ABSENT", null)); return; }
        if (raw.IsBsonNull) { Slots.Add(new(path, "NULL", null)); return; }
        if (!raw.IsString) { Slots.Add(new(path, "WRONG_TYPE", null)); Issue(path, "JSON_BSON_TYPE_INVALID"); return; }
        var size = Encoding.UTF8.GetByteCount(raw.AsString);
        if (size > 1024 * 1024 || size > bytes || nodes <= 0) { Slots.Add(new(path, "NOT_SCANNED", null)); Limit(path); return; }
        bytes -= size;
        var hash = Hash(Encoding.UTF8.GetBytes(raw.AsString));
        try
        {
            using var json = JsonDocument.Parse(raw.AsString, new JsonDocumentOptions { MaxDepth = 48 });
            if (!Unique(json.RootElement)) { Slots.Add(new(path, "DUPLICATE_KEYS", hash)); Issue(path, "JSON_DUPLICATE_KEYS"); return; }
            if (json.RootElement.ValueKind != (expectObject ? JsonValueKind.Object : JsonValueKind.Array))
            { Slots.Add(new(path, "WRONG_SHAPE", hash)); Issue(path, "JSON_ROOT_SHAPE_INVALID"); return; }
            Slots.Add(new(path, "SCANNED", hash));
            if (extract) Extract(json.RootElement, path);
        }
        catch (JsonException) { Slots.Add(new(path, "INVALID_JSON", hash)); Issue(path, "JSON_INVALID_OR_TOO_DEEP"); }
        catch (InspectionLimit) { Slots.Add(new(path, "NOT_SCANNED", hash)); Limit(path); }
    }
    private sealed class InspectionLimit : Exception { }
    private bool Unique(JsonElement value)
    {
        ct.ThrowIfCancellationRequested();
        if (--nodes < 0) throw new InspectionLimit();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }
    private void Extract(JsonElement value, string path)
    {
        ct.ThrowIfCancellationRequested();
        // Paths use ordinal property positions so untrusted/very long property names never enter the response.
        if (value.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var property in value.EnumerateObject())
            {
                var next = $"{path}.p[{index++}]";
                if (property.Name == "labelSnapshot") Snapshot(property.Value, next);
                else if (property.Name is "labelSnapshots" or "rowLabelSnapshots")
                {
                    if (property.Value.ValueKind != JsonValueKind.Array) { Issue(next, "LABEL_SNAPSHOTS_WRONG_TYPE"); continue; }
                    var n = 0; foreach (var item in property.Value.EnumerateArray()) Snapshot(item, $"{next}[{n++}]");
                }
                else Extract(property.Value, next);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { var i = 0; foreach (var item in value.EnumerateArray()) Extract(item, $"{path}[{i++}]"); }
    }
    private void Snapshot(JsonElement item, string path)
    {
        if (item.ValueKind != JsonValueKind.Object) { Issue(path, "LABEL_SNAPSHOT_INVALID"); return; }
        string? Text(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var version = item.TryGetProperty("versionNo", out var no) && no.ValueKind == JsonValueKind.Number && no.TryGetInt32(out var n) ? n : 0;
        var pin = ParsePin($"LABEL:{Text("labelId")}:{Text("versionId")}:{version}:{Text("configHash")}", path, item);
        if (pin is null) Issue(path, "LABEL_SNAPSHOT_INVALID");
        else Add(pin with { Snapshot = item.Clone() });
    }
}
