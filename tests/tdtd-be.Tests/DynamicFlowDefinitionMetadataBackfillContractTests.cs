using MongoDB.Bson;
using MongoObjectId = MongoDB.Bson.ObjectId;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Indexes;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowDefinitionMetadataBackfillContractTests
{
    private const string FamilyId = "64b64c10aafbc4a5ec004001";
    private const string VersionId = "64b64c10aafbc4a5ec004002";
    private const string CreatorId = "64b64c10aafbc4a5ec004003";
    private const string FormId = "64b64c10aafbc4a5ec004004";
    private const string FormFamilyId = "64b64c10aafbc4a5ec004005";
    private static readonly string FormHash = new('a', 64);

    public static void Run()
    {
        StartupPagesAreBounded();
        FamilyFallbacksAndArchiveAuditConverge();
        MutableLegacyDraftIsCanonicalizedOnceButRequiresReviewWithoutPins();
        UnprovenLockedLegacyBytesAreNeverRewrittenOrRehashed();
        ExistingPinsAreNotTrustedWithoutExternalSnapshotProof();
        ProvenCanonicalLockedSnapshotBackfillsOnlyMetadata();
        LockedHashMismatchFailsClosedEvenWithExternalPinEvidence();
        CompareAndSetPinsObservedPayloadAndMissingMetadata();
        ManifestStateMachinePinsPhaseAndOrderIndependentHashes();
        OversizedPayloadManifestFallsBackWithoutRewritingPayload();
        ReviewRequiredReadbackPreservesAccessToMalformedLegacyPayload();
    }

    private static void StartupPagesAreBounded()
    {
        var batchSize = (int)(typeof(DynamicFlowDefinitionMetadataBackfill)
            .GetField(
                nameof(DynamicFlowDefinitionMetadataBackfill.BatchSize),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Dynamic Flow backfill batch size is missing."));
        AssertTrue(batchSize is > 0 and <= 50, "Flow payloads must migrate in bounded pages");
    }

    private static void FamilyFallbacksAndArchiveAuditConverge()
    {
        var updatedAt = new DateTime(2026, 7, 20, 2, 3, 4, DateTimeKind.Utc);
        var document = new BsonDocument
        {
            { "_id", MongoObjectId.Parse(FamilyId) },
            { "createdByUserId", MongoObjectId.Parse(CreatorId) },
            { "updatedByUserId", MongoObjectId.Parse(CreatorId) },
            { "dynamicFormTemplateId", MongoObjectId.Parse(FormId) },
            { "status", DynamicFlowTemplateStatuses.Archived },
            { "updatedAtUtc", updatedAt }
        };

        var first = DynamicFlowDefinitionMetadataBackfill.PlanFamilyPatch(
            document,
            hasImmutableVersion: true);
        AssertEqual(1, first.Set["familyRevision"].AsInt32, "family revision fallback");
        AssertEqual(CreatorId, first.Set["ownerUserId"].AsObjectId.ToString(), "immutable owner fallback");
        AssertEqual(FormId, first.Set["rootDynamicFormTemplateId"].AsObjectId.ToString(), "legacy root Form fallback");
        AssertTrue(first.Set["originFamilyId"].IsBsonNull, "root family origin family must be explicit null");
        AssertTrue(first.Set["originVersionId"].IsBsonNull, "root family origin version must be explicit null");
        AssertTrue(first.Set["hasLockedVersion"].AsBoolean, "locked-version guard");
        AssertEqual(updatedAt, first.Set["archivedAtUtc"].ToUniversalTime(), "archive timestamp fallback");
        AssertEqual(CreatorId, first.Set["archivedByUserId"].AsObjectId.ToString(), "archive actor fallback");

        document.Merge(first.Set, overwriteExistingElements: true);
        AssertFalse(
            DynamicFlowDefinitionMetadataBackfill.PlanFamilyPatch(document, true).HasChanges,
            "second family migration run must be a no-op");
    }

    private static void MutableLegacyDraftIsCanonicalizedOnceButRequiresReviewWithoutPins()
    {
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Draft,
            LegacyPayload(),
            payloadHash: null);
        var expected = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(LegacyPayload());

        var first = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: false);
        AssertTrue(first.RewritesPayload, "mutable legacy draft should use the deterministic v1 adapter");
        AssertEqual(expected.CanonicalJson, first.Set["payloadJson"].AsString, "canonical draft bytes");
        AssertEqual(expected.PayloadHash, first.Set["payloadHash"].AsString, "canonical draft hash");
        AssertEqual(2, first.Set["schemaVersion"].AsInt32, "canonical schema version");
        AssertEqual(1, first.Set["adapterVersion"].AsInt32, "legacy adapter version");
        AssertEqual(
            DynamicFlowDefinitionMigrationStates.RequiresReview,
            first.Set["migrationState"].AsString,
            "unpinned draft review state");
        AssertFalse(first.Set["definitionLockable"].AsBoolean, "migration must not silently make a draft lockable");

        document.Merge(first.Set, overwriteExistingElements: true);
        var second = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: false);
        AssertFalse(second.HasChanges, "second draft migration run must preserve canonical hash and bytes");
    }

    private static void UnprovenLockedLegacyBytesAreNeverRewrittenOrRehashed()
    {
        var legacy = LegacyPayload();
        var legacyHash = Sha256(legacy);
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Locked,
            legacy,
            legacyHash);

        var first = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: false);
        AssertFalse(first.Set.Contains("payloadJson"), "locked legacy bytes must remain immutable");
        AssertFalse(first.Set.Contains("payloadHash"), "locked legacy hash must not be replaced with a newly trusted hash");
        AssertEqual(
            DynamicFlowDefinitionMigrationStates.RequiresReview,
            first.Set["migrationState"].AsString,
            "unproven locked migration state");
        AssertFalse(first.Set["definitionLockable"].AsBoolean, "unproven locked definition must be disabled");
        AssertEqual(
            DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
            first.Set["executionEligibility"].AsString,
            "unproven locked runtime eligibility");

        document.Merge(first.Set, overwriteExistingElements: true);
        var second = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: false);
        AssertFalse(second.HasChanges, "second locked legacy run must be a no-op");
        AssertEqual(legacy, document["payloadJson"].AsString, "locked legacy bytes after second run");
        AssertEqual(legacyHash, document["payloadHash"].AsString, "locked legacy hash after second run");
    }

    private static void ExistingPinsAreNotTrustedWithoutExternalSnapshotProof()
    {
        var canonical = PinnedCanonicalPayload();
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Locked,
            canonical.CanonicalJson,
            canonical.PayloadHash);

        var patch = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: false);
        AssertEqual(
            DynamicFlowDefinitionMigrationStates.RequiresReview,
            patch.Set["migrationState"].AsString,
            "stored pins alone cannot prove a locked definition");
        AssertFalse(patch.Set["definitionLockable"].AsBoolean, "unverified pins cannot enable lockability");
        AssertFalse(patch.Set.Contains("catalogVersion"), "unverified catalog pin must not be copied to metadata");
        AssertFalse(patch.Set.Contains("catalogSemanticHash"), "unverified catalog hash must not be copied to metadata");
    }

    private static void ProvenCanonicalLockedSnapshotBackfillsOnlyMetadata()
    {
        var canonical = PinnedCanonicalPayload();
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Locked,
            canonical.CanonicalJson,
            canonical.PayloadHash);

        var first = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: true);
        AssertFalse(first.Set.Contains("payloadJson"), "proven locked canonical bytes stay immutable");
        AssertFalse(first.Set.Contains("payloadHash"), "proven locked canonical hash stays immutable");
        AssertEqual(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            first.Set["catalogVersion"].AsString,
            "catalog version copied only after proof");
        AssertEqual(
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            first.Set["catalogSemanticHash"].AsString,
            "catalog semantic hash copied only after proof");
        AssertEqual(
            DynamicFlowDefinitionMigrationStates.Canonical,
            first.Set["migrationState"].AsString,
            "proven locked state");
        AssertTrue(first.Set["definitionLockable"].AsBoolean, "proven locked definition remains lockable");

        document.Merge(first.Set, overwriteExistingElements: true);
        var second = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: true);
        AssertFalse(second.HasChanges, "second proven snapshot run must be a no-op");
        AssertEqual(canonical.PayloadHash, document["payloadHash"].AsString, "proven snapshot stable hash");
    }

    private static void LockedHashMismatchFailsClosedEvenWithExternalPinEvidence()
    {
        var canonical = PinnedCanonicalPayload();
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Locked,
            canonical.CanonicalJson,
            new string('0', 64));

        var patch = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: true);
        AssertEqual(
            DynamicFlowDefinitionMigrationStates.RequiresReview,
            patch.Set["migrationState"].AsString,
            "hash mismatch must override otherwise-valid pin evidence");
        AssertFalse(patch.Set["definitionLockable"].AsBoolean, "hash mismatch runtime guard");
        AssertFalse(patch.Set.Contains("payloadHash"), "migration must not repair and trust an immutable hash mismatch");
    }

    private static void CompareAndSetPinsObservedPayloadAndMissingMetadata()
    {
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Draft,
            LegacyPayload(),
            payloadHash: null);
        var observed = new[] { "payloadJson", "payloadHash", "migrationState" };
        var filter = DynamicFlowDefinitionMetadataBackfill.BuildCompareAndSetFilterDocument(document, observed);
        var clauses = filter["$and"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray();

        AssertTrue(
            clauses.Any(clause => clause.TryGetValue("payloadJson", out var value) && value == document["payloadJson"]),
            "CAS must pin the payload bytes used for hashing");
        AssertTrue(
            clauses.Any(clause =>
                clause.TryGetValue("payloadHash", out var value) &&
                value.IsBsonDocument &&
                value.AsBsonDocument["$exists"] == false),
            "CAS must pin an observed missing payload hash");
        AssertTrue(
            clauses.Any(clause =>
                clause.TryGetValue("migrationState", out var value) &&
                value.IsBsonDocument &&
                value.AsBsonDocument["$exists"] == false),
            "CAS must pin an observed missing migration state");
    }

    private static void ManifestStateMachinePinsPhaseAndOrderIndependentHashes()
    {
        AssertEqual(1, DynamicFlowDefinitionMetadataBackfill.FamilyPhase, "family migration phase");
        AssertEqual(2, DynamicFlowDefinitionMetadataBackfill.VersionPhase, "version migration phase");
        AssertTrue(
            DynamicFlowDefinitionMetadataBackfill.MaxManifestBsonBytes < 16 * 1024 * 1024,
            "manifest preflight must stay conservatively below MongoDB's BSON document limit");

        var observed = new BsonDocument
        {
            { "_id", MongoObjectId.Parse(VersionId) },
            { "z", new BsonDocument { { "beta", 2 }, { "alpha", 1 } } },
            { "a", new BsonArray { new BsonDocument { { "y", 2 }, { "x", 1 } } } }
        };
        var reordered = new BsonDocument
        {
            { "a", new BsonArray { new BsonDocument { { "x", 1 }, { "y", 2 } } } },
            { "z", new BsonDocument { { "alpha", 1 }, { "beta", 2 } } },
            { "_id", MongoObjectId.Parse(VersionId) }
        };
        AssertEqual(
            DynamicFlowDefinitionMetadataBackfill.ComputeFieldOrderIndependentSha256(observed),
            DynamicFlowDefinitionMetadataBackfill.ComputeFieldOrderIndependentSha256(reordered),
            "observed BSON hash must ignore document field order while preserving values");

        var set = new BsonDocument
        {
            { "z", new BsonDocument { { "delta", 4 }, { "gamma", 3 } } },
            { "newField", true }
        };
        var preparedAt = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc);
        var manifest = DynamicFlowDefinitionMetadataBackfill.BuildRollbackManifestDocument(
            "dynamic_flow_template_versions",
            DynamicFlowDefinitionMetadataBackfill.VersionPhase,
            observed,
            set,
            preparedAt);
        AssertEqual(
            DynamicFlowDefinitionMetadataBackfill.ManifestPrepared,
            manifest["state"].AsString,
            "manifest initial state");
        AssertEqual(
            DynamicFlowDefinitionMetadataBackfill.VersionPhase,
            manifest["phase"].AsInt32,
            "manifest phase");
        AssertEqual(
            manifest["beforeObservedSha256"].AsString,
            manifest["observedSha256"].AsString,
            "compatibility preimage hash alias");
        AssertSequenceEqual(
            new[] { "newField", "z" },
            manifest["beforeFields"].AsBsonDocument.Names.ToArray(),
            "manifest field order must be deterministic");

        var expectedAfter = observed.DeepClone().AsBsonDocument;
        expectedAfter.Merge(set, overwriteExistingElements: true);
        AssertEqual(
            DynamicFlowDefinitionMetadataBackfill.ComputeFieldOrderIndependentSha256(expectedAfter),
            manifest["afterObservedSha256"].AsString,
            "manifest postimage hash");
    }

    private static void OversizedPayloadManifestFallsBackWithoutRewritingPayload()
    {
        var paddedPayload = new string(' ', 50_000) + LegacyPayload();
        var document = VersionDocument(
            DynamicFlowTemplateVersionStatuses.Draft,
            paddedPayload,
            payloadHash: null);
        var patch = DynamicFlowDefinitionMetadataBackfill.PlanVersionPatch(
            document,
            FormId,
            DynamicFlowMigrationLineageEvidence.Root,
            pinnedDefinitionProven: false);
        AssertTrue(patch.RewritesPayload, "padded legacy draft must initially plan a canonical rewrite");

        const int testManifestLimit = 16_000;
        var bounded = DynamicFlowDefinitionMetadataBackfill.ConstrainVersionPatchForManifest(
            "dynamic_flow_template_versions",
            document,
            patch,
            testManifestLimit);
        AssertFalse(bounded.Contains("payloadJson"), "oversized manifest fallback must preserve payload bytes");
        AssertEqual(
            DynamicFlowDefinitionMigrationStates.RequiresReview,
            bounded["migrationState"].AsString,
            "oversized manifest fallback review state");
        AssertFalse(bounded["definitionLockable"].AsBoolean, "oversized fallback must remain fail closed");
        AssertEqual(
            Sha256(paddedPayload),
            bounded["payloadHash"].AsString,
            "oversized fallback must hash the exact preserved payload bytes");

        var boundedManifest = DynamicFlowDefinitionMetadataBackfill.BuildRollbackManifestDocument(
            "dynamic_flow_template_versions",
            DynamicFlowDefinitionMetadataBackfill.VersionPhase,
            document,
            bounded,
            DateTime.UnixEpoch);
        AssertTrue(
            boundedManifest.ToBson().Length < testManifestLimit,
            "fallback manifest must pass the configured BSON preflight");
    }

    private static void ReviewRequiredReadbackPreservesAccessToMalformedLegacyPayload()
    {
        var reviewed = DynamicFlowTemplatePayloadContract.ReadCanonical(
            "{not-json",
            FormId,
            tolerateRequiresReview: true);
        AssertEqual(0, reviewed.SchemaVersion, "malformed review envelope schema marker");
        AssertEqual(FormId, reviewed.RootDynamicFormTemplateId!, "malformed review envelope root Form");

        AssertThrows<AppException>(() => DynamicFlowTemplatePayloadContract.ReadCanonical(
            "{not-json",
            FormId,
            tolerateRequiresReview: false));
    }

    private static BsonDocument VersionDocument(string status, string payloadJson, string? payloadHash)
    {
        var document = new BsonDocument
        {
            { "_id", MongoObjectId.Parse(VersionId) },
            { "templateId", MongoObjectId.Parse(FamilyId) },
            { "rootDynamicFormTemplateId", MongoObjectId.Parse(FormId) },
            { "versionNo", 1 },
            { "status", status },
            { "payloadJson", payloadJson },
            { "createdByUserId", MongoObjectId.Parse(CreatorId) },
            { "updatedByUserId", MongoObjectId.Parse(CreatorId) },
            { "updatedAtUtc", new DateTime(2026, 7, 20, 2, 3, 4, DateTimeKind.Utc) },
            { "isDeleted", false }
        };
        if (payloadHash is not null)
            document["payloadHash"] = payloadHash;
        return document;
    }

    private static string LegacyPayload()
        => $$"""
        {
          "schemaVersion": 1,
          "rootDynamicFormTemplateId": "{{FormId}}",
          "formNodes": [
            {"formNodeId":"form-a","role":"OWNER","dynamicFormTemplateId":"{{FormId}}"}
          ],
          "steps": [
            {"stepId":"step-a","stepCode":"START","stepName":"Start","formNodeId":"form-a"}
          ],
          "transitions": []
        }
        """;

    private static DynamicFlowCanonicalPayload PinnedCanonicalPayload()
    {
        var json = $$"""
        {
          "schemaVersion": 2,
          "archetypeId": "FLOW-T01",
          "entryStepId": "step-a",
          "rootDynamicFormTemplateId": "{{FormId}}",
          "catalogVersion": "{{DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion}}",
          "catalogSemanticHash": "{{DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256}}",
          "formNodes": [
            {
              "formNodeId":"form-a",
              "role":"OWNER",
              "dynamicFormTemplateId":"{{FormId}}",
              "dynamicFormFamilyId":"{{FormFamilyId}}",
              "dynamicFormVersionNo":1,
              "dynamicFormSchemaHash":"{{FormHash}}",
              "dynamicFormSnapshotHash":"{{FormHash}}"
            }
          ],
          "nodes": [
            {"nodeId":"step-a","nodeCode":"START","nodeKind":"FORM_STEP","formNodeId":"form-a","declaredRoles":["OWNER"]}
          ],
          "edges": [],
          "actorPolicies": [],
          "fieldPolicies": [],
          "tableColumnPolicies": [],
          "mappingRules": [],
          "rollbackPolicy": {},
          "finalResultPolicy": {},
          "statisticProfile": {}
        }
        """;
        return DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            json,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true));
    }

    private static string Sha256(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertSequenceEqual<T>(
        IReadOnlyCollection<T> expected,
        IReadOnlyCollection<T> actual,
        string context)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected '[{string.Join(",", expected)}]', " +
                $"got '[{string.Join(",", actual)}]'.");
        }
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message)
        => AssertTrue(!value, message);
}
