using System.Collections.Immutable;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualBasicCaptureTarget(
    string Mode,
    ActualBasicOwnerBoundary Boundary);

internal sealed record StatisticReconciliationActualCaptureRequest(
    string ReconciliationId,
    StatisticReconciliationActualMongoBoundaryDescriptor Boundary,
    StatisticReconciliationActualCoherentBoundary PinnedInitialBoundary,
    ActualSourceMembershipScope Source,
    ActualDirectProjectionBoundary Direct,
    ActualAggregatePublicationBoundary Aggregate,
    StatisticReconciliationActualBasicCaptureTarget? Basic,
    ActualAdvancedOwnerBoundary? Advanced,
    ActualP9DiffOwnerBoundary? Diff,
    StatisticReconciliationActualApiCaptureRequest Api,
    StatisticReconciliationActualExportOwnerTarget Export,
    StatisticReconciliationActualPublicationContext Publication,
    DateTime CreatedAtUtc,
    string WorkerId,
    string ClaimToken,
    MeResponse Actor,
    StatisticReconciliationActualLifecyclePriorGeneration? LifecyclePrior = null,
    StatisticReconciliationRun? TrustedRun = null,
    StatisticReconciliationExpectedGenerationBinding?
        ExpectedLifecycleBinding = null,
    StatisticReconciliationActualTrustedCaptureMaterial?
        TrustedMaterial = null);

internal sealed record StatisticReconciliationActualCaptureOutcome(
    StatisticReconciliationActualCoherentGeneration Generation,
    StatisticReconciliationActualAppendResult? Publication)
{
    internal bool Published => Publication is not null;
}

