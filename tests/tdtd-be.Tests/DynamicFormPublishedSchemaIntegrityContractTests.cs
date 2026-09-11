using tdtd_be.Services.DynamicForms;
using tdtd_be.Models;

internal static class DynamicFormPublishedSchemaIntegrityContractTests
{
    public static void Run()
    {
        CanonicalSnapshotAndHashAreAcceptedExactly();
        MalformedSnapshotFailsClosed();
        SemanticallyEquivalentNonCanonicalSnapshotFailsClosed();
        HashMismatchFailsClosed();
        LiveTemplateAllowsOnlyStatisticProjectionDrift();
        MalformedLiveSchemaUsesStableIntegrityException();
        TypedAdapterFailureUsesStableIntegrityException();
    }

    private static void CanonicalSnapshotAndHashAreAcceptedExactly()
    {
        var canonical = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 3,
            sectionsJson: """[{"title":"Main","id":"main"}]""",
            fieldsJson: """[{"type":"shortText","id":"name","sectionId":"main"}]""",
            blocksJson: """[{"config":{"z":2,"a":1},"id":"table"}]""");

        var validated = DynamicFormPublishedSchemaSnapshotBuilder.ValidateExisting(
            canonical.Json,
            canonical.Sha256.ToUpperInvariant());

        AssertEqual(canonical.Json, validated.Json, "validated canonical JSON");
        AssertEqual(canonical.Sha256, validated.Sha256, "validated canonical hash");
    }

    private static void MalformedSnapshotFailsClosed()
    {
        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormPublishedSchemaSnapshotBuilder.ValidateExisting(
                "{not-json",
                new string('0', 64)));

        AssertContains(error.Message, "valid JSON", "malformed published snapshot");
    }

    private static void SemanticallyEquivalentNonCanonicalSnapshotFailsClosed()
    {
        var canonical = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");
        var nonCanonical =
            "{\"fields\":[],\"schemaVersion\":1,\"sections\":[],\"blocks\":[]}";

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormPublishedSchemaSnapshotBuilder.ValidateExisting(
                nonCanonical,
                canonical.Sha256));

        AssertContains(error.Message, "canonical", "non-canonical published snapshot");
    }

    private static void HashMismatchFailsClosed()
    {
        var canonical = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 1,
            sectionsJson: "[]",
            fieldsJson: "[]",
            blocksJson: "[]");

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormPublishedSchemaSnapshotBuilder.ValidateExisting(
                canonical.Json,
                new string('f', 64)));

        AssertContains(error.Message, "SHA-256", "published snapshot hash mismatch");
    }

    private static void LiveTemplateAllowsOnlyStatisticProjectionDrift()
    {
        const string sections = """[{"id":"main","title":"Main"}]""";
        const string snapshotFields =
            """[{"id":"name","isStatistic":false,"name":"Name","sectionId":"main","statisticLabelCodes":["OLD"],"type":"shortText"}]""";
        const string snapshotBlocks =
            """[{"blockId":"table","metricLabelTargets":[{"labelCode":"OLD"}],"sectionId":"main","statisticColumns":["a"],"tableMode":"FIXED_GRID"}]""";
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            schemaVersion: 3,
            sectionsJson: sections,
            fieldsJson: snapshotFields,
            blocksJson: snapshotBlocks);
        var template = new DynamicFormTemplate
        {
            SchemaVersion = 3,
            SectionsJson = """[{"title":"Main","id":"main"}]""",
            FieldsJson =
                """[{"type":"shortText","statisticLabelCodes":["NEW"],"sectionId":"main","name":"Name","isStatistic":true,"id":"name"}]""",
            BlocksJson =
                """[{"tableMode":"FIXED_GRID","statisticColumns":["b"],"sectionId":"main","metricLabelTargets":[{"labelCode":"NEW"}],"blockId":"table"}]""",
            PublishedSchemaSnapshotJson = snapshot.Json,
            PublishedSchemaHash = snapshot.Sha256,
            IsPublished = true
        };

        var validated = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template);
        AssertEqual(snapshot.Sha256, validated.Sha256, "statistics-only live projection");

        template.FieldsJson =
            """[{"type":"number","statisticLabelCodes":["NEW"],"sectionId":"main","name":"Name","isStatistic":true,"id":"name"}]""";
        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template));
        AssertContains(error.Message, "live fields", "structural live schema drift");
    }

    private static void MalformedLiveSchemaUsesStableIntegrityException()
    {
        const string sections = """[{"id":"main","title":"Main"}]""";
        const string fields =
            """[{"id":"name","key":"name","name":"Question","sectionId":"main","type":"longText"}]""";
        const string blocks = "[]";
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(1, sections, fields, blocks);

        foreach (var (context, mutate) in new (string, Action<DynamicFormTemplate>)[]
                 {
                     ("malformed sections", template => template.SectionsJson = "{not-json"),
                     ("non-array fields", template => template.FieldsJson = "{}"),
                     ("malformed blocks", template => template.BlocksJson = "{not-json"),
                     ("non-array blocks", template => template.BlocksJson = "{}")
                 })
        {
            var template = PublishedTemplate(snapshot, sections, fields, blocks);
            mutate(template);

            var error = AssertThrows<InvalidOperationException>(() =>
                DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template));

            AssertContains(error.Message, "Published Dynamic Form", context);
        }
    }

    private static void TypedAdapterFailureUsesStableIntegrityException()
    {
        const string sections = """[{"id":"main","title":"Main"}]""";
        const string adapterInvalidFields =
            """[{"id":"name","key":"name","name":"Question","options":"not-an-array","sectionId":"main","type":"longText"}]""";
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            1,
            sections,
            adapterInvalidFields,
            "[]");
        var template = PublishedTemplate(snapshot, sections, adapterInvalidFields, "[]");

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(template));

        AssertContains(error.Message, "parsed or adapted safely", "typed adapter failure");
        if (error.InnerException is not System.Text.Json.JsonException)
        {
            throw new InvalidOperationException(
                $"typed adapter failure: expected JsonException inner cause, got {error.InnerException?.GetType().Name ?? "null"}.");
        }
    }

    private static DynamicFormTemplate PublishedTemplate(
        DynamicFormPublishedSchemaSnapshot snapshot,
        string sectionsJson,
        string fieldsJson,
        string blocksJson)
        => new()
        {
            SchemaVersion = 1,
            SectionsJson = sectionsJson,
            FieldsJson = fieldsJson,
            BlocksJson = blocksJson,
            PublishedSchemaSnapshotJson = snapshot.Json,
            PublishedSchemaHash = snapshot.Sha256,
            IsPublished = true
        };

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
}
