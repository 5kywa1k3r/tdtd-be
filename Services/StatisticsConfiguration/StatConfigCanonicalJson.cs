using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

public sealed record NormalizedStatConfigCommand<TPayload>(
    string CommandId,
    long ExpectedRevision,
    string ExpectedConfigHash,
    TPayload Payload,
    string RequestHash);

public static class StatConfigCanonicalJson
{
    private static readonly HashSet<string> SetArrayProperties = new(
        new[]
        {
            "tagCodes",
            "statisticLabelCodes",
            "allowedRowLabelCodes",
            "metricLabelTargets",
            "flowScopes"
        },
        StringComparer.Ordinal);

    private static readonly Regex CommandIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Regex = new(
        "^[a-fA-F0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly JsonSerializerOptions StrictJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string EmptyConfigHash { get; } = HashUtf8("null");

    public static T DeserializeStrict<T>(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
            throw SchemaError("$", "PAYLOAD_REQUIRED");

        ValidateNoDuplicateProperties(element, "$");
        try
        {
            return JsonSerializer.Deserialize<T>(
                       element.GetRawText(),
                       StrictJsonOptions)
                   ?? throw SchemaError("$", "NULL_PAYLOAD");
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw SchemaError(
                string.IsNullOrWhiteSpace(ex.Path) ? "$" : ex.Path!,
                "SCHEMA_MISMATCH");
        }
    }

    public static NormalizedStatConfigCommand<TPayload> NormalizeCommand<TPayload>(
        StatConfigMutationEnvelope<TPayload>? envelope,
        string commandKind)
    {
        if (envelope is null)
            throw SchemaError("$", "ENVELOPE_REQUIRED");

        var commandId = envelope.CommandId?.Trim();
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_COMMAND_ID_REQUIRED,
                new { path = "$.commandId" });
        }
        if (!CommandIdRegex.IsMatch(commandId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_COMMAND_ID_INVALID,
                new { path = "$.commandId", maxLength = 128 });
        }
        if (!envelope.ExpectedRevision.HasValue ||
            envelope.ExpectedRevision.Value < 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_EXPECTED_REVISION_REQUIRED,
                new { path = "$.expectedRevision" });
        }

        var expectedHash = envelope.ExpectedConfigHash?.Trim();
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_EXPECTED_HASH_REQUIRED,
                new { path = "$.expectedConfigHash" });
        }
        if (!Sha256Regex.IsMatch(expectedHash))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_CONFIG_EXPECTED_HASH_INVALID,
                new { path = "$.expectedConfigHash" });
        }
        if (envelope.Payload is null)
            throw SchemaError("$.payload", "PAYLOAD_REQUIRED");

        expectedHash = expectedHash.ToLowerInvariant();
        var requestHash = HashObject(new
        {
            commandKind,
            expectedRevision = envelope.ExpectedRevision.Value,
            expectedConfigHash = expectedHash,
            payload = envelope.Payload
        });

        return new NormalizedStatConfigCommand<TPayload>(
            commandId,
            envelope.ExpectedRevision.Value,
            expectedHash,
            envelope.Payload,
            requestHash);
    }

    public static string HashObject<T>(T value)
        => HashUtf8(Canonicalize(value));

    public static string HashUtf8(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static string Canonicalize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(
            value,
            StrictJsonOptions);
        return CanonicalizeElement(element);
    }

    public static string CanonicalizeElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false
                   }))
        {
            WriteCanonical(writer, element, null);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void ValidateNoDuplicateProperties(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                if (!seen.Add(property.Name))
                    throw SchemaError(propertyPath, "DUPLICATE_PROPERTY");
                ValidateNoDuplicateProperties(property.Value, propertyPath);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                WriteArray(writer, element, propertyName);
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(element.GetDouble());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw SchemaError("$", "UNSUPPORTED_JSON_TOKEN");
        }
    }

    private static void WriteArray(
        Utf8JsonWriter writer,
        JsonElement element,
        string? propertyName)
    {
        writer.WriteStartArray();
        if (propertyName is null ||
            !SetArrayProperties.Contains(propertyName))
        {
            foreach (var item in element.EnumerateArray())
                WriteCanonical(writer, item, null);
            writer.WriteEndArray();
            return;
        }

        var normalizedItems = element.EnumerateArray()
            .Select(item => NormalizeSetItem(item, propertyName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal);
        foreach (var normalized in normalizedItems)
        {
            using var document = JsonDocument.Parse(normalized);
            WriteCanonical(writer, document.RootElement, null);
        }
        writer.WriteEndArray();
    }

    private static string NormalizeSetItem(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.String)
            return CanonicalizeElement(element);

        var value = element.GetString()?.Trim() ?? string.Empty;
        value = propertyName == "flowScopes"
            ? value.ToUpperInvariant()
            : value.ToLowerInvariant();
        return JsonSerializer.Serialize(value, StrictJsonOptions);
    }

    private static AppException SchemaError(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });
}

public static class StatConfigIsolationGuard
{
    private static readonly AsyncLocal<int> ConfigurationMutationDepth =
        new();

    public const string ProhibitedResultJob =
        "WorkReportStatisticRebuildJob";

    public static IDisposable EnterConfigurationMutation(
        string ownerKind)
    {
        if (string.IsNullOrWhiteSpace(ownerKind))
            throw new ArgumentException("ownerKind is required", nameof(ownerKind));
        ConfigurationMutationDepth.Value++;
        return new ConfigurationMutationScope();
    }

    public static void ThrowIfConfigurationMutationActive(
        string requestedOperation)
    {
        if (ConfigurationMutationDepth.Value > 0)
            RejectResultMaterializer(requestedOperation);
    }

    public static void RejectResultMaterializer(string requestedOperation)
    {
        throw AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                requestedOperation,
                targetPhase = "P9",
                reason = "P8_CONFIG_ONLY"
            });
    }

    private sealed class ConfigurationMutationScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            ConfigurationMutationDepth.Value = Math.Max(
                0,
                ConfigurationMutationDepth.Value - 1);
        }
    }
}
