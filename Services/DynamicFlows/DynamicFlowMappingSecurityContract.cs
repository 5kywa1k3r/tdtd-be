using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.DynamicFlows;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowMappingPreviewTokenBinding(
    string ActorId,
    string TargetReportId,
    string TargetAssignmentId,
    string FlowInstanceId,
    int ExecutionEpoch,
    int TargetPayloadRevision,
    int TargetLifecycleRevision,
    string SourceSignature,
    string ResultSemanticHash,
    string RuleSetHash,
    string AuthorizationScopeHash);

public sealed record DynamicFlowMappingPreviewTokenClaims(
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("targetReportId")] string TargetReportId,
    [property: JsonPropertyName("targetAssignmentId")] string TargetAssignmentId,
    [property: JsonPropertyName("flowInstanceId")] string FlowInstanceId,
    [property: JsonPropertyName("executionEpoch")] int ExecutionEpoch,
    [property: JsonPropertyName("targetPayloadRevision")] int TargetPayloadRevision,
    [property: JsonPropertyName("targetLifecycleRevision")] int TargetLifecycleRevision,
    [property: JsonPropertyName("sourceSignature")] string SourceSignature,
    [property: JsonPropertyName("resultSemanticHash")] string ResultSemanticHash,
    [property: JsonPropertyName("ruleSetHash")] string RuleSetHash,
    [property: JsonPropertyName("authorizationScopeHash")] string AuthorizationScopeHash,
    [property: JsonPropertyName("issuedAtUtc")] DateTime IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTime ExpiresAtUtc,
    [property: JsonPropertyName("tokenId")] string TokenId);

public sealed class DynamicFlowMappingSecurityException : Exception
{
    public DynamicFlowMappingSecurityException(
        string reason,
        string? field = null)
        : base(reason)
    {
        Reason = reason;
        Field = field;
    }

    public string Reason { get; }
    public string? Field { get; }
}

public static class DynamicFlowMappingSecurityContract
{
    public const string SourceSignatureVersion = "P7-SIG-1";
    public const string PreviewTokenVersion = "p7p1";
    public const int MinimumSigningKeyUtf8Bytes = 32;

    public const string CanonicalJsonInvalidReason =
        "DYNAMIC_FLOW_MAPPING_CANONICAL_JSON_INVALID";
    public const string PreviewTokenKeyUnavailableReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_KEY_UNAVAILABLE";
    public const string PreviewTokenInvalidReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_INVALID";
    public const string PreviewTokenSignatureInvalidReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_SIGNATURE_INVALID";
    public const string PreviewTokenExpiredReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_EXPIRED";
    public const string PreviewTokenNotYetValidReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_NOT_YET_VALID";
    public const string PreviewTokenActorMismatchReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_ACTOR_MISMATCH";
    public const string PreviewTokenTargetMismatchReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_TARGET_MISMATCH";
    public const string PreviewTokenEpochMismatchReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_EPOCH_MISMATCH";
    public const string PreviewTokenSnapshotMismatchReason =
        "DYNAMIC_FLOW_MAPPING_PREVIEW_TOKEN_SNAPSHOT_MISMATCH";
    public const string FlowOwnedConfigurationReason =
        "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED";

