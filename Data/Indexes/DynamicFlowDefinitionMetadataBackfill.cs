using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Data.Indexes;

/// <summary>
/// Bounded, idempotent compatibility migration for the P4 Dynamic Flow
/// family/version split. Mutable drafts may be deterministically adapted to
/// schema v2. Immutable legacy snapshots are never rewritten or implicitly
/// trusted: they become review-required unless their existing canonical bytes,
/// hash, catalog pin, Form-version pins, and policy coverage are all proven.
/// </summary>
internal static class DynamicFlowDefinitionMetadataBackfill
{
    internal const int BatchSize = 25;
    internal const int FamilyPhase = 1;
    internal const int VersionPhase = 2;
    internal const int MaxManifestBsonBytes = 14 * 1024 * 1024;
    internal const int ManifestTransitionReserveBytes = 1024;
    internal const string MigrationId = "P4_DYNAMIC_FLOW_DEFINITION_METADATA_V1";
    internal const string RollbackManifestCollectionName =
        "dynamic_flow_definition_metadata_backfill_manifests";
    internal const string ManifestPrepared = "PREPARED";
    internal const string ManifestApplied = "APPLIED";
    internal const string ManifestRolledBack = "ROLLED_BACK";

    private const int TransactionRetryLimit = 3;

    private static readonly TransactionOptions MigrationTransactionOptions = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    private static readonly BsonDocument FamilyProjection = new()
    {
        { "_id", 1 },
        { "familyRevision", 1 },
        { "ownerUserId", 1 },
        { "createdByUserId", 1 },
        { "updatedByUserId", 1 },
        { "rootDynamicFormTemplateId", 1 },
        { "dynamicFormTemplateId", 1 },
        { "originFamilyId", 1 },
        { "originVersionId", 1 },
        { "status", 1 },
        { "currentVersionId", 1 },
        { "currentVersionNo", 1 },
        { "currentVersionHash", 1 },
        { "hasLockedVersion", 1 },
        { "archivedAtUtc", 1 },
        { "archivedByUserId", 1 },
        { "updatedAtUtc", 1 }
    };

    private static readonly BsonDocument VersionProjection = new()
    {
        { "_id", 1 },
        { "templateId", 1 },
        { "versionNo", 1 },
        { "status", 1 },
        { "draftRevision", 1 },
        { "schemaVersion", 1 },
        { "adapterVersion", 1 },
        { "catalogVersion", 1 },
        { "catalogSemanticHash", 1 },
        { "rootDynamicFormTemplateId", 1 },
        { "dynamicFormTemplateId", 1 },
        { "originFamilyId", 1 },
        { "originVersionId", 1 },
        { "payloadJson", 1 },
        { "payloadHash", 1 },
        { "definitionLockable", 1 },
        { "executionEligibility", 1 },
        { "executionBlockedReason", 1 },
        { "blockedUntilPhase", 1 },
        { "migrationState", 1 },
        { "lockedAtUtc", 1 },
        { "lockedByUserId", 1 },
        { "archivedAtUtc", 1 },
        { "archivedByUserId", 1 },
        { "createdByUserId", 1 },
        { "updatedByUserId", 1 },
        { "updatedAtUtc", 1 },
        { "isDeleted", 1 }
    };

    private static readonly string[] FamilyObservedFields = FamilyProjection.Names
        .Where(name => name != "_id")
        .ToArray();

    private static readonly string[] VersionObservedFields = VersionProjection.Names
        .Where(name => name != "_id")
        .ToArray();

    private static readonly BsonDocument FamilyCandidateFilter = new("$or", new BsonArray
    {
        MissingOrNull("familyRevision"),
        new BsonDocument("familyRevision", new BsonDocument("$lte", 0)),
        MissingOrNull("ownerUserId"),
        new BsonDocument("ownerUserId", string.Empty),
        Missing("rootDynamicFormTemplateId"),
        Missing("originFamilyId"),
        Missing("originVersionId"),
        Missing("hasLockedVersion"),
        new BsonDocument("status", DynamicFlowTemplateStatuses.Archived)
    });

    private static readonly BsonDocument VersionCandidateFilter = new("$or", new BsonArray
    {
        MissingOrNull("draftRevision"),
        new BsonDocument("draftRevision", new BsonDocument("$lte", 0)),
        MissingOrNull("schemaVersion"),
        new BsonDocument("schemaVersion", new BsonDocument("$lte", 0)),
        MissingOrNull("adapterVersion"),
        new BsonDocument("adapterVersion", new BsonDocument("$lte", 0)),
        Missing("rootDynamicFormTemplateId"),
        Missing("originFamilyId"),
        Missing("originVersionId"),
        MissingOrNull("payloadHash"),
        new BsonDocument("payloadHash", string.Empty),
        Missing("definitionLockable"),
        MissingOrNull("executionEligibility"),
        MissingOrNull("executionBlockedReason"),
        Missing("blockedUntilPhase"),
        MissingOrNull("migrationState"),
        new BsonDocument("status", new BsonDocument("$in", new BsonArray
        {
            DynamicFlowTemplateVersionStatuses.Locked,
            DynamicFlowTemplateVersionStatuses.Archived
        }))
    });

    internal static async Task RunAsync(
        IMongoDatabase database,
        MongoOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        var families = database.GetCollection<BsonDocument>(options.DynamicFlowTemplateCollection);
        var versions = database.GetCollection<BsonDocument>(options.DynamicFlowTemplateVersionCollection);
        var forms = database.GetCollection<DynamicFormTemplate>(options.DynamicFormTemplateCollection);
        var rollbackManifests = database.GetCollection<BsonDocument>(RollbackManifestCollectionName);

        // Family roots and immutable owners must exist before version evidence
        // is evaluated, and all of this must converge before P4 indexes enforce
        // the new shape.
        await BackfillFamiliesAsync(
            database.Client,
            families,
            versions,
            rollbackManifests,
            cancellationToken);
        await BackfillVersionsAsync(
            database.Client,
            families,
            versions,
            forms,
            rollbackManifests,
            cancellationToken);
    }

    private static async Task BackfillFamiliesAsync(
        IMongoClient client,
        IMongoCollection<BsonDocument> families,
        IMongoCollection<BsonDocument> versions,
        IMongoCollection<BsonDocument> rollbackManifests,
        CancellationToken cancellationToken)
    {
        BsonValue? lastId = null;
        while (true)
        {
            var page = await ReadPageAsync(
                families,
                FamilyCandidateFilter,
                new BsonDocument("_id", 1),
                lastId,
                cancellationToken);
            if (page.Count == 0)
                return;

            foreach (var candidate in page)
            {
                var documentId = candidate["_id"].DeepClone();
                await ExecuteOneDocumentTransactionAsync(
                    client,
                    async (session, transactionCt) =>
                    {
                        var document = await families
                            .Find(session, new BsonDocument("_id", documentId))
                            .Project<BsonDocument>(FamilyProjection)
                            .FirstOrDefaultAsync(transactionCt);
                        if (document is null)
                            return;

                        var evidence = await LoadFamilyEvidenceAsync(
                            session,
                            versions,
                            new[] { document },
                            transactionCt);
                        var id = ReadRequiredObjectId(document, "_id", "Dynamic Flow family");
                        evidence.TryGetValue(id, out var item);
                        var patch = PlanFamilyPatch(
                            document,
                            item?.HasImmutableVersion ?? false,
                            item?.RootDynamicFormTemplateId);
                        if (!patch.HasChanges)
                        {
                            await VerifyAppliedManifestIfPresentAsync(
                                session,
                                rollbackManifests,
                                families.CollectionNamespace.CollectionName,
                                FamilyPhase,
                                document,
                                transactionCt);
                            return;
                        }

                        await ApplyPatchWithManifestAsync(
                            session,
                            families,
                            rollbackManifests,
                            FamilyPhase,
                            document,
                            FamilyObservedFields,
                            patch.Set,
                            transactionCt);
                    },
                    cancellationToken);
            }

            lastId = page[^1]["_id"];
        }
    }

