using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Data.Indexes;

/// <summary>
/// One-time/idempotent compatibility migration for runtime documents created before
/// Dynamic Form family/version provenance became part of the immutable binding.
///
/// The migration deliberately scans only a compact projection, in bounded _id pages.
/// Existing values are validated and never overwritten when they disagree with the
/// exact DynamicFormTemplateId referenced by the runtime document.
/// </summary>
internal static class DynamicFormRuntimeProvenanceBackfill
{
    internal const int BatchSize = 250;

    private static readonly BsonDocument RuntimeProjection = new()
    {
        { "_id", 1 },
        { "dynamicFormTemplateId", 1 },
        { "dynamicFormFamilyId", 1 },
        { "dynamicFormVersionNo", 1 },
        { "dynamicFormSchemaHash", 1 }
    };

    internal static async Task RunAsync(
        IMongoDatabase db,
        MongoOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var formMetadataById = await LoadFormMetadataAsync(db, options, cancellationToken);
        foreach (var collectionName in GetRuntimeCollectionNames(options))
        {
            await BackfillCollectionAsync(
                db.GetCollection<BsonDocument>(collectionName),
                collectionName,
                formMetadataById,
                cancellationToken);
        }
    }

    internal static IReadOnlyList<string> GetRuntimeCollectionNames(MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new[]
        {
            options.WorkAssignmentCollection,
            options.WorkTemplateAssigneeCollection,
            options.WorkReportPeriodCollection,
            options.WorkAssignmentReportCollection,
            options.WorkAssignmentReportSectionCollection,
            options.AssignmentListDocRoleCollection,
            options.MyReportPeriodListDocRoleCollection,
            options.ReviewReportListDocRoleCollection
        }.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static DynamicFormRuntimeProvenancePatch PlanPatch(
        DynamicFormVersionProvenance expected,
        DynamicFormRuntimeProvenanceState actual,
        string documentContext)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (string.IsNullOrWhiteSpace(expected.FamilyId))
        {
            throw new InvalidOperationException(
                $"Cannot backfill Dynamic Form provenance for {documentContext}: " +
                $"template '{expected.TemplateId}' has no familyId.");
        }

        if (expected.VersionNo <= 0)
        {
            throw new InvalidOperationException(
                $"Cannot backfill Dynamic Form provenance for {documentContext}: " +
                $"template '{expected.TemplateId}' has invalid versionNo '{expected.VersionNo}'.");
        }

        if (expected.IsPublished && string.IsNullOrWhiteSpace(expected.SchemaHash))
        {
            throw new InvalidOperationException(
                $"Cannot backfill Dynamic Form provenance for {documentContext}: " +
                $"published template '{expected.TemplateId}' has no publishedSchemaHash.");
        }

        var hasFamilyId = !string.IsNullOrWhiteSpace(actual.FamilyId);
        if (hasFamilyId && !StringComparer.OrdinalIgnoreCase.Equals(actual.FamilyId, expected.FamilyId))
        {
            throw BuildMismatchException(
                documentContext,
                expected.TemplateId,
                "dynamicFormFamilyId",
                actual.FamilyId,
                expected.FamilyId);
        }

        var hasVersionNo = actual.VersionNo is > 0;
        if (hasVersionNo && actual.VersionNo != expected.VersionNo)
        {
            throw BuildMismatchException(
                documentContext,
                expected.TemplateId,
                "dynamicFormVersionNo",
                actual.VersionNo,
                expected.VersionNo);
        }

        var setSchemaHash = false;
        if (!string.IsNullOrWhiteSpace(expected.SchemaHash))
        {
            var hasSchemaHash = !string.IsNullOrWhiteSpace(actual.SchemaHash);
            if (hasSchemaHash &&
                !StringComparer.OrdinalIgnoreCase.Equals(actual.SchemaHash, expected.SchemaHash))
            {
                throw BuildMismatchException(
                    documentContext,
                    expected.TemplateId,
                    "dynamicFormSchemaHash",
                    actual.SchemaHash,
                    expected.SchemaHash);
            }

            setSchemaHash = !hasSchemaHash;
        }

        return new DynamicFormRuntimeProvenancePatch(
            SetFamilyId: !hasFamilyId,
            SetVersionNo: !hasVersionNo,
            SetSchemaHash: setSchemaHash);
    }

    private static InvalidOperationException BuildMismatchException(
        string documentContext,
        string templateId,
        string field,
        object? actual,
        object? expected)
        => new(
            $"Dynamic Form provenance mismatch for {documentContext}: field '{field}' is " +
            $"'{actual ?? "<null>"}', but exact template '{templateId}' requires " +
            $"'{expected ?? "<null>"}'. The migration will not overwrite this value.");

