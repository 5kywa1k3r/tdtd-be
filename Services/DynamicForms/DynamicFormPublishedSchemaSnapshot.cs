using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicForms;

public sealed record DynamicFormPublishedSchemaSnapshot(string Json, string Sha256);

/// <summary>
/// Creates the immutable canonical schema snapshot bound to a published form version.
/// Object properties are sorted ordinally; array order remains part of the schema contract.
/// </summary>
public static class DynamicFormPublishedSchemaSnapshotBuilder
{
    public static DynamicFormPublishedSchemaSnapshot ValidateAgainstTemplate(
        DynamicFormTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        try
        {
            return ValidateAgainstTemplateCore(template);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            // All published-integrity callers intentionally map InvalidOperationException
            // to the stable 409 contract. Do not let malformed legacy/live JSON escape as
            // a raw parser exception (and potentially become a generic 500 response).
            throw new InvalidOperationException(
                "Published Dynamic Form live schema cannot be parsed or adapted safely.",
                ex);
        }
    }

    private static DynamicFormPublishedSchemaSnapshot ValidateAgainstTemplateCore(
        DynamicFormTemplate template)
    {
        var snapshot = ValidateExisting(
            template.PublishedSchemaSnapshotJson,
            template.PublishedSchemaHash);
        using var snapshotDocument = JsonDocument.Parse(snapshot.Json);
        var snapshotRoot = snapshotDocument.RootElement;
        var snapshotSchemaVersion = snapshotRoot.GetProperty("schemaVersion").GetInt32();
        var liveSchemaVersion = Math.Max(1, template.SchemaVersion);
        if (snapshotSchemaVersion != liveSchemaVersion)
        {
            throw new InvalidOperationException(
                "Published Dynamic Form live schemaVersion differs from its immutable snapshot.");
        }

        var effectiveBlocksJson = ResolveEffectiveBlocksJson(
            template.BlocksJson,
            template.ExcelBlockJson);

        // Canonical JSON equality alone is not enough for legacy/out-of-band data:
        // an array can still contain values that the typed API adapter cannot read.
        // Validate the same typed projection returned by detail APIs before any
        // published consumer accepts the document.
        _ = DynamicFormSchemaAdapter.FromLegacy(
            template.SectionsJson,
            template.FieldsJson,
            template.ExcelBlockJson,
            effectiveBlocksJson);

        EnsureStructuralMatch(
            "sections",
            ReadSnapshotArray(snapshotRoot, "sections"),
            template.SectionsJson,
            stripStatisticConfig: false,
            removeFieldStatisticLabels: false);
        EnsureStructuralMatch(
            "fields",
            ReadSnapshotArray(snapshotRoot, "fields"),
            template.FieldsJson,
            stripStatisticConfig: true,
            removeFieldStatisticLabels: true);
        EnsureStructuralMatch(
            "blocks",
            ReadSnapshotArray(snapshotRoot, "blocks"),
            effectiveBlocksJson,
            stripStatisticConfig: true,
            removeFieldStatisticLabels: false);

        return snapshot;
    }

