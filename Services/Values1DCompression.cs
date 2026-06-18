using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.Services;

public static class Values1DCompression
{
    private const string CompressionKind = "NULL_RUNS";
    public const int MinValues1DCompressionLength = 251;
    private const int MinNullRunLength = 8;

    public static string Serialize(IReadOnlyList<object?> values, JsonSerializerOptions options)
    {
        var dense = ToJsonArray(values, options);
        var payload = BuildCompressedPayload(dense, options);
        return payload?.ToJsonString(options) ?? dense.ToJsonString(options);
    }

    public static string SerializeDecimals(IReadOnlyList<decimal?> values, JsonSerializerOptions options)
    {
        var objects = values.Select(value => (object?)value).ToList();
        return Serialize(objects, options);
    }

    public static List<object?> DeserializeObjects(string? json, JsonSerializerOptions options)
    {
        var dense = ParseValuesJson(json);
        if (dense is null)
            return new List<object?>();

        return JsonSerializer.Deserialize<List<object?>>(dense.ToJsonString(options), options)
               ?? new List<object?>();
    }

    public static List<decimal?> DeserializeDecimals(string? json, JsonSerializerOptions options)
    {
        var dense = ParseValuesJson(json);
        if (dense is null)
            return new List<decimal?>();

        return dense
            .Select(ToNullableDecimal)
            .ToList();
    }

    public static string? CompressTableValuesJson(string? tableValuesJson, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return tableValuesJson;

        try
        {
            var root = JsonNode.Parse(tableValuesJson) as JsonObject;
            if (root?["blocks"] is not JsonArray blocks)
                return tableValuesJson;

            foreach (var node in blocks)
            {
                if (node is JsonObject block)
                    CompressBlockValues(block, options);
            }

            return root.ToJsonString(options);
        }
        catch (JsonException)
        {
            return tableValuesJson;
        }
    }

    public static string? ExpandTableValuesJson(string? tableValuesJson, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return tableValuesJson;

        try
        {
            var root = JsonNode.Parse(tableValuesJson) as JsonObject;
            if (root?["blocks"] is not JsonArray blocks)
                return tableValuesJson;

            foreach (var node in blocks)
            {
                if (node is JsonObject block)
                    ExpandBlockValuesInPlace(block, options);
            }

            return root.ToJsonString(options);
        }
        catch (JsonException)
        {
            return tableValuesJson;
        }
    }

    public static List<object?>? ReadBlockObjects(JsonElement block, JsonSerializerOptions options)
    {
        var dense = ExpandBlockValues(block);
        if (dense is null)
            return null;

        return JsonSerializer.Deserialize<List<object?>>(dense.ToJsonString(options), options)
               ?? new List<object?>();
    }

    public static List<decimal?>? ReadBlockDecimals(JsonElement block)
    {
        var dense = ExpandBlockValues(block);
        return dense?.Select(ToNullableDecimal).ToList();
    }