    public static readonly TimeSpan PreviewTokenTtl = TimeSpan.FromMinutes(15);

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions ClaimsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void ValidateFlowOwnedRequestInputs(
        DynamicFlowMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suppliedField =
            request.ActorRole is not null ? "actorRole" :
            request.Provenance is not null ? "provenance" :
            request.Changes is not null ? "changes" :
            request.SourceReports is not null ? "sourceReports" :
            request.SourceMode is not null ? "sourceMode" :
            request.SourceReportIds is not null ? "sourceReportIds" :
            request.FlowTemplateVersionId is not null
                ? "flowTemplateVersionId" :
            request.FlowTemplateId is not null ? "flowTemplateId" :
            request.FlowTemplateVersionNo.HasValue
                ? "flowTemplateVersionNo" :
            request.MappingRulesJson is not null ? "mappingRulesJson" :
            request.MappingRules is not null ? "mappingRules" :
            request.ConflictPolicy is not null ? "conflictPolicy" :
            request.ContributionPolicy is not null
                ? "contributionPolicy" :
            request.RequireSourceReport.HasValue
                ? "requireSourceReport" :
            request.AdditionalCallerInputs is { Count: > 0 }
                ? request.AdditionalCallerInputs.Keys
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .First()
                : null;

        if (suppliedField is not null)
        {
            throw Failure(
                FlowOwnedConfigurationReason,
                suppliedField);
        }
    }

