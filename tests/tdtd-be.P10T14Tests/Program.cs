using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var tests = new (string Name, Func<Task> Run)[]
{
    ("T10_DRAFT_SUBMITTED_ZERO", Tests.DraftSubmittedZero),
    ("T10_RECALLED_RETURNED_ZERO", Tests.RecalledReturnedZero),
    ("T10_TERMINATED_INVALIDATED_SUPERSEDED_ZERO", Tests.TerminatedInvalidatedSupersededZero),
    ("T10_APPROVED_EFFECTIVE_LOCKED_CURRENT", Tests.ApprovedEffectiveLockedCurrent),
    ("T10_AMBIGUOUS_APPROVED_FAIL_CLOSED", Tests.AmbiguousApprovedFails),
    ("T11_DEFAULT_EXCLUDE", Tests.DefaultExclude),
    ("T11_INCLUDE_EXACTLY_ONCE", Tests.IncludeExactlyOnce),
    ("T11_CONTRIBUTION_REPLAY_DEDUPED", Tests.ContributionReplayDeduped),
    ("T11_AMBIGUOUS_CONTRIBUTION_FAIL_CLOSED", Tests.AmbiguousContributionFails),
    ("T12_COUNTS_MEAN_AND_STATES", Tests.CountsMeanAndStates),
    ("T12_ALL_TYPED_VALUE_FAMILIES", Tests.AllTypedValueFamilies),
    ("T12_NO_NUMERIC_STRING_COERCION", Tests.NoNumericStringCoercion),
    ("T12_DECIMAL_OVERFLOW_FAIL_CLOSED", Tests.DecimalOverflowFails),
    ("T12_DETERMINISTIC_TYPED_HASH", Tests.DeterministicTypedHash),
    ("T13_DIRECT_IDENTITIES", Tests.DirectIdentities),
    ("T13_DYNAMIC_FORM_EXPECTED_OWNER_EXACT", Tests.DynamicFormExpectedOwnerIsExact),
    ("T13_DYNAMIC_FORM_EXPECTED_OWNER_MISSING", Tests.DynamicFormExpectedOwnerMissingFailsClosed),
    ("T13_DYNAMIC_FORM_EXPECTED_OWNER_AMBIGUOUS", Tests.DynamicFormExpectedOwnerAmbiguousFailsClosed),
    ("T13_DYNAMIC_FORM_EXPECTED_OWNER_HASH_DRIFT", Tests.DynamicFormExpectedOwnerHashDriftFailsClosed),
    ("T13_DYNAMIC_FORM_DIRECT_FIELD_SCOPE_FULL_PLAN", Tests.DynamicFormDirectFieldScopeKeepsFullExpectedPlan),
    ("T13_DYNAMIC_FORM_DIRECT_FIELD_V3_BROAD_SCOPE", Tests.DynamicFormLegacyDirectFieldBroadScopeKeepsFullPlan),
    ("T13_DYNAMIC_FORM_DIRECT_FIELD_SCOPE_FAIL_CLOSED", Tests.DynamicFormDirectFieldScopeFailsClosed),
    ("T13_DYNAMIC_FORM_DIRECT_SUM_MEAN", Tests.DynamicFormDirectSumAndMeanCompileFromRawPayload),
    ("T13_BASIC_FOUR_SCOPES", Tests.BasicFourScopes),
    ("T13_ADVANCED_THREE_GRAINS", Tests.AdvancedThreeGrains),
    ("T13_DIFF_THREE_KINDS", Tests.DiffThreeKinds),
    ("T14_APPEND_COMMIT_LAST", Tests.AppendCommitLast),
    ("T14_EXACT_REPLAY", Tests.ExactReplay),
    ("T14_CONFLICT_FAIL_CLOSED", Tests.ConflictFails),
    ("T14_INCOMPLETE_NO_COMMIT", Tests.IncompleteDoesNotCommit),
    ("T14_COMPLETE_LINEAGE_RECORDS", Tests.CompleteLineageRecords)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

Console.WriteLine($"P10_T10_T14_OK checks={passed} stopBefore=P10-T15");

internal static partial class Tests
{
    internal static Task DraftSubmittedZero()
    {
        foreach (var status in new[]
                 {
                     StatisticReconciliationExpectedLifecycleStatuses.Draft,
                     StatisticReconciliationExpectedLifecycleStatuses.Submitted
                 })
        {
            var plan = Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}", status)]);
            Equal(0, plan.ApprovedEffectiveSources.Length, status);
            Equal(0, plan.IncludedSources.Length, status);
            Equal(
                StatisticReconciliationExpectedSourceDecisionReasons.DraftOrUnapproved,
                plan.SourceDecisions.Single().ReasonCode,
                status);
        }
        var rejectedOne = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}",
                StatisticReconciliationExpectedLifecycleStatuses.Draft)]);
        var rejectedTwo = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":2}",
                StatisticReconciliationExpectedLifecycleStatuses.Draft)]);
        True(rejectedOne.SourceSetSha256 != rejectedTwo.SourceSetSha256,
            "rejected source payload hash is source-set bound");
        return Task.CompletedTask;
    }

    internal static Task RecalledReturnedZero()
    {
        foreach (var status in new[]
                 {
                     StatisticReconciliationExpectedLifecycleStatuses.Recalled,
                     StatisticReconciliationExpectedLifecycleStatuses.Returned
                 })
        {
            var plan = Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}", status)]);
            Equal(0, plan.ApprovedEffectiveSources.Length, status);
            Equal(
                StatisticReconciliationExpectedSourceDecisionReasons.RecalledOrReturned,
                plan.SourceDecisions.Single().ReasonCode,
                status);
        }
        return Task.CompletedTask;
    }

    internal static Task TerminatedInvalidatedSupersededZero()
    {
        var cases = new[]
        {
            (StatisticReconciliationExpectedLifecycleStatuses.Terminated,
                StatisticReconciliationExpectedRuntimeDispositions.Terminated,
                StatisticReconciliationExpectedSourceDecisionReasons.Terminated),
            (StatisticReconciliationExpectedLifecycleStatuses.Invalidated,
                StatisticReconciliationExpectedRuntimeDispositions.Invalidated,
                StatisticReconciliationExpectedSourceDecisionReasons.Invalidated),
            (StatisticReconciliationExpectedLifecycleStatuses.Superseded,
                StatisticReconciliationExpectedRuntimeDispositions.Superseded,
                StatisticReconciliationExpectedSourceDecisionReasons.Superseded)
        };
        foreach (var item in cases)
        {
            var plan = Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}", item.Item1,
                    runtimeDisposition: item.Item2)]);
            Equal(0, plan.ApprovedEffectiveSources.Length, item.Item1);
            Equal(item.Item3, plan.SourceDecisions.Single().ReasonCode, item.Item1);
        }
        return Task.CompletedTask;
    }

    internal static Task ApprovedEffectiveLockedCurrent()
    {
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")],
            [Fixture.Contribution("s1", StatisticReconciliationExpectedContributionPolicies.Include)]);
        Equal(1, plan.ApprovedEffectiveSources.Length, "approved");
        Equal(1, plan.IncludedSources.Length, "included");
        Equal(
            StatisticReconciliationExpectedSourceDecisionReasons.Included,
            plan.SourceDecisions.Single().ReasonCode,
            "included reason");
        return Task.CompletedTask;
    }

    internal static Task AmbiguousApprovedFails()
    {
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons.LifecycleAmbiguous,
            () => Fixture.Plan(
                Fixture.MinimalConfig(),
                [
                    Fixture.Source("s1", "{\"value\":1}", payloadRevision: 1,
                        lifecycleRevision: 1),
                    Fixture.Source("s1", "{\"value\":2}", payloadRevision: 2,
                        lifecycleRevision: 2)
                ]));
        return Task.CompletedTask;
    }

    internal static Task DefaultExclude()
    {
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")]);
        Equal(1, plan.ApprovedEffectiveSources.Length, "approved");
        Equal(0, plan.IncludedSources.Length, "default exclude");
        var decision = plan.SourceDecisions.Single();
        Equal(StatisticReconciliationExpectedContributionPolicies.Exclude,
            decision.ContributionPolicy, "policy");
        Equal(StatisticReconciliationExpectedSourceDecisionReasons.ContributionDefaultExclude,
            decision.ReasonCode, "reason");
        return Task.CompletedTask;
    }

    internal static Task IncludeExactlyOnce()
    {
        var contribution = Fixture.Contribution(
            "s1",
            StatisticReconciliationExpectedContributionPolicies.Include);
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")],
            [contribution]);
        var included = plan.IncludedSources.Single();
        Equal(contribution.VersionId, included.Contribution.VersionId, "version");
        Equal(contribution.ProvenanceSha256,
            included.Contribution.ProvenanceSha256, "provenance");
        return Task.CompletedTask;
    }

    internal static Task ContributionReplayDeduped()
    {
        var contribution = Fixture.Contribution(
            "s1",
            StatisticReconciliationExpectedContributionPolicies.Include);
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")],
            [contribution, contribution]);
        Equal(1, plan.IncludedSources.Length, "replayed contribution");
        return Task.CompletedTask;
    }

    internal static Task AmbiguousContributionFails()
    {
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons.ContributionAmbiguous,
            () => Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}")],
                [
                    Fixture.Contribution("s1",
                        StatisticReconciliationExpectedContributionPolicies.Include),
                    Fixture.Contribution("s1",
                        StatisticReconciliationExpectedContributionPolicies.Exclude,
                        revision: 2)
                ]));
        return Task.CompletedTask;
    }

    internal static Task CountsMeanAndStates()
    {
        var generation = Fixture.TypedGeneration();
        var atoms = generation.Atoms
            .Where(item => item.Identity.MetricId == "number")
            .ToArray();
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.ReportCount, "2", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.RowCount, "5", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.NumericValueCount, "3", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Sum, "60", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Mean, "20", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Null, "1", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Empty, "1", 2, 5, 3);
        return Task.CompletedTask;
    }

    internal static Task AllTypedValueFamilies()
    {
        var generation = Fixture.TypedGeneration();
        HasValue(generation, "bucket", "S:A");
        HasValue(generation, "bucket", "N:2");
        HasValue(generation, "date", "YEAR:2026");
        HasValue(generation, "date", "MONTH:2026-08");
        HasValue(generation, "fullDate", "DAY:2026-08-10");
        HasValue(generation, "period", "MONTH:2026-08");
        HasValue(generation, "boolean", "true");
        HasValue(generation, "enum", "OPEN");
        HasValue(generation, "tags", "[\"a\",\"b\"]");
        HasValue(generation, "text", "hello");
        var missing = generation.Atoms.Single(item =>
            item.Identity.MetricId == "missing" &&
            item.AtomKind == StatisticReconciliationExpectedAtomKinds.Missing);
        Equal(2L, missing.OccurrenceCount, "missing count");
        return Task.CompletedTask;
    }

    internal static Task NoNumericStringCoercion()
    {
        var plan = Fixture.Plan(
            Fixture.NumberOnlyConfig(),
            [Fixture.Source("s1", "{\"nums\":[\"10\"]}")],
            [Fixture.Contribution("s1", StatisticReconciliationExpectedContributionPolicies.Include)]);
        var compiler = new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        ExpectReason(
            StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
            () => compiler.Compile(plan, Fixture.CatalogPins()));
        return Task.CompletedTask;
    }

    internal static Task DecimalOverflowFails()
    {
        const string maximum = "79228162514264337593543950335";
        var plan = Fixture.Plan(
            Fixture.NumberOnlyConfig(),
            [Fixture.Source("s1", $"{{\"nums\":[{maximum},{maximum}]}}")],
            [Fixture.Contribution("s1", StatisticReconciliationExpectedContributionPolicies.Include)]);
        var compiler = new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        ExpectReason(
            StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
            () => compiler.Compile(plan, Fixture.CatalogPins()));
        return Task.CompletedTask;
    }

    internal static Task DeterministicTypedHash()
    {
        var first = Fixture.TypedGeneration();
        var second = Fixture.TypedGeneration(reverseSources: true);
        Equal(first.SourceSetSha256, second.SourceSetSha256, "source hash");
        Equal(first.TypedSemanticSha256, second.TypedSemanticSha256, "typed hash");
        Equal(first.GenerationId, second.GenerationId, "generation id");
        return Task.CompletedTask;
    }

    internal static Task DirectIdentities()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var field = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Direct,
            StatisticReconciliationExpectedMetricKinds.Field,
            "m-field", "MONTH:2026-08", fieldId: "f"));
        var table = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Direct,
            StatisticReconciliationExpectedMetricKinds.Table,
            "m-table", "MONTH:2026-08", tableId: "t"));
        var label = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Direct,
            StatisticReconciliationExpectedMetricKinds.RowLabel,
            "m-label", "MONTH:2026-08", tableId: "t", rowId: "r", labelId: "l"));
        Equal(3, new[] { field, table, label }
            .Select(item => item.IdentitySha256).Distinct().Count(), "direct identity count");
        return Task.CompletedTask;
    }

    internal static Task BasicFourScopes()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var hashes = StatisticReconciliationExpectedBasicScopes.All
            .Select(scope => compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Basic,
                StatisticReconciliationExpectedMetricKinds.Field,
                "m-basic", "MONTH:2026-08", fieldId: "f", scopeKind: scope,
                scopeId: "scope-" + scope))
                .IdentitySha256)
            .ToArray();
        Equal(StatisticReconciliationExpectedBasicScopes.All.Count,
            hashes.Distinct().Count(), "basic scopes");
        var branchA = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Basic,
            StatisticReconciliationExpectedMetricKinds.Field,
            "m-basic-branch", "MONTH:2026-08", fieldId: "f",
            scopeKind: StatisticReconciliationExpectedBasicScopes.FlowBranch,
            scopeId: "branch-a"));
        var branchB = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Basic,
            StatisticReconciliationExpectedMetricKinds.Field,
            "m-basic-branch", "MONTH:2026-08", fieldId: "f",
            scopeKind: StatisticReconciliationExpectedBasicScopes.FlowBranch,
            scopeId: "branch-b"));
        True(branchA.IdentitySha256 != branchB.IdentitySha256,
            "basic branch scope IDs are distinct");
        return Task.CompletedTask;
    }

    internal static Task AdvancedThreeGrains()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var hashes = StatisticReconciliationExpectedAdvancedGrains.All
            .Select(grain => compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Advanced,
                StatisticReconciliationExpectedMetricKinds.Field,
                "m-advanced", "MONTH:2026-08", fieldId: "f", grain: grain))
                .IdentitySha256)
            .ToArray();
        Equal(3, hashes.Distinct().Count(), "advanced grains");
        return Task.CompletedTask;
    }

    internal static Task DiffThreeKinds()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var identities = new[]
        {
            compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedMetricKinds.Field,
                "m-diff-field", "MONTH:2026-08", fieldId: "f",
                diffKind: StatisticReconciliationExpectedDiffKinds.Field)),
            compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedMetricKinds.Table,
                "m-diff-table", "MONTH:2026-08", tableId: "t",
                diffKind: StatisticReconciliationExpectedDiffKinds.TableMetric)),
            compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedMetricKinds.RowLabel,
                "m-diff-label", "MONTH:2026-08", tableId: "t", rowId: "r", labelId: "l",
                diffKind: StatisticReconciliationExpectedDiffKinds.RowLabel))
        };
        Equal(3, identities.Select(item => item.IdentitySha256).Distinct().Count(),
            "diff identities");
        return Task.CompletedTask;
    }

    internal static async Task AppendCommitLast()
    {
        var backend = new FakeObservationBackend();
        var store = Fixture.Store(backend);
        var result = await store.AppendGenerationAsync(
            Fixture.TypedGeneration(),
            Fixture.UtcNow);
        True(backend.CommitCalled, "commit called");
        Equal(result.DocumentCount - 1, backend.ContentCountAtCommit, "commit last");
        Equal(result.DocumentCount, backend.Documents.Count, "complete count");
    }

    internal static async Task ExactReplay()
    {
        var backend = new FakeObservationBackend();
        var store = Fixture.Store(backend);
        var generation = Fixture.TypedGeneration();
        var first = await store.AppendGenerationAsync(generation, Fixture.UtcNow);
        var second = await store.AppendGenerationAsync(
            generation,
            Fixture.UtcNow.AddMinutes(1));
        True(!first.ExactReplay, "first append");
        True(second.ExactReplay, "second replay");
        Equal(first.ManifestSha256, second.ManifestSha256, "manifest replay");
    }

    internal static async Task ConflictFails()
    {
        var backend = new FakeObservationBackend();
        var generation = Fixture.TypedGeneration();
        backend.SeedUnexpected(generation.ContextPin.ReconciliationId, generation.GenerationId);
        var store = Fixture.Store(backend);
        await ExpectReasonAsync(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationConflict,
            () => store.AppendGenerationAsync(generation, Fixture.UtcNow));
        True(!backend.CommitCalled, "conflict does not commit");
    }

    internal static async Task IncompleteDoesNotCommit()
    {
        var backend = new FakeObservationBackend { DropLastContent = true };
        var store = Fixture.Store(backend);
        await ExpectReasonAsync(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationIncomplete,
            () => store.AppendGenerationAsync(Fixture.TypedGeneration(), Fixture.UtcNow));
        True(!backend.CommitCalled, "incomplete does not commit");
    }

    internal static async Task CompleteLineageRecords()
    {
        var backend = new FakeObservationBackend();
        var generation = Fixture.TypedGeneration();
        await Fixture.Store(backend).AppendGenerationAsync(generation, Fixture.UtcNow);
        Equal(generation.SourcePlan.SourceDecisions.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.SourceDecision),
            "source decisions");
        Equal(generation.SourcePlan.BoundInputs.LockedP8Configuration.Pins.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.ConfigurationPin),
            "configuration pins");
        Equal(generation.SourcePlan.BoundInputs.RuntimeMappingContributionLineage.Pins.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.LineagePin),
            "lineage pins");
        Equal(generation.Atoms.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.ExpectedAtom),
            "typed atoms");
        Equal(1,
            backend.Count(StatisticReconciliationObservationRecordKinds.GenerationCommit),
            "commit");
    }

    private static void Atom(
        IEnumerable<StatisticReconciliationExpectedTypedAtom> atoms,
        string kind,
        string canonical,
        long reports,
        long rows,
        long numeric)
    {
        var atom = atoms.Single(item => item.AtomKind == kind);
        Equal(canonical, atom.CanonicalValue, kind);
        Equal(reports, atom.ReportCount, kind + " reports");
        Equal(rows, atom.RowCount, kind + " rows");
        Equal(numeric, atom.NumericValueCount, kind + " numeric");
    }

    private static void HasValue(
        StatisticReconciliationExpectedCompiledGeneration generation,
        string metric,
        string value)
        => True(generation.Atoms.Any(item =>
            item.Identity.MetricId == metric &&
            item.ValueState == StatisticReconciliationExpectedValueStates.Value &&
            item.CanonicalValue == value), $"{metric}:{value}");

    private static void ExpectReason(string reason, Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {reason}.");
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
            when (error.Reason == reason)
        {
        }
    }

    private static async Task ExpectReasonAsync(string reason, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException($"Expected {reason}.");
        }
        catch (StatisticReconciliationExpectedObservationException error)
            when (error.ReasonCode == reason)
        {
        }
    }

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
}

