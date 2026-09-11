using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P809DirectMongoBundle> RebuildP809BundleFromMongoAsync(
        JsonArray requestPins,
        CancellationToken ct)
    {
        var pins = new JsonArray();
        if (requestPins.Count > 0)
        {
            HarnessAssert.Equal(8, requestPins.Count,
                "Direct Mongo bundle request is not the exact eight-pin set");
            foreach (var kind in P809DependencyKinds)
            {
                pins.Add(kind switch
                {
                    "LABEL" => await P809DirectLabelPinAsync(
                        P809RequestPin(requestPins, kind), ct),
                    "FIELD" => await P809DirectDynamicFormPinAsync(
                        P809RequestPin(requestPins, kind), true, ct),
                    "TABLE" => await P809DirectDynamicFormPinAsync(
                        P809RequestPin(requestPins, kind), false, ct),
                    "BASIC" => await P809DirectBasicPinAsync(
                        P809RequestPin(requestPins, kind), ct),
                    "ADVANCED" => await P809DirectAdvancedPinAsync(
                        P809RequestPin(requestPins, kind), ct),
                    "DIFF" => await P809DirectDiffPinAsync(
                        P809RequestPin(requestPins, kind), ct),
                    "FLOW_CONTRIBUTION" => await P809DirectFlowPinAsync(
                        P809RequestPin(requestPins, kind), ct),
                    "READINESS" => await P809DirectReadinessPinAsync(
                        P809RequestPin(requestPins, kind), ct),
                    _ => throw new InvalidOperationException(
                        $"Unknown P8-09 direct Mongo kind {kind}.")
                });
            }
        }

        var isEmpty = pins.Count == 0;
        var root = new JsonObject
        {
            ["schemaVersion"] = "P8-BUNDLE-1",
            ["ownerKind"] = _p809Label.OwnerKind,
            ["ownerId"] = _p809Label.OwnerId,
            ["isEmpty"] = isEmpty,
            ["pins"] = pins,
            ["eligibility"] = new JsonObject
            {
                ["configuration"] = isEmpty ? "EMPTY_VALID" : "ELIGIBLE",
                ["futureResult"] = "EMPTY_VALID",
                ["executor"] = "UNSUPPORTED",
                ["targetPhase"] = "P9"
            },
            ["freshness"] = isEmpty ? "EMPTY_VALID" : "FRESH"
        };
        var canonicalJson = Canonicalize(root);
        return new P809DirectMongoBundle(
            root,
            pins,
            canonicalJson,
            Sha256(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private async Task<JsonObject> P809DirectLabelPinAsync(
        JsonObject request,
        CancellationToken ct)
    {
        var document = await P809DirectDocumentAsync(
            LabelsCollection,
            RequiredString(request, "ownerId"),
            ct);
        HarnessAssert.Equal(false, P809BsonBool(document, "isDeleted"),
            "Direct Mongo LABEL is deleted");
        var configHash = P809BsonString(document, "configHash");
        return P809DirectPin(
            "LABEL",
            "LABEL",
            P809BsonString(document, "_id"),
            P809BsonString(document, "configId"),
            P809BsonString(document, "versionId"),
            P809BsonInt(document, "versionNo"),
            P809BsonLong(document, "revision"),
            P809BsonBool(document, "isActive") ? "ACTIVE" : "INACTIVE",
            configHash,
            configHash,
            P809BsonPins(document, "dependencyPins"));
    }

    private async Task<JsonObject> P809DirectDynamicFormPinAsync(
        JsonObject request,
        bool isField,
        CancellationToken ct)
    {
        var document = await P809DirectDocumentAsync(
            DynamicFormsCollection,
            RequiredString(request, "ownerId"),
            ct);
        HarnessAssert.Equal(false, P809BsonBool(document, "isDeleted"),
            "Direct Mongo Dynamic Form is deleted");
        var sections = document.TryGetValue(
                "statisticConfigSections",
                out var sectionValue) &&
            sectionValue.IsBsonDocument
                ? sectionValue.AsBsonDocument
                : throw new InvalidOperationException(
                    "Direct Mongo Dynamic Form lacks statisticConfigSections.");
        var sectionJson = P809BsonString(
            sections,
            isField ? "fieldSectionJson" : "tableSectionJson");
        var dependencies = P809BsonPins(
                document,
                "statisticConfigDependencyPins")
            .Append(
                $"DYNAMIC_FORM_SCHEMA:{P809BsonString(document, "_id")}:" +
                $"{P809BsonInt(document, "versionNo")}:" +
                $"{P809BsonString(document, "publishedSchemaHash")}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return P809DirectPin(
            isField ? "FIELD" : "TABLE",
            "DYNAMIC_FORM",
            P809BsonString(document, "_id"),
            P809BsonString(document, "statisticConfigId"),
            P809BsonString(document, "statisticConfigVersionId"),
            P809BsonInt(document, "statisticConfigVersionNo"),
            P809BsonLong(document, "statisticConfigRevision"),
            P809BsonString(document, "statisticConfigStatus"),
            P809BsonString(document, "statisticConfigHash"),
            P809HashCanonicalJson(sectionJson),
            dependencies);
    }

    private async Task<JsonObject> P809DirectBasicPinAsync(
        JsonObject request,
        CancellationToken ct)
    {
        var document = await P809DirectDocumentAsync(
            BasicConfigsCollection,
            RequiredString(request, "configId"),
            ct);
        HarnessAssert.Equal(false, P809BsonBool(document, "isDeleted"),
            "Direct Mongo BASIC is deleted");
        HarnessAssert.Equal(true, P809BsonBool(document, "isActive"),
            "Direct Mongo BASIC is inactive");
        var configHash = P809BsonString(document, "configHash");
        return P809DirectPin(
            "BASIC",
            "BASIC_SUMMARY",
            $"{P809BsonString(document, "assignmentId")}:" +
            P809BsonString(document, "dynamicFormTemplateId"),
            P809BsonString(document, "_id"),
            P809BsonString(document, "versionId"),
            P809BsonInt(document, "versionNo"),
            P809BsonLong(document, "revision"),
            P809BsonString(document, "status"),
            configHash,
            configHash,
            P809BsonPins(document, "dependencyPins"));
    }

    private async Task<JsonObject> P809DirectAdvancedPinAsync(
        JsonObject request,
        CancellationToken ct)
    {
        var document = await P809DirectDocumentAsync(
            AdvancedConfigsCollection,
            RequiredString(request, "versionId"),
            ct);
        HarnessAssert.Equal(false, P809BsonBool(document, "isDeleted"),
            "Direct Mongo ADVANCED is deleted");
        var configHash = P809BsonString(document, "configHash");
        return P809DirectPin(
            "ADVANCED",
            "ADVANCED_SUMMARY",
            $"{P809BsonString(document, "assignmentId")}:" +
            $"{P809BsonString(document, "dynamicFormTemplateId")}:" +
            P809BsonString(document, "sectionId"),
            P809BsonString(document, "configId"),
            P809BsonString(document, "_id"),
            P809BsonInt(document, "versionNo"),
            P809BsonLong(document, "revision"),
            P809BsonString(document, "status"),
            configHash,
            configHash,
            P809BsonPins(document, "dependencyPins"));
    }

    private async Task<JsonObject> P809DirectDiffPinAsync(
        JsonObject request,
        CancellationToken ct)
    {
        var document = await P809DirectDocumentAsync(
            DiffConfigsCollection,
            RequiredString(request, "versionId"),
            ct);
        HarnessAssert.Equal(false, P809BsonBool(document, "isDeleted"),
            "Direct Mongo DIFF is deleted");
        HarnessAssert.Equal(true, P809BsonBool(document, "isActive"),
            "Direct Mongo DIFF is inactive");
        var configHash = P809BsonString(document, "configHash");
        return P809DirectPin(
            "DIFF",
            "DIFF",
            $"{P809BsonString(document, "assignmentId")}:" +
            P809BsonString(document, "dynamicFormTemplateId"),
            P809BsonString(document, "configId"),
            P809BsonString(document, "_id"),
            P809BsonInt(document, "versionNo"),
            P809BsonLong(document, "revision"),
            P809BsonString(document, "status"),
            configHash,
            configHash,
            P809BsonPins(document, "dependencyPins"));
    }

    private async Task<JsonObject> P809DirectFlowPinAsync(
        JsonObject request,
        CancellationToken ct)
    {
        var version = await P809DirectDocumentAsync(
            FlowVersionsCollection,
            RequiredString(request, "versionId"),
            ct);
        HarnessAssert.Equal(false, P809BsonBool(version, "isDeleted"),
            "Direct Mongo Flow version is deleted");
        var familyId = P809BsonString(version, "templateId");
        var family = await P809DirectDocumentAsync(
            FlowFamiliesCollection,
            familyId,
            ct);
        HarnessAssert.Equal(P809BsonString(version, "_id"),
            P809BsonString(family, "currentVersionId"),
            "Direct Mongo Flow family currentVersionId drifted");
        HarnessAssert.Equal(P809BsonInt(version, "versionNo"),
            P809BsonInt(family, "currentVersionNo"),
            "Direct Mongo Flow family currentVersionNo drifted");
        HarnessAssert.Equal(P809BsonString(version, "payloadHash"),
            P809BsonString(family, "currentVersionHash"),
            "Direct Mongo Flow family currentVersionHash drifted");

        var payloadJson = P809BsonString(version, "payloadJson");
        HarnessAssert.Equal(
            P809BsonString(version, "payloadHash"),
            Sha256(Encoding.UTF8.GetBytes(payloadJson)),
            "Direct Mongo Flow payload hash drifted");
        HarnessAssert.Equal(
            P809BsonString(version, "contributionPolicyHash"),
            P809FlowContributionHash(version),
            "Direct Mongo Flow contribution policy hash drifted");
        var dependencies = new[]
        {
            $"CATALOG_SEMANTIC_HASH:{P809BsonString(version, "catalogSemanticHash")}",
            $"CATALOG_VERSION:{P809BsonString(version, "catalogVersion")}",
            $"PAYLOAD_HASH:{P809BsonString(version, "payloadHash")}"
        }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return P809DirectPin(
            "FLOW_CONTRIBUTION",
            "FLOW_CONTRIBUTION",
            familyId,
            familyId,
            P809BsonString(version, "_id"),
            P809BsonInt(version, "versionNo"),
            P809BsonLong(version, "draftRevision"),
            P809BsonString(version, "status"),
            P809BsonString(version, "payloadHash"),
            P809BsonString(version, "contributionPolicyHash"),
            dependencies);
    }

    private async Task<JsonObject> P809DirectReadinessPinAsync(
        JsonObject request,
        CancellationToken ct)
    {
        var versionId = RequiredString(request, "versionId");
        var job = await _database
            .GetCollection<StatConfigValidationJob>(P808JobsCollection)
            .Find(item => item.Id == versionId && !item.IsDeleted)
            .SingleAsync(ct);
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            StatConfigOperationsService.EnqueueCommandKind,
            StatConfigOperationsService.ResetCommandKind,
            StatConfigOperationsService.CancelCommandKind,
            StatConfigOperationsService.CleanupCommandKind
        };
        var receipts = await _database
            .GetCollection<StatConfigCommandReceipt>(ReceiptsCollection)
            .Find(item =>
                item.OwnerKind == job.OwnerKind &&
                item.OwnerId == job.OwnerId &&
                item.ResultConfigId == job.ConfigId &&
                item.ResultVersionId == job.VersionId &&
                item.ResultVersionNo == job.VersionNo &&
                item.ResultRevision == job.ConfigRevision &&
                item.ResultConfigHash == job.ConfigHash)
            .ToListAsync(ct);
        var source = receipts
            .Where(item => !excluded.Contains(item.CommandKind))
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Direct Mongo READINESS lacks exact source config receipt.");
        HarnessAssert.Equal(
            source.ResponseHash,
            Sha256(Encoding.UTF8.GetBytes(source.ResponseJson)),
            "Direct Mongo READINESS source responseHash drifted");
        var sourcePins = P809FindDependencyPins(
            JsonNode.Parse(source.ResponseJson)
            ?? throw new InvalidOperationException(
                "Direct Mongo READINESS source response is null."));
        var storedPins = job.DependencyPins
            .Select(value => value.Trim())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        HarnessAssert.True(
            storedPins.SequenceEqual(sourcePins, StringComparer.Ordinal),
            "Direct Mongo READINESS dependency pins differ from source receipt");

        var dependencyPinsNode = new JsonArray(
            sourcePins.Select(value => JsonValue.Create(value)).ToArray());
        var dependencyPinsHash = P809HashNode(dependencyPinsNode);
        HarnessAssert.Equal(job.DependencyPinsHash, dependencyPinsHash,
            "Direct Mongo READINESS dependencyPinsHash drifted");
        var bundleHash = P809HashNode(new JsonObject
        {
            ["ownerKind"] = job.OwnerKind,
            ["ownerId"] = job.OwnerId,
            ["configId"] = source.ResultConfigId,
            ["versionId"] = source.ResultVersionId,
            ["versionNo"] = source.ResultVersionNo,
            ["configRevision"] = source.ResultRevision,
            ["configHash"] = source.ResultConfigHash,
            ["dependencyPinsHash"] = dependencyPinsHash
        });
        HarnessAssert.Equal(job.BundleHash, bundleHash,
            "Direct Mongo READINESS bundleHash drifted");
        var stateNode = JsonSerializer.SerializeToNode(
            new
            {
                job.Id,
                job.QueueName,
                job.Status,
                job.IsActive,
                job.StateRevision,
                job.RetryCount,
                job.MaxRetryCount,
                job.NextRetryAtUtc,
                job.LastRunAtUtc,
                job.CompletedAtUtc,
                job.FailedAtUtc,
                job.DeadLetterAtUtc,
                job.CancelledAtUtc,
                job.ResetAtUtc,
                job.ResetCount,
                job.SafeCode,
                job.FailureFingerprint
            },
            StatConfigCanonicalJson.StrictJsonOptions)
            ?? throw new InvalidOperationException(
                "Direct Mongo READINESS state serialization returned null.");
        var stateHash = P809HashNode(stateNode);
        HarnessAssert.Equal(job.StateHash, stateHash,
            "Direct Mongo READINESS stateHash drifted");
        var contributionHash = P809HashNode(new JsonObject
        {
            ["bundleHash"] = bundleHash,
            ["dependencyPinsHash"] = dependencyPinsHash,
            ["stateHash"] = stateHash
        });
        return P809DirectPin(
            "READINESS",
            job.OwnerKind,
            job.OwnerId,
            job.ConfigId,
            job.Id,
            job.VersionNo,
            job.StateRevision,
            job.Status,
            job.ConfigHash,
            contributionHash,
            storedPins);
    }

    private static JsonObject P809DirectPin(
        string kind,
        string ownerKind,
        string ownerId,
        string configId,
        string versionId,
        int versionNo,
        long revision,
        string status,
        string configHash,
        string contributionHash,
        IEnumerable<string> dependencyPins)
        => new()
        {
            ["kind"] = kind,
            ["ownerKind"] = ownerKind,
            ["ownerId"] = ownerId,
            ["configId"] = configId,
            ["versionId"] = versionId,
            ["versionNo"] = versionNo,
            ["revision"] = revision,
            ["status"] = status,
            ["configHash"] = configHash,
            ["contributionHash"] = contributionHash,
            ["dependencyPins"] = new JsonArray(
                dependencyPins.Select(value => JsonValue.Create(value)).ToArray())
        };

    private async Task<BsonDocument> P809DirectDocumentAsync(
        string collection,
        string id,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(collection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id",
                ObjectId.Parse(id)))
            .SingleAsync(ct);

    private static JsonObject P809RequestPin(JsonArray pins, string kind)
        => pins.OfType<JsonObject>().Single(pin =>
            string.Equals(RequiredString(pin, "kind"), kind, StringComparison.Ordinal));

    private static string[] P809BsonPins(
        BsonDocument document,
        string name)
    {
        if (!document.TryGetValue(name, out var value) || !value.IsBsonArray)
            throw new InvalidOperationException(
                $"Direct Mongo document lacks canonical {name} array.");
        var source = value.AsBsonArray
            .Select(item => item.AsString)
            .ToArray();
        var canonical = source
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        HarnessAssert.True(
            source.SequenceEqual(canonical, StringComparer.Ordinal),
            $"Direct Mongo {name} is not canonical");
        return canonical;
    }

    private static string P809BsonString(BsonDocument document, string name)
        => BsonString(document, name)
           ?? throw new InvalidOperationException(
               $"Direct Mongo document lacks {name}.");

    private static int P809BsonInt(BsonDocument document, string name)
        => BsonInt(document, name)
           ?? throw new InvalidOperationException(
               $"Direct Mongo document lacks numeric {name}.");

    private static long P809BsonLong(BsonDocument document, string name)
        => BsonLong(document, name)
           ?? throw new InvalidOperationException(
               $"Direct Mongo document lacks numeric {name}.");

    private static bool P809BsonBool(BsonDocument document, string name)
        => BsonBool(document, name)
           ?? throw new InvalidOperationException(
               $"Direct Mongo document lacks boolean {name}.");

    private static string P809HashCanonicalJson(string json)
        => P809HashNode(
            JsonNode.Parse(json)
            ?? throw new InvalidOperationException(
                "Direct Mongo canonical JSON is null."));

    private static string P809HashNode(JsonNode node)
        => Sha256(Encoding.UTF8.GetBytes(Canonicalize(node)));

    private static string[] P809FindDependencyPins(JsonNode node)
    {
        var found = P809FindArray(node, "dependencyPins");
        return found?.OfType<JsonValue>()
            .Select(value => value.GetValue<string>().Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static JsonArray? P809FindArray(JsonNode? node, string name)
    {
        if (node is JsonObject obj)
        {
            if (obj[name] is JsonArray direct)
                return direct;
            foreach (var pair in obj)
            {
                var nested = P809FindArray(pair.Value, name);
                if (nested is not null)
                    return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = P809FindArray(item, name);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static string P809FlowContributionHash(BsonDocument version)
    {
        var policy = P809BsonString(version, "contributionPolicy");
        var warning = string.Equals(policy, "INCLUDE", StringComparison.Ordinal)
            ? "DYNAMIC_FLOW_STATISTIC_CONTRIBUTION_INCLUDE_WARNING"
            : string.Empty;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("adapterVersion", P809BsonInt(version, "adapterVersion"));
            writer.WriteString("catalogSemanticHash", P809BsonString(version, "catalogSemanticHash"));
            writer.WriteString("catalogVersion", P809BsonString(version, "catalogVersion"));
            writer.WriteString("contributionPolicy", policy);
            writer.WriteString("originFamilyId", BsonString(version, "originFamilyId") ?? string.Empty);
            writer.WriteString("originVersionId", BsonString(version, "originVersionId") ?? string.Empty);
            writer.WriteString("payloadHash", P809BsonString(version, "payloadHash"));
            writer.WriteString("payloadJsonSha256",
                Sha256(Encoding.UTF8.GetBytes(P809BsonString(version, "payloadJson"))));
            writer.WriteString("rootDynamicFormTemplateId",
                BsonString(version, "rootDynamicFormTemplateId") ?? string.Empty);
            writer.WriteNumber("schemaVersion", P809BsonInt(version, "schemaVersion"));
            writer.WriteString("templateId", P809BsonString(version, "templateId"));
            writer.WriteString("versionId", P809BsonString(version, "_id"));
            writer.WriteNumber("versionNo", P809BsonInt(version, "versionNo"));
            writer.WriteString("warning", warning);
            writer.WriteEndObject();
        }
        return Sha256(stream.ToArray());
    }
}

internal sealed record P809DirectMongoBundle(
    JsonObject Root,
    JsonArray Pins,
    string CanonicalJson,
    string BundleHash);
