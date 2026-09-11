using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingSecurityContractTests
{
    private const string SigningKey = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime IssuedAtUtc =
        new(2026, 7, 30, 12, 34, 56, DateTimeKind.Utc);

    public static void Run()
    {
        CanonicalJsonSortsPropertiesButPreservesArrays();
        PreviewTokenIsDeterministicAndHasExactClaims();
        SigningKeyUsesUtf8ByteBoundary();
        TamperAndTimeFailuresUseStableReasons();
        ActorTargetEpochAndSnapshotBindingsUseStableReasons();
        NonCanonicalOrWrongTtlClaimsFailClosed();
    }

    private static void CanonicalJsonSortsPropertiesButPreservesArrays()
    {
        var canonical = DynamicFlowMappingSecurityContract.CanonicalizeJson(
            """{"b":2,"a":1}""");
        Require(
            canonical == """{"a":1,"b":2}""",
            "canonical JSON must sort object properties ordinally");
        Require(
            DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
                """{"b":2,"a":1}""") ==
            "43258cff783fe7036d8a43033f830adfc60ec037382473548ac742b888292777",
            "P7-SIG-1 hash must be lowercase SHA-256 over canonical UTF-8 JSON");

        var first = DynamicFlowMappingSecurityContract.CanonicalizeJson(
            """{"items":[{"z":"Nguyễn","a":"An"},2,1]}""");
        var reordered = DynamicFlowMappingSecurityContract.CanonicalizeJson(
            """{"items":[{"a":"An","z":"Nguyễn"},2,1]}""");
        var callerReorderedArray =
            DynamicFlowMappingSecurityContract.CanonicalizeJson(
                """{"items":[{"a":"An","z":"Nguyễn"},1,2]}""");
        Require(
            first == reordered,
            "property order inside array objects must not affect canonical JSON");
        Require(
            first.Contains(""",2,1]""", StringComparison.Ordinal),
            "canonicalization must preserve caller-owned array order");
        Require(
            DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(first) !=
            DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
                callerReorderedArray),
            "the security contract must never sort arrays implicitly");
    }

    private static void PreviewTokenIsDeterministicAndHasExactClaims()
    {
        var binding = Binding();
        var token = DynamicFlowMappingSecurityContract.IssuePreviewToken(
            binding,
            "token-001",
            SigningKey,
            IssuedAtUtc);
        var replay = DynamicFlowMappingSecurityContract.IssuePreviewToken(
            binding,
            "token-001",
            SigningKey,
            IssuedAtUtc);
        Require(token == replay, "same snapshot and clock must issue the same token");
        Require(
            token.StartsWith("p7p1.", StringComparison.Ordinal) &&
            token.Split('.').Length == 3,
            "preview token must use the exact p7p1 three-segment format");

        var claims = DynamicFlowMappingSecurityContract.ValidatePreviewToken(
            token,
            SigningKey,
            binding,
            IssuedAtUtc.AddMinutes(1));
        Require(
            claims.ActorId == binding.ActorId &&
            claims.TargetReportId == binding.TargetReportId &&
            claims.TargetAssignmentId == binding.TargetAssignmentId &&
            claims.FlowInstanceId == binding.FlowInstanceId &&
            claims.ExecutionEpoch == binding.ExecutionEpoch &&
            claims.TargetPayloadRevision == binding.TargetPayloadRevision &&
            claims.TargetLifecycleRevision == binding.TargetLifecycleRevision &&
            claims.SourceSignature == binding.SourceSignature &&
            claims.ResultSemanticHash == binding.ResultSemanticHash &&
            claims.RuleSetHash == binding.RuleSetHash &&
            claims.AuthorizationScopeHash ==
            binding.AuthorizationScopeHash &&
            claims.IssuedAtUtc == IssuedAtUtc &&
            claims.ExpiresAtUtc == IssuedAtUtc.AddMinutes(15) &&
            claims.TokenId == "token-001",
            "validated claims must preserve every exact actor/target/epoch/snapshot pin");
    }

    private static void SigningKeyUsesUtf8ByteBoundary()
    {
        AssertReason(
            () => DynamicFlowMappingSecurityContract.IssuePreviewToken(
                Binding(),
                "token-short-key",
                new string('k', 31),
                IssuedAtUtc),
            DynamicFlowMappingSecurityContract.PreviewTokenKeyUnavailableReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.IssuePreviewToken(
                Binding(),
                "token-blank-key",
                new string(' ', 32),
                IssuedAtUtc),
            DynamicFlowMappingSecurityContract.PreviewTokenKeyUnavailableReason);

        var utf8Key = string.Concat(Enumerable.Repeat("é", 16));
        Require(
            Encoding.UTF8.GetByteCount(utf8Key) == 32,
            "test key must exercise the exact UTF-8 byte boundary");
        var token = DynamicFlowMappingSecurityContract.IssuePreviewToken(
            Binding(),
            "token-utf8-key",
            utf8Key,
            IssuedAtUtc);
        _ = DynamicFlowMappingSecurityContract.ValidatePreviewToken(
            token,
            utf8Key,
            Binding(),
            IssuedAtUtc.AddSeconds(1));
    }

    private static void TamperAndTimeFailuresUseStableReasons()
    {
        var token = DynamicFlowMappingSecurityContract.IssuePreviewToken(
            Binding(),
            "token-time",
            SigningKey,
            IssuedAtUtc);
        var segments = token.Split('.');
        var signature = segments[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        var tampered = $"{segments[0]}.{segments[1]}.{new string(signature)}";

        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                tampered,
                SigningKey,
                Binding(),
                IssuedAtUtc.AddMinutes(1)),
            DynamicFlowMappingSecurityContract.PreviewTokenSignatureInvalidReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                Binding(),
                IssuedAtUtc.AddTicks(-1)),
            DynamicFlowMappingSecurityContract.PreviewTokenNotYetValidReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                Binding(),
                IssuedAtUtc.AddMinutes(15)),
            DynamicFlowMappingSecurityContract.PreviewTokenExpiredReason);
    }

    private static void ActorTargetEpochAndSnapshotBindingsUseStableReasons()
    {
        var binding = Binding();
        var token = DynamicFlowMappingSecurityContract.IssuePreviewToken(
            binding,
            "token-binding",
            SigningKey,
            IssuedAtUtc);
        var validationTime = IssuedAtUtc.AddMinutes(1);

        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                binding with { ActorId = "actor-002" },
                validationTime),
            DynamicFlowMappingSecurityContract.PreviewTokenActorMismatchReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                binding with { TargetReportId = "report-002" },
                validationTime),
            DynamicFlowMappingSecurityContract.PreviewTokenTargetMismatchReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                binding with { ExecutionEpoch = 8 },
                validationTime),
            DynamicFlowMappingSecurityContract.PreviewTokenEpochMismatchReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                binding with { TargetPayloadRevision = 18 },
                validationTime),
            DynamicFlowMappingSecurityContract.PreviewTokenSnapshotMismatchReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                binding with { SourceSignature = Sha('d') },
                validationTime),
            DynamicFlowMappingSecurityContract.PreviewTokenSnapshotMismatchReason);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                token,
                SigningKey,
                binding with { AuthorizationScopeHash = Sha('e') },
                validationTime),
            DynamicFlowMappingSecurityContract.PreviewTokenSnapshotMismatchReason);
    }

    private static void NonCanonicalOrWrongTtlClaimsFailClosed()
    {
        var validToken = DynamicFlowMappingSecurityContract.IssuePreviewToken(
            Binding(),
            "token-claims",
            SigningKey,
            IssuedAtUtc);
        var validClaimsJson = DecodeClaimsJson(validToken);
        using var document = JsonDocument.Parse(validClaimsJson);

        var reversedProperties = document.RootElement
            .EnumerateObject()
            .Reverse()
            .Select(property =>
                $"{JsonSerializer.Serialize(property.Name)}:{property.Value.GetRawText()}");
        var nonCanonicalJson = $"{{{string.Join(",", reversedProperties)}}}";
        var nonCanonicalToken = SignRawClaims(nonCanonicalJson);
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                nonCanonicalToken,
                SigningKey,
                Binding(),
                IssuedAtUtc.AddMinutes(1)),
            DynamicFlowMappingSecurityContract.PreviewTokenInvalidReason);

        var claims =
            JsonSerializer.Deserialize<DynamicFlowMappingPreviewTokenClaims>(
                validClaimsJson)
            ?? throw new InvalidOperationException("valid claims were not deserialized");
        var wrongTtlToken = SignCanonicalClaims(
            claims with { ExpiresAtUtc = claims.IssuedAtUtc.AddMinutes(14) });
        AssertReason(
            () => DynamicFlowMappingSecurityContract.ValidatePreviewToken(
                wrongTtlToken,
                SigningKey,
                Binding(),
                IssuedAtUtc.AddMinutes(1)),
            DynamicFlowMappingSecurityContract.PreviewTokenInvalidReason);
    }

    private static DynamicFlowMappingPreviewTokenBinding Binding()
        => new(
            "actor-001",
            "report-001",
            "assignment-001",
            "flow-instance-001",
            7,
            17,
            4,
            Sha('a'),
            Sha('b'),
            Sha('c'),
            Sha('d'));

    private static string Sha(char value) => new(value, 64);

    private static string DecodeClaimsJson(string token)
    {
        var segment = token.Split('.')[1]
            .Replace('-', '+')
            .Replace('_', '/');
        segment = segment.PadRight(
            segment.Length + ((4 - segment.Length % 4) % 4),
            '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(segment));
    }

    private static string SignCanonicalClaims(
        DynamicFlowMappingPreviewTokenClaims claims)
    {
        var raw = JsonSerializer.Serialize(claims);
        return SignRawClaims(
            DynamicFlowMappingSecurityContract.CanonicalizeJson(raw));
    }

    private static string SignRawClaims(string claimsJson)
    {
        var claimsSegment = EncodeBase64Url(Encoding.UTF8.GetBytes(claimsJson));
        var input = Encoding.ASCII.GetBytes($"p7p1.{claimsSegment}");
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningKey),
            input);
        return $"p7p1.{claimsSegment}.{EncodeBase64Url(signature)}";
    }

    private static string EncodeBase64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void AssertReason(
        Action action,
        string expectedReason)
    {
        try
        {
            action();
        }
        catch (DynamicFlowMappingSecurityException error)
        {
            Require(
                error.Reason == expectedReason &&
                error.Message == expectedReason,
                $"expected stable reason {expectedReason}, got {error.Reason}");
            return;
        }

        throw new InvalidOperationException(
            $"expected security failure {expectedReason}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
