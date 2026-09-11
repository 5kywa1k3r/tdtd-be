using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var mapper = new StatisticReconciliationActualAdapterTypedMapper();
var cases = new (string Id, Action Run)[]
{
    ("P10-T26-MAP-01", DirectNonEmptyDeterministic),
    ("P10-T26-MAP-02", AggregateNonEmptyDeterministic),
    ("P10-T26-MAP-03", BasicNonEmptyDeterministic),
    ("P10-T26-MAP-04", AdvancedNonEmptyDeterministic),
    ("P10-T26-MAP-05", DiffNonEmptyDeterministic),
    ("P10-T26-MAP-06", ApiNonEmptyDeterministic),
    ("P10-T26-MAP-07", ExportNonEmptyDeterministic),
    ("P10-T26-MAP-08", MapperHasNoExpectedLedgerDependency),
    ("P10-T26-MAP-09", DirectApiExportTypedParity),
    ("P10-T26-MAP-10", ApiMissingAverageFailsClosed),
    ("P10-T26-MAP-11", ExportAverageTamperFailsClosed),
    ("P10-T26-MAP-12", ExportIdentityTamperFailsClosed),
    ("P10-T26-MAP-13", ExportMissingNamedCellsFailsClosed),
    ("P10-T26-MAP-14", AggregateSelectsAssignmentScope)
};

