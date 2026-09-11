using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P8FlowDraftFixture> CreateFlowDraftAsync(
        P8Actor actor,
        string key,
        string commandId,
        JsonObject payload,
        CancellationToken ct)
    {
        var response = await _api.PostAsync(
            "api/dynamic-flow-templates",
            new JsonObject
            {
                ["commandId"] = commandId,
                ["code"] = $"P8_FLW_{key.Replace('-', '_').ToUpperInvariant()}",
                ["name"] = $"P8 Flow {key}",
                ["description"] = "P8-07 configuration-only P7 baseline",
                ["rootDynamicFormTemplateId"] = _flowRootFormId,
                ["payload"] = payload.DeepClone()
            },
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Flow create {key}");
        var family = ApiHarnessClient.RequiredObject(
            response.Json,
            $"P8 Flow family {key}");
        var draft = ParseFlowVersion(
            family["draftVersion"],
            $"P8 Flow draft {key}");
        var fixture = new P8FlowDraftFixture(
            key,
            RequiredString(family, "id"),
            RequiredInt(family, "familyRevision"),
            draft);
        RequireFlowVersionContract(draft, fixture.FamilyId, "DRAFT");
        await RequireDirectFlowVersionAsync(
            fixture,
            draft,
            commandId,
            "CREATE",
            actor.Id,
            ct);
        return fixture;
    }

    private async Task<P8FlowDraftFixture> SaveFlowDraftAsync(
        P8Actor actor,
        P8FlowDraftFixture fixture,
        string commandId,
        JsonObject payload,
        CancellationToken ct)
    {
        var response = await _api.PutAsync(
            $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/" +
            $"{fixture.Version.Id}/draft",
            new JsonObject
            {
                ["commandId"] = commandId,
                ["expectedDraftRevision"] = fixture.Version.DraftRevision,
                ["expectedPayloadHash"] = fixture.Version.PayloadHash,
                ["payload"] = payload.DeepClone()
            },
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Flow save {fixture.Key}");
        var saved = ParseFlowVersion(response.Json, "P8 Flow saved draft");
        var family = await ReadFlowFamilyAsync(actor, fixture.FamilyId, ct);
        var result = fixture with
        {
            FamilyRevision = RequiredInt(family, "familyRevision"),
            Version = saved
        };
        RequireFlowVersionContract(saved, fixture.FamilyId, "DRAFT");
        await RequireDirectFlowVersionAsync(
            result,
            saved,
            commandId,
            "SAVE_DRAFT",
            actor.Id,
            ct);
        return result;
    }

    private async Task<P8FlowVersionIdentity> LockFlowVersionAsync(
        P8Actor actor,
        P8FlowDraftFixture fixture,
        string commandId,
        string? contributionPolicy,
        bool? acknowledgeWarning,
        CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["commandId"] = commandId,
            ["expectedFamilyRevision"] = fixture.FamilyRevision,
            ["expectedDraftRevision"] = fixture.Version.DraftRevision,
            ["expectedPayloadHash"] = fixture.Version.PayloadHash
        };
        if (contributionPolicy is not null)
            request["contributionPolicy"] = contributionPolicy;
        if (acknowledgeWarning.HasValue)
            request["acknowledgeContributionWarning"] = acknowledgeWarning.Value;

        var response = await _api.PostAsync(
            $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/" +
            $"{fixture.Version.Id}/lock",
            request,
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P8 Flow lock {fixture.Key}");
        var locked = ParseFlowVersion(response.Json, "P8 Flow locked version");
        RequireFlowVersionContract(locked, fixture.FamilyId, "LOCKED");
        var family = await ReadFlowFamilyAsync(actor, fixture.FamilyId, ct);
        var committed = fixture with
        {
            FamilyRevision = RequiredInt(family, "familyRevision"),
            Version = locked
        };
        await RequireDirectFlowVersionAsync(
            committed,
            locked,
            commandId,
            "LOCK",
            actor.Id,
            ct);
        return locked;
    }

    private async Task<P8FlowDraftFixture> ReopenFlowVersionAsync(
        P8Actor actor,
        P8FlowDraftFixture family,
        P8FlowVersionIdentity source,
        string commandId,
        CancellationToken ct)
    {
        var response = await _api.PostAsync(
            $"api/dynamic-flow-templates/{family.FamilyId}/versions/" +
            $"{source.Id}/reopen",
            new JsonObject
            {
                ["commandId"] = commandId,
                ["expectedFamilyRevision"] = family.FamilyRevision
            },
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P8 Flow reopen version");
        var draft = ParseFlowVersion(response.Json, "P8 Flow reopened draft");
        var familyRead = await ReadFlowFamilyAsync(actor, family.FamilyId, ct);
        var result = new P8FlowDraftFixture(
            "v-include",
            family.FamilyId,
            RequiredInt(familyRead, "familyRevision"),
            draft);
        RequireFlowVersionContract(draft, family.FamilyId, "DRAFT");
        await RequireDirectFlowVersionAsync(
            result,
            draft,
            commandId,
            "REOPEN",
            actor.Id,
            ct);
        return result;
    }

    private async Task<JsonObject> ReadFlowFamilyAsync(
        P8Actor actor,
        string familyId,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"api/dynamic-flow-templates/{familyId}",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            "P8 Flow family GET");
        return ApiHarnessClient.RequiredObject(
            response.Json,
            "P8 Flow family GET response");
    }

    private async Task<P8FlowVersionIdentity> ReadFlowVersionAsync(
        P8Actor actor,
        string familyId,
        string versionId,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"api/dynamic-flow-templates/{familyId}/versions/{versionId}",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            "P8 Flow version GET");
        var version = ParseFlowVersion(response.Json, "P8 Flow version GET");
        RequireFlowVersionContract(version, familyId, version.Status);
        return version;
    }

    private async Task<JsonArray> ListFlowVersionsAsync(
        P8Actor actor,
        string familyId,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"api/dynamic-flow-templates/{familyId}/versions",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            "P8 Flow versions GET");
        return ApiHarnessClient.RequiredArray(
            response.Json,
            "P8 Flow versions response");
    }

    private async Task<ApiHarnessResponse> RequireZeroWriteFlowRejectionAsync(
        string probeId,
        Func<Task<ApiHarnessResponse>> request,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath,
        CancellationToken ct)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var response = await request();
        ExpectFlowFailure(response, expectedStatus, expectedCode, expectedPath);
        var after = await CaptureDatabaseSnapshotAsync(ct);
        VerifyCollectionContract(
            probeId,
            BuildDeltas(before, after),
            Array.Empty<string>(),
            Array.Empty<string>());
        return response;
    }

    private static void ExpectFlowFailure(
        ApiHarnessResponse response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string? expectedPath)
    {
        ApiHarnessClient.ExpectStatus(response, expectedStatus,
            "expected P8 Flow rejection");
        var code = ApiHarnessClient.FindStringRecursive(response.Json, "errorCode")
                   ?? ApiHarnessClient.FindStringRecursive(response.Json, "code");
        HarnessAssert.Equal(expectedCode, code,
            "P8 Flow error code mismatch");
        if (expectedPath is not null)
        {
            var path = ApiHarnessClient.FindStringRecursive(response.Json, "path")
                       ?? ApiHarnessClient.FindStringRecursive(response.Json, "field");
            HarnessAssert.Equal(expectedPath, path,
                "P8 Flow error path mismatch");
        }
    }

    private static P8FlowVersionIdentity ParseFlowVersion(
        JsonNode? node,
        string context)
    {
        var root = ApiHarnessClient.RequiredObject(node, context);
        var payload = root["payload"] as JsonObject
                      ?? throw new InvalidOperationException(
                          $"{context} lacks typed payload");
        var lockedAt = OptionalString(root, "lockedAtUtc");
        return new P8FlowVersionIdentity(
            RequiredString(root, "id"),
            RequiredString(root, "templateId"),
            RequiredInt(root, "versionNo"),
            RequiredString(root, "status"),
            RequiredInt(root, "draftRevision"),
            RequiredInt(root, "schemaVersion"),
            RequiredInt(root, "adapterVersion"),
            RequiredString(root, "catalogVersion"),
            RequiredString(root, "catalogSemanticHash"),
            payload,
            RequiredString(root, "payloadJson"),
            RequiredString(root, "payloadHash"),
            RequiredBool(root, "definitionLockable"),
            OptionalString(root, "contributionPolicy"),
            OptionalString(root, "contributionPolicyHash"),
            OptionalString(root, "contributionWarning"),
            lockedAt is null
                ? null
                : DateTime.Parse(lockedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            OptionalString(root, "lockedByUserId"),
            root);
    }

    private void RequireFlowVersionContract(
        P8FlowVersionIdentity version,
        string familyId,
        string expectedStatus)
    {
        HarnessAssert.Equal(familyId, version.FamilyId,
            "Flow version familyId mismatch");
        HarnessAssert.Equal(expectedStatus, version.Status,
            "Flow version status mismatch");
        HarnessAssert.True(ObjectId.TryParse(version.Id, out _),
            "Flow version id is not an ObjectId");
        HarnessAssert.Equal(2, version.SchemaVersion,
            "Flow version schemaVersion crossed the P7 baseline");
        HarnessAssert.Equal(1, version.AdapterVersion,
            "Flow version adapterVersion crossed the P7 baseline");
        HarnessAssert.Equal(
            tdtd_be.Common.Capabilities
                .DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            version.CatalogVersion,
            "Flow version catalogVersion differs from generated CURRENT");
        HarnessAssert.Equal(
            tdtd_be.Common.Capabilities
                .DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            version.CatalogSemanticHash,
            "Flow version catalogSemanticHash differs from generated CURRENT");
        RequireLowerSha256(version.CatalogSemanticHash,
            "Flow catalogSemanticHash");
        RequireLowerSha256(version.PayloadHash, "Flow payloadHash");
        HarnessAssert.Equal(
            version.PayloadHash,
            Sha256(Encoding.UTF8.GetBytes(version.PayloadJson)),
            "Independent Flow payloadHash recomputation mismatch");
        var canonicalOptions = new tdtd_be.Services.DynamicFlows
            .DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true);
        HarnessAssert.True(
            version.Payload["steps"] is JsonArray { Count: 0 },
            "Typed Flow payload legacy steps alias is not empty");
        HarnessAssert.True(
            version.Payload["transitions"] is JsonArray { Count: 0 },
            "Typed Flow payload legacy transitions alias is not empty");
        var typedProjection = version.Payload.DeepClone().AsObject();
        typedProjection.Remove("steps");
        typedProjection.Remove("transitions");
        var typedCanonical = tdtd_be.Services.DynamicFlows
            .DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                typedProjection.ToJsonString(),
                canonicalOptions);
        var storedCanonical = tdtd_be.Services.DynamicFlows
            .DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                version.PayloadJson,
                canonicalOptions);
        HarnessAssert.Equal(
            storedCanonical.CanonicalJson,
            typedCanonical.CanonicalJson,
            "Flow typed payload differs from canonical payloadJson");
        HarnessAssert.Equal(version.PayloadHash,
            storedCanonical.PayloadHash,
            "Stored payload canonical hash mismatch");
        HarnessAssert.Equal(version.PayloadHash,
            typedCanonical.PayloadHash,
            "Typed payload canonical hash mismatch");
        HarnessAssert.True(version.Payload["contributionPolicy"] is null,
            "P8 contribution policy leaked into the frozen P7 payload");
        RequireP7PinnedFormNode(version.Payload);
        if (string.Equals(expectedStatus, "LOCKED", StringComparison.Ordinal))
        {
            HarnessAssert.True(version.DefinitionLockable,
                "Locked Flow version is not definition-lockable");
            HarnessAssert.True(version.LockedAtUtc.HasValue,
                "Locked Flow version lacks lockedAtUtc");
            HarnessAssert.True(!string.IsNullOrWhiteSpace(version.LockedByUserId),
                "Locked Flow version lacks lockedByUserId");
            HarnessAssert.True(
                version.ContributionPolicy is "EXCLUDE" or "INCLUDE",
                "Locked Flow version lacks immutable contribution policy");
            RequireLowerSha256(
                version.ContributionPolicyHash ?? string.Empty,
                "Flow contributionPolicyHash");
            var expectedWarning = string.Equals(
                version.ContributionPolicy,
                "INCLUDE",
                StringComparison.Ordinal)
                ? "DYNAMIC_FLOW_STATISTIC_CONTRIBUTION_INCLUDE_WARNING"
                : null;
            HarnessAssert.Equal(expectedWarning, version.ContributionWarning,
                "Flow contribution warning mismatch");
            HarnessAssert.Equal(
                ComputeContributionPolicyHash(version),
                version.ContributionPolicyHash,
                "Independent Flow contributionPolicyHash recomputation mismatch");
        }
    }

    private void RequireP7PinnedFormNode(JsonObject payload)
    {
        var formNodes = payload["formNodes"] as JsonArray
                        ?? throw new InvalidOperationException(
                            "Flow payload lacks formNodes");
        HarnessAssert.Equal(2, formNodes.Count,
            "P8-07 baseline must have exactly two pinned form nodes");
        RequireP7PinnedFormNode(
            formNodes,
            "root_form",
            "ROOT",
            _flowRootFormId);
        RequireP7PinnedFormNode(
            formNodes,
            "child_form",
            "CHILD",
            _flowChildFormId);
    }

    private static void RequireP7PinnedFormNode(
        JsonArray formNodes,
        string expectedNodeId,
        string expectedRole,
        string expectedTemplateId)
    {
        var matches = formNodes
            .OfType<JsonObject>()
            .Where(node => string.Equals(
                OptionalString(node, "formNodeId"),
                expectedNodeId,
                StringComparison.Ordinal))
            .ToArray();
        HarnessAssert.Equal(1, matches.Length,
            $"Flow {expectedNodeId} pin is not unique");
        var node = matches[0];
        HarnessAssert.Equal(expectedRole, RequiredString(node, "role"),
            $"Flow {expectedNodeId} role mismatch");
        HarnessAssert.Equal(expectedTemplateId,
            RequiredString(node, "dynamicFormTemplateId"),
            $"Flow {expectedNodeId} template pin mismatch");
        HarnessAssert.Equal(expectedTemplateId,
            RequiredString(node, "dynamicFormFamilyId"),
            $"Flow {expectedNodeId} family pin mismatch");
        HarnessAssert.Equal(1, RequiredInt(node, "dynamicFormVersionNo"),
            $"Flow {expectedNodeId} version pin mismatch");
        RequireLowerSha256(
            RequiredString(node, "dynamicFormSchemaHash"),
            $"Flow {expectedNodeId} schema pin");
        RequireLowerSha256(
            RequiredString(node, "dynamicFormSnapshotHash"),
            $"Flow {expectedNodeId} snapshot pin");
    }

    private static string ComputeContributionPolicyHash(
        P8FlowVersionIdentity version)
    {
        var lineage = version.Raw["lineage"] as JsonObject
                      ?? throw new InvalidOperationException(
                          "Flow version lacks lineage");
        var policy = version.ContributionPolicy
                     ?? throw new InvalidOperationException(
                         "Locked Flow version lacks contribution policy");
        var warning = string.Equals(policy, "INCLUDE", StringComparison.Ordinal)
            ? "DYNAMIC_FLOW_STATISTIC_CONTRIBUTION_INCLUDE_WARNING"
            : string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("adapterVersion", version.AdapterVersion);
            writer.WriteString("catalogSemanticHash", version.CatalogSemanticHash);
            writer.WriteString("catalogVersion", version.CatalogVersion);
            writer.WriteString("contributionPolicy", policy);
            writer.WriteString("originFamilyId",
                OptionalString(lineage, "originFamilyId") ?? string.Empty);
            writer.WriteString("originVersionId",
                OptionalString(lineage, "originVersionId") ?? string.Empty);
            writer.WriteString("payloadHash", version.PayloadHash);
            writer.WriteString("payloadJsonSha256",
                Sha256(Encoding.UTF8.GetBytes(version.PayloadJson)));
            writer.WriteString("rootDynamicFormTemplateId",
                OptionalString(version.Raw, "rootDynamicFormTemplateId")
                ?? string.Empty);
            writer.WriteNumber("schemaVersion", version.SchemaVersion);
            writer.WriteString("templateId", version.FamilyId);
            writer.WriteString("versionId", version.Id);
            writer.WriteNumber("versionNo", version.VersionNo);
            writer.WriteString("warning", warning);
            writer.WriteEndObject();
        }

        return Sha256(stream.ToArray());
    }

    private async Task RequireDirectFlowVersionAsync(
        P8FlowDraftFixture fixture,
        P8FlowVersionIdentity version,
        string commandId,
        string commandKind,
        string actorId,
        CancellationToken ct)
    {
        var family = await _database.GetCollection<BsonDocument>(
                FlowFamiliesCollection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", ObjectId.Parse(fixture.FamilyId)))
            .SingleAsync(ct);
        var row = await ReadFlowVersionRowAsync(version.Id, ct);
        HarnessAssert.Equal(version.FamilyId, BsonString(row, "templateId"),
            "Direct Flow templateId mismatch");
        HarnessAssert.Equal(version.VersionNo, BsonInt(row, "versionNo"),
            "Direct Flow versionNo mismatch");
        HarnessAssert.Equal(version.Status, BsonString(row, "status"),
            "Direct Flow status mismatch");
        HarnessAssert.Equal(version.DraftRevision,
            BsonInt(row, "draftRevision"),
            "Direct Flow draftRevision mismatch");
        HarnessAssert.Equal(version.CatalogVersion,
            BsonString(row, "catalogVersion"),
            "Direct Flow catalogVersion mismatch");
        HarnessAssert.Equal(version.CatalogSemanticHash,
            BsonString(row, "catalogSemanticHash"),
            "Direct Flow catalog hash mismatch");
        HarnessAssert.Equal(version.PayloadJson,
            BsonString(row, "payloadJson"),
            "Direct Flow payloadJson mismatch");
        HarnessAssert.Equal(version.PayloadHash,
            BsonString(row, "payloadHash"),
            "Direct Flow payloadHash mismatch");
        HarnessAssert.Equal(version.ContributionPolicy,
            BsonString(row, "contributionPolicy"),
            "Direct Flow contributionPolicy mismatch");
        HarnessAssert.Equal(version.ContributionPolicyHash,
            BsonString(row, "contributionPolicyHash"),
            "Direct Flow contributionPolicyHash mismatch");
        HarnessAssert.Equal(version.ContributionWarning,
            BsonString(row, "contributionWarning"),
            "Direct Flow contributionWarning mismatch");
        HarnessAssert.Equal(fixture.FamilyRevision,
            BsonInt(family, "familyRevision"),
            "Direct Flow familyRevision mismatch");

        var receipt = await _database.GetCollection<BsonDocument>(
                FlowDefinitionReceiptsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("commandId", commandId))
            .SingleAsync(ct);
        HarnessAssert.Equal(actorId, BsonString(receipt, "actorUserId"),
            "Direct Flow receipt actor mismatch");
        HarnessAssert.Equal(commandKind, BsonString(receipt, "commandKind"),
            "Direct Flow receipt commandKind mismatch");
        HarnessAssert.Equal(fixture.FamilyId,
            BsonString(receipt, "familyId"),
            "Direct Flow receipt familyId mismatch");
        HarnessAssert.Equal(version.Id,
            BsonString(receipt, "versionId"),
            "Direct Flow receipt versionId mismatch");
        HarnessAssert.Equal(fixture.FamilyRevision,
            BsonInt(receipt, "resultFamilyRevision"),
            "Direct Flow receipt familyRevision mismatch");
        HarnessAssert.Equal(version.DraftRevision,
            BsonInt(receipt, "resultDraftRevision"),
            "Direct Flow receipt draftRevision mismatch");
        HarnessAssert.Equal(version.PayloadHash,
            BsonString(receipt, "resultPayloadHash"),
            "Direct Flow receipt payloadHash mismatch");
        HarnessAssert.Equal("SUCCEEDED", BsonString(receipt, "outcome"),
            "Direct Flow receipt outcome mismatch");
    }

    private async Task<BsonDocument> ReadFlowVersionRowAsync(
        string versionId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(FlowVersionsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", ObjectId.Parse(versionId)))
            .SingleAsync(ct);
}
