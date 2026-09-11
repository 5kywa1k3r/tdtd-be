using MongoDB.Bson;
using tdtd_be.Data.Indexes;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

internal static class DynamicFormVersionMetadataBackfillContractTests
{
    private const string TemplateId = "64b64c10aafbc4a5ec000010";
    private const string FamilyId = "64b64c10aafbc4a5ec000011";

    public static void Run()
    {
        StartupPageStaysBoundedForLargeSchemas();
        CandidateFilterAuditsEveryPublishedPair();
        MissingMetadataPatchIsIdempotent();
        LegacyFamilyPreservesPriorRootNormalization();
        PublishedPartialSnapshotPairIsCanonicalizedAtomically();
        PublishedCompleteCanonicalPairIsUntouched();
        PublishedCompleteLiveStructuralDriftFailsClosed();
        PublishedCompleteStatisticsOnlyDeltaIsUntouched();
        PublishedCompleteHashMismatchFailsClosed();
        PublishedCompleteNonCanonicalWhitespaceFailsClosed();
        CompareAndSetFilterPinsObservedMetadataAndSchema();
        ExistingCompleteMetadataIsUntouched();
        InvalidExistingMetadataFailsClosed();
    }

    private static void StartupPageStaysBoundedForLargeSchemas()
    {
        var batchSize = (int)(typeof(DynamicFormVersionMetadataBackfill)
            .GetField(
                nameof(DynamicFormVersionMetadataBackfill.BatchSize),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Backfill batch size contract is missing."));

        AssertTrue(
            batchSize is > 0 and <= 50,
            "large Dynamic Form schemas must be scanned and written in small bounded pages");
    }

    private static void CandidateFilterAuditsEveryPublishedPair()
    {
        var candidateFilter = typeof(DynamicFormVersionMetadataBackfill)
            .GetField(
                "CandidateFilter",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
            .GetValue(null) as BsonDocument
            ?? throw new InvalidOperationException("Backfill candidate filter contract is missing.");
        var candidates = candidateFilter["$or"].AsBsonArray
            .Select(value => value.AsBsonDocument)
            .ToArray();

        AssertTrue(
            candidates.Any(candidate =>
                candidate.TryGetValue("isPublished", out var value) &&
                value.IsBoolean &&
                value.AsBoolean),
            "startup backfill must audit complete published pairs, not only incomplete metadata");
    }

    private static void MissingMetadataPatchIsIdempotent()
    {
        var document = DraftDocument();
        document.Remove("versionNo");
        document.Remove("lineageStatus");
        document.Remove("revision");

        var first = DynamicFormVersionMetadataBackfill.PlanPatch(document);
        AssertEqual(1, first.Set["versionNo"].AsInt32, "missing version number");
        AssertEqual(
            DynamicFormLineageStatuses.Legacy,
            first.Set["lineageStatus"].AsString,
            "missing lineage status");
        AssertEqual(1, first.Set["revision"].AsInt32, "missing revision");

        document.Merge(first.Set, overwriteExistingElements: true);
        var second = DynamicFormVersionMetadataBackfill.PlanPatch(document);
        AssertFalse(second.HasChanges, "re-running a completed metadata patch must be a no-op");
    }

    private static void LegacyFamilyPreservesPriorRootNormalization()
    {
        var document = DraftDocument();
        document.Remove("familyId");
        document["versionNo"] = 7;

        var patch = DynamicFormVersionMetadataBackfill.PlanPatch(document);

        AssertEqual(
            TemplateId,
            patch.Set["familyId"].AsObjectId.ToString(),
            "legacy root family id");
        AssertEqual(1, patch.Set["versionNo"].AsInt32, "legacy root version number");
    }

    private static void PublishedPartialSnapshotPairIsCanonicalizedAtomically()
    {
        var document = DraftDocument();
        document["isPublished"] = true;
        document["publishedSchemaSnapshotJson"] = "{\"stale\":true}";
        document.Remove("publishedSchemaHash");

        var patch = DynamicFormVersionMetadataBackfill.PlanPatch(document);
        var expected = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");

        AssertTrue(patch.SetsPublishedSnapshot, "partial published pair must be rebuilt");
        AssertEqual(
            expected.Json,
            patch.Set["publishedSchemaSnapshotJson"].AsString,
            "canonical published snapshot");
        AssertEqual(
            expected.Sha256,
            patch.Set["publishedSchemaHash"].AsString,
            "canonical published snapshot hash");

        document.Merge(patch.Set, overwriteExistingElements: true);
        AssertFalse(
            DynamicFormVersionMetadataBackfill.PlanPatch(document).HasChanges,
            "canonical published pair must be idempotent");
    }

    private static void ExistingCompleteMetadataIsUntouched()
    {
        var document = DraftDocument();
        var patch = DynamicFormVersionMetadataBackfill.PlanPatch(document);
        AssertFalse(patch.HasChanges, "complete draft metadata must not be rewritten");
    }

    private static void PublishedCompleteCanonicalPairIsUntouched()
    {
        var document = DraftDocument();
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");
        document["isPublished"] = true;
        document["publishedSchemaSnapshotJson"] = snapshot.Json;
        document["publishedSchemaHash"] = snapshot.Sha256;

        var patch = DynamicFormVersionMetadataBackfill.PlanPatch(document);

        AssertFalse(
            patch.HasChanges,
            "a complete canonical published pair must be verified without being rewritten");
    }

    private static void PublishedCompleteLiveStructuralDriftFailsClosed()
    {
        const string sections = """[{"id":"main","title":"Main"}]""";
        const string snapshotFields =
            """[{"id":"value","key":"value","name":"Original question","sectionId":"main","type":"longText"}]""";

        foreach (var (context, liveFields) in new[]
                 {
                     (
                         "field name drift",
                         """[{"id":"value","key":"value","name":"Changed question","sectionId":"main","type":"longText"}]"""),
                     (
                         "field type drift",
                         """[{"id":"value","key":"value","name":"Original question","sectionId":"main","type":"number"}]""")
                 })
        {
            var document = PublishedDocument(sections, snapshotFields, "[]");
            document["fieldsJson"] = liveFields;

            var error = AssertThrows<InvalidOperationException>(() =>
                DynamicFormVersionMetadataBackfill.PlanPatch(document));

            AssertContains(error.Message, "live structure", context);
            AssertTrue(
                error.InnerException is InvalidOperationException,
                $"{context} must preserve the structural-integrity cause");
            AssertContains(error.InnerException!.Message, "live fields", context);
        }
    }

    private static void PublishedCompleteStatisticsOnlyDeltaIsUntouched()
    {
        const string sections = """[{"id":"main","title":"Main"}]""";
        const string snapshotFields =
            """[{"id":"value","isStatistic":false,"key":"value","name":"Question","sectionId":"main","statisticLabelCodes":["OLD"],"type":"longText"}]""";
        var document = PublishedDocument(sections, snapshotFields, "[]");
        document["fieldsJson"] =
            """[{"id":"value","isStatistic":true,"key":"value","name":"Question","sectionId":"main","statistic":{"operation":"COUNT"},"statisticLabelCodes":["NEW"],"type":"longText"}]""";

        var patch = DynamicFormVersionMetadataBackfill.PlanPatch(document);

        AssertFalse(
            patch.HasChanges,
            "statistics-only live delta must pass startup integrity without rewriting the canonical pair");
    }

    private static void PublishedCompleteHashMismatchFailsClosed()
    {
        var document = DraftDocument();
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");
        document["isPublished"] = true;
        document["publishedSchemaSnapshotJson"] = snapshot.Json;
        document["publishedSchemaHash"] = new string('0', 64);

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormVersionMetadataBackfill.PlanPatch(document));

        AssertContains(error.Message, "provenance", "complete published pair mismatch");
        AssertTrue(
            error.InnerException is InvalidOperationException,
            "backfill must preserve the integrity-validation failure as its inner cause");
        AssertContains(
            error.InnerException!.Message,
            "SHA-256",
            "complete published pair mismatch cause");
    }

    private static void PublishedCompleteNonCanonicalWhitespaceFailsClosed()
    {
        var document = PublishedDocument("[]", "[]", "[]");
        document["publishedSchemaSnapshotJson"] =
            " " + document["publishedSchemaSnapshotJson"].AsString;

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormVersionMetadataBackfill.PlanPatch(document));

        AssertContains(error.Message, "provenance", "non-canonical snapshot whitespace");
        AssertTrue(
            error.InnerException is InvalidOperationException,
            "non-canonical snapshot whitespace must preserve its integrity cause");
        AssertContains(
            error.InnerException!.Message,
            "canonical representation",
            "non-canonical snapshot whitespace cause");
    }

    private static void CompareAndSetFilterPinsObservedMetadataAndSchema()
    {
        var document = DraftDocument();
        document.Remove("revision");
        document.Remove("publishedSchemaHash");
        var filter = DynamicFormVersionMetadataBackfill.BuildCompareAndSetFilterDocument(
            document,
            includeSchemaFields: true);
        var clauses = filter["$and"].AsBsonArray
            .Select(value => value.AsBsonDocument)
            .ToArray();

        AssertTrue(
            clauses.Any(clause =>
                clause.TryGetValue("revision", out var value) &&
                value.IsBsonDocument &&
                value.AsBsonDocument.TryGetValue("$exists", out var exists) &&
                exists.IsBoolean &&
                !exists.AsBoolean),
            "CAS must retain the observed absence of metadata fields");
        AssertTrue(
            clauses.Any(clause =>
                clause.TryGetValue("fieldsJson", out var value) && value == document["fieldsJson"]),
            "published snapshot CAS must retain the observed schema inputs");
        AssertTrue(
            clauses.Any(clause =>
                clause.TryGetValue("familyId", out var value) && value == document["familyId"]),
            "CAS must retain existing metadata values");
    }

    private static void InvalidExistingMetadataFailsClosed()
    {
        var document = DraftDocument();
        document["revision"] = "not-an-integer";

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormVersionMetadataBackfill.PlanPatch(document));
        AssertContains(error.Message, "revision", "invalid metadata field");
    }

    private static BsonDocument DraftDocument()
        => new()
        {
            { "_id", new ObjectId(TemplateId) },
            { "code", "FORM-LEGACY" },
            { "familyId", new ObjectId(FamilyId) },
            { "versionNo", 3 },
            { "lineageStatus", DynamicFormLineageStatuses.Version },
            { "revision", 4 },
            { "isPublished", false },
            { "schemaVersion", 1 },
            { "sectionsJson", "[]" },
            { "fieldsJson", "[]" },
            { "blocksJson", "[]" },
            { "excelBlockJson", BsonNull.Value }
        };

    private static BsonDocument PublishedDocument(
        string sectionsJson,
        string fieldsJson,
        string blocksJson)
    {
        var document = DraftDocument();
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson,
            fieldsJson,
            blocksJson);
        document["isPublished"] = true;
        document["sectionsJson"] = sectionsJson;
        document["fieldsJson"] = fieldsJson;
        document["blocksJson"] = blocksJson;
        document["publishedSchemaSnapshotJson"] = snapshot.Json;
        document["publishedSchemaHash"] = snapshot.Sha256;
        return document;
    }

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

    private static void AssertContains(string actual, string expected, string context)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{actual}' to contain '{expected}'.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}', got '{actual}'.");
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
