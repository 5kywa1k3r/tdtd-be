using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record StatisticReconciliationExpectedSourceOwnerSelector(
    string OwnerRunId,
    string OwnerGenerationId,
    string OwnerGenerationSha256,
    string OwnerMembershipSha256,
    long OwnerRevision);

internal sealed record StatisticReconciliationExpectedEnsureGenerationCommand(
    string ReconciliationId,
    string WorkerId,
    string ClaimToken,
    MeResponse WorkerActor,
    StatisticReconciliationExpectedSourceOwnerSelector SourceOwner);

internal interface IStatisticReconciliationExpectedGenerationOwner
{
    Task<StatisticReconciliationExpectedGenerationBinding> EnsureGenerationAsync(
        StatisticReconciliationExpectedEnsureGenerationCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Server-only production owner for one exact expected generation. Authorization,
/// candidate activation, target integrity and the worker fence are all checked
/// before the authoritative snapshot reader is invoked. The returned full tuple
/// must be persisted by the actual capture/recheck publication; callers may never
/// re-resolve it by latest/unique generation.
/// </summary>
internal sealed class StatisticReconciliationExpectedGenerationOwner(
    MongoDbContext context,
    IStatisticReconciliationCandidateActivation activation,
    IStatisticReconciliationExpectedAuthoritativeSnapshotReader snapshotReader,
    IStatisticReconciliationExpectedSourcePlanner sourcePlanner,
    IStatisticReconciliationExpectedTypedCompiler typedCompiler,
    IStatisticReconciliationExpectedObservationStore observationStore)
    : IStatisticReconciliationExpectedGenerationOwner
{
    public async Task<StatisticReconciliationExpectedGenerationBinding>
        EnsureGenerationAsync(
            StatisticReconciliationExpectedEnsureGenerationCommand command,
            CancellationToken cancellationToken)
    {
        // Auth-before-existence/body: never reveal whether a hidden target or
        // source owner exists to an untrusted actor.
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(command?.WorkerActor!);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.SourceOwner);
        _ = activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerCapture);

        var reconciliationId = Required(command.ReconciliationId,
            "RECONCILIATION_ID");
        var workerId = Required(command.WorkerId, "WORKER_ID");
        var claimToken = Required(command.ClaimToken, "CLAIM_TOKEN");
        var source = Normalize(command.SourceOwner);
        var run = await ReadRunAsync(reconciliationId, cancellationToken)
            .ConfigureAwait(false);
        RequireFence(run, workerId, claimToken, DateTime.UtcNow);
        var effectiveRun =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(run);
        RequireSourceOwner(effectiveRun, source);
        var stateRevision = run.StateRevision;
        var stateHash = run.StateHash;

        var contextPin = BuildContext(effectiveRun, source);
        var snapshot = await snapshotReader.ReadAsync(
                contextPin,
                cancellationToken)
            .ConfigureAwait(false);
        var plan = sourcePlanner.Plan(snapshot);
        var generation = typedCompiler.Compile(
            plan,
            BuildCatalogPins(effectiveRun));
        if (generation.MetricPlan.Length == 0)
            throw Fail("SIGNABLE_METRIC_PLAN_REQUIRED");

        // Check the same lease/state immediately before the append. An orphaned
        // immutable generation is harmless, but a stale worker is never allowed
        // to return a binding that can be published.
        var beforeAppend = await ReadRunAsync(reconciliationId, cancellationToken)
            .ConfigureAwait(false);
        RequireSameFence(
            run,
            beforeAppend,
            workerId,
            claimToken,
            stateRevision,
            stateHash,
            DateTime.UtcNow);
        var effectiveBeforeAppend =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(beforeAppend);
        RequireEquivalentCaptureView(
            contextPin,
            BuildCatalogPins(effectiveRun),
            effectiveBeforeAppend,
            source);
        var secondSnapshot = await snapshotReader.ReadAsync(
                contextPin,
                cancellationToken)
            .ConfigureAwait(false);
        var secondPlan = sourcePlanner.Plan(secondSnapshot);
        var secondGeneration = typedCompiler.Compile(
            secondPlan,
            BuildCatalogPins(effectiveBeforeAppend));
        RequireSameAuthoritativeGeneration(generation, secondGeneration);
        var finalFence = await ReadRunAsync(reconciliationId, cancellationToken)
            .ConfigureAwait(false);
        RequireSameFence(
            run,
            finalFence,
            workerId,
            claimToken,
            stateRevision,
            stateHash,
            DateTime.UtcNow);
        RequireEquivalentCaptureView(
            contextPin,
            BuildCatalogPins(effectiveRun),
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(finalFence),
            source);
        var appended = await observationStore.AppendGenerationAsync(
                secondGeneration,
                DeterministicCreatedAt(run.CreatedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        if (appended.MetricPlanEntryCount <= 0)
            throw Fail("SIGNABLE_METRIC_PLAN_REQUIRED");

        var afterAppend = await ReadRunAsync(reconciliationId, cancellationToken)
            .ConfigureAwait(false);
        RequireSameFence(
            run,
            afterAppend,
            workerId,
            claimToken,
            stateRevision,
            stateHash,
            DateTime.UtcNow);
        RequireEquivalentCaptureView(
            contextPin,
            BuildCatalogPins(effectiveRun),
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(afterAppend),
            source);
        return appended.ExactBinding;
    }

    private async Task<StatisticReconciliationRun> ReadRunAsync(
        string reconciliationId,
        CancellationToken cancellationToken)
    {
        var values = await context.StatisticReconciliationRuns
            .Find(run => run.Id == reconciliationId && !run.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (values.Count != 1)
            throw Fail("RUN_NOT_EXACT");
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            values[0]);
        return values[0];
    }

    internal static ExpectedLedgerCompilationContextPin BuildContext(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedSourceOwnerSelector source)
    {
        var anyFlow = new[]
        {
            run.FlowTemplateVersionId,
            run.FlowPayloadHash,
            run.FlowInstanceId,
            run.FlowExecutionEpochId
        }.Any(value => value is not null);
        var allFlow = new[]
        {
            run.FlowTemplateVersionId,
            run.FlowPayloadHash,
            run.FlowInstanceId,
            run.FlowExecutionEpochId
        }.All(value => !string.IsNullOrWhiteSpace(value));
        if (anyFlow != allFlow)
            throw Fail("RUNTIME_KIND_MIXED");
        if (run.P8ConfigOwnerId is null ||
            string.IsNullOrWhiteSpace(run.DynamicFormSchemaHash))
            throw Fail("RUN_EXPECTED_INPUT_PIN_REQUIRED");
        return new ExpectedLedgerCompilationContextPin(
            run.Id,
            run.ImmutableIdentityHash,
            run.ImmutableHeaderHash,
            run.TenantUnitId,
            run.WorkId,
            run.ScopeAssignmentId,
            run.CandidateChainId,
            run.CandidatePromptId,
            run.PeriodKey,
            run.PeriodInstanceKey,
            run.ConceptKey,
            run.Grain,
            run.TimeAxis,
            run.FilterHash,
            run.DynamicFormVersionId,
            run.DynamicFormSchemaHash,
            allFlow
                ? StatisticReconciliationExpectedRuntimeKinds.Flow
                : StatisticReconciliationExpectedRuntimeKinds.NonFlow,
            run.FlowTemplateVersionId,
            run.FlowPayloadHash,
            run.FlowInstanceId,
            run.FlowExecutionEpochId,
            run.P8ConfigOwnerId,
            run.P8ConfigBundleHash,
            source.OwnerRunId,
            source.OwnerGenerationId,
            source.OwnerGenerationSha256,
            source.OwnerMembershipSha256,
            source.OwnerRevision);
    }

    internal static StatisticReconciliationExpectedCatalogPins BuildCatalogPins(
        StatisticReconciliationRun run)
        => StatisticReconciliationExpectedCatalogPins.Create(
            run.P9CatalogVersion,
            run.P9CatalogRawSha256,
            run.P9CatalogSemanticSha256,
            run.P9SchemaRawSha256,
            run.P9SchemaSemanticSha256,
            run.P9StageLockSha256,
            run.CandidateChainId,
            run.CandidatePromptId,
            run.CandidateCatalogVersion,
            run.CandidateCatalogRawSha256,
            run.CandidateCatalogSemanticSha256,
            run.CandidateSchemaRawSha256,
            run.CandidateSchemaSemanticSha256,
            run.CandidateStageLockSha256);

    internal static void RequireSourceOwner(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedSourceOwnerSelector source)
    {
        if (!StringComparer.Ordinal.Equals(run.P9RunId, source.OwnerRunId) ||
            !StringComparer.Ordinal.Equals(
                run.P9GenerationId,
                source.OwnerGenerationId) ||
            !StringComparer.Ordinal.Equals(
                run.P9GenerationHash,
                source.OwnerGenerationSha256))
            throw Fail("SOURCE_OWNER_RUN_BINDING_MISMATCH");
    }

    private static void RequireEquivalentCaptureView(
        ExpectedLedgerCompilationContextPin expectedContext,
        StatisticReconciliationExpectedCatalogPins expectedCatalog,
        StatisticReconciliationRun observedEffective,
        StatisticReconciliationExpectedSourceOwnerSelector source)
    {
        if (BuildContext(observedEffective, source) != expectedContext ||
            BuildCatalogPins(observedEffective) != expectedCatalog)
            throw Fail("EFFECTIVE_CAPTURE_VIEW_DRIFT");
        RequireSourceOwner(observedEffective, source);
    }

    internal static void RequireSameAuthoritativeGeneration(
        StatisticReconciliationExpectedCompiledGeneration first,
        StatisticReconciliationExpectedCompiledGeneration second)
    {
        if (!StringComparer.Ordinal.Equals(first.SchemaVersion, second.SchemaVersion) ||
            first.ContextPin != second.ContextPin ||
            !StringComparer.Ordinal.Equals(first.GenerationId, second.GenerationId) ||
            !StringComparer.Ordinal.Equals(
                first.GenerationSemanticSha256,
                second.GenerationSemanticSha256) ||
            !StringComparer.Ordinal.Equals(
                first.AlgorithmRevision,
                second.AlgorithmRevision) ||
            !StringComparer.Ordinal.Equals(
                first.AlgorithmSha256,
                second.AlgorithmSha256) ||
            !StringComparer.Ordinal.Equals(
                first.SourceSetSha256,
                second.SourceSetSha256) ||
            !StringComparer.Ordinal.Equals(
                first.MembershipSemanticSha256,
                second.MembershipSemanticSha256) ||
            !StringComparer.Ordinal.Equals(
                first.TypedSemanticSha256,
                second.TypedSemanticSha256) ||
            !StringComparer.Ordinal.Equals(
                first.MetricPlanSha256,
                second.MetricPlanSha256) ||
            first.CatalogPins != second.CatalogPins ||
            first.SourcePlan.LifecycleSemanticSha256 !=
                second.SourcePlan.LifecycleSemanticSha256 ||
            first.SourcePlan.ContributionSemanticSha256 !=
                second.SourcePlan.ContributionSemanticSha256 ||
            first.SourcePlan.SourceSetSha256 !=
                second.SourcePlan.SourceSetSha256 ||
            first.SourcePlan.SourceDecisions.Length !=
                second.SourcePlan.SourceDecisions.Length ||
            first.MetricPlan.Length != second.MetricPlan.Length ||
            first.Atoms.Length != second.Atoms.Length)
            throw Fail("AUTHORITATIVE_SNAPSHOT_DRIFT");
    }
    private static void RequireSameFence(
        StatisticReconciliationRun first,
        StatisticReconciliationRun observed,
        string workerId,
        string claimToken,
        long stateRevision,
        string stateHash,
        DateTime now)
    {
        RequireFence(observed, workerId, claimToken, now);
        if (!StringComparer.Ordinal.Equals(first.Id, observed.Id) ||
            observed.StateRevision != stateRevision ||
            !StringComparer.Ordinal.Equals(observed.StateHash, stateHash) ||
            !StringComparer.Ordinal.Equals(
                first.ImmutableIdentityHash,
                observed.ImmutableIdentityHash) ||
            !StringComparer.Ordinal.Equals(
                first.ImmutableHeaderHash,
                observed.ImmutableHeaderHash))
            throw Fail("STALE_WORKER_STATE");
    }

    private static void RequireFence(
        StatisticReconciliationRun run,
        string workerId,
        string claimToken,
        DateTime now)
    {
        if (!StringComparer.Ordinal.Equals(
                run.Status,
                StatisticReconciliationRunStatuses.Running) ||
            !StringComparer.Ordinal.Equals(run.LeaseOwnerId, workerId) ||
            !StringComparer.Ordinal.Equals(run.ClaimToken, claimToken) ||
            run.LeaseUntilUtc is null || run.LeaseUntilUtc <= now ||
            run.DeadlineAtUtc <= now)
            throw Fail("STALE_WORKER_FENCE");
    }

    private static StatisticReconciliationExpectedSourceOwnerSelector Normalize(
        StatisticReconciliationExpectedSourceOwnerSelector value)
    {
        if (value.OwnerRevision <= 0)
            throw Fail("SOURCE_OWNER_REVISION_INVALID");
        return value with
        {
            OwnerRunId = Required(value.OwnerRunId, "SOURCE_OWNER_RUN_ID"),
            OwnerGenerationId = Sha(value.OwnerGenerationId,
                "SOURCE_OWNER_GENERATION_ID"),
            OwnerGenerationSha256 = Sha(value.OwnerGenerationSha256,
                "SOURCE_OWNER_GENERATION_SHA256"),
            OwnerMembershipSha256 = Sha(value.OwnerMembershipSha256,
                "SOURCE_OWNER_MEMBERSHIP_SHA256")
        };
    }

    private static DateTime DeterministicCreatedAt(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string Required(string? value, string reason)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Length > 512 || value.Any(char.IsControl))
            throw Fail(reason);
        return value;
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or
               >= 'a' and <= 'f');

    private static string Sha(string? value, string reason)
    {
        if (!IsLowerSha256(value))
            throw Fail(reason);
        return value!;
    }

    private static StatisticReconciliationExpectedGenerationOwnerException Fail(
        string reason)
        => new(reason);
}

internal sealed class StatisticReconciliationExpectedGenerationOwnerException(
    string reason)
    : InvalidOperationException(reason)
{
    internal string Reason { get; } = reason;
}