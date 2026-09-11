using tdtd_be.Data.Indexes;
using tdtd_be.Data.Infrastructure;

internal static class DynamicFormRuntimeProvenanceBackfillContractTests
{
    private const string TemplateId = "64b64c10aafbc4a5ec000001";
    private const string FamilyId = "64b64c10aafbc4a5ec000002";
    private const string OtherFamilyId = "64b64c10aafbc4a5ec000003";
    private const string SchemaHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static void Run()
    {
        MissingPublishedProvenanceIsFullyBackfilled();
        ExistingValidProvenanceIsUntouched();
        OnlyMissingFieldsAreBackfilled();
        ExistingMismatchFailsClosed();
        PublishedTemplateRequiresHash();
        LegacyDraftMayRemainWithoutHash();
        AllRuntimeCollectionsAreWired();
        RuntimeScanBatchStaysBounded();
    }

    private static void MissingPublishedProvenanceIsFullyBackfilled()
    {
        var patch = DynamicFormRuntimeProvenanceBackfill.PlanPatch(
            PublishedVersion(),
            new DynamicFormRuntimeProvenanceState(null, null, null),
            "test document");

        AssertTrue(patch.SetFamilyId, "missing familyId must be backfilled");
        AssertTrue(patch.SetVersionNo, "missing versionNo must be backfilled");
        AssertTrue(patch.SetSchemaHash, "missing published schema hash must be backfilled");
    }

    private static void ExistingValidProvenanceIsUntouched()
    {
        var patch = DynamicFormRuntimeProvenanceBackfill.PlanPatch(
            PublishedVersion(),
            new DynamicFormRuntimeProvenanceState(FamilyId, 2, SchemaHash.ToUpperInvariant()),
            "test document");

        AssertFalse(patch.HasChanges, "valid immutable provenance must not be rewritten");
    }

    private static void OnlyMissingFieldsAreBackfilled()
    {
        var patch = DynamicFormRuntimeProvenanceBackfill.PlanPatch(
            PublishedVersion(),
            new DynamicFormRuntimeProvenanceState(FamilyId, null, SchemaHash),
            "test document");

        AssertFalse(patch.SetFamilyId, "existing valid familyId must be preserved");
        AssertTrue(patch.SetVersionNo, "missing versionNo must be filled");
        AssertFalse(patch.SetSchemaHash, "existing valid schema hash must be preserved");
    }

    private static void ExistingMismatchFailsClosed()
    {
        AssertThrowsMismatch(
            new DynamicFormRuntimeProvenanceState(OtherFamilyId, 2, SchemaHash),
            "dynamicFormFamilyId");
        AssertThrowsMismatch(
            new DynamicFormRuntimeProvenanceState(FamilyId, 1, SchemaHash),
            "dynamicFormVersionNo");
        AssertThrowsMismatch(
            new DynamicFormRuntimeProvenanceState(FamilyId, 2, new string('f', 64)),
            "dynamicFormSchemaHash");
    }

    private static void PublishedTemplateRequiresHash()
    {
        var expected = new DynamicFormVersionProvenance(
            TemplateId,
            FamilyId,
            2,
            SchemaHash: null,
            IsPublished: true);

        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormRuntimeProvenanceBackfill.PlanPatch(
                expected,
                new DynamicFormRuntimeProvenanceState(null, null, null),
                "test document"));

        AssertContains(error.Message, "publishedSchemaHash", "published form prerequisite error");
    }

    private static void LegacyDraftMayRemainWithoutHash()
    {
        var expected = new DynamicFormVersionProvenance(
            TemplateId,
            FamilyId,
            1,
            SchemaHash: null,
            IsPublished: false);

        var missingHashPatch = DynamicFormRuntimeProvenanceBackfill.PlanPatch(
            expected,
            new DynamicFormRuntimeProvenanceState(null, null, null),
            "test draft document");
        AssertTrue(missingHashPatch.SetFamilyId, "draft familyId must still be backfilled");
        AssertTrue(missingHashPatch.SetVersionNo, "draft versionNo must still be backfilled");
        AssertFalse(missingHashPatch.SetSchemaHash, "legacy draft without a hash must stay hashless");

        var existingHashPatch = DynamicFormRuntimeProvenanceBackfill.PlanPatch(
            expected,
            new DynamicFormRuntimeProvenanceState(FamilyId, 1, SchemaHash),
            "test draft document");
        AssertFalse(existingHashPatch.HasChanges, "unverifiable legacy draft hash must not be overwritten");
    }

    private static void RuntimeScanBatchStaysBounded()
    {
        var batchSize = (int)(typeof(DynamicFormRuntimeProvenanceBackfill)
            .GetField(
                nameof(DynamicFormRuntimeProvenanceBackfill.BatchSize),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Backfill batch size contract is missing."));

        AssertTrue(
            batchSize is > 0 and <= 500,
            "startup migration batch size must remain bounded");
    }

    private static void AllRuntimeCollectionsAreWired()
    {
        var options = new MongoOptions();
        var actual = DynamicFormRuntimeProvenanceBackfill.GetRuntimeCollectionNames(options);
        var expected = new[]
        {
            options.WorkAssignmentCollection,
            options.WorkTemplateAssigneeCollection,
            options.WorkReportPeriodCollection,
            options.WorkAssignmentReportCollection,
            options.WorkAssignmentReportSectionCollection,
            options.AssignmentListDocRoleCollection,
            options.MyReportPeriodListDocRoleCollection,
            options.ReviewReportListDocRoleCollection
        };

        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime provenance collection contract differs. Expected [{string.Join(", ", expected)}], " +
                $"got [{string.Join(", ", actual)}].");
        }
    }

    private static DynamicFormVersionProvenance PublishedVersion()
        => new(TemplateId, FamilyId, 2, SchemaHash, IsPublished: true);

    private static void AssertThrowsMismatch(
        DynamicFormRuntimeProvenanceState actual,
        string expectedField)
    {
        var error = AssertThrows<InvalidOperationException>(() =>
            DynamicFormRuntimeProvenanceBackfill.PlanPatch(
                PublishedVersion(),
                actual,
                "test document"));

        AssertContains(error.Message, expectedField, "mismatch field");
        AssertContains(error.Message, "will not overwrite", "mismatch must explicitly fail closed");
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

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message)
        => AssertTrue(!value, message);
}
