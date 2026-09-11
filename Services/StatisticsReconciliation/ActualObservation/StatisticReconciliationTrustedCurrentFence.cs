using System.Globalization;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationTrustedCurrentOwnerRead(
    StatisticReconciliationActualFreshnessPins CurrentOwners,
    string FenceSha256,
    bool EvidenceComplete,
    bool CaptureCoherent,
    string State,
    string RelationalProofBindingSha256,
    StatisticReconciliationTrustedP8ConfigurationIdentity?
        CurrentP8Configuration = null);

internal interface IStatisticReconciliationTrustedCurrentOwnerReader
{
    Task<StatisticReconciliationTrustedCurrentOwnerRead> ReadAsync(
        StatisticReconciliationRun persistedRun,
        StatisticReconciliationActualAppendResult actual,
        MeResponse systemActor,
        CancellationToken cancellationToken);

    Task<StatisticReconciliationTrustedCurrentOwnerRead> ReadCurrentAsync(
        StatisticReconciliationRun persistedRun,
        StatisticReconciliationActualAppendResult actual,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reconstructs authoritative current owner state after pending publication.
/// Every selector comes from the persisted run/plan or an authoritative
/// current-publication query. Recovery invokes this same owner; no transient
/// command field or client semantic evidence is accepted.
/// </summary>
internal sealed class StatisticReconciliationTrustedCurrentOwnerReader(
    MongoDbContext context,
    IStatisticReconciliationActualPendingVerdictMaterialOwner materialOwner,
    IStatisticReconciliationActualBoundaryRegistry boundaryRegistry,
    IStatisticReconciliationActualSourceOwnerReader sourceOwner,
    IStatisticReconciliationActualCoherentBoundaryReaderFactory boundaryReaders,
    IStatisticReconciliationActualDirectProjectionOwnerReader directOwner,
    IStatisticReconciliationActualAggregateOwnerReader aggregateOwner,
    IStatisticReconciliationActualBasicOwnerReader basicOwner,
    IStatisticReconciliationActualAdvancedOwnerReader advancedOwner,
    IStatisticReconciliationActualP9DiffOwnerReader diffOwner,
    IStatisticReconciliationActualApiOwnerReader apiOwner,
    IStatisticReconciliationActualExportOwnerReader exportOwner,
    IStatisticReconciliationActualExtendedRawSourceOwner extendedRawOwner,
    IStatisticReconciliationActualExtendedRawSourceOwnerParity
        extendedOwnerParity,
    IStatisticReconciliationActualRelationalProofOwner relationalProofOwner,
    ILogger<StatisticReconciliationTrustedCurrentOwnerReader> logger)
    : IStatisticReconciliationTrustedCurrentOwnerReader
{
    public async Task<StatisticReconciliationTrustedCurrentOwnerRead> ReadAsync(
        StatisticReconciliationRun persistedRun,
        StatisticReconciliationActualAppendResult actual,
        MeResponse systemActor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(systemActor);
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(systemActor);
        return await ReadCoreAsync(
                persistedRun, actual, terminalCurrent: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StatisticReconciliationTrustedCurrentOwnerRead>
        ReadCurrentAsync(
            StatisticReconciliationRun persistedRun,
            StatisticReconciliationActualAppendResult actual,
            CancellationToken cancellationToken)
        => await ReadCoreAsync(
                persistedRun, actual, terminalCurrent: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<StatisticReconciliationTrustedCurrentOwnerRead>
        ReadCoreAsync(
            StatisticReconciliationRun persistedRun,
            StatisticReconciliationActualAppendResult actual,
            bool terminalCurrent,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persistedRun);
        ArgumentNullException.ThrowIfNull(actual);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            persistedRun);
        if (terminalCurrent &&
            (actual.GenerationId != persistedRun.CurrentGenerationId ||
             actual.GenerationSemanticSha256 !=
                persistedRun.CurrentGenerationHash))
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_COMMITTED_GENERATION_BINDING_INVALID");

        var effectiveRun =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(persistedRun);
        var captured = Captured(actual, effectiveRun);
        var committedRelational = RequireCommittedRelational(actual);
        var initialTopology = await ReadTopologyAsync(
                effectiveRun, cancellationToken)
            .ConfigureAwait(false);
        var topology = initialTopology.Sha256;
        if (!ExactTopology(initialTopology, effectiveRun))
        {
            return Drift(captured, actual, topology,
                "AUTHORITATIVE_P9_TOPOLOGY_DRIFT", complete: true);
        }

        var stage = "MATERIAL";
        try
        {
            stage = "P8_CONFIGURATION";
            var currentP8Configuration =
                await ReadCurrentP8ConfigurationAsync(
                        effectiveRun, cancellationToken)
                    .ConfigureAwait(false);
            var material = terminalCurrent
                ? await materialOwner.ResolveCurrentVerdictAsync(
                        persistedRun, cancellationToken)
                    .ConfigureAwait(false)
                : await materialOwner.ResolvePendingVerdictAsync(
                        persistedRun, cancellationToken)
                    .ConfigureAwait(false);
            stage = "SOURCE";
            var source = await new
                    StatisticReconciliationActualSourceMembershipAdapter()
                .CaptureAsync(material.Source, sourceOwner, cancellationToken)
                .ConfigureAwait(false);
            var sourceDecisionProof =
                StatisticReconciliationTrustedSourceDecisionFence.Current(source);
            var committedSourceDecisionProof =
                StatisticReconciliationTrustedSourceDecisionFence.Committed(actual);
            if (sourceDecisionProof != committedSourceDecisionProof)
            {
                return Drift(captured, actual,
                    StatisticReconciliationTrustedSourceDecisionFence
                        .DriftSha256(
                            topology,
                            committedSourceDecisionProof,
                            sourceDecisionProof),
                    "AUTHORITATIVE_SOURCE_DECISION_MANIFEST_DRIFT",
                    complete: true);
            }

            stage = "BOUNDARY";
            var descriptor = boundaryRegistry.Build(material, source);
            var boundaryReader = boundaryReaders.Create(descriptor) ??
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_BOUNDARY_READER_NULL");
            var boundary = await boundaryReader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            stage = "API";
            var api = await new
                    StatisticReconciliationActualApiObservationAdapter(apiOwner)
                .CaptureAsync(material.Api, cancellationToken)
                .ConfigureAwait(false);
            if (api.CaptureState !=
                StatisticReconciliationActualApiCaptureStates.Ready)
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_API_NOT_READY");

            stage = "EXPORT";
            var exportRead = await exportOwner.ReadArtifactAsync(
                    material.Export, cancellationToken)
                .ConfigureAwait(false);
            if (exportRead.State !=
                    StatisticReconciliationActualExportOwnerReadStates.Ready ||
                exportRead.Artifact is null)
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_EXPORT_NOT_READY");
            var export = new StatisticReconciliationActualExportParser()
                .Parse(exportRead.Artifact);

            stage = "RELATIONAL";
            var relational = await ResolveRelationalAsync(
                    material, actual, source, api, export, cancellationToken)
                .ConfigureAwait(false);
            if (!RelationalReplayExact(
                    committedRelational.SemanticSha256,
                    relational.SemanticSha256,
                    relational.SemanticSha256))
            {
                return Drift(captured, actual,
                    H("P10_TRUSTED_CURRENT_RELATIONAL_DRIFT_V1",
                        topology,
                        committedRelational.SemanticSha256,
                        relational.SemanticSha256),
                    "AUTHORITATIVE_RELATIONAL_PROOF_DRIFT",
                    complete: true);
            }

            stage = "MID_TOPOLOGY";
            var midTopology = await ReadTopologyAsync(
                    effectiveRun, cancellationToken)
                .ConfigureAwait(false);
            if (midTopology.Sha256 != topology ||
                !ExactTopology(midTopology, effectiveRun, source))
            {
                return Drift(captured, actual, midTopology.Sha256,
                    "AUTHORITATIVE_P9_MID_TOPOLOGY_DRIFT", complete: true);
            }

            // A full second collection closes mutations in source membership,
            // boundary/config/runtime owners, paged API output and export bytes
            // that do not themselves change the P9 job/work topology.
            stage = "FINAL_SOURCE";
            var finalSource = await new
                    StatisticReconciliationActualSourceMembershipAdapter()
                .CaptureAsync(material.Source, sourceOwner, cancellationToken)
                .ConfigureAwait(false);
            var finalSourceDecisionProof =
                StatisticReconciliationTrustedSourceDecisionFence.Current(
                    finalSource);

            stage = "FINAL_P8_CONFIGURATION";
            var finalCurrentP8Configuration =
                await ReadCurrentP8ConfigurationAsync(
                        effectiveRun, cancellationToken)
                    .ConfigureAwait(false);
            if (!SameCurrentP8Configuration(
                    currentP8Configuration,
                    finalCurrentP8Configuration))
            {
                return Drift(captured, actual,
                    H("P10_TRUSTED_CURRENT_P8_REPLAY_DRIFT_V1",
                        topology,
                        currentP8Configuration?.SemanticSha256 ?? "~",
                        finalCurrentP8Configuration?.SemanticSha256 ?? "~"),
                    "AUTHORITATIVE_P8_CONFIGURATION_REPLAY_DRIFT",
                    complete: false);
            }

            stage = "FINAL_BOUNDARY";
            var finalDescriptor = boundaryRegistry.Build(material, finalSource);
            var finalBoundaryReader = boundaryReaders.Create(finalDescriptor) ??
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_FINAL_BOUNDARY_READER_NULL");
            var finalBoundary = await finalBoundaryReader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            stage = "FINAL_API";
            var finalApi = await new
                    StatisticReconciliationActualApiObservationAdapter(apiOwner)
                .CaptureAsync(material.Api, cancellationToken)
                .ConfigureAwait(false);
            if (finalApi.CaptureState !=
                StatisticReconciliationActualApiCaptureStates.Ready)
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_FINAL_API_NOT_READY");

            stage = "FINAL_EXPORT";
            var finalExportRead = await exportOwner.ReadArtifactAsync(
                    material.Export, cancellationToken)
                .ConfigureAwait(false);
            if (finalExportRead.State !=
                    StatisticReconciliationActualExportOwnerReadStates.Ready ||
                finalExportRead.Artifact is null)
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_FINAL_EXPORT_NOT_READY");
            var finalExport = new StatisticReconciliationActualExportParser()
                .Parse(finalExportRead.Artifact);

            stage = "FINAL_RELATIONAL";
            var finalRelational = await ResolveRelationalAsync(
                    material, actual, finalSource, finalApi, finalExport,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!RelationalReplayExact(
                    committedRelational.SemanticSha256,
                    relational.SemanticSha256,
                    finalRelational.SemanticSha256))
            {
                return Drift(captured, actual,
                    H("P10_TRUSTED_CURRENT_RELATIONAL_REPLAY_DRIFT_V1",
                        topology,
                        committedRelational.SemanticSha256,
                        relational.SemanticSha256,
                        finalRelational.SemanticSha256),
                    "AUTHORITATIVE_RELATIONAL_PROOF_REPLAY_DRIFT",
                    complete: true);
            }

            if (!OwnerReplayExact(
                    source.CaptureSemanticSha256,
                    finalSource.CaptureSemanticSha256,
                    boundary.BoundarySemanticSha256,
                    finalBoundary.BoundarySemanticSha256,
                    api.CaptureSemanticSha256,
                    finalApi.CaptureSemanticSha256,
                    export.CaptureSemanticSha256,
                    finalExport.CaptureSemanticSha256))
            {
                var replayDrift = H("P10_TRUSTED_CURRENT_OWNER_REPLAY_DRIFT_V1",
                    topology,
                    source.CaptureSemanticSha256,
                    finalSource.CaptureSemanticSha256,
                    boundary.BoundarySemanticSha256,
                    finalBoundary.BoundarySemanticSha256,
                    api.CaptureSemanticSha256,
                    finalApi.CaptureSemanticSha256,
                    export.CaptureSemanticSha256,
                    finalExport.CaptureSemanticSha256);
                return Drift(captured, actual, replayDrift,
                    "AUTHORITATIVE_OWNER_REPLAY_DRIFT", complete: true);
            }
            if (finalSourceDecisionProof != sourceDecisionProof ||
                finalSourceDecisionProof != committedSourceDecisionProof)
            {
                return Drift(captured, actual,
                    StatisticReconciliationTrustedSourceDecisionFence
                        .DriftSha256(
                            topology,
                            committedSourceDecisionProof,
                            sourceDecisionProof,
                            finalSourceDecisionProof),
                    "AUTHORITATIVE_SOURCE_DECISION_REPLAY_DRIFT",
                    complete: true);
            }

            stage = "FINAL_TOPOLOGY";
            var finalTopology = await ReadTopologyAsync(
                    effectiveRun, cancellationToken)
                .ConfigureAwait(false);
            if (finalTopology.Sha256 != topology ||
                !ExactTopology(finalTopology, effectiveRun, finalSource))
            {
                return Drift(captured, actual, finalTopology.Sha256,
                    "AUTHORITATIVE_P9_FINAL_TOPOLOGY_DRIFT", complete: true);
            }
            stage = "BINDING";
            RequireBoundary(persistedRun, source, boundary);
            RequireExport(material.Export, export);
            var capturedLifecycleMetricScope = RequireSha(
                actual.CapturedLifecycleMetricScopeSha256,
                "CAPTURED_LIFECYCLE_METRIC_SCOPE_MISSING");
            var committedLifecycleMetricScope = RequireSha(
                actual.CommittedRunBinding.LifecycleMetricScopeSha256,
                "COMMITTED_LIFECYCLE_METRIC_SCOPE_MISSING");
            if (capturedLifecycleMetricScope !=
                committedLifecycleMetricScope)
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_COMMITTED_LIFECYCLE_METRIC_SCOPE_INVALID");
            var currentLifecycleMetricScope = RequireSha(
                source.LifecycleMetricScopeSha256,
                "CURRENT_LIFECYCLE_METRIC_SCOPE_MISSING");
            var layers = actual.Layers.OrderBy(value => value.Ordinal).ToArray();
            if (layers.Length != 8)
                throw new StatisticReconciliationActualObservationException(
                    "CURRENT_COMMITTED_LAYER_SET_INVALID");

            var runtime = boundary.RuntimePinSetSha256 ==
                          actual.CapturedRuntimePinSetSha256 &&
                          currentLifecycleMetricScope ==
                          capturedLifecycleMetricScope
                ? StatisticReconciliationTrustedVerdictDeriver
                    .RunRuntimeBindingSha256(effectiveRun)
                : H("P10_TRUSTED_CURRENT_RUNTIME_DRIFT_V2",
                    StatisticReconciliationTrustedVerdictDeriver
                        .RunRuntimeBindingSha256(effectiveRun),
                    actual.CapturedRuntimePinSetSha256,
                    boundary.RuntimePinSetSha256,
                    capturedLifecycleMetricScope,
                    currentLifecycleMetricScope);
            var resultOwner = api.CaptureSemanticSha256 ==
                              layers[6].CaptureSemanticSha256
                ? boundary.ResultPinSetSha256
                : H("P10_TRUSTED_CURRENT_RESULT_DRIFT_V1",
                    boundary.ResultPinSetSha256,
                    layers[6].CaptureSemanticSha256,
                    api.CaptureSemanticSha256);
            var exportOwnerSha = export.CaptureSemanticSha256 ==
                                 layers[7].CaptureSemanticSha256
                ? boundary.ExportPinSetSha256
                : H("P10_TRUSTED_CURRENT_EXPORT_DRIFT_V1",
                    boundary.ExportPinSetSha256,
                    layers[7].CaptureSemanticSha256,
                    export.CaptureSemanticSha256);
            var currentMembership = source.MembershipSemanticSha256 is
                    { Length: 64 }
                ? source.MembershipSemanticSha256
                : throw new StatisticReconciliationActualObservationException(
                    "CURRENT_NEUTRAL_MEMBERSHIP_MISSING");
            var currentCatalog =
                StatisticReconciliationTrustedCatalogComparison.FromCurrent(
                    actual.CommittedRunBinding.CatalogPins,
                    actual.CommittedRunBinding.CoherentCatalogPinSetSha256,
                    boundary.CatalogPinSetSha256);
            var current = new StatisticReconciliationActualFreshnessPins(
                new StatisticReconciliationComparisonBindingPins(
                    source.SourceSetSha256,
                    currentP8Configuration?.BundleSha256 ??
                        actual.CommittedRunBinding.P8ConfigurationBundleSha256,
                    boundary.ConfigurationBundleSha256,
                    currentCatalog,
                    runtime,
                    currentMembership),
                resultOwner,
                actual.GenerationSemanticSha256,
                exportOwnerSha,
                currentP8Configuration?.BundleSha256);
            var fence = H("P10_TRUSTED_CURRENT_OWNER_READ_V6",
                topology,
                boundary.BoundarySemanticSha256,
                source.SourceSetSha256,
                currentMembership,
                sourceDecisionProof.ManifestSha256,
                I(sourceDecisionProof.Count),
                capturedLifecycleMetricScope,
                currentLifecycleMetricScope,
                source.CaptureSemanticSha256,
                api.CaptureSemanticSha256,
                export.CaptureSemanticSha256,
                committedRelational.SemanticSha256,
                relational.SemanticSha256,
                finalRelational.SemanticSha256,
                Pins(current));
            return new StatisticReconciliationTrustedCurrentOwnerRead(
                current, fence, true, true, "AUTHORITATIVE_CURRENT_PROVEN",
                committedRelational.SemanticSha256,
                currentP8Configuration);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // The pending run must converge. An owner that cannot be
            // reconstructed is durable incomplete evidence, never captured
            // pins aliased as current and never a thrown post-CAS dead end.
            return Drift(captured, actual, topology,
                $"AUTHORITATIVE_CURRENT_{stage}_INCOMPLETE", complete: false,
                error);
        }
    }

    private StatisticReconciliationTrustedCurrentOwnerRead Drift(
        StatisticReconciliationActualFreshnessPins captured,
        StatisticReconciliationActualAppendResult actual,
        string topology,
        string state,
        bool complete,
        Exception? error = null)
    {
        logger.LogWarning(
            error,
            "P10 trusted current owner drift. State={State}; Complete={Complete}",
            state,
            complete);
        var relational = RequireCommittedRelational(actual);
        var drift = H("P10_TRUSTED_CURRENT_OWNER_UNAVAILABLE_V2",
            topology, state, actual.GenerationId,
            actual.GenerationSemanticSha256, relational.SemanticSha256);
        var current = captured with
        {
            Binding = captured.Binding with
            {
                SourceSetSha256 = drift,
                MembershipSemanticSha256 = drift
            }
        };
        return new StatisticReconciliationTrustedCurrentOwnerRead(
            current,
            H("P10_TRUSTED_CURRENT_OWNER_READ_V6",
                topology, state, relational.SemanticSha256, Pins(current)),
            complete,
            complete,
            state,
            relational.SemanticSha256,
            null);
    }

    private static StatisticReconciliationActualFreshnessPins Captured(
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationRun effectiveRun)
        => new(
            new StatisticReconciliationComparisonBindingPins(
                actual.CapturedSourceSetSha256,
                actual.CommittedRunBinding.P8ConfigurationBundleSha256,
                actual.CommittedRunBinding.ActualConfigurationBundleSha256,
                StatisticReconciliationTrustedCatalogComparison.FromActual(
                    actual.CommittedRunBinding.CatalogPins),
                StatisticReconciliationTrustedVerdictDeriver
                    .RunRuntimeBindingSha256(effectiveRun),
                RequireSha(
                    actual.CapturedMembershipSemanticSha256,
                    "CAPTURED_NEUTRAL_MEMBERSHIP_MISSING")),
            actual.CapturedResultPinSetSha256,
            actual.GenerationSemanticSha256,
            actual.CapturedExportPinSetSha256,
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(
                effectiveRun.ActualCapturePlan)
                ? actual.CommittedRunBinding.P8ConfigurationBundleSha256
                : null);

    private static StatisticReconciliationActualRelationalProofBinding
        RequireCommittedRelational(
            StatisticReconciliationActualAppendResult actual)
    {
        var outer = actual.RelationalProofBinding is null
            ? throw new StatisticReconciliationActualObservationException(
                "CURRENT_COMMITTED_RELATIONAL_PROOF_REQUIRED")
            : StatisticReconciliationActualRelationalProofBinding.Normalize(
                actual.RelationalProofBinding);
        var inner = actual.CommittedRunBinding.RelationalProofBinding is null
            ? throw new StatisticReconciliationActualObservationException(
                "CURRENT_RUN_RELATIONAL_PROOF_REQUIRED")
            : StatisticReconciliationActualRelationalProofBinding.Normalize(
                actual.CommittedRunBinding.RelationalProofBinding);
        var plan = actual.SummaryPlanBinding is null
            ? throw new StatisticReconciliationActualObservationException(
                "CURRENT_SUMMARY_PLAN_REQUIRED")
            : StatisticReconciliationActualSummaryPlanBinding.Normalize(
                actual.SummaryPlanBinding);
        var committedPlan = actual.CommittedRunBinding.SummaryPlanBinding is null
            ? throw new StatisticReconciliationActualObservationException(
                "CURRENT_RUN_SUMMARY_PLAN_REQUIRED")
            : StatisticReconciliationActualSummaryPlanBinding.Normalize(
                actual.CommittedRunBinding.SummaryPlanBinding);
        if (outer.SemanticSha256 != inner.SemanticSha256 ||
            plan.SemanticSha256 != committedPlan.SemanticSha256 ||
            outer.Facts.SummaryPlanBindingSha256 != plan.SemanticSha256)
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_COMMITTED_RELATIONAL_PROOF_INVALID");
        return outer;
    }

    private async Task<StatisticReconciliationActualRelationalProofBinding>
        ResolveRelationalAsync(
            StatisticReconciliationActualTrustedCaptureMaterial material,
            StatisticReconciliationActualAppendResult actual,
            ActualSourceMembershipCapture source,
            StatisticReconciliationActualApiCapture api,
            StatisticReconciliationActualExportCapture export,
            CancellationToken cancellationToken)
    {
        var summaryPlan = actual.SummaryPlanBinding is null
            ? throw new StatisticReconciliationActualObservationException(
                "CURRENT_SUMMARY_PLAN_REQUIRED")
            : StatisticReconciliationActualSummaryPlanBinding.Normalize(
                actual.SummaryPlanBinding);
        var direct = await new
                StatisticReconciliationActualDirectProjectionAdapter()
            .CaptureAsync(
                material.Direct, source, directOwner, cancellationToken)
            .ConfigureAwait(false);
        var aggregate = await new StatisticReconciliationActualAggregateAdapter()
            .CaptureAsync(material.Aggregate, source, direct, aggregateOwner,
                cancellationToken)
            .ConfigureAwait(false);
        var basic = material.Basic is null
            ? null
            : await CaptureBasicAsync(material.Basic, cancellationToken)
                .ConfigureAwait(false);
        var advanced = material.Advanced is null
            ? null
            : await new StatisticReconciliationActualAdvancedAdapter()
                .CaptureAsync(
                    material.Advanced,
                    advancedOwner,
                    cancellationToken)
                .ConfigureAwait(false);
        var diff = material.Diff is null
            ? null
            : await new StatisticReconciliationActualP9DiffAdapter()
                .CaptureAsync(material.Diff, diffOwner, cancellationToken)
                .ConfigureAwait(false);
        var extended = await extendedRawOwner.ResolveAsync(new(
                StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                material, summaryPlan, advanced, diff), cancellationToken)
            .ConfigureAwait(false);
        StatisticReconciliationActualExtendedRawSourceIntegrity
            .RequireResolution(extended);
        var extendedParity = extendedOwnerParity.Prove(new(
            summaryPlan, extended, advanced, diff));
        if (extendedParity.State !=
                StatisticReconciliationActualExtendedRawSourceSchemas.Complete ||
            extendedParity.FailureCode !=
                StatisticReconciliationActualExtendedRawSourceFailures.None ||
            !extendedParity.RequiredPersistenceFields.IsDefaultOrEmpty)
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_EXTENDED_OWNER_PARITY_INCOMPLETE");
        var relational = await relationalProofOwner.ResolveAsync(new(
                StatisticReconciliationActualRelationalProofSchemas.Command,
                summaryPlan, material, source, direct, aggregate, basic,
                advanced, diff, extended, extendedParity, api, export),
                cancellationToken)
            .ConfigureAwait(false);
        if (relational.State !=
                StatisticReconciliationActualRelationalProofStates.Complete ||
            relational.FailureCode != "NONE" ||
            !relational.RequiredPersistenceFields.IsDefaultOrEmpty ||
            relational.Binding is null)
            throw new StatisticReconciliationActualObservationException(
                $"CURRENT_RELATIONAL_PROOF_INCOMPLETE:{relational.FailureCode}");
        return StatisticReconciliationActualRelationalProofBinding.Normalize(
            relational.Binding);
    }

    private async Task<ActualBasicResultObservation> CaptureBasicAsync(
        StatisticReconciliationActualBasicCaptureTarget target,
        CancellationToken cancellationToken)
    {
        var adapter = new StatisticReconciliationActualBasicAdapter();
        return target.Mode switch
        {
            StatisticReconciliationActualBasicModes.DirectChildrenOrSelf =>
                await adapter.CaptureDirectChildrenOrSelfAsync(
                        target.Boundary, basicOwner, cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.DirectChildren =>
                await adapter.CaptureDirectChildrenAsync(
                        target.Boundary, basicOwner, cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowBranch =>
                await adapter.CaptureFlowBranchAsync(
                        target.Boundary, basicOwner, cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowStep =>
                await adapter.CaptureFlowStepAsync(
                        target.Boundary, basicOwner, cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowEffectivePath =>
                await adapter.CaptureFlowEffectivePathAsync(
                        target.Boundary, basicOwner, cancellationToken)
                    .ConfigureAwait(false),
            StatisticReconciliationActualBasicModes.FlowFinal =>
                await adapter.CaptureFlowFinalAsync(
                        target.Boundary, basicOwner, cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new StatisticReconciliationActualObservationException(
                "CURRENT_BASIC_MODE_INVALID")
        };
    }

    internal static bool RelationalReplayExact(
        string committed,
        string first,
        string final)
        => committed == first && first == final;
    private async Task<CurrentTopology> ReadTopologyAsync(
        StatisticReconciliationRun run,
        CancellationToken cancellationToken)
    {
        var candidates = await context.WorkReportStatisticRebuildJobs
            .Find(CurrentTopologyFilter(run))
            .SortBy(job => job.Id)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var workRevisions = await context.Works
            .Find(work => work.Id == run.WorkId && !work.IsDeleted)
            .Project(work => work.DirectSourceRevision)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var revision = workRevisions.Count == 1 ? workRevisions[0] : -1L;
        return new CurrentTopology(
            candidates, workRevisions.Count, revision,
            Topology(candidates, workRevisions.Count, revision));
    }

    internal static System.Linq.Expressions.Expression<
        Func<WorkReportStatisticRebuildJob, bool>> CurrentTopologyFilter(
            StatisticReconciliationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return job =>
            job.WorkId == run.WorkId &&
            job.RunKind ==
                WorkReportStatisticRebuildJobRunKinds
                    .LifecycleDirectProjection &&
            job.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
            job.IsCurrentPublication && !job.IsActive && !job.IsDeleted &&
            job.PeriodInstanceKey == run.PeriodInstanceKey &&
            job.PeriodKind == run.PeriodKind &&
            job.DynamicFormFamilyId == run.DynamicFormFamilyId &&
            job.DynamicFormTemplateId == run.DynamicFormVersionId &&
            job.DynamicFormVersionNo == run.DynamicFormVersionNo &&
            job.DynamicFormSchemaHash == run.DynamicFormSchemaHash &&
            job.CandidateChainId == run.P9CandidateChainId &&
            job.CandidatePromptId == run.P9CandidatePromptId;
    }

    internal static bool OwnerReplayExact(
        string firstSource,
        string finalSource,
        string firstBoundary,
        string finalBoundary,
        string firstApi,
        string finalApi,
        string firstExport,
        string finalExport)
        => firstSource == finalSource &&
           firstBoundary == finalBoundary &&
           firstApi == finalApi &&
           firstExport == finalExport;

    private static bool ExactTopology(
        CurrentTopology topology,
        StatisticReconciliationRun run,
        ActualSourceMembershipCapture? source = null)
    {
        if (topology.WorkCount != 1 ||
            topology.WorkDirectSourceRevision < 1 ||
            topology.Candidates.Count != 1)
            return false;
        var job = topology.Candidates[0];
        var exact = job.Id == run.P9RunId &&
            job.GenerationId == run.P9GenerationId &&
            job.GenerationHash == run.P9GenerationHash &&
            job.StateRevision > 0 && IsSha(job.StateHash) &&
            job.FreshnessState ==
                WorkReportStatisticRebuildJobFreshnessStates.Fresh &&
            job.DirectSourceRevision == topology.WorkDirectSourceRevision &&
            IsSha(job.SourceMembershipSignature);
        if (!exact || source is null)
            return exact;
        return job.Id == source.OwnerRunId &&
            job.GenerationId == source.OwnerGenerationId &&
            job.GenerationHash == source.OwnerGenerationSha256 &&
            job.SourceMembershipSignature ==
                source.OwnerMembershipSignature &&
            job.DirectSourceRevision == source.OwnerDirectSourceRevision;
    }

    private static string Topology(
        IReadOnlyList<WorkReportStatisticRebuildJob> candidates,
        int workCount,
        long workDirectSourceRevision)
        => H("P10_TRUSTED_CURRENT_P9_TOPOLOGY_V2",
            I(workCount), I(workDirectSourceRevision),
            H("P10_TRUSTED_CURRENT_P9_CANDIDATES_V2",
                candidates.OrderBy(value => value.Id, StringComparer.Ordinal)
                    .Select(value => H(
                        "P10_TRUSTED_CURRENT_P9_CANDIDATE_V2",
                        value.Id,
                        value.GenerationId ?? "~",
                        value.GenerationHash ?? "~",
                        I(value.StateRevision),
                        value.StateHash ?? "~",
                        value.FreshnessState ?? "~",
                        value.DirectSourceRevision.HasValue
                            ? I(value.DirectSourceRevision.Value)
                            : "~",
                        value.DirectPublicationRevision.HasValue
                            ? I(value.DirectPublicationRevision.Value)
                            : "~",
                        value.SourceMembershipSignature ?? "~",
                        value.SourceReportId ?? "~",
                        value.SourcePayloadRevision.HasValue
                            ? I(value.SourcePayloadRevision.Value)
                            : "~",
                        value.SourcePayloadHash ?? "~",
                        value.SourceLifecycleRevision.HasValue
                            ? I(value.SourceLifecycleRevision.Value)
                            : "~",
                        value.SourceLifecycleEventKey ?? "~",
                        value.PublicationScopeKey ?? "~",
                        value.Status,
                        value.IsCurrentPublication ? "1" : "0",
                        value.IsActive ? "1" : "0",
                        value.IsDeleted ? "1" : "0"))
                    .ToArray()));

    private static bool IsSha(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<StatisticReconciliationTrustedP8ConfigurationIdentity?>
        ReadCurrentP8ConfigurationAsync(
            StatisticReconciliationRun run,
            CancellationToken cancellationToken)
    {
        var plan = run.ActualCapturePlan;
        if (plan is null ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan))
            return null;

        if (string.IsNullOrWhiteSpace(run.P8ConfigOwnerId) ||
            !StringComparer.Ordinal.Equals(
                run.P8ConfigOwnerId, run.DynamicFormVersionId))
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_P8_CONFIGURATION_OWNER_BINDING_INVALID");

        var owners = await context.DynamicFormTemplates
            .Find(value =>
                value.Id == run.P8ConfigOwnerId &&
                value.IsActive &&
                value.IsPublished &&
                !value.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (owners.Count != 1)
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_P8_CONFIGURATION_OWNER_NOT_EXACT");

        var trusted = DynamicFormStatisticConfigCommandService
            .GetP804TrustedPersistedView(owners[0]);
        if (trusted is null ||
            !StringComparer.Ordinal.Equals(trusted.Status, "LOCKED"))
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_P8_CONFIGURATION_NOT_LOCKED");

        return StatisticReconciliationTrustedP8ConfigurationIdentityCanonical
            .Create(
                owners[0].Id,
                trusted.ConfigId,
                trusted.VersionId,
                trusted.VersionNo,
                trusted.Revision,
                trusted.ConfigHash);
    }

    private static bool SameCurrentP8Configuration(
        StatisticReconciliationTrustedP8ConfigurationIdentity? left,
        StatisticReconciliationTrustedP8ConfigurationIdentity? right)
        => left is null && right is null ||
           left is not null && right is not null &&
           StringComparer.Ordinal.Equals(
               left.SemanticSha256, right.SemanticSha256);

    private static void RequireBoundary(
        StatisticReconciliationRun run,
        ActualSourceMembershipCapture source,
        StatisticReconciliationActualCoherentBoundary boundary)
    {
        if (boundary.ReconciliationId != run.Id ||
            boundary.WorkId != run.WorkId ||
            boundary.ScopeAssignmentId != run.ScopeAssignmentId ||
            boundary.FilterSha256 != run.FilterHash ||
            boundary.AuthorizationSnapshotSha256 !=
                run.AuthorizationSnapshotHash ||
            boundary.SourceSetSha256 != source.SourceSetSha256)
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_BOUNDARY_BINDING_INVALID");
    }

    private static void RequireExport(
        StatisticReconciliationActualExportOwnerTarget target,
        StatisticReconciliationActualExportCapture export)
    {
        if (export.ExportId != target.ExportId ||
            export.ResultKind != target.ResultKind ||
            export.ResultId != target.ResultId ||
            export.SourceSha256 != target.ExpectedSourceOwnerSha256 ||
            export.ConfigSha256 != target.ExpectedConfigOwnerSha256 ||
            export.PeriodInstanceKey != target.ExpectedPeriodInstanceKey ||
            export.FilterSha256 != target.ExpectedFilterSha256 ||
            string.IsNullOrWhiteSpace(export.CanonicalFilterJson) ||
            export.ContentSha256 != target.ExpectedContentSha256 ||
            export.OwnerSemanticSha256 != target.ExpectedOwnerSemanticSha256)
            throw new StatisticReconciliationActualObservationException(
                "CURRENT_EXPORT_BINDING_INVALID");
    }

    private static string Pins(
        StatisticReconciliationActualFreshnessPins value)
        => value.ConfigurationPinSetSha256 is null
            ? H("P10_TRUSTED_CURRENT_PINS_V2",
                value.Binding.SourceSetSha256,
                value.Binding.MembershipSemanticSha256 ?? "~",
                value.Binding.P8ConfigurationBundleSha256,
                value.Binding.ActualConfigurationBundleSha256,
                value.Binding.CatalogPinSetSha256,
                value.Binding.RuntimePinSetSha256,
                value.ResultOwnerSha256,
                value.GenerationSemanticSha256,
                value.ExportOwnerSha256)
            : H("P10_TRUSTED_CURRENT_PINS_V3",
                value.Binding.SourceSetSha256,
                value.Binding.MembershipSemanticSha256 ?? "~",
                value.Binding.P8ConfigurationBundleSha256,
                value.Binding.ActualConfigurationBundleSha256,
                value.Binding.CatalogPinSetSha256,
                value.Binding.RuntimePinSetSha256,
                value.ConfigurationPinSetSha256,
                value.ResultOwnerSha256,
                value.GenerationSemanticSha256,
                value.ExportOwnerSha256);

    private static string RequireSha(string? value, string reason)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new StatisticReconciliationActualObservationException(reason);

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(
            domain, fields);

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private sealed record CurrentTopology(
        IReadOnlyList<WorkReportStatisticRebuildJob> Candidates,
        int WorkCount,
        long WorkDirectSourceRevision,
        string Sha256);
}
