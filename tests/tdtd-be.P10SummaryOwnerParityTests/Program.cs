using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var basic = Fixture.Create("BASIC", [10m]);
Pass("P10-SOP-01 basic-positive", Fixture.Prove(basic));
var basicRootTamper = basic with
{
    Actual = basic.Actual with
    {
        Basic = basic.Actual.Basic! with
        {
            CaptureSemanticSha256 = new string('f', 64)
        }
    }
};
Fail("P10-SOP-17 basic-capture-root-tamper",
    Fixture.Prove(basicRootTamper),
    "BASIC_CAPTURE_ROOT_SEMANTIC_MISMATCH");

var wrongBasic = basic with
{
    Actual = basic.Actual with
    {
        Basic = Fixture.BasicActual(basic.Plan, 11m)
    }
};
Fail("P10-SOP-02 basic-one-bit-value", Fixture.Prove(wrongBasic),
    "BASIC_OWNER_NUMBER_METADATA_MISMATCH");

var extraBasic = basic with
{
    Actual = basic.Actual with
    {
        Basic = Fixture.BasicActual(basic.Plan, 10m, extra: true)
    }
};
Fail("P10-SOP-03 basic-extra-owner-item", Fixture.Prove(extraBasic),
    "BASIC_OWNER_PARTITION_CARDINALITY");

var compactBasic = basic with
{
    Actual = basic.Actual with
    {
        Basic = Fixture.BasicActual(basic.Plan, 10m, compactTable: true)
    }
};
Fail("P10-SOP-04 basic-compact-table", Fixture.Prove(compactBasic),
    "BASIC_OWNER_ITEMS_SNAPSHOT_MISMATCH");

var atom = basic.Raw.Atoms[0];
var tamperedRaw = basic with
{
    Raw = basic.Raw with
    {
        Atoms = basic.Raw.Atoms.SetItem(0,
            atom with { CanonicalValue = atom.CanonicalValue + "0" })
    }
};
Fail("P10-SOP-05 raw-atom-tamper", Fixture.Prove(tamperedRaw),
    "RAW_ATOM_SEMANTIC_MISMATCH");

var advanced = Fixture.Create("ADVANCED", [10m]);
Pass("P10-SOP-06 advanced-positive", Fixture.Prove(advanced));
var advancedRootTamper = advanced with
{
    Actual = advanced.Actual with
    {
        Advanced = advanced.Actual.Advanced! with
        {
            CaptureSemanticSha256 = new string('e', 64)
        }
    }
};
Fail("P10-SOP-18 advanced-capture-root-tamper",
    Fixture.Prove(advancedRootTamper),
    "ADVANCED_CAPTURE_ROOT_SEMANTIC_MISMATCH");

var wrongAdvanced = advanced with
{
    Actual = advanced.Actual with
    {
        Advanced = Fixture.AdvancedActual(advanced.Plan, 10m, valueCount: 2)
    }
};
Fail("P10-SOP-07 advanced-wrong-count", Fixture.Prove(wrongAdvanced),
    "ADVANCED_OWNER_COUNT_MISMATCH");

var extraAdvanced = advanced with
{
    Actual = advanced.Actual with
    {
        Advanced = Fixture.AdvancedActual(advanced.Plan, 10m, extra: true)
    }
};
Fail("P10-SOP-08 advanced-extra-field", Fixture.Prove(extraAdvanced),
    "ADVANCED_OWNER_FIELD_CARDINALITY");

var multiAdvanced = Fixture.Create("ADVANCED", [10m, 20m]);
Fail("P10-SOP-09 advanced-multi-source-fail-closed",
    Fixture.Prove(multiAdvanced), "ADVANCED_SOURCE_ORDER_UNPROVABLE");

var diff = Fixture.Create("DIFF", [4m], before: [1m]);
Pass("P10-SOP-10 diff-positive", Fixture.Prove(diff));
var diffRootTamper = diff with
{
    Actual = diff.Actual with
    {
        Diff = diff.Actual.Diff! with
        {
            CaptureSemanticSha256 = new string('d', 64)
        }
    }
};
Fail("P10-SOP-19 diff-capture-root-tamper",
    Fixture.Prove(diffRootTamper),
    "DIFF_CAPTURE_ROOT_SEMANTIC_MISMATCH");