internal sealed class StatisticReconciliationActualCapturePublicationGate(
    StatisticReconciliationActualGenerationPublisher publisher)
{
    internal async Task<StatisticReconciliationActualCaptureOutcome> ExecuteAsync(
        string reconciliationId,
        IStatisticReconciliationActualCoherentBoundaryReader boundaryReader,
        ImmutableArray<StatisticReconciliationActualCoherentCaptureStep> steps,
        StatisticReconciliationActualPublicationContext publicationContext,
        DateTime createdAtUtc,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken = default,
        StatisticReconciliationActualCoherentBoundary? pinnedInitialBoundary = null,
        Func<CancellationToken, Task<string?>>? finalGuardAsync = null,
        Func<StatisticReconciliationActualCoherentGeneration,
            StatisticReconciliationActualPublicationContext,
            CancellationToken,
            Task<StatisticReconciliationActualPublicationContext>>?
            preparePublicationAsync = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        var coordinator = new StatisticReconciliationActualCoherentCaptureCoordinator();
        var generation = await coordinator.CaptureAsync(
                reconciliationId,
                boundaryReader,
                steps,
                cancellationToken,
                pinnedInitialBoundary,
                finalGuardAsync)
            .ConfigureAwait(false);
        if (!generation.ReadyForAppendOnlyPersistence)
            return new StatisticReconciliationActualCaptureOutcome(generation, null);

        var preparedContext = preparePublicationAsync is null
            ? publicationContext
            : await preparePublicationAsync(
                    generation,
                    publicationContext,
                    cancellationToken)
                .ConfigureAwait(false);
        var publication = await publisher.PublishCoherentAsync(
                generation,
                preparedContext,
                createdAtUtc,
                workerId,
                claimToken,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        return new StatisticReconciliationActualCaptureOutcome(
            generation,
            publication);
    }
}

/// <summary>
/// Converts independently observed owner records to the neutral actual typed
/// tuple. Implementations may parse only a frozen owner schema; they must not
/// call an expected compiler or a mutating P5-P9 service.
/// </summary>
internal interface IStatisticReconciliationActualAdapterTypedMapper
{
    ImmutableArray<StatisticReconciliationActualTypedObservation> MapDirect(
        ActualDirectProjectionCapture capture,
        string ownerId,
        string ownerVersionSha256);

    ImmutableArray<StatisticReconciliationActualTypedObservation> MapAggregate(
        ActualAggregateCapture capture,
        string scopeAssignmentId,
        string ownerId,
        string ownerVersionSha256);

    ImmutableArray<StatisticReconciliationActualTypedObservation> MapBasic(
        ActualBasicResultObservation capture,
        string ownerId,
        string ownerVersionSha256);

    ImmutableArray<StatisticReconciliationActualTypedObservation> MapAdvanced(
        ActualAdvancedCapture capture,
        string ownerId,
        string ownerVersionSha256);

    ImmutableArray<StatisticReconciliationActualTypedObservation> MapDiff(
        ActualP9DiffCapture capture,
        string ownerId,
        string ownerVersionSha256);

    ImmutableArray<StatisticReconciliationActualTypedObservation> MapApi(
        StatisticReconciliationActualApiCapture capture,
        string ownerId,
        string ownerVersionSha256);

    ImmutableArray<StatisticReconciliationActualTypedObservation> MapExport(
        StatisticReconciliationActualExportCapture capture,
        string ownerId,
        string ownerVersionSha256);
}

/// <summary>
/// Production orchestration boundary for P10 actual capture. It performs only
/// read-only adapter calls, parses caller-supplied immutable export bytes, then
/// delegates the append-only rows plus one-CAS publication to the T25 owner.
/// </summary>
internal sealed class StatisticReconciliationActualCaptureService(
    IStatisticReconciliationActualCoherentBoundaryReaderFactory boundaryReaders,
    IStatisticReconciliationActualSourceOwnerReader sourceOwner,
    IStatisticReconciliationActualDirectProjectionOwnerReader directOwner,
    IStatisticReconciliationActualAggregateOwnerReader aggregateOwner,
    IStatisticReconciliationActualBasicOwnerReader basicOwner,
    IStatisticReconciliationActualAdvancedOwnerReader advancedOwner,
    IStatisticReconciliationActualP9DiffOwnerReader diffOwner,
    IStatisticReconciliationActualApiOwnerReader apiOwner,
    IStatisticReconciliationActualExportOwnerReader exportOwner,
    IStatisticReconciliationActualAdapterTypedMapper typedMapper,
    IStatRunDirectProjectionLifecycleAuditOwner lifecycleAuditOwner,
    IStatisticReconciliationExpectedAuthoritativeLifecycleProjectionReader
        expectedLifecycleReader,
    IStatisticReconciliationActualLifecycleBridge lifecycleBridge,
    IStatisticReconciliationActualLifecycleLedger lifecycleLedger,
    IStatisticReconciliationActualExtendedRawSourceOwner extendedRawOwner,
    IStatisticReconciliationActualExtendedRawSourceOwnerParity
        extendedOwnerParity,
    IStatisticReconciliationActualRelationalProofOwner relationalProofOwner,
    StatisticReconciliationActualGenerationPublisher publisher)
{
    private readonly StatisticReconciliationActualSourceMembershipAdapter
        _source = new();
    private readonly StatisticReconciliationActualDirectProjectionAdapter
        _direct = new();
    private readonly StatisticReconciliationActualAggregateAdapter
        _aggregate = new();
    private readonly StatisticReconciliationActualBasicAdapter _basic = new();
    private readonly StatisticReconciliationActualAdvancedAdapter _advanced = new();
    private readonly StatisticReconciliationActualP9DiffAdapter _diff = new();
    private readonly StatisticReconciliationActualApiObservationAdapter _api =
        new(apiOwner ?? throw new ArgumentNullException(nameof(apiOwner)));
    private readonly StatisticReconciliationActualExportParser _export = new();

    internal async Task<StatisticReconciliationActualCaptureOutcome> CaptureAsync(
        StatisticReconciliationActualCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(boundaryReaders);
        ArgumentNullException.ThrowIfNull(sourceOwner);
        ArgumentNullException.ThrowIfNull(directOwner);
        ArgumentNullException.ThrowIfNull(aggregateOwner);
        ArgumentNullException.ThrowIfNull(basicOwner);
        ArgumentNullException.ThrowIfNull(advancedOwner);
        ArgumentNullException.ThrowIfNull(diffOwner);
        ArgumentNullException.ThrowIfNull(exportOwner);
        ArgumentNullException.ThrowIfNull(typedMapper);
        ArgumentNullException.ThrowIfNull(lifecycleAuditOwner);
        ArgumentNullException.ThrowIfNull(lifecycleBridge);
        ArgumentNullException.ThrowIfNull(lifecycleLedger);
        ArgumentNullException.ThrowIfNull(extendedRawOwner);
        ArgumentNullException.ThrowIfNull(extendedOwnerParity);
        ArgumentNullException.ThrowIfNull(relationalProofOwner);
        ArgumentNullException.ThrowIfNull(publisher);
        ValidateRequest(request);

        var boundaryReader = boundaryReaders.Create(request.Boundary)
            ?? throw Fail("BOUNDARY_READER_NULL");
        ActualSourceMembershipCapture? sourceCapture = null;
        ActualDirectProjectionCapture? directCapture = null;
        ActualAggregateCapture? aggregateCapture = null;
        ActualBasicResultObservation? basicCapture = null;
        ActualAdvancedCapture? advancedCapture = null;
        ActualP9DiffCapture? diffCapture = null;
        StatisticReconciliationActualApiCapture? apiCapture = null;
        StatisticReconciliationActualExportCapture? exportCapture = null;

        var steps = ImmutableArray.Create(
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.SourceMembership,
                async (boundary, ct) =>
                {
                    sourceCapture = await _source.CaptureAsync(
                            request.Source,
                            sourceOwner,
                            ct)
                        .ConfigureAwait(false);
                    var sourceMatches = Eq(
                        sourceCapture.SourceSetSha256,
                        boundary.SourceSetSha256);
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.SourceMembership,
                        boundary,
                        sourceCapture.CaptureSemanticSha256,
                        sourceCapture.Decisions.Length,
                        sourceCapture.OwnerGenerationId,
                        sourceCapture.OwnerGenerationSha256,
                        [],
                        sourceMatches ? null : "SOURCE_SET_MISMATCH");
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.DirectProjection,
                async (boundary, ct) =>
                {
                    var source = sourceCapture ??
                                 throw Fail("SOURCE_CAPTURE_NOT_AVAILABLE");
                    directCapture = await _direct.CaptureAsync(
                            request.Direct,
                            source,
                            directOwner,
                            ct)
                        .ConfigureAwait(false);
                    var ownerId = JoinOwner(
                        directCapture.Boundary.RunId,
                        directCapture.Boundary.GenerationId);
                    var ownerVersion = directCapture.CaptureSemanticSha256;
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.DirectProjection,
                        boundary,
                        directCapture.CaptureSemanticSha256,
                        directCapture.TotalRowCount,
                        ownerId,
                        ownerVersion,
                        RequireTyped(ProjectV4DirectFields(
                            request,
                            typedMapper.MapDirect(
                                directCapture,
                                ownerId,
                                ownerVersion))));
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.Aggregate,
                async (boundary, ct) =>
                {
                    var source = sourceCapture ??
                                 throw Fail("SOURCE_CAPTURE_NOT_AVAILABLE");
                    var direct = directCapture ??
                                 throw Fail("DIRECT_CAPTURE_NOT_AVAILABLE");
                    aggregateCapture = await _aggregate.CaptureAsync(
                            request.Aggregate,
                            source,
                            direct,
                            aggregateOwner,
                            ct)
                        .ConfigureAwait(false);
                    // Persist the exact P9 result-generation relation used by
                    // trusted aggregate reconciliation. The publication scope
                    // remains part of the owner identity; the Direct owner is
                    // appended so a verdict never infers it from opaque bytes.
                    var ownerId = JoinOwner(
                        aggregateCapture.Boundary.PublicationScopeKey,
                        JoinOwner(
                            direct.Boundary.RunId,
                            direct.Boundary.GenerationId));
                    var ownerVersion = aggregateCapture.CaptureSemanticSha256;
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.Aggregate,
                        boundary,
                        aggregateCapture.CaptureSemanticSha256,
                        aggregateCapture.TotalRowCount,
                        ownerId,
                        ownerVersion,
                        RequireTyped(ProjectV4DirectFields(
                            request,
                            typedMapper.MapAggregate(
                                aggregateCapture,
                                request.Boundary.ScopeAssignmentId,
                                ownerId,
                                ownerVersion))));
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.Basic,
                async (boundary, ct) =>
                {
                    if (request.Basic is null)
                        return NotApplicableLayer(
                            StatisticReconciliationActualCoherentLayers.Basic,
                            StatisticReconciliationActualCapturePlanIntegrity.BasicFamily,
                            boundary,
                            RequireCapturePlan(request));
                    basicCapture = await CaptureBasicAsync(request.Basic, ct)
                        .ConfigureAwait(false);
                    var ownerId = basicCapture.Boundary.OwnerSnapshotId;
                    var ownerVersion = basicCapture.CaptureSemanticSha256;
                    var count = basicCapture.ResultItems.Length;
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.Basic,
                        boundary,
                        ownerVersion,
                        count,
                        ownerId,
                        ownerVersion,
                        RequireTyped(typedMapper.MapBasic(
                            basicCapture,
                            ownerId,
                            ownerVersion)));
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.Advanced,
                async (boundary, ct) =>
                {
                    if (request.Advanced is null)
                        return NotApplicableLayer(
                            StatisticReconciliationActualCoherentLayers.Advanced,
                            StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily,
                            boundary,
                            RequireCapturePlan(request));
                    advancedCapture = await _advanced.CaptureAsync(
                            request.Advanced,
                            advancedOwner,
                            ct)
                        .ConfigureAwait(false);
                    var ownerId = JoinOwner(
                        advancedCapture.Boundary.AssignmentId,
                        advancedCapture.Boundary.SectionId);
                    var ownerVersion = advancedCapture.CaptureSemanticSha256;
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.Advanced,
                        boundary,
                        advancedCapture.CaptureSemanticSha256,
                        advancedCapture.TotalNodeCount,
                        ownerId,
                        ownerVersion,
                        RequireTyped(typedMapper.MapAdvanced(
                            advancedCapture,
                            ownerId,
                            ownerVersion)));
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.Diff,
                async (boundary, ct) =>
                {
                    if (request.Diff is null)
                        return NotApplicableLayer(
                            StatisticReconciliationActualCoherentLayers.Diff,
                            StatisticReconciliationActualCapturePlanIntegrity.DiffFamily,
                            boundary,
                            RequireCapturePlan(request));
                    diffCapture = await _diff.CaptureAsync(
                            request.Diff,
                            diffOwner,
                            ct)
                        .ConfigureAwait(false);
                    var ownerId = diffCapture.Boundary.ResultId;
                    var ownerVersion = diffCapture.CaptureSemanticSha256;
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.Diff,
                        boundary,
                        diffCapture.CaptureSemanticSha256,
                        diffCapture.Rows.Length,
                        ownerId,
                        ownerVersion,
                        RequireTyped(typedMapper.MapDiff(
                            diffCapture,
                            ownerId,
                            ownerVersion)));
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.Api,
                async (boundary, ct) =>
                {
                    apiCapture = await _api.CaptureAsync(request.Api, ct)
                        .ConfigureAwait(false);
                    var ownerId = JoinOwner(
                        apiCapture.RouteId,
                        apiCapture.OwnerResultId ?? apiCapture.Surface);
                    var ownerVersion = apiCapture.CaptureSemanticSha256;
                    // API authorization is the current API/service actor
                    // snapshot. The coherent boundary carries the persisted
                    // reconciliation-run authorization owner. They are
                    // deliberately independent domains and both remain bound
                    // by their respective semantic hashes.
                    var ready = apiCapture.CaptureState ==
                                StatisticReconciliationActualApiCaptureStates.Ready;
                    var reason = ready
                        ? null
                        : $"API_{apiCapture.CaptureState}:{apiCapture.CaptureReason}";
                    var count = apiCapture.Pages.Sum(page =>
                        (long)page.Rows.Length + page.FullFilterTotals.Length);
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.Api,
                        boundary,
                        apiCapture.CaptureSemanticSha256,
                        count,
                        ownerId,
                        ownerVersion,
                        ready
                            ? RequireTyped(ProjectV4DirectFields(
                                request,
                                typedMapper.MapApi(
                                    apiCapture,
                                    ownerId,
                                    ownerVersion)))
                            : [],
                        reason);
                }),
            new StatisticReconciliationActualCoherentCaptureStep(
                StatisticReconciliationActualCoherentLayers.Export,
                async (boundary, ct) =>
                {
                    var ownerRead = await exportOwner.ReadArtifactAsync(
                            request.Export,
                            ct)
                        .ConfigureAwait(false);
                    if (ownerRead.State !=
                            StatisticReconciliationActualExportOwnerReadStates.Ready ||
                        ownerRead.Artifact is null)
                    {
                        var staleReason = ownerRead.Reason ??
                                          "EXPORT_OWNER_READ_INVALID";
                        var staleSemantic =
                            StatisticReconciliationActualCanonical.Hash(
                                "P10_ACTUAL_EXPORT_OWNER_STALE_V1",
                                request.Export.ExportId,
                                request.Export.ExpectedOwnerSemanticSha256,
                                staleReason);
                        return Layer(
                            StatisticReconciliationActualCoherentLayers.Export,
                            boundary,
                            staleSemantic,
                            0,
                            request.Export.ExportId,
                            request.Export.ExpectedOwnerSemanticSha256,
                            [],
                            $"EXPORT_OWNER_{staleReason}");
                    }

                    try
                    {
                        exportCapture = _export.Parse(ownerRead.Artifact);
                    }
                    catch (StatisticReconciliationActualObservationException)
                    {
                        var staleSemantic =
                            StatisticReconciliationActualCanonical.Hash(
                                "P10_ACTUAL_EXPORT_OWNER_STALE_V1",
                                request.Export.ExportId,
                                request.Export.ExpectedOwnerSemanticSha256,
                                "EXPORT_T23_PARSE_FAILED");
                        return Layer(
                            StatisticReconciliationActualCoherentLayers.Export,
                            boundary,
                            staleSemantic,
                            0,
                            request.Export.ExportId,
                            request.Export.ExpectedOwnerSemanticSha256,
                            [],
                            "EXPORT_OWNER_EXPORT_T23_PARSE_FAILED");
                    }
                    var ownerId = exportCapture.ExportId;
                    var ownerVersion = exportCapture.CaptureSemanticSha256;
                    var matches = ExportMatchesBoundary(
                        request,
                        boundary,
                        exportCapture,
                        ownerRead.Artifact);
                    var count = exportCapture.Rows.Length +
                                exportCapture.FullFilterTotals.Length;
                    return Layer(
                        StatisticReconciliationActualCoherentLayers.Export,
                        boundary,
                        exportCapture.CaptureSemanticSha256,
                        count,
                        ownerId,
                        ownerVersion,
                        matches
                            ? RequireTyped(ProjectV4DirectFields(
                                request,
                                typedMapper.MapExport(
                                    exportCapture,
                                    ownerId,
                                    ownerVersion)))
                            : [],
                        matches ? null : "EXPORT_BOUNDARY_MISMATCH");
                }));

        async Task<string?> FinalOwnerGuardAsync(CancellationToken ct)
        {
            try
            {
                if (sourceCapture is null)
                    return "SOURCE_OWNER_DRIFT";
                var finalSource = await _source.CaptureAsync(
                        request.Source,
                        sourceOwner,
                        ct)
                    .ConfigureAwait(false);
                if (!Eq(
                        finalSource.CaptureSemanticSha256,
                        sourceCapture.CaptureSemanticSha256))
                    return "SOURCE_OWNER_DRIFT";
            }
            catch (StatisticReconciliationActualObservationException)
            {
                return "SOURCE_OWNER_DRIFT";
            }

            try
            {
                if (apiCapture is null)
                    return "API_OWNER_DRIFT";
                var finalApi = await _api.CaptureAsync(request.Api, ct)
                    .ConfigureAwait(false);
                if (finalApi.CaptureState !=
                        StatisticReconciliationActualApiCaptureStates.Ready ||
                    !Eq(
                        finalApi.CaptureSemanticSha256,
                        apiCapture.CaptureSemanticSha256))
                    return "API_OWNER_DRIFT";
            }
            catch (Exception exception) when (
                exception is StatisticReconciliationActualObservationException
                    or StatisticReconciliationActualApiEndpointException)
            {
                return "API_OWNER_DRIFT";
            }

            try
            {
                var finalRead = await exportOwner.ReadArtifactAsync(
                        request.Export,
                        ct)
                    .ConfigureAwait(false);
                return finalRead.State ==
                       StatisticReconciliationActualExportOwnerReadStates.Ready &&
                       finalRead.Artifact is not null
                    ? null
                    : "EXPORT_ARTIFACT_DRIFT";
            }
            catch (StatisticReconciliationActualObservationException)
            {
                return "EXPORT_ARTIFACT_DRIFT";
            }
        }

        async Task<StatisticReconciliationActualPublicationContext>
            PreparePublicationAsync(
                StatisticReconciliationActualCoherentGeneration generation,
                StatisticReconciliationActualPublicationContext context,
                CancellationToken ct)
        {
            var source = sourceCapture ??
                throw Fail("LIFECYCLE_SOURCE_CAPTURE_NOT_AVAILABLE");
            var direct = directCapture ??
                throw Fail("LIFECYCLE_DIRECT_CAPTURE_NOT_AVAILABLE");
            var aggregate = aggregateCapture ??
                throw Fail("RELATIONAL_AGGREGATE_CAPTURE_NOT_AVAILABLE");
            var basic = basicCapture;
            var advanced = advancedCapture;
            var diff = diffCapture;
            var api = apiCapture ??
                throw Fail("RELATIONAL_API_CAPTURE_NOT_AVAILABLE");
            var export = exportCapture ??
                throw Fail("RELATIONAL_EXPORT_CAPTURE_NOT_AVAILABLE");
            var auditScope = new StatRunDirectSourceOwnerScope(
                source.OwnerRunId,
                source.OwnerGenerationId,
                source.OwnerGenerationSha256,
                source.WorkId,
                source.PeriodInstanceKey,
                source.DynamicFormTemplateId,
                source.OwnerMembershipSignature,
                source.OwnerDirectSourceRevision,
                source.LifecycleMetricScope);
            var audit = await lifecycleAuditOwner.ReadLifecycleAuditAsync(
                    auditScope,
                    ct)
                .ConfigureAwait(false);
            var trustedRun = request.TrustedRun ??
                throw Fail("LIFECYCLE_TRUSTED_RUN_NOT_AVAILABLE");
            var expectedBinding = request.ExpectedLifecycleBinding ??
                throw Fail("LIFECYCLE_EXPECTED_BINDING_NOT_AVAILABLE");
            var expected = await expectedLifecycleReader.ResolveAsync(
                    trustedRun,
                    expectedBinding,
                    ct)
                .ConfigureAwait(false);
            var comparisonBinding = BuildComparisonBinding(
                source,
                generation,
                context);
            var evidence = lifecycleBridge.Build(
                new StatisticReconciliationActualLifecycleBuildInput(
                    request.ReconciliationId,
                    generation.GenerationId,
                    generation.GenerationSemanticSha256,
                    comparisonBinding,
                    expected,
                    source,
                    direct,
                    audit,
                    request.LifecyclePrior));
            var append = await lifecycleLedger.AppendAsync(evidence, ct)
                .ConfigureAwait(false);
            if (!string.Equals(
                    append.ManifestSha256,
                    evidence.ManifestSha256,
                    StringComparison.Ordinal) ||
                append.ObservationCount != evidence.ObservationCount)
            {
                throw Fail("LIFECYCLE_APPEND_BINDING_MISMATCH");
            }
            var publishedSourceDecisions = source.Decisions
                .Select((value, index) =>
                    StatisticReconciliationActualPublishedSourceDecision.Create(
                        index,
                        value))
                .ToImmutableArray();
            var sourceDecisionManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_PUBLICATION_SOURCE_DECISION_MANIFEST_V1",
                    publishedSourceDecisions.Select(value =>
                        value.SemanticSha256));
            var membershipSemantic =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_INCLUDED_SOURCE_MEMBERSHIP_V1",
                    publishedSourceDecisions
                        .Where(value => value.Included)
                        .Select(value => value.NeutralStableIdentitySha256)
                        .OrderBy(value => value, StringComparer.Ordinal));
            if (!StringComparer.Ordinal.Equals(
                    membershipSemantic,
                    source.MembershipSemanticSha256))
                throw Fail("SOURCE_MEMBERSHIP_SEMANTIC_MISMATCH");
            var trustedMaterial = request.TrustedMaterial ??
                throw Fail("RELATIONAL_TRUSTED_MATERIAL_NOT_AVAILABLE");
            var summaryPlan = context.SummaryPlanBinding ??
                throw Fail("RELATIONAL_SUMMARY_PLAN_NOT_AVAILABLE");
            var extended = await extendedRawOwner.ResolveAsync(new(
                    StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                    trustedMaterial, summaryPlan, advanced, diff), ct)
                .ConfigureAwait(false);
            if (extended.State !=
                    StatisticReconciliationActualExtendedRawSourceSchemas.Complete ||
                extended.FailureCode !=
                    StatisticReconciliationActualExtendedRawSourceFailures.None ||
                !extended.RequiredPersistenceFields.IsDefaultOrEmpty)
                throw Fail(
                    $"RELATIONAL_EXTENDED_RAW_SOURCE_INCOMPLETE:{extended.FailureCode}");
            StatisticReconciliationActualExtendedRawSourceIntegrity
                .RequireResolution(extended);
            var extendedParity = extendedOwnerParity.Prove(new(
                summaryPlan, extended, advanced, diff));
            if (extendedParity.State !=
                    StatisticReconciliationActualExtendedRawSourceSchemas.Complete ||
                extendedParity.FailureCode !=
                    StatisticReconciliationActualExtendedRawSourceFailures.None ||
                !extendedParity.RequiredPersistenceFields.IsDefaultOrEmpty)
                throw Fail("RELATIONAL_EXTENDED_OWNER_PARITY_INCOMPLETE");
            var relational = await relationalProofOwner.ResolveAsync(new(
                    StatisticReconciliationActualRelationalProofSchemas.Command,
                    summaryPlan, trustedMaterial, source, direct, aggregate,
                    basic, advanced, diff, extended, extendedParity, api, export),
                    ct)
                .ConfigureAwait(false);
            if (relational.State !=
                    StatisticReconciliationActualRelationalProofStates.Complete ||
                relational.FailureCode != "NONE" ||
                !relational.RequiredPersistenceFields.IsDefaultOrEmpty ||
                relational.Binding is null)
                throw Fail($"RELATIONAL_PROOF_INCOMPLETE:{relational.FailureCode}");
            var relationalBinding =
                StatisticReconciliationActualRelationalProofBinding.Normalize(
                    relational.Binding);
            return context with
            {
                LifecycleManifestSha256 = append.ManifestSha256,
                LifecycleObservationCount = append.ObservationCount,
                ActualMembershipSemanticSha256 = membershipSemantic,
                ActualSourceDecisionManifestSha256 = sourceDecisionManifest,
                ActualSourceDecisions = publishedSourceDecisions,
                LifecycleMetricScopeSha256 =
                    StatisticReconciliationActualCanonical.Sha256(
                        source.LifecycleMetricScopeSha256,
                        "LIFECYCLE_METRIC_SCOPE_SHA256"),
                RelationalProofBinding = relationalBinding
            };
        }

        return await new StatisticReconciliationActualCapturePublicationGate(
                publisher)
            .ExecuteAsync(
                request.ReconciliationId,
                boundaryReader,
                steps,
                request.Publication,
                request.CreatedAtUtc,
                request.WorkerId,
                request.ClaimToken,
                request.Actor,
                cancellationToken,
                request.PinnedInitialBoundary,
                FinalOwnerGuardAsync,
                PreparePublicationAsync)
            .ConfigureAwait(false);
    }

    private static string BuildComparisonBinding(
        ActualSourceMembershipCapture source,
        StatisticReconciliationActualCoherentGeneration generation,
        StatisticReconciliationActualPublicationContext context)
    {
        _ = generation;
        return StatisticReconciliationFinalVerdictEvaluator.HashFields(
            "P10_COMPARISON_BINDING_PINS_V1",
            source.SourceSetSha256,
            context.P8ConfigurationBundleSha256,
            context.ActualConfigurationBundleSha256,
            context.CatalogPins.CatalogPinSetSha256,
            generation.CommonBoundary.RuntimePinSetSha256);
    }
    private async Task<ActualBasicResultObservation> CaptureBasicAsync(
        StatisticReconciliationActualBasicCaptureTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return target.Mode switch
        {
            StatisticReconciliationActualBasicModes.DirectChildrenOrSelf =>
                await _basic.CaptureDirectChildrenOrSelfAsync(
                        target.Boundary,
                        basicOwner,
                        cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.DirectChildren =>
                await _basic.CaptureDirectChildrenAsync(
                        target.Boundary,
                        basicOwner,
                        cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowBranch =>
                await _basic.CaptureFlowBranchAsync(
                        target.Boundary,
                        basicOwner,
                        cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowStep =>
                await _basic.CaptureFlowStepAsync(
                        target.Boundary,
                        basicOwner,
                        cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowEffectivePath =>
                await _basic.CaptureFlowEffectivePathAsync(
                        target.Boundary,
                        basicOwner,
                        cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowFinal =>
                await _basic.CaptureFlowFinalAsync(
                        target.Boundary,
                        basicOwner,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw Fail("BASIC_MODE_INVALID")
        };
    }

    private static StatisticReconciliationActualCapturePlan RequireCapturePlan(
        StatisticReconciliationActualCaptureRequest request)
        => request.TrustedRun?.ActualCapturePlan ??
           throw Fail("TRUSTED_CAPTURE_PLAN_NOT_AVAILABLE");

    private static ImmutableArray<StatisticReconciliationActualTypedObservation>
        ProjectV4DirectFields(
            StatisticReconciliationActualCaptureRequest request,
            ImmutableArray<StatisticReconciliationActualTypedObservation> values)
    {
        var plan = RequireCapturePlan(request);
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan))
            return values;
        var summaryPlan = request.Publication.SummaryPlanBinding ??
            throw Fail("V4_SUMMARY_PLAN_NOT_AVAILABLE");
        return StatisticReconciliationActualAdapterTypedMapper
            .ProjectPlanBoundDirectFields(summaryPlan, values);
    }

    private static StatisticReconciliationActualLayerCapture NotApplicableLayer(
        string layer,
        string family,
        StatisticReconciliationActualCoherentBoundary boundary,
        StatisticReconciliationActualCapturePlan plan)
    {
        var disposition = family switch
        {
            StatisticReconciliationActualCapturePlanIntegrity.BasicFamily =>
                plan.Basic.Disposition,
            StatisticReconciliationActualCapturePlanIntegrity.AdvancedFamily =>
                plan.Advanced.Disposition,
            StatisticReconciliationActualCapturePlanIntegrity.DiffFamily =>
                plan.Diff.Disposition,
            _ => throw Fail("SUMMARY_NOT_APPLICABLE_FAMILY_INVALID")
        };
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                disposition))
            throw Fail("SUMMARY_NOT_APPLICABLE_PROOF_REQUIRED");
        var ownerId = StatisticReconciliationActualCapturePlanIntegrity
            .NotApplicableOwnerId(plan, family);
        var captureSha = StatisticReconciliationActualCapturePlanIntegrity
            .NotApplicableCaptureSha(plan, family);
        return Layer(layer, boundary, captureSha, 0, ownerId, captureSha, []);
    }
    private static StatisticReconciliationActualLayerCapture Layer(
        string layer,
        StatisticReconciliationActualCoherentBoundary boundary,
        string captureSemanticSha256,
        long observationCount,
        string ownerId,
        string ownerVersionSha256,
        ImmutableArray<StatisticReconciliationActualTypedObservation> typed,
        string? staleReason = null)
        => new(
            layer,
            boundary.BoundarySemanticSha256,
            captureSemanticSha256,
            observationCount,
            staleReason is null
                ? StatisticReconciliationActualCoherentCaptureStates.Ready
                : StatisticReconciliationActualCoherentCaptureStates.Stale,
            staleReason,
            ownerId,
            ownerVersionSha256,
            typed);

    private static ImmutableArray<StatisticReconciliationActualTypedObservation>
        RequireTyped(
            ImmutableArray<StatisticReconciliationActualTypedObservation> value)
    {
        if (value.IsDefault)
            throw Fail("TYPED_MAPPER_DEFAULT_RESULT");
        return value;
    }

    private static bool ExportMatchesBoundary(
        StatisticReconciliationActualCaptureRequest request,
        StatisticReconciliationActualCoherentBoundary boundary,
        StatisticReconciliationActualExportCapture capture,
        StatisticReconciliationActualExportArtifact artifact)
        => Eq(
               capture.SourceSha256,
               request.Export.ExpectedSourceOwnerSha256) &&
           Eq(
               capture.ConfigSha256,
               request.Export.ExpectedConfigOwnerSha256) &&
           Eq(
               capture.FilterSha256,
               request.Export.ExpectedFilterSha256) &&
           Eq(
               capture.PeriodInstanceKey,
               request.Export.ExpectedPeriodInstanceKey) &&
           Eq(
               artifact.Manifest.PeriodInstanceKey,
               request.Export.ExpectedPeriodInstanceKey) &&
           Eq(artifact.Manifest.WorkId, boundary.WorkId) &&
           Eq(artifact.Manifest.ScopeId, boundary.ScopeAssignmentId);

    private static void ValidateRequest(
        StatisticReconciliationActualCaptureRequest request)
    {
        if (request.Boundary is null || request.Source is null ||
            request.Direct is null || request.Aggregate is null ||
            request.Api is null || request.Export is null ||
            request.Publication is null ||
            request.PinnedInitialBoundary is null || request.Actor is null ||
            request.TrustedRun is null || request.TrustedMaterial is null ||
            request.ExpectedLifecycleBinding is null)
            throw Fail("REQUEST_SHAPE_INVALID");
        var plan = request.TrustedRun.ActualCapturePlan ??
            throw Fail("TRUSTED_CAPTURE_PLAN_NOT_AVAILABLE");
        try
        {
            StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
                plan,
                request.TrustedRun.ActualCapturePlanSha256);
        }
        catch (InvalidOperationException)
        {
            throw Fail("TRUSTED_CAPTURE_PLAN_INVALID");
        }
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
        if ((request.Basic is null) != basicNotApplicable ||
            (request.Advanced is null) != advancedNotApplicable ||
            (request.Diff is null) != diffNotApplicable)
            throw Fail("SUMMARY_TARGET_APPLICABILITY_INVALID");

        if (!Eq(request.PinnedInitialBoundary.ReconciliationId,
                request.Boundary.ReconciliationId) ||
            !Eq(request.PinnedInitialBoundary.WorkId, request.Boundary.WorkId) ||
            !Eq(request.PinnedInitialBoundary.ScopeAssignmentId,
                request.Boundary.ScopeAssignmentId) ||
            !Eq(request.PinnedInitialBoundary.SourceSetSha256,
                request.Boundary.SourceSetSha256) ||
            !Eq(request.PinnedInitialBoundary.ConfigurationBundleSha256,
                request.Boundary.ConfigurationBundleSha256) ||
            !Eq(request.PinnedInitialBoundary.FilterSha256,
                request.Boundary.FilterSha256) ||
            !Eq(request.PinnedInitialBoundary.AuthorizationSnapshotSha256,
                request.Boundary.AuthorizationSnapshotSha256))
            throw Fail("PINNED_BOUNDARY_MISMATCH");
        var expectedPageCount = Math.Max(
            1,
            checked((int)((request.Api.ExpectedTotalRows + 199L) / 200L)));
        if (request.Api.ExpectedTotalRows is < 0 or > 6_400 ||
            request.Api.Pages.IsDefault ||
            request.Api.Pages.Length != expectedPageCount ||
            request.Api.Pages.Where((page, index) =>
                    page.Page != index || page.PageSize != 200)
                .Any())
        {
            throw Fail("API_PAGE_PLAN_INVALID");
        }
        if (!Eq(request.TrustedRun.Id, request.ReconciliationId) ||
            !Eq(request.ExpectedLifecycleBinding.ReconciliationId,
                request.ReconciliationId))
            throw Fail("LIFECYCLE_EXPECTED_RUN_BINDING_MISMATCH");
        if (!Eq(request.ReconciliationId, request.Boundary.ReconciliationId) ||
            !Eq(request.ReconciliationId, request.Publication.ReconciliationId) ||
            !Eq(request.Boundary.WorkId, request.Source.WorkId) ||
            !Eq(request.Boundary.WorkId, request.Direct.WorkId) ||
            !Eq(request.Boundary.WorkId, request.Aggregate.Direct.WorkId) ||
            request.Advanced is not null &&
                !Eq(request.Boundary.WorkId, request.Advanced.WorkId) ||
            request.Diff is not null &&
                !Eq(request.Boundary.WorkId, request.Diff.WorkId) ||
            !Eq(request.Boundary.WorkId, request.Api.WorkId) ||
            !Eq(request.Boundary.WorkId, request.Export.WorkId) ||
            !Eq(request.Boundary.ScopeAssignmentId, request.Api.ScopeAssignmentId) ||
            !Eq(request.Boundary.ScopeAssignmentId, request.Export.ScopeId) ||
            !Eq(request.Boundary.ScopeAssignmentId,
                request.Publication.ScopeAssignmentId) ||
            request.Advanced is not null &&
                !Eq(request.Boundary.ScopeAssignmentId,
                    request.Advanced.AssignmentId) ||
            request.Diff is not null &&
                !Eq(request.Boundary.ScopeAssignmentId,
                    request.Diff.AssignmentId))
            throw Fail("REQUEST_BOUNDARY_MISMATCH");
        if (request.Basic is not null &&
            (request.Basic.Boundary is null ||
             !StatisticReconciliationActualBasicModes.ExactSet.Contains(
                 request.Basic.Mode,
                 StringComparer.Ordinal) ||
             !Eq(request.Basic.Mode, request.Basic.Boundary.SourceScopeMode) ||
             !Eq(request.Basic.Boundary.WorkId, request.Boundary.WorkId) ||
             !Eq(request.Basic.Boundary.ScopeAssignmentId,
                 request.Boundary.ScopeAssignmentId)))
            throw Fail("BASIC_TARGET_INVALID");
        if (request.CreatedAtUtc.Kind != DateTimeKind.Utc)
            throw Fail("REQUEST_SHAPE_INVALID");
    }

    private static string JoinOwner(string left, string right)
        => $"{left}:{right}";

    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_CAPTURE_SERVICE_{reason}");
}
