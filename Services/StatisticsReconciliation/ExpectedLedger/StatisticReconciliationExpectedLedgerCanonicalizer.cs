using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal readonly record struct ExpectedLedgerCanonicalJson(
    string Value,
    string Sha256);

internal static class StatisticReconciliationExpectedLedgerCanonicalizer
{
    internal static ExpectedLedgerCanonicalJson NormalizeObject(
        string? value,
        int maximumBytes,
        string path,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            throw Failure(reason, path, "A bounded JSON object is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                value,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Failure(reason, path, "JSON root must be an object.");

            EnsureNoDuplicateProperties(document.RootElement, path, reason, 0);

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       stream,
                       new JsonWriterOptions
                       {
                           Indented = false,
                           SkipValidation = false
                       }))
            {
                WriteCanonical(writer, document.RootElement, path, reason, 0);
            }

            if (stream.Length > maximumBytes)
                throw Failure(reason, path, "Canonical JSON exceeds the byte limit.");

            var bytes = stream.ToArray();
            return new ExpectedLedgerCanonicalJson(
                Encoding.UTF8.GetString(bytes),
                HashBytes(bytes));
        }
        catch (StatisticReconciliationExpectedLedgerInputException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw Failure(reason, path, "JSON is invalid.");
        }
        catch (OverflowException)
        {
            throw Failure(reason, path, "JSON number exponent is outside the supported range.");
        }
    }

    internal static string HashSequence(string domain, IEnumerable<string> fields)
    {
        var builder = new StringBuilder();
        AppendLengthPrefixed(builder, domain);
        foreach (var field in fields)
            AppendLengthPrefixed(builder, field);
        return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    internal static string HashSequence(string domain, params string[] fields)
        => HashSequence(domain, (IEnumerable<string>)fields);

    private static void AppendLengthPrefixed(StringBuilder builder, string value)
    {
        var byteLength = Encoding.UTF8.GetByteCount(value);
        builder
            .Append(byteLength.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static void EnsureNoDuplicateProperties(
        JsonElement value,
        string path,
        string reason,
        int depth)
    {
        if (depth > 64)
            throw Failure(reason, path, "JSON exceeds the maximum depth.");

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Failure(
                        reason,
                        $"{path}.{property.Name}",
                        "JSON has a duplicate property.");
                EnsureNoDuplicateProperties(
                    property.Value,
                    $"{path}.{property.Name}",
                    reason,
                    depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                EnsureNoDuplicateProperties(
                    item,
                    $"{path}[{index++}]",
                    reason,
                    depth + 1);
            }
        }
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        string path,
        string reason,
        int depth)
    {
        if (depth > 64)
            throw Failure(reason, path, "JSON exceeds the maximum depth.");

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(
                        writer,
                        property.Value,
                        $"{path}.{property.Name}",
                        reason,
                        depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(
                        writer,
                        item,
                        $"{path}[{index++}]",
                        reason,
                        depth + 1);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    NormalizeNumber(element.GetRawText(), path, reason),
                    skipInputValidation: false);
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
                throw Failure(reason, path, "JSON token is unsupported.");
        }
    }

    private static string NormalizeNumber(
        string raw,
        string path,
        string reason)
    {
        var index = 0;
        var negative = raw[index] == '-';
        if (negative)
            index++;

        var integerStart = index;
        while (index < raw.Length && raw[index] is >= '0' and <= '9')
            index++;
        var integerLength = index - integerStart;

        var fractionStart = index;
        var fractionLength = 0;
        if (index < raw.Length && raw[index] == '.')
        {
            index++;
            fractionStart = index;
            while (index < raw.Length && raw[index] is >= '0' and <= '9')
                index++;
            fractionLength = index - fractionStart;
        }

        long exponent = 0;
        if (index < raw.Length && raw[index] is 'e' or 'E')
        {
            index++;
            var exponentText = raw[index..];
            if (!long.TryParse(
                    exponentText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out exponent))
            {
                throw Failure(reason, path, "JSON number exponent is invalid.");
            }
            index = raw.Length;
        }

        if (index != raw.Length || integerLength == 0)
            throw Failure(reason, path, "JSON number is invalid.");

        var digits = string.Concat(
            raw.AsSpan(integerStart, integerLength),
            raw.AsSpan(fractionStart, fractionLength));
        var leading = 0;
        while (leading < digits.Length && digits[leading] == '0')
            leading++;
        if (leading == digits.Length)
            return "0";

        var trailingExclusive = digits.Length;
        while (trailingExclusive > leading + 1 &&
               digits[trailingExclusive - 1] == '0')
        {
            trailingExclusive--;
        }

        var significant = digits[leading..trailingExclusive];
        var decimalPosition = checked((long)integerLength + exponent - leading);
        var scientificExponent = checked(decimalPosition - 1);
        var mantissa = significant.Length == 1
            ? significant
            : $"{significant[0]}.{significant[1..]}";
        var sign = negative ? "-" : string.Empty;
        return scientificExponent == 0
            ? $"{sign}{mantissa}"
            : $"{sign}{mantissa}e{scientificExponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static StatisticReconciliationExpectedLedgerInputException Failure(
        string reason,
        string path,
        string message)
        => new(reason, path, message);
}