    private static async Task<Dictionary<string, BsonDocument>> LoadFormMetadataAsync(
        IMongoDatabase db,
        MongoOptions options,
        CancellationToken cancellationToken)
    {
        var projection = new BsonDocument
        {
            { "_id", 1 },
            { "familyId", 1 },
            { "versionNo", 1 },
            { "isPublished", 1 },
            { "publishedSchemaHash", 1 }
        };

        var forms = await db.GetCollection<BsonDocument>(options.DynamicFormTemplateCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project<BsonDocument>(projection)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, BsonDocument>(forms.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var form in forms)
        {
            var templateId = ReadRequiredObjectId(form, "_id", "Dynamic Form metadata");
            if (!result.TryAdd(templateId, form))
            {
                throw new InvalidOperationException(
                    $"Duplicate Dynamic Form metadata was loaded for template '{templateId}'.");
            }
        }

        return result;
    }

    private static async Task BackfillCollectionAsync(
        IMongoCollection<BsonDocument> collection,
        string collectionName,
        IReadOnlyDictionary<string, BsonDocument> formMetadataById,
        CancellationToken cancellationToken)
    {
        BsonValue? lastId = null;
        var baseFilter = Builders<BsonDocument>.Filter.Exists("dynamicFormTemplateId", true)
                         & Builders<BsonDocument>.Filter.Ne("dynamicFormTemplateId", BsonNull.Value);

        while (true)
        {
            var pageFilter = lastId is null
                ? baseFilter
                : baseFilter & Builders<BsonDocument>.Filter.Gt("_id", lastId);

            var page = await collection
                .Find(pageFilter)
                .Project<BsonDocument>(RuntimeProjection)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .Limit(BatchSize)
                .ToListAsync(cancellationToken);

            if (page.Count == 0)
                return;

            var writes = new List<WriteModel<BsonDocument>>(page.Count);
            foreach (var document in page)
            {
                var documentId = ReadRequiredIdValue(document, collectionName);
                var documentContext = $"collection '{collectionName}', document '{documentId}'";
                var templateId = ReadRequiredObjectId(
                    document,
                    "dynamicFormTemplateId",
                    documentContext);

                if (!formMetadataById.TryGetValue(templateId, out var formDocument))
                {
                    throw new InvalidOperationException(
                        $"Cannot backfill Dynamic Form provenance for {documentContext}: " +
                        $"referenced template '{templateId}' does not exist.");
                }

                var expected = ReadExpectedProvenance(formDocument, templateId, documentContext);
                var actual = ReadActualProvenance(document, documentContext);
                var patch = PlanPatch(expected, actual, documentContext);
                if (!patch.HasChanges)
                    continue;

                var set = new BsonDocument();
                if (patch.SetFamilyId)
                    set["dynamicFormFamilyId"] = new BsonObjectId(ObjectId.Parse(expected.FamilyId));
                if (patch.SetVersionNo)
                    set["dynamicFormVersionNo"] = expected.VersionNo;
                if (patch.SetSchemaHash)
                    set["dynamicFormSchemaHash"] = expected.SchemaHash!;

                var compareAndSetFilter = Builders<BsonDocument>.Filter.Eq("_id", documentId)
                                          & BuildObservedFieldFilter(document, "dynamicFormTemplateId");
                if (patch.SetFamilyId)
                    compareAndSetFilter &= BuildObservedFieldFilter(document, "dynamicFormFamilyId");
                if (patch.SetVersionNo)
                    compareAndSetFilter &= BuildObservedFieldFilter(document, "dynamicFormVersionNo");
                if (patch.SetSchemaHash)
                    compareAndSetFilter &= BuildObservedFieldFilter(document, "dynamicFormSchemaHash");

                writes.Add(new UpdateOneModel<BsonDocument>(
                    compareAndSetFilter,
                    new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument("$set", set))));
            }

            if (writes.Count > 0)
            {
                await collection.BulkWriteAsync(
                    writes,
                    new BulkWriteOptions { IsOrdered = false },
                    cancellationToken);

                await VerifyPageAsync(
                    collection,
                    collectionName,
                    page.Select(document => document["_id"]).ToArray(),
                    formMetadataById,
                    cancellationToken);
            }

            lastId = page[^1]["_id"];
        }
    }

