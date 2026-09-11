using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P8AdvancedConfigIdentity> ReadAdvancedConfigAsync(
        P8Actor actor,
        P8AdvancedFixture fixture,
        CancellationToken ct,
        bool requirePersisted)
    {
        var response = await _api.GetAsync(
            AdvancedConfigRoute(fixture),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Advanced config GET {fixture.Key}");
        var identity = ParseAdvancedIdentity(
            response.Json,
            requireCommandReceipt: false);
        RequireAdvancedIdentityContract(identity, fixture);
        await RequireDirectAdvancedIdentityAsync(
            identity,
            fixture,
            commandId: null,
            expectedCommandKind: null,
            actorId: null,
            requirePersisted,
            ct);
        return identity;
    }

    private async Task<(ApiHarnessResponse Response, P8AdvancedConfigIdentity Identity)>
        PutAdvancedConfigAsync(
            P8Actor actor,
            P8AdvancedFixture fixture,
            string commandId,
            JsonObject payload,
            CancellationToken ct,
            long? expectedRevision = null,
            string? expectedConfigHash = null)
    {
        var current = await ReadAdvancedConfigAsync(
            actor,
            fixture,
            ct,
            requirePersisted: false);
        if (expectedRevision.HasValue)
        {
            HarnessAssert.Equal(expectedRevision.Value, current.Revision,
                "Caller-provided Advanced CAS revision differs from current GET");
        }
        if (expectedConfigHash is not null)
        {
            HarnessAssert.Equal(expectedConfigHash, current.ConfigHash,
                "Caller-provided Advanced CAS hash differs from current GET");
        }
        var response = await _api.PutAsync(
            AdvancedConfigRoute(fixture),
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
            $"P8 Advanced config PUT {commandId}");
        var identity = ParseAdvancedIdentity(
            response.Json,
            requireCommandReceipt: true);
        RequireAdvancedIdentityContract(identity, fixture);
        await RequireDirectAdvancedIdentityAsync(
            identity,
            fixture,
            commandId,
            "UPSERT_ADVANCED_SUMMARY_CONFIG",
            actor.Id,
            requirePersisted: true,
            ct);
        var readback = await ReadAdvancedConfigAsync(
            actor,
            fixture,
            ct,
            requirePersisted: true);
        HarnessAssert.Equal(
            CanonicalAdvancedReadback(identity.Raw),
            CanonicalAdvancedReadback(readback.Raw),
            "Advanced PUT response and stable current GET differ");
        return (response, identity);
    }

    private async Task<(ApiHarnessResponse Response, P8AdvancedConfigIdentity Identity)>
        PostAdvancedActionAsync(
            P8Actor actor,
            P8AdvancedFixture fixture,
            string action,
            string commandId,
            P8AdvancedConfigIdentity current,
            CancellationToken ct,
            JsonObject? payload = null)
    {
        var response = await _api.PostAsync(
            $"{AdvancedConfigRoute(fixture)}/{action}",
            Envelope(
                commandId,
                current.Revision,
                current.ConfigHash,
                payload ?? AdvancedActionPayload()),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Advanced config {action} {commandId}");
        var identity = ParseAdvancedIdentity(
            response.Json,
            requireCommandReceipt: true);
        RequireAdvancedIdentityContract(identity, fixture);
        var commandKind = action switch
        {
            "lock" => "LOCK_ADVANCED_SUMMARY_CONFIG",
            "next-draft" => "CREATE_ADVANCED_SUMMARY_DRAFT",
            "archive" => "ARCHIVE_ADVANCED_SUMMARY_CONFIG",
            _ => throw new InvalidOperationException(
                $"Unsupported P8 Advanced action {action}")
        };
        await RequireDirectAdvancedIdentityAsync(
            identity,
            fixture,
            commandId,
            commandKind,
            actor.Id,
            requirePersisted: true,
            ct);
        return (response, identity);
    }

    private async Task<ApiHarnessResponse> RequireZeroWriteAdvancedRejectionAsync(
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
        ExpectAdvancedFailure(
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

    private static void ExpectAdvancedFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath = null,
        string? expectedReason = null)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            expectedStatus,
            "expected P8 Advanced Summary rejection");
        var actualCode = ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "errorCode")
                         ?? ApiHarnessClient.FindStringRecursive(
                             response.Json,
                             "code");
        HarnessAssert.Equal(expectedCode, actualCode,
            "P8 Advanced Summary error code mismatch");
        if (expectedPath is not null)
        {
            HarnessAssert.Equal(expectedPath,
                ApiHarnessClient.FindStringRecursive(response.Json, "path"),
                "P8 Advanced Summary error path mismatch");
        }
        if (expectedReason is not null)
        {
            HarnessAssert.Equal(expectedReason,
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                "P8 Advanced Summary error reason mismatch");
        }
    }

    private static P8AdvancedConfigIdentity ParseAdvancedIdentity(
        JsonNode? node,
        bool requireCommandReceipt)
    {
        var root = ApiHarnessClient.RequiredObject(
            node,
            "P8 Advanced Summary config response");
        var identityNode = root["identity"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "P8 Advanced response lacks identity");
        var payload = root["payload"] as JsonObject
                      ?? throw new InvalidOperationException(
                          "P8 Advanced response lacks payload");
        var pins = identityNode["dependencyPins"] as JsonArray
                   ?? throw new InvalidOperationException(
                       "P8 Advanced identity lacks dependencyPins");
        var permissions = root["permissions"] as JsonObject
                          ?? throw new InvalidOperationException(
                              "P8 Advanced response lacks permissions");
        var validationReceipt = root["validationReceipt"] as JsonObject;
        var commandReceiptId = OptionalString(root, "commandReceiptId")
                               ?? OptionalString(root, "receiptId");
        if (requireCommandReceipt && string.IsNullOrWhiteSpace(commandReceiptId))
        {
            throw new InvalidOperationException(
                "Successful P8 Advanced mutation lacks command receipt id");
        }
        return new P8AdvancedConfigIdentity(
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
            validationReceipt,
            OptionalString(root, "previousVersionId"),
            root["versions"] as JsonArray ?? new JsonArray(),
            commandReceiptId,
            root);
    }

    private static void RequireAdvancedIdentityContract(
        P8AdvancedConfigIdentity identity,
        P8AdvancedFixture fixture)
    {
        HarnessAssert.Equal("ADVANCED_SUMMARY", identity.OwnerKind,
            "Advanced ownerKind mismatch");
        HarnessAssert.Equal(
            $"{fixture.Assignment.Id}:{fixture.DynamicFormTemplateId}:{fixture.SectionId}",
            identity.OwnerId,
            "Advanced ownerId does not bind assignment/template/section");
        HarnessAssert.True(ObjectId.TryParse(identity.ConfigId, out _),
            "Advanced configId is not an ObjectId");
        HarnessAssert.True(ObjectId.TryParse(identity.VersionId, out _),
            "Advanced versionId is not an ObjectId");
        if (identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(
                Sha256(Encoding.UTF8.GetBytes(
                    $"ADVANCED_SUMMARY\0{identity.OwnerId}\0CONFIG"))[..24],
                identity.ConfigId,
                "Virtual Advanced configId is not deterministic");
            HarnessAssert.Equal(
                Sha256(Encoding.UTF8.GetBytes(
                    $"ADVANCED_SUMMARY\0{identity.OwnerId}\0VERSION:1"))[..24],
                identity.VersionId,
                "Virtual Advanced versionId is not deterministic");
            HarnessAssert.Equal(1, identity.VersionNo,
                "Virtual Advanced versionNo mismatch");
            HarnessAssert.Equal(0L, identity.Revision,
                "Virtual Advanced revision mismatch");
            HarnessAssert.Equal("DRAFT", identity.Status,
                "Virtual Advanced status mismatch");
            HarnessAssert.Equal(EmptyConfigHash, identity.ConfigHash,
                "Virtual Advanced configHash mismatch");
            HarnessAssert.Equal(null, identity.PreviousVersionId,
                "Virtual Advanced previousVersionId is non-null");
            HarnessAssert.Equal(0, identity.Versions.Count,
                "Virtual Advanced unexpectedly exposes versions");
        }
        RequireLowerSha256(identity.ConfigHash, "Advanced configHash");
        HarnessAssert.Equal("BLOCKED_UNTIL_P9", identity.RuntimeEligibility,
            "Advanced runtime eligibility crossed the P9 barrier");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                identity.DependencyPins
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Advanced dependency pins are not canonical distinct ordinal");
        HarnessAssert.Equal(true,
            RequiredBool(identity.Permissions, "canReadConfig"),
            "Advanced config read permission mismatch");
        HarnessAssert.Equal(false,
            RequiredBool(identity.Permissions, "canViewResult"),
            "Advanced result permission crossed P9 barrier");
        HarnessAssert.Equal(false,
            RequiredBool(identity.Permissions, "canReadDiagnostics"),
            "Advanced diagnostics permission crossed P9 barrier");
        if (identity.Status is "LOCKED" or "ARCHIVED")
        {
            HarnessAssert.True(identity.ValidationReceipt is not null,
                "Locked/archived Advanced version lacks validation receipt");
            RequireAdvancedValidationReceipt(identity, fixture);
        }
        else
        {
            HarnessAssert.Equal(null, identity.ValidationReceipt,
                "Virtual/DRAFT Advanced config exposed a validation receipt");
        }
    }

    private static void RequireAdvancedValidationReceipt(
        P8AdvancedConfigIdentity identity,
        P8AdvancedFixture fixture)
    {
        var receipt = identity.ValidationReceipt
                      ?? throw new InvalidOperationException(
                          "Advanced validation receipt is absent");
        HarnessAssert.Equal("P8_ADVANCED_CONFIG_VALIDATION_V1",
            RequiredString(receipt, "contractVersion"),
            "Advanced validation receipt contractVersion mismatch");
        HarnessAssert.Equal("CONFIG_ONLY_NO_DATASET",
            RequiredString(receipt, "validationMode"),
            "Advanced validation receipt mode mismatch");
        HarnessAssert.Equal(identity.OwnerId,
            RequiredString(receipt, "ownerId"),
            "Advanced validation receipt ownerId mismatch");
        HarnessAssert.Equal(identity.ConfigId,
            RequiredString(receipt, "configId"),
            "Advanced validation receipt configId mismatch");
        HarnessAssert.Equal(identity.VersionId,
            RequiredString(receipt, "versionId"),
            "Advanced validation receipt versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo,
            RequiredInt(receipt, "versionNo"),
            "Advanced validation receipt versionNo mismatch");
        var receiptRevision = identity.Status == "ARCHIVED"
            ? identity.Revision - 1
            : identity.Revision;
        HarnessAssert.Equal(receiptRevision,
            RequiredLong(receipt, "revision"),
            "Advanced validation receipt locked revision mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            RequiredString(receipt, "configHash"),
            "Advanced validation receipt hash mismatch");
        HarnessAssert.Equal(fixture.SectionId,
            RequiredString(receipt, "sectionId"),
            "Advanced validation receipt sectionId mismatch");
        HarnessAssert.Equal("BLOCKED_UNTIL_P9",
            RequiredString(receipt, "runtimeEligibility"),
            "Advanced validation receipt runtime barrier mismatch");
        HarnessAssert.Equal(false, RequiredBool(receipt, "previewRead"),
            "Advanced validation receipt reports preview read");
        HarnessAssert.Equal(false, RequiredBool(receipt, "previewWrite"),
            "Advanced validation receipt reports preview write");
        HarnessAssert.Equal(false, RequiredBool(receipt, "hierarchyRead"),
            "Advanced validation receipt reports hierarchy read");
        HarnessAssert.Equal(false, RequiredBool(receipt, "hierarchyWrite"),
            "Advanced validation receipt reports hierarchy write");
        HarnessAssert.Equal(3,
            RequiredInt(receipt, "maxHierarchyDepth"),
            "Advanced maximum hierarchy depth drifted");
        HarnessAssert.Equal(1_048_576,
            RequiredInt(receipt, "maxCanonicalPayloadBytes"),
            "Advanced maximum canonical payload bytes drifted");
        var expectedReceiptId = Sha256(Encoding.UTF8.GetBytes(
            $"ADVANCED_SUMMARY_VALIDATION\0{identity.OwnerId}\0" +
            $"{identity.VersionId}\0{receiptRevision}\0{identity.ConfigHash}"));
        HarnessAssert.Equal(expectedReceiptId,
            RequiredString(receipt, "receiptId"),
            "Advanced validation receiptId is not deterministic");
    }

    private async Task RequireDirectAdvancedIdentityAsync(
        P8AdvancedConfigIdentity identity,
        P8AdvancedFixture fixture,
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
                     Builders<BsonDocument>.Filter.Eq(
                         "sectionId",
                         fixture.SectionId) &
                     Builders<BsonDocument>.Filter.Ne("isDeleted", true);
        var documents = await _database
            .GetCollection<BsonDocument>(AdvancedConfigsCollection)
            .Find(filter)
            .ToListAsync(ct);
        if (identity.IsVirtualEmpty)
        {
            HarnessAssert.Equal(0, documents.Count,
                "Virtual Advanced config unexpectedly persisted");
            return;
        }
        if (!requirePersisted)
            return;
        HarnessAssert.Equal(identity.Versions.Count, documents.Count,
            "Advanced row-per-version cardinality differs from API versions");
        HarnessAssert.True(documents.All(document => string.Equals(
                identity.ConfigId,
                BsonString(document, "configId"),
                StringComparison.Ordinal)),
            "Advanced lineage rows do not share configId");
        var owner = documents.Single(document => string.Equals(
            identity.VersionId,
            BsonString(document, "_id"),
            StringComparison.Ordinal));
        HarnessAssert.Equal(identity.ConfigId, BsonString(owner, "configId"),
            "Direct Advanced shared configId mismatch");
        HarnessAssert.Equal(fixture.Assignment.WorkId,
            BsonString(owner, "workId"),
            "Direct Advanced workId mismatch");
        HarnessAssert.Equal(fixture.Assignment.Id,
            BsonString(owner, "assignmentId"),
            "Direct Advanced assignmentId mismatch");
        HarnessAssert.Equal(fixture.DynamicFormTemplateId,
            BsonString(owner, "dynamicFormTemplateId"),
            "Direct Advanced template mismatch");
        HarnessAssert.Equal(fixture.SectionId,
            BsonString(owner, "sectionId"),
            "Direct Advanced section mismatch");
        HarnessAssert.Equal(identity.VersionId, BsonString(owner, "_id"),
            "Direct Advanced versionId mismatch");
        HarnessAssert.Equal(identity.PreviousVersionId,
            BsonString(owner, "previousVersionId"),
            "Direct Advanced previousVersionId mismatch");
        HarnessAssert.Equal(identity.VersionNo,
            BsonInt(owner, "versionNo"),
            "Direct Advanced versionNo mismatch");
        HarnessAssert.Equal(identity.Revision,
            BsonLong(owner, "revision"),
            "Direct Advanced revision mismatch");
        HarnessAssert.Equal(identity.Status,
            BsonString(owner, "status"),
            "Direct Advanced status mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            BsonString(owner, "configHash"),
            "Direct Advanced configHash mismatch");
        var payloadJson = BsonString(owner, "configJson")
                          ?? throw new InvalidOperationException(
                              "Direct Advanced configJson is absent");
        HarnessAssert.Equal(Canonicalize(identity.Payload),
            Canonicalize(JsonNode.Parse(payloadJson)
                         ?? throw new InvalidOperationException(
                             "Direct Advanced configJson is null")),
            "Direct Advanced payload differs from API readback");
        var pins = owner.GetValue("dependencyPins", new BsonArray());
        HarnessAssert.True(pins.IsBsonArray,
            "Direct Advanced dependencyPins are malformed");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                pins.AsBsonArray.Select(value => value.AsString),
                StringComparer.Ordinal),
            "Direct Advanced dependency pins differ from API");
        var recomputed = Sha256(Encoding.UTF8.GetBytes(Canonicalize(
            new JsonObject
            {
                ["payload"] = JsonNode.Parse(payloadJson),
                ["dependencyPins"] = new JsonArray(
                    identity.DependencyPins.Select(value =>
                        JsonValue.Create(value)).ToArray())
            })));
        HarnessAssert.Equal(identity.ConfigHash, recomputed,
            "Independent Advanced configHash recomputation mismatch");
        HarnessAssert.True(documents
                .OrderBy(document => BsonInt(document, "versionNo"))
                .Select(document => BsonInt(document, "versionNo"))
                .SequenceEqual(Enumerable.Range(1, documents.Count)
                    .Select(value => (int?)value)),
            "Direct Advanced row-per-version sequence is not contiguous");
        if (identity.ValidationReceipt is not null)
        {
            var validationJson = BsonString(owner, "validationReceiptJson")
                                 ?? throw new InvalidOperationException(
                                     "Locked Advanced row lacks validationReceiptJson");
            HarnessAssert.Equal(Canonicalize(identity.ValidationReceipt),
                Canonicalize(JsonNode.Parse(validationJson)
                             ?? throw new InvalidOperationException(
                                 "Direct validation receipt JSON is null")),
                "Direct Advanced validation receipt differs from API");
            HarnessAssert.Equal(Sha256(Encoding.UTF8.GetBytes(validationJson)),
                BsonString(owner, "validationReceiptHash"),
                "Direct Advanced validation receipt hash mismatch");
        }
        if (commandId is null)
            return;
        HarnessAssert.True(!string.IsNullOrWhiteSpace(actorId),
            "Advanced receipt assertion lacks actorId");
        HarnessAssert.True(!string.IsNullOrWhiteSpace(expectedCommandKind),
            "Advanced receipt assertion lacks commandKind");
        var expectedReceiptId = Sha256(Encoding.UTF8.GetBytes(
            $"ADVANCED_SUMMARY\0{identity.OwnerId}\0{commandId}"));
        HarnessAssert.Equal(expectedReceiptId, identity.CommandReceiptId,
            "Advanced command receipt id is not deterministic");
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
            "Direct Advanced command receipt _id mismatch");
        HarnessAssert.Equal("ADVANCED_SUMMARY",
            BsonString(receipt, "ownerKind"),
            "Direct Advanced receipt ownerKind mismatch");
        HarnessAssert.Equal(expectedCommandKind,
            BsonString(receipt, "commandKind"),
            "Direct Advanced receipt commandKind mismatch");
        HarnessAssert.Equal(actorId, BsonString(receipt, "actorUserId"),
            "Direct Advanced receipt actor mismatch");
        HarnessAssert.Equal(identity.ConfigId,
            BsonString(receipt, "resultConfigId"),
            "Direct Advanced receipt configId mismatch");
        HarnessAssert.Equal(identity.VersionId,
            BsonString(receipt, "resultVersionId"),
            "Direct Advanced receipt versionId mismatch");
        HarnessAssert.Equal(identity.VersionNo,
            BsonInt(receipt, "resultVersionNo"),
            "Direct Advanced receipt versionNo mismatch");
        HarnessAssert.Equal(identity.Revision,
            BsonLong(receipt, "resultRevision"),
            "Direct Advanced receipt revision mismatch");
        HarnessAssert.Equal(identity.Status,
            BsonString(receipt, "resultStatus"),
            "Direct Advanced receipt status mismatch");
        HarnessAssert.Equal(identity.ConfigHash,
            BsonString(receipt, "resultConfigHash"),
            "Direct Advanced receipt hash mismatch");
    }

    private async Task<P8TokenQuotaIdentity> ReadTokenQuotaAsync(
        P8Actor actor,
        string ownerUnitId,
        CancellationToken ct,
        string? periodMonthKey = null)
    {
        var route = $"api/work-summary-tokens/quota?ownerUnitId={ownerUnitId}";
        if (!string.IsNullOrWhiteSpace(periodMonthKey))
            route += $"&periodMonthKey={periodMonthKey}";
        var response = await _api.GetAsync(route, actor.Token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            "P8 token quota GET");
        var root = ApiHarnessClient.RequiredObject(response.Json,
            "P8 token quota response");
        var quota = new P8TokenQuotaIdentity(
            RequiredString(root, "poolId"),
            RequiredString(root, "ownerUnitId"),
            RequiredString(root, "tokenKind"),
            RequiredString(root, "periodMonthKey"),
            RequiredInt(root, "baseMonthlyQuota"),
            RequiredInt(root, "grantedUnits"),
            RequiredInt(root, "usedUnits"),
            RequiredInt(root, "monthlyQuota"),
            RequiredInt(root, "remainingUnits"),
            RequiredLong(root, "revision"),
            RequiredString(root, "poolHash"),
            root);
        RequireTokenQuotaContract(quota, ownerUnitId);
        return quota;
    }

    private static void RequireTokenQuotaContract(
        P8TokenQuotaIdentity quota,
        string ownerUnitId)
    {
        HarnessAssert.Equal(ownerUnitId, quota.OwnerUnitId,
            "Token quota ownerUnitId mismatch");
        HarnessAssert.Equal("ADVANCED_SUMMARY_CONFIG_LOCK", quota.TokenKind,
            "Token quota kind mismatch");
        HarnessAssert.Equal(quota.BaseMonthlyQuota + quota.GrantedUnits,
            quota.MonthlyQuota,
            "Token monthly quota arithmetic mismatch");
        HarnessAssert.Equal(quota.MonthlyQuota - quota.UsedUnits,
            quota.RemainingUnits,
            "Token remaining quota arithmetic mismatch");
        HarnessAssert.True(ObjectId.TryParse(quota.PoolId, out _),
            "Token poolId is not an ObjectId");
        RequireLowerSha256(quota.PoolHash, "token poolHash");
    }

    private async Task<P8TokenMutationResult> GrantTokensAsync(
        P8Actor actor,
        string ownerUnitId,
        string commandId,
        int units,
        string reason,
        CancellationToken ct)
    {
        var current = await ReadTokenQuotaAsync(actor, ownerUnitId, ct);
        var response = await _api.PostAsync(
            TokenPoolGrantRoute(ownerUnitId),
            Envelope(commandId, current.Revision, current.PoolHash,
                TokenGrantPayload(units, reason)),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            $"P8 token grant {commandId}");
        var result = ParseTokenMutation(response.Json, compensation: false);
        RequireTokenMutationContract(result, ownerUnitId, units);
        await RequireDirectTokenEntryAsync(
            result.LedgerId,
            "GRANT",
            ownerUnitId,
            result.CommandReceiptId,
            actor.Id,
            units,
            ct);
        return result;
    }

    private async Task<P8TokenMutationResult> CompensateTokensAsync(
        P8Actor actor,
        string ownerUnitId,
        string ledgerId,
        string commandId,
        string reason,
        CancellationToken ct)
    {
        var current = await ReadTokenQuotaAsync(actor, ownerUnitId, ct);
        var response = await _api.PostAsync(
            TokenCompensationRoute(ledgerId),
            Envelope(commandId, current.Revision, current.PoolHash,
                TokenCompensationPayload(reason)),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            $"P8 token compensation {commandId}");
        var result = ParseTokenMutation(response.Json, compensation: true);
        HarnessAssert.Equal(ledgerId, result.CompensatedLedgerId,
            "Compensation response target ledger mismatch");
        RequireTokenMutationContract(result, ownerUnitId, 1);
        await RequireDirectTokenEntryAsync(
            result.LedgerId,
            "COMPENSATE",
            ownerUnitId,
            result.CommandReceiptId,
            actor.Id,
            1,
            ct);
        return result;
    }

    private static P8TokenMutationResult ParseTokenMutation(
        JsonNode? node,
        bool compensation)
    {
        var root = ApiHarnessClient.RequiredObject(node,
            "P8 token mutation response");
        var quota = root["quota"] as JsonObject
                    ?? throw new InvalidOperationException(
                        "P8 token mutation lacks quota");
        var ledgerId = compensation
            ? RequiredString(root, "compensationLedgerId")
            : RequiredString(root, "ledgerId");
        return new P8TokenMutationResult(
            ledgerId,
            RequiredString(root, "commandReceiptId"),
            RequiredString(root, "ownerUnitId"),
            OptionalString(root, "issuerUserId") ?? string.Empty,
            RequiredString(root, "tokenKind"),
            RequiredString(root, "periodMonthKey"),
            RequiredInt(root, "units"),
            RequiredLong(root, "poolRevision"),
            RequiredString(root, "poolHash"),
            quota,
            compensation
                ? RequiredString(root, "compensationLedgerId")
                : null,
            compensation
                ? RequiredString(root, "compensatedLedgerId")
                : null,
            root);
    }

    private static void RequireTokenMutationContract(
        P8TokenMutationResult result,
        string ownerUnitId,
        int units)
    {
        HarnessAssert.Equal(ownerUnitId, result.OwnerUnitId,
            "Token mutation ownerUnitId mismatch");
        HarnessAssert.Equal("ADVANCED_SUMMARY_CONFIG_LOCK", result.TokenKind,
            "Token mutation kind mismatch");
        HarnessAssert.Equal(units, result.Units,
            "Token mutation units mismatch");
        HarnessAssert.True(ObjectId.TryParse(result.LedgerId, out _),
            "Token mutation ledgerId is not ObjectId");
        RequireLowerSha256(result.PoolHash, "token mutation poolHash");
        HarnessAssert.Equal(result.PoolRevision,
            RequiredLong(result.Quota, "revision"),
            "Token mutation quota revision mismatch");
        HarnessAssert.Equal(result.PoolHash,
            RequiredString(result.Quota, "poolHash"),
            "Token mutation quota hash mismatch");
    }

    private async Task RequireDirectTokenEntryAsync(
        string ledgerId,
        string direction,
        string ownerUnitId,
        string requestTokenId,
        string actorId,
        int units,
        CancellationToken ct)
    {
        var entry = await _database
            .GetCollection<BsonDocument>(TokenLedgersCollection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", ObjectId.Parse(ledgerId)))
            .SingleAsync(ct);
        HarnessAssert.Equal("ENTRY", BsonString(entry, "recordKind"),
            "Token ledger mutation did not persist ENTRY recordKind");
        HarnessAssert.Equal(direction, BsonString(entry, "direction"),
            "Token ledger direction mismatch");
        HarnessAssert.Equal(ownerUnitId, BsonString(entry, "ownerUnitId"),
            "Token ledger ownerUnitId mismatch");
        HarnessAssert.Equal(actorId, BsonString(entry, "actorUserId"),
            "Token ledger actor mismatch");
        HarnessAssert.Equal(units, BsonInt(entry, "units"),
            "Token ledger units mismatch");
        HarnessAssert.Equal("ADVANCED_SUMMARY_CONFIG_LOCK",
            BsonString(entry, "tokenKind"),
            "Token ledger kind mismatch");
        HarnessAssert.Equal("SUCCESS", BsonString(entry, "outcome"),
            "Token ledger outcome mismatch");
        HarnessAssert.Equal(requestTokenId, BsonString(entry, "requestTokenId"),
            "Token ledger requestTokenId is not the durable command receipt id");
    }

    private static string CanonicalAdvancedReadback(JsonObject root)
    {
        var clone = (JsonObject)root.DeepClone();
        clone.Remove("commandReceiptId");
        clone.Remove("receiptId");
        return Canonicalize(clone);
    }
}

internal sealed record P8AdvancedConfigIdentity(
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
    JsonObject? ValidationReceipt,
    string? PreviousVersionId,
    JsonArray Versions,
    string? CommandReceiptId,
    JsonObject Raw);

internal sealed record P8TokenQuotaIdentity(
    string PoolId,
    string OwnerUnitId,
    string TokenKind,
    string PeriodMonthKey,
    int BaseMonthlyQuota,
    int GrantedUnits,
    int UsedUnits,
    int MonthlyQuota,
    int RemainingUnits,
    long Revision,
    string PoolHash,
    JsonObject Raw);

internal sealed record P8TokenMutationResult(
    string LedgerId,
    string CommandReceiptId,
    string OwnerUnitId,
    string IssuerUserId,
    string TokenKind,
    string PeriodMonthKey,
    int Units,
    long PoolRevision,
    string PoolHash,
    JsonObject Quota,
    string? CompensationLedgerId,
    string? CompensatedLedgerId,
    JsonObject Raw);