    public static string CanonicalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            return StrictUtf8.GetString(CanonicalizeUtf8(document.RootElement));
        }
        catch (Exception error) when (
            error is JsonException or
            DecoderFallbackException or
            InvalidOperationException)
        {
            throw Failure(CanonicalJsonInvalidReason);
        }
    }

    public static byte[] CanonicalizeUtf8(string json)
        => StrictUtf8.GetBytes(CanonicalizeJson(json));

    public static string ComputeCanonicalSha256(string json)
        => LowerSha256(CanonicalizeUtf8(json));

    public static string ComputeCanonicalSha256(JsonElement value)
        => LowerSha256(CanonicalizeUtf8(value));

    public static string ComputeSourceSignature(string sourceFactsJson)
        => ComputeCanonicalSha256(sourceFactsJson);

    public static string IssuePreviewToken(
        DynamicFlowMappingPreviewTokenBinding binding,
        string tokenId,
        string? signingKey,
        DateTime nowUtc)
    {
        var keyBytes = RequireSigningKey(signingKey);
        try
        {
            RequireBinding(binding);
            RequireText(tokenId, PreviewTokenInvalidReason);
            RequireUtc(nowUtc);

            var claims = new DynamicFlowMappingPreviewTokenClaims(
                binding.ActorId,
                binding.TargetReportId,
                binding.TargetAssignmentId,
                binding.FlowInstanceId,
                binding.ExecutionEpoch,
                binding.TargetPayloadRevision,
                binding.TargetLifecycleRevision,
                binding.SourceSignature,
                binding.ResultSemanticHash,
                binding.RuleSetHash,
                binding.AuthorizationScopeHash,
                nowUtc,
                nowUtc.Add(PreviewTokenTtl),
                tokenId);
            var claimsElement = JsonSerializer.SerializeToElement(claims, ClaimsJsonOptions);
            var claimsSegment = EncodeBase64Url(CanonicalizeUtf8(claimsElement));
            var signingInput = Encoding.ASCII.GetBytes(
                $"{PreviewTokenVersion}.{claimsSegment}");
            var signature = HMACSHA256.HashData(keyBytes, signingInput);
            return $"{PreviewTokenVersion}.{claimsSegment}.{EncodeBase64Url(signature)}";
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Failure(PreviewTokenInvalidReason);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    public static DynamicFlowMappingPreviewTokenClaims ValidatePreviewToken(
        string? token,
        string? signingKey,
        DynamicFlowMappingPreviewTokenBinding expected,
        DateTime nowUtc)
    {
        var keyBytes = RequireSigningKey(signingKey);
        try
        {
            RequireBinding(expected);
            RequireUtc(nowUtc);

            var segments = token?.Split('.', StringSplitOptions.None);
            if (segments is not { Length: 3 } ||
                !string.Equals(
                    segments[0],
                    PreviewTokenVersion,
                    StringComparison.Ordinal) ||
                string.IsNullOrEmpty(segments[1]) ||
                string.IsNullOrEmpty(segments[2]))
            {
                throw Failure(PreviewTokenInvalidReason);
            }

            var signingInput = Encoding.ASCII.GetBytes(
                $"{PreviewTokenVersion}.{segments[1]}");
            var expectedSignature = HMACSHA256.HashData(keyBytes, signingInput);
            byte[] suppliedSignature;
            try
            {
                suppliedSignature = DecodeBase64Url(segments[2]);
            }
            catch (FormatException)
            {
                throw Failure(PreviewTokenInvalidReason);
            }

            if (suppliedSignature.Length != expectedSignature.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    suppliedSignature,
                    expectedSignature))
            {
                throw Failure(PreviewTokenSignatureInvalidReason);
            }

            byte[] claimsBytes;
            try
            {
                claimsBytes = DecodeBase64Url(segments[1]);
            }
            catch (FormatException)
            {
                throw Failure(PreviewTokenInvalidReason);
            }

            var claims = ReadCanonicalClaims(claimsBytes);
            ValidateClaims(claims, nowUtc);
            ValidateBinding(claims, expected);
            return claims;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static DynamicFlowMappingPreviewTokenClaims ReadCanonicalClaims(
        byte[] claimsBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(claimsBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var canonicalClaims = CanonicalizeUtf8(document.RootElement);
            if (!claimsBytes.AsSpan().SequenceEqual(canonicalClaims))
                throw Failure(PreviewTokenInvalidReason);

            return JsonSerializer.Deserialize<DynamicFlowMappingPreviewTokenClaims>(
                       claimsBytes,
                       ClaimsJsonOptions)
                   ?? throw Failure(PreviewTokenInvalidReason);
        }
        catch (DynamicFlowMappingSecurityException)
        {
            throw;
        }
        catch (Exception error) when (
            error is JsonException or
            NotSupportedException or
            DecoderFallbackException or
            InvalidOperationException)
        {
            throw Failure(PreviewTokenInvalidReason);
        }
    }

    private static void ValidateClaims(
        DynamicFlowMappingPreviewTokenClaims claims,
        DateTime nowUtc)
    {
        RequireText(claims.ActorId, PreviewTokenInvalidReason);
        RequireText(claims.TargetReportId, PreviewTokenInvalidReason);
        RequireText(claims.TargetAssignmentId, PreviewTokenInvalidReason);
        RequireText(claims.FlowInstanceId, PreviewTokenInvalidReason);
        RequireText(claims.TokenId, PreviewTokenInvalidReason);
        RequireRevision(claims.ExecutionEpoch, allowZero: false);
        RequireRevision(claims.TargetPayloadRevision, allowZero: true);
        RequireRevision(claims.TargetLifecycleRevision, allowZero: true);
        RequireLowerSha256(claims.SourceSignature);
        RequireLowerSha256(claims.ResultSemanticHash);
        RequireLowerSha256(claims.RuleSetHash);
        RequireLowerSha256(claims.AuthorizationScopeHash);
        RequireUtc(claims.IssuedAtUtc);
        RequireUtc(claims.ExpiresAtUtc);

        if (claims.ExpiresAtUtc - claims.IssuedAtUtc != PreviewTokenTtl)
            throw Failure(PreviewTokenInvalidReason);
        if (nowUtc < claims.IssuedAtUtc)
            throw Failure(PreviewTokenNotYetValidReason);
        if (nowUtc >= claims.ExpiresAtUtc)
            throw Failure(PreviewTokenExpiredReason);
    }

    private static void ValidateBinding(
        DynamicFlowMappingPreviewTokenClaims claims,
        DynamicFlowMappingPreviewTokenBinding expected)
    {
        if (!string.Equals(claims.ActorId, expected.ActorId, StringComparison.Ordinal))
            throw Failure(PreviewTokenActorMismatchReason);

        if (!string.Equals(
                claims.TargetReportId,
                expected.TargetReportId,
                StringComparison.Ordinal) ||
            !string.Equals(
                claims.TargetAssignmentId,
                expected.TargetAssignmentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                claims.FlowInstanceId,
                expected.FlowInstanceId,
                StringComparison.Ordinal))
        {
            throw Failure(PreviewTokenTargetMismatchReason);
        }

        if (claims.ExecutionEpoch != expected.ExecutionEpoch)
            throw Failure(PreviewTokenEpochMismatchReason);

        if (claims.TargetPayloadRevision != expected.TargetPayloadRevision ||
            claims.TargetLifecycleRevision != expected.TargetLifecycleRevision ||
            !string.Equals(
                claims.SourceSignature,
                expected.SourceSignature,
                StringComparison.Ordinal) ||
            !string.Equals(
                claims.ResultSemanticHash,
                expected.ResultSemanticHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                claims.RuleSetHash,
                expected.RuleSetHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                claims.AuthorizationScopeHash,
                expected.AuthorizationScopeHash,
                StringComparison.Ordinal))
        {
            throw Failure(PreviewTokenSnapshotMismatchReason);
        }
    }

    private static void RequireBinding(
        DynamicFlowMappingPreviewTokenBinding? binding)
    {
        if (binding is null)
            throw Failure(PreviewTokenInvalidReason);

        RequireText(binding.ActorId, PreviewTokenInvalidReason);
        RequireText(binding.TargetReportId, PreviewTokenInvalidReason);
        RequireText(binding.TargetAssignmentId, PreviewTokenInvalidReason);
        RequireText(binding.FlowInstanceId, PreviewTokenInvalidReason);
        RequireRevision(binding.ExecutionEpoch, allowZero: false);
        RequireRevision(binding.TargetPayloadRevision, allowZero: true);
        RequireRevision(binding.TargetLifecycleRevision, allowZero: true);
        RequireLowerSha256(binding.SourceSignature);
        RequireLowerSha256(binding.ResultSemanticHash);
        RequireLowerSha256(binding.RuleSetHash);
        RequireLowerSha256(binding.AuthorizationScopeHash);
    }

    private static void RequireText(string? value, string reason)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Failure(reason);
    }

    private static void RequireRevision(int value, bool allowZero)
    {
        if (value < 0 || (!allowZero && value == 0))
            throw Failure(PreviewTokenInvalidReason);
    }

    private static void RequireLowerSha256(string? value)
    {
        if (value is not { Length: 64 })
            throw Failure(PreviewTokenInvalidReason);

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                throw Failure(PreviewTokenInvalidReason);
            }
        }
    }

    private static void RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw Failure(PreviewTokenInvalidReason);
    }

    private static byte[] RequireSigningKey(string? signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw Failure(PreviewTokenKeyUnavailableReason);
        }

        try
        {
            if (StrictUtf8.GetByteCount(signingKey) < MinimumSigningKeyUtf8Bytes)
                throw Failure(PreviewTokenKeyUnavailableReason);

            return StrictUtf8.GetBytes(signingKey);
        }
        catch (EncoderFallbackException)
        {
            throw Failure(PreviewTokenKeyUnavailableReason);
        }
    }

    private static byte[] CanonicalizeUtf8(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, value);
        }

        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
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
                if (value.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (value.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(value.GetDouble());
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
                throw Failure(CanonicalJsonInvalidReason);
        }
    }

    private static string LowerSha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string EncodeBase64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Any(character =>
                !(character is >= 'A' and <= 'Z') &&
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '-' and not '_'))
        {
            throw new FormatException();
        }

        var remainder = value.Length % 4;
        if (remainder == 1)
            throw new FormatException();

        var padded = value
            .Replace('-', '+')
            .Replace('_', '/')
            .PadRight(value.Length + ((4 - remainder) % 4), '=');
        var decoded = Convert.FromBase64String(padded);
        if (!string.Equals(EncodeBase64Url(decoded), value, StringComparison.Ordinal))
            throw new FormatException();
        return decoded;
    }

    private static DynamicFlowMappingSecurityException Failure(
        string reason,
        string? field = null)
        => new(reason, field);
}