    private static async Task BackfillVersionsAsync(
        IMongoClient client,
        IMongoCollection<BsonDocument> families,
        IMongoCollection<BsonDocument> versions,
        IMongoCollection<DynamicFormTemplate> forms,
        IMongoCollection<BsonDocument> rollbackManifests,
        CancellationToken cancellationToken)
    {
        BsonValue? lastId = null;
        while (true)
        {
            var page = await ReadPageAsync(
                versions,
                VersionCandidateFilter,
                new BsonDocument("_id", 1),
                lastId,
                cancellationToken);
            if (page.Count == 0)
                return;

            foreach (var candidate in page)
            {
                var documentId = candidate["_id"].DeepClone();
                await ExecuteOneDocumentTransactionAsync(
                    client,
                    async (session, transactionCt) =>
                    {
                        var document = await versions
                            .Find(session, new BsonDocument("_id", documentId))
                            .Project<BsonDocument>(VersionProjection)
                            .FirstOrDefaultAsync(transactionCt);
                        if (document is null)
                            return;

                        var familyRoots = await LoadFamilyRootsAsync(
                            session,
                            families,
                            new[] { document },
                            transactionCt);
                        var templateId = ReadRequiredObjectId(document, "templateId", "Dynamic Flow version");
                        familyRoots.TryGetValue(templateId, out var familyRoot);
                        var root = ResolveVersionRoot(document, familyRoot);
                        var lineage = await ResolveLineageEvidenceAsync(
                            session,
                            versions,
                            document,
                            transactionCt);
                        var pinnedDefinitionProven = await ProvePinnedDefinitionAsync(
                            session,
                            forms,
                            document,
                            root,
                            transactionCt);
                        var patch = PlanVersionPatch(
                            document,
                            familyRoot,
                            lineage,
                            pinnedDefinitionProven);
                        if (!patch.HasChanges)
                        {
                            await VerifyAppliedManifestIfPresentAsync(
                                session,
                                rollbackManifests,
                                versions.CollectionNamespace.CollectionName,
                                VersionPhase,
                                document,
                                transactionCt);
                            return;
                        }

                        var boundedSet = ConstrainVersionPatchForManifest(
                            versions.CollectionNamespace.CollectionName,
                            document,
                            patch,
                            MaxManifestBsonBytes);
                        await ApplyPatchWithManifestAsync(
                            session,
                            versions,
                            rollbackManifests,
                            VersionPhase,
                            document,
                            VersionObservedFields,
                            boundedSet,
                            transactionCt);
                    },
                    cancellationToken);
            }

            lastId = page[^1]["_id"];
        }
    }

    internal static BsonDocument BuildRollbackManifestDocument(
        string collectionName,
        int phase,
        BsonDocument observed,
        BsonDocument set,
        DateTime? preparedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        if (phase is not (FamilyPhase or VersionPhase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (!observed.TryGetValue("_id", out var documentId))
            throw new InvalidOperationException("Cannot backfill Dynamic Flow metadata without _id.");
        var beforeFields = new BsonDocument();
        foreach (var field in set.Names.OrderBy(value => value, StringComparer.Ordinal))
        {
            var entry = new BsonDocument();
            if (observed.TryGetValue(field, out var value))
            {
                entry["exists"] = true;
                entry["value"] = value.DeepClone();
            }
            else
            {
                entry["exists"] = false;
            }
            beforeFields[field] = entry;
        }

        var afterFields = new BsonDocument();
        foreach (var field in set.Elements.OrderBy(value => value.Name, StringComparer.Ordinal))
            afterFields[field.Name] = field.Value.DeepClone();
        var expectedAfter = ApplySet(observed, set);
        var beforeHash = ComputeFieldOrderIndependentSha256(observed);
        var afterHash = ComputeFieldOrderIndependentSha256(expectedAfter);
        var preparedAt = preparedAtUtc ?? DateTime.UtcNow;
        return new BsonDocument
        {
            ["_id"] = $"{MigrationId}:{collectionName}:{documentId}",
            ["migrationId"] = MigrationId,
            ["phase"] = phase,
            ["state"] = ManifestPrepared,
            ["collectionName"] = collectionName,
            ["documentId"] = documentId.DeepClone(),
            ["beforeFields"] = beforeFields,
            ["afterFields"] = afterFields,
            ["beforeObservedSha256"] = beforeHash,
            ["afterObservedSha256"] = afterHash,
            // Retain the original field as an explicit compatibility alias.
            ["observedSha256"] = beforeHash,
            ["createdAtUtc"] = preparedAt,
            ["preparedAtUtc"] = preparedAt
        };
    }

    private static async Task ApplyPatchWithManifestAsync(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> collection,
        IMongoCollection<BsonDocument> rollbackManifests,
        int phase,
        BsonDocument observed,
        IReadOnlyCollection<string> observedFields,
        BsonDocument set,
        CancellationToken cancellationToken)
    {
        if (set.ElementCount == 0)
            return;

        var collectionName = collection.CollectionNamespace.CollectionName;
        var prepared = BuildRollbackManifestDocument(
            collectionName,
            phase,
            observed,
            set);
        EnsureManifestFits(prepared, MaxManifestBsonBytes);
        var manifestId = prepared["_id"].AsString;
        var existing = await rollbackManifests
            .Find(session, new BsonDocument("_id", manifestId))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            await rollbackManifests.InsertOneAsync(
                session,
                prepared,
                cancellationToken: cancellationToken);
        }
        else
        {
            ValidateManifestEnvelope(existing, collectionName, phase, observed["_id"]);
            var state = ReadManifestState(existing);
            if (state == ManifestApplied)
            {
                EnsureManifestMatchesPrepared(existing, prepared);
                EnsureObservedHash(
                    observed,
                    existing["afterObservedSha256"].AsString,
                    $"applied migration target '{collectionName}/{observed["_id"]}'");
                return;
            }

            if (state == ManifestRolledBack)
            {
                EnsureObservedHash(
                    observed,
                    existing["beforeObservedSha256"].AsString,
                    $"rolled-back migration target '{collectionName}/{observed["_id"]}'");
                var replace = await rollbackManifests.ReplaceOneAsync(
                    session,
                    new BsonDocument
                    {
                        { "_id", manifestId },
                        { "state", ManifestRolledBack }
                    },
                    prepared,
                    cancellationToken: cancellationToken);
                if (replace.MatchedCount != 1 || replace.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Dynamic Flow metadata migration lost the manifest resume race for '{manifestId}'.");
                }
            }
            else if (state == ManifestPrepared)
            {
                EnsureManifestMatchesPrepared(existing, prepared);
                EnsureObservedHash(
                    observed,
                    existing["beforeObservedSha256"].AsString,
                    $"prepared migration target '{collectionName}/{observed["_id"]}'");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow metadata rollback manifest '{manifestId}' has invalid state '{state}'.");
            }
        }

        EnsureObservedHash(
            observed,
            prepared["beforeObservedSha256"].AsString,
            $"migration preimage '{collectionName}/{observed["_id"]}'");
        var updateResult = await collection.UpdateOneAsync(
            session,
            BuildCompareAndSetFilter(observed, observedFields),
            new BsonDocumentUpdateDefinition<BsonDocument>(
                new BsonDocument("$set", set)),
            cancellationToken: cancellationToken);
        if (updateResult.MatchedCount != 1)
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata migration lost a compare-and-set race for " +
                $"'{collectionName}/{observed["_id"]}'.");
        }

        var after = await collection
            .Find(session, new BsonDocument("_id", observed["_id"]))
            .Project<BsonDocument>(phase == FamilyPhase ? FamilyProjection : VersionProjection)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Dynamic Flow metadata migration target '{collectionName}/{observed["_id"]}' disappeared.");
        EnsureObservedHash(
            after,
            prepared["afterObservedSha256"].AsString,
            $"migration postimage '{collectionName}/{observed["_id"]}'");