    private static async Task VerifyPageAsync(
        IMongoCollection<BsonDocument> collection,
        string collectionName,
        IReadOnlyCollection<BsonValue> documentIds,
        IReadOnlyDictionary<string, BsonDocument> formMetadataById,
        CancellationToken cancellationToken)
    {
        var documents = await collection
            .Find(Builders<BsonDocument>.Filter.In("_id", documentIds))
            .Project<BsonDocument>(RuntimeProjection)
            .ToListAsync(cancellationToken);

        foreach (var document in documents)
        {
            var documentId = ReadRequiredIdValue(document, collectionName);
            var documentContext = $"collection '{collectionName}', document '{documentId}'";
            var templateId = ReadRequiredObjectId(
                document,
                "dynamicFormTemplateId",
                documentContext);

            if (!formMetadataById.TryGetValue(templateId, out var formDocument))
            {
                throw new InvalidOperationException(
                    $"Cannot verify Dynamic Form provenance for {documentContext}: " +
                    $"referenced template '{templateId}' does not exist.");
            }

            var patch = PlanPatch(
                ReadExpectedProvenance(formDocument, templateId, documentContext),
                ReadActualProvenance(document, documentContext),
                documentContext);

            if (patch.HasChanges)
            {
                throw new InvalidOperationException(
                    $"Dynamic Form provenance backfill did not converge for {documentContext}; " +
                    "the document changed concurrently or still has missing provenance.");
            }
        }
    }

    private static DynamicFormVersionProvenance ReadExpectedProvenance(
        BsonDocument form,
        string templateId,
        string documentContext)
    {
        var familyId = ReadRequiredObjectId(form, "familyId", $"template '{templateId}'");
        var versionNo = ReadNullableInt32(form, "versionNo", $"template '{templateId}'") ?? 0;
        var isPublished = ReadOptionalBoolean(form, "isPublished", $"template '{templateId}'");
        var schemaHash = ReadOptionalString(form, "publishedSchemaHash", $"template '{templateId}'");

        var expected = new DynamicFormVersionProvenance(
            templateId,
            familyId,
            versionNo,
            schemaHash,
            isPublished);

        // Validate form-side metadata before inspecting the runtime document so the
        // failure points to the prerequisite migration when it has not completed.
        _ = PlanPatch(expected, new(null, null, null), documentContext);
        return expected;
    }

    private static DynamicFormRuntimeProvenanceState ReadActualProvenance(
        BsonDocument document,
        string documentContext)
        => new(
            ReadOptionalObjectId(document, "dynamicFormFamilyId", documentContext),
            ReadNullableInt32(document, "dynamicFormVersionNo", documentContext),
            ReadOptionalString(document, "dynamicFormSchemaHash", documentContext));

    private static FilterDefinition<BsonDocument> BuildObservedFieldFilter(
        BsonDocument document,
        string field)
    {
        if (!document.TryGetValue(field, out var value))
            return Builders<BsonDocument>.Filter.Exists(field, false);

        return Builders<BsonDocument>.Filter.Eq(field, value);
    }

    private static BsonValue ReadRequiredIdValue(BsonDocument document, string collectionName)
    {
        if (!document.TryGetValue("_id", out var id) || id.IsBsonNull)
        {
            throw new InvalidOperationException(
                $"Cannot backfill Dynamic Form provenance in collection '{collectionName}': " +
                "a document has no _id.");
        }

        return id;
    }

    private static string ReadRequiredObjectId(
        BsonDocument document,
        string field,
        string context)
        => ReadOptionalObjectId(document, field, context)
           ?? throw new InvalidOperationException(
               $"Cannot backfill Dynamic Form provenance for {context}: field '{field}' " +
               "is missing, blank, or not a valid ObjectId.");

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
            $"Cannot backfill Dynamic Form provenance for {context}: field '{field}' " +
            $"has unsupported value '{value}'.");
    }

    private static int? ReadNullableInt32(
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
            $"Cannot backfill Dynamic Form provenance for {context}: field '{field}' " +
            $"must be an Int32-compatible value, got '{value}'.");
    }

    private static string? ReadOptionalString(
        BsonDocument document,
        string field,
        string context)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;

        if (!value.IsString)
        {
            throw new InvalidOperationException(
                $"Cannot backfill Dynamic Form provenance for {context}: field '{field}' " +
                $"must be a string, got '{value.BsonType}'.");
        }

        var text = value.AsString.Trim();
        return text.Length == 0 ? null : text;
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
            $"Cannot backfill Dynamic Form provenance for {context}: field '{field}' " +
            $"must be a boolean, got '{value.BsonType}'.");
    }
}

internal sealed record DynamicFormVersionProvenance(
    string TemplateId,
    string FamilyId,
    int VersionNo,
    string? SchemaHash,
    bool IsPublished);

internal sealed record DynamicFormRuntimeProvenanceState(
    string? FamilyId,
    int? VersionNo,
    string? SchemaHash);

internal readonly record struct DynamicFormRuntimeProvenancePatch(
    bool SetFamilyId,
    bool SetVersionNo,
    bool SetSchemaHash)
{
    public bool HasChanges => SetFamilyId || SetVersionNo || SetSchemaHash;
}
