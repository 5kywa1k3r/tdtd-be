using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateCanonical
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text(value))));
    internal static string Text<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, JsonSerializer.SerializeToElement(value, Json));
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    internal static string Key(params string[] parts) => Hash(parts);
    internal static void CommandId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new AggregatePreviewException("AGG_COMMAND_ID_INVALID");
    }
}

// Stateless confirmations: no preview write/cache; key supplied by host secret configuration.
// The token is evidence of what was shown, never authorization or a substitute for re-evaluation.
internal sealed class AggregateConfirmationTokens(byte[] key)
{
    private readonly byte[] _key = key.Length >= 32 ? key.ToArray() : throw new ArgumentException("At least 256 bits required", nameof(key));
    internal string Issue(AggregateConfirmation confirmation)
    {
        var body = Encoding.UTF8.GetBytes(AggregateCanonical.Text(confirmation));
        return Convert.ToBase64String(body) + "." + Convert.ToBase64String(HMACSHA256.HashData(_key, body));
    }
    internal void Verify(string token, AggregateConfirmation expected, DateTimeOffset now)
    {
        try
        {
            if (token.Length > 8192) throw new FormatException();
            var pieces = token.Split('.'); if (pieces.Length != 2) throw new FormatException();
            var body = Convert.FromBase64String(pieces[0]); var signature = Convert.FromBase64String(pieces[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, body))) throw new FormatException();
            var value = JsonSerializer.Deserialize<AggregateConfirmation>(body, AggregateCanonical.Json) ?? throw new FormatException();
            if (value.ExpiresAt <= now || value.ExpiresAt > now.AddMinutes(5)
                || value with { ExpiresAt = expected.ExpiresAt } != expected) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { throw new AggregatePreviewException("AGG_CONFIRMATION_STALE"); }
    }
}
