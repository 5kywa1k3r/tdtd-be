using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed class StatisticReconciliationExpectedTypedCompiler
    : IStatisticReconciliationExpectedTypedCompiler
{
    internal const string AtomSchemaVersion = "P10_EXPECTED_TYPED_ATOM_V3";
    internal const string GenerationSchemaVersion = "P10_EXPECTED_GENERATION_V3";
    internal const string AlgorithmRevision = "P10-EXPECTED-ALGORITHM-003";
    internal const int MaxMetricDefinitions = 10_000;
    internal const int MaxCompiledAtoms = 80_000;


    internal static readonly string AlgorithmSha256 = BuildAlgorithmSha256();

    private readonly IStatisticReconciliationExpectedMetricIdentityCompiler _identityCompiler;

    public StatisticReconciliationExpectedTypedCompiler(
        IStatisticReconciliationExpectedMetricIdentityCompiler identityCompiler)
    {
        _identityCompiler = identityCompiler;
    }

    public StatisticReconciliationExpectedCompiledGeneration Compile(
        StatisticReconciliationExpectedSourcePlan sourcePlan,
        StatisticReconciliationExpectedCatalogPins catalogPins)
    {
        ArgumentNullException.ThrowIfNull(sourcePlan);
        ArgumentNullException.ThrowIfNull(catalogPins);
        var metricPlan = ParseMetricPlan(
            sourcePlan.BoundInputs.LockedP8Configuration.CanonicalConfigurationJson,
            sourcePlan.BoundInputs.ContextPin.PeriodKey);
        var metricPlanSha256 =
            StatisticReconciliationExpectedMetricPlanIntegrity.BuildPlanSha256(metricPlan);
        var payloads = sourcePlan.IncludedSources
            .OrderBy(
                item => item.LifecycleRevision.StableSourceId,
                StringComparer.Ordinal)
            .Select(item => item.LifecycleRevision.PayloadJson)
            .ToImmutableArray();

        var atomBuilder = ImmutableArray.CreateBuilder<
            StatisticReconciliationExpectedTypedAtom>();
        foreach (var entry in metricPlan)
        {
            foreach (var atom in CompileMetric(entry, payloads))
            {
                if (atomBuilder.Count >= MaxCompiledAtoms)
                    throw Fail(
                        StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                        "$.atoms",
                        $"Expected generation exceeds {MaxCompiledAtoms} atoms.");
                atomBuilder.Add(atom);
            }
        }
        var atoms = atomBuilder
            .OrderBy(item => item.Identity.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.AtomKind, StringComparer.Ordinal)
            .ThenBy(item => item.ValueIdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var typedSemanticSha256 = H(
            "P10_EXPECTED_TYPED_LEDGER_V1",
            atoms.Select(item => item.AtomSemanticSha256));
        var membershipSemanticSha256 =
            StatisticReconciliationExpectedMembershipIntegrity.BuildManifestSha256(
                sourcePlan.IncludedSources.Select(source =>
                    StatisticReconciliationExpectedMembershipIntegrity
                        .BuildStableIdentitySha256(
                            source.LifecycleRevision.Identity.WorkId,
                            source.LifecycleRevision.Identity.WorkAssignmentId,
                            source.LifecycleRevision.Identity.ReportId)));        var generationSemanticSha256 = H(
            GenerationSchemaVersion,
            sourcePlan.BoundInputs.ContextPin.ReconciliationId,
            sourcePlan.BoundInputs.InputFingerprints.InputBindingSha256,
            sourcePlan.SourceSetSha256,
            membershipSemanticSha256,
            AlgorithmRevision,
            AlgorithmSha256,
            catalogPins.CatalogPinSetSha256,
            metricPlanSha256,
            typedSemanticSha256);
        var generationId = H(
            "P10_EXPECTED_GENERATION_ID_V3",
            sourcePlan.BoundInputs.ContextPin.ReconciliationId,
            generationSemanticSha256);

        return new StatisticReconciliationExpectedCompiledGeneration(
            GenerationSchemaVersion,
            sourcePlan.BoundInputs.ContextPin,
            generationId,
            generationSemanticSha256,
            AlgorithmRevision,
            AlgorithmSha256,
            sourcePlan.SourceSetSha256,
            membershipSemanticSha256,
            typedSemanticSha256,
            metricPlanSha256,
            catalogPins,
            sourcePlan,
            metricPlan,
            atoms);
    }

    private ImmutableArray<StatisticReconciliationExpectedMetricPlanEntry> ParseMetricPlan(
        string canonicalConfigurationJson,
        string defaultPeriodKey)
    {
        using var document = JsonDocument.Parse(canonicalConfigurationJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("expectedMetrics", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanMissing,
                "$.lockedP8Configuration.expectedMetrics",
                "Locked P8 configuration must expose expectedMetrics.");
        if (metrics.GetArrayLength() is < 1 or > MaxMetricDefinitions)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                "$.lockedP8Configuration.expectedMetrics",
                "Expected metric plan cardinality is outside the bounded contract.");

        var result = new List<StatisticReconciliationExpectedMetricPlanEntry>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var metric in metrics.EnumerateArray())
        {
            var path = $"$.lockedP8Configuration.expectedMetrics[{index++}]";
            if (metric.ValueKind != JsonValueKind.Object)
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                    path,
                    "Metric plan entry must be an object.");
            var identity = _identityCompiler.Compile(new ExpectedMetricIdentityRequest(
                RequiredString(metric, "family", path),
                RequiredString(metric, "kind", path),
                RequiredString(metric, "metricId", path),
                OptionalString(metric, "periodKey", path) ?? defaultPeriodKey,
                OptionalString(metric, "fieldId", path),
                OptionalString(metric, "tableId", path),
                OptionalString(metric, "rowId", path),
                OptionalString(metric, "labelId", path),
                OptionalString(metric, "scopeKind", path),
                OptionalString(metric, "scopeId", path),
                OptionalString(metric, "grain", path),
                OptionalString(metric, "diffKind", path)));
            if (!identities.Add(identity.IdentitySha256))
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.DuplicateIdentity,
                    path,
                    "Expected metric identity appears more than once.");

            var pointer = RequiredString(metric, "jsonPointer", path, allowEmpty: true);
            ValidateJsonPointer(pointer, $"{path}.jsonPointer");
            var valueType = RequiredString(metric, "valueType", path);
            if (!StatisticReconciliationExpectedValueTypes.All.Contains(valueType))
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.ValueTypeInvalid,
                    $"{path}.valueType",
                    "Unsupported expected value type.");
            var unordered = OptionalBoolean(metric, "unordered", path);
            var expandArray = OptionalBoolean(metric, "expandArray", path);
            if (unordered &&
                valueType != StatisticReconciliationExpectedValueTypes.StringList)
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                    $"{path}.unordered",
                    "Only STRING_LIST supports unordered set semantics.");
            if (valueType == StatisticReconciliationExpectedValueTypes.StringList &&
                expandArray)
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                    path,
                    "STRING_LIST cannot also expand its array as rows.");
            var operations = ReadOperations(metric, path);
            if ((operations.Contains(StatisticReconciliationExpectedMetricOperations.Sum) ||
                 operations.Contains(StatisticReconciliationExpectedMetricOperations.Mean)) &&
                valueType != StatisticReconciliationExpectedValueTypes.Number)
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                    path,
                    "SUM and MEAN require NUMBER values.");

            var transitionMode =
                StatisticReconciliationExpectedDiffTransitionModes.None;
            string? beforePointer = null;
            string? afterPointer = null;
            string? differenceOperation = null;
            string? transitionKind = null;
            if (identity.Family == StatisticReconciliationExpectedMetricFamilies.Diff)
            {
                transitionMode = RequiredString(metric, "transitionMode", path);
                if (transitionMode !=
                    StatisticReconciliationExpectedDiffTransitionModes
                        .BeforeAfterDifference)
                    throw Fail(
                        StatisticReconciliationExpectedTypedFailureReasons
                            .MetricPlanInvalid,
                        $"{path}.transitionMode",
                        "DIFF requires an explicit before/after/difference transition.");
                beforePointer = RequiredString(
                    metric, "beforeJsonPointer", path, allowEmpty: true);
                afterPointer = RequiredString(
                    metric, "afterJsonPointer", path, allowEmpty: true);
                ValidateJsonPointer(beforePointer, $"{path}.beforeJsonPointer");
                ValidateJsonPointer(afterPointer, $"{path}.afterJsonPointer");
                differenceOperation = RequiredString(
                    metric, "differenceOperation", path);
                transitionKind = RequiredString(metric, "transitionKind", path);
                if (!StatisticReconciliationExpectedTransitionKinds.All
                        .Contains(transitionKind))
                    throw Fail(
                        StatisticReconciliationExpectedTypedFailureReasons
                            .MetricPlanInvalid,
                        $"{path}.transitionKind",
                        "DIFF transition kind must be ADDED, REMOVED or CHANGED.");
                if (!StatisticReconciliationExpectedDifferenceOperations.All
                        .Contains(differenceOperation))
                    throw Fail(
                        StatisticReconciliationExpectedTypedFailureReasons
                            .MetricPlanInvalid,
                        $"{path}.differenceOperation",
                        "Unsupported DIFF operation.");
            }
            else if (metric.TryGetProperty("transitionMode", out _) ||
                     metric.TryGetProperty("beforeJsonPointer", out _) ||
                     metric.TryGetProperty("afterJsonPointer", out _) ||
                     metric.TryGetProperty("differenceOperation", out _) ||
                     metric.TryGetProperty("transitionKind", out _))
            {
                throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons
                        .MetricPlanInvalid,
                    path,
                    "Only DIFF metrics may carry transition descriptors.");
            }

            var planEntrySha256 =
                StatisticReconciliationExpectedMetricPlanIntegrity.BuildEntrySha256(
                    identity,
                    pointer,
                    valueType,
                    unordered,
                    expandArray,
                    operations,
                    transitionMode,
                    beforePointer,
                    afterPointer,
                    differenceOperation,
                    null);
            result.Add(new StatisticReconciliationExpectedMetricPlanEntry(
                identity,
                pointer,
                valueType,
                unordered,
                expandArray,
                operations,
                planEntrySha256,
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
                transitionMode,
                beforePointer,
                afterPointer,
                differenceOperation,
                transitionKind));
        }

        return result
            .OrderBy(item => item.Identity.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<StatisticReconciliationExpectedTypedAtom> CompileMetric(
        StatisticReconciliationExpectedMetricPlanEntry entry,
        ImmutableArray<string> payloads)
    {
        if (entry.Identity.Family ==
            StatisticReconciliationExpectedMetricFamilies.Diff)
        {
            var before = CompileMetricLeg(
                entry,
                payloads,
                entry.BeforeJsonPointer!,
                StatisticReconciliationExpectedTransitionLegs.Before);
            var after = CompileMetricLeg(
                entry,
                payloads,
                entry.AfterJsonPointer!,
                StatisticReconciliationExpectedTransitionLegs.After);
            return before.Concat(after)
                .Concat(CompileTransitionAtoms(entry, payloads))
                .ToImmutableArray();
        }

        return CompileMetricLeg(
            entry,
            payloads,
            entry.JsonPointer,
            StatisticReconciliationExpectedTransitionLegs.None);
    }

    private static ImmutableArray<StatisticReconciliationExpectedTypedAtom>
        CompileMetricLeg(
            StatisticReconciliationExpectedMetricPlanEntry entry,
            ImmutableArray<string> payloads,
            string jsonPointer,
            string transitionLeg)
    {
        var samples = new List<TypedSample>();
        long rowCount = 0;
        foreach (var payload in payloads)
        {
            using var document = JsonDocument.Parse(payload);
            if (!TryResolvePointer(document.RootElement, jsonPointer, out var value))
            {
                samples.Add(TypedSample.Missing);
                continue;
            }

            if (entry.ExpandArray && value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() == 0)
                {
                    samples.Add(TypedSample.Empty);
                    continue;
                }
                rowCount = CheckedAdd(rowCount, value.GetArrayLength(), "$.rowCount");
                foreach (var item in value.EnumerateArray())
                    samples.Add(NormalizeSample(item, entry));
                continue;
            }

            samples.Add(NormalizeSample(value, entry));
        }

        var reportCount = (long)payloads.Length;
        var valueSamples = samples
            .Where(sample => sample.State == StatisticReconciliationExpectedValueStates.Value)
            .ToArray();
        var numericSamples = entry.ValueType ==
                StatisticReconciliationExpectedValueTypes.Number
            ? valueSamples.Select(sample => decimal.Parse(
                    sample.CanonicalValue,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture))
                .ToArray()
            : [];
        var numericValueCount = (long)numericSamples.Length;
        var atoms = new List<StatisticReconciliationExpectedTypedAtom>
        {
            CountAtom(entry, StatisticReconciliationExpectedAtomKinds.ReportCount,
                reportCount, reportCount, rowCount, numericValueCount, transitionLeg),
            CountAtom(entry, StatisticReconciliationExpectedAtomKinds.RowCount,
                rowCount, reportCount, rowCount, numericValueCount, transitionLeg),
            CountAtom(entry, StatisticReconciliationExpectedAtomKinds.Count,
                valueSamples.LongLength, reportCount, rowCount, numericValueCount, transitionLeg),
            CountAtom(entry, StatisticReconciliationExpectedAtomKinds.NumericValueCount,
                numericValueCount, reportCount, rowCount, numericValueCount, transitionLeg),
            StateAtom(entry, StatisticReconciliationExpectedAtomKinds.Missing,
                samples.LongCount(sample => sample.State ==
                    StatisticReconciliationExpectedValueStates.Missing),
                reportCount, rowCount, numericValueCount, transitionLeg),
            StateAtom(entry, StatisticReconciliationExpectedAtomKinds.Null,
                samples.LongCount(sample => sample.State ==
                    StatisticReconciliationExpectedValueStates.Null),
                reportCount, rowCount, numericValueCount, transitionLeg),
            StateAtom(entry, StatisticReconciliationExpectedAtomKinds.Empty,
                samples.LongCount(sample => sample.State ==
                    StatisticReconciliationExpectedValueStates.Empty),
                reportCount, rowCount, numericValueCount, transitionLeg)
        };

        decimal? numericSum = null;
        if (numericSamples.Length > 0 &&
            (entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Sum) ||
             entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Mean)))
            numericSum = SumDecimals(numericSamples);

        if (entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Sum))
            atoms.Add(NumericAggregateAtom(
                entry,
                StatisticReconciliationExpectedAtomKinds.Sum,
                numericSum,
                reportCount,
                rowCount,
                numericValueCount,
                transitionLeg));
        if (entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Min))
            atoms.Add(MinMaxAtom(entry, true, valueSamples, numericSamples,
                reportCount, rowCount, numericValueCount, transitionLeg));
        if (entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Max))
            atoms.Add(MinMaxAtom(entry, false, valueSamples, numericSamples,
                reportCount, rowCount, numericValueCount, transitionLeg));
        if (entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Mean))
        {
            decimal? mean = null;
            if (numericValueCount > 0)
            {
                try
                {
                    mean = numericSum!.Value / numericValueCount;
                }
                catch (OverflowException)
                {
                    throw Fail(
                        StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
                        "$.mean",
                        "Canonical decimal mean overflowed.");
                }
            }
            atoms.Add(NumericAggregateAtom(
                entry,
                StatisticReconciliationExpectedAtomKinds.Mean,
                mean,
                reportCount,
                rowCount,
                numericValueCount,
                transitionLeg));
        }

        if (entry.Operations.Contains(StatisticReconciliationExpectedMetricOperations.Values) &&
            entry.ValueType != StatisticReconciliationExpectedValueTypes.Number)
        {
            var atomKind = ValueAtomKind(entry.ValueType);
            atoms.AddRange(valueSamples
                .GroupBy(sample => sample.CanonicalValue, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => Atom(
                    entry,
                    atomKind,
                    StatisticReconciliationExpectedValueStates.Value,
                    group.Key,
                    group.First().DecimalScale,
                    group.LongCount(),
                    reportCount,
                    rowCount,
                    numericValueCount,
                    transitionLeg)));
        }

        return atoms.ToImmutableArray();
    }

    private static StatisticReconciliationExpectedTypedAtom MinMaxAtom(
        StatisticReconciliationExpectedMetricPlanEntry entry,
        bool minimum,
        IReadOnlyList<TypedSample> valueSamples,
        IReadOnlyList<decimal> numericSamples,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string transitionLeg =
            StatisticReconciliationExpectedTransitionLegs.None)
    {
        if (entry.ValueType == StatisticReconciliationExpectedValueTypes.Number)
        {
            decimal? value = numericSamples.Count == 0
                ? null
                : minimum ? numericSamples.Min() : numericSamples.Max();
            return NumericAggregateAtom(
                entry,
                minimum
                    ? StatisticReconciliationExpectedAtomKinds.Min
                    : StatisticReconciliationExpectedAtomKinds.Max,
                value,
                reportCount,
                rowCount,
                numericValueCount,
                transitionLeg);
        }

        var canonical = valueSamples.Count == 0
            ? null
            : minimum
                ? valueSamples.MinBy(sample => sample.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue
                : valueSamples.MaxBy(sample => sample.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue;
        return Atom(
            entry,
            minimum
                ? StatisticReconciliationExpectedAtomKinds.Min
                : StatisticReconciliationExpectedAtomKinds.Max,
            canonical is null
                ? StatisticReconciliationExpectedValueStates.Missing
                : StatisticReconciliationExpectedValueStates.Value,
            canonical ?? string.Empty,
            0,
            canonical is null ? 0 : 1,
            reportCount,
            rowCount,
            numericValueCount,
            transitionLeg);
    }

    private static StatisticReconciliationExpectedTypedAtom NumericAggregateAtom(
        StatisticReconciliationExpectedMetricPlanEntry entry,
        string atomKind,
        decimal? value,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string transitionLeg =
            StatisticReconciliationExpectedTransitionLegs.None)
    {
        var canonical = value.HasValue ? CanonicalDecimal(value.Value) : string.Empty;
        return Atom(
            entry,
            atomKind,
            value.HasValue
                ? StatisticReconciliationExpectedValueStates.Value
                : StatisticReconciliationExpectedValueStates.Missing,
            canonical,
            DecimalScale(canonical),
            value.HasValue ? 1 : 0,
            reportCount,
            rowCount,
            numericValueCount,
            transitionLeg);
    }

    private static StatisticReconciliationExpectedTypedAtom CountAtom(
        StatisticReconciliationExpectedMetricPlanEntry entry,
        string atomKind,
        long value,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string transitionLeg =
            StatisticReconciliationExpectedTransitionLegs.None)
        => Atom(
            entry,
            atomKind,
            StatisticReconciliationExpectedValueStates.Value,
            value.ToString(CultureInfo.InvariantCulture),
            0,
            1,
            reportCount,
            rowCount,
            numericValueCount,
            transitionLeg);

    private static StatisticReconciliationExpectedTypedAtom StateAtom(
        StatisticReconciliationExpectedMetricPlanEntry entry,
        string atomKind,
        long occurrenceCount,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string transitionLeg =
            StatisticReconciliationExpectedTransitionLegs.None)
        => Atom(
            entry,
            atomKind,
            atomKind,
            occurrenceCount.ToString(CultureInfo.InvariantCulture),
            0,
            occurrenceCount,
            reportCount,
            rowCount,
            numericValueCount,
            transitionLeg);

    private static StatisticReconciliationExpectedTypedAtom Atom(
        StatisticReconciliationExpectedMetricPlanEntry entry,
        string atomKind,
        string valueState,
        string canonicalValue,
        int decimalScale,
        long occurrenceCount,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string transitionLeg =
            StatisticReconciliationExpectedTransitionLegs.None,
        string? atomValueType = null,
        string? collectionSemantics = null)
    {
        var valueType = atomValueType ?? entry.ValueType;
        var semantics = collectionSemantics ??
            (valueType == StatisticReconciliationExpectedValueTypes.StringList
                ? entry.Unordered
                    ? StatisticReconciliationExpectedCollectionSemantics.Unordered
                    : StatisticReconciliationExpectedCollectionSemantics.Ordered
                : null);
        var valueIdentitySha256 = H(
            "P10_EXPECTED_TYPED_VALUE_IDENTITY_V3",
            entry.Identity.IdentitySha256,
            transitionLeg,
            entry.TransitionKind ?? "~",
            semantics ?? "~",
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            decimalScale.ToString(CultureInfo.InvariantCulture));
        var semanticSha256 = H(
            AtomSchemaVersion,
            entry.Identity.IdentitySha256,
            transitionLeg,
            entry.TransitionKind ?? "~",
            semantics ?? "~",
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            decimalScale.ToString(CultureInfo.InvariantCulture),
            occurrenceCount.ToString(CultureInfo.InvariantCulture),
            reportCount.ToString(CultureInfo.InvariantCulture),
            rowCount.ToString(CultureInfo.InvariantCulture),
            numericValueCount.ToString(CultureInfo.InvariantCulture),
            valueIdentitySha256);
        return new StatisticReconciliationExpectedTypedAtom(
            AtomSchemaVersion,
            entry.Identity,
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            decimalScale,
            occurrenceCount,
            reportCount,
            rowCount,
            numericValueCount,
            valueIdentitySha256,
            semanticSha256,
            transitionLeg,
            entry.TransitionKind,
            semantics);
    }

    private static ImmutableArray<StatisticReconciliationExpectedTypedAtom>
        CompileTransitionAtoms(
            StatisticReconciliationExpectedMetricPlanEntry entry,
            ImmutableArray<string> payloads)
    {
        long added = 0;
        long removed = 0;
        long changed = 0;
        long unchanged = 0;
        decimal delta = 0;
        long numericPairs = 0;
        foreach (var payload in payloads)
        {
            using var document = JsonDocument.Parse(payload);
            var before = ResolveTransitionSample(
                document.RootElement, entry.BeforeJsonPointer!, entry);
            var after = ResolveTransitionSample(
                document.RootElement, entry.AfterJsonPointer!, entry);
            if (before.State == StatisticReconciliationExpectedValueStates.Missing &&
                after.State != StatisticReconciliationExpectedValueStates.Missing)
                added++;
            else if (before.State != StatisticReconciliationExpectedValueStates.Missing &&
                     after.State == StatisticReconciliationExpectedValueStates.Missing)
                removed++;
            else if (before != after)
                changed++;
            else
                unchanged++;

            if (entry.DifferenceOperation ==
                    StatisticReconciliationExpectedDifferenceOperations.Subtract &&
                before.State == StatisticReconciliationExpectedValueStates.Value &&
                after.State == StatisticReconciliationExpectedValueStates.Value)
            {
                delta += decimal.Parse(after.CanonicalValue,
                    NumberStyles.Number, CultureInfo.InvariantCulture) -
                    decimal.Parse(before.CanonicalValue,
                        NumberStyles.Number, CultureInfo.InvariantCulture);
                numericPairs++;
            }
        }

        var observedKind = added > 0 && removed == 0 && changed == 0
            ? StatisticReconciliationExpectedTransitionKinds.Added
            : removed > 0 && added == 0 && changed == 0
                ? StatisticReconciliationExpectedTransitionKinds.Removed
                : StatisticReconciliationExpectedTransitionKinds.Changed;
        if (entry.TransitionKind != observedKind)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                "$.transitionKind",
                "Configured transition kind differs from authoritative state.");

        var atoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationExpectedTypedAtom>();
        atoms.Add(Atom(
            entry, "TRANSITION_KIND",
            StatisticReconciliationExpectedValueStates.Value,
            observedKind, 0, 1, payloads.Length, 0, numericPairs,
            StatisticReconciliationExpectedTransitionLegs.ChangeState,
            StatisticReconciliationExpectedValueTypes.Text));
        foreach (var pair in new[]
                 {
                     ("ADDED_COUNT", added),
                     ("REMOVED_COUNT", removed),
                     ("CHANGED_COUNT", changed),
                     ("UNCHANGED_COUNT", unchanged)
                 })
            atoms.Add(Atom(
                entry, pair.Item1,
                StatisticReconciliationExpectedValueStates.Value,
                pair.Item2.ToString(CultureInfo.InvariantCulture),
                0, 1, payloads.Length, 0, numericPairs,
                StatisticReconciliationExpectedTransitionLegs.ChangeState,
                StatisticReconciliationExpectedValueTypes.Number));

        if (entry.DifferenceOperation ==
            StatisticReconciliationExpectedDifferenceOperations.Subtract)
        {
            var canonical = CanonicalDecimal(delta);
            atoms.Add(Atom(
                entry, "DIFFERENCE",
                numericPairs > 0
                    ? StatisticReconciliationExpectedValueStates.Value
                    : StatisticReconciliationExpectedValueStates.Missing,
                numericPairs > 0 ? canonical : string.Empty,
                numericPairs > 0 ? DecimalScale(canonical) : 0,
                numericPairs > 0 ? 1 : 0,
                payloads.Length, 0, numericPairs,
                StatisticReconciliationExpectedTransitionLegs.Delta,
                StatisticReconciliationExpectedValueTypes.Number));
        }
        return atoms.ToImmutable();
    }

    private static TypedSample ResolveTransitionSample(
        JsonElement root,
        string pointer,
        StatisticReconciliationExpectedMetricPlanEntry entry)
    {
        if (!TryResolvePointer(root, pointer, out var value))
            return TypedSample.Missing;
        return NormalizeSample(value, entry);
    }
    private static TypedSample NormalizeSample(
        JsonElement value,
        StatisticReconciliationExpectedMetricPlanEntry entry)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return TypedSample.Null;
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Length == 0)
            return TypedSample.Empty;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)
            return TypedSample.Empty;

        return entry.ValueType switch
        {
            StatisticReconciliationExpectedValueTypes.Number => Number(value),
            StatisticReconciliationExpectedValueTypes.Bucket => Bucket(value),
            StatisticReconciliationExpectedValueTypes.Date => Date(value, fullDate: false),
            StatisticReconciliationExpectedValueTypes.FullDate => Date(value, fullDate: true),
            StatisticReconciliationExpectedValueTypes.Period => Period(value),
            StatisticReconciliationExpectedValueTypes.Boolean => Boolean(value),
            StatisticReconciliationExpectedValueTypes.Enum => String(value, "ENUM"),
            StatisticReconciliationExpectedValueTypes.StringList =>
                StringList(value, entry.Unordered),
            StatisticReconciliationExpectedValueTypes.Text => String(value, "TEXT"),
            _ => throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.ValueTypeInvalid,
                "$.value",
                "Unsupported expected value type.")
        };
    }

    private static TypedSample Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
                "$.value",
                "NUMBER requires a JSON decimal number without coercion.");
        var canonical = CanonicalDecimal(number);
        return new TypedSample(
            StatisticReconciliationExpectedValueStates.Value,
            canonical,
            DecimalScale(canonical));
    }

    private static TypedSample Bucket(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => new TypedSample(
                StatisticReconciliationExpectedValueStates.Value,
                "S:" + value.GetString(),
                0),
            JsonValueKind.Number when value.TryGetDecimal(out var number) => new TypedSample(
                StatisticReconciliationExpectedValueStates.Value,
                "N:" + CanonicalDecimal(number),
                DecimalScale(CanonicalDecimal(number))),
            JsonValueKind.True => new TypedSample(
                StatisticReconciliationExpectedValueStates.Value, "B:true", 0),
            JsonValueKind.False => new TypedSample(
                StatisticReconciliationExpectedValueStates.Value, "B:false", 0),
            _ => throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.ValueTypeInvalid,
                "$.value",
                "BUCKET requires string, decimal number or boolean.")
        };

    private static TypedSample Date(JsonElement value, bool fullDate)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.DateInvalid,
                "$.value",
                "DATE requires a canonical JSON string.");
        var raw = value.GetString()!;
        string canonical;
        if (fullDate)
        {
            if (!DateTime.TryParseExact(raw, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                throw DateFailure();
            canonical = "DAY:" + parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else if (DateTime.TryParseExact(raw, "yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var year))
            canonical = "YEAR:" + year.ToString("yyyy", CultureInfo.InvariantCulture);
        else if (DateTime.TryParseExact(raw, "MM/yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var month))
            canonical = "MONTH:" + month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        else if (DateTime.TryParseExact(raw, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var day))
            canonical = "DAY:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else
            throw DateFailure();
        return new TypedSample(
            StatisticReconciliationExpectedValueStates.Value,
            canonical,
            0);
    }

    private static TypedSample Period(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw PeriodFailure();
        var raw = value.GetString()!;
        var valid = raw.StartsWith("YEAR:", StringComparison.Ordinal)
            ? DateTime.TryParseExact(raw[5..], "yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)
            : raw.StartsWith("MONTH:", StringComparison.Ordinal)
                ? DateTime.TryParseExact(raw[6..], "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _)
                : raw.StartsWith("DAY:", StringComparison.Ordinal) &&
                  DateTime.TryParseExact(raw[4..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                      DateTimeStyles.None, out _);
        if (!valid)
            throw PeriodFailure();
        return new TypedSample(
            StatisticReconciliationExpectedValueStates.Value,
            raw,
            0);
    }

    private static TypedSample Boolean(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.True => new TypedSample(
                StatisticReconciliationExpectedValueStates.Value, "true", 0),
            JsonValueKind.False => new TypedSample(
                StatisticReconciliationExpectedValueStates.Value, "false", 0),
            _ => throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.ValueTypeInvalid,
                "$.value",
                "BOOLEAN requires a JSON boolean without coercion.")
        };

    private static TypedSample String(JsonElement value, string kind)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.ValueTypeInvalid,
                "$.value",
                $"{kind} requires a JSON string.");
        return new TypedSample(
            StatisticReconciliationExpectedValueStates.Value,
            value.GetString()!,
            0);
    }

    private static TypedSample StringList(JsonElement value, bool unordered)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.StringListInvalid,
                "$.value",
                "STRING_LIST requires JSON string items.");
        IEnumerable<string> items = value.EnumerateArray().Select(item => item.GetString()!);
        if (unordered)
            items = items.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal);
        var canonical = JsonSerializer.Serialize(items.ToArray());
        return new TypedSample(
            StatisticReconciliationExpectedValueStates.Value,
            canonical,
            0);
    }

    private static string ValueAtomKind(string valueType)
        => valueType switch
        {
            StatisticReconciliationExpectedValueTypes.Bucket =>
                StatisticReconciliationExpectedAtomKinds.Bucket,
            StatisticReconciliationExpectedValueTypes.Date =>
                StatisticReconciliationExpectedAtomKinds.Date,
            StatisticReconciliationExpectedValueTypes.FullDate =>
                StatisticReconciliationExpectedAtomKinds.FullDate,
            StatisticReconciliationExpectedValueTypes.Period =>
                StatisticReconciliationExpectedAtomKinds.Period,
            StatisticReconciliationExpectedValueTypes.Boolean =>
                StatisticReconciliationExpectedAtomKinds.Boolean,
            StatisticReconciliationExpectedValueTypes.Enum =>
                StatisticReconciliationExpectedAtomKinds.Enum,
            StatisticReconciliationExpectedValueTypes.StringList =>
                StatisticReconciliationExpectedAtomKinds.StringList,
            StatisticReconciliationExpectedValueTypes.Text =>
                StatisticReconciliationExpectedAtomKinds.Text,
            _ => throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.ValueTypeInvalid,
                "$.valueType",
                "No typed value atom kind exists.")
        };

    private static ImmutableArray<string> ReadOperations(JsonElement metric, string path)
    {
        if (!metric.TryGetProperty("operations", out var element) ||
            element.ValueKind != JsonValueKind.Array)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                $"{path}.operations",
                "Metric operations array required.");
        var operations = element.EnumerateArray()
            .Select((item, index) => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                    $"{path}.operations[{index}]",
                    "Metric operation must be a string."))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (operations.Count == 0 ||
            !operations.IsSubsetOf(StatisticReconciliationExpectedMetricOperations.All))
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                $"{path}.operations",
                "Metric operations are empty or unsupported.");
        return operations.OrderBy(item => item, StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool TryResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        foreach (var rawSegment in pointer[1..].Split('/'))
        {
            var segment = DecodePointerSegment(rawSegment);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                    return false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var index) &&
                index >= 0 &&
                (segment == "0" || !segment.StartsWith('0')) &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static void ValidateJsonPointer(string pointer, string path)
    {
        if (pointer.Length > 2048 ||
            (pointer.Length > 0 && !pointer.StartsWith("/", StringComparison.Ordinal)))
            throw Pointer(path);
        if (pointer.Length == 0)
            return;
        foreach (var segment in pointer[1..].Split('/'))
            _ = DecodePointerSegment(segment);
    }

    private static string DecodePointerSegment(string segment)
    {
        var result = new System.Text.StringBuilder(segment.Length);
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '~')
            {
                result.Append(segment[index]);
                continue;
            }
            if (++index >= segment.Length || segment[index] is not ('0' or '1'))
                throw Pointer("$.jsonPointer");
            result.Append(segment[index] == '0' ? '~' : '/');
        }
        return result.ToString();
    }

    private static string RequiredString(
        JsonElement element,
        string name,
        string path,
        bool allowEmpty = false)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                $"{path}.{name}",
                "Metric plan string required.");
        var result = value.GetString()!;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(result)) ||
            result.Length > 2048 ||
            result != result.Trim() && !allowEmpty ||
            result.Any(char.IsControl))
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                $"{path}.{name}",
                "Canonical bounded metric plan string required.");
        return result;
    }

    private static string? OptionalString(JsonElement element, string name, string path)
        => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String
                ? RequiredString(element, name, path)
                : throw Fail(
                    StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                    $"{path}.{name}",
                    "Optional metric plan token must be a string.");

    private static bool OptionalBoolean(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                $"{path}.{name}",
                "Optional metric plan flag must be boolean.")
        };
    }

    private static decimal SumDecimals(IEnumerable<decimal> values)
    {
        try
        {
            return values.Sum();
        }
        catch (OverflowException)
        {
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
                "$.sum",
                "Canonical decimal sum overflowed.");
        }
    }

    private static string CanonicalDecimal(decimal value)
        => value.ToString("0.#############################", CultureInfo.InvariantCulture);

    private static int DecimalScale(string canonical)
    {
        var point = canonical.IndexOf('.');
        return point < 0 ? 0 : canonical.Length - point - 1;
    }

    private static long CheckedAdd(long current, int next, string path)
    {
        try
        {
            return checked(current + next);
        }
        catch (OverflowException)
        {
            throw Fail(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                path,
                "Expected row count overflowed.");
        }
    }

    private static string BuildAlgorithmSha256()
        => H(
            "P10_EXPECTED_ALGORITHM_DESCRIPTOR_V2",
            AlgorithmRevision,
            "LIFECYCLE=APPROVED+EFFECTIVE+CURRENT+LOCKED",
            "CONTRIBUTION=V_EXCLUDE_DEFAULT;V_INCLUDE_ONCE",
            "MEAN=sum/numericValueCount",
            "DECIMAL=INVARIANT_FIXED_CANONICAL_NO_EPSILON",
            "DATE=yyyy|MM/yyyy|dd/MM/yyyy",
            "FULL_DATE=dd/MM/yyyy",
            "STATE=MISSING!=NULL!=EMPTY!=VALUE",
            "BASIC_SCOPE_IDENTITY=scopeKind+scopeId",
            H("P10_EXPECTED_VALUE_TYPES_V1",
                StatisticReconciliationExpectedValueTypes.All.OrderBy(x => x, StringComparer.Ordinal)),
            H("P10_EXPECTED_FAMILIES_V1",
                StatisticReconciliationExpectedMetricFamilies.All.OrderBy(x => x, StringComparer.Ordinal)),
            H("P10_EXPECTED_BASIC_SCOPES_V2",
                StatisticReconciliationExpectedBasicScopes.All.OrderBy(x => x, StringComparer.Ordinal)),
            H("P10_EXPECTED_ADVANCED_GRAINS_V1",
                StatisticReconciliationExpectedAdvancedGrains.All.OrderBy(x => x, StringComparer.Ordinal)),
            H("P10_EXPECTED_DIFF_KINDS_V1",
                StatisticReconciliationExpectedDiffKinds.All.OrderBy(x => x, StringComparer.Ordinal)));

    private static StatisticReconciliationExpectedLedgerInputException DateFailure()
        => Fail(
            StatisticReconciliationExpectedTypedFailureReasons.DateInvalid,
            "$.value",
            "Date value is not in a canonical source format.");

    private static StatisticReconciliationExpectedLedgerInputException PeriodFailure()
        => Fail(
            StatisticReconciliationExpectedTypedFailureReasons.PeriodInvalid,
            "$.value",
            "Period identity must be YEAR, MONTH or DAY canonical identity.");

    private static StatisticReconciliationExpectedLedgerInputException Pointer(string path)
        => Fail(
            StatisticReconciliationExpectedTypedFailureReasons.JsonPointerInvalid,
            path,
            "RFC 6901 JSON pointer required.");

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static StatisticReconciliationExpectedLedgerInputException Fail(
        string reason,
        string path,
        string message)
        => new(reason, path, message);

    private sealed record TypedSample(string State, string CanonicalValue, int DecimalScale)
    {
        internal static readonly TypedSample Missing = new(
            StatisticReconciliationExpectedValueStates.Missing, string.Empty, 0);
        internal static readonly TypedSample Null = new(
            StatisticReconciliationExpectedValueStates.Null, string.Empty, 0);
        internal static readonly TypedSample Empty = new(
            StatisticReconciliationExpectedValueStates.Empty, string.Empty, 0);
    }
}
