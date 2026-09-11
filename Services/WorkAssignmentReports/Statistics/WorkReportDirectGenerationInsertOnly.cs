using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal static class WorkReportDirectGenerationInsertOnly
{
    private static readonly JsonWriterSettings CanonicalJsonSettings = new()
    {
        OutputMode = JsonOutputMode.CanonicalExtendedJson,
        Indent = false
    };

    private static readonly string[] PermittedAuditFields =
    [
        "createdAtUtc",
        "updatedAtUtc",
        "createdByUserId",
        "updatedByUserId"
    ];

    public static async Task InsertOrValidateAsync<T>(
        IMongoCollection<T> collection,
        IReadOnlyCollection<T> rows,
        string store,
        string generationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(generationId) ||
            !string.Equals(generationId, generationId.Trim(), StringComparison.Ordinal))
        {
            throw Conflict(store, "GENERATION_ID_INVALID");
        }

        var expected = rows
            .Select(row => CanonicalPersistedRow(row?.ToBsonDocument()
                ?? throw Conflict(store, "ROW_NULL")))
            .ToList();
        if (expected.Select(item => item.IdKey).Distinct(StringComparer.Ordinal).Count() != expected.Count)
            throw Conflict(store, "DUPLICATE_EXPECTED_ID");
        var generationIds = expected
            .Select(item => item.GenerationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (expected.Count > 0 &&
            (generationIds.Length != 1 ||
             !string.Equals(generationIds[0], generationId, StringComparison.Ordinal)))
        {
            throw Conflict(store, "GENERATION_ID_SET_INVALID");
        }

        var rawCollection = collection.Database.GetCollection<BsonDocument>(
            collection.CollectionNamespace.CollectionName);
        var generationFilter = new BsonDocument(
            "directProjection.generationId",
            generationId);
        var observed = await LoadGenerationAsync(
            rawCollection,
            generationFilter,
            ct);
        if (observed.Count > 0 || expected.Count == 0)
        {
            EnsureExactGeneration(expected, observed, store);
            return;
        }

        try
        {
            await collection.InsertManyAsync(
                rows,
                new InsertManyOptions { IsOrdered = false },
                ct);
            observed = await LoadGenerationAsync(
                rawCollection,
                generationFilter,
                ct);
            EnsureExactGeneration(expected, observed, store);
            return;
        }
        catch (MongoBulkWriteException<T> exception) when (
            exception.WriteErrors.Count > 0 &&
            exception.WriteErrors.All(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            // A retry or a concurrent exact replay may encounter rows already inserted.
            // Validate the raw persisted BSON below; never replace an existing generation row.
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Some driver/server combinations surface a single-row duplicate this way.
        }

        observed = await LoadGenerationAsync(
            rawCollection,
            generationFilter,
            ct);
        EnsureExactGeneration(expected, observed, store);
    }

    private static async Task<List<CanonicalRow>> LoadGenerationAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument generationFilter,
        CancellationToken ct)
    {
        var documents = await collection
            .Find(generationFilter)
            .Sort(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        return documents
            .Select(CanonicalPersistedRow)
            .ToList();
    }

    private static void EnsureExactGeneration(
        IReadOnlyCollection<CanonicalRow> expected,
        IReadOnlyCollection<CanonicalRow> observed,
        string store)
    {
        if (observed.Count != expected.Count ||
            observed.Select(item => item.IdKey).Distinct(StringComparer.Ordinal).Count() != observed.Count)
        {
            throw Conflict(store, "ROW_SET_MISMATCH");
        }

        var observedById = observed.ToDictionary(
            item => item.IdKey,
            item => item.CanonicalJson,
            StringComparer.Ordinal);
        if (expected.Any(item =>
                !observedById.TryGetValue(item.IdKey, out var persisted) ||
                !string.Equals(persisted, item.CanonicalJson, StringComparison.Ordinal)))
        {
            throw Conflict(store, "ROW_CONTENT_MISMATCH");
        }
    }

    private static CanonicalRow CanonicalPersistedRow(BsonDocument source)
    {
        var document = source.DeepClone().AsBsonDocument;
        foreach (var auditField in PermittedAuditFields)
            document.Remove(auditField);

        if (!document.TryGetValue("_id", out var id) || id.IsBsonNull)
            throw WorkReportDirectGenerationValidationException.RowConflict();
        if (!document.TryGetValue("directProjection", out var directProjection) ||
            !directProjection.IsBsonDocument ||
            !directProjection.AsBsonDocument.TryGetValue("generationId", out var generationIdValue) ||
            !generationIdValue.IsString ||
            string.IsNullOrWhiteSpace(generationIdValue.AsString) ||
            !string.Equals(generationIdValue.AsString, generationIdValue.AsString.Trim(), StringComparison.Ordinal))
        {
            throw WorkReportDirectGenerationValidationException.RowConflict();
        }
        var canonicalId = CanonicalizeBson(id).ToJson(CanonicalJsonSettings);
        var canonicalJson = CanonicalizeBson(document)
            .AsBsonDocument
            .ToJson(CanonicalJsonSettings);
        return new CanonicalRow(
            id.DeepClone(),
            canonicalId,
            generationIdValue.AsString,
            canonicalJson);
    }

    private static BsonValue CanonicalizeBson(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            return new BsonDocument(value.AsBsonDocument.Elements
                .OrderBy(element => element.Name, StringComparer.Ordinal)
                .Select(element => new BsonElement(
                    element.Name,
                    CanonicalizeBson(element.Value))));
        }

        if (value.IsBsonArray)
            return new BsonArray(value.AsBsonArray.Select(CanonicalizeBson));
        return value.DeepClone();
    }

    private static WorkReportDirectGenerationValidationException Conflict(
        string store,
        string reason)
    {
        _ = store;
        _ = reason;
        return WorkReportDirectGenerationValidationException.RowConflict();
    }

    private sealed record CanonicalRow(
        BsonValue Id,
        string IdKey,
        string GenerationId,
        string CanonicalJson);
}
