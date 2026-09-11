using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] FieldMutationWrites =
        [DynamicFormsCollection, ReceiptsCollection];

    private static JsonObject FieldStatistic(
        IEnumerable<string> aggregateOps,
        string bucketMode = "NONE",
        bool showInDetail = true,
        bool showInTree = false)
        => new()
        {
            ["aggregateOps"] = new JsonArray(
                aggregateOps.Select(value => JsonValue.Create(value)).ToArray()),
            ["bucketMode"] = bucketMode,
            ["showInDetail"] = showInDetail,
            ["showInTree"] = showInTree
        };

    private static JsonObject FieldPatch(
        string fieldId,
        IEnumerable<string> aggregateOps,
        string bucketMode = "NONE",
        bool showInDetail = true,
        bool showInTree = false,
        IEnumerable<string>? statisticLabelCodes = null)
        => new()
        {
            ["fieldId"] = fieldId,
            ["isStatistic"] = true,
            ["statisticLabelCodes"] = new JsonArray(
                (statisticLabelCodes ?? Array.Empty<string>())
                .Select(value => JsonValue.Create(value)).ToArray()),
            ["statistic"] = FieldStatistic(
                aggregateOps,
                bucketMode,
                showInDetail,
                showInTree)
        };

    private static JsonObject FieldPayload(params JsonObject[] fields)
        => new()
        {
            ["fields"] = new JsonArray(
                fields.Select(field => field.DeepClone()).ToArray())
        };

    private async Task<(ApiHarnessResponse Response, P8FieldConfigIdentity Identity)> PatchFieldConfigAsync(
        P8Actor actor,
        P8FormFixture fixture,
        string commandId,
        JsonObject payload,
        CancellationToken ct,
        long? expectedRevision = null,
        string? expectedConfigHash = null)
    {
        var current = await ReadFieldConfigAsync(
            actor, fixture.Id, ct, requirePersisted: false);
        if (expectedRevision.HasValue)
            HarnessAssert.Equal(expectedRevision.Value, current.Revision,
                "Caller-provided field CAS revision differs from pre-PATCH GET");
        if (expectedConfigHash is not null)
            HarnessAssert.Equal(expectedConfigHash, current.ConfigHash,
                "Caller-provided field CAS hash differs from pre-PATCH GET");
        var response = await _api.PatchAsync(
            $"api/dynamic-forms/{fixture.Id}/statistics",
            Envelope(
                commandId,
                expectedRevision ?? current.Revision,
                expectedConfigHash ?? current.ConfigHash,
                payload),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"field statistic PATCH {commandId}");
        var identity = ParseFieldIdentity(response.Json, requireReceipt: true);
        RequireFieldIdentityContract(identity, fixture.Id);
        await RequireDirectFieldIdentityAsync(identity, commandId, actor.Id, ct);

        var read = await ReadFieldConfigAsync(actor, fixture.Id, ct, requirePersisted: true);
        HarnessAssert.Equal(
            CanonicalFieldReadback(identity.Raw),
            CanonicalFieldReadback(read.Raw),
            "PATCH response and stable GET readback differ");
        HarnessAssert.Equal(current.TableSectionHash, identity.TableSectionHash,
            "Field PATCH changed the pre-existing tableSectionHash");
        HarnessAssert.Equal(
            Canonicalize(current.TableConfig),
            Canonicalize(identity.TableConfig),
            "Field PATCH changed canonical tableConfig content");
        return (response, identity);
    }

    private async Task<P8FieldConfigIdentity> ReadFieldConfigAsync(
        P8Actor actor,
        string formId,
        CancellationToken ct,
        bool requirePersisted = true)
    {
        var response = await _api.GetAsync(
            $"api/dynamic-forms/{formId}/statistics",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"field statistic GET {formId}");
        var identity = ParseFieldIdentity(response.Json, requireReceipt: false);
        RequireFieldIdentityContract(identity, formId);
        if (requirePersisted)
            await RequireDirectFieldIdentityAsync(identity, commandId: null, actorId: null, ct);
        return identity;
    }

    private static P8FieldConfigIdentity ParseFieldIdentity(JsonNode? node, bool requireReceipt)
    {
        var root = ApiHarnessClient.RequiredObject(node, "P8 field statistic config response");
        var pins = root["dependencyPins"] as JsonArray
                   ?? throw new InvalidOperationException("P8 field response lacks dependencyPins.");
        var permissions = root["permissions"] as JsonObject
                          ?? throw new InvalidOperationException("P8 field response lacks permissions.");
        var fields = root["fields"] as JsonArray
                     ?? throw new InvalidOperationException("P8 field response lacks fields.");
        var tableConfig = root["tableConfig"]
                          ?? throw new InvalidOperationException("P8 field response lacks tableConfig.");
        var versions = root["versions"] as JsonArray
                       ?? throw new InvalidOperationException("P8 field response lacks versions.");
        var receiptId = OptionalString(root, "receiptId");
        if (requireReceipt && string.IsNullOrWhiteSpace(receiptId))
            throw new InvalidOperationException("Successful field mutation lacks receiptId.");

        return new P8FieldConfigIdentity(
            RequiredString(root, "ownerKind"),
            RequiredString(root, "ownerId"),
            RequiredString(root, "configId"),
            RequiredString(root, "versionId"),
            RequiredInt(root, "versionNo"),
            RequiredLong(root, "revision"),
            RequiredString(root, "status"),
            RequiredString(root, "configHash"),
            pins.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray(),
            RequiredString(root, "fieldSectionHash"),
            RequiredString(root, "tableSectionHash"),
            permissions,
            fields,
            tableConfig,
            versions,
            receiptId,
            root);
    }

    private static void RequireFieldIdentityContract(
        P8FieldConfigIdentity identity,
        string expectedOwnerId)
    {
        HarnessAssert.Equal("DYNAMIC_FORM", identity.OwnerKind, "Field owner kind mismatch");
        HarnessAssert.Equal(expectedOwnerId, identity.OwnerId, "Field ownerId mismatch");
        HarnessAssert.True(ObjectId.TryParse(identity.ConfigId, out _), "Field configId is not an ObjectId");
        HarnessAssert.True(ObjectId.TryParse(identity.VersionId, out _), "Field versionId is not an ObjectId");
        HarnessAssert.True(identity.VersionNo >= 1, "Field config versionNo must be positive");
        HarnessAssert.True(identity.Revision >= 1, "Field config revision must be positive");
        RequireLowerSha256(identity.ConfigHash, "configHash");
        RequireLowerSha256(identity.FieldSectionHash, "fieldSectionHash");
        RequireLowerSha256(identity.TableSectionHash, "tableSectionHash");
        HarnessAssert.True(
            identity.DependencyPins.SequenceEqual(
                identity.DependencyPins.Distinct(StringComparer.Ordinal).OrderBy(pin => pin, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Field dependencyPins are not normalized distinct ordinal set");
        HarnessAssert.Equal(identity.VersionNo, identity.Versions.Count,
            "Field version snapshot count must equal versionNo");
        var current = identity.Versions.OfType<JsonObject>()
            .SingleOrDefault(version => RequiredInt(version, "versionNo") == identity.VersionNo)
            ?? throw new InvalidOperationException("Current field config version snapshot is missing.");
        HarnessAssert.Equal(identity.VersionId, RequiredString(current, "versionId"),
            "Field current versionId differs from snapshot");
        HarnessAssert.Equal(identity.Revision, RequiredLong(current, "revision"),
            "Field current revision differs from snapshot");
        HarnessAssert.Equal(identity.Status, RequiredString(current, "status"),
            "Field current status differs from snapshot");
        HarnessAssert.Equal(identity.ConfigHash, RequiredString(current, "configHash"),
            "Field current configHash differs from snapshot");
    }

    private async Task RequireDirectFieldIdentityAsync(
        P8FieldConfigIdentity identity,
        string? commandId,
        string? actorId,
        CancellationToken ct)
    {
        var owner = await RequireFormDocumentAsync(identity.OwnerId, ct);
        HarnessAssert.Equal(identity.ConfigId, BsonString(owner, "statisticConfigId"),
            "Direct form configId mismatch");
        HarnessAssert.Equal(identity.VersionId, BsonString(owner, "statisticConfigVersionId"),
            "Direct form versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo, BsonInt(owner, "statisticConfigVersionNo"),
            "Direct form versionNo mismatch");
        HarnessAssert.Equal(identity.Revision, BsonLong(owner, "statisticConfigRevision"),
            "Direct form revision mismatch");
        HarnessAssert.Equal(identity.ConfigHash, BsonString(owner, "statisticConfigHash"),
            "Direct form configHash mismatch");
        HarnessAssert.Equal(identity.Status, BsonString(owner, "statisticConfigStatus"),
            "Direct form config status mismatch");

        var sections = owner.GetValue("statisticConfigSections", new BsonDocument());
        HarnessAssert.True(sections.IsBsonDocument, "Direct form statisticConfigSections is absent");
        var fieldSectionJson = BsonString(sections.AsBsonDocument, "fieldSectionJson")
                               ?? throw new InvalidOperationException("Direct fieldSectionJson is absent.");
        var tableSectionJson = BsonString(sections.AsBsonDocument, "tableSectionJson")
                               ?? throw new InvalidOperationException("Direct tableSectionJson is absent.");
        HarnessAssert.Equal(identity.FieldSectionHash, HashCanonicalJson(fieldSectionJson),
            "Independent fieldSectionHash mismatch");
        HarnessAssert.Equal(identity.TableSectionHash, HashCanonicalJson(tableSectionJson),
            "Independent tableSectionHash mismatch");
        HarnessAssert.Equal(
            Canonicalize(identity.Fields),
            Canonicalize(ExtractSectionItems(fieldSectionJson, "fields")),
            "Direct field section differs from API fields");
        HarnessAssert.Equal(
            Canonicalize(identity.TableConfig),
            Canonicalize(JsonNode.Parse(tableSectionJson)
                         ?? throw new InvalidOperationException("Direct table section is null.")),
            "Direct table section differs from API tableConfig");

        var directPins = owner.GetValue("statisticConfigDependencyPins", new BsonArray());
        HarnessAssert.True(directPins.IsBsonArray, "Direct statisticConfigDependencyPins is malformed");
        HarnessAssert.True(
            identity.DependencyPins.SequenceEqual(
                directPins.AsBsonArray.Select(value => value.AsString),
                StringComparer.Ordinal),
            "Direct dependency pins differ from API");
        var snapshots = owner.GetValue("statisticConfigSnapshots", new BsonArray());
        HarnessAssert.True(snapshots.IsBsonArray, "Direct statisticConfigSnapshots is malformed");
        HarnessAssert.Equal(identity.Versions.Count, snapshots.AsBsonArray.Count,
            "Direct statistic config snapshot count mismatch");
        var currentSnapshot = snapshots.AsBsonArray
            .Select(value => value.AsBsonDocument)
            .Single(snapshot => BsonInt(snapshot, "versionNo") == identity.VersionNo);
        HarnessAssert.Equal(identity.VersionId, BsonString(currentSnapshot, "versionId"),
            "Direct current snapshot versionId mismatch");
        HarnessAssert.Equal(identity.Status, BsonString(currentSnapshot, "status"),
            "Direct current snapshot status mismatch");
        HarnessAssert.Equal(identity.ConfigHash, BsonString(currentSnapshot, "configHash"),
            "Direct current snapshot configHash mismatch");
        var recomputedConfigHash = Sha256(Encoding.UTF8.GetBytes(Canonicalize(new JsonObject
        {
            ["ownerKind"] = "DYNAMIC_FORM",
            ["ownerId"] = identity.OwnerId,
            ["fieldConfig"] = JsonNode.Parse(fieldSectionJson),
            ["tableConfig"] = JsonNode.Parse(tableSectionJson),
            ["dependencyPins"] = new JsonArray(
                identity.DependencyPins.Select(pin => JsonValue.Create(pin)).ToArray())
        })));
        HarnessAssert.Equal(identity.ConfigHash, recomputedConfigHash,
            "Independent Dynamic Form configHash mismatch");

        if (commandId is null)
            return;

        var receipt = await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ownerKind", "DYNAMIC_FORM"),
                Builders<BsonDocument>.Filter.Eq("ownerId", identity.OwnerId),
                Builders<BsonDocument>.Filter.Eq("commandId", commandId)))
            .SingleAsync(ct);
        var expectedReceiptId = Sha256(
            Encoding.UTF8.GetBytes($"DYNAMIC_FORM\0{identity.OwnerId}\0{commandId}"));
        HarnessAssert.Equal(expectedReceiptId, BsonString(receipt, "_id"),
            "Dynamic form receipt identity is not deterministic");
        HarnessAssert.Equal(identity.ReceiptId, expectedReceiptId,
            "API receiptId differs from deterministic direct receipt id");
        HarnessAssert.Equal("UPDATE_DYNAMIC_FORM_STATISTICS", BsonString(receipt, "commandKind"),
            "Dynamic form receipt commandKind mismatch");
        HarnessAssert.Equal(identity.ConfigId, BsonString(receipt, "resultConfigId"),
            "Dynamic form receipt configId mismatch");
        HarnessAssert.Equal(identity.VersionId, BsonString(receipt, "resultVersionId"),
            "Dynamic form receipt versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo, BsonInt(receipt, "resultVersionNo"),
            "Dynamic form receipt versionNo mismatch");
        HarnessAssert.Equal(identity.Revision, BsonLong(receipt, "resultRevision"),
            "Dynamic form receipt revision mismatch");
        HarnessAssert.Equal(identity.Status, BsonString(receipt, "resultStatus"),
            "Dynamic form receipt status mismatch");
        HarnessAssert.Equal(identity.ConfigHash, BsonString(receipt, "resultConfigHash"),
            "Dynamic form receipt configHash mismatch");
        HarnessAssert.Equal(actorId, BsonString(receipt, "actorUserId"),
            "Dynamic form receipt actor mismatch");
        RequireLowerSha256(BsonString(receipt, "requestHash") ?? string.Empty, "receipt.requestHash");
        var responseJson = BsonString(receipt, "responseJson")
                           ?? throw new InvalidOperationException("Receipt responseJson is absent.");
        HarnessAssert.Equal(
            BsonString(receipt, "responseHash"),
            Sha256(Encoding.UTF8.GetBytes(responseJson)),
            "Receipt responseHash does not hash exact responseJson");
        HarnessAssert.Equal(
            Canonicalize(identity.Raw),
            Canonicalize(JsonNode.Parse(responseJson)
                         ?? throw new InvalidOperationException("Receipt responseJson is null.")),
            "Receipt responseJson differs from API mutation response");
    }

    private async Task<ApiHarnessResponse> RequireZeroWriteFieldRejectionAsync(
        string probeId,
        Func<Task<ApiHarnessResponse>> request,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedPath,
        string expectedReason,
        CancellationToken ct)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var response = await request();
        ExpectFieldFailure(
            response,
            expectedStatus,
            expectedCode,
            expectedPath,
            expectedReason);
        var after = await CaptureDatabaseSnapshotAsync(ct);
        VerifyCollectionContract(
            probeId,
            BuildDeltas(before, after),
            Array.Empty<string>(),
            Array.Empty<string>());
        return response;
    }

    private static void ExpectFieldFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedPath,
        string expectedReason)
    {
        ApiHarnessClient.ExpectStatus(response, expectedStatus, "expected P8 field rejection");
        var actualCode = ApiHarnessClient.FindStringRecursive(response.Json, "errorCode")
                         ?? ApiHarnessClient.FindStringRecursive(response.Json, "code");
        var actualPath = ApiHarnessClient.FindStringRecursive(response.Json, "path");
        var actualReason = ApiHarnessClient.FindStringRecursive(response.Json, "reason");
        HarnessAssert.Equal(expectedCode, actualCode, "P8 field error code mismatch");
        HarnessAssert.Equal(expectedPath, actualPath, "P8 field error path mismatch");
        HarnessAssert.Equal(expectedReason, actualReason, "P8 field error reason mismatch");
    }

    private static void RequireFieldReadback(
        P8FieldConfigIdentity identity,
        string fieldId,
        string expectedType,
        IReadOnlyList<string> expectedOps,
        string expectedBucket,
        bool expectedShowInDetail,
        bool expectedShowInTree,
        IReadOnlyList<string>? expectedLabels = null,
        int? expectedSnapshotCount = null)
    {
        var field = identity.Fields.OfType<JsonObject>()
            .SingleOrDefault(item => string.Equals(
                RequiredString(item, "fieldId"),
                fieldId,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Field readback {fieldId} is missing.");
        HarnessAssert.Equal(expectedType, RequiredString(field, "fieldType"),
            $"Field type mismatch for {fieldId}");
        HarnessAssert.True(RequiredBool(field, "isStatistic"),
            $"isStatistic mismatch for {fieldId}");
        var labels = field["statisticLabelCodes"] as JsonArray
                     ?? throw new InvalidOperationException($"Label codes absent for {fieldId}.");
        HarnessAssert.True(
            (expectedLabels ?? Array.Empty<string>()).SequenceEqual(
                labels.Select(item => item?.GetValue<string>() ?? string.Empty),
                StringComparer.Ordinal),
            $"Statistic label codes mismatch for {fieldId}");
        var statistic = field["statistic"] as JsonObject
                        ?? throw new InvalidOperationException($"Typed statistic config absent for {fieldId}.");
        var ops = statistic["aggregateOps"] as JsonArray
                  ?? throw new InvalidOperationException($"aggregateOps absent for {fieldId}.");
        HarnessAssert.True(
            expectedOps.SequenceEqual(
                ops.Select(item => item?.GetValue<string>() ?? string.Empty),
                StringComparer.Ordinal),
            $"aggregateOps mismatch for {fieldId}");
        HarnessAssert.Equal(expectedBucket, RequiredString(statistic, "bucketMode"),
            $"bucketMode mismatch for {fieldId}");
        HarnessAssert.Equal(expectedShowInDetail, RequiredBool(statistic, "showInDetail"),
            $"showInDetail mismatch for {fieldId}");
        HarnessAssert.Equal(expectedShowInTree, RequiredBool(statistic, "showInTree"),
            $"showInTree mismatch for {fieldId}");
        var snapshots = field["labelSnapshots"] as JsonArray
                        ?? throw new InvalidOperationException($"labelSnapshots absent for {fieldId}.");
        if (expectedSnapshotCount.HasValue)
            HarnessAssert.Equal(expectedSnapshotCount.Value, snapshots.Count,
                $"labelSnapshots count mismatch for {fieldId}");
    }

    private async Task<BsonDocument> RequireFormDocumentAsync(string formId, CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(DynamicFormsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(formId)))
            .SingleAsync(ct);

    private async Task<long> CountFormReceiptsAsync(
        string formId,
        string commandId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ownerKind", "DYNAMIC_FORM"),
                    Builders<BsonDocument>.Filter.Eq("ownerId", formId),
                    Builders<BsonDocument>.Filter.Eq("commandId", commandId)),
                cancellationToken: ct);

    private static JsonArray ExtractSectionItems(string sectionJson, string property)
    {
        var parsed = JsonNode.Parse(sectionJson)
                     ?? throw new InvalidOperationException($"Stored {property} section is null.");
        if (parsed is JsonArray array)
            return array;
        if (parsed is JsonObject obj && obj[property] is JsonArray nested)
            return nested;
        throw new InvalidOperationException($"Stored section does not contain {property} array.");
    }

    private static string HashCanonicalJson(string json)
    {
        var node = JsonNode.Parse(json)
                   ?? throw new InvalidOperationException("Canonical section JSON is null.");
        return Sha256(Encoding.UTF8.GetBytes(Canonicalize(node)));
    }

    private static string CanonicalFieldReadback(JsonObject value)
    {
        var clone = (JsonObject)value.DeepClone();
        clone.Remove("receiptId");
        return Canonicalize(clone);
    }

    private static void RequireLowerSha256(string value, string name)
        => HarnessAssert.True(
            value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{name} is not lowercase SHA-256 hex");
}

internal sealed record P8FieldConfigIdentity(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    string FieldSectionHash,
    string TableSectionHash,
    JsonObject Permissions,
    JsonArray Fields,
    JsonNode TableConfig,
    JsonArray Versions,
    string? ReceiptId,
    JsonObject Raw);
