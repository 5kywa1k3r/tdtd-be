using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal static class P711FreshRegressionSemanticHash
{
    public static string Compute(JsonElement command)
    {
        RequireKind(command, JsonValueKind.Object, "command");

        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   payload,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "commandId",
                RequireString(command, "commandId"));
            writer.WriteNumber(
                "expectedExitCode",
                RequireInt32(command, "expectedExitCode"));
            writer.WriteNumber(
                "actualExitCode",
                RequireInt32(command, "actualExitCode"));

            writer.WritePropertyName("validation");
            RequireProperty(command, "validation").WriteTo(writer);

            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            var artifacts = RequireProperty(
                command,
                "artifacts",
                JsonValueKind.Array);
            foreach (var artifact in artifacts.EnumerateArray())
            {
                RequireKind(
                    artifact,
                    JsonValueKind.Object,
                    "command.artifacts[]");

                writer.WriteStartObject();
                writer.WriteString(
                    "path",
                    RequireString(artifact, "path"));
                writer.WriteString(
                    "sha256",
                    RequireString(artifact, "sha256"));
                writer.WriteNumber(
                    "bytes",
                    RequireInt64(artifact, "bytes"));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(
                SHA256.HashData(payload.GetBuffer().AsSpan(
                    0,
                    checked((int)payload.Length))))
            .ToLowerInvariant();
    }

    private static JsonElement RequireProperty(
        JsonElement owner,
        string propertyName)
    {
        RequireKind(owner, JsonValueKind.Object, "property owner");
        if (!owner.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException(
                $"Required JSON property '{propertyName}' is missing.");
        }

        return property;
    }

    private static JsonElement RequireProperty(
        JsonElement owner,
        string propertyName,
        JsonValueKind expectedKind)
    {
        var property = RequireProperty(owner, propertyName);
        RequireKind(property, expectedKind, propertyName);
        return property;
    }

    private static string RequireString(
        JsonElement owner,
        string propertyName)
    {
        var property = RequireProperty(
            owner,
            propertyName,
            JsonValueKind.String);
        return property.GetString()
            ?? throw new InvalidOperationException(
                $"Required JSON property '{propertyName}' is null.");
    }

    private static int RequireInt32(
        JsonElement owner,
        string propertyName)
    {
        var property = RequireProperty(
            owner,
            propertyName,
            JsonValueKind.Number);
        if (!property.TryGetInt32(out var value))
        {
            throw new InvalidOperationException(
                $"Required JSON property '{propertyName}' is not an Int32.");
        }

        return value;
    }

    private static long RequireInt64(
        JsonElement owner,
        string propertyName)
    {
        var property = RequireProperty(
            owner,
            propertyName,
            JsonValueKind.Number);
        if (!property.TryGetInt64(out var value))
        {
            throw new InvalidOperationException(
                $"Required JSON property '{propertyName}' is not an Int64.");
        }

        return value;
    }

    private static void RequireKind(
        JsonElement value,
        JsonValueKind expectedKind,
        string context)
    {
        if (value.ValueKind != expectedKind)
        {
            throw new InvalidOperationException(
                $"JSON value '{context}' must be {expectedKind}, " +
                $"but was {value.ValueKind}.");
        }
    }
}