foreach (var item in cases)
{
    try
    {
        item.Run();
        Console.WriteLine($"PASS {item.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {item.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Console.WriteLine(
    "P10_T26_MAPPER_OK cases=14 layers=7 nonempty=true multiplicity=true shuffleDeterministic=true expectedLedgerDependency=false opaqueExact=true directViewTyped=true directViewTamperFailClosed=true");
return 0;

void DirectNonEmptyDeterministic()
{
    var first = DirectField("direct-a", "report-a", 2m);
    var second = DirectField("direct-b", "report-b", 5m);
    var left = DirectCapture([first, second]);
    var right = DirectCapture([second, first]);
    var mapped = mapper.MapDirect(left, "direct-owner", Hash("direct-version"));
    AssertStable(
        mapped,
        mapper.MapDirect(right, "direct-owner", Hash("direct-version")),
        "DIRECT_SHUFFLE");
    Require(mapped.Length == 8, "DIRECT_STANDARD_ATOMS");
    Require(mapped.Single(item => item.AtomKind == "COUNT").CanonicalValue == "2",
        "DIRECT_MULTIPLICITY");
    Require(mapped.Single(item => item.AtomKind == "SUM").CanonicalValue == "7",
        "DIRECT_SUM");
}

void AggregateNonEmptyDeterministic()
{
    var first = AggregateField("aggregate-a", "period-a", 7m);
    var second = AggregateField("aggregate-b", "period-b", 11m);
    var label = AggregateLabel();
    var left = AggregateCapture([first, second], [label]);
    var right = AggregateCapture([second, first], [label]);
    var mapped = mapper.MapAggregate(
        left, "scope-01", "aggregate-owner",
        Hash("aggregate-version"));
    AssertStable(
        mapped,
        mapper.MapAggregate(
            right, "scope-01", "aggregate-owner",
            Hash("aggregate-version")),
        "AGGREGATE_SHUFFLE");
    Require(mapped.Any(item => item.AtomKind == "SUM"),
        "AGGREGATE_EXPLICIT_MEASURE");
    Require(mapped.Any(item => item.MetricId.StartsWith(
        "OWNER_OPAQUE:AGGREGATE_ROW_LABEL:", StringComparison.Ordinal)),
        "AGGREGATE_ROW_LABEL_OPAQUE");
}

void AggregateSelectsAssignmentScope()
{
    var target = AggregateField("aggregate-target", "period-a", 7m);
    var work = target with
    {
        OwnerRowId = "aggregate-work",
        ScopeType = "WORK",
        ScopeId = "work-01",
        SemanticSha256 = Hash("aggregate-work")
    };
    var root = target with
    {
        OwnerRowId = "aggregate-root",
        ScopeType = "ROOT",
        ScopeId = "root-01",
        SemanticSha256 = Hash("aggregate-root")
    };
    var ancestor = target with
    {
        OwnerRowId = "aggregate-ancestor",
        ScopeId = "scope-ancestor",
        SemanticSha256 = Hash("aggregate-ancestor")
    };
    var targetOnly = mapper.MapAggregate(
        AggregateCapture([target], []),
        "scope-01", "aggregate-owner", Hash("aggregate-version"));
    var generation = mapper.MapAggregate(
        AggregateCapture([work, root, ancestor, target], []),
        "scope-01", "aggregate-owner", Hash("aggregate-version"));
    AssertStable(targetOnly, generation, "AGGREGATE_SCOPE_PARTITION");
}
void BasicNonEmptyDeterministic()
{
    var first = BasicItem(0, "fields", "field-a", "{\"value\":1}");
    var second = BasicItem(1, "tables", "table-a", "{\"value\":2}");
    var left = BasicCapture([first, second]);
    var right = BasicCapture([second, first]);
    var mapped = mapper.MapBasic(left, "basic-owner", Hash("basic-version"));
    AssertStable(
        mapped,
        mapper.MapBasic(right, "basic-owner", Hash("basic-version")),
        "BASIC_SHUFFLE");
    Require(mapped.Length == 2 && mapped.All(item =>
            item.Family == "BASIC" && item.AtomKind == "TEXT" &&
            item.MetricId.StartsWith("OWNER_OPAQUE:BASIC_ITEM:",
                StringComparison.Ordinal)),
        "BASIC_OPAQUE_EXACT");
}

void AdvancedNonEmptyDeterministic()
{
    var first = AdvancedNode("advanced-a", "2026-08-10", 2);
    var second = AdvancedNode("advanced-b", "2026-08-11", 3);
    var left = new ActualAdvancedCapture(
        null!, [first, second], 2, Hash("advanced-capture"));
    var right = left with { Nodes = [second, first] };
    var mapped = mapper.MapAdvanced(
        left, "advanced-owner", Hash("advanced-version"));
    AssertStable(
        mapped,
        mapper.MapAdvanced(
            right, "advanced-owner", Hash("advanced-version")),
        "ADVANCED_SHUFFLE");
    Require(mapped.Any(item => item.Family == "ADVANCED" &&
                               item.AtomKind == "COUNT"),
        "ADVANCED_EXPLICIT_COUNT");
    Require(mapped.Count(item => item.MetricId.StartsWith(
        "OWNER_OPAQUE:ADVANCED_NODE:", StringComparison.Ordinal)) == 2,
        "ADVANCED_OPAQUE_NODES");
}

void DiffNonEmptyDeterministic()
{
    var first = DiffRow("diff-a", 0, 7m, 5m);
    var second = DiffRow("diff-b", 1, 13m, 8m);
    var left = DiffCapture([first, second]);
    var right = DiffCapture([second, first]);
    var mapped = mapper.MapDiff(left, "diff-owner", Hash("diff-version"));
    AssertStable(
        mapped,
        mapper.MapDiff(right, "diff-owner", Hash("diff-version")),
        "DIFF_SHUFFLE");
    Require(mapped.Any(item => item.Family == "DIFF" &&
                               item.AtomKind == "SUM"),
        "DIFF_TYPED_SIDE");
    Require(mapped.Count(item => item.MetricId.StartsWith(
        "OWNER_OPAQUE:P9_DIFF_ROW_VERDICT:", StringComparison.Ordinal)) == 2,
        "DIFF_OPAQUE_VERDICTS");
}

void ApiNonEmptyDeterministic()
{
    var capture = ApiCapture([
        ApiPage(0, "row-a", DirectFieldJson(DirectFieldView(7m)))
    ]);
    var mapped = mapper.MapApi(capture, "api-owner", Hash("api-version"));
    AssertStable(
        mapped,
        mapper.MapApi(capture, "api-owner", Hash("api-version")),
        "API_REPEAT");
    var typed = DirectViewAtoms(mapped, "API");
    Require(typed.Length == 8, "API_DIRECT_FIELD_EIGHT_ATOMS");
    Require(typed.Single(item => item.AtomKind == "SUM").CanonicalValue == "7",
        "API_DIRECT_FIELD_SUM");
    Require(mapped.Any(item => item.Family == "API" &&
                               item.MetricId.StartsWith(
                                   "OWNER_OPAQUE:",
                                   StringComparison.Ordinal)),
        "API_OPAQUE_AUDIT_RETAINED");
}

void ExportNonEmptyDeterministic()
{
    var capture = DirectFieldExportCapture(DirectFieldView(7m));
    var mapped = mapper.MapExport(
        capture, "export-owner", Hash("export-version"));
    AssertStable(
        mapped,
        mapper.MapExport(
            capture, "export-owner", Hash("export-version")),
        "EXPORT_REPEAT");
    var typed = DirectViewAtoms(mapped, "EXPORT");
    Require(typed.Length == 8, "EXPORT_DIRECT_FIELD_EIGHT_ATOMS");
    Require(typed.Single(item => item.AtomKind == "MEAN").CanonicalValue == "7",
        "EXPORT_DIRECT_FIELD_MEAN");
    Require(mapped.Any(item => item.Family == "EXPORT" &&
                               item.MetricId.StartsWith(
                                   "OWNER_OPAQUE:",
                                   StringComparison.Ordinal)),
        "EXPORT_OPAQUE_AUDIT_RETAINED");
}

void DirectApiExportTypedParity()
{
    var row = DirectFieldView(7m);
    var api = DirectViewAtoms(
        mapper.MapApi(
            ApiCapture([ApiPage(0, "row-a", DirectFieldJson(row))]),
            "api-owner",
            Hash("api-version")),
        "API");
    var export = DirectViewAtoms(
        mapper.MapExport(
            DirectFieldExportCapture(row),
            "export-owner",
            Hash("export-version")),
        "EXPORT");
    Require(
        api.Select(ComparableSignature).SequenceEqual(
            export.Select(ComparableSignature),
            StringComparer.Ordinal),
        "DIRECT_VIEW_TYPED_SIGNATURE_MISMATCH");
}

void ApiMissingAverageFailsClosed()
{
    var row = DirectFieldView(7m);
    row.Average = null;
    RequireThrows(
        () => mapper.MapApi(
            ApiCapture([ApiPage(0, "row-a", DirectFieldJson(row))]),
            "api-owner",
            Hash("api-version")),
        "API_MISSING_AVERAGE_ACCEPTED");
}

void ExportAverageTamperFailsClosed()
{
    var row = DirectFieldView(7m);
    row.Average = 8m;
    RequireThrows(
        () => mapper.MapExport(
            DirectFieldExportCapture(row),
            "export-owner",
            Hash("export-version")),
        "EXPORT_AVERAGE_TAMPER_ACCEPTED");
}

void ExportIdentityTamperFailsClosed()
{
    var row = DirectFieldView(7m);
    row.FieldId = string.Empty;
    RequireThrows(
        () => mapper.MapExport(
            DirectFieldExportCapture(row),
            "export-owner",
            Hash("export-version")),
        "EXPORT_IDENTITY_TAMPER_ACCEPTED");
}

void ExportMissingNamedCellsFailsClosed()
{
    var capture = ExportCapture([
        ExportRow(1, "7"),
        ExportRow(2, "8")
    ]);
    RequireThrows(
        () => mapper.MapExport(
            capture,
            "export-owner",
            Hash("export-version")),
        "EXPORT_MISSING_NAMED_CELLS_ACCEPTED");
}

static ImmutableArray<StatisticReconciliationActualTypedObservation>
    DirectViewAtoms(
        ImmutableArray<StatisticReconciliationActualTypedObservation> values,
        string layer)
{
    var result = values.Where(item => item.Layer == layer &&
                                      item.Family == "DIRECT" &&
                                      !item.MetricId.StartsWith(
                                          "OWNER_OPAQUE:",
                                          StringComparison.Ordinal))
        .OrderBy(item => item.Ordinal)
        .ToImmutableArray();
    Require(result.Length > 0, $"{layer}_DIRECT_VIEW_ATOMS_MISSING");
    return result;
}

static string ComparableSignature(
    StatisticReconciliationActualTypedObservation value)
    => string.Join(
        "|",
        value.Family,
        value.Kind,
        value.MetricId,
        value.FieldId ?? "~",
        value.PeriodKey,
        value.AtomKind,
        value.ValueType,
        value.CanonicalValue,
        value.DecimalScale.ToString(CultureInfo.InvariantCulture),
        value.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
        value.ReportCount.ToString(CultureInfo.InvariantCulture),
        value.RowCount.ToString(CultureInfo.InvariantCulture),
        value.NumericValueCount.ToString(CultureInfo.InvariantCulture));

static void RequireThrows(Action action, string reason)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException)
    {
        return;
    }
    throw new InvalidOperationException(reason);
}

