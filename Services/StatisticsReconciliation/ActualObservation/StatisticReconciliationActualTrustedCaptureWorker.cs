using System.Collections.Immutable;
using MongoDB.Bson;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualClaimedCaptureCommand(
    string ReconciliationId,
    string WorkerId,
    string ClaimToken,
    MeResponse WorkerActor);

/// <summary>
/// Exact, server-resolved owner selectors. It intentionally contains no
/// collection name, BSON filter, projection, or caller supplied hash.
/// </summary>
internal sealed record StatisticReconciliationActualBoundaryOwnerSelectors(
    ImmutableArray<string> SourceReportIds,
    string? BasicSnapshotId,
    string? BasicImmutableSelectorSha256,
    ImmutableArray<string> AdvancedDayNodeIds,
    ImmutableArray<string> AdvancedMonthNodeIds,
    ImmutableArray<string> AdvancedYearNodeIds,
    string? AdvancedImmutableSelectorSha256,
    string? DiffResultId,
    string? DiffImmutableSelectorSha256,
    string ExportCollection,
    string ExportId,
    long ExportLifecycleRevision);

internal sealed record StatisticReconciliationActualTrustedCaptureMaterial(
    StatisticReconciliationRun Run,
    string BoundaryRegistryVersion,
    ActualSourceMembershipScope Source,
    ActualDirectProjectionBoundary Direct,
    ActualAggregatePublicationBoundary Aggregate,
    StatisticReconciliationActualBasicCaptureTarget? Basic,
    ActualAdvancedOwnerBoundary? Advanced,
    ActualP9DiffOwnerBoundary? Diff,
    StatisticReconciliationActualApiCaptureRequest Api,
    StatisticReconciliationActualExportOwnerTarget Export,
    StatisticReconciliationActualBoundaryOwnerSelectors BoundarySelectors)
{
    // The capture view may contain a successor binding. Creation integrity and
    // prior-generation ownership must use the unchanged persisted snapshot.
    internal StatisticReconciliationRun? PersistedRun { get; init; }
}

/// <summary>
/// Production implementation owns the authorization-before-hidden-target,
/// immutable-run integrity, RUNNING lease/fence, plan integrity, and exact
/// owner resolution boundary. A missing legacy plan must fail before it reads
/// any hidden owner referenced by that plan.
/// </summary>
internal interface IStatisticReconciliationActualClaimedCaptureMaterialOwner
{
    Task<StatisticReconciliationActualTrustedCaptureMaterial> ResolveAsync(
        StatisticReconciliationActualClaimedCaptureCommand command,
        CancellationToken cancellationToken);
}

internal interface IStatisticReconciliationActualBoundaryRegistry
{
    StatisticReconciliationActualMongoBoundaryDescriptor Build(
        StatisticReconciliationActualTrustedCaptureMaterial material,
        ActualSourceMembershipCapture sourceCapture);
}