internal sealed record SourceSpec(
    string StableSourceId,
    string PayloadJson,
    string LifecycleStatus,
    bool IsEffective,
    bool IsLocked,
    string RuntimeDisposition,
    int PayloadRevision,
    int LifecycleRevision);

internal static class Fixture
{
    internal static readonly DateTime UtcNow =
        new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    internal static SourceSpec Source(
        string sourceId,
        string payloadJson,
        string lifecycleStatus = StatisticReconciliationExpectedLifecycleStatuses.Approved,
        bool isEffective = true,
        bool isLocked = true,
        string runtimeDisposition = StatisticReconciliationExpectedRuntimeDispositions.Current,
        int payloadRevision = 1,
        int lifecycleRevision = 1)
        => new(sourceId, payloadJson, lifecycleStatus, isEffective, isLocked,
            runtimeDisposition, payloadRevision, lifecycleRevision);

    internal static ExpectedContributionCandidate Contribution(
        string sourceId,
        string policy,
        long revision = 1)
        => new(
            sourceId,
            policy,
            $"contribution-version-{revision}",
            revision,
            Sha($"policy-{sourceId}-{policy}-{revision}"),
            $"provenance-{sourceId}-{revision}",
            Sha($"provenance-{sourceId}-{policy}-{revision}"),
            true);

