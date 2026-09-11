using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

internal static class StatRunExportColumnManifestContract
{
    internal const string SchemaVersion = "P9_CANONICAL_EXPORT_COLUMN_MANIFEST_V1";

    internal static class ValueTypes
    {
        internal const string Integer = "INTEGER";
        internal const string Decimal = "DECIMAL";
        internal const string Boolean = "BOOLEAN";
        internal const string UtcInstant = "UTC_INSTANT";
        internal const string Text = "TEXT";
        internal const string Json = "JSON";

        internal static bool IsSupported(string value)
            => value is Integer or Decimal or Boolean or UtcInstant or Text or Json;
    }

    internal static string? DeclaredValueType(
        string resultKind,
        string columnName)
    {
        if (!string.Equals(
                resultKind,
                StatRunExportResultKinds.DirectField,
                StringComparison.Ordinal))
        {
            return null;
        }

        return columnName switch
        {
            "workId" or "scopeType" or "scopeId" or "rootAssignmentId" or
            "dynamicFormTemplateId" or "dynamicFormTemplateCode" or
            "dynamicFormTemplateName" or "fieldId" or "fieldKey" or
            "fieldLabel" or "fieldType" or "bucketKey" or "bucketLabel" or
            "periodKey" or "periodInstanceKey" or "periodKind" =>
                ValueTypes.Text,
            "statisticLabelCodes" => ValueTypes.Json,
            "showInTree" or "showInDetail" => ValueTypes.Boolean,
            "reportStatus" or "valueCount" or "numericValueCount" or
            "sum" or "min" or "max" or "average" or "trueCount" or
            "falseCount" or "reportCount" => ValueTypes.Decimal,
            "earliestDateUtc" or "latestDateUtc" or "updatedAtUtc" =>
                ValueTypes.UtcInstant,
            _ => null
        };
    }

    internal static class BlankPolicies
    {
        internal const string Forbidden = "FORBIDDEN";
        internal const string Null = "NULL";
        internal const string Empty = "EMPTY";

        internal static bool IsSupported(string value)
            => value is Forbidden or Null or Empty;
    }

    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    internal static StatRunExportColumnManifestSidecar Create(
        IReadOnlyList<StatRunExportColumnManifestEntry> columns)
    {
        var manifest = Validate(new StatRunExportColumnManifest(
            SchemaVersion,
            columns));
        var json = Canonicalize(manifest);
        return new StatRunExportColumnManifestSidecar(
            manifest,
            json,
            Hash(json));
    }

    internal static StatRunExportColumnManifestSidecar Parse(
        string? json,
        string? declaredSha256)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            !StatRunCanonicalJson.IsCanonicalSha256(declaredSha256))
        {
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_REQUIRED");
        }
        if (Encoding.UTF8.GetByteCount(json) > 512 * 1024)
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_TOO_LARGE");

        StatRunExportColumnManifest? parsed;
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            EnsureNoDuplicateProperties(document.RootElement, "$", 0);
            parsed = JsonSerializer.Deserialize<StatRunExportColumnManifest>(
                document.RootElement.GetRawText(),
                StrictJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "EXPORT_COLUMN_MANIFEST_JSON_INVALID",
                exception);
        }

        var manifest = Validate(parsed ?? throw new InvalidOperationException(
            "EXPORT_COLUMN_MANIFEST_REQUIRED"));
        var canonical = Canonicalize(manifest);
        if (!string.Equals(json, canonical, StringComparison.Ordinal))
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_NOT_CANONICAL");
        var computed = Hash(canonical);
        if (!string.Equals(declaredSha256, computed, StringComparison.Ordinal))
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_DIGEST_MISMATCH");
        return new StatRunExportColumnManifestSidecar(
            manifest,
            canonical,
            computed);
    }

    internal static string ComputeOwnerSemanticSha256(
        string resultKind,
        string workId,
        string scopeType,
        string scopeId,
        string resultId,
        string resultSha256,
        string configSha256,
        string sourceSha256,
        string filterSha256,
        int lifecycleRevision,
        int rowCount,
        int columnCount,
        string? columnManifestSha256)
        => StatRunCanonicalJson.HashObject(new
        {
            schemaVersion = StatRunExportContract.SchemaVersion,
            resultKind,
            workId,
            scopeType,
            scopeId,
            resultId,
            resultSha256,
            configSha256,
            sourceSha256,
            filterSha256,
            lifecycleRevision,
            rowCount,
            columnCount,
            columnManifestSha256
        });

    private static StatRunExportColumnManifest Validate(
        StatRunExportColumnManifest manifest)
    {
        if (!string.Equals(
                manifest.SchemaVersion,
                SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_SCHEMA_INVALID");
        }
        if (manifest.Columns is null || manifest.Columns.Count == 0 ||
            manifest.Columns.Count > 512)
        {
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_COUNT_INVALID");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<StatRunExportColumnManifestEntry>(
            manifest.Columns.Count);
        for (var index = 0; index < manifest.Columns.Count; index++)
        {
            var column = manifest.Columns[index] ?? throw new InvalidOperationException(
                "EXPORT_COLUMN_MANIFEST_ENTRY_REQUIRED");
            if (column.Ordinal != index || string.IsNullOrWhiteSpace(column.Name) ||
                column.Name.Length > 256 || !names.Add(column.Name) ||
                !ValueTypes.IsSupported(column.ValueType) ||
                !BlankPolicies.IsSupported(column.BlankPolicy))
            {
                throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_ENTRY_INVALID");
            }
            normalized.Add(column);
        }

        var ordinal = normalized[0];
        if (!string.Equals(ordinal.Name, "ordinal", StringComparison.Ordinal) ||
            !string.Equals(ordinal.ValueType, ValueTypes.Integer, StringComparison.Ordinal) ||
            !string.Equals(
                ordinal.BlankPolicy,
                BlankPolicies.Forbidden,
                StringComparison.Ordinal) ||
            ordinal.IsFullFilterTotal)
        {
            throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_ORDINAL_INVALID");
        }
        return manifest with { Columns = normalized };
    }

    private static string Canonicalize(StatRunExportColumnManifest manifest)
    {
        var element = JsonSerializer.SerializeToElement(manifest, StrictJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void EnsureNoDuplicateProperties(
        JsonElement value,
        string path,
        int depth)
    {
        if (depth > 32)
            throw new JsonException($"Maximum depth exceeded at {path}.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException($"Duplicate property at {path}.{property.Name}.");
                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                EnsureNoDuplicateProperties(item, $"{path}[{index++}]", depth + 1);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new InvalidOperationException("EXPORT_COLUMN_MANIFEST_TOKEN_INVALID");
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed record StatRunExportColumnManifest(
    string SchemaVersion,
    IReadOnlyList<StatRunExportColumnManifestEntry> Columns);

internal sealed record StatRunExportColumnManifestEntry(
    int Ordinal,
    string Name,
    string ValueType,
    string BlankPolicy,
    bool IsFullFilterTotal);

internal sealed record StatRunExportColumnManifestSidecar(
    StatRunExportColumnManifest Manifest,
    string CanonicalJson,
    string Sha256);