        var applyResult = await rollbackManifests.UpdateOneAsync(
            session,
            new BsonDocument
            {
                { "_id", manifestId },
                { "state", ManifestPrepared },
                { "beforeObservedSha256", prepared["beforeObservedSha256"] },
                { "afterObservedSha256", prepared["afterObservedSha256"] }
            },
            new BsonDocumentUpdateDefinition<BsonDocument>(
                new BsonDocument("$set", new BsonDocument
                {
                    { "state", ManifestApplied },
                    { "appliedAtUtc", DateTime.UtcNow }
                })),
            cancellationToken: cancellationToken);
        if (applyResult.MatchedCount != 1 || applyResult.ModifiedCount != 1)
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata migration could not mark manifest '{manifestId}' applied.");
        }
    }

    internal static async Task RollbackAsync(
        IMongoDatabase database,
        MongoOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        var rollbackManifests = database.GetCollection<BsonDocument>(RollbackManifestCollectionName);
        var allowedCollections = new HashSet<string>(StringComparer.Ordinal)
        {
            options.DynamicFlowTemplateCollection,
            options.DynamicFlowTemplateVersionCollection
        };
        foreach (var phase in new[] { VersionPhase, FamilyPhase })
        {
            string? lastId = null;
            while (true)
            {
                var filterClauses = new BsonArray
                {
                    new BsonDocument("migrationId", MigrationId),
                    new BsonDocument("phase", phase),
                    new BsonDocument("state", ManifestApplied)
                };
                if (lastId is not null)
                    filterClauses.Add(new BsonDocument("_id", new BsonDocument("$gt", lastId)));
                var page = await rollbackManifests
                    .Find(new BsonDocument("$and", filterClauses))
                    .Project<BsonDocument>(new BsonDocument("_id", 1))
                    .Sort(new BsonDocument("_id", 1))
                    .Limit(BatchSize)
                    .ToListAsync(cancellationToken);
                if (page.Count == 0)
                    break;

                foreach (var item in page)
                {
                    var manifestId = item["_id"].AsString;
                    await ExecuteOneDocumentTransactionAsync(
                        database.Client,
                        async (session, transactionCt) =>
                        {
                            var manifest = await rollbackManifests
                                .Find(session, new BsonDocument
                                {
                                    { "_id", manifestId },
                                    { "migrationId", MigrationId },
                                    { "phase", phase },
                                    { "state", ManifestApplied }
                                })
                                .FirstOrDefaultAsync(transactionCt);
                            if (manifest is null)
                                return;

                            var collectionName = manifest["collectionName"].AsString;
                            if (!allowedCollections.Contains(collectionName) ||
                                (phase == FamilyPhase &&
                                 !string.Equals(
                                     collectionName,
                                     options.DynamicFlowTemplateCollection,
                                     StringComparison.Ordinal)) ||
                                (phase == VersionPhase &&
                                 !string.Equals(
                                     collectionName,
                                     options.DynamicFlowTemplateVersionCollection,
                                     StringComparison.Ordinal)))
                            {
                                throw new InvalidOperationException(
                                    $"Dynamic Flow metadata rollback manifest targets unexpected " +
                                    $"phase/collection '{phase}/{collectionName}'.");
                            }

                            var documentId = manifest["documentId"];
                            ValidateManifestEnvelope(manifest, collectionName, phase, documentId);
                            var beforeFields = manifest["beforeFields"].AsBsonDocument;
                            var afterFields = manifest["afterFields"].AsBsonDocument;
                            var collection = database.GetCollection<BsonDocument>(collectionName);
                            var projection = phase == FamilyPhase ? FamilyProjection : VersionProjection;
                            var current = await collection
                                .Find(session, new BsonDocument("_id", documentId))
                                .Project<BsonDocument>(projection)
                                .FirstOrDefaultAsync(transactionCt)
                                ?? throw new InvalidOperationException(
                                    $"Dynamic Flow metadata rollback target " +
                                    $"'{collectionName}/{documentId}' is missing.");

                            EnsureObservedHash(
                                current,
                                manifest["afterObservedSha256"].AsString,
                                $"rollback postimage '{collectionName}/{documentId}'");
                            if (!MatchesAfterFields(current, afterFields))
                            {
                                throw new InvalidOperationException(
                                    $"Dynamic Flow metadata rollback target " +
                                    $"'{collectionName}/{documentId}' changed after migration; " +
                                    "refusing to overwrite concurrent business data.");
                            }

                            var expectedBefore = RestoreBeforeFields(current, beforeFields);
                            EnsureObservedHash(
                                expectedBefore,
                                manifest["beforeObservedSha256"].AsString,
                                $"rollback reconstructed preimage '{collectionName}/{documentId}'");
                            var update = BuildRestoreUpdate(beforeFields);
                            var result = await collection.UpdateOneAsync(
                                session,
                                BuildExactAfterFilter(documentId, afterFields),
                                new BsonDocumentUpdateDefinition<BsonDocument>(update),
                                cancellationToken: transactionCt);
                            if (result.MatchedCount != 1)
                            {
                                throw new InvalidOperationException(
                                    $"Dynamic Flow metadata rollback lost a compare-and-set race for " +
                                    $"'{collectionName}/{documentId}'.");
                            }

                            var restored = await collection
                                .Find(session, new BsonDocument("_id", documentId))
                                .Project<BsonDocument>(projection)
                                .FirstOrDefaultAsync(transactionCt)
                                ?? throw new InvalidOperationException(
                                    $"Dynamic Flow metadata rollback target " +
                                    $"'{collectionName}/{documentId}' disappeared.");
                            EnsureObservedHash(
                                restored,
                                manifest["beforeObservedSha256"].AsString,
                                $"rollback restored preimage '{collectionName}/{documentId}'");

                            var markResult = await rollbackManifests.UpdateOneAsync(
                                session,
                                new BsonDocument
                                {
                                    { "_id", manifestId },
                                    { "state", ManifestApplied },
                                    { "afterObservedSha256", manifest["afterObservedSha256"] }
                                },
                                new BsonDocumentUpdateDefinition<BsonDocument>(
                                    new BsonDocument("$set", new BsonDocument
                                    {
                                        { "state", ManifestRolledBack },
                                        { "rolledBackAtUtc", DateTime.UtcNow }
                                    })),
                                cancellationToken: transactionCt);
                            if (markResult.MatchedCount != 1 || markResult.ModifiedCount != 1)
                            {
                                throw new InvalidOperationException(
                                    $"Dynamic Flow metadata rollback could not mark manifest " +
                                    $"'{manifestId}' rolled back.");
                            }
                        },
                        cancellationToken);
                }

                lastId = page[^1]["_id"].AsString;
            }
        }
    }

    private static BsonDocument BuildRestoreUpdate(BsonDocument beforeFields)
    {
        var set = new BsonDocument();
        var unset = new BsonDocument();
        foreach (var field in beforeFields)
        {
            var entry = field.Value.AsBsonDocument;
            if (entry["exists"].AsBoolean)
                set[field.Name] = entry["value"].DeepClone();
            else
                unset[field.Name] = string.Empty;
        }
        var update = new BsonDocument();
        if (set.ElementCount > 0)
            update["$set"] = set;
        if (unset.ElementCount > 0)
            update["$unset"] = unset;
        return update;
    }

    private static bool MatchesAfterFields(BsonDocument current, BsonDocument afterFields)
        => afterFields.All(field =>
            current.TryGetValue(field.Name, out var currentValue) &&
            currentValue.Equals(field.Value));

    private static FilterDefinition<BsonDocument> BuildExactAfterFilter(
        BsonValue documentId,
        BsonDocument afterFields)
    {
        var clauses = new BsonArray { new BsonDocument("_id", documentId) };
        foreach (var field in afterFields)
            clauses.Add(new BsonDocument(field.Name, field.Value.DeepClone()));
        return new BsonDocumentFilterDefinition<BsonDocument>(
            new BsonDocument("$and", clauses));
    }

    private static BsonDocument RestoreBeforeFields(
        BsonDocument current,
        BsonDocument beforeFields)
    {
        var restored = current.DeepClone().AsBsonDocument;
        foreach (var field in beforeFields)
        {
            var entry = field.Value.AsBsonDocument;
            if (entry["exists"].AsBoolean)
                restored[field.Name] = entry["value"].DeepClone();
            else
                restored.Remove(field.Name);
        }
        return restored;
    }

    private static BsonDocument ApplySet(BsonDocument document, BsonDocument set)
    {
        var result = document.DeepClone().AsBsonDocument;
        foreach (var field in set)
            result[field.Name] = field.Value.DeepClone();
        return result;
    }

    internal static string ComputeFieldOrderIndependentSha256(BsonDocument document)
        => Sha256Hex(CanonicalizeBson(document).AsBsonDocument.ToBson());

    private static BsonValue CanonicalizeBson(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            return new BsonDocument(value.AsBsonDocument.Elements
                .OrderBy(element => element.Name, StringComparer.Ordinal)
                .Select(element =>
                    new BsonElement(element.Name, CanonicalizeBson(element.Value))));
        }
        if (value.IsBsonArray)
        {
            return new BsonArray(value.AsBsonArray.Select(CanonicalizeBson));
        }
        return value.DeepClone();
    }

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    internal static BsonDocument ConstrainVersionPatchForManifest(
        string collectionName,
        BsonDocument observed,
        DynamicFlowVersionMetadataPatch patch,
        int maxManifestBsonBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(observed);
        if (maxManifestBsonBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxManifestBsonBytes));

        var set = patch.Set.DeepClone().AsBsonDocument;
        var manifest = BuildRollbackManifestDocument(
            collectionName,
            VersionPhase,
            observed,
            set,
            DateTime.UnixEpoch);
        if (ManifestFits(manifest, maxManifestBsonBytes))
            return set;
        if (!patch.RewritesPayload || !set.Contains("payloadJson"))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest for '{collectionName}/{observed["_id"]}' " +
                $"would exceed the conservative {maxManifestBsonBytes}-byte BSON limit.");
        }

        // A mutable legacy payload can approach MongoDB's document limit. In
        // that case, do not duplicate its before+after bytes in the manifest:
        // preserve the exact payload, record a byte hash, and force review.
        var fallback = set.DeepClone().AsBsonDocument;
        fallback.Remove("payloadJson");
        fallback.Remove("payloadHash");
        fallback.Remove("catalogVersion");
        fallback.Remove("catalogSemanticHash");
        var payloadJson = ReadOptionalRawString(
            observed,
            "payloadJson",
            "Dynamic Flow version manifest size fallback");
        if (payloadJson is not null)
        {
            fallback["payloadHash"] = Sha256Hex(Encoding.UTF8.GetBytes(payloadJson));
            fallback["schemaVersion"] = Math.Max(1, ReadPayloadSchemaVersion(payloadJson) ?? 1);
            fallback["adapterVersion"] = 1;
        }
        fallback["migrationState"] = DynamicFlowDefinitionMigrationStates.RequiresReview;
        fallback["definitionLockable"] = false;
        fallback["executionEligibility"] = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase;
        fallback["executionBlockedReason"] = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented;
        if (!fallback.Contains("blockedUntilPhase"))
            fallback["blockedUntilPhase"] = "P6";

        var fallbackManifest = BuildRollbackManifestDocument(
            collectionName,
            VersionPhase,
            observed,
            fallback,
            DateTime.UnixEpoch);
        EnsureManifestFits(fallbackManifest, maxManifestBsonBytes);
        return fallback;
    }

    private static async Task VerifyAppliedManifestIfPresentAsync(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> rollbackManifests,
        string collectionName,
        int phase,
        BsonDocument observed,
        CancellationToken cancellationToken)
    {
        var manifestId = $"{MigrationId}:{collectionName}:{observed["_id"]}";
        var manifest = await rollbackManifests
            .Find(session, new BsonDocument("_id", manifestId))
            .FirstOrDefaultAsync(cancellationToken);
        if (manifest is null)
            return;

        ValidateManifestEnvelope(manifest, collectionName, phase, observed["_id"]);
        var currentHash = ComputeFieldOrderIndependentSha256(observed);
        var beforeHash = manifest["beforeObservedSha256"].AsString;
        var afterHash = manifest["afterObservedSha256"].AsString;
        switch (ReadManifestState(manifest))
        {
            case ManifestApplied:
                if (!string.Equals(currentHash, afterHash, StringComparison.OrdinalIgnoreCase) ||
                    !MatchesAfterFields(observed, manifest["afterFields"].AsBsonDocument))
                {
                    throw new InvalidOperationException(
                        $"Dynamic Flow metadata applied target '{collectionName}/{observed["_id"]}' " +
                        "does not match its recorded postimage.");
                }
                return;
            case ManifestRolledBack:
                EnsureObservedHash(
                    observed,
                    beforeHash,
                    $"rolled-back migration target '{collectionName}/{observed["_id"]}'");
                return;
            case ManifestPrepared when
                string.Equals(currentHash, afterHash, StringComparison.OrdinalIgnoreCase):
            {
                var result = await rollbackManifests.UpdateOneAsync(
                    session,
                    new BsonDocument
                    {
                        { "_id", manifestId },
                        { "state", ManifestPrepared },
                        { "afterObservedSha256", afterHash }
                    },
                    new BsonDocumentUpdateDefinition<BsonDocument>(
                        new BsonDocument("$set", new BsonDocument
                        {
                            { "state", ManifestApplied },
                            { "appliedAtUtc", DateTime.UtcNow }
                        })),
                    cancellationToken: cancellationToken);
                if (result.MatchedCount != 1 || result.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Dynamic Flow metadata migration could not resume manifest '{manifestId}'.");
                }
                return;
            }
            case ManifestPrepared:
                throw new InvalidOperationException(
                    $"Dynamic Flow metadata migration found stale PREPARED manifest '{manifestId}'; " +
                    "the target matches neither a committed postimage nor a resumable operation.");
            default:
                throw new InvalidOperationException(
                    $"Dynamic Flow metadata rollback manifest '{manifestId}' has an invalid state.");
        }
    }

    private static void EnsureManifestMatchesPrepared(
        BsonDocument stored,
        BsonDocument prepared)
    {
        foreach (var field in new[]
                 {
                     "migrationId",
                     "phase",
                     "collectionName",
                     "documentId",
                     "beforeObservedSha256",
                     "afterObservedSha256",
                     "observedSha256"
                 })
        {
            if (!stored.TryGetValue(field, out var actual) ||
                !prepared.TryGetValue(field, out var expected) ||
                !actual.Equals(expected))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow metadata rollback manifest '{prepared["_id"]}' " +
                    $"does not match the transactional migration plan at '{field}'.");
            }
        }
        foreach (var field in new[] { "beforeFields", "afterFields" })
        {
            if (!stored.TryGetValue(field, out var actual) ||
                !actual.IsBsonDocument ||
                !prepared.TryGetValue(field, out var expected) ||
                !expected.IsBsonDocument ||
                !string.Equals(
                    ComputeFieldOrderIndependentSha256(actual.AsBsonDocument),
                    ComputeFieldOrderIndependentSha256(expected.AsBsonDocument),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow metadata rollback manifest '{prepared["_id"]}' " +
                    $"does not match the transactional migration plan at '{field}'.");
            }
        }
    }

    private static void ValidateManifestEnvelope(
        BsonDocument manifest,
        string collectionName,
        int phase,
        BsonValue documentId)
    {
        var expectedId = $"{MigrationId}:{collectionName}:{documentId}";
        if (!manifest.TryGetValue("_id", out var id) ||
            !id.IsString ||
            !string.Equals(id.AsString, expectedId, StringComparison.Ordinal) ||
            !manifest.TryGetValue("migrationId", out var migrationId) ||
            !migrationId.IsString ||
            !string.Equals(migrationId.AsString, MigrationId, StringComparison.Ordinal) ||
            !manifest.TryGetValue("phase", out var storedPhase) ||
            !storedPhase.IsInt32 ||
            storedPhase.AsInt32 != phase ||
            !manifest.TryGetValue("collectionName", out var storedCollection) ||
            !storedCollection.IsString ||
            !string.Equals(storedCollection.AsString, collectionName, StringComparison.Ordinal) ||
            !manifest.TryGetValue("documentId", out var storedDocumentId) ||
            !storedDocumentId.Equals(documentId))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{expectedId}' has an invalid envelope.");
        }

        _ = ReadManifestState(manifest);
        var beforeFields = ReadRequiredManifestDocument(manifest, "beforeFields");
        var afterFields = ReadRequiredManifestDocument(manifest, "afterFields");
        if (!beforeFields.Names.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(afterFields.Names.OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{expectedId}' has mismatched field sets.");
        }
        foreach (var field in beforeFields)
        {
            if (!field.Value.IsBsonDocument ||
                !field.Value.AsBsonDocument.TryGetValue("exists", out var exists) ||
                !exists.IsBoolean ||
                (exists.AsBoolean && !field.Value.AsBsonDocument.Contains("value")) ||
                (!exists.AsBoolean && field.Value.AsBsonDocument.Contains("value")))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow metadata rollback manifest '{expectedId}' has an invalid " +
                    $"preimage entry for '{field.Name}'.");
            }
        }

        var beforeHash = ReadRequiredManifestHash(manifest, "beforeObservedSha256");
        _ = ReadRequiredManifestHash(manifest, "afterObservedSha256");
        var compatibilityHash = ReadRequiredManifestHash(manifest, "observedSha256");
        if (!string.Equals(beforeHash, compatibilityHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{expectedId}' has inconsistent preimage hashes.");
        }
        EnsureManifestFits(manifest, MaxManifestBsonBytes);
    }

    private static BsonDocument ReadRequiredManifestDocument(BsonDocument manifest, string field)
    {
        if (!manifest.TryGetValue(field, out var value) || !value.IsBsonDocument)
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{manifest.GetValue("_id", "?")}' " +
                $"is missing BSON document '{field}'.");
        }
        return value.AsBsonDocument;
    }

    private static string ReadRequiredManifestHash(BsonDocument manifest, string field)
    {
        if (!manifest.TryGetValue(field, out var value) ||
            !value.IsString ||
            value.AsString.Length != 64 ||
            !value.AsString.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{manifest.GetValue("_id", "?")}' " +
                $"has invalid SHA-256 field '{field}'.");
        }
        return value.AsString;
    }

    private static string ReadManifestState(BsonDocument manifest)
    {
        if (!manifest.TryGetValue("state", out var value) || !value.IsString)
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{manifest.GetValue("_id", "?")}' " +
                "is missing its state.");
        }
        var state = value.AsString;
        if (state is not (ManifestPrepared or ManifestApplied or ManifestRolledBack))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{manifest.GetValue("_id", "?")}' " +
                $"has invalid state '{state}'.");
        }
        return state;
    }

    private static void EnsureObservedHash(
        BsonDocument document,
        string expectedHash,
        string context)
    {
        var actualHash = ComputeFieldOrderIndependentSha256(document);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata {context} SHA-256 mismatch; refusing stale or concurrent data.");
        }
    }

    private static bool ManifestFits(BsonDocument manifest, int maxManifestBsonBytes)
        => manifest.ToBson().Length + ManifestTransitionReserveBytes < maxManifestBsonBytes;

    private static void EnsureManifestFits(BsonDocument manifest, int maxManifestBsonBytes)
    {
        var actualBytes = manifest.ToBson().Length;
        if (actualBytes + ManifestTransitionReserveBytes >= maxManifestBsonBytes)
        {
            throw new InvalidOperationException(
                $"Dynamic Flow metadata rollback manifest '{manifest.GetValue("_id", "?")}' " +
                $"is {actualBytes} bytes and exceeds the conservative " +
                $"{maxManifestBsonBytes}-byte BSON limit.");
        }
    }

    private static async Task ExecuteOneDocumentTransactionAsync(
        IMongoClient client,
        Func<IClientSessionHandle, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= TransactionRetryLimit; attempt++)
        {
            using var session = await client.StartSessionAsync(
                cancellationToken: cancellationToken);
            session.StartTransaction(MigrationTransactionOptions);
            try
            {
                await operation(session, cancellationToken);
                await CommitTransactionWithRetryAsync(session, cancellationToken);
                return;
            }
            catch (Exception error)
            {
                if (session.IsInTransaction)
                {
                    try
                    {
                        await session.AbortTransactionAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // Preserve the original migration failure.
                    }
                }

                if (attempt < TransactionRetryLimit &&
                    !cancellationToken.IsCancellationRequested &&
                    IsTransientTransactionFailure(error))
                {
                    continue;
                }
                throw;
            }
        }
    }

    private static async Task CommitTransactionWithRetryAsync(
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await session.CommitTransactionAsync(cancellationToken);
                return;
            }
            catch (MongoException error) when (
                attempt < TransactionRetryLimit &&
                error.HasErrorLabel("UnknownTransactionCommitResult"))
            {
                // Retrying commit is safe; do not replay the migration body.
            }
        }
    }

    private static bool IsTransientTransactionFailure(Exception error)
        => error is MongoException mongoError &&
           mongoError.HasErrorLabel("TransientTransactionError");

    internal static DynamicFlowFamilyMetadataPatch PlanFamilyPatch(
        BsonDocument document,
        bool hasImmutableVersion,
        string? derivedRootDynamicFormTemplateId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var context = $"Dynamic Flow family '{ReadRequiredObjectId(document, "_id", "Dynamic Flow family")}'";
        var set = new BsonDocument();

        var revision = ReadOptionalInt32(document, "familyRevision", context);
        if (revision is null or <= 0)
            set["familyRevision"] = 1;

        var owner = ReadOptionalObjectId(document, "ownerUserId", context);
        if (owner is null && ReadOptionalObjectId(document, "createdByUserId", context) is { } creator)
            set["ownerUserId"] = ObjectId.Parse(creator);

        if (!document.Contains("rootDynamicFormTemplateId"))
        {
            var legacyRoot = ReadOptionalObjectId(document, "dynamicFormTemplateId", context);
            var root = legacyRoot ?? NormalizeObjectIdOrNull(derivedRootDynamicFormTemplateId, context, "rootDynamicFormTemplateId");
            set["rootDynamicFormTemplateId"] = root is null ? BsonNull.Value : ObjectId.Parse(root);
        }
        else if (ReadOptionalObjectId(document, "rootDynamicFormTemplateId", context) is null)
        {
            var legacyRoot = ReadOptionalObjectId(document, "dynamicFormTemplateId", context);
            var root = legacyRoot ?? NormalizeObjectIdOrNull(derivedRootDynamicFormTemplateId, context, "rootDynamicFormTemplateId");
            if (root is not null)
                set["rootDynamicFormTemplateId"] = ObjectId.Parse(root);
        }

        BackfillRootLineage(document, set, context);

        var storedHasLocked = ReadOptionalBoolean(document, "hasLockedVersion", context);
        if (storedHasLocked is null || (hasImmutableVersion && !storedHasLocked.Value))
            set["hasLockedVersion"] = hasImmutableVersion;

        if (string.Equals(
                ReadOptionalString(document, "status", context),
                DynamicFlowTemplateStatuses.Archived,
                StringComparison.Ordinal))
        {
            BackfillArchiveAudit(document, set, owner, context);
        }

        return new DynamicFlowFamilyMetadataPatch(set);
    }

    internal static DynamicFlowVersionMetadataPatch PlanVersionPatch(
        BsonDocument document,
        string? familyRootDynamicFormTemplateId = null,
        DynamicFlowMigrationLineageEvidence? lineageEvidence = null,
        bool pinnedDefinitionProven = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var id = ReadRequiredObjectId(document, "_id", "Dynamic Flow version");
        var context = $"Dynamic Flow version '{id}'";
        var status = ReadOptionalString(document, "status", context) ?? DynamicFlowTemplateVersionStatuses.Draft;
        var versionNo = ReadOptionalInt32(document, "versionNo", context) ?? 1;
        var set = new BsonDocument();

        var draftRevision = ReadOptionalInt32(document, "draftRevision", context);
        if (draftRevision is null or <= 0)
            set["draftRevision"] = 1;

        var root = ResolveVersionRoot(document, familyRootDynamicFormTemplateId);
        if (!document.Contains("rootDynamicFormTemplateId"))
            set["rootDynamicFormTemplateId"] = root is null ? BsonNull.Value : ObjectId.Parse(root);
        else if (ReadOptionalObjectId(document, "rootDynamicFormTemplateId", context) is null && root is not null)
            set["rootDynamicFormTemplateId"] = ObjectId.Parse(root);

        var lineageProven = ApplyVersionLineage(
            document,
            set,
            versionNo,
            lineageEvidence,
            context);
        var analysis = AnalyzePayload(document, root, status, pinnedDefinitionProven, context);

        var isMutableDraft = string.Equals(status, DynamicFlowTemplateVersionStatuses.Draft, StringComparison.Ordinal);
        var isImmutable = string.Equals(status, DynamicFlowTemplateVersionStatuses.Locked, StringComparison.Ordinal) ||
                          string.Equals(status, DynamicFlowTemplateVersionStatuses.Archived, StringComparison.Ordinal);
        var exactDefinitionProven = analysis.ExactDefinitionProven &&
                                    lineageProven &&
                                    familyRootDynamicFormTemplateId is not null &&
                                    string.Equals(root, familyRootDynamicFormTemplateId, StringComparison.Ordinal);

        if (isMutableDraft && analysis.CanonicalJson is not null && analysis.CanonicalHash is not null)
        {
            SetIfDifferent(document, set, "payloadJson", analysis.CanonicalJson);
            SetIfDifferent(document, set, "payloadHash", analysis.CanonicalHash);
            SetIfDifferent(document, set, "schemaVersion", DynamicFlowDefinitionSchema.CurrentVersion);
            SetIfDifferent(document, set, "adapterVersion", analysis.AdapterVersion ?? DynamicFlowDefinitionSchema.CurrentAdapterVersion);
        }
        else
        {
            if (isMutableDraft && analysis.FallbackHash is not null)
                SetIfDifferent(document, set, "payloadHash", analysis.FallbackHash);
            if (analysis.SourceSchemaVersion is { } sourceSchemaVersion)
                SetIfMissingOrNonPositive(document, set, "schemaVersion", Math.Max(1, sourceSchemaVersion));
            if (analysis.AdapterVersion is { } adapterVersion)
                SetIfMissingOrNonPositive(document, set, "adapterVersion", adapterVersion);
        }

        // Version-level catalog metadata may only be copied from an already
        // proven payload. The migration never manufactures a catalog pin.
        if (exactDefinitionProven && analysis.CatalogVersion is not null && analysis.CatalogSemanticHash is not null)
        {
            SetIfDifferent(document, set, "catalogVersion", analysis.CatalogVersion);
            SetIfDifferent(document, set, "catalogSemanticHash", analysis.CatalogSemanticHash);
        }

        var canonicalState = exactDefinitionProven && (isMutableDraft || isImmutable);
        SetIfDifferent(
            document,
            set,
            "migrationState",
            canonicalState
                ? DynamicFlowDefinitionMigrationStates.Canonical
                : DynamicFlowDefinitionMigrationStates.RequiresReview);
        SetIfDifferent(document, set, "definitionLockable", canonicalState && isImmutable);
        SetIfDifferent(
            document,
            set,
            "executionEligibility",
            DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase);
        SetIfDifferent(
            document,
            set,
            "executionBlockedReason",
            DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented);
        SetIfDifferent(document, set, "blockedUntilPhase", analysis.BlockedUntilPhase ?? "P6");

        if (string.Equals(status, DynamicFlowTemplateVersionStatuses.Archived, StringComparison.Ordinal))
        {
            BackfillArchiveAudit(
                document,
                set,
                ReadOptionalObjectId(document, "createdByUserId", context),
                context);
        }

        return new DynamicFlowVersionMetadataPatch(set, isMutableDraft && set.Contains("payloadJson"));
    }

    private static DynamicFlowPayloadMigrationAnalysis AnalyzePayload(
        BsonDocument document,
        string? rootDynamicFormTemplateId,
        string status,
        bool pinnedDefinitionProven,
        string context)
    {
        var payloadJson = ReadOptionalRawString(document, "payloadJson", context);
        if (string.IsNullOrWhiteSpace(payloadJson))
            return DynamicFlowPayloadMigrationAnalysis.Invalid();

        var fallbackHash = TryComputeCanonicalObjectHash(payloadJson);
        DynamicFlowCanonicalPayload canonical;
        try
        {
            canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: true,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: false,
                    AllowHistoricalCatalogPins: true));
        }
        catch (Exception error) when (IsExpectedPayloadFailure(error))
        {
            return DynamicFlowPayloadMigrationAnalysis.Invalid(
                ReadPayloadSchemaVersion(payloadJson),
                fallbackHash);
        }

        var payloadRootText = canonical.Payload.RootDynamicFormTemplateId?.Trim();
        var payloadRootValid = string.IsNullOrWhiteSpace(payloadRootText) ||
                               ObjectId.TryParse(payloadRootText, out _);
        var payloadRoot = payloadRootValid && !string.IsNullOrWhiteSpace(payloadRootText)
            ? ObjectId.Parse(payloadRootText).ToString()
            : null;
        var rootMatches = rootDynamicFormTemplateId is null ||
                          (payloadRootValid && payloadRoot is null) ||
                          string.Equals(rootDynamicFormTemplateId, payloadRoot, StringComparison.Ordinal);
        if (!rootMatches)
            pinnedDefinitionProven = false;

        var catalogMatchesVersion = MatchesOptionalString(
                                        document,
                                        "catalogVersion",
                                        canonical.Payload.CatalogVersion,
                                        context) &&
                                    MatchesOptionalString(
                                        document,
                                        "catalogSemanticHash",
                                        canonical.Payload.CatalogSemanticHash,
                                        context);
        var hasExactCatalogPins = !string.IsNullOrWhiteSpace(canonical.Payload.CatalogVersion) &&
                                  !string.IsNullOrWhiteSpace(canonical.Payload.CatalogSemanticHash);
        var isImmutable = string.Equals(status, DynamicFlowTemplateVersionStatuses.Locked, StringComparison.Ordinal) ||
                          string.Equals(status, DynamicFlowTemplateVersionStatuses.Archived, StringComparison.Ordinal);
        var immutableBytesAndHashMatch = !isImmutable ||
                                         (canonical.SourceSchemaVersion == DynamicFlowDefinitionSchema.CurrentVersion &&
                                          string.Equals(payloadJson, canonical.CanonicalJson, StringComparison.Ordinal) &&
                                          string.Equals(
                                              ReadOptionalString(document, "payloadHash", context),
                                              canonical.PayloadHash,
                                              StringComparison.OrdinalIgnoreCase));
        var schemaMetadataMatches = MatchesOptionalPositiveInt(
                                        document,
                                        "schemaVersion",
                                        DynamicFlowDefinitionSchema.CurrentVersion,
                                        context) &&
                                    MatchesOptionalPositiveInt(
                                        document,
                                        "adapterVersion",
                                        canonical.AdapterVersion,
                                        context);
        var exact = pinnedDefinitionProven &&
                    rootMatches &&
                    catalogMatchesVersion &&
                    hasExactCatalogPins &&
                    immutableBytesAndHashMatch &&
                    (!isImmutable || schemaMetadataMatches);

        return new DynamicFlowPayloadMigrationAnalysis(
            canonical.CanonicalJson,
            canonical.PayloadHash,
            fallbackHash,
            canonical.SourceSchemaVersion,
            canonical.AdapterVersion,
            canonical.Payload.CatalogVersion,
            canonical.Payload.CatalogSemanticHash,
            canonical.BlockedUntilPhase,
            exact);
    }

    private static async Task<bool> ProvePinnedDefinitionAsync(
        IClientSessionHandle session,
        IMongoCollection<DynamicFormTemplate> forms,
        BsonDocument document,
        string? rootDynamicFormTemplateId,
        CancellationToken cancellationToken)
    {
        var context = $"Dynamic Flow version '{ReadRequiredObjectId(document, "_id", "Dynamic Flow version")}'";
        var payloadJson = ReadOptionalRawString(document, "payloadJson", context);
        if (string.IsNullOrWhiteSpace(payloadJson) || rootDynamicFormTemplateId is null)
            return false;

        try
        {
            var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payloadJson,
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: true,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
            if (!string.Equals(
                    canonical.Payload.RootDynamicFormTemplateId,
                    rootDynamicFormTemplateId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var ids = canonical.Payload.FormNodes
                .Select(node => NormalizeObjectIdOrNull(
                    node.DynamicFormTemplateId,
                    context,
                    "formNodes.dynamicFormTemplateId"))
                .Where(id => id is not null)
                .Select(id => id!)
                .Append(rootDynamicFormTemplateId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length == 0 || ids.Any(id => !ObjectId.TryParse(id, out _)))
                return false;

            var rows = await forms
                .Find(session, form => ids.Contains(form.Id) && !form.IsDeleted && form.IsActive)
                .ToListAsync(cancellationToken);
            if (rows.Count != ids.Length)
                return false;
            DynamicFlowTemplateService.EnsureLockableDynamicFormVersions(rows);
            var byId = rows.ToDictionary(form => form.Id, StringComparer.Ordinal);

            var rootDeclared = false;
            foreach (var formNode in canonical.Payload.FormNodes)
            {
                if (!byId.TryGetValue(formNode.DynamicFormTemplateId, out var form))
                    return false;
                var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
                if (!string.Equals(formNode.DynamicFormFamilyId, form.FamilyId, StringComparison.Ordinal) ||
                    formNode.DynamicFormVersionNo != form.VersionNo ||
                    !string.Equals(formNode.DynamicFormSchemaHash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(formNode.DynamicFormSnapshotHash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                rootDeclared |= string.Equals(form.Id, rootDynamicFormTemplateId, StringComparison.Ordinal);
            }
            if (!rootDeclared)
                return false;

            var status = ReadOptionalString(document, "status", context) ?? DynamicFlowTemplateVersionStatuses.Draft;
            if (string.Equals(status, DynamicFlowTemplateVersionStatuses.Locked, StringComparison.Ordinal) ||
                string.Equals(status, DynamicFlowTemplateVersionStatuses.Archived, StringComparison.Ordinal))
            {
                if (HasNonEmptyStatisticProfile(canonical.Payload.StatisticProfile))
                    return false;
                DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(
                    canonical.Payload,
                    BuildFormEndpointCatalogs(canonical.Payload, byId));
            }

            return true;
        }
        catch (Exception error) when (IsExpectedPayloadFailure(error))
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> BuildFormEndpointCatalogs(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyDictionary<string, DynamicFormTemplate> forms)
    {
        var result = new Dictionary<string, DynamicFlowFormEndpointCatalog>(StringComparer.Ordinal);
        foreach (var formNode in payload.FormNodes)
        {
            if (!forms.TryGetValue(formNode.DynamicFormTemplateId, out var form))
                continue;
            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
            var root = JsonNode.Parse(snapshot.Json) as JsonObject
                       ?? throw new InvalidOperationException("Published Dynamic Form snapshot must be a JSON object.");
            var scalarFields = new List<DynamicFlowScalarEndpoint>();
            if (root["fields"] is JsonArray fields)
            {
                foreach (var field in fields.OfType<JsonObject>())
                {
                    var fieldId = ReadNodeString(field, "id", "fieldId");
                    if (!string.IsNullOrWhiteSpace(fieldId))
                        scalarFields.Add(new DynamicFlowScalarEndpoint(fieldId, ReadNodeString(field, "key", "fieldKey")));
                }
            }

            var tableColumns = new List<DynamicFlowTableColumnEndpoint>();
            if (root["blocks"] is JsonArray blocks)
            {
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    var blockId = ReadNodeString(block, "blockId", "id");
                    if (string.IsNullOrWhiteSpace(blockId))
                        continue;
                    var tableMode = ReadNodeString(block, "tableMode")?.ToUpperInvariant() ?? "FIXED_GRID";
                    var columnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in new[] { "indexMap", "valueSlots", "columns", "cells", "statisticColumns" })
                        AddColumnKeys(block[property], columnKeys);
                    if (block["dataRect"] is JsonObject dataRect &&
                        ReadNodeInt(dataRect, "c0") is { } c0 &&
                        ReadNodeInt(dataRect, "c1") is { } c1 &&
                        c1 >= c0 && c1 - c0 < 1000)
                    {
                        for (var offset = 0; offset <= c1 - c0; offset++)
                            columnKeys.Add($"col_{offset + 1}");
                    }
                    else if (ReadNodeInt(block, "w") is { } width && width is > 0 and <= 1000)
                    {
                        for (var offset = 0; offset < width; offset++)
                            columnKeys.Add($"col_{offset + 1}");
                    }
                    tableColumns.AddRange(columnKeys
                        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                        .Select(key => new DynamicFlowTableColumnEndpoint(blockId, key, tableMode)));
                }
            }
            result[formNode.FormNodeId] = new DynamicFlowFormEndpointCatalog(
                formNode.FormNodeId,
                scalarFields,
                tableColumns);
        }
        return result;
    }

    private static async Task<DynamicFlowMigrationLineageEvidence> ResolveLineageEvidenceAsync(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> versions,
        BsonDocument document,
        CancellationToken cancellationToken)
    {
        var context = $"Dynamic Flow version '{ReadRequiredObjectId(document, "_id", "Dynamic Flow version")}'";
        var templateId = ReadRequiredObjectId(document, "templateId", context);
        var versionNo = ReadOptionalInt32(document, "versionNo", context) ?? 1;
        var originFamilyId = ReadOptionalObjectId(document, "originFamilyId", context);
        var originVersionId = ReadOptionalObjectId(document, "originVersionId", context);
        var versionId = ReadRequiredObjectId(document, "_id", context);

        if (originFamilyId is null && originVersionId is null && versionNo <= 1)
            return DynamicFlowMigrationLineageEvidence.Root;

        if (originVersionId is not null)
        {
            if (string.Equals(originVersionId, versionId, StringComparison.Ordinal))
                return DynamicFlowMigrationLineageEvidence.Unproven;
            var originFilter = new BsonDocument("_id", ObjectId.Parse(originVersionId));
            var origin = await versions
                .Find(session, originFilter)
                .Project<BsonDocument>(new BsonDocument { { "_id", 1 }, { "templateId", 1 } })
                .FirstOrDefaultAsync(cancellationToken);
            if (origin is null)
                return DynamicFlowMigrationLineageEvidence.Unproven;
            var actualFamilyId = ReadRequiredObjectId(origin, "templateId", "Dynamic Flow origin version");
            if (originFamilyId is not null && !string.Equals(originFamilyId, actualFamilyId, StringComparison.Ordinal))
                return DynamicFlowMigrationLineageEvidence.Unproven;
            return new DynamicFlowMigrationLineageEvidence(true, actualFamilyId, originVersionId);
        }

        if (originFamilyId is not null || versionNo <= 1)
            return DynamicFlowMigrationLineageEvidence.Unproven;

        var priorFilter = new BsonDocument
        {
            { "templateId", ObjectId.Parse(templateId) },
            { "versionNo", new BsonDocument("$lt", versionNo) },
            { "isDeleted", new BsonDocument("$ne", true) }
        };
        var prior = await versions
            .Find(session, priorFilter)
            .Project<BsonDocument>(new BsonDocument { { "_id", 1 }, { "versionNo", 1 } })
            .Sort(new BsonDocument { { "versionNo", -1 }, { "_id", -1 } })
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (prior.Count == 0)
            return DynamicFlowMigrationLineageEvidence.Unproven;
        if (prior.Count > 1 &&
            ReadOptionalInt32(prior[0], "versionNo", context) == ReadOptionalInt32(prior[1], "versionNo", context))
        {
            return DynamicFlowMigrationLineageEvidence.Unproven;
        }
        return new DynamicFlowMigrationLineageEvidence(
            true,
            templateId,
            ReadRequiredObjectId(prior[0], "_id", "Dynamic Flow prior version"));
    }

    private static bool ApplyVersionLineage(
        BsonDocument document,
        BsonDocument set,
        int versionNo,
        DynamicFlowMigrationLineageEvidence? evidence,
        string context)
    {
        var hasFamilyField = document.Contains("originFamilyId");
        var hasVersionField = document.Contains("originVersionId");
        var originFamilyId = ReadOptionalObjectId(document, "originFamilyId", context);
        var originVersionId = ReadOptionalObjectId(document, "originVersionId", context);

        if (evidence is { Proven: true })
        {
            if (!hasFamilyField)
            {
                set["originFamilyId"] = evidence.Value.OriginFamilyId is null
                    ? BsonNull.Value
                    : ObjectId.Parse(evidence.Value.OriginFamilyId);
            }
            if (!hasVersionField)
            {
                set["originVersionId"] = evidence.Value.OriginVersionId is null
                    ? BsonNull.Value
                    : ObjectId.Parse(evidence.Value.OriginVersionId);
            }
            return true;
        }

        if (evidence.HasValue)
            return false;

        if (!hasFamilyField && !hasVersionField && versionNo <= 1)
        {
            set["originFamilyId"] = BsonNull.Value;
            set["originVersionId"] = BsonNull.Value;
            return true;
        }

        return hasFamilyField == hasVersionField &&
               ((originFamilyId is null && originVersionId is null && versionNo <= 1) ||
                (originFamilyId is not null && originVersionId is not null));
    }

    private static async Task<Dictionary<string, DynamicFlowFamilyEvidence>> LoadFamilyEvidenceAsync(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> versions,
        IReadOnlyCollection<BsonDocument> familyPage,
        CancellationToken cancellationToken)
    {
        var familyIds = familyPage
            .Select(document => document["_id"])
            .ToArray();
        var filter = new BsonDocument
        {
            { "templateId", new BsonDocument("$in", new BsonArray(familyIds)) },
            { "isDeleted", new BsonDocument("$ne", true) }
        };
        using var cursor = await versions
            .Find(session, filter, new FindOptions { BatchSize = 1 })
            .Project<BsonDocument>(new BsonDocument
            {
                { "templateId", 1 },
                { "status", 1 },
                { "rootDynamicFormTemplateId", 1 },
                { "dynamicFormTemplateId", 1 },
                { "payloadJson", 1 }
            })
            .ToCursorAsync(cancellationToken);

        var result = familyPage.ToDictionary(
            document => ReadRequiredObjectId(document, "_id", "Dynamic Flow family"),
            _ => new DynamicFlowFamilyEvidence(),
            StringComparer.Ordinal);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var row in cursor.Current)
            {
                var familyId = ReadRequiredObjectId(row, "templateId", "Dynamic Flow version");
                if (!result.TryGetValue(familyId, out var evidence))
                    continue;
                var status = ReadOptionalString(row, "status", "Dynamic Flow version");
                evidence.HasImmutableVersion |= status is DynamicFlowTemplateVersionStatuses.Locked or
                    DynamicFlowTemplateVersionStatuses.Archived;
                var root = ReadOptionalObjectId(row, "rootDynamicFormTemplateId", "Dynamic Flow version") ??
                           ReadOptionalObjectId(row, "dynamicFormTemplateId", "Dynamic Flow version");
                root ??= TryReadPayloadRootObjectId(row);
                if (root is not null)
                    evidence.RootCandidates.Add(root);
            }
        }
        foreach (var evidence in result.Values)
        {
            if (evidence.RootCandidates.Count == 1)
                evidence.RootDynamicFormTemplateId = evidence.RootCandidates.Single();
        }
        return result;
    }

    private static async Task<Dictionary<string, string?>> LoadFamilyRootsAsync(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> families,
        IReadOnlyCollection<BsonDocument> versionPage,
        CancellationToken cancellationToken)
    {
        var ids = versionPage
            .Select(document => document.TryGetValue("templateId", out var value) ? value : BsonNull.Value)
            .Where(value => !value.IsBsonNull)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        var rows = await families
            .Find(
                session,
                new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids))))
            .Project<BsonDocument>(new BsonDocument
            {
                { "_id", 1 },
                { "rootDynamicFormTemplateId", 1 },
                { "dynamicFormTemplateId", 1 }
            })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            row => ReadRequiredObjectId(row, "_id", "Dynamic Flow family"),
            row => ReadOptionalObjectId(row, "rootDynamicFormTemplateId", "Dynamic Flow family") ??
                   ReadOptionalObjectId(row, "dynamicFormTemplateId", "Dynamic Flow family"),
            StringComparer.Ordinal);
    }

    private static string? ResolveVersionRoot(BsonDocument document, string? familyRootDynamicFormTemplateId)
    {
        var context = $"Dynamic Flow version '{ReadRequiredObjectId(document, "_id", "Dynamic Flow version")}'";
        var current = ReadOptionalObjectId(document, "rootDynamicFormTemplateId", context);
        var legacy = ReadOptionalObjectId(document, "dynamicFormTemplateId", context);
        var family = NormalizeObjectIdOrNull(familyRootDynamicFormTemplateId, context, "family.rootDynamicFormTemplateId");
        var candidates = new[] { current, legacy, family }
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static async Task<List<BsonDocument>> ReadPageAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument candidateFilter,
        BsonDocument projection,
        BsonValue? lastId,
        CancellationToken cancellationToken)
    {
        var filter = lastId is null
            ? candidateFilter.DeepClone().AsBsonDocument
            : new BsonDocument("$and", new BsonArray
            {
                candidateFilter.DeepClone().AsBsonDocument,
                new BsonDocument("_id", new BsonDocument("$gt", lastId))
            });
        return await collection
            .Find(filter)
            .Project<BsonDocument>(projection)
            .Sort(new BsonDocument("_id", 1))
            .Limit(BatchSize)
            .ToListAsync(cancellationToken);
    }

    internal static BsonDocument BuildCompareAndSetFilterDocument(
        BsonDocument document,
        IReadOnlyCollection<string> observedFields)
    {
        if (!document.TryGetValue("_id", out var id) || id.IsBsonNull)
            throw new InvalidOperationException("Cannot backfill Dynamic Flow metadata without _id.");
        var clauses = new BsonArray { new BsonDocument("_id", id) };
        foreach (var field in observedFields)
        {
            clauses.Add(document.TryGetValue(field, out var value)
                ? new BsonDocument(field, value)
                : new BsonDocument(field, new BsonDocument("$exists", false)));
        }
        return new BsonDocument("$and", clauses);
    }

    private static FilterDefinition<BsonDocument> BuildCompareAndSetFilter(
        BsonDocument document,
        IReadOnlyCollection<string> observedFields)
        => new BsonDocumentFilterDefinition<BsonDocument>(
            BuildCompareAndSetFilterDocument(document, observedFields));

    private static void BackfillRootLineage(BsonDocument document, BsonDocument set, string context)
    {
        var hasFamily = document.Contains("originFamilyId");
        var hasVersion = document.Contains("originVersionId");
        _ = ReadOptionalObjectId(document, "originFamilyId", context);
        _ = ReadOptionalObjectId(document, "originVersionId", context);
        if (!hasFamily && !hasVersion)
        {
            set["originFamilyId"] = BsonNull.Value;
            set["originVersionId"] = BsonNull.Value;
        }
    }

    private static void BackfillArchiveAudit(
        BsonDocument document,
        BsonDocument set,
        string? fallbackActorUserId,
        string context)
    {
        if (!document.Contains("archivedAtUtc") &&
            document.TryGetValue("updatedAtUtc", out var updatedAt) &&
            updatedAt.IsValidDateTime)
        {
            set["archivedAtUtc"] = updatedAt;
        }
        if (!document.Contains("archivedByUserId"))
        {
            var actor = ReadOptionalObjectId(document, "updatedByUserId", context) ??
                        fallbackActorUserId;
            if (actor is not null)
                set["archivedByUserId"] = ObjectId.Parse(actor);
        }
    }

    private static bool MatchesOptionalString(
        BsonDocument document,
        string field,
        string? expected,
        string context)
    {
        var actual = ReadOptionalString(document, field, context);
        return actual is null || string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static bool MatchesOptionalPositiveInt(
        BsonDocument document,
        string field,
        int expected,
        string context)
    {
        var actual = ReadOptionalInt32(document, field, context);
        return actual is null or <= 0 || actual == expected;
    }

    private static string? TryReadPayloadRootObjectId(BsonDocument document)
    {
        if (!document.TryGetValue("payloadJson", out var value) || !value.IsString)
            return null;
        try
        {
            using var payload = JsonDocument.Parse(value.AsString);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var field in new[] { "rootDynamicFormTemplateId", "dynamicFormTemplateId" })
            {
                if (!payload.RootElement.TryGetProperty(field, out var node) ||
                    node.ValueKind != JsonValueKind.String ||
                    !ObjectId.TryParse(node.GetString(), out var objectId))
                {
                    continue;
                }
                return objectId.ToString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static void SetIfMissingOrNonPositive(
        BsonDocument document,
        BsonDocument set,
        string field,
        int value)
    {
        var current = ReadOptionalInt32(document, field, "Dynamic Flow version");
        if (current is null or <= 0)
            set[field] = value;
    }

    private static void SetIfDifferent(BsonDocument document, BsonDocument set, string field, string value)
    {
        if (!document.TryGetValue(field, out var current) || !current.IsString || current.AsString != value)
            set[field] = value;
    }

    private static void SetIfDifferent(BsonDocument document, BsonDocument set, string field, int value)
    {
        var current = ReadOptionalInt32(document, field, "Dynamic Flow version");
        if (current != value)
            set[field] = value;
    }

    private static void SetIfDifferent(BsonDocument document, BsonDocument set, string field, bool value)
    {
        var current = ReadOptionalBoolean(document, field, "Dynamic Flow version");
        if (current != value)
            set[field] = value;
    }

    private static string? TryComputeCanonicalObjectHash(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return DynamicFlowDefinitionPayloadContract.ComputeCanonicalSha256(payloadJson);
        }
        catch (Exception error) when (error is JsonException or AppException)
        {
            return null;
        }
    }

    private static int? ReadPayloadSchemaVersion(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return document.RootElement.TryGetProperty("schemaVersion", out var value) && value.TryGetInt32(out var version)
                ? version
                : 1;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsExpectedPayloadFailure(Exception error)
        => error is AppException or JsonException or ArgumentException or InvalidOperationException or FormatException;

    private static bool HasNonEmptyStatisticProfile(IReadOnlyDictionary<string, JsonElement> profile)
    {
        if (profile.Count == 0)
            return false;
        if (profile.Count != 1 ||
            !profile.TryGetValue("diffMode", out var diffMode) ||
            diffMode.ValueKind != JsonValueKind.String)
        {
            return true;
        }
        var value = diffMode.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddColumnKeys(JsonNode? node, ISet<string> target)
    {
        if (node is not JsonArray array)
            return;
        foreach (var item in array.OfType<JsonObject>())
        {
            var key = ReadNodeString(item, "columnKey", "key", "column", "columnInstanceId");
            if (!string.IsNullOrWhiteSpace(key))
                target.Add(key);
        }
    }

    private static string? ReadNodeString(JsonObject node, params string[] names)
    {
        foreach (var name in names)
        {
            if (node[name] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        return null;
    }

    private static int? ReadNodeInt(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var integer))
            return integer;
        return value.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue
            ? (int)longValue
            : null;
    }

    private static string ReadRequiredObjectId(BsonDocument document, string field, string context)
        => ReadOptionalObjectId(document, field, context)
           ?? throw new InvalidOperationException(
               $"Cannot backfill {context}: field '{field}' is missing or invalid.");

    private static string? ReadOptionalObjectId(BsonDocument document, string field, string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (value.IsObjectId)
            return value.AsObjectId.ToString();
        if (value.IsString)
        {
            var text = value.AsString.Trim();
            if (text.Length == 0)
                return null;
            if (ObjectId.TryParse(text, out var objectId))
                return objectId.ToString();
        }
        throw new InvalidOperationException(
            $"Cannot backfill {context}: field '{field}' must be a valid ObjectId.");
    }

    private static string? NormalizeObjectIdOrNull(string? value, string context, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!ObjectId.TryParse(value, out var objectId))
            throw new InvalidOperationException(
                $"Cannot backfill {context}: field '{field}' must be a valid ObjectId.");
        return objectId.ToString();
    }

    private static int? ReadOptionalInt32(BsonDocument document, string field, string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (value.IsInt32)
            return value.AsInt32;
        if (value.IsInt64 && value.AsInt64 is >= int.MinValue and <= int.MaxValue)
            return (int)value.AsInt64;
        throw new InvalidOperationException(
            $"Cannot backfill {context}: field '{field}' must be an Int32-compatible value.");
    }

    private static bool? ReadOptionalBoolean(BsonDocument document, string field, string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (value.IsBoolean)
            return value.AsBoolean;
        throw new InvalidOperationException(
            $"Cannot backfill {context}: field '{field}' must be a boolean.");
    }

    private static string? ReadOptionalString(BsonDocument document, string field, string? context = null)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (!value.IsString)
            throw new InvalidOperationException(
                $"Cannot backfill {context ?? "Dynamic Flow"}: field '{field}' must be a string.");
        var text = value.AsString.Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? ReadOptionalRawString(BsonDocument document, string field, string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (!value.IsString)
            throw new InvalidOperationException(
                $"Cannot backfill {context}: field '{field}' must be a JSON string.");
        return value.AsString;
    }

    private static BsonDocument Missing(string field)
        => new(field, new BsonDocument("$exists", false));

    private static BsonDocument MissingOrNull(string field)
        => new("$or", new BsonArray
        {
            Missing(field),
            new BsonDocument(field, BsonNull.Value)
        });

    private sealed class DynamicFlowFamilyEvidence
    {
        public bool HasImmutableVersion { get; set; }
        public HashSet<string> RootCandidates { get; } = new(StringComparer.Ordinal);
        public string? RootDynamicFormTemplateId { get; set; }
    }
}

internal readonly record struct DynamicFlowFamilyMetadataPatch(BsonDocument Set)
{
    public bool HasChanges => Set.ElementCount > 0;
}

internal readonly record struct DynamicFlowVersionMetadataPatch(BsonDocument Set, bool RewritesPayload)
{
    public bool HasChanges => Set.ElementCount > 0;
}

internal readonly record struct DynamicFlowMigrationLineageEvidence(
    bool Proven,
    string? OriginFamilyId,
    string? OriginVersionId)
{
    public static DynamicFlowMigrationLineageEvidence Root => new(true, null, null);
    public static DynamicFlowMigrationLineageEvidence Unproven => new(false, null, null);
}

internal readonly record struct DynamicFlowPayloadMigrationAnalysis(
    string? CanonicalJson,
    string? CanonicalHash,
    string? FallbackHash,
    int? SourceSchemaVersion,
    int? AdapterVersion,
    string? CatalogVersion,
    string? CatalogSemanticHash,
    string? BlockedUntilPhase,
    bool ExactDefinitionProven)
{
    public static DynamicFlowPayloadMigrationAnalysis Invalid(
        int? sourceSchemaVersion = null,
        string? fallbackHash = null)
        => new(null, null, fallbackHash, sourceSchemaVersion, sourceSchemaVersion is null ? null : 1,
            null, null, null, false);
}
