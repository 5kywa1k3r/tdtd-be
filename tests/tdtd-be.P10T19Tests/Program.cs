using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-AGGREGATE-01", FieldOwnerParity),
    ("P10-AGGREGATE-02", TableConceptGrainTimePins),
    ("P10-AGGREGATE-03", RowLabelCountSeparation),
    ("P10-AGGREGATE-04", CountAndMeasureReveal),
    ("P10-AGGREGATE-05", MixedPinAndDigestFailClosed),
    ("P10-AGGREGATE-06", OrderingMultiplicityAndZeroWriteBoundary)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {item.Id}: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

Console.WriteLine($"P10_T19_ACTUAL_AGGREGATE_OK cases={passed} cumulative=16 stopBefore=P10-T20");
return passed == cases.Length ? 0 : 1;

static async Task FieldOwnerParity()
{
    var context = await Context();
    var field = Field(context, "field-amount", "amount", "bucket-a");
    field.ReportCount = 2;
    field.ValueCount = 5;
    field.NumericValueCount = 2;
    field.Sum = 12m;
    field.Min = 5m;
    field.Max = 7m;
    var capture = await Capture(context, [field], [], []);
    Equal(1, capture.FieldRows.Length, "field count");
    var actual = capture.FieldRows[0];
    Equal(field.Id, actual.OwnerRowId, "physical owner id");
    Equal(2L, actual.Counts.ReportCount, "report count");
    Equal(5L, actual.Counts.RowCount, "row count");
    Equal(2L, actual.Counts.NumericValueCount, "numeric count");
    Equal(6m, actual.Measures.Mean, "numeric mean denominator");
    True(actual.RowState.SourceMembershipMatched, "source membership");
    True(actual.RowState.SourcePayloadMatched, "source payload");
    True(actual.RowState.SourceLifecycleMatched, "source lifecycle");
    Equal(
        ActualFixture.P9OwnerStableObjectId(
            context.Boundary.GenerationId,
            "FIELD_AGGREGATE",
            field.WorkId,
            field.ScopeType,
            field.ScopeId,
            field.DynamicFormTemplateId,
            field.FieldId,
            field.BucketKey,
            field.PeriodInstanceKey,
            field.ReportStatus.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        actual.OwnerRowId,
        "independent P9 field id");
}

static async Task TableConceptGrainTimePins()
{
    var context = await Context();
    var table = Table(context, "block-b", "metric-z", "bucket-z");
    table.ReportCount = 1;
    table.ValueCount = 3;
    table.NumericValueCount = 3;
    table.Sum = 18m;
    table.Min = 4m;
    table.Max = 8m;
    var capture = await Capture(context, [], [table], []);
    var actual = Single(capture.TableMetricRows, "table row");
    Equal("block-b", actual.BlockId, "block");
    Equal("metric-z", actual.MetricKey, "metric");
    Equal("MONTH:2026-08", actual.Time.PeriodInstanceKey, "time pin");
    Equal(capture.OwnerFilterSha256, actual.IdentityPins.OwnerFilterSha256, "filter pin");
    Equal(capture.ConfigIdentitySha256, actual.IdentityPins.ConfigIdentitySha256, "config pin");
    Equal(capture.ResultGenerationIdentitySha256, actual.IdentityPins.ResultGenerationIdentitySha256, "result pin");
    Distinct(
        actual.IdentityPins.ConceptIdentitySha256,
        actual.IdentityPins.GrainIdentitySha256,
        actual.IdentityPins.TimeIdentitySha256,
        "concept/grain/time identities");
}

static async Task RowLabelCountSeparation()
{
    var context = await Context();
    var label = Label(context, "block-label", "label-risk");
    label.ReportCount = 2;
    label.RowCount = 7;
    var capture = await Capture(context, [], [], [label]);
    var actual = Single(capture.RowLabelRows, "label row");
    Equal(2L, actual.Counts.ReportCount, "label report count");
    Equal(7L, actual.Counts.RowCount, "label row count");
    Equal(0L, actual.Counts.NumericValueCount, "label numeric count");
    True(actual.RowState.CountsNonNegative, "label counts nonnegative");
    True(actual.RowState.ReportCountWithinRows, "label report within rows");
}

static async Task CountAndMeasureReveal()
{
    var context = await Context();
    var valid = Field(context, "field-valid", "a-valid", "a");
    valid.ReportCount = 2;
    valid.ValueCount = 7;
    valid.NumericValueCount = 3;
    valid.Sum = 18m;
    valid.Min = 4m;
    valid.Max = 8m;
    valid.TrueCount = 2;
    valid.FalseCount = 1;
    var invalid = Field(context, "field-invalid", "b-invalid", "b");
    invalid.ReportCount = 3;
    invalid.ValueCount = 1;
    invalid.NumericValueCount = 2;
    invalid.Sum = 9m;
    invalid.Min = 5m;
    invalid.Max = 4m;
    invalid.TrueCount = 2;
    invalid.FalseCount = 1;
    var capture = await Capture(context, [invalid, valid], [], []);
    Equal(2, capture.FieldRows.Length, "wrong aggregate remains observable");
    var observedValid = capture.FieldRows.Single(x => x.FieldId == "field-valid");
    Equal(6m, observedValid.Measures.Mean, "mean uses numeric count");
    var observedInvalid = capture.FieldRows.Single(x => x.FieldId == "field-invalid");
    False(observedInvalid.RowState.ReportCountWithinRows, "wrong report count revealed");
    False(observedInvalid.RowState.NumericCountWithinRows, "wrong numeric count revealed");
    False(observedInvalid.RowState.BooleanCountWithinRows, "wrong boolean count revealed");
    False(observedInvalid.RowState.NumericShapeConsistent, "wrong numeric shape revealed");
    Equal(9m, observedInvalid.Measures.Sum, "wrong owner result preserved");
}

static async Task MixedPinAndDigestFailClosed()
{
    var context = await Context();
    var unexpectedSource = ActualFixture.Source(ActualFixture.Report2, ordinal: 1);
    var extra = Field(context, "field-extra", "extra", "x", unexpectedSource);
    var visible = await Capture(context, [extra], [], []);
    False(visible.FieldRows[0].RowState.SourceMembershipMatched, "unexpected owner source remains visible");

    var mixed = Clone(extra);
    mixed.DirectProjection!.ConfigHash = ActualFixture.Hash('f');
    await Throws("AGGREGATE_ROW_GENERATION_MISMATCH", async () =>
        await Capture(context, [mixed], [], []));

    var pins = Pins([extra], [], []);
    pins[0] = pins[0] with { Sha256 = ActualFixture.Hash('e') };
    var badBoundary = Boundary(context.Boundary, pins);
    await Throws("AGGREGATE_STORE_DIGEST_MISMATCH", async () =>
        await new StatisticReconciliationActualAggregateAdapter().CaptureAsync(
            badBoundary,
            context.Membership,
            context.Direct,
            new AggregateReader([extra], [], [])));
}

static async Task OrderingMultiplicityAndZeroWriteBoundary()
{
    Equal(
        "70bc609c45f7e5684eb02f16cd586ab6921f8f6bb2cf82fa232046af956c713a",
        IndependentDigest(StatisticReconciliationActualAggregateStores.Field, Array.Empty<WorkReportFieldStatAggregate>()).Sha256,
        "empty P9 store known vector");
    var context = await Context();
    var first = Field(context, "field-a", "a", "a");
    var second = Field(context, "field-b", "b", "b");
    var forward = await Capture(context, [first, second], [], []);
    var reverse = await Capture(context, [second, first], [], []);
    Equal(2, forward.TotalRowCount, "multiplicity");
    Equal(forward.CaptureSemanticSha256, reverse.CaptureSemanticSha256, "capture ordering");
    SequenceEqual(
        forward.FieldRows.Select(x => x.OwnerRowId),
        reverse.FieldRows.Select(x => x.OwnerRowId),
        "physical shuffle");
    Equal("a", forward.FieldRows[0].FieldKey, "P9 result order first");
    Equal("b", forward.FieldRows[1].FieldKey, "P9 result order second");
    var methods = typeof(IStatisticReconciliationActualAggregateOwnerReader).GetMethods();
    Equal(3, methods.Length, "three read-only owner methods");
    True(methods.All(x => x.Name.StartsWith("Read", StringComparison.Ordinal)), "read-only method names");
}

static async Task<AggregateContext> Context()
{
    var source = ActualFixture.Source(ActualFixture.Report1);
    var membership = await ActualFixture.CaptureSources([source]);
    var boundary = ActualFixture.Boundary();
    var direct = await ActualFixture.CaptureDirect(boundary, membership);
    return new AggregateContext(boundary, source, membership, direct);
}

static async Task<ActualAggregateCapture> Capture(
    AggregateContext context,
    IReadOnlyList<WorkReportFieldStatAggregate> fields,
    IReadOnlyList<WorkReportTableStatAggregate> tables,
    IReadOnlyList<WorkReportLabelStatAggregate> labels)
    => await new StatisticReconciliationActualAggregateAdapter().CaptureAsync(
        Boundary(context.Boundary, Pins(fields, tables, labels)),
        context.Membership,
        context.Direct,
        new AggregateReader(fields, tables, labels));

static ActualAggregatePublicationBoundary Boundary(
    ActualDirectProjectionBoundary direct,
    IReadOnlyList<ActualAggregateStoreDigestPin> pins)
    => new(
        direct,
        "DIRECT:fixture:work:period:template",
        7,
        "FRESH",
        new DateTime(2026, 8, 10, 12, 1, 0, DateTimeKind.Utc),
        pins.ToImmutableArray());

static ActualAggregateStoreDigestPin[] Pins(
    IReadOnlyList<WorkReportFieldStatAggregate> fields,
    IReadOnlyList<WorkReportTableStatAggregate> tables,
    IReadOnlyList<WorkReportLabelStatAggregate> labels)
    =>
    [
        IndependentDigest(StatisticReconciliationActualAggregateStores.Field, fields),
        IndependentDigest(StatisticReconciliationActualAggregateStores.RowLabel, labels),
        IndependentDigest(StatisticReconciliationActualAggregateStores.TableMetric, tables)
    ];

static WorkReportFieldStatAggregate Field(
    AggregateContext context,
    string fieldId,
    string fieldKey,
    string? bucketKey,
    ActualSourceOwnerRevision? source = null)
{
    source ??= context.Source;
    var row = new WorkReportFieldStatAggregate
    {
        WorkId = ActualFixture.WorkId,
        ScopeType = "SYSTEM",
        ScopeId = ActualFixture.WorkId,
        RootAssignmentId = ActualFixture.Assignment1,
        DynamicFormTemplateId = ActualFixture.TemplateId,
        DynamicFormTemplateCode = "FORM-A",
        DynamicFormTemplateName = "Form A",
        FieldId = fieldId,
        FieldKey = fieldKey,
        FieldLabel = fieldKey,
        FieldType = "NUMBER",
        StatisticLabelCodes = ["finance"],
        ShowInTree = true,
        ShowInDetail = true,
        BucketKey = bucketKey,
        BucketLabel = bucketKey,
        PeriodKey = "2026-08",
        PeriodInstanceKey = ActualFixture.PeriodInstanceKey,
        PeriodKind = "MONTH",
        PeriodAnchorDate = Utc(1),
        PeriodStartDate = Utc(1),
        PeriodEndDate = Utc(31),
        CompletedDate = Utc(31),
        ReportStatus = 3,
        IsHistoricalData = false,
        ReportCount = 1,
        ValueCount = 1,
        NumericValueCount = 1,
        Sum = 1,
        Min = 1,
        Max = 1,
        DirectProjection = Pin(context.Boundary, source)
    };
    row.Id = ActualFixture.P9OwnerStableObjectId(
        context.Boundary.GenerationId,
        "FIELD_AGGREGATE",
        row.WorkId, row.ScopeType, row.ScopeId, row.DynamicFormTemplateId,
        row.FieldId, row.BucketKey, row.PeriodInstanceKey,
        row.ReportStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return row;
}

static WorkReportTableStatAggregate Table(
    AggregateContext context,
    string blockId,
    string metricKey,
    string? bucketKey)
{
    var row = new WorkReportTableStatAggregate
    {
        WorkId = ActualFixture.WorkId,
        ScopeType = "SYSTEM",
        ScopeId = ActualFixture.WorkId,
        RootAssignmentId = ActualFixture.Assignment1,
        DynamicFormTemplateId = ActualFixture.TemplateId,
        DynamicFormTemplateCode = "FORM-A",
        DynamicFormTemplateName = "Form A",
        DynamicExcelTemplateId = ActualFixture.ExcelTemplateId,
        BlockId = blockId,
        TableMode = "FIXED_GRID",
        MetricKey = metricKey,
        MetricLabelCode = "metric-code",
        RowKey = "row-1",
        ColumnKey = "column-1",
        DataType = "NUMBER",
        BucketKey = bucketKey,
        BucketLabel = bucketKey,
        PeriodKey = "2026-08",
        PeriodInstanceKey = ActualFixture.PeriodInstanceKey,
        PeriodKind = "MONTH",
        PeriodAnchorDate = Utc(1),
        PeriodStartDate = Utc(1),
        PeriodEndDate = Utc(31),
        CompletedDate = Utc(31),
        ReportStatus = 3,
        ReportCount = 1,
        ValueCount = 1,
        NumericValueCount = 1,
        Sum = 1,
        Min = 1,
        Max = 1,
        DirectProjection = Pin(context.Boundary, context.Source)
    };
    row.Id = ActualFixture.P9OwnerStableObjectId(
        context.Boundary.GenerationId,
        "TABLE_AGGREGATE",
        row.WorkId, row.ScopeType, row.ScopeId, row.DynamicFormTemplateId,
        row.DynamicExcelTemplateId, row.BlockId, row.TableMode, row.MetricKey,
        row.MetricLabelCode, row.DataType, row.BucketKey, row.PeriodInstanceKey,
        row.ReportStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return row;
}

static WorkReportLabelStatAggregate Label(
    AggregateContext context,
    string blockId,
    string labelCode)
{
    var row = new WorkReportLabelStatAggregate
    {
        WorkId = ActualFixture.WorkId,
        ScopeType = "SYSTEM",
        ScopeId = ActualFixture.WorkId,
        RootAssignmentId = ActualFixture.Assignment1,
        DynamicFormTemplateId = ActualFixture.TemplateId,
        DynamicFormTemplateCode = "FORM-A",
        DynamicFormTemplateName = "Form A",
        DynamicExcelTemplateId = ActualFixture.ExcelTemplateId,
        BlockId = blockId,
        LabelCode = labelCode,
        PeriodKey = "2026-08",
        PeriodInstanceKey = ActualFixture.PeriodInstanceKey,
        PeriodKind = "MONTH",
        PeriodAnchorDate = Utc(1),
        PeriodStartDate = Utc(1),
        PeriodEndDate = Utc(31),
        CompletedDate = Utc(31),
        ReportStatus = 3,
        ReportCount = 1,
        RowCount = 1,
        DirectProjection = Pin(context.Boundary, context.Source)
    };
    row.Id = ActualFixture.P9OwnerStableObjectId(
        context.Boundary.GenerationId,
        "LABEL_AGGREGATE",
        row.WorkId, row.ScopeType, row.ScopeId, row.DynamicFormTemplateId,
        row.DynamicExcelTemplateId, row.BlockId, row.LabelCode,
        row.PeriodInstanceKey,
        row.ReportStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return row;
}

static WorkReportDirectProjectionPin Pin(
    ActualDirectProjectionBoundary boundary,
    ActualSourceOwnerRevision source)
    => new()
    {
        RunId = boundary.RunId,
        GenerationId = boundary.GenerationId,
        LifecycleEventKey = boundary.OwnerLifecycleEventKey,
        SourceReportId = source.ReportId,
        SourcePayloadRevision = source.PayloadRevision,
        SourcePayloadHash = source.PayloadSha256,
        SourceLifecycleRevision = source.LifecycleRevision,
        DirectSourceRevision = boundary.DirectSourceRevision,
        DynamicFormFamilyId = boundary.DynamicFormFamilyId,
        DynamicFormTemplateId = boundary.DynamicFormTemplateId,
        DynamicFormVersionNo = boundary.DynamicFormVersionNo,
        DynamicFormSchemaHash = boundary.DynamicFormSchemaSha256,
        ConfigId = boundary.ConfigId,
        ConfigVersionId = boundary.ConfigVersionId,
        ConfigVersionNo = boundary.ConfigVersionNo,
        ConfigRevision = boundary.ConfigRevision,
        ConfigHash = boundary.ConfigSha256,
        CandidateChainId = boundary.CandidateChainId,
        CatalogVersion = boundary.CatalogVersion,
        CatalogRawSha256 = boundary.CatalogRawSha256,
        CatalogSemanticSha256 = boundary.CatalogSemanticSha256,
        SchemaRawSha256 = boundary.SchemaRawSha256,
        SchemaSemanticSha256 = boundary.SchemaSemanticSha256,
        StageLockSha256 = boundary.StageLockSha256,
        SourceMembershipSignature = boundary.OwnerMembershipSignature,
        ComputedAtUtc = boundary.OwnerComputedAtUtc
    };

static WorkReportFieldStatAggregate Clone(WorkReportFieldStatAggregate value)
{
    var document = value.ToBsonDocument();
    return MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WorkReportFieldStatAggregate>(document);
}

static ActualAggregateStoreDigestPin IndependentDigest<T>(string store, IReadOnlyList<T> rows)
{
    var canonical = rows
        .Select(row =>
        {
            var document = row!.ToBsonDocument();
            foreach (var name in new[] { "createdAtUtc", "updatedAtUtc", "createdByUserId", "updatedByUserId" })
                document.Remove(name);
            var id = document["_id"].ToString();
            var json = Canonical(document).AsBsonDocument.ToJson(new JsonWriterSettings
            {
                OutputMode = JsonOutputMode.CanonicalExtendedJson,
                Indent = false
            });
            return (Id: id, Json: json);
        })
        .OrderBy(x => x.Id, StringComparer.Ordinal)
        .Select(x => x.Json)
        .ToArray();
    var material = $"P9_DIRECT_STORE_ROWS_V2\n{string.Join("\n", canonical)}";
    var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    return new ActualAggregateStoreDigestPin(store, canonical.LongLength, sha);
}

static BsonValue Canonical(BsonValue value)
{
    if (value.IsBsonDocument)
        return new BsonDocument(value.AsBsonDocument.Elements
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new BsonElement(x.Name, Canonical(x.Value))));
    return value.IsBsonArray
        ? new BsonArray(value.AsBsonArray.Select(Canonical))
        : value;
}

static DateTime Utc(int day) => new(2026, 8, day, 0, 0, 0, DateTimeKind.Utc);

static T Single<T>(IReadOnlyList<T> values, string name)
{
    Equal(1, values.Count, name);
    return values[0];
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected={expected} actual={actual}");
}

static void True(bool value, string name)
{
    if (!value)
        throw new InvalidOperationException($"{name}: expected true");
}

static void False(bool value, string name)
{
    if (value)
        throw new InvalidOperationException($"{name}: expected false");
}

static void Distinct(string one, string two, string three, string name)
{
    if (new[] { one, two, three }.Distinct(StringComparer.Ordinal).Count() != 3)
        throw new InvalidOperationException($"{name}: expected distinct hashes");
}

static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual, string name)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        throw new InvalidOperationException($"{name}: sequence mismatch");
}

static async Task Throws(string reason, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualObservationException ex) when (ex.Reason == reason)
    {
        return;
    }
    throw new InvalidOperationException($"expected failure {reason}");
}

internal sealed record AggregateContext(
    ActualDirectProjectionBoundary Boundary,
    ActualSourceOwnerRevision Source,
    ActualSourceMembershipCapture Membership,
    ActualDirectProjectionCapture Direct);

internal sealed class AggregateReader(
    IReadOnlyList<WorkReportFieldStatAggregate> fields,
    IReadOnlyList<WorkReportTableStatAggregate> tables,
    IReadOnlyList<WorkReportLabelStatAggregate> labels)
    : IStatisticReconciliationActualAggregateOwnerReader
{
    public Task<IReadOnlyList<WorkReportFieldStatAggregate>> ReadFieldGenerationAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken)
        => Task.FromResult(fields);

    public Task<IReadOnlyList<WorkReportTableStatAggregate>> ReadTableMetricGenerationAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken)
        => Task.FromResult(tables);

    public Task<IReadOnlyList<WorkReportLabelStatAggregate>> ReadRowLabelGenerationAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken)
        => Task.FromResult(labels);
}