internal sealed class StatisticReconciliationActualTrustedCaptureRequestFactory(
    IStatisticReconciliationActualClaimedCaptureMaterialOwner materialOwner,
    IStatisticReconciliationActualBoundaryRegistry boundaryRegistry,
    IStatisticReconciliationActualSourceOwnerReader sourceOwner,
    IStatisticReconciliationActualCoherentBoundaryReaderFactory boundaryReaders,
    IStatisticReconciliationActualSummaryPlanOwner summaryPlanOwner,
    IStatisticReconciliationExpectedGenerationOwner expectedGenerationOwner,
    IStatisticReconciliationActualLifecyclePriorOwner lifecyclePriorOwner)
{
    internal async Task<StatisticReconciliationActualCaptureRequest> CreateAsync(
        string reconciliationId,
        string workerId,
        string claimToken,
        MeResponse workerActor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialOwner);
        ArgumentNullException.ThrowIfNull(boundaryRegistry);
        ArgumentNullException.ThrowIfNull(sourceOwner);
        ArgumentNullException.ThrowIfNull(boundaryReaders);
        ArgumentNullException.ThrowIfNull(summaryPlanOwner);
        ArgumentNullException.ThrowIfNull(expectedGenerationOwner);
        ArgumentNullException.ThrowIfNull(lifecyclePriorOwner);
        ArgumentNullException.ThrowIfNull(workerActor);
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(workerActor);

        var command = new StatisticReconciliationActualClaimedCaptureCommand(
            Required(reconciliationId, "RECONCILIATION_ID"),
            Required(workerId, "WORKER_ID"),
            Required(claimToken, "CLAIM_TOKEN"),
            workerActor);
        var material = await materialOwner.ResolveAsync(command, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("TRUSTED_MATERIAL_NULL");
        ValidateClaimedMaterial(command, material);

        // Source-set SHA is not persisted on the P10 run and is not equivalent
        // to the P9 source-payload hash. Derive it from the exact frozen P9
        // owner selector before constructing the coherent boundary.
        var sourceCapture = await new StatisticReconciliationActualSourceMembershipAdapter()
            .CaptureAsync(material.Source, sourceOwner, cancellationToken)
            .ConfigureAwait(false);
        var descriptor = boundaryRegistry.Build(
            material,
            sourceCapture);

        // The catalog pin-set SHA includes live projected owner bytes. Obtain it
        // from the same production reader that CaptureService will use again
        // before and after all eight layers.
        var boundaryReader = boundaryReaders.Create(descriptor)
            ?? throw Fail("BOUNDARY_READER_NULL");
        var initialBoundary = await boundaryReader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        // Re-resolve all plan-bound material after the exact initial Mongo
        // boundary is pinned. Owner mutation before this point fails the
        // strengthened plan fingerprints; mutation after it is caught by the
        // final boundary/file guards in CaptureService.
        var confirmed = await materialOwner.ResolveAsync(
                command,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("TRUSTED_CONFIRMATION_NULL");
        ValidateClaimedMaterial(command, confirmed);
        ValidateStableMaterial(material, confirmed);

        var exactExpectedBinding = await expectedGenerationOwner
            .EnsureGenerationAsync(
                new StatisticReconciliationExpectedEnsureGenerationCommand(
                    command.ReconciliationId,
                    command.WorkerId,
                    command.ClaimToken,
                    command.WorkerActor,
                    new StatisticReconciliationExpectedSourceOwnerSelector(
                        confirmed.Source.OwnerRunId,
                        confirmed.Source.OwnerGenerationId,
                        confirmed.Source.OwnerGenerationSha256,
                        confirmed.Source.OwnerMembershipSignature,
                        confirmed.Source.OwnerDirectSourceRevision)),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("EXPECTED_GENERATION_BINDING_NULL");
        var summaryPlan = await summaryPlanOwner.ResolveAsync(
                confirmed.Run,
                exactExpectedBinding,
                cancellationToken)
            .ConfigureAwait(false);
        var publication = PublicationContext(
            confirmed.Run,
            initialBoundary.CatalogPinSetSha256,
            exactExpectedBinding.RuntimeKind) with
        {
            SummaryPlanBinding = summaryPlan
        };
        var lifecyclePrior = await lifecyclePriorOwner.ResolveAsync(
                confirmed.PersistedRun!,
                cancellationToken)
            .ConfigureAwait(false);

        return new StatisticReconciliationActualCaptureRequest(
            command.ReconciliationId,
            descriptor,
            initialBoundary,
            confirmed.Source,
            confirmed.Direct,
            confirmed.Aggregate,
            confirmed.Basic,
            confirmed.Advanced,
            confirmed.Diff,
            confirmed.Api,
            confirmed.Export,
            publication,
            Utc(confirmed.Run.CreatedAtUtc, "RUN_CREATED_AT_UTC"),
            command.WorkerId,
            command.ClaimToken,
            command.WorkerActor,
            lifecyclePrior,
            confirmed.Run,
            exactExpectedBinding,
            confirmed);
    }

    private static void ValidateClaimedMaterial(
        StatisticReconciliationActualClaimedCaptureCommand command,
        StatisticReconciliationActualTrustedCaptureMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var run = material.Run ?? throw Fail("RUN_NULL");
        ValidateCaptureRunViews(material.PersistedRun, run);
        if (!StringComparer.Ordinal.Equals(run.Id, command.ReconciliationId) ||
            !StringComparer.Ordinal.Equals(
                run.Status,
                StatisticReconciliationRunStatuses.Running) ||
            !StringComparer.Ordinal.Equals(run.LeaseOwnerId, command.WorkerId) ||
            !StringComparer.Ordinal.Equals(run.ClaimToken, command.ClaimToken) ||
            !run.LeaseUntilUtc.HasValue ||
            run.LeaseUntilUtc.Value.Kind != DateTimeKind.Utc ||
            run.LeaseUntilUtc.Value <= DateTime.UtcNow ||
            run.DeadlineAtUtc.Kind != DateTimeKind.Utc ||
            run.DeadlineAtUtc <= DateTime.UtcNow)
        {
            throw Fail("STALE_WORKER_FENCE");
        }
        var plan = run.ActualCapturePlan ?? throw Fail("CAPTURE_PLAN_NULL");
        var basicNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) &&
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Basic.Disposition);
        var advancedNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) &&
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Advanced.Disposition);
        var diffNotApplicable =
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) &&
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Diff.Disposition);
        if ((material.Basic is null) != basicNotApplicable ||
            (material.Advanced is null) != advancedNotApplicable ||
            (material.Diff is null) != diffNotApplicable ||
            !StringComparer.Ordinal.Equals(run.WorkId, material.Source.WorkId) ||
            !StringComparer.Ordinal.Equals(run.WorkId, material.Direct.WorkId) ||
            material.Basic is not null && !StringComparer.Ordinal.Equals(
                run.ScopeAssignmentId,
                material.Basic.Boundary.ScopeAssignmentId) ||
            material.Advanced is not null && !StringComparer.Ordinal.Equals(
                run.ScopeAssignmentId,
                material.Advanced.AssignmentId) ||
            material.Diff is not null && !StringComparer.Ordinal.Equals(
                run.ScopeAssignmentId,
                material.Diff.AssignmentId) ||
            !StringComparer.Ordinal.Equals(run.WorkId, material.Export.WorkId) ||
            !StringComparer.Ordinal.Equals(
                run.ScopeAssignmentId,
                material.Export.ScopeId))
        {
            throw Fail("TRUSTED_MATERIAL_SCOPE_MISMATCH");
        }
    }

    internal static void ValidateCaptureRunViews(
        StatisticReconciliationRun? persistedRun,
        StatisticReconciliationRun captureRun)
    {
        var persisted = persistedRun ?? throw Fail("PERSISTED_RUN_NULL");
        // Never validate a projected successor against creation hashes, and
        // never trust a separately supplied view without binding every field.
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(persisted);
        var expected = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCaptureRun(persisted);
        if (!expected.ToBsonDocument().Equals(captureRun.ToBsonDocument()))
        {
            throw Fail("CAPTURE_RUN_VIEW_MISMATCH");
        }
    }

    private static void ValidateStableMaterial(
        StatisticReconciliationActualTrustedCaptureMaterial first,
        StatisticReconciliationActualTrustedCaptureMaterial confirmed)
    {
        var left = first.BoundarySelectors ?? throw Fail("SELECTORS_NULL");
        var right = confirmed.BoundarySelectors ?? throw Fail("SELECTORS_NULL");
        if (!Same(first.Run.Id, confirmed.Run.Id) ||
            !Same(first.Run.ImmutableIdentityHash,
                confirmed.Run.ImmutableIdentityHash) ||
            !Same(first.Run.ImmutableHeaderHash,
                confirmed.Run.ImmutableHeaderHash) ||
            !Same(first.Run.ActualCapturePlanSha256,
                confirmed.Run.ActualCapturePlanSha256) ||
            !Same(first.Run.ActualConfigurationBundleSha256,
                confirmed.Run.ActualConfigurationBundleSha256) ||
            !Same(first.Run.AuthorizationSnapshotHash,
                confirmed.Run.AuthorizationSnapshotHash) ||
            !Same(first.BoundaryRegistryVersion,
                confirmed.BoundaryRegistryVersion) ||
            !Same(first.Source.OwnerRunId, confirmed.Source.OwnerRunId) ||
            !Same(first.Source.OwnerGenerationId,
                confirmed.Source.OwnerGenerationId) ||
            !Same(first.Source.OwnerGenerationSha256,
                confirmed.Source.OwnerGenerationSha256) ||
            first.Source.OwnerDirectSourceRevision !=
                confirmed.Source.OwnerDirectSourceRevision ||
            first.Source.LifecycleMetricScope !=
                confirmed.Source.LifecycleMetricScope ||
            !Same(first.Direct.GenerationId, confirmed.Direct.GenerationId) ||
            !Same(first.Direct.OwnerGenerationSha256,
                confirmed.Direct.OwnerGenerationSha256) ||
            !Same(left.BasicSnapshotId, right.BasicSnapshotId) ||
            !Same(left.BasicImmutableSelectorSha256,
                right.BasicImmutableSelectorSha256) ||
            !left.AdvancedDayNodeIds.SequenceEqual(
                right.AdvancedDayNodeIds,
                StringComparer.Ordinal) ||
            !left.AdvancedMonthNodeIds.SequenceEqual(
                right.AdvancedMonthNodeIds,
                StringComparer.Ordinal) ||
            !left.AdvancedYearNodeIds.SequenceEqual(
                right.AdvancedYearNodeIds,
                StringComparer.Ordinal) ||
            !Same(left.AdvancedImmutableSelectorSha256,
                right.AdvancedImmutableSelectorSha256) ||
            !Same(left.DiffResultId, right.DiffResultId) ||
            !Same(left.DiffImmutableSelectorSha256,
                right.DiffImmutableSelectorSha256) ||
            !Same(left.ExportCollection, right.ExportCollection) ||
            !Same(left.ExportId, right.ExportId) ||
            left.ExportLifecycleRevision != right.ExportLifecycleRevision ||
            !Same(first.Api.Surface, confirmed.Api.Surface) ||
            !Same(first.Api.OwnerResultId, confirmed.Api.OwnerResultId) ||
            first.Api.ExpectedTotalRows != confirmed.Api.ExpectedTotalRows ||
            !first.Api.Pages.SequenceEqual(confirmed.Api.Pages) ||
            !Same(first.Export.ExpectedOwnerSemanticSha256,
                confirmed.Export.ExpectedOwnerSemanticSha256) ||
            !Same(first.Export.ExpectedSourceOwnerSha256,
                confirmed.Export.ExpectedSourceOwnerSha256) ||
            !Same(first.Export.ExpectedConfigOwnerSha256,
                confirmed.Export.ExpectedConfigOwnerSha256) ||
            !Same(first.Export.ExpectedPeriodInstanceKey,
                confirmed.Export.ExpectedPeriodInstanceKey) ||
            !Same(first.Export.ExpectedFilterSha256,
                confirmed.Export.ExpectedFilterSha256) ||
            !Same(first.Export.ExpectedContentSha256,
                confirmed.Export.ExpectedContentSha256))
        {
            throw Fail("TRUSTED_MATERIAL_DRIFT");
        }
    }

    private static bool Same(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);
    private static StatisticReconciliationActualPublicationContext
        PublicationContext(
            StatisticReconciliationRun run,
            string catalogPinSetSha256,
            string runtimeKind)
    {
        runtimeKind = StatisticReconciliationActualCanonical.Upper(
            runtimeKind, "RUNTIME_KIND");
        if (runtimeKind is not (
                StatisticReconciliationExpectedRuntimeKinds.Flow or
                StatisticReconciliationExpectedRuntimeKinds.NonFlow))
            throw Fail("RUNTIME_KIND_INVALID");
        var isFlow = runtimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.Flow;
        if (!isFlow && (run.FlowTemplateVersionId is not null ||
            run.FlowPayloadHash is not null || run.FlowInstanceId is not null ||
            run.FlowExecutionEpochId is not null ||
            run.FlowExecutionEpoch is not null ||
            run.FlowExecutionEpochRevision is not null))
            throw Fail("NON_FLOW_RUNTIME_PINS_PRESENT");
        var flowTemplateVersionId = isFlow
            ? Required(run.FlowTemplateVersionId, "FLOW_TEMPLATE_VERSION_ID")
            : null;
        var flowPayloadSha256 = isFlow
            ? Sha(run.FlowPayloadHash, "FLOW_PAYLOAD")
            : null;
        var flowInstanceId = isFlow
            ? Required(run.FlowInstanceId, "FLOW_INSTANCE_ID")
            : null;
        var executionEpochId = isFlow
            ? Required(run.FlowExecutionEpochId, "EXECUTION_EPOCH_ID")
            : null;
        int? executionEpoch = isFlow
            ? Positive(run.FlowExecutionEpoch, "EXECUTION_EPOCH")
            : null;
        long? executionEpochRevision = isFlow
            ? Positive(run.FlowExecutionEpochRevision,
                "EXECUTION_EPOCH_REVISION")
            : null;
        string? recheckCaptureBindingSha256 = null;
        if (run.Recheck is not null)
        {
            StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(
                run.Recheck.CaptureBinding);
            recheckCaptureBindingSha256 =
                run.Recheck.CaptureBinding!.BindingSha256;
        }
        var staticCatalogPinSetSha256 =
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CATALOG_PIN_SET_V1",
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
        var catalogPins = new StatisticReconciliationActualPublicationCatalogPins(
            Required(run.P9CatalogVersion, "P9_CATALOG_VERSION"),
            Sha(run.P9CatalogRawSha256, "P9_CATALOG_RAW"),
            Sha(run.P9CatalogSemanticSha256, "P9_CATALOG_SEMANTIC"),
            Sha(run.P9SchemaRawSha256, "P9_SCHEMA_RAW"),
            Sha(run.P9SchemaSemanticSha256, "P9_SCHEMA_SEMANTIC"),
            Sha(run.P9StageLockSha256, "P9_STAGE_LOCK"),
            Required(run.CandidateChainId, "CANDIDATE_CHAIN"),
            Required(run.CandidatePromptId, "CANDIDATE_PROMPT"),
            Required(run.CandidateCatalogVersion, "CANDIDATE_CATALOG_VERSION"),
            Sha(run.CandidateCatalogRawSha256, "CANDIDATE_CATALOG_RAW"),
            Sha(run.CandidateCatalogSemanticSha256, "CANDIDATE_CATALOG_SEMANTIC"),
            Sha(run.CandidateSchemaRawSha256, "CANDIDATE_SCHEMA_RAW"),
            Sha(run.CandidateSchemaSemanticSha256, "CANDIDATE_SCHEMA_SEMANTIC"),
            Sha(run.CandidateStageLockSha256, "CANDIDATE_STAGE_LOCK"),
            staticCatalogPinSetSha256);
        return new StatisticReconciliationActualPublicationContext(
            Required(run.Id, "RUN_ID"),
            Sha(run.ImmutableIdentityHash, "IMMUTABLE_IDENTITY"),
            Sha(run.ImmutableHeaderHash, "IMMUTABLE_HEADER"),
            Required(run.WorkId, "WORK_ID"),
            Required(run.ScopeAssignmentId, "SCOPE_ASSIGNMENT_ID"),
            Required(run.PeriodKey, "PERIOD_KEY"),
            Required(run.PeriodInstanceKey, "PERIOD_INSTANCE_KEY"),
            Required(run.ConceptKey, "CONCEPT_KEY"),
            Required(run.Grain, "GRAIN"),
            Required(run.TimeAxis, "TIME_AXIS"),
            Sha(run.FilterHash, "FILTER"),
            Required(run.DynamicFormVersionId, "FORM_VERSION_ID"),
            Sha(run.DynamicFormSchemaHash, "FORM_SCHEMA"),
            flowTemplateVersionId,
            flowPayloadSha256,
            flowInstanceId,
            executionEpochId,
            executionEpoch,
            executionEpochRevision,
            Required(run.P8ConfigOwnerId, "P8_CONFIG_OWNER_ID"),
            Sha(run.P8ConfigBundleHash, "P8_CONFIG_BUNDLE"),
            Sha(run.ActualConfigurationBundleSha256, "ACTUAL_CONFIG_BUNDLE"),
            Sha(catalogPinSetSha256, "COHERENT_CATALOG_PIN_SET"),
            catalogPins,
            recheckCaptureBindingSha256,
            RuntimeKind: runtimeKind);
    }

    private static int Positive(int? value, string name)
        => value is > 0 ? value.Value : throw Fail($"{name}_INVALID");

    private static long Positive(long? value, string name)
        => value is > 0 ? value.Value : throw Fail($"{name}_INVALID");

    private static DateTime Utc(DateTime value, string name)
        => value.Kind == DateTimeKind.Utc ? value : throw Fail($"{name}_INVALID");

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_TRUSTED_FACTORY_{reason}");
}

/// <summary>
/// Internal worker-owned production seam. No controller or public DTO accepts
/// an actual capture request, owner hash, collection, projection, or BSON slice.
/// </summary>
internal interface IStatisticReconciliationActualClaimedCaptureWorker
{
    Task<StatisticReconciliationActualCaptureOutcome> CaptureAsync(
        string reconciliationId,
        string workerId,
        string claimToken,
        MeResponse workerActor,
        CancellationToken cancellationToken = default);
}

internal sealed class StatisticReconciliationActualClaimedCaptureWorker(
    StatisticReconciliationActualTrustedCaptureRequestFactory requestFactory,
    StatisticReconciliationActualCaptureService captureService)
    : IStatisticReconciliationActualClaimedCaptureWorker
{
    public async Task<StatisticReconciliationActualCaptureOutcome> CaptureAsync(
        string reconciliationId,
        string workerId,
        string claimToken,
        MeResponse workerActor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(captureService);
        var request = await requestFactory.CreateAsync(
                reconciliationId,
                workerId,
                claimToken,
                workerActor,
                cancellationToken)
            .ConfigureAwait(false);
        return await captureService.CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