var swappedDiff = diff with
{
    Actual = diff.Actual with
    {
        Diff = Fixture.DiffActual(diff.Plan, before: 4m, after: 1m)
    }
};
Fail("P10-SOP-11 diff-before-after-swap", Fixture.Prove(swappedDiff),
    "DIFF_OWNER_LEG_MISMATCH");

var wrongDelta = diff with
{
    Actual = diff.Actual with
    {
        Diff = Fixture.DiffActual(diff.Plan, before: 1m, after: 4m,
            declaredDelta: 4m)
    }
};
Fail("P10-SOP-12 diff-delta-mismatch", Fixture.Prove(wrongDelta),
    "DIFF_OWNER_COMPARISON_MISMATCH");

var extraDiff = diff with
{
    Actual = diff.Actual with
    {
        Diff = Fixture.DiffActual(diff.Plan, before: 1m, after: 4m,
            extra: true)
    }
};
Fail("P10-SOP-13 diff-extra-row", Fixture.Prove(extraDiff),
    "DIFF_OWNER_STATE_INCOMPLETE");

var absentA = basic with
{
    Actual = basic.Actual with
    {
        Advanced = Fixture.AbsentAdvanced('a')
    }
};
var absentB = basic with
{
    Actual = basic.Actual with
    {
        Advanced = Fixture.AbsentAdvanced('b')
    }
};
var proofA = Fixture.Prove(absentA);
var proofB = Fixture.Prove(absentB);
Pass("P10-SOP-14 absent-family-owner-bound", proofA);
Pass("P10-SOP-15 absent-family-owner-bound-variant", proofB);
if (proofA.ProofSha256 == proofB.ProofSha256)
    throw new InvalidOperationException("absent owner manifest was not bound");
Console.WriteLine("PASS P10-SOP-16 absent-owner-proof-sha-differs");

var basicProductionCases = BasicProductionCases.Run();
if (basicProductionCases != 17)
    throw new InvalidOperationException("basic production marker drift");

Console.WriteLine(
    "P10_SUMMARY_OWNER_PARITY_OK cases=36 pure=true wired=false " +
    "completePartition=true failClosed=true basic=true advanced=true diff=true");

static void Pass(
    string id,
    StatisticReconciliationActualSummaryOwnerParityProof proof)
{
    if (!proof.Complete || proof.FailureCode is not null ||
        proof.ProofSha256.Length != 64)
        throw new InvalidOperationException(
            $"{id}: expected complete, got {proof.FailureCode}");
    Console.WriteLine($"PASS {id}");
}

static void Fail(
    string id,
    StatisticReconciliationActualSummaryOwnerParityProof proof,
    string expected)
{
    if (proof.Complete || proof.FailureCode != expected ||
        proof.ProofSha256.Length != 64)
        throw new InvalidOperationException(
            $"{id}: expected {expected}, got {proof.FailureCode}");
    Console.WriteLine($"PASS {id}");
}

internal sealed record ParityFixture(
    StatisticReconciliationActualSummaryPlanBinding Plan,
    StatisticReconciliationActualRawSummaryProof Raw,
    StatisticReconciliationActualSummaryOwnerParityActual Actual);

