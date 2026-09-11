using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.DirectReconciliation;

internal static class StatisticReconciliationDirectCanonical
{
    internal static string Required(string? value, string path, int maximum = 4096)
    {
        if (value is null || value.Length is < 1 || value.Length > maximum ||
            value != value.Trim() || value.Any(char.IsControl))
            throw Invalid(path);
        return value;
    }

    internal static string? Optional(string? value, string path)
        => value is null ? null : Required(value, path);

    internal static string Upper(string? value, string path)
    {
        var required = Required(value, path);
        if (required != required.ToUpperInvariant())
            throw Invalid(path);
        return required;
    }

    internal static string Sha(string? value, string path)
    {
        var required = Required(value, path, 64);
        if (required.Length != 64 || required.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid(path);
        return required;
    }

    internal static string Hash(string domain, params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, Required(domain, "hash.domain"));
        foreach (var field in fields)
            Add(hash, field ?? throw Invalid("hash.field"));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string CanonicalJson(string value, string path)
    {
        Required(value, path, 65_536);
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var builder = new StringBuilder();
            WriteJson(document.RootElement, builder);
            return builder.ToString();
        }
        catch (JsonException)
        {
            throw Invalid(path);
        }
    }

    internal static StatisticReconciliationDirectException Invalid(string path)
        => new(StatisticReconciliationDirectFailureCodes.EvidenceInvalid, path);

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture)));
        hash.AppendData([0x3a]);
        hash.AppendData(bytes);
    }

    private static void WriteJson(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var properties = value.EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                for (var index = 0; index < properties.Length; index++)
                {
                    if (index > 0) builder.Append(',');
                    builder.Append(JsonSerializer.Serialize(properties[index].Name));
                    builder.Append(':');
                    WriteJson(properties[index].Value, builder);
                }
                builder.Append('}');
                return;
            case JsonValueKind.Array:
                builder.Append('[');
                var items = value.EnumerateArray().ToArray();
                for (var index = 0; index < items.Length; index++)
                {
                    if (index > 0) builder.Append(',');
                    WriteJson(items[index], builder);
                }
                builder.Append(']');
                return;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(value.GetString()));
                return;
            case JsonValueKind.Number:
                builder.Append(value.GetRawText());
                return;
            case JsonValueKind.True:
                builder.Append("true");
                return;
            case JsonValueKind.False:
                builder.Append("false");
                return;
            case JsonValueKind.Null:
                builder.Append("null");
                return;
            default:
                throw Invalid("json.valueKind");
        }
    }
}
