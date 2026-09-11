using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private JsonObject P809BundleRequest(
        JsonArray pins,
        string? ownerKind = null,
        string? ownerId = null)
        => new()
        {
            ["ownerKind"] = ownerKind ?? _p809Label.OwnerKind,
            ["ownerId"] = ownerId ?? _p809Label.OwnerId,
            ["dependencyPins"] = pins.DeepClone()
        };

    private Task<ApiHarnessResponse> ReadP809EmptyBundleAsync(CancellationToken ct)
        => _api.GetAsync(
            $"{P809BundleRoute}?ownerKind={_p809Label.OwnerKind}" +
            $"&ownerId={_p809Label.OwnerId}",
            Actor("system_admin").Token,
            ct: ct);

    private Task<ApiHarnessResponse> ReadP809BundleAsync(
        JsonArray pins,
        CancellationToken ct)
        => _api.PostAsync(
            $"{P809BundleRoute}/readback",
            P809BundleRequest(pins),
            Actor("system_admin").Token,
            ct: ct);

    private Task<ApiHarnessResponse> ValidateP809BundleAsync(
        string commandId,
        string expectedBundleHash,
        JsonArray pins,
        CancellationToken ct,
        string? ownerKind = null,
        string? ownerId = null)
        => _api.PostAsync(
            $"{P809BundleRoute}/validate",
            new JsonObject
            {
                ["commandId"] = commandId,
                ["expectedBundleHash"] = expectedBundleHash,
                ["bundle"] = P809BundleRequest(pins, ownerKind, ownerId)
            },
            Actor("system_admin").Token,
            ct: ct);

    private async Task<P809BundleApiIdentity> RequireP809BundleAsync(
        string caseId,
        ApiHarnessResponse response,
        int expectedPinCount,
        JsonArray requestPins,
        CancellationToken ct)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"{caseId} bundle readback");
        var root = ApiHarnessClient.RequiredObject(response.Json, $"{caseId} bundle response");
        HarnessAssert.Equal("P8-BUNDLE-1", RequiredString(root, "schemaVersion"),
            $"{caseId} schemaVersion drifted");
        HarnessAssert.Equal(_p809Label.OwnerKind, RequiredString(root, "ownerKind"),
            $"{caseId} ownerKind drifted");
        HarnessAssert.Equal(_p809Label.OwnerId, RequiredString(root, "ownerId"),
            $"{caseId} ownerId drifted");
        var isEmpty = RequiredBool(root, "isEmpty");
        HarnessAssert.Equal(expectedPinCount == 0, isEmpty,
            $"{caseId} empty flag differs from pin count");
        var pins = root["pins"] as JsonArray
                   ?? throw new InvalidOperationException($"{caseId} response lacks pins array.");
        HarnessAssert.Equal(expectedPinCount, pins.Count,
            $"{caseId} pin count drifted");
        if (expectedPinCount > 0)
        {
            var kinds = pins.OfType<JsonObject>()
                .Select(pin => RequiredString(pin, "kind"))
                .ToArray();
            HarnessAssert.True(kinds.SequenceEqual(P809DependencyKinds, StringComparer.Ordinal),
                $"{caseId} dependency kind order drifted: {string.Join(',', kinds)}");
        }

        var eligibility = root["eligibility"] as JsonObject
                          ?? throw new InvalidOperationException($"{caseId} lacks eligibility.");
        var identity = new P809BundleApiIdentity(
            isEmpty,
            pins,
            RequiredString(eligibility, "configuration"),
            RequiredString(eligibility, "futureResult"),
            RequiredString(eligibility, "executor"),
            RequiredString(eligibility, "targetPhase"),
            RequiredString(root, "freshness"),
            RequiredString(root, "canonicalJson"),
            RequiredString(root, "bundleHash"),
            root);
        RequireP809LowerSha(identity.BundleHash, $"{caseId}.bundleHash");
        var parsedCanonical = JsonNode.Parse(identity.CanonicalJson)
                              ?? throw new InvalidOperationException($"{caseId} canonicalJson is null.");
        HarnessAssert.Equal(identity.CanonicalJson, Canonicalize(parsedCanonical),
            $"{caseId} canonicalJson is not recursive ordinal canonical JSON");
        var recomputedHash = Sha256(Encoding.UTF8.GetBytes(identity.CanonicalJson));
        HarnessAssert.Equal(identity.BundleHash, recomputedHash,
            $"{caseId} bundle hash differs from independent UTF-8 SHA-256");
        var ownerDocumentSetHash = expectedPinCount == 0
            ? Sha256(Array.Empty<byte>())
            : await CaptureP809OwnerDocumentSetSha256Async(requestPins, ct);
        var directMongo = await RebuildP809BundleFromMongoAsync(requestPins, ct);
        HarnessAssert.Equal(
            identity.CanonicalJson,
            directMongo.CanonicalJson,
            $"{caseId} API canonical JSON differs from direct Mongo reconstruction");
        HarnessAssert.Equal(
            identity.BundleHash,
            directMongo.BundleHash,
            $"{caseId} API bundle hash differs from direct Mongo reconstruction");
        _p809BundleHashes.Add(new P809BundleHashEvidence(
            caseId,
            identity.BundleHash,
            recomputedHash,
            pins.OfType<JsonObject>().Select(pin => RequiredString(pin, "kind")).ToArray(),
            pins.Count,
            ownerDocumentSetHash,
            directMongo.CanonicalJson,
            directMongo.BundleHash,
            true,
            true));
        return identity;
    }

    private async Task<string> CaptureP809OwnerDocumentSetSha256Async(
        JsonArray requestPins,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pin in requestPins.OfType<JsonObject>()
                     .OrderBy(pin => RequiredString(pin, "kind"), StringComparer.Ordinal))
        {
            var kind = RequiredString(pin, "kind");
            var collection = kind switch
            {
                "LABEL" => LabelsCollection,
                "FIELD" or "TABLE" => DynamicFormsCollection,
                "BASIC" => BasicConfigsCollection,
                "ADVANCED" => AdvancedConfigsCollection,
                "DIFF" => DiffConfigsCollection,
                "FLOW_CONTRIBUTION" => FlowVersionsCollection,
                "READINESS" => P808JobsCollection,
                _ => throw new InvalidOperationException($"Unknown P8-09 owner kind {kind}.")
            };
            var identity = kind switch
            {
                "LABEL" or "FIELD" or "TABLE" => RequiredString(pin, "ownerId"),
                "BASIC" => RequiredString(pin, "configId"),
                _ => RequiredString(pin, "versionId")
            };
            var document = await _database.GetCollection<BsonDocument>(collection)
                .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(identity)))
                .SingleAsync(ct);
            var kindBytes = Encoding.UTF8.GetBytes(kind);
            var bytes = document.ToBson();
            hash.AppendData(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(kindBytes.Length)));
            hash.AppendData(kindBytes);
            hash.AppendData(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length)));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void RequireP809Stale(
        ApiHarnessResponse response,
        string expectedReason,
        string context,
        string? expectedKind = null)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, context);
        HarnessAssert.Equal(
            "STAT_CONFIG_CAS_CONFLICT",
            RequiredP809RecursiveString(response.Json, "errorCode", "code"),
            $"{context} error code drifted");
        HarnessAssert.Equal(expectedReason,
            RequiredP809RecursiveString(response.Json, "reason"),
            $"{context} reason drifted");
        HarnessAssert.Equal("STALE",
            RequiredP809RecursiveString(response.Json, "freshness"),
            $"{context} freshness drifted");
        if (expectedKind is not null)
        {
            HarnessAssert.Equal(expectedKind,
                RequiredP809RecursiveString(response.Json, "kind"),
                $"{context} kind drifted");
        }
        var autoUpgrade = FindP809Property(response.Json, "autoUpgrade");
        HarnessAssert.True(
            autoUpgrade is JsonValue value &&
            value.TryGetValue<bool>(out var enabled) &&
            !enabled,
            $"{context} did not explicitly reject implicit upgrade");
    }

    private static void RequireP809SchemaRejected(
        ApiHarnessResponse response,
        string expectedPath,
        string expectedReason,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, context);
        HarnessAssert.Equal(
            "STAT_CONFIG_SCHEMA_INVALID",
            RequiredP809RecursiveString(response.Json, "errorCode", "code"),
            $"{context} error code drifted");
        HarnessAssert.Equal(expectedPath,
            RequiredP809RecursiveString(response.Json, "path"),
            $"{context} path drifted");
        HarnessAssert.Equal(expectedReason,
            RequiredP809RecursiveString(response.Json, "reason"),
            $"{context} reason drifted");
    }

    private static JsonNode? FindP809Property(JsonNode? node, string property)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(property, out var value))
                return value;
            foreach (var pair in obj)
            {
                var nested = FindP809Property(pair.Value, property);
                if (nested is not null)
                    return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindP809Property(item, property);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static void RequireP809LowerSha(string value, string context)
        => HarnessAssert.True(
            value.Length == 64 &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{context} is not lowercase SHA-256");
}

internal sealed record P809BundleApiIdentity(
    bool IsEmpty,
    JsonArray Pins,
    string ConfigurationEligibility,
    string FutureResultEligibility,
    string ExecutorEligibility,
    string TargetPhase,
    string Freshness,
    string CanonicalJson,
    string BundleHash,
    JsonObject Raw);
