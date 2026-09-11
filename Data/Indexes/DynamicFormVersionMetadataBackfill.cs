using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Data.Indexes;

/// <summary>
/// Bounded compatibility migration for Dynamic Form version metadata and the
/// immutable published-schema snapshot introduced after the original schema.
/// </summary>
internal static class DynamicFormVersionMetadataBackfill
{
    // A form schema can be large. Keep both the read page and BulkWrite envelope
    // deliberately small so first deployment has a predictable memory ceiling.
    internal const int BatchSize = 25;

    private static readonly BsonDocument Projection = new()
    {
        { "_id", 1 },
        { "code", 1 },
        { "familyId", 1 },
        { "versionNo", 1 },
        { "lineageStatus", 1 },
        { "revision", 1 },
        { "isPublished", 1 },
        { "publishedSchemaSnapshotJson", 1 },
        { "publishedSchemaHash", 1 },
        { "schemaVersion", 1 },
        { "sectionsJson", 1 },
        { "fieldsJson", 1 },
        { "blocksJson", 1 },
        { "excelBlockJson", 1 }
    };

    private static readonly string[] ObservedMetadataFields =
    {
        "familyId",
        "versionNo",
        "lineageStatus",
        "revision",
        "isPublished",
        "publishedSchemaSnapshotJson",
        "publishedSchemaHash"
    };

    private static readonly string[] ObservedSchemaFields =
    {
        "schemaVersion",
        "sectionsJson",
        "fieldsJson",
        "blocksJson",
        "excelBlockJson"
    };

    private static readonly BsonDocument CandidateFilter = new("$or", new BsonArray
    {
        MissingOrNull("familyId"),
        new BsonDocument("familyId", string.Empty),
        MissingOrNull("versionNo"),
        new BsonDocument("versionNo", new BsonDocument("$lte", 0)),
        MissingOrNull("lineageStatus"),
        new BsonDocument("lineageStatus", string.Empty),
        MissingOrNull("revision"),
        new BsonDocument("revision", new BsonDocument("$lte", 0)),
        // Audit every published pair, including already-complete metadata. This
        // keeps startup fail-closed if a stored immutable snapshot or hash was
        // changed out of band; paging still bounds the memory envelope.
        new BsonDocument("isPublished", true)
    });

    internal static async Task RunAsync(
        IMongoCollection<DynamicFormTemplate> collection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);