    internal static StatisticReconciliationExpectedSourcePlan Plan(
        string configurationJson,
        IReadOnlyList<SourceSpec> sources,
        IReadOnlyList<ExpectedContributionCandidate>? contributions = null)
    {
        var context = Context();
        var candidates = sources.Select(source => Candidate(context, source)).ToArray();
        var membership = new CurrentEpochFlowMembership(
            context,
            context.FlowTemplateVersionId!,
            context.FlowPayloadSha256!,
            context.FlowInstanceId!,
            context.ExecutionEpochId!,
            3,
            7,
            sources.Select(item => item.StableSourceId).Distinct(StringComparer.Ordinal));
        var canonicalConfig = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            configurationJson,
            4 * 1024 * 1024,
            "$.fixture.config",
            StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid);
        var configuration = new LockedP8Configuration(
            context,
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            canonicalConfig.Sha256,
            canonicalConfig.Value,
            [
                new LockedP8ConfigurationPin(
                    StatisticReconciliationExpectedLedgerConfigurationKinds.Field,
                    context.P8ConfigurationOwnerId,
                    "config-field",
                    "config-field-v1",
                    1,
                    1,
                    Sha("config-field"))
            ]);
        var lineage = new P5P7RuntimeMappingContributionLineage(
            context,
            StatisticReconciliationExpectedLedgerLineageLayers.Required.Select((layer, index) =>
                new ExpectedLedgerLineagePin(
                    layer,
                    $"owner-{index}",
                    $"version-{index}",
                    index + 1,
                    Sha($"lineage-{index}"))));
        var snapshot = new ExpectedAuthoritativeSourceSnapshot(
            context,
            candidates,
            membership,
            configuration,
            lineage,
            contributions ?? []);
        return new StatisticReconciliationExpectedSourcePlanner(
            new StatisticReconciliationExpectedLedgerCompiler()).Plan(snapshot);
    }

    internal static StatisticReconciliationExpectedCompiledGeneration TypedGeneration(
        bool reverseSources = false)
    {
        var sources = new[]
        {
            Source("s1", "{\"nums\":[10,20,null,\"\"],\"bucket\":\"A\",\"date\":\"2026\",\"fullDate\":\"10/08/2026\",\"period\":\"MONTH:2026-08\",\"flag\":true,\"enum\":\"OPEN\",\"tags\":[\"b\",\"a\",\"a\"],\"text\":\"hello\"}"),
            Source("s2", "{\"nums\":[30],\"bucket\":2,\"date\":\"08/2026\",\"fullDate\":\"11/08/2026\",\"period\":\"DAY:2026-08-11\",\"flag\":false,\"enum\":\"CLOSED\",\"tags\":[\"a\",\"b\"],\"text\":\"world\"}")
        };
        if (reverseSources)
            Array.Reverse(sources);
        var contributions = sources.Select(item => Contribution(
            item.StableSourceId,
            StatisticReconciliationExpectedContributionPolicies.Include)).ToArray();
        var plan = Plan(TypedConfig(), sources, contributions);
        return new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler())
            .Compile(plan, CatalogPins());
    }

    internal static IStatisticReconciliationExpectedObservationStore Store(
        FakeObservationBackend backend)
        => new StatisticReconciliationExpectedObservationStore(
            backend,
            new StatisticReconciliationExpectedMetricIdentityCompiler());

    internal static StatisticReconciliationExpectedCatalogPins CatalogPins()
        => StatisticReconciliationExpectedCatalogPins.Create(
            "P9-CATALOG-V1",
            Sha("p9-catalog-raw"),
            Sha("p9-catalog-semantic"),
            Sha("p9-schema-raw"),
            Sha("p9-schema-semantic"),
            Sha("p9-stage-lock"),
            "p10-chain",
            "P10-01",
            "P10-CANDIDATE-V1",
            Sha("candidate-catalog-raw"),
            Sha("candidate-catalog-semantic"),
            Sha("candidate-schema-raw"),
            Sha("candidate-schema-semantic"),
            Sha("candidate-stage-lock"));

    internal static string MinimalConfig()
        => JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                Metric("value", "/value", StatisticReconciliationExpectedValueTypes.Number,
                    [StatisticReconciliationExpectedMetricOperations.Sum])
            }
        });

    internal static string NumberOnlyConfig()
        => JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                Metric("number", "/nums", StatisticReconciliationExpectedValueTypes.Number,
                    [StatisticReconciliationExpectedMetricOperations.Sum,
                     StatisticReconciliationExpectedMetricOperations.Mean], expandArray: true)
            }
        });

    private static string TypedConfig()
    {
        var metrics = new List<Dictionary<string, object?>>
        {
            Metric("number", "/nums", StatisticReconciliationExpectedValueTypes.Number,
                [StatisticReconciliationExpectedMetricOperations.Sum,
                 StatisticReconciliationExpectedMetricOperations.Min,
                 StatisticReconciliationExpectedMetricOperations.Max,
                 StatisticReconciliationExpectedMetricOperations.Mean], expandArray: true),
            Metric("bucket", "/bucket", StatisticReconciliationExpectedValueTypes.Bucket,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("date", "/date", StatisticReconciliationExpectedValueTypes.Date,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("fullDate", "/fullDate", StatisticReconciliationExpectedValueTypes.FullDate,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("period", "/period", StatisticReconciliationExpectedValueTypes.Period,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("boolean", "/flag", StatisticReconciliationExpectedValueTypes.Boolean,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("enum", "/enum", StatisticReconciliationExpectedValueTypes.Enum,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("tags", "/tags", StatisticReconciliationExpectedValueTypes.StringList,
                [StatisticReconciliationExpectedMetricOperations.Values], unordered: true),
            Metric("text", "/text", StatisticReconciliationExpectedValueTypes.Text,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("missing", "/does-not-exist", StatisticReconciliationExpectedValueTypes.Text,
                [StatisticReconciliationExpectedMetricOperations.Values])
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["expectedMetrics"] = metrics
        });
    }

    private static Dictionary<string, object?> Metric(
        string metricId,
        string pointer,
        string valueType,
        string[] operations,
        bool expandArray = false,
        bool unordered = false)
        => new()
        {
            ["family"] = StatisticReconciliationExpectedMetricFamilies.Direct,
            ["kind"] = StatisticReconciliationExpectedMetricKinds.Field,
            ["metricId"] = metricId,
            ["fieldId"] = "field-" + metricId,
            ["jsonPointer"] = pointer,
            ["valueType"] = valueType,
            ["operations"] = operations,
            ["expandArray"] = expandArray,
            ["unordered"] = unordered
        };

    private static ExpectedLifecycleRevisionCandidate Candidate(
        ExpectedLedgerCompilationContextPin context,
        SourceSpec source)
    {
        var canonical = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            source.PayloadJson,
            16 * 1024 * 1024,
            "$.fixture.payload",
            StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid);
        var identity = new ExpectedSourceIdentityPin(
            source.StableSourceId,
            "report-" + source.StableSourceId,
            context.WorkId,
            context.ScopeAssignmentId,
            source.PayloadRevision,
            Sha($"payload-owner-{source.StableSourceId}-{source.PayloadRevision}"),
            canonical.Sha256,
            source.LifecycleRevision,
            Sha($"lifecycle-{source.StableSourceId}-{source.LifecycleRevision}"),
            context.DynamicFormVersionId,
            context.FlowInstanceId!,
            context.ExecutionEpochId!);
        return new ExpectedLifecycleRevisionCandidate(
            source.StableSourceId,
            identity,
            $"payload-{source.StableSourceId}-{source.PayloadRevision}",
            canonical.Value,
            source.LifecycleStatus,
            source.IsEffective,
            source.IsLocked,
            source.RuntimeDisposition);
    }

    private static ExpectedLedgerCompilationContextPin Context()
        => new(
            "reconciliation-1",
            Sha("immutable-identity"),
            Sha("immutable-header"),
            "tenant-1",
            "work-1",
            "assignment-1",
            "p10-chain",
            "P10-01",
            "MONTH:2026-08",
            "period-instance-1",
            "concept-1",
            "MONTH",
            "APPROVED_AT",
            Sha("filter"),
            "form-version-1",
            Sha("form-schema"),
            "flow-template-version-1",
            Sha("flow-payload"),
            "flow-instance-1",
            "epoch-1",
            "p8-owner-1",
            Sha("p8-bundle"));

    internal static string Sha(string value)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_T14_FIXTURE_V1",
            value);
}

internal sealed class FakeObservationBackend
    : IStatisticReconciliationExpectedObservationBackend
{
    internal Dictionary<string, StatisticReconciliationObservation> Documents { get; } =
        new(StringComparer.Ordinal);
    internal bool DropLastContent { get; init; }
    internal bool CommitCalled { get; private set; }
    internal int ContentCountAtCommit { get; private set; }

    public Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>(
            Documents.Values
                .Where(item =>
                    item.ReconciliationId == reconciliationId &&
                    item.GenerationId == generationId)
                .Select(item => new StatisticReconciliationExpectedStoredObservation(
                    item.Id,
                    item.RecordKind,
                    item.DocumentSemanticSha256))
                .ToArray());

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        var count = DropLastContent && observations.Count > 0
            ? observations.Count - 1
            : observations.Count;
        for (var index = 0; index < count; index++)
            Documents.TryAdd(observations[index].Id, observations[index]);
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        CommitCalled = true;
        ContentCountAtCommit = Documents.Count;
        Documents.TryAdd(observation.Id, observation);
        return Task.CompletedTask;
    }

    internal int Count(string recordKind)
        => Documents.Values.Count(item => item.RecordKind == recordKind);

    internal void SeedUnexpected(string reconciliationId, string generationId)
    {
        Documents["unexpected"] = new StatisticReconciliationObservation
        {
            Id = "unexpected",
            RecordKind = StatisticReconciliationObservationRecordKinds.SourceDecision,
            ReconciliationId = reconciliationId,
            GenerationId = generationId,
            DocumentSemanticSha256 = Fixture.Sha("unexpected")
        };
    }
}