    public static int? ReadBlockValuesLength(JsonElement block)
    {
        if (IsCompressed(block))
        {
            var length = ReadJsonInt(block, "values1DLength");
            if (length.HasValue && length.Value >= 0)
                return length.Value;
        }

        return TryGetJsonProperty(block, "values1D", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.GetArrayLength()
            : null;
    }

    public static Values1DReader? CreateBlockReader(JsonElement block)
        => Values1DReader.Create(block);

    private static void CompressBlockValues(JsonObject block, JsonSerializerOptions options)
    {
        var dense = ExpandBlockValuesToArray(block);
        if (dense is null)
            return;

        RemoveCompressionFields(block);
        var payload = BuildCompressedPayload(dense, options);
        if (payload is null)
        {
            block["values1D"] = dense;
            return;
        }

        block["values1D"] = payload["values1D"]?.DeepClone();
        block["values1DCompressed"] = true;
        block["values1DCompression"] = CompressionKind;
        block["values1DLength"] = payload["values1DLength"]?.DeepClone();
        block["values1DCompressedIndexes"] = payload["values1DCompressedIndexes"]?.DeepClone();
        block["values1DCompressedCounts"] = payload["values1DCompressedCounts"]?.DeepClone();
    }

    private static void ExpandBlockValuesInPlace(JsonObject block, JsonSerializerOptions options)
    {
        var dense = ExpandBlockValuesToArray(block);
        if (dense is null)
            return;

        RemoveCompressionFields(block);
        block["values1D"] = dense;
    }

    private static JsonArray? ParseValuesJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonArray();

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => CloneJsonArray(document.RootElement),
                JsonValueKind.Object => ExpandBlockValues(document.RootElement),
                _ => new JsonArray()
            };
        }
        catch (JsonException)
        {
            return new JsonArray();
        }
    }

    private static JsonArray? ExpandBlockValuesToArray(JsonObject block)
    {
        if (block["values1D"] is not JsonArray values)
            return null;

        if (block["values1DCompressed"] is not JsonValue compressedFlag ||
            !compressedFlag.TryGetValue<bool>(out var compressed) ||
            !compressed ||
            !TryGetJsonNodeString(block["values1DCompression"], out var compression) ||
            compression != CompressionKind)
        {
            return CloneJsonArray(values);
        }

        var length = TryGetJsonNodeInt(block["values1DLength"], out var rawLength) ? rawLength : values.Count;
        var indexes = ReadJsonNodeIntArray(block["values1DCompressedIndexes"]);
        var counts = ReadJsonNodeIntArray(block["values1DCompressedCounts"]);
        return ExpandCompressedArray(values, Math.Max(0, length), indexes, counts);
    }

    private static JsonArray? ExpandBlockValues(JsonElement block)
    {
        using var reader = CreateBlockReader(block);
        return reader?.ToJsonArray();
    }

    private static JsonObject? BuildCompressedPayload(JsonArray dense, JsonSerializerOptions options)
    {
        if (dense.Count < MinValues1DCompressionLength)
            return null;

        var compressed = new JsonArray();
        var indexes = new JsonArray();
        var counts = new JsonArray();

        for (var index = 0; index < dense.Count;)
        {
            if (!IsJsonNull(dense[index]))
            {
                compressed.Add(dense[index]?.DeepClone());
                index++;
                continue;
            }

            var end = index + 1;
            while (end < dense.Count && IsJsonNull(dense[end]))
                end++;

            var runLength = end - index;
            if (runLength >= MinNullRunLength)
            {
                indexes.Add(index);
                counts.Add(runLength);
                compressed.Add(null);
            }
            else
            {
                for (var cursor = index; cursor < end; cursor++)
                    compressed.Add(null);
            }

            index = end;
        }

        if (indexes.Count == 0)
            return null;

        var payload = new JsonObject
        {
            ["values1DCompressed"] = true,
            ["values1DCompression"] = CompressionKind,
            ["values1DLength"] = dense.Count,
            ["values1D"] = compressed,
            ["values1DCompressedIndexes"] = indexes,
            ["values1DCompressedCounts"] = counts
        };

        var denseSize = dense.ToJsonString(options).Length;
        var compressedSize = payload.ToJsonString(options).Length;
        return compressedSize < denseSize ? payload : null;
    }

    private static JsonArray ExpandCompressedArray(
        JsonArray values,
        int length,
        IReadOnlyList<int> indexes,
        IReadOnlyList<int> counts)
    {
        if (indexes.Count == 0 || indexes.Count != counts.Count)
            return CloneJsonArray(values);

        var output = new JsonArray();
        var compressedIndex = 0;
        for (var runIndex = 0; runIndex < indexes.Count; runIndex++)
        {
            var originalIndex = indexes[runIndex];
            var count = counts[runIndex];
            if (originalIndex < 0 || originalIndex > length || count <= 0)
                continue;

            while (output.Count < originalIndex && compressedIndex < values.Count)
            {
                output.Add(values[compressedIndex]?.DeepClone());
                compressedIndex++;
            }

            if (compressedIndex < values.Count)
                compressedIndex++;

            while (output.Count < originalIndex + count)
                output.Add(null);
        }

        while (output.Count < length && compressedIndex < values.Count)
        {
            output.Add(values[compressedIndex]?.DeepClone());
            compressedIndex++;
        }

        while (output.Count < length)
            output.Add(null);

        return SliceJsonArray(output, length);
    }

    private static JsonArray ExpandCompressedArray(
        JsonElement values,
        int length,
        IReadOnlyList<int> indexes,
        IReadOnlyList<int> counts)
    {
        var array = CloneJsonArray(values);
        return ExpandCompressedArray(array, length, indexes, counts);
    }

    public sealed class Values1DReader : IDisposable
    {
        private readonly JsonDocument _document;
        private readonly JsonElement _values;
        private readonly CompressedRun[] _runs;

        private Values1DReader(
            JsonDocument document,
            JsonElement values,
            int length,
            CompressedRun[] runs)
        {
            _document = document;
            _values = values;
            Length = Math.Max(0, length);
            _runs = runs;
        }

        public int Length { get; }

        public bool IsCompressed => _runs.Length > 0;

        public static Values1DReader? Create(JsonElement block)
        {
            if (!TryGetJsonProperty(block, "values1D", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var document = JsonDocument.Parse(block.GetRawText());
            var root = document.RootElement;
            if (!TryGetJsonProperty(root, "values1D", out values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                return null;
            }

            var length = values.GetArrayLength();
            var runs = Array.Empty<CompressedRun>();
            if (IsCompressed(root))
            {
                var compressedLength = values.GetArrayLength();
                var originalLength = Math.Max(0, ReadJsonInt(root, "values1DLength") ?? compressedLength);
                var candidate = BuildRuns(root, originalLength, compressedLength);
                if (candidate is not null)
                {
                    length = originalLength;
                    runs = candidate;
                }
            }

            return new Values1DReader(document, values, length, runs);
        }

        public JsonElement? GetElement(int originalIndex)
        {
            if (originalIndex < 0 || originalIndex >= Length)
                return null;

            var compressedIndex = MapIndex(originalIndex, out var inCompressedRun);
            if (inCompressedRun)
                return null;

            if (compressedIndex < 0 || compressedIndex >= _values.GetArrayLength())
                return null;

            return _values[compressedIndex];
        }

        public decimal? ReadDecimal(int originalIndex)
        {
            var value = GetElement(originalIndex);
            return value.HasValue ? ToNullableDecimal(value.Value) : null;
        }

        public JsonArray ToJsonArray()
        {
            var result = new JsonArray();
            for (var index = 0; index < Length; index++)
            {
                var value = GetElement(index);
                result.Add(value.HasValue ? CloneJsonElementToNode(value.Value) : null);
            }

            return result;
        }

        public void Dispose()
            => _document.Dispose();

        private int MapIndex(int originalIndex, out bool inCompressedRun)
        {
            inCompressedRun = false;
            if (_runs.Length == 0)
                return originalIndex;

            var lo = 0;
            var hi = _runs.Length - 1;
            var match = -1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (_runs[mid].Start <= originalIndex)
                {
                    match = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (match < 0)
                return originalIndex;

            var run = _runs[match];
            if (originalIndex < run.EndExclusive)
            {
                inCompressedRun = true;
                return run.CompressedIndex;
            }

            return originalIndex - run.RemovedThrough;
        }

        private static CompressedRun[]? BuildRuns(
            JsonElement root,
            int length,
            int compressedLength)
        {
            var indexes = ReadJsonIntArray(root, "values1DCompressedIndexes");
            var counts = ReadJsonIntArray(root, "values1DCompressedCounts");
            if (indexes.Count == 0 || indexes.Count != counts.Count)
                return null;

            var runs = new List<CompressedRun>();
            var previousEnd = 0;
            var removedBefore = 0;
            for (var runIndex = 0; runIndex < indexes.Count; runIndex++)
            {
                var start = indexes[runIndex];
                var count = counts[runIndex];
                var endExclusive = start + count;
                if (start < previousEnd ||
                    start < 0 ||
                    start >= length ||
                    count <= 0 ||
                    endExclusive > length)
                {
                    return null;
                }

                var compressedIndex = start - removedBefore;
                if (compressedIndex < 0 || compressedIndex >= compressedLength)
                    return null;

                removedBefore += count - 1;
                runs.Add(new CompressedRun(start, endExclusive, compressedIndex, removedBefore));
                previousEnd = endExclusive;
            }

            return length - removedBefore == compressedLength ? runs.ToArray() : null;
        }

        private readonly record struct CompressedRun(
            int Start,
            int EndExclusive,
            int CompressedIndex,
            int RemovedThrough);
    }

    private static JsonArray ToJsonArray(IReadOnlyList<object?> values, JsonSerializerOptions options)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(ToJsonNode(value, options));
        return array;
    }

    private static JsonNode? ToJsonNode(object? value, JsonSerializerOptions options)
    {
        if (value is null)
            return null;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
        return JsonSerializer.SerializeToNode(value, options);
    }

    private static JsonArray CloneJsonArray(JsonElement array)
    {
        var result = new JsonArray();
        foreach (var item in array.EnumerateArray())
            result.Add(CloneJsonElementToNode(item));
        return result;
    }

    private static JsonNode? CloneJsonElementToNode(JsonElement item)
        => item.ValueKind == JsonValueKind.Null || item.ValueKind == JsonValueKind.Undefined
            ? null
            : JsonNode.Parse(item.GetRawText());

    private static JsonArray CloneJsonArray(JsonArray array)
    {
        var result = new JsonArray();
        foreach (var item in array)
            result.Add(item?.DeepClone());
        return result;
    }

    private static JsonArray SliceJsonArray(JsonArray array, int length)
    {
        if (array.Count <= length)
            return array;

        var result = new JsonArray();
        for (var index = 0; index < length; index++)
            result.Add(array[index]?.DeepClone());
        return result;
    }

    private static void RemoveCompressionFields(JsonObject block)
    {
        block.Remove("values1DCompressed");
        block.Remove("values1DCompression");
        block.Remove("values1DLength");
        block.Remove("values1DCompressedIndexes");
        block.Remove("values1DCompressedCounts");
    }

    private static bool IsJsonNull(JsonNode? node)
        => node is null;

    private static bool IsCompressed(JsonElement element)
        => ReadJsonBool(element, "values1DCompressed") == true &&
           string.Equals(ReadJsonString(element, "values1DCompression"), CompressionKind, StringComparison.Ordinal);

    private static bool? ReadJsonBool(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? ReadJsonInt(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadJsonString(JsonElement element, string name)
        => TryGetJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<int> ReadJsonIntArray(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<int>();

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : -1)
            .Where(number => number >= 0)
            .ToList();
    }

    private static List<int> ReadJsonNodeIntArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            return new List<int>();

        return array
            .Select(item => item is JsonValue value && value.TryGetValue<int>(out var number) ? number : -1)
            .Where(number => number >= 0)
            .ToList();
    }

    private static bool TryGetJsonNodeString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
            return false;
        value = text;
        return true;
    }

    private static bool TryGetJsonNodeInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value);
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static decimal? ToNullableDecimal(JsonNode? node)
    {
        if (node is null)
            return null;
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<decimal>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) &&
               decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? ToNullableDecimal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }
}