        BsonValue? lastId = null;
        while (true)
        {
            var pageFilter = lastId is null
                ? CandidateFilter.DeepClone().AsBsonDocument
                : new BsonDocument("$and", new BsonArray
                {
                    CandidateFilter.DeepClone().AsBsonDocument,
                    new BsonDocument("_id", new BsonDocument("$gt", lastId))
                });

            var page = await collection
                .Find(new BsonDocumentFilterDefinition<DynamicFormTemplate>(pageFilter))
                .Project<BsonDocument>(Projection)
                .Sort(new BsonDocumentSortDefinition<DynamicFormTemplate>(new BsonDocument("_id", 1)))
                .Limit(BatchSize)
                .ToListAsync(cancellationToken);

            if (page.Count == 0)
                return;

            var writes = new List<WriteModel<DynamicFormTemplate>>(page.Count);
            foreach (var document in page)
            {
                var patch = PlanPatch(document);
                if (!patch.HasChanges)
                    continue;

                writes.Add(new UpdateOneModel<DynamicFormTemplate>(
                    BuildCompareAndSetFilter(document, patch.SetsPublishedSnapshot),
                    new BsonDocumentUpdateDefinition<DynamicFormTemplate>(
                        new BsonDocument("$set", patch.Set))));
            }

            if (writes.Count > 0)
            {
                await collection.BulkWriteAsync(
                    writes,
                    new BulkWriteOptions { IsOrdered = false },
                    cancellationToken);

                await VerifyPageAsync(
                    collection,
                    page.Select(document => document["_id"]).ToArray(),
                    cancellationToken);
            }

            lastId = page[^1]["_id"];
        }
    }

    internal static DynamicFormVersionMetadataPatch PlanPatch(BsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var templateId = ReadRequiredObjectId(document, "_id");
        var code = ReadOptionalString(document, "code") ?? "<missing-code>";
        var context = $"Dynamic Form '{templateId}' ({code})";
        var set = new BsonDocument();

        var familyId = ReadOptionalObjectId(document, "familyId", context);
        var isLegacyFamily = string.IsNullOrWhiteSpace(familyId);
        if (isLegacyFamily)
            set["familyId"] = new BsonObjectId(ObjectId.Parse(templateId));

        var versionNo = ReadOptionalInt32(document, "versionNo", context);
        if (isLegacyFamily || versionNo is null or <= 0)
            set["versionNo"] = 1;

        var lineageStatus = ReadOptionalString(document, "lineageStatus", context);
        if (string.IsNullOrWhiteSpace(lineageStatus))
            set["lineageStatus"] = DynamicFormLineageStatuses.Legacy;

        var revision = ReadOptionalInt32(document, "revision", context);
        if (revision is null or <= 0)
            set["revision"] = 1;

        var setsPublishedSnapshot = false;
        if (ReadOptionalBoolean(document, "isPublished", context))
        {
            var snapshotJson = ReadOptionalRawString(
                document,
                "publishedSchemaSnapshotJson",
                context);
            var schemaHash = ReadOptionalString(document, "publishedSchemaHash", context);
            var publishedTemplate = ReadPublishedTemplate(
                document,
                templateId,
                code,
                snapshotJson,
                schemaHash,
                context);
            if (string.IsNullOrWhiteSpace(snapshotJson) || string.IsNullOrWhiteSpace(schemaHash))
            {
                DynamicFormPublishedSchemaSnapshot snapshot;
                try
                {
                    snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(publishedTemplate);
                    publishedTemplate.PublishedSchemaSnapshotJson = snapshot.Json;
                    publishedTemplate.PublishedSchemaHash = snapshot.Sha256;
                    _ = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(
                        publishedTemplate);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"Cannot backfill published Dynamic Form schema snapshot for {context}.",
                        ex);
                }

                // A partial pair is not usable provenance. Canonicalize both values,
                // matching the prior migration behavior, under compare-and-set.
                set["publishedSchemaSnapshotJson"] = snapshot.Json;
                set["publishedSchemaHash"] = snapshot.Sha256;
                setsPublishedSnapshot = true;
            }
            else
            {
                try
                {
                    DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(
                        publishedTemplate);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        $"Published Dynamic Form schema provenance or live structure is invalid for {context}.",
                        ex);
                }
            }
        }

        return new DynamicFormVersionMetadataPatch(set, setsPublishedSnapshot);
    }

    private static DynamicFormTemplate ReadPublishedTemplate(
        BsonDocument document,
        string templateId,
        string code,
        string? snapshotJson,
        string? schemaHash,
        string context)
        => new()
        {
            Id = templateId,
            Code = code,
            SchemaVersion = ReadOptionalInt32(document, "schemaVersion", context) ?? 1,
            SectionsJson = ReadSchemaJson(document, "sectionsJson", "[]", context),
            FieldsJson = ReadSchemaJson(document, "fieldsJson", "[]", context),
            BlocksJson = ReadSchemaJson(document, "blocksJson", "[]", context),
            ExcelBlockJson = ReadSchemaJson(document, "excelBlockJson", null!, context),
            IsPublished = true,
            PublishedSchemaSnapshotJson = snapshotJson,
            PublishedSchemaHash = schemaHash
        };

    private static FilterDefinition<DynamicFormTemplate> BuildCompareAndSetFilter(
        BsonDocument document,
        bool includeSchemaFields)
        => new BsonDocumentFilterDefinition<DynamicFormTemplate>(
            BuildCompareAndSetFilterDocument(document, includeSchemaFields));

    internal static BsonDocument BuildCompareAndSetFilterDocument(
        BsonDocument document,
        bool includeSchemaFields)
    {
        if (!document.TryGetValue("_id", out var id) || id.IsBsonNull)
            throw new InvalidOperationException("Cannot backfill a Dynamic Form without _id.");

        var clauses = new BsonArray { new BsonDocument("_id", id) };
        foreach (var field in ObservedMetadataFields)
            clauses.Add(BuildObservedFieldClause(document, field));

        if (includeSchemaFields)
        {
            foreach (var field in ObservedSchemaFields)
                clauses.Add(BuildObservedFieldClause(document, field));
        }

        return new BsonDocument("$and", clauses);
    }

    private static async Task VerifyPageAsync(
        IMongoCollection<DynamicFormTemplate> collection,
        IReadOnlyCollection<BsonValue> ids,
        CancellationToken cancellationToken)
    {
        var idFilter = new BsonDocumentFilterDefinition<DynamicFormTemplate>(
            new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids))));
        var documents = await collection
            .Find(idFilter)
            .Project<BsonDocument>(Projection)
            .ToListAsync(cancellationToken);

        if (documents.Count != ids.Count)
        {
            throw new InvalidOperationException(
                "Dynamic Form version metadata backfill could not verify its page; " +
                "a candidate was removed concurrently.");
        }

        foreach (var document in documents)
        {
            var patch = PlanPatch(document);
            if (patch.HasChanges)
            {
                throw new InvalidOperationException(
                    $"Dynamic Form version metadata backfill did not converge for " +
                    $"'{ReadRequiredObjectId(document, "_id")}'; the document changed " +
                    "concurrently or still has incomplete metadata.");
            }
        }
    }

    private static BsonDocument BuildObservedFieldClause(BsonDocument document, string field)
        => document.TryGetValue(field, out var value)
            ? new BsonDocument(field, value)
            : new BsonDocument(field, new BsonDocument("$exists", false));

    private static BsonDocument MissingOrNull(string field)
        => new("$or", new BsonArray
        {
            new BsonDocument(field, new BsonDocument("$exists", false)),
            new BsonDocument(field, BsonNull.Value)
        });

    private static string ReadRequiredObjectId(BsonDocument document, string field)
        => ReadOptionalObjectId(document, field, $"document field '{field}'")
           ?? throw new InvalidOperationException(
               $"Cannot backfill Dynamic Form version metadata: field '{field}' is missing.");

    private static string? ReadOptionalObjectId(
        BsonDocument document,
        string field,
        string context)
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

    private static int? ReadOptionalInt32(
        BsonDocument document,
        string field,
        string context)
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

    private static string? ReadOptionalString(
        BsonDocument document,
        string field,
        string? context = null)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (!value.IsString)
        {
            throw new InvalidOperationException(
                $"Cannot backfill {context ?? "Dynamic Form"}: field '{field}' must be a string.");
        }

        var text = value.AsString.Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? ReadOptionalRawString(
        BsonDocument document,
        string field,
        string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        if (!value.IsString)
        {
            throw new InvalidOperationException(
                $"Cannot backfill {context}: field '{field}' must be a string.");
        }

        return value.AsString;
    }

    private static bool ReadOptionalBoolean(
        BsonDocument document,
        string field,
        string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return false;
        if (value.IsBoolean)
            return value.AsBoolean;

        throw new InvalidOperationException(
            $"Cannot backfill {context}: field '{field}' must be a boolean.");
    }

    private static string ReadSchemaJson(
        BsonDocument document,
        string field,
        string missingDefault,
        string context)
    {
        if (!document.TryGetValue(field, out var value))
            return missingDefault;
        if (value.IsBsonNull)
            return null!;
        if (value.IsString)
            return value.AsString;

        throw new InvalidOperationException(
            $"Cannot backfill {context}: field '{field}' must be a JSON string.");
    }

}

internal readonly record struct DynamicFormVersionMetadataPatch(
    BsonDocument Set,
    bool SetsPublishedSnapshot)
{
    public bool HasChanges => Set.ElementCount > 0;
}
