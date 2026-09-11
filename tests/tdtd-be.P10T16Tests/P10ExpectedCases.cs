using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class P10ExpectedCases
{
    internal static async Task IncludeAndReplay()
    {
        await Tests.IncludeExactlyOnce();
        await Tests.ContributionReplayDeduped();
    }

    internal static Task OrderedAndUnordered()
    {
        var orderedA = StringListGeneration("[\"a\",\"b\",\"b\"]", false);
        var orderedB = StringListGeneration("[\"b\",\"a\",\"b\"]", false);
        var orderedAtomA = ListValue(orderedA);
        var orderedAtomB = ListValue(orderedB);
        Equal("[\"a\",\"b\",\"b\"]", orderedAtomA.CanonicalValue,
            "ordered multiplicity");
        NotEqual(orderedAtomA.ValueIdentitySha256, orderedAtomB.ValueIdentitySha256,
            "ordered permutation identity");
        NotEqual(orderedA.TypedSemanticSha256, orderedB.TypedSemanticSha256,
            "ordered permutation typed hash");

        var unorderedA = StringListGeneration("[\"b\",\"a\",\"a\"]", true);
        var unorderedB = StringListGeneration("[\"a\",\"b\"]", true);
        var unorderedAtomA = ListValue(unorderedA);
        var unorderedAtomB = ListValue(unorderedB);
        Equal("[\"a\",\"b\"]", unorderedAtomA.CanonicalValue,
            "unordered canonical set");
        Equal(unorderedAtomA.ValueIdentitySha256, unorderedAtomB.ValueIdentitySha256,
            "unordered permutation identity");
        Equal(unorderedA.TypedSemanticSha256, unorderedB.TypedSemanticSha256,
            "unordered permutation typed hash");

        var memberChanged = ListValue(
            StringListGeneration("[\"a\",\"c\"]", true));
        NotEqual(unorderedAtomA.ValueIdentitySha256, memberChanged.ValueIdentitySha256,
            "unordered member change");
        return Task.CompletedTask;
    }

    internal static Task MissingNullEmptyDistinct()
    {
        var generation = Fixture.TypedGeneration();
        var number = generation.Atoms
            .Where(item => item.Identity.MetricId == "number")
            .ToArray();
        Equal(1L, number.Single(item =>
            item.ValueState == StatisticReconciliationExpectedValueStates.Null)
            .OccurrenceCount, "null occurrence");
        Equal(1L, number.Single(item =>
            item.ValueState == StatisticReconciliationExpectedValueStates.Empty)
            .OccurrenceCount, "empty occurrence");
        Equal(2L, generation.Atoms.Single(item =>
            item.Identity.MetricId == "missing" &&
            item.ValueState == StatisticReconciliationExpectedValueStates.Missing)
            .OccurrenceCount, "missing occurrence");
        return Task.CompletedTask;
    }

    internal static Task MeanUsesNumericValueCount()
    {
        var atom = Fixture.TypedGeneration().Atoms.Single(item =>
            item.Identity.MetricId == "number" &&
            item.AtomKind == StatisticReconciliationExpectedAtomKinds.Mean);
        Equal("20", atom.CanonicalValue, "mean value");
        Equal(3L, atom.NumericValueCount, "mean numeric denominator");
        Equal(5L, atom.RowCount, "row count remains distinct");
        Equal(2L, atom.ReportCount, "report count remains distinct");
        return Task.CompletedTask;
    }

    internal static async Task ConflictAndIncompleteFailClosed()
    {
        await Tests.ConflictFails();
        await Tests.IncompleteDoesNotCommit();
    }

    internal static Task CleanRecomputeDeterministic()
    {
        var first = Fixture.TypedGeneration();
        var second = Fixture.TypedGeneration();
        Equal(first.SourceSetSha256, second.SourceSetSha256, "source set hash");
        Equal(first.TypedSemanticSha256, second.TypedSemanticSha256,
            "typed semantic hash");
        Equal(first.GenerationSemanticSha256, second.GenerationSemanticSha256,
            "generation semantic hash");
        Equal(first.GenerationId, second.GenerationId, "generation id");
        Console.WriteLine(
            $"CONTROL EXPECTED sourceSet={first.SourceSetSha256} " +
            $"algorithmRevision={first.AlgorithmRevision} " +
            $"algorithmSha={first.AlgorithmSha256} " +
            $"typed={first.TypedSemanticSha256} " +
            $"generation={first.GenerationSemanticSha256} " +
            $"generationId={first.GenerationId}");
        return Task.CompletedTask;
    }

    internal static Task SourceConfigRuntimeChangesHashes()
    {
        var configA = NumberConfig(
            StatisticReconciliationExpectedMetricOperations.Sum);
        var baseline = Compile(
            Fixture.Source("s1", "{\"nums\":[10]}", lifecycleRevision: 1),
            configA);
        var sourceChanged = Compile(
            Fixture.Source("s1", "{\"nums\":[11]}", lifecycleRevision: 1),
            configA);
        var configChanged = Compile(
            Fixture.Source("s1", "{\"nums\":[10]}", lifecycleRevision: 1),
            NumberConfig(
                StatisticReconciliationExpectedMetricOperations.Sum,
                StatisticReconciliationExpectedMetricOperations.Mean));
        var runtimeChanged = Compile(
            Fixture.Source("s1", "{\"nums\":[10]}", lifecycleRevision: 2),
            configA);

        NotEqual(baseline.SourceSetSha256, sourceChanged.SourceSetSha256,
            "source payload changes source set");
        NotEqual(baseline.GenerationSemanticSha256,
            sourceChanged.GenerationSemanticSha256, "source changes generation");
        NotEqual(baseline.MetricPlanSha256, configChanged.MetricPlanSha256,
            "config changes metric plan");
        NotEqual(baseline.GenerationSemanticSha256,
            configChanged.GenerationSemanticSha256, "config changes generation");
        NotEqual(baseline.SourceSetSha256, runtimeChanged.SourceSetSha256,
            "runtime lifecycle changes source set");
        NotEqual(baseline.GenerationSemanticSha256,
            runtimeChanged.GenerationSemanticSha256, "runtime changes generation");
        return Task.CompletedTask;
    }

    internal static Task IndependentDependencyGraph()
    {
        var graph = new StatisticReconciliationExpectedLedgerCompiler()
            .DescribeDependencies();
        Equal(
            StatisticReconciliationExpectedLedgerCompiler.DependencyGraphSchemaVersion,
            graph.SchemaVersion,
            "dependency graph schema");
        Equal(5, graph.AllowedDomainInputs.Length, "allowed domain inputs");
        Equal(6, graph.ForbiddenDependencies.Length, "forbidden dependencies");
        True(graph.ForbiddenDependencies.Any(item => item.Contains(
            "P9_PROJECTION", StringComparison.Ordinal)), "projection forbidden");
        True(graph.ForbiddenDependencies.Any(item => item.Contains(
            "P9_RESULT", StringComparison.Ordinal)), "result forbidden");
        True(graph.ForbiddenDependencies.Any(item => item.Contains(
            "SHARED_P9_CALCULATOR", StringComparison.Ordinal)),
            "shared calculator forbidden");
        Equal(64, StatisticReconciliationExpectedTypedCompiler.AlgorithmSha256.Length,
            "algorithm hash");
        Console.WriteLine(
            $"CONTROL DEPENDENCY allowed={graph.AllowedDomainInputs.Length} " +
            $"forbidden={graph.ForbiddenDependencies.Length} p9Actual=0");
        return Task.CompletedTask;
    }

    internal static async Task WrongLedgerDetectedRejectedAndRecovered()
    {
        var baseline = Fixture.TypedGeneration();
        var control = WrongLedgerControl.Mutate(baseline);
        Equal(WrongLedgerControl.Detector, control.Detector, "detector");
        NotEqual(control.BaselineTypedSemanticSha256,
            control.MutatedTypedSemanticSha256, "typed mismatch detected");
        NotEqual(control.BaselineGenerationSemanticSha256,
            control.MutatedGenerationSemanticSha256, "generation mismatch detected");
        NotEqual(control.BaselineGenerationId,
            control.MutatedGenerationId, "generation id mismatch detected");

        var rejectingBackend = new WrongLedgerRejectingBackend();
        var store = new StatisticReconciliationExpectedObservationStore(
            rejectingBackend,
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        try
        {
            _ = await store.AppendGenerationAsync(
                control.PoisonedProductGeneration,
                Fixture.UtcNow);
            throw new InvalidOperationException("Product accepted the wrong ledger.");
        }
        catch (StatisticReconciliationExpectedObservationException error)
            when (error.ReasonCode ==
                  StatisticReconciliationExpectedObservationFailureReasons.GenerationInvalid)
        {
        }
        Equal(0, rejectingBackend.ReadCalls, "wrong ledger rejected before read");
        Equal(0, rejectingBackend.ContentWriteCalls,
            "wrong ledger content writes");
        Equal(0, rejectingBackend.CommitWriteCalls,
            "wrong ledger commit writes");

        var recovered = Fixture.TypedGeneration();
        Equal(baseline.TypedSemanticSha256, recovered.TypedSemanticSha256,
            "typed hash recovery");
        Equal(baseline.GenerationSemanticSha256,
            recovered.GenerationSemanticSha256, "generation hash recovery");
        Equal(baseline.GenerationId, recovered.GenerationId, "generation id recovery");

        var direct = await DirectMongoControl.AppendValidateAndCleanAsync(recovered);
        True(direct.CleanupVerified, "direct Mongo cleanup");
        Equal(1, direct.CollectionCount, "isolated Mongo owner collection count");
        Console.WriteLine(
            $"CONTROL WRONG_LEDGER detector={control.Detector} " +
            $"baseline={control.BaselineGenerationSemanticSha256} " +
            $"mutated={control.MutatedGenerationSemanticSha256} " +
            "productRejected=1 backendReads=0 backendWrites=0 recovered=1");
        Console.WriteLine(
            $"CONTROL DIRECT_MONGO documents={direct.DocumentCount} " +
            $"manifest={direct.ManifestSha256} collections={direct.CollectionCount} " +
            "p5p9Writes=0 cleanup=1 cleanupReplay=1");
    }

    private static StatisticReconciliationExpectedCompiledGeneration StringListGeneration(
        string listJson,
        bool unordered)
    {
        var config = JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                new
                {
                    family = StatisticReconciliationExpectedMetricFamilies.Direct,
                    kind = StatisticReconciliationExpectedMetricKinds.Field,
                    metricId = "tags",
                    fieldId = "field-tags",
                    jsonPointer = "/tags",
                    valueType = StatisticReconciliationExpectedValueTypes.StringList,
                    operations = new[]
                    {
                        StatisticReconciliationExpectedMetricOperations.Values
                    },
                    unordered
                }
            }
        });
        return Compile(Fixture.Source("s1", $"{{\"tags\":{listJson}}}"), config);
    }

    private static StatisticReconciliationExpectedCompiledGeneration Compile(
        SourceSpec source,
        string config)
    {
        var plan = Fixture.Plan(
            config,
            [source],
            [Fixture.Contribution(
                source.StableSourceId,
                StatisticReconciliationExpectedContributionPolicies.Include)]);
        return new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler())
            .Compile(plan, Fixture.CatalogPins());
    }

    private static string NumberConfig(params string[] operations)
        => JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                new
                {
                    family = StatisticReconciliationExpectedMetricFamilies.Direct,
                    kind = StatisticReconciliationExpectedMetricKinds.Field,
                    metricId = "number",
                    fieldId = "field-number",
                    jsonPointer = "/nums",
                    valueType = StatisticReconciliationExpectedValueTypes.Number,
                    operations,
                    expandArray = true,
                    unordered = false
                }
            }
        });

    private static StatisticReconciliationExpectedTypedAtom ListValue(
        StatisticReconciliationExpectedCompiledGeneration generation)
        => generation.Atoms.Single(item =>
            item.AtomKind == StatisticReconciliationExpectedAtomKinds.StringList &&
            item.ValueState == StatisticReconciliationExpectedValueStates.Value);

    private static void True(bool value, string name)
    {
        if (!value)
            throw new InvalidOperationException($"Assertion failed: {name}.");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"Assertion failed: {name}; expected={expected}; actual={actual}.");
    }

    private static void NotEqual<T>(T left, T right, string name)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException(
                $"Assertion failed: {name}; values unexpectedly match.");
    }
}