internal static class Fixture
{
    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web);

    internal static ParityFixture Create(
        string family,
        decimal[] values,
        decimal[]? before = null)
    {
        var plan = Plan(family);
        var sources = values.Select((value, index) => Source(index,
            family == "DIFF"
                ? Payload(before![index], value)
                : Payload(value)))
            .OrderBy(value => value.SourceStableIdentitySha256,
                StringComparer.Ordinal)
            .ToImmutableArray();
        var raw = Raw(plan, sources);
        var actual = family switch
        {
            "BASIC" => new StatisticReconciliationActualSummaryOwnerParityActual(
                BasicActual(plan, values.Sum()), null, null),
            "ADVANCED" => new StatisticReconciliationActualSummaryOwnerParityActual(
                null, AdvancedActual(plan, values.Sum(), values.Length), null),
            "DIFF" => new StatisticReconciliationActualSummaryOwnerParityActual(
                null, null, DiffActual(plan, before![0], values[0])),
            _ => throw new InvalidOperationException("family")
        };
        return new ParityFixture(plan, raw, actual);
    }

    internal static StatisticReconciliationActualSummaryOwnerParityProof Prove(
        ParityFixture value)
        => StatisticReconciliationActualSummaryOwnerParity.Prove(
            new StatisticReconciliationActualSummaryOwnerParityPlan(
                value.Plan, value.Raw), value.Actual);

    private static StatisticReconciliationActualSummaryPlanBinding Plan(
        string family)
    {
        var metricId = family switch
        {
            "ADVANCED" => "amount:SUM",
            _ => "amount"
        };
        var period = family == "ADVANCED" ? "DAY:2026-08-01" : "2026-08";
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                family, "FIELD", metricId, period, "field-main", null,
                null, null,
                family == "BASIC" ? "DIRECT_CHILDREN_OR_SELF" : null,
                family == "BASIC" ? "assignment-root" : null,
                family == "ADVANCED" ? "DAY" : null,
                family == "DIFF" ? "FIELD" : null));
        var operations = family == "DIFF"
            ? ImmutableArray.Create("SUM")
            : ImmutableArray.Create("MAX", "MEAN", "MIN", "SUM");
        var transitionMode = family == "DIFF"
            ? StatisticReconciliationExpectedDiffTransitionModes
                .BeforeAfterDifference
            : StatisticReconciliationExpectedDiffTransitionModes.None;
        var before = family == "DIFF"
            ? "/fieldValues/values/before" : null;
        var after = family == "DIFF"
            ? "/fieldValues/values/after" : null;
        var difference = family == "DIFF" ? "SUBTRACT" : null;
        var transition = family == "DIFF" ? "CHANGED" : null;
        var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
            .BuildEntrySha256(identity, "/fieldValues/values/amount", "NUMBER",
                false, false, operations, transitionMode, before, after,
                difference, transition);
        var valueFree =
            new StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
                identity, "/fieldValues/values/amount", "NUMBER", false,
                null, false, operations, transitionMode, before, after,
                difference, transition,
                family == "DIFF"
                    ? ["BEFORE", "AFTER", "CHANGE_STATE", "DELTA"]
                    : ["NONE"], entrySha);
        var descriptor = StatisticReconciliationActualSummaryIdentityDescriptor
            .Create(valueFree);
        var binding = new StatisticReconciliationExpectedGenerationBinding(
            "reconciliation-owner-parity", Sha("generation"),
            Sha("generation-semantic"), Sha("metric-plan"), 1,
            Sha("manifest"), 8, Sha("membership"), "NON_FLOW");
        return StatisticReconciliationActualSummaryPlanBinding.Create(binding,
            [descriptor]);
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
        var capture = Sha("actual-source-capture");
        var membership = Sha("actual-membership");
        var doubleCollect = H(
            "P10_ACTUAL_RAW_SUMMARY_DOUBLE_COLLECT_V1", capture, collect,
            collect);
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
    {
        var canonical = Canonical(payload);
        return new StatisticReconciliationActualRawPayloadEnvelope(
            H("P10_ACTUAL_SOURCE_STABLE_IDENTITY_V1", "work",
                $"assignment-{index}", $"report-{index}"),
            "work", $"assignment-{index}",
            $"report-{index}", $"payload-{index}", 1,
            Sha($"payload-owner-{index}"), RawSha(canonical), canonical,
            Sha($"envelope-{index}-{canonical}"));
    }

    private static string Payload(decimal amount)
        => Canonical(JsonSerializer.Serialize(new
        {
            fieldValues = new { values = new { amount } }
        }, Web));

    private static string Payload(decimal before, decimal after)
        => Canonical(JsonSerializer.Serialize(new
        {
            fieldValues = new { values = new { before, after, amount = after } }
        }, Web));

    internal static ActualBasicResultObservation BasicActual(
        StatisticReconciliationActualSummaryPlanBinding plan,
        decimal value,
        bool extra = false,
        bool compactTable = false)
    {
        var descriptor = plan.IdentityDescriptors.Single();
        var item = BasicItem(descriptor, value, descriptor.MetricId, 0);
        var items = extra
            ? new[] { item, BasicItem(descriptor, value, "extra", 1) }
            : [item];
        var table = compactTable
            ? new[] { JsonSerializer.SerializeToElement(new { b = "block" }, Web) }
            : Array.Empty<JsonElement>();
        var itemElements = items.Select(value =>
        {
            using var document = JsonDocument.Parse(value.CanonicalJson);
            return document.RootElement.Clone();
        }).ToArray();
        var snapshot = Canonical(JsonSerializer.Serialize(new
        {
            v = 9,
            meta = new { },
            fields = itemElements,
            tb = table,
            warnings = Array.Empty<string>()
        }, Web));
        var request = Canonical("{\"periodKey\":\"2026-08\"}");
        var boundary = new ActualBasicOwnerBoundary(
            "snapshot", "work", "assignment-root", "form",
            "DIRECT_CHILDREN_OR_SELF", null, null, null, null,
            Sha("request"), "config", "config-version", 1, 1,
            Sha("config"), ImmutableArray<string>.Empty, "chain", "P9-04",
            2,
            "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509",
            "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1",
            "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad");
        var state = new ActualBasicOwnerState("DONE", false, false, true,
            true, false, true, true, true, true, Sha("basic-lifecycle"));
        var assignments = ImmutableArray.Create("assignment-0");
        var reports = ImmutableArray.Create("report-0");
        var draft = new ActualBasicResultObservation(boundary, "snapshot", request,
            RawSha(request), RawSha(request), "COMPACT_VALUES1D_V9", 9,
            snapshot, RawSha(snapshot), RawSha(snapshot), assignments, reports,
            Hs("P10_ACTUAL_BASIC_ASSIGNMENT_SET_V1", assignments),
            Hs("P10_ACTUAL_BASIC_REPORT_SET_V1", reports),
            Sha("source-signature"), items.ToImmutableArray(), state, "~");
        return draft with
        {
            CaptureSemanticSha256 =
                StatisticReconciliationActualSummaryOwnerParity
                    .BasicCaptureSemantic(draft)
        };
    }

    private static ActualBasicPayloadItemObservation BasicItem(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        decimal value,
        string target,
        int ordinal)
    {
        var json = Canonical(JsonSerializer.Serialize(new
        {
            targetKind = "FIELD",
            targetKey = target,
            fieldId = descriptor.FieldId,
            fieldKey = target,
            blockId = (string?)null,
            tableMode = (string?)null,
            metricKey = (string?)null,
            rowKey = (string?)null,
            columnKey = (string?)null,
            index = (int?)null,
            label = "Amount",
            dataType = "NUMBER",
            operation = "SUM",
            value,
            valueCount = 1,
            reportCount = 1,
            sum = value,
            min = value,
            max = value,
            mean = value,
            trueCount = (int?)null,
            falseCount = (int?)null,
            minDateUtc = (DateTime?)null,
            maxDateUtc = (DateTime?)null,
            text = (string?)null,
            textCharCount = (int?)null,
            textTruncated = false,
            buckets = Array.Empty<object>()
        }, Web));
        return new ActualBasicPayloadItemObservation("FIELD", ordinal, target,
            json, H("P10_ACTUAL_BASIC_RESULT_ITEM_V1", "FIELD", I(ordinal),
                target, RawSha(json)));
    }

    internal static ActualAdvancedCapture AdvancedActual(
        StatisticReconciliationActualSummaryPlanBinding plan,
        decimal result,
        int valueCount = 1,
        bool extra = false)
    {
        var descriptor = plan.IdentityDescriptors.Single();
        var fields = ImmutableArray.CreateBuilder<ActualAdvancedFieldObservation>();
        fields.Add(AdvancedField(descriptor, result, valueCount, 0));
        if (extra)
            fields.Add(AdvancedField(descriptor, result, valueCount, 1,
                "extra", "field-extra"));
        var fieldArray = fields.ToImmutable();
        var valueJson = "{}";
        var valueSemantic = H("P10_ACTUAL_ADVANCED_TYPED_VALUE_V1",
            RawSha(valueJson), Hs("P10_ACTUAL_ADVANCED_TYPED_FIELDS_V1",
                fieldArray.Select(value => value.SemanticSha256)));
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var valueObservation = new ActualAdvancedValueObservation(
            1, "ADVANCED_SUMMARY_DAY_NODE_V1", start, "config",
            Sha("config"), "DAY", "2026-08-01", "2026-08-01", "2026-08",
            "2026", start, end, "DIRECT_CHILDREN_OR_SELF", null, null, null,
            null, 1, 1, 1, fieldArray.Length, fieldArray.Length, 0,
            ImmutableArray<string>.Empty, fieldArray, valueSemantic);
        var state = new ActualAdvancedNodeState("CLEAN", false, null, false,
            true, true, true, true, true, true, true, true, true, true,
            Sha("advanced-lifecycle"));
        var reports = ImmutableArray.Create("report-0");
        var observed = RawSha(valueJson);
        var nodeSemantic = H("P10_ACTUAL_ADVANCED_NODE_OBSERVATION_V1",
            "work_assignment_advanced_summary_day_nodes", "node", "DAY",
            "2026-08-01", "2026-08-01", "2026-08", "2026",
            Instant(start), Instant(end), Sha("source-signature"), "1",
            Hs("P10_ACTUAL_ADVANCED_SOURCE_REPORT_ORDER_V1", reports),
            Hs("P10_ACTUAL_ADVANCED_INPUT_NODE_ORDER_V1", []), observed,
            observed, observed, valueSemantic, state.BuildLifecycleSemanticSha256,
            "true", "true", "true", "true", "true", "true");
        var node = new ActualAdvancedNodeObservation(
            "work_assignment_advanced_summary_day_nodes", "node", "DAY",
            "2026-08-01", "2026-08-01", "2026-08", "2026", start, end,
            Sha("source-signature"), 1, reports, ImmutableArray<string>.Empty,
            valueJson, observed, observed, observed, start, valueObservation,
            state, nodeSemantic);
        var boundary = new ActualAdvancedOwnerBoundary(
            "work", "assignment", "form", "section", "config",
            "config-version", 1, 1, Sha("config"),
            ImmutableArray<string>.Empty, "UTC_GREGORIAN", "chain", "P9-05",
            3,
            "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3",
            "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f",
            "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc",
            ["node"], [], []);
        var draft = new ActualAdvancedCapture(boundary, [node], 1, "~");
        return draft with
        {
            CaptureSemanticSha256 =
                StatisticReconciliationActualSummaryOwnerParity
                    .AdvancedCaptureSemantic(draft)
        };
    }

    private static ActualAdvancedFieldObservation AdvancedField(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        decimal result,
        int valueCount,
        int ordinal,
        string? fieldKey = null,
        string? fieldId = null)
    {
        fieldKey ??= "amount";
        fieldId ??= descriptor.FieldId!;
        var canonical = JsonSerializer.Serialize(result, Web);
        var samples = ImmutableArray.Create(result.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        var semantic = H("P10_ACTUAL_ADVANCED_FIELD_V1", I(ordinal), fieldId,
            fieldKey, "Amount", "NUMBER", "SUM", I(valueCount),
            I(valueCount), RawSha(canonical),
            Hs("P10_ACTUAL_ADVANCED_FIELD_SAMPLES_V1", samples));
        return new ActualAdvancedFieldObservation(ordinal, fieldId, fieldKey,
            "Amount", "NUMBER", "SUM", valueCount, valueCount, canonical,
            samples, semantic);
    }

    internal static ActualAdvancedCapture AbsentAdvanced(char discriminator)
    {
        var boundary = new ActualAdvancedOwnerBoundary(
            "work", "assignment", "form", "section", "config",
            "version", 1, 1, Sha("config"), [], "UTC_GREGORIAN", "chain",
            "P9-05", 3, Sha("raw"), Sha("semantic"), Sha("lock"), [], [], []);
        return new ActualAdvancedCapture(boundary, [], 0,
            Sha($"absent-advanced-{discriminator}"));
    }

    internal static ActualP9DiffCapture DiffActual(
        StatisticReconciliationActualSummaryPlanBinding plan,
        decimal before,
        decimal after,
        decimal? declaredDelta = null,
        bool extra = false)
    {
        var descriptor = plan.IdentityDescriptors.Single();
        var rows = ImmutableArray.CreateBuilder<ActualP9DiffRowObservation>();
        rows.Add(DiffRow(descriptor, before, after,
            declaredDelta ?? after - before, 0));
        if (extra)
            rows.Add(DiffRow(descriptor, before, after, after - before, 1,
                "extra"));
        var rowArray = rows.ToImmutable();
        var boundary = new ActualP9DiffOwnerBoundary(
            Sha("diff-result"), "run", "work", "assignment", "form",
            "config", "version", 1, 1, Sha("config"), [], "chain", "P9-06",
            4,
            "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68",
            "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36",
            "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397");
        var state = new ActualP9DiffOwnerState("COMPLETED", true, true, false,
            false, true, true, true, true, true, true, true, true, true,
            true, true, Sha("diff-lifecycle"));
        var payloadSha = Sha("source-payload");
        var generationSha = Sha("direct-generation");
        var pinSemantic = H("P10_ACTUAL_P9_DIFF_SOURCE_PIN_V1", "0",
            "LEFT", "report-0", "1", payloadSha, "1", "direct-run",
            generationSha);
        var sourcePins = ImmutableArray.Create(
            new ActualP9DiffSourcePinObservation(0, "LEFT", "report-0", 1,
                payloadSha, 1, "direct-run", generationSha, pinSemantic));
        var draft = new ActualP9DiffCapture(boundary, "FIELD", "field-main",
            "concept", "NUMBER", Canonical("{\"periodKey\":\"2026-08\"}"),
            "FIELD", "field-main", "concept", "NUMBER",
            Canonical("{\"periodKey\":\"2026-07\"}"), "LEFT_TO_RIGHT",
            "INCLUDE", "INCLUDE", "UTC_GREGORIAN", sourcePins, rowArray,
            Sha("diff-result-sha"), Sha("diff-result-sha"), state,
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed,
            null, "~");
        return draft with
        {
            CaptureSemanticSha256 =
                StatisticReconciliationActualSummaryOwnerParity
                    .DiffCaptureSemantic(draft)
        };
    }

    private static ActualP9DiffRowObservation DiffRow(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        decimal before,
        decimal after,
        decimal delta,
        int ordinal,
        string? key = null)
    {
        key ??= descriptor.MetricId;
        var left = Typed(after);
        var right = Typed(before);
        var rowId = Sha($"row-{ordinal}");
        var semantic = H("P10_ACTUAL_P9_DIFF_ROW_V1", rowId, I(ordinal), key,
            "FIELD", descriptor.FieldId, left.SemanticSha256,
            right.SemanticSha256, "false", "VALUE_CHANGED", Number(delta),
            "true", "true",
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed);
        return new ActualP9DiffRowObservation(rowId, ordinal, key, "FIELD",
            descriptor.FieldId!, left, right, false, "VALUE_CHANGED", delta,
            true, true,
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed,
            null, semantic);
    }

    private static ActualP9DiffTypedObservation Typed(decimal value)
    {
        var canonical = Number(value);
        var semantic = H("P10_ACTUAL_P9_DIFF_TYPED_VALUE_V1", "VALUE",
            "NUMBER", canonical, canonical, null, null,
            Hs("P10_ACTUAL_P9_DIFF_CHOICES_V1", []), "true");
        return new ActualP9DiffTypedObservation("VALUE", "NUMBER", canonical,
            value, null, null, [], true, semantic);
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
    private static string Number(decimal value)
        => StatisticReconciliationActualCanonical.Number(value);
    private static string Instant(DateTime value)
        => StatisticReconciliationActualCanonical.Instant(value);
    private static string RawSha(string value)
        => StatisticReconciliationActualJson.RawSha256(value);
    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