    internal static string CanonicalizeStructureForComparison(
        string? json,
        bool stripStatisticConfig,
        bool removeFieldStatisticLabels)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Dynamic Form structure is not valid JSON.",
                ex);
        }

        if (node is null)
            throw new InvalidOperationException("Dynamic Form structure is not valid JSON.");

        if (stripStatisticConfig)
            StripStatisticConfig(node, removeFieldStatisticLabels);

        using var document = JsonDocument.Parse(node.ToJsonString());
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, document.RootElement);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static DynamicFormPublishedSchemaSnapshot ValidateExisting(
        string? snapshotJson,
        string? schemaHash)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
            throw new InvalidOperationException("Published Dynamic Form schema snapshot is missing.");
        if (string.IsNullOrWhiteSpace(schemaHash))
            throw new InvalidOperationException("Published Dynamic Form schema hash is missing.");

        DynamicFormPublishedSchemaSnapshot canonical;
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Published Dynamic Form schema snapshot must be a JSON object.");

            if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number ||
                !schemaVersionElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion <= 0)
            {
                throw new InvalidOperationException(
                    "Published Dynamic Form schema snapshot has an invalid schemaVersion.");
            }

            canonical = Build(
                schemaVersion,
                ReadSnapshotArray(root, "sections"),
                ReadSnapshotArray(root, "fields"),
                ReadSnapshotArray(root, "blocks"));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Published Dynamic Form schema snapshot is not valid JSON.",
                ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "Published Dynamic Form schema snapshot is not canonical.",
                ex);
        }

        if (!string.Equals(canonical.Json, snapshotJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Published Dynamic Form schema snapshot does not match its canonical representation.");
        }

        if (!string.Equals(canonical.Sha256, schemaHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Published Dynamic Form schema snapshot does not match its SHA-256 hash.");
        }

        return canonical;
    }

    public static DynamicFormPublishedSchemaSnapshot Build(DynamicFormTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Build(
            Math.Max(1, template.SchemaVersion),
            template.SectionsJson,
            template.FieldsJson,
            ResolveEffectiveBlocksJson(template.BlocksJson, template.ExcelBlockJson));
    }

    public static DynamicFormPublishedSchemaSnapshot Build(
        int schemaVersion,
        string sectionsJson,
        string fieldsJson,
        string blocksJson)
    {
        using var sections = ParseArray(sectionsJson, nameof(sectionsJson));
        using var fields = ParseArray(fieldsJson, nameof(fieldsJson));
        using var blocks = ParseArray(blocksJson, nameof(blocksJson));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", Math.Max(1, schemaVersion));
            writer.WritePropertyName("sections");
            WriteCanonical(writer, sections.RootElement);
            writer.WritePropertyName("fields");
            WriteCanonical(writer, fields.RootElement);
            writer.WritePropertyName("blocks");
            WriteCanonical(writer, blocks.RootElement);
            writer.WriteEndObject();
            writer.Flush();
        }

        var bytes = buffer.WrittenSpan;
        var json = Encoding.UTF8.GetString(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new DynamicFormPublishedSchemaSnapshot(json, hash);
    }

    private static JsonDocument ParseArray(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Dynamic Form schema payload is required.", name);

        var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new ArgumentException("Dynamic Form schema payload must be a JSON array.", name);
        }

        return document;
    }

    private static string ReadSnapshotArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Published Dynamic Form schema snapshot property '{propertyName}' must be a JSON array.");
        }

        return value.GetRawText();
    }

    private static void EnsureStructuralMatch(
        string component,
        string snapshotJson,
        string? liveJson,
        bool stripStatisticConfig,
        bool removeFieldStatisticLabels)
    {
        var snapshotProjection = CanonicalizeStructureForComparison(
            snapshotJson,
            stripStatisticConfig,
            removeFieldStatisticLabels);
        var liveProjection = CanonicalizeStructureForComparison(
            liveJson,
            stripStatisticConfig,
            removeFieldStatisticLabels);
        if (!string.Equals(snapshotProjection, liveProjection, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Published Dynamic Form live {component} differs from its immutable snapshot.");
        }
    }

    private static void StripStatisticConfig(JsonNode? node, bool removeFieldStatisticLabels)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("isStatistic");
            obj.Remove("statistic");
            if (removeFieldStatisticLabels)
                obj.Remove("statisticLabelCodes");
            obj.Remove("statisticColumns");
            obj.Remove("statisticColumnLabels");
            obj.Remove("metricLabelTargets");
            obj.Remove("allowedRowLabelCodes");
            obj.Remove("statisticsDisabled");
            obj.Remove("statisticsDisabledReason");
            if (obj["metricRules"] is JsonArray metricRules)
            {
                foreach (var ruleNode in metricRules)
                {
                    if (ruleNode is JsonObject rule)
                        rule.Remove("aggregateOps");
                }
            }

            foreach (var child in obj.ToList())
                StripStatisticConfig(child.Value, removeFieldStatisticLabels);
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
                StripStatisticConfig(item, removeFieldStatisticLabels);
        }
    }

    private static string ResolveEffectiveBlocksJson(string? blocksJson, string? legacyExcelBlockJson)
    {
        if (!string.IsNullOrWhiteSpace(blocksJson))
        {
            using var blocks = JsonDocument.Parse(blocksJson);
            if (blocks.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Dynamic Form blocks payload must be a JSON array.", nameof(blocksJson));

            if (blocks.RootElement.GetArrayLength() > 0 || string.IsNullOrWhiteSpace(legacyExcelBlockJson))
                return blocksJson;
        }

        if (string.IsNullOrWhiteSpace(legacyExcelBlockJson))
            return "[]";

        using var legacyBlock = JsonDocument.Parse(legacyExcelBlockJson);
        if (legacyBlock.RootElement.ValueKind is JsonValueKind.Null)
            return "[]";
        if (legacyBlock.RootElement.ValueKind is not JsonValueKind.Object)
            throw new ArgumentException(
                "Dynamic Form legacy Excel block payload must be a JSON object.",
                nameof(legacyExcelBlockJson));

        return JsonSerializer.Serialize(new[] { legacyBlock.RootElement.Clone() });
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, value);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON kind: {value.ValueKind}.");
        }
    }

    private static void WriteCanonicalNumber(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.TryGetInt64(out var signed))
        {
            writer.WriteNumberValue(signed);
            return;
        }

        if (value.TryGetUInt64(out var unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (value.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(
                decimalValue.ToString("G29", CultureInfo.InvariantCulture),
                skipInputValidation: true);
            return;
        }

        if (value.TryGetDouble(out var doubleValue) && double.IsFinite(doubleValue))
        {
            writer.WriteNumberValue(doubleValue);
            return;
        }

        throw new ArgumentException("Dynamic Form schema contains a number outside the supported JSON range.");
    }
}
