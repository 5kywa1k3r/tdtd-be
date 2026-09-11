using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P8DiffConfigIdentity> ReadDiffConfigAsync(
        P8Actor actor,
        P8DiffFixture fixture,
        CancellationToken ct,
        bool requirePersisted)
    {
        var response = await _api.GetAsync(
            DiffConfigRoute(fixture),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Diff config GET {fixture.Key}");
        var identity = ParseDiffIdentity(response.Json, requireReceipt: false);
        RequireDiffIdentityContract(identity, fixture);
        await RequireDirectDiffIdentityAsync(
            identity,
            fixture,
            commandId: null,
            expectedCommandKind: null,
            actorId: null,
            requirePersisted,
            ct);
        return identity;
    }

    private async Task<(ApiHarnessResponse Response, P8DiffConfigIdentity Identity)>
        PutDiffConfigAsync(
            P8Actor actor,
            P8DiffFixture fixture,
            string commandId,
            JsonObject payload,
            CancellationToken ct,
            long? expectedRevision = null,
            string? expectedConfigHash = null)
    {
        var current = await ReadDiffConfigAsync(
            actor,
            fixture,
            ct,
            requirePersisted: false);
        if (expectedRevision.HasValue)
        {
            HarnessAssert.Equal(expectedRevision.Value, current.Revision,
                "Caller-provided Diff CAS revision differs from current GET");
        }
        if (expectedConfigHash is not null)
        {
            HarnessAssert.Equal(expectedConfigHash, current.ConfigHash,
                "Caller-provided Diff CAS hash differs from current GET");
        }
        var response = await _api.PutAsync(
            DiffConfigRoute(fixture),
            Envelope(
                commandId,
                expectedRevision ?? current.Revision,
                expectedConfigHash ?? current.ConfigHash,
                payload),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Diff config PUT {commandId}");
        var identity = ParseDiffIdentity(response.Json, requireReceipt: true);
        RequireDiffIdentityContract(identity, fixture);
        await RequireDirectDiffIdentityAsync(
            identity,
            fixture,
            commandId,
            "UPSERT_DIFF_CONFIG",
            actor.Id,
            requirePersisted: true,
            ct);
        var readback = await ReadDiffConfigAsync(
            actor,
            fixture,
            ct,
            requirePersisted: true);
        HarnessAssert.Equal(
            CanonicalDiffReadback(identity.Raw),
            CanonicalDiffReadback(readback.Raw),
            "Diff PUT response and stable current GET differ");
        return (response, identity);
    }

    private async Task<(ApiHarnessResponse Response, P8DiffConfigIdentity Identity)>
        PostDiffActionAsync(
            P8Actor actor,
            P8DiffFixture fixture,
            string action,
            string commandId,
            P8DiffConfigIdentity current,
            CancellationToken ct)
    {
        var response = await _api.PostAsync(
            $"{DiffConfigRoute(fixture)}/{action}",
            Envelope(
                commandId,
                current.Revision,
                current.ConfigHash,
                new JsonObject()),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Diff config {action} {commandId}");
        var identity = ParseDiffIdentity(response.Json, requireReceipt: true);
        RequireDiffIdentityContract(identity, fixture);
        var commandKind = action switch
        {
            "lock" => "LOCK_DIFF_CONFIG",
            "next-draft" => "CREATE_DIFF_DRAFT",
            _ => throw new InvalidOperationException(
                $"Unsupported P8 Diff action {action}")
        };
        await RequireDirectDiffIdentityAsync(
            identity,
            fixture,
            commandId,
            commandKind,
            actor.Id,
            requirePersisted: true,
            ct);
        return (response, identity);
    }

    private async Task<ApiHarnessResponse> RequireZeroWriteDiffRejectionAsync(
        string probeId,
        Func<Task<ApiHarnessResponse>> request,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath,
        string? expectedReason,
        CancellationToken ct,
        string? expectedTargetPhase = null)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var response = await request();
        ExpectDiffFailure(
            response,
            expectedStatus,
            expectedCode,
            expectedPath,
            expectedReason,
            expectedTargetPhase);
        var after = await CaptureDatabaseSnapshotAsync(ct);
        VerifyCollectionContract(
            probeId,
            BuildDeltas(before, after),
            Array.Empty<string>(),
            Array.Empty<string>());
        return response;
    }

    private static void ExpectDiffFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath = null,
        string? expectedReason = null,
        string? expectedTargetPhase = null)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            expectedStatus,
            "expected P8 Diff rejection");
        var actualCode = ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "errorCode")
                         ?? ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "code");
        HarnessAssert.Equal(expectedCode, actualCode,
            "P8 Diff error code mismatch");
        if (expectedPath is not null)
        {
            HarnessAssert.Equal(expectedPath,
                ApiHarnessClient.FindStringRecursive(response.Json, "path"),
                "P8 Diff error path mismatch");
        }
        if (expectedReason is not null)
        {
            HarnessAssert.Equal(expectedReason,
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                "P8 Diff error reason mismatch");
        }
        if (expectedTargetPhase is not null)
        {
            HarnessAssert.Equal(expectedTargetPhase,
                ApiHarnessClient.FindStringRecursive(response.Json, "targetPhase"),
                "P8 Diff target phase mismatch");
        }
    }

    private static P8DiffConfigIdentity ParseDiffIdentity(
        JsonNode? node,
        bool requireReceipt)
    {
        var root = ApiHarnessClient.RequiredObject(
            node,
            "P8 Diff config response");
        var identityNode = root["identity"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "P8 Diff response lacks identity");
        var payload = root["payload"] as JsonObject
                      ?? throw new InvalidOperationException(
                          "P8 Diff response lacks payload");
        var pins = identityNode["dependencyPins"] as JsonArray
                   ?? throw new InvalidOperationException(
                       "P8 Diff identity lacks dependencyPins");
        var permissions = root["permissions"] as JsonObject
                          ?? throw new InvalidOperationException(
                              "P8 Diff response lacks permissions");
        var receiptId = OptionalString(root, "receiptId")
                        ?? OptionalString(root, "commandReceiptId");
        if (requireReceipt && string.IsNullOrWhiteSpace(receiptId))
        {
            throw new InvalidOperationException(
                "Successful P8 Diff mutation lacks receiptId");
        }
        return new P8DiffConfigIdentity(
            RequiredString(identityNode, "ownerKind"),
            RequiredString(identityNode, "ownerId"),
            RequiredString(identityNode, "configId"),
            RequiredString(identityNode, "versionId"),
            RequiredInt(identityNode, "versionNo"),
            RequiredLong(identityNode, "revision"),
            RequiredString(identityNode, "status"),
            RequiredString(identityNode, "configHash"),
            pins.Select(item => item?.GetValue<string>() ?? string.Empty)
                .ToArray(),
            RequiredString(root, "runtimeEligibility"),
            RequiredBool(root, "isVirtualEmpty"),
            payload,
            permissions,
            OptionalString(root, "previousVersionId"),
            root["versions"] as JsonArray ?? new JsonArray(),
            receiptId,
            root);
    }

    private static void RequireDiffIdentityContract(
        P8DiffConfigIdentity identity,
        P8DiffFixture fixture)
    {
        HarnessAssert.Equal("DIFF", identity.OwnerKind,
            "Diff ownerKind mismatch");
        HarnessAssert.Equal(
            $"{fixture.Assignment.Id}:{fixture.DynamicFormTemplateId}",
            identity.OwnerId,
            "Diff ownerId does not bind assignment and template");
        HarnessAssert.True(ObjectId.TryParse(identity.ConfigId, out _),
            "Diff configId is not an ObjectId");
        HarnessAssert.True(ObjectId.TryParse(identity.VersionId, out _),
            "Diff versionId is not an ObjectId");
        if (identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(
                Sha256(Encoding.UTF8.GetBytes(
                    $"DIFF\0{identity.OwnerId}\0CONFIG"))[..24],
                identity.ConfigId,
                "Virtual Diff configId is not deterministic");
            HarnessAssert.Equal(
                Sha256(Encoding.UTF8.GetBytes(
                    $"DIFF\0{identity.OwnerId}\0VERSION:1"))[..24],
                identity.VersionId,
                "Virtual Diff versionId is not deterministic");
            HarnessAssert.Equal(1, identity.VersionNo,
                "Virtual Diff versionNo mismatch");
            HarnessAssert.Equal(0L, identity.Revision,
                "Virtual Diff revision mismatch");
            HarnessAssert.Equal("DRAFT", identity.Status,
                "Virtual Diff status mismatch");
            HarnessAssert.Equal(EmptyConfigHash, identity.ConfigHash,
                "Virtual Diff configHash mismatch");
            HarnessAssert.Equal(null, identity.PreviousVersionId,
                "Virtual Diff previousVersionId is non-null");
            HarnessAssert.Equal(0, identity.Versions.Count,
                "Virtual Diff unexpectedly exposes versions");
        }
        RequireLowerSha256(identity.ConfigHash, "Diff configHash");
        HarnessAssert.Equal("BLOCKED_UNTIL_P9", identity.RuntimeEligibility,
            "Diff runtime eligibility crossed the P9 barrier");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                identity.DependencyPins
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Diff dependency pins are not canonical distinct ordinal");
        if (!identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(1,
                identity.DependencyPins.Count(pin => pin.StartsWith(
                    "DYNAMIC_FORM_SCHEMA:",
                    StringComparison.Ordinal)),
                "Diff does not pin exactly one published schema");
            HarnessAssert.Equal(1,
                identity.DependencyPins.Count(pin => pin.StartsWith(
                    "DYNAMIC_FORM_STAT_CONFIG:",
                    StringComparison.Ordinal)),
                "Diff does not pin exactly one statistic config");
        }
        HarnessAssert.Equal(true,
            RequiredBool(identity.Permissions, "canReadConfig"),
            "Diff config read permission mismatch");
        HarnessAssert.Equal(false,
            RequiredBool(identity.Permissions, "canViewResult"),
            "Diff result permission crossed P9 barrier");
        HarnessAssert.Equal(false,
            RequiredBool(identity.Permissions, "canReadDiagnostics"),
            "Diff diagnostics permission crossed P9 barrier");
    }

    private async Task RequireDirectDiffIdentityAsync(
        P8DiffConfigIdentity identity,
        P8DiffFixture fixture,
        string? commandId,
        string? expectedCommandKind,
        string? actorId,
        bool requirePersisted,
        CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(
                         "assignmentId",
                         ObjectId.Parse(fixture.Assignment.Id)) &
                     Builders<BsonDocument>.Filter.Eq(
                         "dynamicFormTemplateId",
                         ObjectId.Parse(fixture.DynamicFormTemplateId)) &
                     Builders<BsonDocument>.Filter.Ne("isDeleted", true);
        var documents = await _database
            .GetCollection<BsonDocument>(DiffConfigsCollection)
            .Find(filter)
            .ToListAsync(ct);
        if (identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(0, documents.Count,
                "Virtual Diff config unexpectedly persisted");
            return;
        }
        if (!requirePersisted)
            return;
        HarnessAssert.Equal(identity.Versions.Count, documents.Count,
            "Diff row-per-version cardinality differs from API versions");
        HarnessAssert.True(documents.All(document => string.Equals(
                identity.ConfigId,
                BsonString(document, "configId"),
                StringComparison.Ordinal)),
            "Diff lineage rows do not share stable configId");
        var owner = documents.Single(document => string.Equals(
            identity.VersionId,
            BsonString(document, "_id"),
            StringComparison.Ordinal));
        HarnessAssert.Equal(identity.ConfigId,
            BsonString(owner, "configId"),
            "Direct Diff shared configId mismatch");
        HarnessAssert.Equal(identity.VersionId, BsonString(owner, "_id"),
            "Direct Diff row _id is not versionId");
        HarnessAssert.Equal(fixture.Assignment.WorkId,
            BsonString(owner, "workId"),
            "Direct Diff workId mismatch");
        HarnessAssert.Equal(fixture.Assignment.Id,
            BsonString(owner, "assignmentId"),
            "Direct Diff assignmentId mismatch");
        HarnessAssert.Equal(fixture.DynamicFormTemplateId,
            BsonString(owner, "dynamicFormTemplateId"),
            "Direct Diff templateId mismatch");
        HarnessAssert.Equal(identity.PreviousVersionId,
            BsonString(owner, "previousVersionId"),
            "Direct Diff previousVersionId mismatch");
        HarnessAssert.Equal(identity.VersionNo,
            BsonInt(owner, "versionNo"),
            "Direct Diff versionNo mismatch");
        HarnessAssert.Equal(identity.Revision,
            BsonLong(owner, "revision"),
            "Direct Diff revision mismatch");
        HarnessAssert.Equal(identity.Status,
            BsonString(owner, "status"),
            "Direct Diff status mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            BsonString(owner, "configHash"),
            "Direct Diff configHash mismatch");
        HarnessAssert.Equal(true, BsonBool(owner, "isActive"),
            "Direct Diff owner is inactive");
        HarnessAssert.Equal(false, BsonBool(owner, "isDeleted"),
            "Direct Diff owner is deleted");
        var payloadJson = BsonString(owner, "configJson")
                          ?? throw new InvalidOperationException(
                              "Direct Diff configJson is absent");
        HarnessAssert.Equal(
            Canonicalize(identity.Payload),
            Canonicalize(JsonNode.Parse(payloadJson)
                         ?? throw new InvalidOperationException(
                             "Direct Diff configJson is null")),
            "Direct Diff payload differs from API readback");
        var pins = owner.GetValue("dependencyPins", new BsonArray());
        HarnessAssert.True(pins.IsBsonArray,
            "Direct Diff dependencyPins are malformed");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                pins.AsBsonArray.Select(value => value.AsString),
                StringComparer.Ordinal),
            "Direct Diff dependency pins differ from API");
        var recomputed = Sha256(Encoding.UTF8.GetBytes(Canonicalize(
            new JsonObject
            {
                ["ownerId"] = identity.OwnerId,
                ["payload"] = JsonNode.Parse(payloadJson),
                ["dependencyPins"] = new JsonArray(
                    identity.DependencyPins.Select(value =>
                        JsonValue.Create(value)).ToArray())
            })));
        HarnessAssert.Equal(identity.ConfigHash, recomputed,
            "Independent Diff configHash recomputation mismatch");
        HarnessAssert.True(documents
                .OrderBy(document => BsonInt(document, "versionNo"))
                .Select(document => BsonInt(document, "versionNo"))
                .SequenceEqual(Enumerable.Range(1, documents.Count)
                    .Select(value => (int?)value)),
            "Direct Diff row-per-version sequence is not contiguous");
        if (commandId is null)
            return;
        HarnessAssert.True(!string.IsNullOrWhiteSpace(actorId),
            "Diff receipt assertion lacks actorId");
        HarnessAssert.True(!string.IsNullOrWhiteSpace(expectedCommandKind),
            "Diff receipt assertion lacks commandKind");
        var expectedReceiptId = Sha256(Encoding.UTF8.GetBytes(
            $"DIFF\0{identity.OwnerId}\0{commandId}"));
        HarnessAssert.Equal(expectedReceiptId, identity.ReceiptId,
            "Diff receiptId is not deterministic");
        var receipt = await _database.GetCollection<BsonDocument>(
                ReceiptsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                      "ownerId",
                      identity.OwnerId) &
                  Builders<BsonDocument>.Filter.Eq(
                      "commandId",
                      commandId))
            .SingleAsync(ct);
        HarnessAssert.Equal(expectedReceiptId, BsonString(receipt, "_id"),
            "Direct Diff receipt _id mismatch");
        HarnessAssert.Equal("DIFF", BsonString(receipt, "ownerKind"),
            "Direct Diff receipt ownerKind mismatch");
        HarnessAssert.Equal(expectedCommandKind,
            BsonString(receipt, "commandKind"),
            "Direct Diff receipt commandKind mismatch");
        HarnessAssert.Equal(actorId, BsonString(receipt, "actorUserId"),
            "Direct Diff receipt actor mismatch");
        HarnessAssert.Equal(identity.ConfigId,
            BsonString(receipt, "resultConfigId"),
            "Direct Diff receipt configId mismatch");
        HarnessAssert.Equal(identity.VersionId,
            BsonString(receipt, "resultVersionId"),
            "Direct Diff receipt versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo,
            BsonInt(receipt, "resultVersionNo"),
            "Direct Diff receipt versionNo mismatch");
        HarnessAssert.Equal(identity.Revision,
            BsonLong(receipt, "resultRevision"),
            "Direct Diff receipt revision mismatch");
        HarnessAssert.Equal(identity.Status,
            BsonString(receipt, "resultStatus"),
            "Direct Diff receipt status mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            BsonString(receipt, "resultConfigHash"),
            "Direct Diff receipt hash mismatch");
    }

    private async Task<JsonArray> ListDiffVersionsAsync(
        P8Actor actor,
        P8DiffFixture fixture,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"{DiffConfigRoute(fixture)}/versions",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P8 Diff versions GET");
        var root = ApiHarnessClient.RequiredObject(
            response.Json,
            "P8 Diff versions response");
        return root["items"] as JsonArray
               ?? throw new InvalidOperationException(
                   "P8 Diff versions response lacks items");
    }

    private async Task<P8DiffConfigIdentity> ReadDiffVersionAsync(
        P8Actor actor,
        P8DiffFixture fixture,
        int versionNo,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"{DiffConfigRoute(fixture)}/versions/{versionNo}",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Diff version {versionNo} GET");
        var identity = ParseDiffIdentity(response.Json, requireReceipt: false);
        RequireDiffIdentityContract(identity, fixture);
        return identity;
    }

    private static string CanonicalDiffReadback(JsonObject root)
    {
        var clone = (JsonObject)root.DeepClone();
        clone.Remove("receiptId");
        clone.Remove("commandReceiptId");
        return Canonicalize(clone);
    }
}

internal sealed record P8DiffConfigIdentity(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    string RuntimeEligibility,
    bool IsVirtualEmpty,
    JsonObject Payload,
    JsonObject Permissions,
    string? PreviousVersionId,
    JsonArray Versions,
    string? ReceiptId,
    JsonObject Raw);
