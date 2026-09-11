using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class BasicProductionCases
{
    private const string PeriodKey = "2026-08";
    private const string ScopeId = "assignment-root";

    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions SnapshotWeb =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    internal static int Run()
    {
        Complete("P10-SOP-BP-01 number-field-production-shape",
            FieldFixture("amount", "field-amount", "NUMBER", 10m,
                ["COUNT", "MAX", "MEAN", "MIN", "SUM"], NumberItem));
        Failure("P10-SOP-BP-02 number-metadata-mutation",
            FieldFixture("amount", "field-amount", "NUMBER", 10m,
                ["COUNT", "MAX", "MEAN", "MIN", "SUM"], NumberItem,
                item => item.Sum = 11m),
            "BASIC_OWNER_NUMBER_METADATA_MISMATCH");

        Complete("P10-SOP-BP-03 date-field-production-shape",
            FieldFixture("when", "field-when", "DATE", "05/08/2026",
                ["COUNT", "MAX", "MIN"], DateItem));
        Failure("P10-SOP-BP-04 date-metadata-mutation",
            FieldFixture("when", "field-when", "DATE", "05/08/2026",
                ["COUNT", "MAX", "MIN"], DateItem,
                item => item.MinDateUtc = item.MinDateUtc!.Value.AddDays(1)),
            "BASIC_OWNER_DATE_METADATA_MISMATCH");

        Complete("P10-SOP-BP-05 boolean-field-production-shape",
            FieldFixture("approved", "field-approved", "BOOLEAN", true,
                ["COUNT", "VALUES"], BooleanItem));
        Failure("P10-SOP-BP-06 boolean-count-mutation",
            FieldFixture("approved", "field-approved", "BOOLEAN", true,
                ["COUNT", "VALUES"], BooleanItem,
                item => item.TrueCount = 2),
            "BASIC_OWNER_BOOLEAN_COUNT_MISMATCH");

        Complete("P10-SOP-BP-07 selection-field-production-shape",
            FieldFixture("status", "field-status", "BUCKET", "red",
                ["COUNT", "VALUES"], SelectionItem));
        Failure("P10-SOP-BP-08 selection-bucket-mutation",
            FieldFixture("status", "field-status", "BUCKET", "red",
                ["COUNT", "VALUES"], SelectionItem,
                item => item.Buckets[0].Count = 2),
            "BASIC_OWNER_SELECTION_BUCKET_MISMATCH");

        Complete("P10-SOP-BP-09 text-field-trim-production-shape",
            FieldFixture("notes", "field-notes", "TEXT", " alpha ",
                ["COUNT", "VALUES"], TextItem));
        Failure("P10-SOP-BP-10 text-metadata-mutation",
            FieldFixture("notes", "field-notes", "TEXT", " alpha ",
                ["COUNT", "VALUES"], TextItem,
                item => item.TextCharCount = 6),
            "BASIC_OWNER_TEXT_MISMATCH");

        Complete("P10-SOP-BP-11 compact-table-null-runs-production-shape",
            CompactTableFixture(malformedNullRun: false));
        Failure("P10-SOP-BP-12 compact-table-null-runs-mutation",
            CompactTableFixture(malformedNullRun: true),
            "BASIC_NULL_RUNS_RANGE_INVALID");

        Failure("P10-SOP-BP-13 raw-report-id-envelope-mutation",
            FieldFixture("amount", "field-amount", "NUMBER", 10m,
                ["COUNT", "MAX", "MEAN", "MIN", "SUM"], NumberItem,
                ownerReportIds: ["report-x"]),
            "BASIC_SOURCE_REPORT_IDS_MISMATCH");

        var rangeRequest = Canonical(JsonSerializer.Serialize(new
        {
            periodScopeMode = "PERIOD_RANGE",
            periodKeyFrom = "2026-07",
            periodKeyTo = PeriodKey,
            maxTextChars = 12000
        }, Web));
        Failure("P10-SOP-BP-14 period-range-identity-unprovable",
            FieldFixture("amount", "field-amount", "NUMBER", 10m,
                ["COUNT", "MAX", "MEAN", "MIN", "SUM"], NumberItem,
                requestJson: rangeRequest),
            "BASIC_PERIOD_RANGE_IDENTITY_UNPROVABLE");

        Complete("P10-SOP-BP-15 unsorted-production-source-ids-and-text-order",
            MultiSourceTextFixture(
                rawReportIds: ["report-a", "report-z"],
                ownerReportIds: ["report-z", "report-a"],
                ownerAssignmentIds: ["assignment-z", "assignment-a"]));
        Failure("P10-SOP-BP-16 duplicate-raw-report-id",
            MultiSourceTextFixture(
                rawReportIds: ["report-z", "report-z"],
                ownerReportIds: ["report-z", "report-a"],
                ownerAssignmentIds: ["assignment-z", "assignment-a"]),
            "BASIC_SOURCE_REPORT_IDS_MISMATCH");
        Failure("P10-SOP-BP-17 one-bit-owner-report-id-mutation",
            MultiSourceTextFixture(
                rawReportIds: ["report-a", "report-z"],
                ownerReportIds: ["report-z", "report-b"],
                ownerAssignmentIds: ["assignment-z", "assignment-a"]),
            "BASIC_SOURCE_REPORT_IDS_MISMATCH");

        return 17;
    }

    private static ParityFixture MultiSourceTextFixture(
        string[] rawReportIds,
        string[] ownerReportIds,
        string[] ownerAssignmentIds)
    {
        const string metricId = "notes";
        var plan = Plan("FIELD", metricId, "field-notes", null,
            "/fieldValues/values/notes", "TEXT", ["COUNT", "VALUES"]);
        var sources = new[]
        {
            Source("assignment-a", rawReportIds[0], "payload-a",
                Payload(metricId, "alpha")),
            Source("assignment-z", rawReportIds[1], "payload-z",
                Payload(metricId, "zeta"))
        }.OrderBy(value => value.SourceStableIdentitySha256,
            StringComparer.Ordinal).ToImmutableArray();
        var item = TextItem(plan.IdentityDescriptors.Single());
        var joined = $"zeta{Environment.NewLine}alpha";
        item.Value = joined;
        item.ValueCount = 2;
        item.ReportCount = 2;
        item.Text = joined;
        item.TextCharCount = 9;
        var owner = BasicOwner(sources, SinglePeriodRequest(),
            [JsonSerializer.SerializeToElement(item, SnapshotWeb)], [],
            ownerReportIds, ownerAssignmentIds);
        return new ParityFixture(plan, Raw(plan, sources),
            new StatisticReconciliationActualSummaryOwnerParityActual(
                owner, null, null));
    }

    private static ParityFixture FieldFixture(
        string metricId,
        string fieldId,
        string valueType,
        object sourceValue,
        string[] operations,
        Func<StatisticReconciliationActualSummaryIdentityDescriptor,
            WorkAssignmentBasicSummaryItemDto> itemFactory,
        Action<WorkAssignmentBasicSummaryItemDto>? mutate = null,
        string? requestJson = null,
        string[]? ownerReportIds = null)
    {
        var pointer = $"/fieldValues/values/{metricId}";
        var plan = Plan("FIELD", metricId, fieldId, null, pointer,
            valueType, operations);
        var sources = ImmutableArray.Create(Source(0,
            Payload(metricId, sourceValue)));
        var item = itemFactory(plan.IdentityDescriptors.Single());
        mutate?.Invoke(item);
        var owner = BasicOwner(sources,
            requestJson ?? SinglePeriodRequest(),
            [JsonSerializer.SerializeToElement(item, SnapshotWeb)], [],
            ownerReportIds);
        return new ParityFixture(plan, Raw(plan, sources),
            new StatisticReconciliationActualSummaryOwnerParityActual(
                owner, null, null));
    }

    private static ParityFixture CompactTableFixture(bool malformedNullRun)
    {
        const string blockId = "block-main";
        const string sourceKey = "tableCell";
        const string metricId =
            "table:block-main.row:row_256.column:col_1";
        var plan = Plan("TABLE", metricId, null, blockId,
            $"/fieldValues/values/{sourceKey}", "NUMBER",
            ["COUNT", "MAX", "MEAN", "MIN", "SUM"]);
        var sources = ImmutableArray.Create(Source(0, Payload(sourceKey, 10m)));
        var block = JsonSerializer.SerializeToElement(new
        {
            b = blockId,
            m = "FIXED_GRID",
            w = 1,
            n = 256,
            op = "SUM",
            vc = NullRunVector(1m, malformedNullRun ? 7 : 255),
            rc = NullRunVector(1m, 255),
            s = NullRunVector(10m, 255),
            mi = NullRunVector(10m, 255),
            ma = NullRunVector(10m, 255)
        }, SnapshotWeb);
        var owner = BasicOwner(sources, SinglePeriodRequest(), [], [block], null);
        return new ParityFixture(plan, Raw(plan, sources),
            new StatisticReconciliationActualSummaryOwnerParityActual(
                owner, null, null));
    }

    private static JsonElement NullRunVector(decimal tail, int runCount)
        => JsonSerializer.SerializeToElement(new
        {
            values1DCompressed = true,
            values1DCompression = "NULL_RUNS",
            values1DLength = 256,
            values1D = new decimal?[] { null, tail },
            values1DCompressedIndexes = new[] { 0 },
            values1DCompressedCounts = new[] { runCount }
        }, SnapshotWeb);

    private static WorkAssignmentBasicSummaryItemDto NumberItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
        => FieldItem(descriptor, "NUMBER", "SUM", 10m).WithValues(
            sum: 10m, min: 10m, max: 10m, mean: 10m);

    private static WorkAssignmentBasicSummaryItemDto DateItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        var date = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
        var item = FieldItem(descriptor, "DATE", "MIN_DATE", date);
        item.Sum = 0m;
        item.Mean = 0m;
        item.MinDateUtc = date;
        item.MaxDateUtc = date;
        return item;
    }

    private static WorkAssignmentBasicSummaryItemDto BooleanItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        var item = FieldItem(descriptor, "BOOLEAN", "TRUE_COUNT", 1);
        item.Sum = 0m;
        item.Mean = 0m;
        item.TrueCount = 1;
        return item;
    }

    private static WorkAssignmentBasicSummaryItemDto SelectionItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        var buckets = new List<WorkAssignmentBasicSummaryBucketDto>
        {
            new() { Key = "red", Label = "Red", Count = 1 }
        };
        var item = FieldItem(descriptor, "SINGLE_SELECT", "BUCKET_COUNT",
            buckets);
        item.Sum = 0m;
        item.Mean = 0m;
        item.Buckets = buckets;
        return item;
    }

    private static WorkAssignmentBasicSummaryItemDto TextItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        var item = FieldItem(descriptor, "TEXT", "JOIN", "alpha");
        item.Sum = 0m;
        item.Mean = 0m;
        item.Text = "alpha";
        item.TextCharCount = 5;
        return item;
    }

    private static WorkAssignmentBasicSummaryItemDto FieldItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string dataType,
        string operation,
        object value)
        => new()
        {
            TargetKind = "FIELD",
            TargetKey = descriptor.MetricId,
            FieldId = descriptor.FieldId,
            FieldKey = descriptor.MetricId,
            Label = descriptor.MetricId,
            DataType = dataType,
            Operation = operation,
            Value = value,
            ValueCount = 1,
            ReportCount = 1,
            Buckets = []
        };

    private static WorkAssignmentBasicSummaryItemDto WithValues(
        this WorkAssignmentBasicSummaryItemDto item,
        decimal? sum,
        decimal? min,
        decimal? max,
        decimal? mean)
    {
        item.Sum = sum;
        item.Min = min;
        item.Max = max;
        item.Mean = mean;
        return item;
    }

    private static StatisticReconciliationActualSummaryPlanBinding Plan(
        string kind,
        string metricId,
        string? fieldId,
        string? tableId,
        string pointer,
        string valueType,
        IEnumerable<string> requestedOperations)
    {
        var operations = requestedOperations
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                "BASIC", kind, metricId, PeriodKey, fieldId, tableId,
                null, null, "DIRECT_CHILDREN_OR_SELF", ScopeId));
        var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
            .BuildEntrySha256(identity, pointer, valueType, false, false,
                operations, StatisticReconciliationExpectedDiffTransitionModes.None,
                null, null, null, null);
        var planEntry =
            new StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
                identity, pointer, valueType, false, null, false, operations,
                StatisticReconciliationExpectedDiffTransitionModes.None,
                null, null, null, null, ["NONE"], entrySha);
        var descriptor = StatisticReconciliationActualSummaryIdentityDescriptor
            .Create(planEntry);
        var binding = new StatisticReconciliationExpectedGenerationBinding(
            "reconciliation-owner-parity-basic-production", Sha("generation"),
            Sha("generation-semantic"), Sha("metric-plan"), 1,
            Sha("manifest"), 8, Sha("membership"), "NON_FLOW");
        return StatisticReconciliationActualSummaryPlanBinding.Create(
            binding, [descriptor]);
    }

    private static StatisticReconciliationActualRawSummaryProof Raw(
        StatisticReconciliationActualSummaryPlanBinding plan,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources)
    {
        var atoms = new StatisticReconciliationActualRawSummaryCompiler()
            .Compile(plan, sources);
        var sourceManifest = Hs("P10_ACTUAL_RAW_SUMMARY_SOURCE_MANIFEST_V1",
            sources.Select(value => value.EnvelopeSemanticSha256));
        var atomManifest = Hs("P10_ACTUAL_RAW_SUMMARY_ATOM_MANIFEST_V1",
            atoms.Select(value => value.AtomSemanticSha256));
        var collect = Hs("P10_ACTUAL_RAW_SUMMARY_COLLECT_V1",
            sources.Select(value => value.EnvelopeSemanticSha256));
        var capture = Sha("actual-source-capture-basic-production");
        var membership = Sha("actual-membership-basic-production");
        var doubleCollect = H("P10_ACTUAL_RAW_SUMMARY_DOUBLE_COLLECT_V1",
            capture, collect, collect);
        var proof = H(
            StatisticReconciliationActualMongoRawSummaryOwner.ProofSchemaVersion,
            plan.SemanticSha256, capture, membership, sourceManifest,
            I(sources.Length), atomManifest, I(atoms.Length), doubleCollect);
        return new StatisticReconciliationActualRawSummaryProof(
            StatisticReconciliationActualMongoRawSummaryOwner.ProofSchemaVersion,
            plan.SemanticSha256, capture, membership, sources, sourceManifest,
            atoms, atomManifest, collect, collect, doubleCollect, proof);
    }

    private static StatisticReconciliationActualRawPayloadEnvelope Source(
        int index,
        string payload)
        => Source($"assignment-{index}", $"report-{index}",
            $"payload-{index}", payload);

    private static StatisticReconciliationActualRawPayloadEnvelope Source(
        string assignmentId,
        string reportId,
        string payloadId,
        string payload)
    {
        return new StatisticReconciliationActualRawPayloadEnvelope(
            H("P10_ACTUAL_SOURCE_STABLE_IDENTITY_V1", "work", assignmentId,
                reportId),
            "work", assignmentId, reportId, payloadId, 1,
            Sha($"payload-owner-{payloadId}"), RawSha(payload), payload,
            Sha($"envelope-{payloadId}-{payload}"));
    }

    private static ActualBasicResultObservation BasicOwner(
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources,
        string request,
        IReadOnlyList<JsonElement> fields,
        IReadOnlyList<JsonElement> blocks,
        string[]? ownerReportIds,
        string[]? ownerAssignmentIds = null)
    {
        var snapshot = Canonical(JsonSerializer.Serialize(new
        {
            v = 9,
            meta = new
            {
                summaryType = "BASIC",
                snapshotPayloadKind = "COMPACT_VALUES1D_OPTIMIZED"
            },
            fields,
            tb = blocks,
            warnings = Array.Empty<string>()
        }, SnapshotWeb));
        var resultItems = SnapshotItems(snapshot);
        var assignments = (ownerAssignmentIds ?? sources
                .Select(value => value.WorkAssignmentId)
                .Distinct(StringComparer.Ordinal).ToArray())
            .ToImmutableArray();
        var reports = (ownerReportIds ?? sources.Select(value => value.ReportId)
            .ToArray()).ToImmutableArray();
        var boundary = new ActualBasicOwnerBoundary(
            "snapshot", "work", ScopeId, "form",
            "DIRECT_CHILDREN_OR_SELF", null, null, null, null,
            RawSha(request), "config", "config-version", 1, 1,
            Sha("config"), [], "chain", "P9-04", 2,
            "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509",
            "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1",
            "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad");
        var state = new ActualBasicOwnerState("DONE", false, false, true,
            true, false, true,
            IsCanonicalIdSequence(assignments),
            IsCanonicalIdSequence(reports),
            assignments.Distinct(StringComparer.Ordinal).Count() ==
                assignments.Length &&
            reports.Distinct(StringComparer.Ordinal).Count() == reports.Length,
            Sha("basic-production-lifecycle"));
        var draft = new ActualBasicResultObservation(
            boundary, "snapshot", request, RawSha(request), RawSha(request),
            "COMPACT_VALUES1D_V9", 9, snapshot, RawSha(snapshot),
            RawSha(snapshot), assignments, reports,
            SetSha("P10_ACTUAL_BASIC_ASSIGNMENT_SET_V1", assignments),
            SetSha("P10_ACTUAL_BASIC_REPORT_SET_V1", reports),
            Sha("source-signature-basic-production"), resultItems, state, "~");
        return draft with
        {
            CaptureSemanticSha256 =
                StatisticReconciliationActualSummaryOwnerParity
                    .BasicCaptureSemantic(draft)
        };
    }

    private static bool IsCanonicalIdSequence(
        ImmutableArray<string> values)
        => values.SequenceEqual(values.OrderBy(value => value,
            StringComparer.Ordinal), StringComparer.Ordinal);

    private static string SetSha(
        string domain,
        ImmutableArray<string> values)
        => Hs(domain, values.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));

    private static ImmutableArray<ActualBasicPayloadItemObservation>
        SnapshotItems(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        var items = ImmutableArray.CreateBuilder<
            ActualBasicPayloadItemObservation>();
        AddSnapshotItems(items, "FIELD",
            document.RootElement.GetProperty("fields"));
        AddSnapshotItems(items, "TABLE_BLOCK",
            document.RootElement.GetProperty("tb"));
        return items.ToImmutable();
    }

    private static void AddSnapshotItems(
        ImmutableArray<ActualBasicPayloadItemObservation>.Builder output,
        string section,
        JsonElement array)
    {
        var ordinal = 0;
        foreach (var value in array.EnumerateArray())
        {
            var canonical = Canonical(value.GetRawText());
            var identity = FirstString(value, "fieldKey", "targetKey", "b",
                "blockId", "metricKey") ?? $"ordinal:{ordinal}";
            output.Add(new ActualBasicPayloadItemObservation(
                section, ordinal, identity, canonical,
                H("P10_ACTUAL_BASIC_RESULT_ITEM_V1", section, I(ordinal),
                    identity, RawSha(canonical))));
            ordinal++;
        }
    }

    private static string? FirstString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.TryGetProperty(name, out var found) &&
                found.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(found.GetString()))
                return found.GetString();
        return null;
    }

    private static string Payload(string key, object value)
        => Canonical(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fieldValues"] = new Dictionary<string, object?>
            {
                ["values"] = new Dictionary<string, object?> { [key] = value }
            }
        }, Web));

    private static string SinglePeriodRequest()
        => Canonical(JsonSerializer.Serialize(new
        {
            periodScopeMode = "SINGLE_PERIOD",
            periodKey = PeriodKey,
            maxTextChars = 12000
        }, Web));

    private static StatisticReconciliationActualSummaryOwnerParityProof Prove(
        ParityFixture fixture)
        => StatisticReconciliationActualSummaryOwnerParity.Prove(
            new StatisticReconciliationActualSummaryOwnerParityPlan(
                fixture.Plan, fixture.Raw), fixture.Actual);

    private static void Complete(string id, ParityFixture fixture)
    {
        var proof = Prove(fixture);
        if (!proof.Complete || proof.FailureCode is not null ||
            proof.ProofSha256.Length != 64)
            throw new InvalidOperationException(
                $"{id}: expected complete, got {proof.FailureCode}");
        Console.WriteLine($"PASS {id}");
    }

    private static void Failure(
        string id,
        ParityFixture fixture,
        string expected)
    {
        var proof = Prove(fixture);
        if (proof.Complete || proof.FailureCode != expected ||
            proof.ProofSha256.Length != 64)
            throw new InvalidOperationException(
                $"{id}: expected {expected}, got {proof.FailureCode}");
        Console.WriteLine($"PASS {id}");
    }

    private static string Canonical(string json)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json, "FIXTURE_JSON");
        return StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
    }

    private static string H(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    private static string Hs(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static string RawSha(string value)
        => StatisticReconciliationActualJson.RawSha256(value);
    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