static FieldStatisticSummaryRow DirectFieldView(decimal value)
    => new()
    {
        WorkId = "work-01",
        ScopeType = "ASSIGNMENT",
        ScopeId = "scope-01",
        RootAssignmentId = "root-01",
        DynamicFormTemplateId = "form-01",
        DynamicFormTemplateCode = "FORM-01",
        DynamicFormTemplateName = "Form 01",
        FieldId = "field-01",
        FieldKey = "field-key-01",
        FieldLabel = "Field 01",
        FieldType = "NUMBER",
        StatisticLabelCodes = ["metric-01"],
        ShowInTree = true,
        ShowInDetail = true,
        BucketKey = "bucket-01",
        BucketLabel = "Bucket 01",
        PeriodKey = "2026-08",
        PeriodInstanceKey = "instance-2026-08",
        PeriodKind = "MONTH",
        ReportStatus = 4,
        ValueCount = 1,
        NumericValueCount = 1,
        Sum = value,
        Min = value,
        Max = value,
        Average = value,
        TrueCount = 0,
        FalseCount = 0,
        EarliestDateUtc = Utc(1),
        LatestDateUtc = Utc(1),
        ReportCount = 1,
        UpdatedAtUtc = Utc(2)
    };

static string DirectFieldJson(FieldStatisticSummaryRow row)
    => JsonSerializer.Serialize(
        row,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

static StatisticReconciliationActualExportCapture DirectFieldExportCapture(
    FieldStatisticSummaryRow source)
{
    var document = JsonSerializer.SerializeToElement(
        source,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var headers = new List<string> { "ordinal" };
    headers.AddRange(document.EnumerateObject().Select(value => value.Name));
    var cells = ImmutableArray.CreateBuilder<
        StatisticReconciliationActualExportCellObservation>(headers.Count);
    cells.Add(new(
        0,
        "ordinal",
        StatisticReconciliationActualExportValueTypes.Integer,
        StatisticReconciliationActualExportValueStates.Value,
        "1",
        0,
        false,
        Hash("export-cell-ordinal")));
    var index = 1;
    foreach (var property in document.EnumerateObject())
    {
        var (type, state, canonical, scale) = ExportValue(
            property.Name,
            property.Value);
        cells.Add(new(
            index,
            property.Name,
            type,
            state,
            canonical,
            scale,
            false,
            Hash($"export-cell-{index}-{property.Name}")));
        index++;
    }
    var row = new StatisticReconciliationActualExportRowObservation(
        1,
        cells.MoveToImmutable(),
        Hash("export-row-direct-field"));
    return new(
        "export-01",
        StatisticReconciliationActualExportFormats.Csv,
        StatRunExportResultKinds.DirectField,
        "run-01",
        Hash("export-result"),
        Hash("export-config"),
        Hash("export-source"),
        Hash("export-filter"),
        1,
        Hash("export-content"),
        Hash("export-manifest"),
        Hash("export-owner"),
        headers.ToImmutableArray(),
        [row],
        [new StatisticReconciliationActualExportTotalObservation(
            "totalRows",
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualExportValueStates.Value,
            "1",
            0,
            Hash("export-total"))],
        Hash("export-rows"),
        Hash("export-totals"),
        Hash("export-capture"),
        "instance-2026-08",
        "{}");
}

static (string Type, string State, string Canonical, int Scale) ExportValue(
    string name,
    JsonElement value)
{
    if (value.ValueKind == JsonValueKind.Null)
        return (
            StatisticReconciliationActualExportValueTypes.Text,
            StatisticReconciliationActualExportValueStates.Null,
            string.Empty,
            0);
    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        return (
            StatisticReconciliationActualExportValueTypes.Boolean,
            StatisticReconciliationActualExportValueStates.Value,
            value.GetBoolean() ? "true" : "false",
            0);
    if (value.ValueKind == JsonValueKind.Number)
    {
        var canonical = value.GetRawText();
        var point = canonical.IndexOf('.');
        return (
            StatisticReconciliationActualExportValueTypes.Decimal,
            StatisticReconciliationActualExportValueStates.Value,
            canonical,
            point < 0 ? 0 : canonical.Length - point - 1);
    }
    if (value.ValueKind == JsonValueKind.String)
        return (
            name.EndsWith("Utc", StringComparison.Ordinal) ||
            name.EndsWith("Date", StringComparison.Ordinal)
                ? StatisticReconciliationActualExportValueTypes.UtcInstant
                : StatisticReconciliationActualExportValueTypes.Text,
            StatisticReconciliationActualExportValueStates.Value,
            value.GetString() ?? string.Empty,
            0);
    return (
        StatisticReconciliationActualExportValueTypes.Json,
        StatisticReconciliationActualExportValueStates.Value,
        value.GetRawText(),
        0);
}
void MapperHasNoExpectedLedgerDependency()
{
    var relative = Path.Combine(
        "tdtd-be", "Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualAdapterTypedMapper.cs");
    var candidates = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    };
    string? path = null;
    foreach (var candidate in candidates)
    {
        var directory = new DirectoryInfo(candidate);
        while (directory is not null)
        {
            var file = Path.Combine(directory.FullName, relative);
            if (File.Exists(file))
            {
                path = file;
                break;
            }
            directory = directory.Parent;
        }
        if (path is not null)
            break;
    }
    Require(path is not null, "MAPPER_SOURCE_NOT_FOUND");
    var source = File.ReadAllText(path!);
    Require(!source.Contains("ExpectedLedger", StringComparison.Ordinal),
        "EXPECTED_LEDGER_DEPENDENCY_FORBIDDEN");
}

static ActualDirectProjectionCapture DirectCapture(
    ImmutableArray<ActualFieldProjectionObservation> rows)
    => new(
        null!,
        Hash("direct-source-set"),
        Hash("direct-boundary"),
        rows,
        [],
        [],
        rows.Length,
        Hash("direct-capture"));

static ActualFieldProjectionObservation DirectField(
    string suffix,
    string reportId,
    decimal value)
{
    var canonical = value.ToString("0.#############################",
        System.Globalization.CultureInfo.InvariantCulture);
    return new ActualFieldProjectionObservation(
        suffix,
        "assignment-01",
        reportId,
        "2026-08",
        "period-instance-01",
        "form-01",
        "field-01",
        "field-key-01",
        "NUMBER",
        "metric-01",
        ["metric-01"],
        null,
        "field-01",
        new ActualDirectTypedValue(
            "NUMBER", canonical, null, value, null, null, null,
            Hash($"typed-{suffix}")),
        null!,
        null!,
        Hash("direct-stable"),
        Hash($"occurrence-{suffix}"),
        Hash($"direct-semantic-{suffix}"));
}

static ActualAggregateCapture AggregateCapture(
    ImmutableArray<ActualFieldAggregateObservation> fields,
    ImmutableArray<ActualRowLabelAggregateObservation> labels)
    => new(
        null!,
        Hash("aggregate-source-set"),
        Hash("aggregate-direct"),
        Hash("aggregate-filter"),
        Hash("aggregate-config"),
        Hash("aggregate-generation"),
        fields,
        [],
        labels,
        fields.Length + labels.Length,
        Hash("aggregate-capture"));

static ActualFieldAggregateObservation AggregateField(
    string suffix,
    string periodKey,
    decimal value)
    => new(
        suffix,
        "work-01",
        "ASSIGNMENT",
        "scope-01",
        null,
        "form-01",
        null,
        null,
        "field-01",
        "field-key-01",
        "Field 01",
        "NUMBER",
        ["metric-01"],
        true,
        true,
        null,
        null,
        AggregateTime(periodKey),
        new ActualAggregateCounts(1, 1, 1),
        new ActualAggregateMeasures(value, value, value, value, 0, 0, null, null),
        null!,
        null!,
        null!,
        Hash($"aggregate-semantic-{suffix}"));

static ActualRowLabelAggregateObservation AggregateLabel()
    => new(
        "label-owner-01",
        "work-01",
        "ASSIGNMENT",
        "scope-01",
        null,
        "form-01",
        null,
        null,
        "excel-01",
        "block-01",
        "label-01",
        AggregateTime("2026-08"),
        new ActualAggregateCounts(1, 2, 0),
        null!,
        null!,
        null!,
        Hash("aggregate-label-semantic"));

static ActualAggregateTimePin AggregateTime(string periodKey)
    => new(
        periodKey,
        $"instance-{periodKey}",
        "MONTH",
        null,
        null,
        null,
        null,
        false,
        4);

static ActualBasicPayloadItemObservation BasicItem(
    int ordinal,
    string section,
    string identity,
    string json)
    => new(section, ordinal, identity, json, Hash($"basic-item-{ordinal}"));

static ActualBasicResultObservation BasicCapture(
    ImmutableArray<ActualBasicPayloadItemObservation> items)
{
    var boundary = new ActualBasicOwnerBoundary(
        "snapshot-01",
        "work-01",
        "scope-01",
        "form-01",
        "FLOW_FINAL",
        "flow-instance-01",
        null,
        null,
        "EFFECTIVE",
        Hash("basic-request"),
        "config-01",
        "config-version-01",
        1,
        1,
        Hash("basic-config"),
        [],
        "chain-01",
        "P10-03",
        3,
        Hash("catalog-raw"),
        Hash("catalog-semantic"),
        Hash("catalog-lock"));
    return new ActualBasicResultObservation(
        boundary,
        "run-01",
        "{}",
        Hash("request-raw"),
        Hash("request-canonical"),
        "BASIC_SUMMARY",
        1,
        "{}",
        Hash("snapshot-raw"),
        Hash("snapshot-canonical"),
        ["assignment-01"],
        ["report-01"],
        Hash("assignment-set"),
        Hash("report-set"),
        Hash("source-signature"),
        items,
        null!,
        Hash("basic-capture"));
}

static ActualAdvancedNodeObservation AdvancedNode(
    string ownerNodeId,
    string grainKey,
    long valueCount)
{
    var field = new ActualAdvancedFieldObservation(
        0,
        "field-01",
        "field-key-01",
        "Field 01",
        "NUMBER",
        "SUM",
        valueCount,
        1,
        $"{{\"sum\":{valueCount}}}",
        [valueCount.ToString()],
        Hash($"advanced-field-{ownerNodeId}"));
    var value = new ActualAdvancedValueObservation(
        1,
        "ADVANCED_SUMMARY_DAY_NODE_V1",
        Utc(1),
        "config-01",
        Hash("advanced-config"),
        "DAY",
        grainKey,
        grainKey,
        grainKey[..7],
        grainKey[..4],
        Utc(1),
        Utc(2),
        "FLOW_FINAL",
        "flow-instance-01",
        null,
        null,
        "EFFECTIVE",
        1,
        1,
        1,
        1,
        1,
        0,
        [],
        [field],
        Hash($"advanced-value-{ownerNodeId}"));
    return new ActualAdvancedNodeObservation(
        "day-store",
        ownerNodeId,
        "DAY",
        grainKey,
        grainKey,
        grainKey[..7],
        grainKey[..4],
        Utc(1),
        Utc(2),
        Hash($"advanced-source-{ownerNodeId}"),
        1,
        ["report-01"],
        [],
        $"{{\"node\":\"{ownerNodeId}\"}}",
        Hash($"advanced-stored-{ownerNodeId}"),
        Hash($"advanced-observed-{ownerNodeId}"),
        Hash($"advanced-canonical-{ownerNodeId}"),
        Utc(2),
        value,
        null!,
        Hash($"advanced-node-{ownerNodeId}"));
}

static ActualP9DiffRowObservation DiffRow(
    string ownerRowId,
    int ordinal,
    decimal left,
    decimal right)
{
    var leftValue = DiffValue(left, $"{ownerRowId}-left");
    var rightValue = DiffValue(right, $"{ownerRowId}-right");
    return new ActualP9DiffRowObservation(
        ownerRowId,
        ordinal,
        "FIELD:field-01",
        "FIELD",
        "field-01",
        leftValue,
        rightValue,
        false,
        "VALUE_CHANGED",
        left - right,
        true,
        true,
        "NOT_COMPUTED",
        null,
        Hash($"diff-row-{ownerRowId}"));
}

static ActualP9DiffTypedObservation DiffValue(decimal value, string suffix)
    => new(
        "VALUE",
        "NUMBER",
        value.ToString("0.#############################",
            System.Globalization.CultureInfo.InvariantCulture),
        value,
        null,
        null,
        [],
        true,
        Hash($"diff-value-{suffix}"));

static ActualP9DiffCapture DiffCapture(
    ImmutableArray<ActualP9DiffRowObservation> rows)
    => new(
        null!,
        "FIELD",
        "field-01",
        "metric-01",
        "NUMBER",
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\",\"periodKeyFrom\":null,\"periodKeyTo\":null}",
        "FIELD",
        "field-01",
        "metric-01",
        "NUMBER",
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-07\",\"periodKeyFrom\":null,\"periodKeyTo\":null}",
        "LEFT_TO_RIGHT",
        "INCLUDE",
        "INCLUDE",
        "UTC_GREGORIAN",
        [
            new ActualP9DiffSourcePinObservation(
                0, "LEFT", "report-left", 1, Hash("payload-left"), 1,
                "run-left", Hash("generation-left"), Hash("pin-left")),
            new ActualP9DiffSourcePinObservation(
                1, "RIGHT", "report-right", 1, Hash("payload-right"), 1,
                "run-right", Hash("generation-right"), Hash("pin-right"))
        ],
        rows,
        Hash("diff-stored"),
        Hash("diff-observed"),
        null!,
        "NOT_COMPUTED",
        null,
        Hash("diff-capture"));

static StatisticReconciliationActualApiPageObservation ApiPage(
    int ordinal,
    string identity,
    string json)
    => new(
        ordinal,
        200,
        1,
        1,
        1,
        [new StatisticReconciliationActualApiTotalValue(
            "totalRows", "INTEGER", "1")],
        [new StatisticReconciliationActualApiRowObservation(
            ordinal, identity, json, Hash($"api-row-{identity}"), true)],
        $"etag-{ordinal}",
        "generation-01",
        Hash("api-generation"),
        true,
        true,
        true,
        true,
        true,
        Hash($"api-page-{ordinal}"));

static StatisticReconciliationActualApiCapture ApiCapture(
    ImmutableArray<StatisticReconciliationActualApiPageObservation> pages)
    => new(
        StatisticReconciliationActualApiSurfaces.DirectField,
        StatisticReconciliationActualApiProtocol.RouteId(
            StatisticReconciliationActualApiSurfaces.DirectField),
        "work-01",
        "scope-01",
        "form-01",
        "run-01",
        "{}",
        Hash("api-filter"),
        new StatisticReconciliationActualApiAuthorizationContext(
            "actor-01",
            "work-01",
            "scope-01",
            ["READ"],
            1,
            1,
            Hash("api-auth"),
            true),
        pages,
        true,
        true,
        true,
        true,
        StatisticReconciliationActualApiCaptureStates.Ready,
        null,
        Hash("api-capture"));

static StatisticReconciliationActualExportRowObservation ExportRow(
    int ordinal,
    string value)
    => new(
        ordinal,
        [new StatisticReconciliationActualExportCellObservation(
            0,
            "value",
            StatisticReconciliationActualExportValueTypes.Text,
            StatisticReconciliationActualExportValueStates.Value,
            value,
            0,
            true,
            Hash($"export-cell-{ordinal}"))],
        Hash($"export-row-{ordinal}"));

static StatisticReconciliationActualExportCapture ExportCapture(
    ImmutableArray<StatisticReconciliationActualExportRowObservation> rows)
    => new(
        "export-01",
        StatisticReconciliationActualExportFormats.Csv,
        StatRunExportResultKinds.DirectField,
        "result-01",
        Hash("export-result"),
        Hash("export-config"),
        Hash("export-source"),
        Hash("export-filter"),
        1,
        Hash("export-content"),
        Hash("export-manifest"),
        Hash("export-owner"),
        ["value"],
        rows,
        [new StatisticReconciliationActualExportTotalObservation(
            "totalCount",
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualExportValueStates.Value,
            "2",
            0,
            Hash("export-total"))],
        Hash("export-rows"),
        Hash("export-totals"),
        Hash("export-capture"));

static void AssertStable(
    ImmutableArray<StatisticReconciliationActualTypedObservation> left,
    ImmutableArray<StatisticReconciliationActualTypedObservation> right,
    string reason)
{
    Require(left.Length > 0, $"{reason}_NONEMPTY");
    Require(left.SequenceEqual(right), $"{reason}_ROWS");
    Require(
        StatisticReconciliationActualTypedObservationCanonical.ManifestSha256(left) ==
        StatisticReconciliationActualTypedObservationCanonical.ManifestSha256(right),
        $"{reason}_MANIFEST");
}

static DateTime Utc(int day)
    => new(2026, 8, day, 0, 0, 0, DateTimeKind.Utc);

static string Hash(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}
