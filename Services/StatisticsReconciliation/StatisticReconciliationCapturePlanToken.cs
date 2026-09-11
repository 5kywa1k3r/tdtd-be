using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.Services.StatisticsReconciliation;

internal sealed record StatisticReconciliationCapturePlanTokenPayload(
    string SchemaVersion,
    string ActorUserId,
    string? TenantUnitId,
    string WorkId,
    string ScopeAssignmentId,
    string P9ResultKind,
    string P9ResultId,
    string P9RunId,
    string ConceptKey,
    string Grain,
    string CanonicalFilterJson,
    string FilterSha256,
    string ExportId,
    string P8ConfigurationOwnerId,
    string P8ConfigurationBundleSha256,
    string BasicDisposition,
    string AdvancedDisposition,
    string DiffDisposition,
    string ApiSurface,
    string ApiOwnerResultId,
    string PlanSha256,
    string ActualConfigurationBundleSha256,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc);

internal static class StatisticReconciliationCapturePlanToken
{
    internal const string SchemaVersion = "P10_CAPTURE_PLAN_TOKEN_V1";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxTokenLength = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] DeriveSigningKey(string configuredKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException(
                "Statistic reconciliation capture-plan signing key is missing.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            "P10_CAPTURE_PLAN_TOKEN_SIGNING_KEY_V1",
            configuredKey)));
    }

    internal static string Issue(
        StatisticReconciliationCapturePlanTokenPayload payload,
        ReadOnlySpan<byte> signingKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RequireSigningKey(signingKey);
        ValidatePayload(payload, payload.IssuedAtUtc, requireCurrent: false);
        var payloadSegment = Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        var signature = Sign(payloadSegment, signingKey);
        return $"{payloadSegment}.{Base64UrlEncode(signature)}";
    }

    internal static StatisticReconciliationCapturePlanTokenPayload Validate(
        string token,
        ReadOnlySpan<byte> signingKey,
        DateTime nowUtc)
        => Decode(token, signingKey, nowUtc, requireCurrent: true);

    internal static StatisticReconciliationCapturePlanTokenPayload
        ValidateForReplay(
            string token,
            ReadOnlySpan<byte> signingKey,
            DateTime nowUtc)
        => Decode(token, signingKey, nowUtc, requireCurrent: false);

    private static StatisticReconciliationCapturePlanTokenPayload Decode(
        string token,
        ReadOnlySpan<byte> signingKey,
        DateTime nowUtc,
        bool requireCurrent)
    {
        RequireSigningKey(signingKey);
        if (string.IsNullOrWhiteSpace(token) ||
            token.Length > MaxTokenLength ||
            !string.Equals(token, token.Trim(), StringComparison.Ordinal) ||
            token.Any(char.IsControl))
        {
            throw Invalid("TOKEN_FORMAT_INVALID");
        }

        var segments = token.Split('.');
        if (segments.Length != 2 ||
            !CanonicalBase64Url(segments[0]) ||
            !CanonicalBase64Url(segments[1]))
            throw Invalid("TOKEN_FORMAT_INVALID");

        byte[] suppliedSignature;
        byte[] payloadBytes;
        try
        {
            suppliedSignature = Base64UrlDecode(segments[1]);
            payloadBytes = Base64UrlDecode(segments[0]);
        }
        catch (FormatException)
        {
            throw Invalid("TOKEN_FORMAT_INVALID");
        }

        var expectedSignature = Sign(segments[0], signingKey);
        if (suppliedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(
                suppliedSignature,
                expectedSignature))
            throw Invalid("TOKEN_SIGNATURE_INVALID");

        StatisticReconciliationCapturePlanTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<
                StatisticReconciliationCapturePlanTokenPayload>(
                    payloadBytes,
                    JsonOptions);
        }
        catch (JsonException)
        {
            throw Invalid("TOKEN_PAYLOAD_INVALID");
        }
        if (payload is null)
            throw Invalid("TOKEN_PAYLOAD_INVALID");

        ValidatePayload(payload, nowUtc, requireCurrent);
        return payload;
    }

    private static void ValidatePayload(
        StatisticReconciliationCapturePlanTokenPayload payload,
        DateTime nowUtc,
        bool requireCurrent)
    {
        if (!string.Equals(
                payload.SchemaVersion,
                SchemaVersion,
                StringComparison.Ordinal) ||
            payload.IssuedAtUtc.Kind != DateTimeKind.Utc ||
            payload.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            payload.ExpiresAtUtc <= payload.IssuedAtUtc ||
            payload.ExpiresAtUtc - payload.IssuedAtUtc > Lifetime ||
            nowUtc.Kind != DateTimeKind.Utc)
            throw Invalid("TOKEN_PAYLOAD_INVALID");

        if (requireCurrent &&
            (nowUtc < payload.IssuedAtUtc.AddSeconds(-30) ||
             nowUtc >= payload.ExpiresAtUtc))
            throw Invalid("TOKEN_EXPIRED");
    }

    private static byte[] Sign(
        string payloadSegment,
        ReadOnlySpan<byte> signingKey)
    {
        using var hmac = new HMACSHA256(signingKey.ToArray());
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(string.Join(
            ".",
            SchemaVersion,
            payloadSegment)));
    }

    private static void RequireSigningKey(ReadOnlySpan<byte> signingKey)
    {
        if (signingKey.Length != 32)
            throw new InvalidOperationException(
                "Statistic reconciliation capture-plan signing key must be 256 bits.");
    }

    private static bool CanonicalBase64Url(string value)
        => value.Length > 0 &&
           value.All(character =>
               char.IsAsciiLetterOrDigit(character) ||
               character is '-' or '_') &&
           !value.Contains('=');

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value
            .Replace('-', '+')
            .Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(padded);
    }

    private static InvalidOperationException Invalid(string reason)
        => new($"STAT_RECONCILIATION_CAPTURE_PLAN_{reason}");
}