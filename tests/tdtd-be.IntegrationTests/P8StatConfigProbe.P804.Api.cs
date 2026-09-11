using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P8BasicConfigIdentity> ReadBasicConfigAsync(
        P8Actor actor,
        P8BasicFixture fixture,
        CancellationToken ct,
        bool requirePersisted)
    {
        var response = await _api.GetAsync(
            BasicConfigRoute(fixture),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Basic config GET {fixture.Key}");
        var identity = ParseBasicIdentity(response.Json, requireReceipt: false);
        RequireBasicIdentityContract(identity, fixture);
        await RequireDirectBasicIdentityAsync(
            identity,
            fixture,
            commandId: null,
            expectedCommandKind: null,
            actorId: null,
            requirePersisted,
            ct);
        return identity;
    }

    private async Task<(ApiHarnessResponse Response, P8BasicConfigIdentity Identity)>
        PutBasicConfigAsync(
            P8Actor actor,
            P8BasicFixture fixture,
            string commandId,
            JsonObject payload,
            CancellationToken ct,
            long? expectedRevision = null,
            string? expectedConfigHash = null)
    {
        var current = await ReadBasicConfigAsync(
            actor,
            fixture,
            ct,
            requirePersisted: false);
        if (expectedRevision.HasValue)
        {
            HarnessAssert.Equal(
                expectedRevision.Value,
                current.Revision,
                "Caller-provided Basic CAS revision differs from pre-PUT GET");
        }
        if (expectedConfigHash is not null)
        {
            HarnessAssert.Equal(
                expectedConfigHash,
                current.ConfigHash,
                "Caller-provided Basic CAS hash differs from pre-PUT GET");
        }

        var response = await _api.PutAsync(
            BasicConfigRoute(fixture),
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
            $"P8 Basic config PUT {commandId}");
        var identity = ParseBasicIdentity(response.Json, requireReceipt: true);
        RequireBasicIdentityContract(identity, fixture);
        await RequireDirectBasicIdentityAsync(
            identity,
            fixture,
            commandId,
            "UPSERT_BASIC_SUMMARY_CONFIG",
            actor.Id,
            requirePersisted: true,
            ct);
        var readback = await ReadBasicConfigAsync(
            actor,
            fixture,
            ct,
            requirePersisted: true);
        HarnessAssert.Equal(
            CanonicalBasicReadback(identity.Raw),
            CanonicalBasicReadback(readback.Raw),
            "Basic PUT response and stable current GET differ");
        return (response, identity);
    }

    private async Task<(ApiHarnessResponse Response, P8BasicConfigIdentity Identity)>
        PostBasicActionAsync(
            P8Actor actor,
            P8BasicFixture fixture,
            string action,
            string commandId,
            P8BasicConfigIdentity current,
            CancellationToken ct)
    {
        var response = await _api.PostAsync(
            $"{BasicConfigRoute(fixture)}/{action}",
            Envelope(
                commandId,
                current.Revision,
                current.ConfigHash,
                BasicActionPayload()),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Basic config {action} {commandId}");
        var identity = ParseBasicIdentity(response.Json, requireReceipt: true);
        RequireBasicIdentityContract(identity, fixture);
        await RequireDirectBasicIdentityAsync(
            identity,
            fixture,
            commandId,
            string.Equals(action, "lock", StringComparison.Ordinal)
                ? "LOCK_BASIC_SUMMARY_CONFIG"
                : "CREATE_BASIC_SUMMARY_DRAFT",
            actor.Id,
            requirePersisted: true,
            ct);
        return (response, identity);
    }

    private async Task<ApiHarnessResponse> RequireZeroWriteBasicRejectionAsync(
        string probeId,
        Func<Task<ApiHarnessResponse>> request,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath,
        string? expectedReason,
        CancellationToken ct)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var response = await request();
        ExpectBasicFailure(
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

    private static void ExpectBasicFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath,
        string? expectedReason)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            expectedStatus,
            "expected P8 Basic Summary rejection");
        var actualCode = ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "errorCode")
                         ?? ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "code");
        HarnessAssert.Equal(expectedCode, actualCode,
            "P8 Basic Summary error code mismatch");
        if (expectedPath is not null)
        {
            HarnessAssert.Equal(
                expectedPath,
                ApiHarnessClient.FindStringRecursive(response.Json, "path"),
                "P8 Basic Summary error path mismatch");
        }
        if (expectedReason is not null)
        {
            HarnessAssert.Equal(
                expectedReason,
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                "P8 Basic Summary error reason mismatch");
        }
    }

    private static P8BasicConfigIdentity ParseBasicIdentity(
        JsonNode? node,
        bool requireReceipt)
    {
        var root = ApiHarnessClient.RequiredObject(
            node,
            "P8 Basic Summary config response");
        var identityNode = root["identity"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "P8 Basic response lacks typed identity.");
        var payload = root["payload"] as JsonObject
                      ?? throw new InvalidOperationException(
                          "P8 Basic response lacks typed payload.");
        var pins = identityNode["dependencyPins"] as JsonArray
                   ?? throw new InvalidOperationException(
                       "P8 Basic response lacks dependencyPins.");
        var permissions = root["permissions"] as JsonObject
                          ?? throw new InvalidOperationException(
                              "P8 Basic response lacks permissions.");
        var meanContract = root["meanContract"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "P8 Basic response lacks meanContract.");
        var receiptId = OptionalString(root, "receiptId");
        if (requireReceipt && string.IsNullOrWhiteSpace(receiptId))
        {
            throw new InvalidOperationException(
                "Successful P8 Basic mutation lacks receiptId.");
        }
        return new P8BasicConfigIdentity(
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
            meanContract,
            OptionalString(root, "previousVersionId"),
            root["versions"] as JsonArray ?? new JsonArray(),
            receiptId,
            root);
    }

    private static void RequireBasicIdentityContract(
        P8BasicConfigIdentity identity,
        P8BasicFixture fixture)
    {
        HarnessAssert.Equal("BASIC_SUMMARY", identity.OwnerKind,
            "Basic ownerKind mismatch");
        HarnessAssert.Equal(
            $"{fixture.Assignment.Id}:{fixture.DynamicFormTemplateId}",
            identity.OwnerId,
            "Basic ownerId does not bind assignment and template");
        HarnessAssert.True(ObjectId.TryParse(identity.ConfigId, out _),
            "Basic configId is not a deterministic/persisted ObjectId");
        HarnessAssert.True(ObjectId.TryParse(identity.VersionId, out _),
            "Basic versionId is not a deterministic/persisted ObjectId");
        if (identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(
                Sha256(Encoding.UTF8.GetBytes(
                    $"BASIC_SUMMARY\0{identity.OwnerId}\0CONFIG"))[..24],
                identity.ConfigId,
                "Virtual Basic configId is not deterministic");
            HarnessAssert.Equal(
                Sha256(Encoding.UTF8.GetBytes(
                    $"BASIC_SUMMARY\0{identity.OwnerId}\0VERSION:1"))[..24],
                identity.VersionId,
                "Virtual Basic versionId is not deterministic");
            HarnessAssert.Equal(1, identity.VersionNo,
                "Virtual Basic versionNo mismatch");
            HarnessAssert.Equal(0L, identity.Revision,
                "Virtual Basic revision mismatch");
            HarnessAssert.Equal("DRAFT", identity.Status,
                "Virtual Basic status mismatch");
            HarnessAssert.Equal(EmptyConfigHash, identity.ConfigHash,
                "Virtual Basic configHash mismatch");
            HarnessAssert.Equal(null, identity.PreviousVersionId,
                "Virtual Basic previousVersionId is non-null");
            HarnessAssert.Equal(0, identity.Versions.Count,
                "Virtual Basic unexpectedly exposes version snapshots");
        }
        RequireLowerSha256(identity.ConfigHash, "Basic configHash");
        HarnessAssert.Equal("BLOCKED_UNTIL_P9", identity.RuntimeEligibility,
            "Basic runtime eligibility drifted from P9 barrier");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                identity.DependencyPins
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Basic dependency pins are not a canonical distinct ordinal set");
        var schemaPins = identity.DependencyPins
            .Where(value => value.StartsWith(
                "DYNAMIC_FORM_SCHEMA:",
                StringComparison.Ordinal))
            .ToArray();
        var statisticPins = identity.DependencyPins
            .Where(value => value.StartsWith(
                "DYNAMIC_FORM_STAT_CONFIG:",
                StringComparison.Ordinal))
            .ToArray();
        var labelPins = identity.DependencyPins
            .Where(value => value.StartsWith(
                "LABEL:",
                StringComparison.Ordinal))
            .ToArray();
        HarnessAssert.Equal(1, schemaPins.Length,
            "Basic dependency pins do not expose exactly one Dynamic Form schema identity");
        HarnessAssert.True(schemaPins[0].StartsWith(
                $"DYNAMIC_FORM_SCHEMA:{fixture.DynamicFormTemplateId}:1:",
                StringComparison.Ordinal),
            "Basic dependency pin differs from the assignment Dynamic Form schema identity");
        HarnessAssert.Equal(1, statisticPins.Length,
            "Basic dependency pins do not expose exactly one Dynamic Form statistic identity");
        HarnessAssert.True(statisticPins[0].StartsWith(
                $"DYNAMIC_FORM_STAT_CONFIG:{fixture.DynamicFormTemplateId}:",
                StringComparison.Ordinal),
            "Basic dependency pin differs from the assignment Dynamic Form statistic identity");
        var targets = identity.Payload["targets"] as JsonArray
                      ?? throw new InvalidOperationException(
                          "Basic payload lacks targets for dependency-pin validation.");
        var expectedLabelPins = targets
            .OfType<JsonObject>()
            .Where(target => string.Equals(
                RequiredString(target, "conceptKind"),
                "ROW_LABEL",
                StringComparison.Ordinal))
            .Select(target => RequiredString(target, "conceptKey"))
            .Distinct(StringComparer.Ordinal)
            .Count();
        HarnessAssert.Equal(expectedLabelPins, labelPins.Length,
            "Basic LABEL dependency pins do not match distinct ROW_LABEL targets");
        HarnessAssert.Equal(2 + labelPins.Length, identity.DependencyPins.Count,
            "Basic dependency pins contain an unexpected identity kind");
        HarnessAssert.Equal(true,
            RequiredBool(identity.Permissions, "canReadConfig"),
            "Basic admin read permission mismatch");
        HarnessAssert.Equal(true,
            RequiredBool(identity.Permissions, "canManageDraft"),
            "Basic admin manage permission mismatch");
        HarnessAssert.Equal(true,
            RequiredBool(identity.Permissions, "canLockVersion"),
            "Basic admin lock permission mismatch");
        HarnessAssert.Equal(false,
            RequiredBool(identity.Permissions, "canViewResult"),
            "Basic result permission crossed the P9 barrier");
        HarnessAssert.Equal(false,
            RequiredBool(identity.Permissions, "canReadDiagnostics"),
            "Basic diagnostics permission crossed the P9 barrier");
        HarnessAssert.Equal(
            "sum/numericValueCount",
            RequiredString(identity.MeanContract, "formula"),
            "Basic MEAN formula metadata drifted");
        HarnessAssert.True(
            RequiredBool(identity.MeanContract, "metadataOnly"),
            "Basic MEAN contract must remain metadata-only");
    }

    private async Task RequireDirectBasicIdentityAsync(
        P8BasicConfigIdentity identity,
        P8BasicFixture fixture,
        string? commandId,
        string? expectedCommandKind,
        string? actorId,
        bool requirePersisted,
        CancellationToken ct)
    {
        var configs = _database.GetCollection<BsonDocument>(
            BasicConfigsCollection);
        var filter = Builders<BsonDocument>.Filter.Eq(
                         "assignmentId",
                         ObjectId.Parse(fixture.Assignment.Id)) &
                     Builders<BsonDocument>.Filter.Eq(
                         "dynamicFormTemplateId",
                         ObjectId.Parse(fixture.DynamicFormTemplateId)) &
                     Builders<BsonDocument>.Filter.Ne("isDeleted", true);
        var documents = await configs.Find(filter).ToListAsync(ct);
        if (identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(0, documents.Count,
                "Virtual Basic config unexpectedly has a persisted owner");
            return;
        }
        if (!requirePersisted)
            return;
        HarnessAssert.Equal(1, documents.Count,
            "Basic config owner cardinality mismatch");
        var owner = documents.Single();
        HarnessAssert.Equal(identity.ConfigId, BsonString(owner, "_id"),
            "Direct Basic configId mismatch");
        HarnessAssert.Equal(fixture.Assignment.WorkId,
            BsonString(owner, "workId"),
            "Direct Basic workId mismatch");
        HarnessAssert.Equal(fixture.Assignment.Id,
            BsonString(owner, "assignmentId"),
            "Direct Basic assignmentId mismatch");
        HarnessAssert.Equal(fixture.DynamicFormTemplateId,
            BsonString(owner, "dynamicFormTemplateId"),
            "Direct Basic templateId mismatch");
        HarnessAssert.Equal(true, BsonBool(owner, "isActive"),
            "Direct Basic owner is inactive");
        HarnessAssert.Equal(false, BsonBool(owner, "isDeleted"),
            "Direct Basic owner is deleted");
        HarnessAssert.Equal(identity.VersionId, BsonString(owner, "versionId"),
            "Direct Basic versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo, BsonInt(owner, "versionNo"),
            "Direct Basic versionNo mismatch");
        HarnessAssert.Equal(identity.Revision, BsonLong(owner, "revision"),
            "Direct Basic revision mismatch");
        HarnessAssert.Equal(identity.Status, BsonString(owner, "status"),
            "Direct Basic status mismatch");
        HarnessAssert.Equal(identity.ConfigHash, BsonString(owner, "configHash"),
            "Direct Basic configHash mismatch");
        var payloadJson = BsonString(owner, "configJson")
                          ?? throw new InvalidOperationException(
                              "Direct Basic configJson is absent.");
        HarnessAssert.Equal(
            Canonicalize(identity.Payload),
            Canonicalize(JsonNode.Parse(payloadJson)
                         ?? throw new InvalidOperationException(
                             "Direct Basic configJson is null.")),
            "Direct Basic payload differs from typed API readback");
        var pins = owner.GetValue("dependencyPins", new BsonArray());
        HarnessAssert.True(pins.IsBsonArray,
            "Direct Basic dependencyPins are malformed");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                pins.AsBsonArray.Select(value => value.AsString),
                StringComparer.Ordinal),
            "Direct Basic dependency pins differ from API readback");
        var recomputedHash = Sha256(Encoding.UTF8.GetBytes(Canonicalize(
            new JsonObject
            {
                ["payload"] = JsonNode.Parse(payloadJson),
                ["dependencyPins"] = new JsonArray(
                    identity.DependencyPins.Select(value =>
                        JsonValue.Create(value)).ToArray())
            })));
        HarnessAssert.Equal(identity.ConfigHash, recomputedHash,
            "Independent Basic configHash recomputation mismatch");
        HarnessAssert.Equal("{}", BsonString(owner, "defaultMethodsJson"),
            "Basic owner legacy defaultMethodsJson drifted");
        var rules = JsonNode.Parse(BsonString(owner, "rulesJson") ?? "[]")
                    as JsonArray
                    ?? throw new InvalidOperationException(
                        "Basic owner legacy rulesJson is malformed.");
        var expectedFieldTargetCount =
            (identity.Payload["targets"] as JsonArray)?
            .OfType<JsonObject>()
            .Count(target => string.Equals(
                RequiredString(target, "conceptKind"),
                "FIELD",
                StringComparison.Ordinal)) ?? 0;
        HarnessAssert.Equal(expectedFieldTargetCount, rules.Count,
            "Basic legacy rules projection differs from FIELD target count");
        var versions = owner.GetValue("versions", new BsonArray());
        HarnessAssert.True(versions.IsBsonArray,
            "Direct Basic versions snapshot is malformed");
        HarnessAssert.Equal(identity.Versions.Count, versions.AsBsonArray.Count,
            "Direct Basic versions count differs from API readback");
        var currentVersion = versions.AsBsonArray
            .Select(value => value.AsBsonDocument)
            .Single(value => BsonInt(value, "versionNo") == identity.VersionNo);
        HarnessAssert.Equal(identity.VersionId,
            BsonString(currentVersion, "versionId"),
            "Direct Basic current versionId snapshot mismatch");
        HarnessAssert.Equal(identity.Revision,
            BsonLong(currentVersion, "revision"),
            "Direct Basic current revision snapshot mismatch");
        HarnessAssert.Equal(identity.Status,
            BsonString(currentVersion, "status"),
            "Direct Basic current status snapshot mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            BsonString(currentVersion, "configHash"),
            "Direct Basic current hash snapshot mismatch");
        if (commandId is null)
            return;
        HarnessAssert.True(!string.IsNullOrWhiteSpace(actorId),
            "Direct Basic receipt assertion lacks actorId");
        HarnessAssert.True(!string.IsNullOrWhiteSpace(expectedCommandKind),
            "Direct Basic receipt assertion lacks commandKind");
        var expectedReceiptId = Sha256(Encoding.UTF8.GetBytes(
            $"BASIC_SUMMARY\0{identity.OwnerId}\0{commandId}"));
        HarnessAssert.Equal(expectedReceiptId, identity.ReceiptId,
            "Basic mutation response receiptId is not deterministic");
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
            "Direct Basic receipt _id mismatch");
        HarnessAssert.Equal("BASIC_SUMMARY", BsonString(receipt, "ownerKind"),
            "Direct Basic receipt ownerKind mismatch");
        HarnessAssert.Equal(expectedCommandKind,
            BsonString(receipt, "commandKind"),
            "Basic receipt commandKind mismatch");
        HarnessAssert.Equal(actorId, BsonString(receipt, "actorUserId"),
            "Basic receipt actor mismatch");
        HarnessAssert.Equal(identity.ConfigId,
            BsonString(receipt, "resultConfigId"),
            "Basic receipt result configId mismatch");
        HarnessAssert.Equal(identity.VersionId,
            BsonString(receipt, "resultVersionId"),
            "Basic receipt result versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo,
            BsonInt(receipt, "resultVersionNo"),
            "Basic receipt result versionNo mismatch");
        HarnessAssert.Equal(identity.Revision,
            BsonLong(receipt, "resultRevision"),
            "Basic receipt result revision mismatch");
        HarnessAssert.Equal(identity.Status,
            BsonString(receipt, "resultStatus"),
            "Basic receipt result status mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            BsonString(receipt, "resultConfigHash"),
            "Basic receipt result hash mismatch");
        var responseJson = BsonString(receipt, "responseJson")
                           ?? throw new InvalidOperationException(
                               "Basic receipt responseJson is absent.");
        HarnessAssert.Equal(Canonicalize(identity.Raw),
            Canonicalize(JsonNode.Parse(responseJson)
                         ?? throw new InvalidOperationException(
                             "Basic receipt responseJson is null.")),
            "Basic receipt responseJson differs from mutation response");
        HarnessAssert.Equal(BsonString(receipt, "responseHash"),
            Sha256(Encoding.UTF8.GetBytes(responseJson)),
            "Basic receipt responseHash mismatch");
    }

    private static string CanonicalBasicReadback(JsonObject root)
    {
        var clone = (JsonObject)root.DeepClone();
        clone.Remove("receiptId");
        return Canonicalize(clone);
    }
}

internal sealed record P8BasicConfigIdentity(
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
    JsonObject MeanContract,
    string? PreviousVersionId,
    JsonArray Versions,
    string? ReceiptId,
    JsonObject Raw);
