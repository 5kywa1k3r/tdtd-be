using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static JsonObject LabelPayload(
        string code,
        string name,
        string scopeType,
        string? scopeId,
        string usage = "CLASSIFICATION",
        string dataType = "NUMBER",
        bool isActive = true,
        string valueSourceType = "NONE",
        JsonArray? valueOptions = null,
        string? valueSourceCatalogId = null,
        string? description = null,
        string? color = "#336699",
        string? groupCode = "p8")
        => new()
        {
            ["code"] = code,
            ["name"] = name,
            ["description"] = description,
            ["color"] = color,
            ["groupCode"] = groupCode,
            ["usage"] = usage,
            ["dataType"] = dataType,
            ["valueSourceType"] = valueSourceType,
            ["valueOptions"] = valueOptions ?? new JsonArray(),
            ["valueSourceCatalogId"] = valueSourceCatalogId,
            ["scopeType"] = scopeType,
            ["scopeId"] = scopeId,
            ["isActive"] = isActive
        };

    private static JsonObject Envelope(
        string commandId,
        long expectedRevision,
        string expectedConfigHash,
        JsonNode payload)
        => new()
        {
            ["commandId"] = commandId,
            ["expectedRevision"] = expectedRevision,
            ["expectedConfigHash"] = expectedConfigHash,
            ["payload"] = payload.DeepClone()
        };

    private async Task<(ApiHarnessResponse Response, P8ConfigIdentity Identity)> CreateLabelAsync(
        P8Actor actor,
        string commandId,
        JsonObject payload,
        CancellationToken ct)
    {
        var response = await _api.PostAsync(
            "api/labels/config",
            Envelope(commandId, 0, EmptyConfigHash, payload),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"create label {commandId}");
        var identity = ParseIdentity(response.Json, requireReceipt: true);
        RequireIdentityContract(identity);
        await RequireDirectIdentityAsync(identity, commandId, actor.Id, ct);
        return (response, identity);
    }

    private async Task<(ApiHarnessResponse Response, P8ConfigIdentity Identity)> UpdateLabelAsync(
        P8Actor actor,
        P8ConfigIdentity current,
        string commandId,
        JsonObject payload,
        CancellationToken ct)
    {
        var response = await _api.PutAsync(
            $"api/labels/{current.LabelId}/config",
            Envelope(commandId, current.Revision, current.ConfigHash, payload),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"update label {commandId}");
        var identity = ParseIdentity(response.Json, requireReceipt: true);
        RequireIdentityContract(identity);
        await RequireDirectIdentityAsync(identity, commandId, actor.Id, ct);
        return (response, identity);
    }

    private async Task<(ApiHarnessResponse Response, P8ConfigIdentity Identity)> DeleteLabelAsync(
        P8Actor actor,
        P8ConfigIdentity current,
        string commandId,
        CancellationToken ct)
    {
        var response = await _api.SendAsync(
            HttpMethod.Post,
            $"api/labels/{current.LabelId}/config/tombstone",
            Envelope(commandId, current.Revision, current.ConfigHash, new JsonObject()),
            actor.Token,
            headers: null,
            ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"delete label {commandId}");
        var identity = ParseIdentity(response.Json, requireReceipt: true);
        RequireIdentityContract(identity);
        await RequireDirectIdentityAsync(identity, commandId, actor.Id, ct);
        return (response, identity);
    }

    private async Task<P8ConfigIdentity> ReadLabelAsync(
        P8Actor actor,
        string labelId,
        CancellationToken ct)
    {
        var response = await _api.GetAsync($"api/labels/{labelId}/config", actor.Token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"read label {labelId}");
        var identity = ParseIdentity(response.Json, requireReceipt: false);
        RequireIdentityContract(identity);
        await RequireDirectIdentityAsync(identity, commandId: null, actorId: null, ct);
        return identity;
    }

    private static P8ConfigIdentity ParseIdentity(JsonNode? node, bool requireReceipt)
    {
        var root = ApiHarnessClient.RequiredObject(node, "P8 label config response");
        var label = root["label"] as JsonObject
                    ?? throw new InvalidOperationException("P8 response lacks flattened label owner row.");
        var pins = root["dependencyPins"] as JsonArray
                   ?? throw new InvalidOperationException("P8 response lacks dependencyPins array.");
        var versions = root["versions"] as JsonArray
                       ?? throw new InvalidOperationException("P8 response lacks version snapshot array.");
        var receiptId = OptionalString(root, "receiptId");
        if (requireReceipt && string.IsNullOrWhiteSpace(receiptId))
            throw new InvalidOperationException("Successful mutation lacks durable receiptId.");
        return new P8ConfigIdentity(
            RequiredString(root, "ownerKind"),
            RequiredString(root, "ownerId"),
            RequiredString(root, "configId"),
            RequiredString(root, "versionId"),
            RequiredInt(root, "versionNo"),
            RequiredLong(root, "revision"),
            RequiredString(root, "status"),
            RequiredString(root, "configHash"),
            pins.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray(),
            RequiredString(label, "id"),
            RequiredString(label, "code"),
            RequiredString(label, "scopeType"),
            OptionalString(label, "scopeId"),
            RequiredString(label, "usage"),
            RequiredString(label, "dataType"),
            RequiredBool(label, "isActive"),
            receiptId,
            versions,
            root);
    }

    private static void RequireIdentityContract(P8ConfigIdentity identity)
    {
        HarnessAssert.Equal("LABEL", identity.OwnerKind, "Label owner kind mismatch");
        HarnessAssert.Equal(identity.LabelId, identity.OwnerId, "ownerId must be the canonical label id");
        HarnessAssert.True(ObjectId.TryParse(identity.ConfigId, out _), "configId is not an ObjectId");
        HarnessAssert.True(ObjectId.TryParse(identity.VersionId, out _), "versionId is not an ObjectId");
        HarnessAssert.True(identity.VersionNo >= 1, "versionNo must be positive");
        HarnessAssert.True(identity.Revision >= 1, "revision must be positive");
        HarnessAssert.True(identity.ConfigHash.Length == 64, "configHash must be SHA-256 hex");
        HarnessAssert.True(
            identity.Versions.Count == identity.VersionNo,
            "version snapshot count must equal current versionNo");
        var currentVersion = identity.Versions
            .OfType<JsonObject>()
            .SingleOrDefault(version => RequiredInt(version, "versionNo") == identity.VersionNo)
            ?? throw new InvalidOperationException("Current version snapshot is missing.");
        HarnessAssert.Equal(identity.VersionId, RequiredString(currentVersion, "versionId"),
            "Current versionId differs from snapshot");
        HarnessAssert.Equal(identity.Revision, RequiredLong(currentVersion, "revision"),
            "Current revision differs from snapshot");
        HarnessAssert.Equal(identity.Status, RequiredString(currentVersion, "status"),
            "Current status differs from snapshot");
        HarnessAssert.Equal(identity.ConfigHash, RequiredString(currentVersion, "configHash"),
            "Current configHash differs from snapshot");
    }

    private async Task RequireDirectIdentityAsync(
        P8ConfigIdentity identity,
        string? commandId,
        string? actorId,
        CancellationToken ct)
    {
        var owner = await _database.GetCollection<BsonDocument>(LabelsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(identity.LabelId)))
            .SingleAsync(ct);
        HarnessAssert.Equal(identity.ConfigId, BsonString(owner, "configId"), "Direct Mongo configId mismatch");
        HarnessAssert.Equal(identity.VersionId, BsonString(owner, "versionId"), "Direct Mongo versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo, BsonInt(owner, "versionNo"), "Direct Mongo versionNo mismatch");
        HarnessAssert.Equal(identity.Revision, BsonLong(owner, "revision"), "Direct Mongo revision mismatch");
        HarnessAssert.Equal(identity.ConfigHash, BsonString(owner, "configHash"), "Direct Mongo configHash mismatch");
        HarnessAssert.Equal(identity.LabelCode, BsonString(owner, "code"), "Direct Mongo label code mismatch");
        var snapshots = owner.GetValue("versionSnapshots", new BsonArray()).AsBsonArray;
        HarnessAssert.Equal(identity.Versions.Count, snapshots.Count, "Direct Mongo version snapshot count mismatch");
        var recomputed = RecomputeConfigHash(identity);
        HarnessAssert.Equal(identity.ConfigHash, recomputed, "Independent canonical config hash mismatch");

        if (commandId is null)
            return;
        var receipt = await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ownerKind", "LABEL"),
                Builders<BsonDocument>.Filter.Eq("ownerId", identity.OwnerId),
                Builders<BsonDocument>.Filter.Eq("commandId", commandId)))
            .SingleAsync(ct);
        HarnessAssert.Equal(identity.ReceiptId, BsonString(receipt, "_id"), "receiptId differs from direct Mongo");
        HarnessAssert.Equal(identity.ConfigId, BsonString(receipt, "resultConfigId"), "Receipt configId mismatch");
        HarnessAssert.Equal(identity.VersionId, BsonString(receipt, "resultVersionId"), "Receipt versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo, BsonInt(receipt, "resultVersionNo"), "Receipt versionNo mismatch");
        HarnessAssert.Equal(identity.Revision, BsonLong(receipt, "resultRevision"), "Receipt revision mismatch");
        HarnessAssert.Equal(identity.Status, BsonString(receipt, "resultStatus"), "Receipt status mismatch");
        HarnessAssert.Equal(identity.ConfigHash, BsonString(receipt, "resultConfigHash"), "Receipt configHash mismatch");
        if (actorId is not null)
            HarnessAssert.Equal(actorId, BsonString(receipt, "actorUserId"), "Receipt actor mismatch");
        var requestHash = BsonString(receipt, "requestHash");
        var responseHash = BsonString(receipt, "responseHash");
        HarnessAssert.True(requestHash?.Length == 64, "Receipt requestHash is not SHA-256");
        HarnessAssert.True(responseHash?.Length == 64, "Receipt responseHash is not SHA-256");
    }

    private static string RecomputeConfigHash(P8ConfigIdentity identity)
    {
        var label = identity.Raw["label"] as JsonObject
                    ?? throw new InvalidOperationException("Cannot recompute label hash without label row.");
        var canonicalObject = new JsonObject
        {
            ["labelId"] = identity.LabelId,
            ["code"] = CloneOrNull(label["code"]),
            ["name"] = CloneOrNull(label["name"]),
            ["description"] = CloneOrNull(label["description"]),
            ["color"] = CloneOrNull(label["color"]),
            ["groupCode"] = CloneOrNull(label["groupCode"]),
            ["usage"] = CloneOrNull(label["usage"]),
            ["dataType"] = CloneOrNull(label["dataType"]),
            ["valueSourceType"] = CloneOrNull(label["valueSourceType"]),
            ["valueOptions"] = CloneOrNull(label["valueOptions"]),
            ["valueSourceCatalogId"] = CloneOrNull(label["valueSourceCatalogId"]),
            ["valueSourceCatalogCode"] = CloneOrNull(label["valueSourceCatalogCode"]),
            ["valueSourceCatalogName"] = CloneOrNull(label["valueSourceCatalogName"]),
            ["scopeType"] = CloneOrNull(label["scopeType"]),
            ["scopeId"] = CloneOrNull(label["scopeId"]),
            ["isActive"] = CloneOrNull(label["isActive"]),
            ["status"] = identity.Status,
            ["dependencyPins"] = new JsonArray(identity.DependencyPins.Select(pin => JsonValue.Create(pin)).ToArray())
        };
        var canonical = Canonicalize(canonicalObject);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static JsonNode? CloneOrNull(JsonNode? node)
        => node?.DeepClone();

    private static string Canonicalize(JsonNode node)
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
            WriteCanonical(writer, node);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is null)
        {
            writer.WriteNullValue();
            return;
        }
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Key);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (var item in array)
                WriteCanonical(writer, item);
            writer.WriteEndArray();
            return;
        }
        node.WriteTo(writer);
    }

    private static void ExpectFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        params string[] acceptedCodes)
    {
        ApiHarnessClient.ExpectStatus(response, expectedStatus, "expected P8 rejection");
        var actualCode = ApiHarnessClient.FindStringRecursive(response.Json, "errorCode")
                         ?? ApiHarnessClient.FindStringRecursive(response.Json, "code")
                         ?? ApiHarnessClient.FindStringRecursive(response.Json, "reason");
        HarnessAssert.True(
            actualCode is not null && acceptedCodes.Contains(actualCode, StringComparer.Ordinal),
            $"Expected one of [{string.Join(",", acceptedCodes)}], got {actualCode ?? "<missing>"}");
    }

    private static string RequiredString(JsonObject obj, string property)
        => obj[property] is JsonValue value &&
           value.TryGetValue<string>(out var text) &&
           !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException($"Required string {property} is missing: {obj.ToJsonString()}");

    private static string? OptionalString(JsonObject obj, string property)
    {
        if (obj[property] is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private static int RequiredInt(JsonObject obj, string property)
        => obj[property] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : throw new InvalidOperationException($"Required int {property} is missing: {obj.ToJsonString()}");

    private static long RequiredLong(JsonObject obj, string property)
    {
        if (obj[property] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
                return longValue;
            if (value.TryGetValue<int>(out var intValue))
                return intValue;
        }
        throw new InvalidOperationException($"Required long {property} is missing: {obj.ToJsonString()}");
    }

    private static bool RequiredBool(JsonObject obj, string property)
        => obj[property] is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw new InvalidOperationException($"Required bool {property} is missing: {obj.ToJsonString()}");

    private static string CanonicalResponse(ApiHarnessResponse response)
        => response.Json is null ? response.Body : Canonicalize(response.Json);

    private async Task<long> CountReceiptsAsync(string commandId, CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("commandId", commandId),
                cancellationToken: ct);

    private async Task<BsonDocument> RequireLabelDocumentAsync(string labelId, CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(LabelsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(labelId)))
            .SingleAsync(ct);

    private static void RequireVersionAdvanced(P8ConfigIdentity before, P8ConfigIdentity after)
    {
        HarnessAssert.Equal(before.ConfigId, after.ConfigId, "Config family changed across mutation");
        HarnessAssert.Equal(before.VersionNo + 1, after.VersionNo, "versionNo did not advance exactly once");
        HarnessAssert.Equal(before.Revision + 1, after.Revision, "revision did not advance exactly once");
        HarnessAssert.True(before.VersionId != after.VersionId, "versionId did not advance");
        HarnessAssert.True(before.ConfigHash != after.ConfigHash, "configHash did not advance");
        HarnessAssert.Equal(before.VersionId,
            OptionalString(after.Versions.OfType<JsonObject>().Single(version =>
                RequiredInt(version, "versionNo") == after.VersionNo), "previousVersionId"),
            "previousVersionId does not chain to prior version");
    }
}
