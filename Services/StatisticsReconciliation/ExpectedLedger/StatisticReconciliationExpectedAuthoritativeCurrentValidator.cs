using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record StatisticReconciliationExpectedAuthoritativeCurrentProof(
    StatisticReconciliationExpectedGenerationBinding Binding,
    bool Complete,
    bool Current,
    string CurrentGenerationId,
    string CurrentGenerationSemanticSha256,
    string CurrentMetricPlanSha256,
    int CurrentMetricPlanEntryCount,
    string CurrentMembershipSemanticSha256,
    string CurrentInputBindingSha256,
    string CurrentLifecycleSemanticSha256,
    string CurrentContributionSemanticSha256,
    string FirstCollectProofSha256,
    string SecondCollectProofSha256,
    string DoubleCollectProofSha256);

internal interface
    IStatisticReconciliationExpectedAuthoritativeCurrentValidator
{
    Task<StatisticReconciliationExpectedAuthoritativeCurrentProof> ValidateAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedGenerationBinding exactBinding,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only current-authority verifier. It never appends observations and never
/// reads P9 result values. Both collects independently rebuild the expected
/// generation from current report/assignment/period/payload/P8/flow/mapping
/// owners. Any mutation between collects fails closed.
/// </summary>
internal sealed class
    StatisticReconciliationExpectedAuthoritativeCurrentValidator(
        MongoDbContext context,
        IStatisticReconciliationExpectedGenerationBindingReader bindingReader,
        IStatisticReconciliationExpectedAuthoritativeSnapshotReader snapshotReader,
        IStatisticReconciliationExpectedSourcePlanner sourcePlanner,
        IStatisticReconciliationExpectedTypedCompiler typedCompiler)
    : IStatisticReconciliationExpectedAuthoritativeCurrentValidator
{
    public async Task<StatisticReconciliationExpectedAuthoritativeCurrentProof>
        ValidateAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationExpectedGenerationBinding exactBinding,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(exactBinding);
        var firstPersisted = await ReadRunAsync(run.Id, cancellationToken)
            .ConfigureAwait(false);
        var resolvedBinding = await bindingReader.ResolveAsync(
                firstPersisted,
                exactBinding.GenerationId,
                exactBinding.GenerationSemanticSha256,
                exactBinding.MetricPlanSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolvedBinding != exactBinding)
            throw Fail("EXACT_GENERATION_BINDING_MISMATCH");
        var firstEffective =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(firstPersisted);
        RequireSuppliedIdentity(run, firstEffective);
        var first = await CompileCurrentAsync(
                firstEffective,
                cancellationToken)
            .ConfigureAwait(false);
        var firstProof = CollectProof(first);

        var secondPersisted = await ReadRunAsync(run.Id, cancellationToken)
            .ConfigureAwait(false);
        if (firstPersisted.StateRevision != secondPersisted.StateRevision ||
            !StringComparer.Ordinal.Equals(
                firstPersisted.StateHash,
                secondPersisted.StateHash) ||
            !StringComparer.Ordinal.Equals(
                firstPersisted.ImmutableIdentityHash,
                secondPersisted.ImmutableIdentityHash) ||
            !StringComparer.Ordinal.Equals(
                firstPersisted.ImmutableHeaderHash,
                secondPersisted.ImmutableHeaderHash))
            throw Fail("RUN_CHANGED_DURING_DOUBLE_COLLECT");
        var secondEffective =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(secondPersisted);
        RequireSameEffectiveView(firstEffective, secondEffective);
        var second = await CompileCurrentAsync(
                secondEffective,
                cancellationToken)
            .ConfigureAwait(false);
        StatisticReconciliationExpectedGenerationOwner
            .RequireSameAuthoritativeGeneration(first, second);
        var secondProof = CollectProof(second);
        if (!StringComparer.Ordinal.Equals(firstProof, secondProof))
            throw Fail("AUTHORITATIVE_DOUBLE_COLLECT_DRIFT");

        var current =
            StringComparer.Ordinal.Equals(
                second.GenerationId,
                exactBinding.GenerationId) &&
            StringComparer.Ordinal.Equals(
                second.GenerationSemanticSha256,
                exactBinding.GenerationSemanticSha256) &&
            StringComparer.Ordinal.Equals(
                second.MetricPlanSha256,
                exactBinding.MetricPlanSha256) &&
            second.MetricPlan.Length == exactBinding.MetricPlanEntryCount &&
            StringComparer.Ordinal.Equals(
                second.MembershipSemanticSha256,
                exactBinding.MembershipSemanticSha256) &&
            StringComparer.Ordinal.Equals(
                second.ContextPin.RuntimeKind,
                exactBinding.RuntimeKind);
        var doubleCollect = BuildDoubleCollectProofSha256(
            exactBinding,
            firstPersisted.ImmutableHeaderHash,
            firstProof,
            secondProof,
            current);
        return new(
            exactBinding,
            Complete: true,
            Current: current,
            second.GenerationId,
            second.GenerationSemanticSha256,
            second.MetricPlanSha256,
            second.MetricPlan.Length,
            second.MembershipSemanticSha256,
            second.SourcePlan.BoundInputs.InputFingerprints.InputBindingSha256,
            second.SourcePlan.LifecycleSemanticSha256,
            second.SourcePlan.ContributionSemanticSha256,
            firstProof,
            secondProof,
            doubleCollect);
    }

    private async Task<StatisticReconciliationExpectedCompiledGeneration>
        CompileCurrentAsync(
            StatisticReconciliationRun effectiveRun,
            CancellationToken cancellationToken)
    {
        var jobs = await context.WorkReportStatisticRebuildJobs
            .Find(job =>
                job.Id == effectiveRun.P9RunId &&
                job.GenerationId == effectiveRun.P9GenerationId &&
                job.GenerationHash == effectiveRun.P9GenerationHash &&
                job.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                !job.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (jobs.Count != 1 ||
            jobs[0].DirectSourceRevision is not > 0 ||
            !IsSha(jobs[0].SourceMembershipSignature))
            throw Fail("CURRENT_SOURCE_OWNER_NOT_EXACT");
        var source = new StatisticReconciliationExpectedSourceOwnerSelector(
            jobs[0].Id,
            jobs[0].GenerationId,
            jobs[0].GenerationHash,
            jobs[0].SourceMembershipSignature,
            jobs[0].DirectSourceRevision!.Value);
        StatisticReconciliationExpectedGenerationOwner.RequireSourceOwner(
            effectiveRun,
            source);
        var contextPin = StatisticReconciliationExpectedGenerationOwner
            .BuildContext(effectiveRun, source);
        var snapshot = await snapshotReader.ReadAsync(
                contextPin,
                cancellationToken)
            .ConfigureAwait(false);
        var plan = sourcePlanner.Plan(snapshot);
        var generation = typedCompiler.Compile(
            plan,
            StatisticReconciliationExpectedGenerationOwner.BuildCatalogPins(
                effectiveRun));
        if (generation.MetricPlan.Length <= 0)
            throw Fail("SIGNABLE_METRIC_PLAN_REQUIRED");
        return generation;
    }

    private async Task<StatisticReconciliationRun> ReadRunAsync(
        string reconciliationId,
        CancellationToken cancellationToken)
    {
        var values = await context.StatisticReconciliationRuns
            .Find(item => item.Id == reconciliationId && !item.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (values.Count != 1)
            throw Fail("RUN_NOT_EXACT");
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            values[0]);
        return values[0];
    }

    private static string CollectProof(
        StatisticReconciliationExpectedCompiledGeneration value)
        => H(
            "P10_EXPECTED_AUTHORITATIVE_COLLECT_V1",
            [value.GenerationId,
             value.GenerationSemanticSha256,
             value.SourceSetSha256,
             value.MembershipSemanticSha256,
             value.TypedSemanticSha256,
             value.MetricPlanSha256,
             value.MetricPlan.Length.ToString(
                 System.Globalization.CultureInfo.InvariantCulture),
             value.SourcePlan.BoundInputs.InputFingerprints.InputBindingSha256,
             value.SourcePlan.LifecycleSemanticSha256,
             value.SourcePlan.ContributionSemanticSha256,
             value.SourcePlan.SourceDecisions.Length.ToString(
                 System.Globalization.CultureInfo.InvariantCulture)]);

    internal static string BuildDoubleCollectProofSha256(
        StatisticReconciliationExpectedGenerationBinding exactBinding,
        string immutableHeaderHash,
        string firstProofSha256,
        string secondProofSha256,
        bool current)
    {
        ArgumentNullException.ThrowIfNull(exactBinding);
        if (!IsSha(exactBinding.GenerationId) ||
            !IsSha(exactBinding.GenerationSemanticSha256) ||
            !IsSha(exactBinding.MetricPlanSha256) ||
            !IsSha(exactBinding.ManifestSha256) ||
            exactBinding.DocumentCount < 1 ||
            !IsSha(immutableHeaderHash) ||
            !IsSha(firstProofSha256) ||
            !IsSha(secondProofSha256))
            throw Fail("DOUBLE_COLLECT_PROOF_INPUT_INVALID");
        return H(
            "P10_EXPECTED_AUTHORITATIVE_CURRENT_PROOF_V2",
            [exactBinding.GenerationId,
             exactBinding.GenerationSemanticSha256,
             exactBinding.MetricPlanSha256,
             exactBinding.ManifestSha256,
             exactBinding.DocumentCount.ToString(
                 System.Globalization.CultureInfo.InvariantCulture),
             immutableHeaderHash,
             firstProofSha256,
             secondProofSha256,
             current ? "CURRENT" : "DRIFTED"]);
    }

    private static void RequireSuppliedIdentity(
        StatisticReconciliationRun supplied,
        StatisticReconciliationRun current)
    {
        if (!StringComparer.Ordinal.Equals(supplied.Id, current.Id) ||
            !StringComparer.Ordinal.Equals(
                supplied.ImmutableIdentityHash,
                current.ImmutableIdentityHash) ||
            !StringComparer.Ordinal.Equals(
                supplied.ImmutableHeaderHash,
                current.ImmutableHeaderHash))
            throw Fail("SUPPLIED_RUN_IDENTITY_MISMATCH");
    }

    private static void RequireSameEffectiveView(
        StatisticReconciliationRun first,
        StatisticReconciliationRun second)
    {
        if (first.P9RunId != second.P9RunId ||
            first.P9GenerationId != second.P9GenerationId ||
            first.P9GenerationHash != second.P9GenerationHash ||
            first.P8ConfigVersionId != second.P8ConfigVersionId ||
            first.P8ConfigRevision != second.P8ConfigRevision ||
            first.P8ConfigHash != second.P8ConfigHash ||
            first.P8ConfigBundleHash != second.P8ConfigBundleHash ||
            first.DynamicFormVersionId != second.DynamicFormVersionId ||
            first.DynamicFormSchemaHash != second.DynamicFormSchemaHash ||
            first.FlowTemplateVersionId != second.FlowTemplateVersionId ||
            first.FlowPayloadHash != second.FlowPayloadHash ||
            first.FlowInstanceId != second.FlowInstanceId ||
            first.FlowExecutionEpochId != second.FlowExecutionEpochId ||
            first.FlowExecutionEpoch != second.FlowExecutionEpoch ||
            first.FlowExecutionEpochRevision != second.FlowExecutionEpochRevision)
            throw Fail("EFFECTIVE_CAPTURE_VIEW_DRIFT");
    }

    private static bool IsSha(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or
            >= 'a' and <= 'f');
    private static string H(string domain, IEnumerable<string> values)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            domain,
            values);
    private static StatisticReconciliationExpectedGenerationOwnerException Fail(
        string reason) => new(reason);
}