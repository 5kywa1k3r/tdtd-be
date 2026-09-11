using System.Collections.Immutable;
using System.Text;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualPublicationRecordKinds
{
    internal const string Layer = StatisticReconciliationObservationRecordKinds.ActualLayer;
    internal const string Atom = StatisticReconciliationObservationRecordKinds.ActualAtom;
    internal const string SourceDecision =
        StatisticReconciliationObservationRecordKinds.ActualSourceDecision;
    internal const string Commit = StatisticReconciliationObservationRecordKinds.GenerationCommit;
}

internal static class StatisticReconciliationActualPublicationStates
{
    internal const string Ready = "READY";
    internal const string Stale = "STALE";
}

internal sealed record StatisticReconciliationActualPublicationCatalogPins(
    string P9CatalogVersion,
    string P9CatalogRawSha256,
    string P9CatalogSemanticSha256,
    string P9SchemaRawSha256,
    string P9SchemaSemanticSha256,
    string P9StageLockSha256,
    string CandidateChainId,
    string CandidatePromptId,
    string CandidateCatalogVersion,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateSchemaRawSha256,
    string CandidateSchemaSemanticSha256,
    string CandidateStageLockSha256,
    string CatalogPinSetSha256);

internal sealed record StatisticReconciliationActualPublishedSourceDecision(
    int Ordinal,
    string WorkId,
    string WorkAssignmentId,
    string ReportId,
    string SourceStableIdentitySha256,
    string NeutralStableIdentitySha256,
    bool Included,
    string OwnerDecisionCode,
    int OwnerOrdinal,
    string OwnerStateSemanticSha256,
    string SourceDecisionSemanticSha256,
    string SemanticSha256)
{
    internal static StatisticReconciliationActualPublishedSourceDecision Create(
        int ordinal,
        ActualSourceMembershipDecision value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var owner = value.ObservedOwner ??
            throw new StatisticReconciliationActualObservationException(
                "ACTUAL_SOURCE_PUBLICATION:OWNER_REQUIRED");
        var neutral =
            StatisticReconciliationActualSourceMembershipAdapter
                .NeutralStableIdentitySha256(
                    owner.WorkId,
                    owner.WorkAssignmentId,
                    owner.ReportId);
        var draft = new StatisticReconciliationActualPublishedSourceDecision(
            ordinal,
            owner.WorkId,
            owner.WorkAssignmentId,
            owner.ReportId,
            value.SourceStableIdentitySha256,
            neutral,
            value.Included,
            value.OwnerDecisionCode,
            value.OwnerOrdinal,
            value.OwnerStateSemanticSha256,
            value.DecisionSemanticSha256,
            "~");
        return Normalize(draft with { SemanticSha256 = ComputeSemantic(draft) });
    }

    internal static StatisticReconciliationActualPublishedSourceDecision
        Normalize(StatisticReconciliationActualPublishedSourceDecision value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Ordinal < 0 || value.OwnerOrdinal < 0)
            throw Fail("ORDINAL_INVALID");
        var normalized = value with
        {
            WorkId = Required(value.WorkId, "WORK_ID"),
            WorkAssignmentId = Required(
                value.WorkAssignmentId, "WORK_ASSIGNMENT_ID"),
            ReportId = Required(value.ReportId, "REPORT_ID"),
            SourceStableIdentitySha256 = Sha(
                value.SourceStableIdentitySha256,
                "SOURCE_STABLE_IDENTITY_SHA256"),
            NeutralStableIdentitySha256 = Sha(
                value.NeutralStableIdentitySha256,
                "NEUTRAL_STABLE_IDENTITY_SHA256"),
            OwnerDecisionCode = Upper(
                value.OwnerDecisionCode, "OWNER_DECISION_CODE"),
            OwnerStateSemanticSha256 = Sha(
                value.OwnerStateSemanticSha256,
                "OWNER_STATE_SEMANTIC_SHA256"),
            SourceDecisionSemanticSha256 = Sha(
                value.SourceDecisionSemanticSha256,
                "SOURCE_DECISION_SEMANTIC_SHA256"),
            SemanticSha256 = Sha(value.SemanticSha256, "SEMANTIC_SHA256")
        };
        var actualStable = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_STABLE_IDENTITY_V1",
            normalized.WorkId,
            normalized.WorkAssignmentId,
            normalized.ReportId);
        var neutralStable =
            StatisticReconciliationActualSourceMembershipAdapter
                .NeutralStableIdentitySha256(
                    normalized.WorkId,
                    normalized.WorkAssignmentId,
                    normalized.ReportId);
        var sourceDecision = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_OWNER_DECISION_V2",
            actualStable,
            StatisticReconciliationActualCanonical.Boolean(
                normalized.Included),
            normalized.OwnerDecisionCode,
            StatisticReconciliationActualCanonical.Integer(
                normalized.OwnerOrdinal),
            normalized.OwnerStateSemanticSha256);
        if (normalized.SourceStableIdentitySha256 != actualStable ||
            normalized.NeutralStableIdentitySha256 != neutralStable ||
            normalized.SourceDecisionSemanticSha256 != sourceDecision ||
            normalized.SemanticSha256 != ComputeSemantic(normalized))
            throw Fail("SEMANTIC_MISMATCH");
        return normalized;
    }

    internal static string ComputeSemantic(
        StatisticReconciliationActualPublishedSourceDecision value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_PUBLICATION_SOURCE_DECISION_V1",
            StatisticReconciliationActualCanonical.Integer(value.Ordinal),
            value.WorkId,
            value.WorkAssignmentId,
            value.ReportId,
            value.SourceStableIdentitySha256,
            value.NeutralStableIdentitySha256,
            StatisticReconciliationActualCanonical.Boolean(value.Included),
            value.OwnerDecisionCode,
            StatisticReconciliationActualCanonical.Integer(value.OwnerOrdinal),
            value.OwnerStateSemanticSha256,
            value.SourceDecisionSemanticSha256);

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Upper(string? value, string name)
        => StatisticReconciliationActualCanonical.Upper(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_SOURCE_PUBLICATION:{reason}");
}

internal sealed record StatisticReconciliationActualPublicationContext(
    string ReconciliationId,
    string ImmutableIdentitySha256,
    string ImmutableHeaderSha256,
    string WorkId,
    string ScopeAssignmentId,
    string PeriodKey,
    string PeriodInstanceKey,
    string ConceptKey,
    string Grain,
    string TimeAxis,
    string FilterSha256,
    string DynamicFormVersionId,
    string DynamicFormSchemaSha256,
    string? FlowTemplateVersionId,
    string? FlowPayloadSha256,
    string? FlowInstanceId,
    string? ExecutionEpochId,
    int? ExecutionEpoch,
    long? ExecutionEpochRevision,
    string P8ConfigurationOwnerId,
    string P8ConfigurationBundleSha256,
    string ActualConfigurationBundleSha256,
    string CoherentCatalogPinSetSha256,
    StatisticReconciliationActualPublicationCatalogPins CatalogPins,
    string? RecheckCaptureBindingSha256 = null,
    StatisticReconciliationActualSummaryPlanBinding? SummaryPlanBinding = null,
    string? LifecycleManifestSha256 = null,
    int? LifecycleObservationCount = null,
    string? RuntimeKind = null,
    string? ActualMembershipSemanticSha256 = null,
    string? ActualSourceDecisionManifestSha256 = null,
    ImmutableArray<StatisticReconciliationActualPublishedSourceDecision>
        ActualSourceDecisions = default,
    string? LifecycleMetricScopeSha256 = null,
    StatisticReconciliationActualRelationalProofBinding?
        RelationalProofBinding = null);

internal sealed record StatisticReconciliationActualPublicationLayer(
    int Ordinal,
    string Layer,
    string OwnerId,
    string VersionId,
    long ObservationCount,
    string CaptureSemanticSha256,
    string SemanticSha256,
    string TypedObservationManifestSha256,
    ImmutableArray<StatisticReconciliationActualTypedObservation>
        TypedObservations);

internal sealed record StatisticReconciliationActualPublicationGeneration(
    StatisticReconciliationActualPublicationContext Context,
    string GenerationId,
    string GenerationSemanticSha256,
    string SourceSetSha256,
    string InputBindingSha256,
    string LifecycleSemanticSha256,
    string ResultPinSetSha256,
    string ExportPinSetSha256,
    string CaptureState,
    ImmutableArray<StatisticReconciliationActualPublicationLayer> Layers,
    string? SummaryMappingManifestSha256 = null);


internal sealed record StatisticReconciliationActualCommittedLayer(
    int Ordinal,
    string Layer,
    string OwnerId,
    string OwnerVersionSha256,
    long ObservationCount,
    string CaptureSemanticSha256,
    string LayerSemanticSha256,
    string TypedObservationManifestSha256,
    int TypedObservationCount);

internal sealed record StatisticReconciliationActualAppendResult(
    string ReconciliationId,
    string GenerationId,
    string GenerationSemanticSha256,
    string ManifestSha256,
    int DocumentCount,
    int TypedAtomCount,
    ImmutableArray<StatisticReconciliationActualTypedObservation>
        TypedObservations,
    ImmutableArray<StatisticReconciliationActualCommittedLayer> Layers,
    StatisticReconciliationActualPublicationContext CommittedRunBinding,
    string CapturedSourceSetSha256,
    string CapturedRuntimePinSetSha256,
    string CapturedResultPinSetSha256,
    string CapturedExportPinSetSha256,
    bool ExactReplay,
    bool PublishedByCas,
    StatisticReconciliationActualSummaryPlanBinding? SummaryPlanBinding = null,
    string? SummaryMappingManifestSha256 = null,
    string? LifecycleManifestSha256 = null,
    int? LifecycleObservationCount = null,
    string? CapturedMembershipSemanticSha256 = null,
    string? CapturedSourceDecisionManifestSha256 = null,
    int? CapturedSourceDecisionCount = null,
    string? CapturedLifecycleMetricScopeSha256 = null,
    StatisticReconciliationActualRelationalProofBinding?
        RelationalProofBinding = null);

internal interface IStatisticReconciliationActualObservationBackend
{
    Task<IReadOnlyList<StatisticReconciliationObservation>> ReadGenerationAsync(
        string reconciliationId,
        string generationId,
        CancellationToken cancellationToken);

    Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken);

    Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken);
}

internal interface IStatisticReconciliationActualGenerationCas
{
    Task PublishAsync(
        string reconciliationId,
        string generationId,
        string generationSemanticSha256,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken);
}

internal sealed class StatisticReconciliationActualObservationMongoBackend(
    MongoDbContext context)
    : IStatisticReconciliationActualObservationBackend
{
    public async Task<IReadOnlyList<StatisticReconciliationObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        var documents = await context.StatisticReconciliationObservations
            .Find(item =>
                item.ReconciliationId == reconciliationId &&
                item.GenerationId == generationId &&
                item.SchemaVersion ==
                StatisticReconciliationActualGenerationPublisher.SchemaVersion &&
                (item.RecordKind ==
                     StatisticReconciliationActualPublicationRecordKinds.Layer ||
                 item.RecordKind ==
                     StatisticReconciliationActualPublicationRecordKinds.Atom ||
                 item.RecordKind ==
                     StatisticReconciliationActualPublicationRecordKinds.SourceDecision ||
                 item.RecordKind ==
                     StatisticReconciliationActualPublicationRecordKinds.Commit))
            .ToListAsync(cancellationToken);
        return documents;
    }

    public async Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
            return;
        try
        {
            await context.StatisticReconciliationObservations.InsertManyAsync(
                observations,
                new InsertManyOptions { IsOrdered = false },
                cancellationToken);
        }
        catch (MongoBulkWriteException<StatisticReconciliationObservation> error)
            when (error.WriteErrors.Count > 0 &&
                  error.WriteErrors.All(item =>
                      item.Category == ServerErrorCategory.DuplicateKey))
        {
            // The owner validates every existing immutable document below.
        }
    }

    public async Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.StatisticReconciliationObservations.InsertOneAsync(
                observation,
                cancellationToken: cancellationToken);
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // A racing exact replay is validated by the owner below.
        }
    }
}

internal sealed class StatisticReconciliationRunServiceActualGenerationCas(
    IStatisticReconciliationRunService runService,
    IStatisticReconciliationTrustedVerdictPipeline verdictPipeline)
    : IStatisticReconciliationActualGenerationCas
{
    public async Task PublishAsync(
        string reconciliationId,
        string generationId,
        string generationSemanticSha256,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken)
    {
        var pending = await runService.PublishPendingAsync(
            reconciliationId,
            new StatisticReconciliationPendingPublishRequest
            {
                WorkerId = workerId,
                ClaimToken = claimToken,
                GenerationId = generationId,
                GenerationHash = generationSemanticSha256
            },
            actor,
            cancellationToken);
        var command = StatisticReconciliationTrustedVerdictPipeline.CreateCommand(
            pending.ReconciliationId,
            generationId,
            generationSemanticSha256,
            pending.StateRevision,
            pending.StateHash,
            pending.GenerationPublishRevision);
        _ = await verdictPipeline.FinalizeAsync(
            command,
            actor,
            cancellationToken);
    }
}

internal sealed class StatisticReconciliationActualGenerationPublisher(
    IStatisticReconciliationActualObservationBackend backend,
    IStatisticReconciliationActualGenerationCas generationCas)
{
    internal const int MaximumDocumentCount = 100_000;
    internal const int MaximumTypedAtomCount =
        StatisticReconciliationActualTypedObservationCanonical
            .MaximumGenerationAtomCount;
    internal const long MaximumTypedCanonicalBytes =
        StatisticReconciliationActualTypedObservationCanonical
            .MaximumGenerationCanonicalUtf8Bytes;
    internal const string SchemaVersion = "P10_ACTUAL_OBSERVATION_V7";
    internal const string LegacySchemaVersionV6 = "P10_ACTUAL_OBSERVATION_V6";
    internal const string LegacySchemaVersion = "P10_ACTUAL_OBSERVATION_V5";
    internal const string LegacySchemaVersionV4 = "P10_ACTUAL_OBSERVATION_V4";
    internal const string LegacySchemaVersionV3 = "P10_ACTUAL_OBSERVATION_V3";
    private const string AlgorithmRevision = "P10_ACTUAL_CAPTURE_V7";
    private const string LegacyAlgorithmRevisionV6 = "P10_ACTUAL_CAPTURE_V6";
    private const string LegacyAlgorithmRevision = "P10_ACTUAL_CAPTURE_V5";
    private const string LegacyAlgorithmRevisionV4 = "P10_ACTUAL_CAPTURE_V4";
    private const string LegacyAlgorithmRevisionV3 = "P10_ACTUAL_CAPTURE_V3";
    private static readonly string AlgorithmSha256 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CAPTURE_ALGORITHM_V7",
            AlgorithmRevision,
            "BOUNDARY_REREAD",
            "RUNTIME_KIND_DISCRIMINATOR_V1",
            "SUMMARY_IDENTITY_MAPPING_V3",
            "ACTUAL_SOURCE_DECISION_MANIFEST_V1",
            "NEUTRAL_INCLUDED_MEMBERSHIP_V1",
            "LIFECYCLE_METRIC_SCOPE_BINDING_V1",
            "LIFECYCLE_MANIFEST_BINDING_V1",
            "SERVER_ONLY_RELATIONAL_PROOF_BINDING_V2",
            "APPEND_CONTENT_THEN_COMMIT",
            "ONE_CAS_PUBLICATION");
    private static readonly string LegacyAlgorithmSha256V6 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CAPTURE_ALGORITHM_V6",
            LegacyAlgorithmRevisionV6,
            "BOUNDARY_REREAD",
            "RUNTIME_KIND_DISCRIMINATOR_V1",
            "SUMMARY_IDENTITY_MAPPING_V3",
            "ACTUAL_SOURCE_DECISION_MANIFEST_V1",
            "NEUTRAL_INCLUDED_MEMBERSHIP_V1",
            "LIFECYCLE_METRIC_SCOPE_BINDING_V1",
            "LIFECYCLE_MANIFEST_BINDING_V1",
            "APPEND_CONTENT_THEN_COMMIT",
            "ONE_CAS_PUBLICATION");
    private static readonly string LegacyAlgorithmSha256 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CAPTURE_ALGORITHM_V5",
            LegacyAlgorithmRevision,
            "BOUNDARY_REREAD",
            "RUNTIME_KIND_DISCRIMINATOR_V1",
            "SUMMARY_IDENTITY_MAPPING_V3",
            "ACTUAL_SOURCE_DECISION_MANIFEST_V1",
            "NEUTRAL_INCLUDED_MEMBERSHIP_V1",
            "LIFECYCLE_MANIFEST_BINDING_V1",
            "APPEND_CONTENT_THEN_COMMIT",
            "ONE_CAS_PUBLICATION");
    private static readonly string LegacyAlgorithmSha256V4 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CAPTURE_ALGORITHM_V4",
            LegacyAlgorithmRevisionV4,
            "BOUNDARY_REREAD",
            "SUMMARY_IDENTITY_MAPPING_V2",
            "LIFECYCLE_MANIFEST_BINDING_V1",
            "APPEND_CONTENT_THEN_COMMIT",
            "ONE_CAS_PUBLICATION");
    private static readonly string LegacyAlgorithmSha256V3 =
        StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CAPTURE_ALGORITHM_V3",
            LegacyAlgorithmRevisionV3,
            "BOUNDARY_REREAD",
            "APPEND_CONTENT_THEN_COMMIT",
            "ONE_CAS_PUBLICATION");

    internal Task<StatisticReconciliationActualAppendResult> PublishCoherentAsync(
        StatisticReconciliationActualCoherentGeneration coherent,
        StatisticReconciliationActualPublicationContext context,
        DateTime createdAtUtc,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coherent);
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizePublicationRuntime(context);
        if (!coherent.ReadyForAppendOnlyPersistence ||
            coherent.PublicationForbidden ||
            !coherent.BoundaryStable ||
            !coherent.Complete ||
            coherent.CaptureState !=
            StatisticReconciliationActualCoherentCaptureStates.Ready ||
            coherent.StaleReason is not null)
            throw Invalid("COHERENT_GENERATION_NOT_READY");
        var boundary = coherent.CommonBoundary;
        if (!StringComparer.Ordinal.Equals(
                boundary.BoundarySemanticSha256,
                coherent.FinalBoundary.BoundarySemanticSha256) ||
            context.ReconciliationId != coherent.ReconciliationId ||
            context.ReconciliationId != boundary.ReconciliationId ||
            context.WorkId != boundary.WorkId ||
            context.ScopeAssignmentId != boundary.ScopeAssignmentId ||
            context.FilterSha256 != boundary.FilterSha256 ||
            context.ActualConfigurationBundleSha256 !=
            boundary.ConfigurationBundleSha256 ||
            context.CoherentCatalogPinSetSha256 !=
            boundary.CatalogPinSetSha256)
            throw Invalid("COHERENT_CONTEXT_BINDING_MISMATCH");
        if (coherent.OrderedLayerObservations.Length !=
                StatisticReconciliationActualCoherentLayers.RequiredOrder.Length ||
            !coherent.OrderedLayerObservations.Select(item => item.Layer)
                .SequenceEqual(
                    StatisticReconciliationActualCoherentLayers.RequiredOrder,
                    StringComparer.Ordinal))
            throw Invalid("COHERENT_LAYER_SET_INVALID");
        var layerManifestSha256 = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_COHERENT_LAYER_MANIFEST_V2",
            coherent.OrderedLayerObservations.Select(item =>
                item.LayerSemanticSha256));
        if (!StringComparer.Ordinal.Equals(
                layerManifestSha256,
                coherent.LayerManifestSha256))
            throw Invalid("COHERENT_LAYER_MANIFEST_MISMATCH");
        var generationSemanticSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_GENERATION_V2",
            coherent.ReconciliationId,
            boundary.BoundarySemanticSha256,
            coherent.FinalBoundary.BoundarySemanticSha256,
            coherent.LayerManifestSha256,
            coherent.CaptureState,
            coherent.StaleReason,
            StatisticReconciliationActualCanonical.Boolean(
                coherent.BoundaryStable),
            StatisticReconciliationActualCanonical.Boolean(coherent.Complete));
        var generationId = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_GENERATION_ID_V2",
            coherent.ReconciliationId,
            generationSemanticSha256);
        if (!StringComparer.Ordinal.Equals(
                generationSemanticSha256,
                coherent.GenerationSemanticSha256) ||
            !StringComparer.Ordinal.Equals(generationId, coherent.GenerationId))
            throw Invalid("COHERENT_GENERATION_SEMANTIC_MISMATCH");
        var publicationLayers = coherent.OrderedLayerObservations.Select(item =>
                new StatisticReconciliationActualPublicationLayer(
                    item.Ordinal,
                    item.Layer,
                    item.OwnerId,
                    item.OwnerVersionSha256,
                    item.ObservationCount,
                    item.CaptureSemanticSha256,
                    item.LayerSemanticSha256,
                    item.TypedObservationManifestSha256,
                    item.TypedObservations))
            .ToImmutableArray();
        var summaryPlan = context.SummaryPlanBinding is null
            ? throw Invalid("SUMMARY_PLAN_BINDING_REQUIRED")
            : StatisticReconciliationActualSummaryPlanBinding.Normalize(
                context.SummaryPlanBinding);
        var sourceDecisions = NormalizeSourceDecisions(
            context.ActualSourceDecisions);
        if (sourceDecisions.Any(value =>
                !StringComparer.Ordinal.Equals(value.WorkId, context.WorkId)))
            throw Invalid("SOURCE_DECISION_WORK_MISMATCH");
        var membershipSemantic = MembershipSemanticSha256(sourceDecisions);
        var sourceDecisionManifest =
            SourceDecisionManifestSha256(sourceDecisions);
        if (!StringComparer.Ordinal.Equals(
                membershipSemantic,
                OptionalSha(context.ActualMembershipSemanticSha256,
                    "ACTUAL_MEMBERSHIP_SEMANTIC_SHA256")) ||
            !StringComparer.Ordinal.Equals(
                sourceDecisionManifest,
                OptionalSha(context.ActualSourceDecisionManifestSha256,
                    "ACTUAL_SOURCE_DECISION_MANIFEST_SHA256")))
            throw Invalid("SOURCE_DECISION_BINDING_MISMATCH");
        var lifecycleMetricScope = ExactSha(
            context.LifecycleMetricScopeSha256,
            "LIFECYCLE_METRIC_SCOPE_SHA256");
        var summaryMappingManifest =
            StatisticReconciliationActualSummaryMappingManifest.Create(
                summaryPlan,
                publicationLayers.Select(item => (
                    item.Layer,
                    item.OwnerId,
                    item.VersionId,
                    item.TypedObservationManifestSha256)));
        var lifecycleManifest = OptionalSha(
            context.LifecycleManifestSha256,
            "LIFECYCLE_MANIFEST_SHA256");
        if ((lifecycleManifest is null) !=
                (context.LifecycleObservationCount is null) ||
            (lifecycleManifest is not null &&
             context.LifecycleObservationCount is not > 0))
            throw Invalid("LIFECYCLE_MANIFEST_BINDING_INVALID");
        var relationalProof = context.RelationalProofBinding is null
            ? throw Invalid("RELATIONAL_PROOF_BINDING_REQUIRED")
            : StatisticReconciliationActualRelationalProofBinding.Normalize(
                context.RelationalProofBinding);
        if (relationalProof.Facts.SummaryPlanBindingSha256 !=
            summaryPlan.SemanticSha256)
            throw Invalid("RELATIONAL_PROOF_PLAN_BINDING_MISMATCH");
        StatisticReconciliationActualRelationalProofOwner
            .RequirePlanProjections(relationalProof.Facts, summaryPlan);
        var committedGenerationSha256 =
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_COMMITTED_GENERATION_V7",
                coherent.GenerationSemanticSha256,
                context.RecheckCaptureBindingSha256 is null
                    ? "~"
                    : ExactSha(context.RecheckCaptureBindingSha256,
                        "RECHECK_CAPTURE_BINDING_SHA256"),
                context.RuntimeKind!,
                membershipSemantic,
                sourceDecisionManifest,
                StatisticReconciliationActualCanonical.Integer(
                    sourceDecisions.Length),
                lifecycleMetricScope,
                summaryPlan.SemanticSha256,
                summaryMappingManifest,
                lifecycleManifest ?? "~",
                context.LifecycleObservationCount?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "~",
                relationalProof.SemanticSha256);
        var committedGenerationId =
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_COMMITTED_GENERATION_ID_V7",
                coherent.ReconciliationId,
                committedGenerationSha256);
        var committedContext = context with
        {
            SummaryPlanBinding = summaryPlan,
            LifecycleManifestSha256 = lifecycleManifest,
            ActualMembershipSemanticSha256 = membershipSemantic,
            ActualSourceDecisionManifestSha256 = sourceDecisionManifest,
            ActualSourceDecisions = sourceDecisions,
            LifecycleMetricScopeSha256 = lifecycleMetricScope,
            RelationalProofBinding = relationalProof
        };
        var generation = new StatisticReconciliationActualPublicationGeneration(
            committedContext,
            committedGenerationId,
            committedGenerationSha256,
            boundary.SourceSetSha256,
            boundary.BoundarySemanticSha256,
            boundary.RuntimePinSetSha256,
            boundary.ResultPinSetSha256,
            boundary.ExportPinSetSha256,
            StatisticReconciliationActualPublicationStates.Ready,
            publicationLayers,
            summaryMappingManifest);
        return PublishCoreAsync(
            generation,
            createdAtUtc,
            workerId,
            claimToken,
            actor,
            cancellationToken);
    }

    private async Task<StatisticReconciliationActualAppendResult> PublishCoreAsync(
        StatisticReconciliationActualPublicationGeneration generation,
        DateTime createdAtUtc,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(actor);
        if (createdAtUtc.Kind != DateTimeKind.Utc)
            throw Invalid("CREATED_AT_NOT_UTC");
        if (!StringComparer.Ordinal.Equals(
                generation.CaptureState,
                StatisticReconciliationActualPublicationStates.Ready))
            throw Invalid("GENERATION_NOT_READY");

        var normalized = Normalize(generation);
        var before = await backend.ReadGenerationAsync(
            normalized.Context.ReconciliationId,
            normalized.GenerationId,
            cancellationToken);
        var effectiveCreatedAtUtc = ResolveCreatedAtUtc(before, createdAtUtc);
        var content = BuildContent(normalized, effectiveCreatedAtUtc);
        var manifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_OBSERVATION_MANIFEST_V7",
            content.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.DocumentSemanticSha256));
        var atomCount = normalized.Layers.Sum(layer =>
            layer.TypedObservations.Length);
        var commit = BuildCommit(
            normalized,
            effectiveCreatedAtUtc,
            manifest,
            normalized.Layers.Length,
            atomCount,
            content.Count);
        var expected = content.Append(commit)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        ValidateExisting(before, expected, allowIncomplete: true);
        var replay = HasCommit(before);
        if (!replay)
        {
            await backend.AppendContentAsync(content, cancellationToken);
            var afterContent = await backend.ReadGenerationAsync(
                normalized.Context.ReconciliationId,
                normalized.GenerationId,
                cancellationToken);
            ValidateExisting(afterContent, expected, allowIncomplete: true);
            EnsureAllContent(content, afterContent);
            if (!HasCommit(afterContent))
                await backend.AppendCommitAsync(commit, cancellationToken);
        }

        var completed = await backend.ReadGenerationAsync(
            normalized.Context.ReconciliationId,
            normalized.GenerationId,
            cancellationToken);
        ValidateExisting(completed, expected, allowIncomplete: false);
        ValidateComplete(completed, manifest, expected.Count);

        await generationCas.PublishAsync(
            normalized.Context.ReconciliationId,
            normalized.GenerationId,
            normalized.GenerationSemanticSha256,
            StatisticReconciliationActualCanonical.Required(workerId, "WORKER_ID", 128),
            StatisticReconciliationActualCanonical.Required(claimToken, "CLAIM_TOKEN", 128),
            actor,
            cancellationToken);

        return new StatisticReconciliationActualAppendResult(
            normalized.Context.ReconciliationId,
            normalized.GenerationId,
            normalized.GenerationSemanticSha256,
            manifest,
            expected.Count,
            atomCount,
            ExtractTypedObservations(completed),
            ExtractCommittedLayers(completed),
            RunBindingFromStoredDocument(
                completed.Single(item =>
                    item.RecordKind ==
                    StatisticReconciliationActualPublicationRecordKinds.Commit),
                ExtractSourceDecisions(completed)),
            normalized.SourceSetSha256,
            normalized.LifecycleSemanticSha256,
            normalized.ResultPinSetSha256,
            normalized.ExportPinSetSha256,
            replay,
            PublishedByCas: true,
            normalized.Context.SummaryPlanBinding,
            normalized.SummaryMappingManifestSha256,
            normalized.Context.LifecycleManifestSha256,
            normalized.Context.LifecycleObservationCount,
            CapturedMembershipSemanticSha256:
                normalized.Context.ActualMembershipSemanticSha256,
            CapturedSourceDecisionManifestSha256:
                normalized.Context.ActualSourceDecisionManifestSha256,
            CapturedSourceDecisionCount:
                normalized.Context.ActualSourceDecisions.Length,
            CapturedLifecycleMetricScopeSha256:
                normalized.Context.LifecycleMetricScopeSha256,
            RelationalProofBinding:
                normalized.Context.RelationalProofBinding);
    }

    internal async Task<StatisticReconciliationActualAppendResult?>
        ReadCompleteAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken = default)
        => await ReadCompleteFromBackendAsync(
            backend,
            reconciliationId,
            generationId,
            cancellationToken);

    internal static async Task<StatisticReconciliationActualAppendResult?>
        ReadCompleteFromBackendAsync(
            IStatisticReconciliationActualObservationBackend observationBackend,
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observationBackend);
        reconciliationId = StatisticReconciliationActualCanonical.Required(
            reconciliationId,
            "RECONCILIATION_ID");
        generationId = ExactSha(generationId, "GENERATION_ID");
        var documents = await observationBackend.ReadGenerationAsync(
            reconciliationId,
            generationId,
            cancellationToken);
        var commits = documents.Where(item =>
                item.RecordKind == StatisticReconciliationActualPublicationRecordKinds.Commit)
            .ToArray();
        if (commits.Length == 0)
            return null;
        if (commits.Length != 1)
            throw Invalid("COMMIT_CARDINALITY_INVALID");
        var commit = commits[0];
        var manifestSha256 = commit.Commit?.ManifestSha256
            ?? throw Invalid("COMMIT_MANIFEST_REQUIRED");
        var documentCount = commit.Commit?.DocumentCount
            ?? throw Invalid("COMMIT_DOCUMENT_COUNT_REQUIRED");
        ValidateComplete(
            documents,
            ExactSha(manifestSha256, "MANIFEST_SHA256"),
            documentCount);
        return new StatisticReconciliationActualAppendResult(
            reconciliationId,
            generationId,
            commit.GenerationSemanticSha256,
            manifestSha256,
            documentCount,
            commit.Commit!.AtomCount,
            ExtractTypedObservations(documents),
            ExtractCommittedLayers(documents),
            RunBindingFromStoredDocument(
                commit,
                ExtractSourceDecisions(documents)),
            ExactSha(commit.SourceSetSha256,
                "STORED_SOURCE_SET_SHA256"),
            ExactSha(commit.LifecycleSemanticSha256,
                "STORED_RUNTIME_PIN_SET_SHA256"),
            ExactSha(commit.CapturedResultPinSetSha256,
                "STORED_RESULT_PIN_SET_SHA256"),
            ExactSha(commit.CapturedExportPinSetSha256,
                "STORED_EXPORT_PIN_SET_SHA256"),
            true,
            false,
            SummaryPlanBindingFromStoredDocument(commit),
            OptionalSha(commit.SummaryMappingManifestSha256,
                "STORED_SUMMARY_MAPPING_MANIFEST_SHA256"),
            OptionalSha(commit.LifecycleManifestSha256,
                "STORED_LIFECYCLE_MANIFEST_SHA256"),
            commit.LifecycleObservationCount,
            ExactSha(commit.ActualMembershipSemanticSha256,
                "STORED_ACTUAL_MEMBERSHIP_SEMANTIC_SHA256"),
            ExactSha(commit.ActualSourceDecisionManifestSha256,
                "STORED_ACTUAL_SOURCE_DECISION_MANIFEST_SHA256"),
            commit.Commit.SourceDecisionCount,
            ExactSha(commit.LifecycleMetricScopeSha256,
                "STORED_LIFECYCLE_METRIC_SCOPE_SHA256"),
            RelationalProofBindingFromStoredDocument(commit));
    }

    private static ImmutableArray<StatisticReconciliationActualCommittedLayer>
        ExtractCommittedLayers(
            IEnumerable<StatisticReconciliationObservation> documents)
        => documents
            .Where(item => item.RecordKind ==
                StatisticReconciliationActualPublicationRecordKinds.Layer)
            .OrderBy(item => item.ActualLayer!.Ordinal)
            .Select(item => new StatisticReconciliationActualCommittedLayer(
                item.ActualLayer!.Ordinal,
                item.LineagePin!.Layer,
                item.LineagePin.OwnerId,
                item.LineagePin.VersionId,
                item.ActualLayer.ObservationCount,
                item.ActualLayer.CaptureSemanticSha256,
                item.ActualLayer.LayerSemanticSha256,
                item.ActualLayer.TypedObservationManifestSha256,
                item.ActualLayer.TypedObservationCount))
            .ToImmutableArray();

    private static ImmutableArray<
        StatisticReconciliationActualPublishedSourceDecision>
        ExtractSourceDecisions(
            IEnumerable<StatisticReconciliationObservation> documents)
        => documents
            .Where(item => item.RecordKind ==
                StatisticReconciliationActualPublicationRecordKinds
                    .SourceDecision)
            .Select(FromStoredSourceDecision)
            .OrderBy(value => value.Ordinal)
            .ToImmutableArray();

    private static StatisticReconciliationActualPublishedSourceDecision
        FromStoredSourceDecision(StatisticReconciliationObservation document)
    {
        var value = document.ActualSourceDecision ??
            throw Invalid("STORED_ACTUAL_SOURCE_DECISION_REQUIRED");
        return StatisticReconciliationActualPublishedSourceDecision.Normalize(
            new StatisticReconciliationActualPublishedSourceDecision(
                value.Ordinal,
                value.WorkId,
                value.WorkAssignmentId,
                value.ReportId,
                value.SourceStableIdentitySha256,
                value.NeutralStableIdentitySha256,
                value.Included,
                value.OwnerDecisionCode,
                value.OwnerOrdinal,
                value.OwnerStateSemanticSha256,
                value.SourceDecisionSemanticSha256,
                value.SemanticSha256));
    }

    private static StatisticReconciliationActualPublicationContext
        RunBindingFromStoredDocument(
            StatisticReconciliationObservation document,
            ImmutableArray<StatisticReconciliationActualPublishedSourceDecision>
                sourceDecisions)
    {
        ArgumentNullException.ThrowIfNull(document);
        var storedPins = document.CatalogPins
            ?? throw Invalid("STORED_CATALOG_PINS_REQUIRED");
        var pins = NormalizeCatalog(
            new StatisticReconciliationActualPublicationCatalogPins(
                storedPins.P9CatalogVersion,
                storedPins.P9CatalogRawSha256,
                storedPins.P9CatalogSemanticSha256,
                storedPins.P9SchemaRawSha256,
                storedPins.P9SchemaSemanticSha256,
                storedPins.P9StageLockSha256,
                storedPins.CandidateChainId,
                storedPins.CandidatePromptId,
                storedPins.CandidateCatalogVersion,
                storedPins.CandidateCatalogRawSha256,
                storedPins.CandidateCatalogSemanticSha256,
                storedPins.CandidateSchemaRawSha256,
                storedPins.CandidateSchemaSemanticSha256,
                storedPins.CandidateStageLockSha256,
                storedPins.CatalogPinSetSha256));
        return new StatisticReconciliationActualPublicationContext(
            document.ReconciliationId,
            document.ImmutableIdentitySha256,
            document.ImmutableHeaderSha256,
            document.WorkId,
            document.ScopeAssignmentId,
            document.PeriodKey,
            document.PeriodInstanceKey,
            document.ConceptKey,
            document.Grain,
            document.TimeAxis,
            document.FilterSha256,
            document.DynamicFormVersionId,
            document.DynamicFormSchemaSha256,
            document.FlowTemplateVersionId,
            document.FlowPayloadSha256,
            document.FlowInstanceId,
            document.ExecutionEpochId,
            document.ExecutionEpoch,
            document.ExecutionEpochRevision,
            document.P8ConfigurationOwnerId,
            document.P8ConfigurationBundleSha256,
            document.ActualConfigurationBundleSha256,
            document.MetricPlanSha256,
            pins,
            OptionalSha(document.RecheckCaptureBindingSha256,
                "STORED_RECHECK_CAPTURE_BINDING_SHA256"),
            SummaryPlanBindingFromStoredDocument(document),
            OptionalSha(document.LifecycleManifestSha256,
                "STORED_LIFECYCLE_MANIFEST_SHA256"),
            document.LifecycleObservationCount,
            document.RuntimeKind,
            ExactSha(document.ActualMembershipSemanticSha256,
                "STORED_ACTUAL_MEMBERSHIP_SEMANTIC_SHA256"),
            ExactSha(document.ActualSourceDecisionManifestSha256,
                "STORED_ACTUAL_SOURCE_DECISION_MANIFEST_SHA256"),
            sourceDecisions,
            ExactSha(document.LifecycleMetricScopeSha256,
                "STORED_LIFECYCLE_METRIC_SCOPE_SHA256"),
            RelationalProofBindingFromStoredDocument(document));
    }

    private static StatisticReconciliationActualSummaryPlanBinding
        SummaryPlanBindingFromStoredDocument(
            StatisticReconciliationObservation document)
    {
        if (document.RecordKind !=
                StatisticReconciliationActualPublicationRecordKinds.Commit)
            throw Invalid("STORED_SUMMARY_PLAN_COMMIT_REQUIRED");
        var values = new object?[]
        {
            document.SummaryExpectedReconciliationId,
            document.SummaryExpectedGenerationId,
            document.SummaryExpectedGenerationSha256,
            document.SummaryExpectedMetricPlanSha256,
            document.SummaryExpectedMetricPlanEntryCount,
            document.SummaryExpectedManifestSha256,
            document.SummaryExpectedDocumentCount,
            document.SummaryExpectedMembershipSemanticSha256,
            document.SummaryExpectedRuntimeKind,
            document.SummaryExpectedIdentitySetSha256,
            document.SummaryMetricIdentityCount,
            document.SummaryPlanBindingSha256
        };
        if (values.Any(value => value is null))
            throw Invalid("STORED_SUMMARY_PLAN_BINDING_INCOMPLETE");
        var stored = document.Commit?.SummaryIdentityDescriptors
            ?? throw Invalid("STORED_SUMMARY_DESCRIPTORS_REQUIRED");
        var descriptors = stored.Select(FromStoredSummaryDescriptor)
            .ToImmutableArray();
        var exact = new StatisticReconciliationExpectedGenerationBinding(
            document.SummaryExpectedReconciliationId!,
            document.SummaryExpectedGenerationId!,
            document.SummaryExpectedGenerationSha256!,
            document.SummaryExpectedMetricPlanSha256!,
            document.SummaryExpectedMetricPlanEntryCount!.Value,
            document.SummaryExpectedManifestSha256!,
            document.SummaryExpectedDocumentCount!.Value,
            document.SummaryExpectedMembershipSemanticSha256!,
            document.SummaryExpectedRuntimeKind!);
        var binding = StatisticReconciliationActualSummaryPlanBinding.Create(
            exact,
            descriptors);
        if (binding.ExpectedIdentitySetSha256 !=
                document.SummaryExpectedIdentitySetSha256 ||
            binding.MetricIdentityCount != document.SummaryMetricIdentityCount ||
            binding.SemanticSha256 != document.SummaryPlanBindingSha256)
            throw Invalid("STORED_SUMMARY_PLAN_BINDING_MISMATCH");
        return binding;
    }
    private static StatisticReconciliationActualPublicationContext
        NormalizePublicationRuntime(
            StatisticReconciliationActualPublicationContext context)
    {
        var runtimeKind = Required(context.RuntimeKind, "RUNTIME_KIND")
            .ToUpperInvariant();
        if (runtimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            if (context.ExecutionEpoch is not > 0 ||
                context.ExecutionEpochRevision is not > 0)
                throw Invalid("FLOW_EXECUTION_EPOCH_INVALID");
            return context with
            {
                RuntimeKind = runtimeKind,
                FlowTemplateVersionId = Required(
                    context.FlowTemplateVersionId, "FLOW_VERSION_ID"),
                FlowPayloadSha256 = ExactSha(
                    context.FlowPayloadSha256, "FLOW_PAYLOAD_SHA256"),
                FlowInstanceId = Required(
                    context.FlowInstanceId, "FLOW_INSTANCE_ID"),
                ExecutionEpochId = Required(
                    context.ExecutionEpochId, "EXECUTION_EPOCH_ID"),
                ExecutionEpoch = context.ExecutionEpoch.Value,
                ExecutionEpochRevision = context.ExecutionEpochRevision.Value
            };
        }
        if (runtimeKind !=
                StatisticReconciliationExpectedRuntimeKinds.NonFlow ||
            context.FlowTemplateVersionId is not null ||
            context.FlowPayloadSha256 is not null ||
            context.FlowInstanceId is not null ||
            context.ExecutionEpochId is not null ||
            context.ExecutionEpoch is not null ||
            context.ExecutionEpochRevision is not null)
            throw Invalid("NON_FLOW_RUNTIME_BINDING_INVALID");
        return context with
        {
            RuntimeKind = runtimeKind,
            FlowTemplateVersionId = null,
            FlowPayloadSha256 = null,
            FlowInstanceId = null,
            ExecutionEpochId = null,
            ExecutionEpoch = null,
            ExecutionEpochRevision = null
        };
    }

    private static ImmutableArray<
        StatisticReconciliationActualPublishedSourceDecision>
        NormalizeSourceDecisions(
            ImmutableArray<StatisticReconciliationActualPublishedSourceDecision>
                values)
    {
        if (values.IsDefault ||
            values.Length >
                StatisticReconciliationActualSourceMembershipAdapter
                    .MaxSourceCandidates)
            throw Invalid("SOURCE_DECISION_SET_INVALID");
        var normalized = values
            .Select(StatisticReconciliationActualPublishedSourceDecision
                .Normalize)
            .OrderBy(value => value.Ordinal)
            .ToImmutableArray();
        if (!normalized.Select(value => value.Ordinal)
                .SequenceEqual(Enumerable.Range(0, normalized.Length)) ||
            normalized.Select(value => value.SourceStableIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Select(value => value.NeutralStableIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Select(value => value.ReportId)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Select(value => value.OwnerOrdinal)
                .Distinct().Count() != normalized.Length)
            throw Invalid("SOURCE_DECISION_SET_AMBIGUOUS");
        return normalized;
    }

    private static string SourceDecisionManifestSha256(
        ImmutableArray<StatisticReconciliationActualPublishedSourceDecision>
            values)
        => StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_PUBLICATION_SOURCE_DECISION_MANIFEST_V1",
            values.Select(value => value.SemanticSha256));

    private static string MembershipSemanticSha256(
        ImmutableArray<StatisticReconciliationActualPublishedSourceDecision>
            values)
        => StatisticReconciliationActualCanonical.HashSequence(
            "P10_INCLUDED_SOURCE_MEMBERSHIP_V1",
            values.Where(value => value.Included)
                .Select(value => value.NeutralStableIdentitySha256)
                .OrderBy(value => value, StringComparer.Ordinal));

    private static StatisticReconciliationActualPublicationGeneration Normalize(
        StatisticReconciliationActualPublicationGeneration value)
    {
        var context = NormalizePublicationRuntime(
            value.Context ?? throw Invalid("CONTEXT_REQUIRED"));
        var sourceDecisions = NormalizeSourceDecisions(
            context.ActualSourceDecisions);
        var membershipSemantic = MembershipSemanticSha256(sourceDecisions);
        var sourceDecisionManifest =
            SourceDecisionManifestSha256(sourceDecisions);
        if (membershipSemantic != ExactSha(
                context.ActualMembershipSemanticSha256,
                "ACTUAL_MEMBERSHIP_SEMANTIC_SHA256") ||
            sourceDecisionManifest != ExactSha(
                context.ActualSourceDecisionManifestSha256,
                "ACTUAL_SOURCE_DECISION_MANIFEST_SHA256"))
            throw Invalid("SOURCE_DECISION_BINDING_MISMATCH");
        var normalizedContext = context with
        {
            ReconciliationId = Required(context.ReconciliationId, "RECONCILIATION_ID"),
            ImmutableIdentitySha256 = ExactSha(context.ImmutableIdentitySha256, "IMMUTABLE_IDENTITY_SHA256"),
            ImmutableHeaderSha256 = ExactSha(context.ImmutableHeaderSha256, "IMMUTABLE_HEADER_SHA256"),
            WorkId = Required(context.WorkId, "WORK_ID"),
            ScopeAssignmentId = Required(context.ScopeAssignmentId, "SCOPE_ASSIGNMENT_ID"),
            PeriodKey = Required(context.PeriodKey, "PERIOD_KEY"),
            PeriodInstanceKey = Required(context.PeriodInstanceKey, "PERIOD_INSTANCE_KEY"),
            ConceptKey = Required(context.ConceptKey, "CONCEPT_KEY"),
            Grain = Required(context.Grain, "GRAIN"),
            TimeAxis = Required(context.TimeAxis, "TIME_AXIS"),
            FilterSha256 = ExactSha(context.FilterSha256, "FILTER_SHA256"),
            DynamicFormVersionId = Required(context.DynamicFormVersionId, "FORM_VERSION_ID"),
            DynamicFormSchemaSha256 = ExactSha(context.DynamicFormSchemaSha256, "FORM_SCHEMA_SHA256"),
            P8ConfigurationOwnerId = Required(context.P8ConfigurationOwnerId, "P8_OWNER_ID"),
            P8ConfigurationBundleSha256 = ExactSha(context.P8ConfigurationBundleSha256, "P8_BUNDLE_SHA256"),
            ActualConfigurationBundleSha256 = ExactSha(
                context.ActualConfigurationBundleSha256,
                "ACTUAL_CONFIGURATION_BUNDLE_SHA256"),
            CoherentCatalogPinSetSha256 = ExactSha(
                context.CoherentCatalogPinSetSha256,
                "COHERENT_CATALOG_PIN_SET_SHA256"),
            RecheckCaptureBindingSha256 = OptionalSha(
                context.RecheckCaptureBindingSha256,
                "RECHECK_CAPTURE_BINDING_SHA256"),
            SummaryPlanBinding = context.SummaryPlanBinding is null
                ? throw Invalid("SUMMARY_PLAN_BINDING_REQUIRED")
                : StatisticReconciliationActualSummaryPlanBinding.Normalize(
                    context.SummaryPlanBinding),
            LifecycleManifestSha256 = OptionalSha(
                context.LifecycleManifestSha256,
                "LIFECYCLE_MANIFEST_SHA256"),
            ActualMembershipSemanticSha256 = membershipSemantic,
            ActualSourceDecisionManifestSha256 = sourceDecisionManifest,
            ActualSourceDecisions = sourceDecisions,
            LifecycleMetricScopeSha256 = ExactSha(
                context.LifecycleMetricScopeSha256,
                "LIFECYCLE_METRIC_SCOPE_SHA256"),
            RelationalProofBinding = context.RelationalProofBinding is null
                ? throw Invalid("RELATIONAL_PROOF_BINDING_REQUIRED")
                : StatisticReconciliationActualRelationalProofBinding.Normalize(
                    context.RelationalProofBinding),
            CatalogPins = NormalizeCatalog(context.CatalogPins)
        };
        if (!StringComparer.Ordinal.Equals(
                normalizedContext.SummaryPlanBinding.ExpectedReconciliationId,
                normalizedContext.ReconciliationId) ||
            !StringComparer.Ordinal.Equals(
                normalizedContext.SummaryPlanBinding.ExpectedRuntimeKind,
                normalizedContext.RuntimeKind) ||
            sourceDecisions.Any(value => !StringComparer.Ordinal.Equals(
                value.WorkId, normalizedContext.WorkId)))
            throw Invalid("TRUSTED_PLAN_OR_SOURCE_CONTEXT_MISMATCH");
        if ((normalizedContext.LifecycleManifestSha256 is null) !=
                (normalizedContext.LifecycleObservationCount is null) ||
            (normalizedContext.LifecycleManifestSha256 is not null &&
             normalizedContext.LifecycleObservationCount is not > 0))
            throw Invalid("LIFECYCLE_MANIFEST_BINDING_INVALID");
        if (value.Layers.IsDefaultOrEmpty)
            throw Invalid("LAYERS_REQUIRED");
        var layerBuilder = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualPublicationLayer>(value.Layers.Length);
        var typedBudget =
            new StatisticReconciliationActualTypedObservationCanonical
                .GenerationBudget();
        foreach (var layer in value.Layers)
        {
            var typed = StatisticReconciliationActualTypedObservationCanonical
                .NormalizeSet(
                    layer.TypedObservations.IsDefault
                        ? []
                        : layer.TypedObservations,
                    typedBudget);
            var typedManifest =
                StatisticReconciliationActualTypedObservationCanonical
                    .ManifestSha256(typed);
            var normalizedLayer = layer with
            {
                Layer = Required(layer.Layer, "LAYER").ToUpperInvariant(),
                OwnerId = Required(layer.OwnerId, "LAYER_OWNER_ID"),
                VersionId = ExactSha(layer.VersionId, "LAYER_VERSION_ID"),
                CaptureSemanticSha256 = ExactSha(
                    layer.CaptureSemanticSha256,
                    "LAYER_CAPTURE_SEMANTIC_SHA256"),
                SemanticSha256 = ExactSha(
                    layer.SemanticSha256,
                    "LAYER_SEMANTIC_SHA256"),
                TypedObservationManifestSha256 = ExactSha(
                    layer.TypedObservationManifestSha256,
                    "LAYER_TYPED_MANIFEST_SHA256"),
                TypedObservations = typed
            };
            if (normalizedLayer.Ordinal < 0 ||
                normalizedLayer.ObservationCount < 0 ||
                normalizedLayer.TypedObservationManifestSha256 != typedManifest ||
                typed.Any(item => item.Layer != normalizedLayer.Layer ||
                    item.OwnerId != normalizedLayer.OwnerId ||
                    item.OwnerVersionSha256 != normalizedLayer.VersionId))
                throw Invalid("LAYER_TYPED_BINDING_INVALID");
            layerBuilder.Add(normalizedLayer);
        }
        var layers = layerBuilder.MoveToImmutable();
        if (layers.Select(layer => layer.Layer)
                .Distinct(StringComparer.Ordinal).Count() != layers.Length ||
            !layers.Select(layer => layer.Ordinal)
                .SequenceEqual(Enumerable.Range(0, layers.Length)) ||
            !layers.Select(layer => layer.Layer).SequenceEqual(
                StatisticReconciliationActualCoherentLayers.RequiredOrder,
                StringComparer.Ordinal))
            throw Invalid("LAYER_SET_OR_ORDER_INVALID");
        RequireTypedBounds(layers.SelectMany(layer => layer.TypedObservations));
        if (!layers.SelectMany(layer => layer.TypedObservations).Any())
            throw Invalid("TYPED_ATOMS_REQUIRED");
        var relational = normalizedContext.RelationalProofBinding!;
        var facts = relational.Facts;
        StatisticReconciliationActualRelationalProofOwner
            .RequirePlanProjections(
                facts, normalizedContext.SummaryPlanBinding);
        if (facts.SummaryPlanBindingSha256 !=
                normalizedContext.SummaryPlanBinding.SemanticSha256 ||
            facts.SourceCaptureSha256 != layers[0].CaptureSemanticSha256 ||
            facts.DirectCaptureSha256 != layers[1].CaptureSemanticSha256 ||
            facts.AggregateCaptureSha256 != layers[2].CaptureSemanticSha256 ||
            facts.BasicCaptureSha256 != layers[3].CaptureSemanticSha256 ||
            facts.AdvancedCaptureSha256 != layers[4].CaptureSemanticSha256 ||
            facts.DiffCaptureSha256 != layers[5].CaptureSemanticSha256 ||
            facts.ApiCaptureSha256 != layers[6].CaptureSemanticSha256 ||
            facts.ExportCaptureSha256 != layers[7].CaptureSemanticSha256)
            throw Invalid("RELATIONAL_PROOF_LAYER_BINDING_MISMATCH");
        var generationId = ExactSha(value.GenerationId, "GENERATION_ID");
        var generationSha = ExactSha(value.GenerationSemanticSha256, "GENERATION_SHA256");
        var summaryMapping =
            StatisticReconciliationActualSummaryMappingManifest.Create(
                normalizedContext.SummaryPlanBinding,
                layers.Select(item => (
                    item.Layer,
                    item.OwnerId,
                    item.VersionId,
                    item.TypedObservationManifestSha256)));
        if (!StringComparer.Ordinal.Equals(
                summaryMapping,
                ExactSha(value.SummaryMappingManifestSha256,
                    "SUMMARY_MAPPING_MANIFEST_SHA256")))
            throw Invalid("SUMMARY_MAPPING_MANIFEST_MISMATCH");

        return value with
        {
            Context = normalizedContext,
            GenerationId = generationId,
            GenerationSemanticSha256 = generationSha,
            SourceSetSha256 = ExactSha(value.SourceSetSha256, "SOURCE_SET_SHA256"),
            InputBindingSha256 = ExactSha(value.InputBindingSha256, "INPUT_BINDING_SHA256"),
            LifecycleSemanticSha256 = ExactSha(value.LifecycleSemanticSha256, "LIFECYCLE_SHA256"),
            ResultPinSetSha256 = ExactSha(value.ResultPinSetSha256, "RESULT_PIN_SET_SHA256"),
            ExportPinSetSha256 = ExactSha(value.ExportPinSetSha256, "EXPORT_PIN_SET_SHA256"),
            CaptureState = Required(value.CaptureState, "CAPTURE_STATE").ToUpperInvariant(),
            Layers = layers,
            SummaryMappingManifestSha256 = summaryMapping
        };
    }

    private static StatisticReconciliationActualPublicationCatalogPins NormalizeCatalog(
        StatisticReconciliationActualPublicationCatalogPins pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var normalized = pins with
        {
            P9CatalogVersion = Required(pins.P9CatalogVersion, "P9_CATALOG_VERSION"),
            P9CatalogRawSha256 = ExactSha(pins.P9CatalogRawSha256, "P9_CATALOG_RAW_SHA256"),
            P9CatalogSemanticSha256 = ExactSha(pins.P9CatalogSemanticSha256, "P9_CATALOG_SEMANTIC_SHA256"),
            P9SchemaRawSha256 = ExactSha(pins.P9SchemaRawSha256, "P9_SCHEMA_RAW_SHA256"),
            P9SchemaSemanticSha256 = ExactSha(pins.P9SchemaSemanticSha256, "P9_SCHEMA_SEMANTIC_SHA256"),
            P9StageLockSha256 = ExactSha(pins.P9StageLockSha256, "P9_STAGE_LOCK_SHA256"),
            CandidateChainId = Required(pins.CandidateChainId, "CANDIDATE_CHAIN_ID"),
            CandidatePromptId = Required(pins.CandidatePromptId, "CANDIDATE_PROMPT_ID"),
            CandidateCatalogVersion = Required(pins.CandidateCatalogVersion, "CANDIDATE_CATALOG_VERSION"),
            CandidateCatalogRawSha256 = ExactSha(pins.CandidateCatalogRawSha256, "CANDIDATE_CATALOG_RAW_SHA256"),
            CandidateCatalogSemanticSha256 = ExactSha(pins.CandidateCatalogSemanticSha256, "CANDIDATE_CATALOG_SEMANTIC_SHA256"),
            CandidateSchemaRawSha256 = ExactSha(pins.CandidateSchemaRawSha256, "CANDIDATE_SCHEMA_RAW_SHA256"),
            CandidateSchemaSemanticSha256 = ExactSha(pins.CandidateSchemaSemanticSha256, "CANDIDATE_SCHEMA_SEMANTIC_SHA256"),
            CandidateStageLockSha256 = ExactSha(pins.CandidateStageLockSha256, "CANDIDATE_STAGE_LOCK_SHA256"),
            CatalogPinSetSha256 = ExactSha(pins.CatalogPinSetSha256, "CATALOG_PIN_SET_SHA256")
        };
        var derived = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CATALOG_PIN_SET_V1",
            normalized.P9CatalogVersion,
            normalized.P9CatalogRawSha256,
            normalized.P9CatalogSemanticSha256,
            normalized.P9SchemaRawSha256,
            normalized.P9SchemaSemanticSha256,
            normalized.P9StageLockSha256,
            normalized.CandidateChainId,
            normalized.CandidatePromptId,
            normalized.CandidateCatalogVersion,
            normalized.CandidateCatalogRawSha256,
            normalized.CandidateCatalogSemanticSha256,
            normalized.CandidateSchemaRawSha256,
            normalized.CandidateSchemaSemanticSha256,
            normalized.CandidateStageLockSha256);
        if (!StringComparer.Ordinal.Equals(derived, normalized.CatalogPinSetSha256))
            throw Invalid("CATALOG_PIN_SET_MISMATCH");
        return normalized;
    }

    private static List<StatisticReconciliationObservation> BuildContent(
        StatisticReconciliationActualPublicationGeneration generation,
        DateTime createdAtUtc)
    {
        var atomCount = generation.Layers.Sum(layer =>
            layer.TypedObservations.Length);
        var content = new List<StatisticReconciliationObservation>(
            generation.Context.ActualSourceDecisions.Length +
            generation.Layers.Length + atomCount);
        content.AddRange(generation.Context.ActualSourceDecisions.Select(value =>
            BuildSourceDecision(generation, value, createdAtUtc)));
        foreach (var layer in generation.Layers)
        {
            content.Add(BuildLayer(generation, layer, createdAtUtc));
            content.AddRange(layer.TypedObservations.Select(item =>
                BuildAtom(generation, layer, item, createdAtUtc)));
        }
        return content;
    }

    private static StatisticReconciliationObservation BuildSourceDecision(
        StatisticReconciliationActualPublicationGeneration generation,
        StatisticReconciliationActualPublishedSourceDecision value,
        DateTime createdAtUtc)
    {
        value = StatisticReconciliationActualPublishedSourceDecision.Normalize(
            value);
        var binding = GenerationBindingSha256(generation, createdAtUtc);
        var documentSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_SOURCE_DECISION_DOCUMENT_V1",
            binding,
            value.SemanticSha256);
        var id = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_ID_V7",
            generation.Context.ReconciliationId,
            generation.GenerationId,
            StatisticReconciliationActualPublicationRecordKinds.SourceDecision,
            StatisticReconciliationActualCanonical.Integer(value.Ordinal),
            value.NeutralStableIdentitySha256);
        var observation = Common(
            generation,
            id,
            StatisticReconciliationActualPublicationRecordKinds.SourceDecision,
            value.SemanticSha256,
            documentSha,
            createdAtUtc);
        observation.ActualSourceDecision =
            new StatisticReconciliationObservationActualSourceDecision
            {
                Ordinal = value.Ordinal,
                WorkId = value.WorkId,
                WorkAssignmentId = value.WorkAssignmentId,
                ReportId = value.ReportId,
                SourceStableIdentitySha256 =
                    value.SourceStableIdentitySha256,
                NeutralStableIdentitySha256 =
                    value.NeutralStableIdentitySha256,
                Included = value.Included,
                OwnerDecisionCode = value.OwnerDecisionCode,
                OwnerOrdinal = value.OwnerOrdinal,
                OwnerStateSemanticSha256 =
                    value.OwnerStateSemanticSha256,
                SourceDecisionSemanticSha256 =
                    value.SourceDecisionSemanticSha256,
                SemanticSha256 = value.SemanticSha256
            };
        return observation;
    }


    private static StatisticReconciliationObservation BuildLayer(
        StatisticReconciliationActualPublicationGeneration generation,
        StatisticReconciliationActualPublicationLayer layer,
        DateTime createdAtUtc)
    {
        var binding = GenerationBindingSha256(generation, createdAtUtc);
        var documentSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_LAYER_DOCUMENT_V7",
            binding,
            StatisticReconciliationActualPublicationRecordKinds.Layer,
            StatisticReconciliationActualCanonical.Integer(layer.Ordinal),
            layer.Layer,
            layer.OwnerId,
            layer.VersionId,
            StatisticReconciliationActualCanonical.Integer(
                layer.ObservationCount),
            layer.CaptureSemanticSha256,
            StatisticReconciliationActualCanonical.Integer(
                layer.TypedObservations.Length),
            layer.TypedObservationManifestSha256,
            layer.SemanticSha256);
        var id = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_ID_V7",
            generation.Context.ReconciliationId,
            generation.GenerationId,
            StatisticReconciliationActualPublicationRecordKinds.Layer,
            layer.Layer,
            layer.OwnerId);
        var observation = Common(
            generation,
            id,
            StatisticReconciliationActualPublicationRecordKinds.Layer,
            layer.TypedObservationManifestSha256,
            documentSha,
            createdAtUtc);
        observation.LineagePin = new StatisticReconciliationObservationLineagePin
        {
            Layer = layer.Layer,
            OwnerId = layer.OwnerId,
            VersionId = layer.VersionId,
            Revision = layer.Ordinal + 1L,
            Sha256 = layer.SemanticSha256
        };
        observation.ActualLayer = new StatisticReconciliationObservationActualLayer
        {
            Ordinal = layer.Ordinal,
            ObservationCount = layer.ObservationCount,
            CaptureSemanticSha256 = layer.CaptureSemanticSha256,
            TypedObservationCount = layer.TypedObservations.Length,
            TypedObservationManifestSha256 =
                layer.TypedObservationManifestSha256,
            LayerSemanticSha256 = layer.SemanticSha256
        };
        return observation;
    }

    private static StatisticReconciliationObservation BuildAtom(
        StatisticReconciliationActualPublicationGeneration generation,
        StatisticReconciliationActualPublicationLayer layer,
        StatisticReconciliationActualTypedObservation typed,
        DateTime createdAtUtc)
    {
        var binding = GenerationBindingSha256(generation, createdAtUtc);
        var documentSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_ATOM_DOCUMENT_V7",
            binding,
            StatisticReconciliationActualCanonical.Integer(typed.Ordinal),
            typed.Layer,
            typed.OwnerId,
            typed.OwnerVersionSha256,
            typed.ProvenanceSha256,
            typed.IdentitySha256,
            typed.AtomKind,
            typed.ValueIdentitySha256,
            typed.AtomSemanticSha256);
        var id = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_ID_V7",
            generation.Context.ReconciliationId,
            generation.GenerationId,
            StatisticReconciliationActualPublicationRecordKinds.Atom,
            typed.Layer,
            StatisticReconciliationActualCanonical.Integer(typed.Ordinal),
            typed.IdentitySha256,
            typed.AtomKind,
            typed.ValueIdentitySha256);
        var observation = Common(
            generation,
            id,
            StatisticReconciliationActualPublicationRecordKinds.Atom,
            typed.AtomSemanticSha256,
            documentSha,
            createdAtUtc);
        observation.LineagePin = new StatisticReconciliationObservationLineagePin
        {
            Layer = typed.Layer,
            OwnerId = typed.OwnerId,
            VersionId = typed.OwnerVersionSha256,
            Revision = typed.Ordinal + 1L,
            Sha256 = typed.ProvenanceSha256
        };
        observation.Atom = ToStoredAtom(typed);
        return observation;
    }

    private static StatisticReconciliationObservationAtom ToStoredAtom(
        StatisticReconciliationActualTypedObservation value)
        => new()
        {
            IdentitySha256 = value.IdentitySha256,
            Family = value.Family,
            Kind = value.Kind,
            MetricId = value.MetricId,
            FieldId = value.FieldId,
            TableId = value.TableId,
            RowId = value.RowId,
            LabelId = value.LabelId,
            BasicScope = value.BasicScope,
            BasicScopeId = value.BasicScopeId,
            FlowBranchId = value.FlowBranchId,
            FlowStepId = value.FlowStepId,
            AdvancedGrain = value.AdvancedGrain,
            DiffKind = value.DiffKind,
            PeriodKey = value.PeriodKey,
            AtomKind = value.AtomKind,
            ValueType = value.ValueType,
            ValueState = value.ValueState,
            CanonicalValue = value.CanonicalValue,
            DecimalScale = value.DecimalScale,
            OccurrenceCount = value.OccurrenceCount,
            ReportCount = value.ReportCount,
            RowCount = value.RowCount,
            NumericValueCount = value.NumericValueCount,
            ValueIdentitySha256 = value.ValueIdentitySha256,
            AtomSemanticSha256 = value.AtomSemanticSha256,
            TransitionLeg = value.TransitionLeg == "NONE"
                ? null
                : value.TransitionLeg,
            TransitionKind = value.TransitionKind,
            CollectionSemantics = value.CollectionSemantics
        };

    private static StatisticReconciliationObservationSummaryIdentityDescriptor
        ToStoredSummaryDescriptor(
            StatisticReconciliationActualSummaryIdentityDescriptor value)
    {
        var normalized =
            StatisticReconciliationActualSummaryIdentityDescriptor.Normalize(
                value);
        return new StatisticReconciliationObservationSummaryIdentityDescriptor
        {
            Family = normalized.Family,
            Kind = normalized.Kind,
            MetricId = normalized.MetricId,
            FieldId = normalized.FieldId,
            TableId = normalized.TableId,
            RowId = normalized.RowId,
            LabelId = normalized.LabelId,
            BasicScope = normalized.BasicScope,
            BasicScopeId = normalized.BasicScopeId,
            AdvancedGrain = normalized.AdvancedGrain,
            DiffKind = normalized.DiffKind,
            PeriodKey = normalized.PeriodKey,
            IdentitySha256 = normalized.IdentitySha256,
            ValueType = normalized.ValueType,
            AtomKinds = normalized.AtomKinds.ToList(),
            SemanticSha256 = normalized.SemanticSha256,
            IdentitySchemaVersion = normalized.IdentitySchemaVersion,
            PlanEntrySchemaVersion = normalized.PlanEntrySchemaVersion,
            JsonPointer = normalized.JsonPointer,
            Unordered = normalized.Unordered,
            ExpandArray = normalized.ExpandArray,
            Operations = normalized.Operations.ToList(),
            TransitionMode = normalized.TransitionMode,
            BeforeJsonPointer = normalized.BeforeJsonPointer,
            AfterJsonPointer = normalized.AfterJsonPointer,
            DifferenceOperation = normalized.DifferenceOperation,
            TransitionKind = normalized.TransitionKind,
            TransitionLegs = normalized.TransitionLegs.ToList(),
            CollectionSemantics = normalized.CollectionSemantics,
            PlanEntrySha256 = normalized.PlanEntrySha256
        };
    }

    private static StatisticReconciliationActualSummaryIdentityDescriptor
        FromStoredSummaryDescriptor(
            StatisticReconciliationObservationSummaryIdentityDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return StatisticReconciliationActualSummaryIdentityDescriptor.Normalize(
            new StatisticReconciliationActualSummaryIdentityDescriptor(
                value.Family,
                value.Kind,
                value.MetricId,
                value.FieldId,
                value.TableId,
                value.RowId,
                value.LabelId,
                value.BasicScope,
                value.BasicScopeId,
                value.AdvancedGrain,
                value.DiffKind,
                value.PeriodKey,
                value.IdentitySha256,
                value.ValueType,
                (value.AtomKinds ?? []).ToImmutableArray(),
                value.SemanticSha256,
                value.IdentitySchemaVersion,
                value.PlanEntrySchemaVersion,
                value.JsonPointer,
                value.Unordered,
                value.ExpandArray,
                (value.Operations ?? []).ToImmutableArray(),
                value.TransitionMode,
                value.BeforeJsonPointer,
                value.AfterJsonPointer,
                value.DifferenceOperation,
                value.TransitionKind,
                (value.TransitionLegs ?? []).ToImmutableArray(),
                value.CollectionSemantics,
                value.PlanEntrySha256));
    }
    internal static StatisticReconciliationObservationActualRelationalProof
        ToStoredRelationalProof(
            StatisticReconciliationActualRelationalProofBinding value)
    {
        value = StatisticReconciliationActualRelationalProofBinding.Normalize(
            value);
        var facts = value.Facts;
        return new StatisticReconciliationObservationActualRelationalProof
        {
            SchemaVersion = value.SchemaVersion,
            FactsSchemaVersion = facts.SchemaVersion,
            SummaryPlanBindingSha256 = facts.SummaryPlanBindingSha256,
            CentralProjectedPlanSha256 = facts.CentralProjectedPlanSha256,
            CentralPlanProjectionProofSha256 =
                facts.CentralPlanProjectionProofSha256,
            SourceCaptureSha256 = facts.SourceCaptureSha256,
            DirectCaptureSha256 = facts.DirectCaptureSha256,
            AggregateCaptureSha256 = facts.AggregateCaptureSha256,
            BasicCaptureSha256 = facts.BasicCaptureSha256,
            AdvancedCaptureSha256 = facts.AdvancedCaptureSha256,
            DiffCaptureSha256 = facts.DiffCaptureSha256,
            ApiCaptureSha256 = facts.ApiCaptureSha256,
            ExportCaptureSha256 = facts.ExportCaptureSha256,
            RawProofSha256 = facts.RawProofSha256,
            RawSourceManifestSha256 = facts.RawSourceManifestSha256,
            RawSourceCount = facts.RawSourceCount,
            RawAtomManifestSha256 = facts.RawAtomManifestSha256,
            RawAtomCount = facts.RawAtomCount,
            RawDoubleCollectProofSha256 = facts.RawDoubleCollectProofSha256,
            SummaryParityProofSha256 = facts.SummaryParityProofSha256,
            SummaryParityRawManifestSha256 =
                facts.SummaryParityRawManifestSha256,
            SummaryParityRawAtomCount = facts.SummaryParityRawAtomCount,
            SummaryParityOwnerManifestSha256 =
                facts.SummaryParityOwnerManifestSha256,
            SummaryParityOwnerItemCount = facts.SummaryParityOwnerItemCount,
            SummaryParityRelationManifestSha256 =
                facts.SummaryParityRelationManifestSha256,
            SummaryParityRelationCount = facts.SummaryParityRelationCount,
            DirectParityProofSha256 = facts.DirectParityProofSha256,
            DirectParityOwnerManifestSha256 =
                facts.DirectParityOwnerManifestSha256,
            DirectParityOwnerItemCount = facts.DirectParityOwnerItemCount,
            DirectParityRelationManifestSha256 =
                facts.DirectParityRelationManifestSha256,
            DirectParityRelationCount = facts.DirectParityRelationCount,
            ExtendedRawResolutionProofSha256 =
                facts.ExtendedRawResolutionProofSha256,
            ExtendedAdvancedProofSha256 = facts.ExtendedAdvancedProofSha256,
            ExtendedAdvancedSchemaOptionBindingSha256 =
                facts.ExtendedAdvancedSchemaOptionBindingSha256,
            ExtendedAdvancedDescriptorProjectionManifestSha256 =
                facts.ExtendedAdvancedDescriptorProjectionManifestSha256,
            ExtendedAdvancedDescriptorProjectionCount =
                facts.ExtendedAdvancedDescriptorProjectionCount,
            ExtendedAdvancedTypedAtomManifestSha256 =
                facts.ExtendedAdvancedTypedAtomManifestSha256,
            ExtendedAdvancedTypedAtomCount =
                facts.ExtendedAdvancedTypedAtomCount,
            ExtendedAdvancedDescriptorContributionManifestSha256 =
                facts.ExtendedAdvancedDescriptorContributionManifestSha256,
            ExtendedAdvancedDescriptorContributionCount =
                facts.ExtendedAdvancedDescriptorContributionCount,
            ExtendedDiffProofSha256 = facts.ExtendedDiffProofSha256,
            ExtendedDiffPeriodBindingSha256 =
                facts.ExtendedDiffPeriodBindingSha256,
            ExtendedDiffTypedAtomManifestSha256 =
                facts.ExtendedDiffTypedAtomManifestSha256,
            ExtendedDiffTypedAtomCount = facts.ExtendedDiffTypedAtomCount,
            ExtendedDiffSourcePairManifestSha256 =
                facts.ExtendedDiffSourcePairManifestSha256,
            ExtendedDiffSourcePairCount = facts.ExtendedDiffSourcePairCount,
            ExtendedPartitionDoubleCollectManifestSha256 =
                facts.ExtendedPartitionDoubleCollectManifestSha256,
            ExtendedPartitionDoubleCollectCount =
                facts.ExtendedPartitionDoubleCollectCount,
            ExtendedOwnerParityProofSha256 =
                facts.ExtendedOwnerParityProofSha256,
            ExtendedOwnerDescriptorManifestSha256 =
                facts.ExtendedOwnerDescriptorManifestSha256,
            ExtendedOwnerDescriptorCount = facts.ExtendedOwnerDescriptorCount,
            ExtendedOwnerTypedAtomManifestSha256 =
                facts.ExtendedOwnerTypedAtomManifestSha256,
            ExtendedOwnerTypedAtomCount = facts.ExtendedOwnerTypedAtomCount,
            ExtendedOwnerItemManifestSha256 =
                facts.ExtendedOwnerItemManifestSha256,
            ExtendedOwnerItemCount = facts.ExtendedOwnerItemCount,
            ExtendedOwnerRelationManifestSha256 =
                facts.ExtendedOwnerRelationManifestSha256,
            ExtendedOwnerRelationCount = facts.ExtendedOwnerRelationCount,
            CrossViewAuthorizationRelationSha256 =
                facts.CrossViewAuthorizationRelationSha256,
            CrossViewResolutionSha256 = facts.CrossViewResolutionSha256,
            CrossViewProofSha256 = facts.CrossViewProofSha256,
            CrossViewCompatibilityProofSha256 =
                facts.CrossViewCompatibilityProofSha256,
            CrossViewOriginalApiSemanticSha256 =
                facts.CrossViewOriginalApiSemanticSha256,
            CrossViewOriginalExportSemanticSha256 =
                facts.CrossViewOriginalExportSemanticSha256,
            FactsSemanticSha256 = facts.SemanticSha256,
            FirstRelationalResolutionSha256 =
                value.FirstRelationalResolutionSha256,
            SecondRelationalResolutionSha256 =
                value.SecondRelationalResolutionSha256,
            DoubleResolveProofSha256 = value.DoubleResolveProofSha256,
            SemanticSha256 = value.SemanticSha256
        };
    }

    internal static StatisticReconciliationActualRelationalProofBinding
        RelationalProofBindingFromStoredDocument(
            StatisticReconciliationObservation document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.RecordKind !=
            StatisticReconciliationActualPublicationRecordKinds.Commit)
            throw Invalid("STORED_RELATIONAL_PROOF_COMMIT_REQUIRED");
        var value = document.ActualRelationalProof ??
            throw Invalid("STORED_RELATIONAL_PROOF_REQUIRED");
        var facts = new StatisticReconciliationActualRelationalProofFacts(
            value.FactsSchemaVersion,
            value.SummaryPlanBindingSha256,
            value.CentralProjectedPlanSha256,
            value.CentralPlanProjectionProofSha256,
            value.SourceCaptureSha256,
            value.DirectCaptureSha256,
            value.AggregateCaptureSha256,
            value.BasicCaptureSha256,
            value.AdvancedCaptureSha256,
            value.DiffCaptureSha256,
            value.ApiCaptureSha256,
            value.ExportCaptureSha256,
            value.RawProofSha256,
            value.RawSourceManifestSha256,
            value.RawSourceCount,
            value.RawAtomManifestSha256,
            value.RawAtomCount,
            value.RawDoubleCollectProofSha256,
            value.SummaryParityProofSha256,
            value.SummaryParityRawManifestSha256,
            value.SummaryParityRawAtomCount,
            value.SummaryParityOwnerManifestSha256,
            value.SummaryParityOwnerItemCount,
            value.SummaryParityRelationManifestSha256,
            value.SummaryParityRelationCount,
            value.DirectParityProofSha256,
            value.DirectParityOwnerManifestSha256,
            value.DirectParityOwnerItemCount,
            value.DirectParityRelationManifestSha256,
            value.DirectParityRelationCount,
            value.ExtendedRawResolutionProofSha256,
            value.ExtendedAdvancedProofSha256,
            value.ExtendedAdvancedSchemaOptionBindingSha256,
            value.ExtendedAdvancedDescriptorProjectionManifestSha256,
            value.ExtendedAdvancedDescriptorProjectionCount,
            value.ExtendedAdvancedTypedAtomManifestSha256,
            value.ExtendedAdvancedTypedAtomCount,
            value.ExtendedAdvancedDescriptorContributionManifestSha256,
            value.ExtendedAdvancedDescriptorContributionCount,
            value.ExtendedDiffProofSha256,
            value.ExtendedDiffPeriodBindingSha256,
            value.ExtendedDiffTypedAtomManifestSha256,
            value.ExtendedDiffTypedAtomCount,
            value.ExtendedDiffSourcePairManifestSha256,
            value.ExtendedDiffSourcePairCount,
            value.ExtendedPartitionDoubleCollectManifestSha256,
            value.ExtendedPartitionDoubleCollectCount,
            value.ExtendedOwnerParityProofSha256,
            value.ExtendedOwnerDescriptorManifestSha256,
            value.ExtendedOwnerDescriptorCount,
            value.ExtendedOwnerTypedAtomManifestSha256,
            value.ExtendedOwnerTypedAtomCount,
            value.ExtendedOwnerItemManifestSha256,
            value.ExtendedOwnerItemCount,
            value.ExtendedOwnerRelationManifestSha256,
            value.ExtendedOwnerRelationCount,
            value.CrossViewAuthorizationRelationSha256,
            value.CrossViewResolutionSha256,
            value.CrossViewProofSha256,
            value.CrossViewCompatibilityProofSha256,
            value.CrossViewOriginalApiSemanticSha256,
            value.CrossViewOriginalExportSemanticSha256,
            value.FactsSemanticSha256);
        var binding =
            StatisticReconciliationActualRelationalProofBinding.Normalize(new(
                value.SchemaVersion,
                facts,
                value.FirstRelationalResolutionSha256,
                value.SecondRelationalResolutionSha256,
                value.DoubleResolveProofSha256,
                value.SemanticSha256));
        if (ExactSha(document.ActualRelationalProofBindingSha256,
                "STORED_RELATIONAL_PROOF_BINDING_SHA256") !=
            binding.SemanticSha256)
            throw Invalid("STORED_RELATIONAL_PROOF_BINDING_MISMATCH");
        return binding;
    }
    private static StatisticReconciliationObservation BuildCommit(
        StatisticReconciliationActualPublicationGeneration generation,
        DateTime createdAtUtc,
        string manifestSha256,
        int layerCount,
        int atomCount,
        int contentCount)
    {
        var documentCount = contentCount + 1;
        var documentSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_COMMIT_V7",
            GenerationBindingSha256(generation, createdAtUtc),
            manifestSha256,
            StatisticReconciliationActualCanonical.Integer(
                generation.Context.ActualSourceDecisions.Length),
            StatisticReconciliationActualCanonical.Integer(
                layerCount + atomCount),
            StatisticReconciliationActualCanonical.Integer(atomCount),
            StatisticReconciliationActualCanonical.Integer(documentCount));
        var id = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_OBSERVATION_ID_V7",
            generation.Context.ReconciliationId,
            generation.GenerationId,
            StatisticReconciliationActualPublicationRecordKinds.Commit);
        var observation = Common(
            generation,
            id,
            StatisticReconciliationActualPublicationRecordKinds.Commit,
            generation.GenerationSemanticSha256,
            documentSha,
            createdAtUtc);
        observation.Commit = new StatisticReconciliationObservationCommit
        {
            SourceDecisionCount =
                generation.Context.ActualSourceDecisions.Length,
            ConfigurationPinCount = 0,
            LineagePinCount = layerCount + atomCount,
            AtomCount = atomCount,
            DocumentCount = documentCount,
            ManifestSha256 = manifestSha256,
            MembershipSemanticSha256 =
                generation.Context.ActualMembershipSemanticSha256,
            SummaryIdentityDescriptors = generation.Context.SummaryPlanBinding
                .IdentityDescriptors.Select(ToStoredSummaryDescriptor).ToList()
        };
        observation.ActualRelationalProof = ToStoredRelationalProof(
            generation.Context.RelationalProofBinding!);
        return observation;
    }
    private static StatisticReconciliationObservation Common(
        StatisticReconciliationActualPublicationGeneration generation,
        string id,
        string recordKind,
        string typedSemanticSha256,
        string documentSha,
        DateTime createdAtUtc)
    {
        var context = generation.Context;
        var pins = context.CatalogPins;
        return new StatisticReconciliationObservation
        {
            Id = id,
            SchemaVersion = SchemaVersion,
            RecordKind = recordKind,
            ReconciliationId = context.ReconciliationId,
            ImmutableIdentitySha256 = context.ImmutableIdentitySha256,
            ImmutableHeaderSha256 = context.ImmutableHeaderSha256,
            GenerationId = generation.GenerationId,
            GenerationSemanticSha256 = generation.GenerationSemanticSha256,
            AlgorithmRevision = AlgorithmRevision,
            AlgorithmSha256 = AlgorithmSha256,
            SourceSetSha256 = generation.SourceSetSha256,
            ActualMembershipSemanticSha256 =
                context.ActualMembershipSemanticSha256,
            ActualSourceDecisionManifestSha256 =
                context.ActualSourceDecisionManifestSha256,
            LifecycleMetricScopeSha256 =
                context.LifecycleMetricScopeSha256,
            TypedSemanticSha256 = typedSemanticSha256,
            MetricPlanSha256 = context.CoherentCatalogPinSetSha256,
            InputBindingSha256 = generation.InputBindingSha256,
            LifecycleSemanticSha256 = generation.LifecycleSemanticSha256,
            ContributionSemanticSha256 = GenerationBindingSha256(
                generation,
                createdAtUtc),
            RecheckCaptureBindingSha256 =
                context.RecheckCaptureBindingSha256,
            CapturedResultPinSetSha256 = generation.ResultPinSetSha256,
            CapturedExportPinSetSha256 = generation.ExportPinSetSha256,
            SummaryExpectedReconciliationId =
                context.SummaryPlanBinding.ExpectedReconciliationId,
            SummaryExpectedGenerationId =
                context.SummaryPlanBinding.ExpectedGenerationId,
            SummaryExpectedGenerationSha256 =
                context.SummaryPlanBinding.ExpectedGenerationSha256,
            SummaryExpectedMetricPlanSha256 =
                context.SummaryPlanBinding.ExpectedMetricPlanSha256,
            SummaryExpectedMetricPlanEntryCount =
                context.SummaryPlanBinding.ExpectedMetricPlanEntryCount,
            SummaryExpectedManifestSha256 =
                context.SummaryPlanBinding.ExpectedManifestSha256,
            SummaryExpectedDocumentCount =
                context.SummaryPlanBinding.ExpectedDocumentCount,
            SummaryExpectedMembershipSemanticSha256 =
                context.SummaryPlanBinding.ExpectedMembershipSemanticSha256,
            SummaryExpectedRuntimeKind =
                context.SummaryPlanBinding.ExpectedRuntimeKind,
            SummaryExpectedIdentitySetSha256 =
                context.SummaryPlanBinding.ExpectedIdentitySetSha256,
            SummaryMetricIdentityCount =
                context.SummaryPlanBinding.MetricIdentityCount,
            SummaryPlanBindingSha256 =
                context.SummaryPlanBinding.SemanticSha256,
            SummaryMappingManifestSha256 =
                generation.SummaryMappingManifestSha256,
            LifecycleManifestSha256 = context.LifecycleManifestSha256,
            LifecycleObservationCount = context.LifecycleObservationCount,
            ActualRelationalProofBindingSha256 =
                context.RelationalProofBinding!.SemanticSha256,
            WorkId = context.WorkId,
            ScopeAssignmentId = context.ScopeAssignmentId,
            PeriodKey = context.PeriodKey,
            PeriodInstanceKey = context.PeriodInstanceKey,
            ConceptKey = context.ConceptKey,
            Grain = context.Grain,
            TimeAxis = context.TimeAxis,
            FilterSha256 = context.FilterSha256,
            DynamicFormVersionId = context.DynamicFormVersionId,
            DynamicFormSchemaSha256 = context.DynamicFormSchemaSha256,
            RuntimeKind = context.RuntimeKind,
            FlowTemplateVersionId = context.FlowTemplateVersionId,
            FlowPayloadSha256 = context.FlowPayloadSha256,
            FlowInstanceId = context.FlowInstanceId,
            ExecutionEpochId = context.ExecutionEpochId,
            ExecutionEpoch = context.ExecutionEpoch,
            ExecutionEpochRevision = context.ExecutionEpochRevision,
            P8ConfigurationOwnerId = context.P8ConfigurationOwnerId,
            P8ConfigurationBundleSha256 = context.P8ConfigurationBundleSha256,
            ActualConfigurationBundleSha256 =
                context.ActualConfigurationBundleSha256,
            CatalogPins = new StatisticReconciliationObservationCatalogPins
            {
                P9CatalogVersion = pins.P9CatalogVersion,
                P9CatalogRawSha256 = pins.P9CatalogRawSha256,
                P9CatalogSemanticSha256 = pins.P9CatalogSemanticSha256,
                P9SchemaRawSha256 = pins.P9SchemaRawSha256,
                P9SchemaSemanticSha256 = pins.P9SchemaSemanticSha256,
                P9StageLockSha256 = pins.P9StageLockSha256,
                CandidateChainId = pins.CandidateChainId,
                CandidatePromptId = pins.CandidatePromptId,
                CandidateCatalogVersion = pins.CandidateCatalogVersion,
                CandidateCatalogRawSha256 = pins.CandidateCatalogRawSha256,
                CandidateCatalogSemanticSha256 = pins.CandidateCatalogSemanticSha256,
                CandidateSchemaRawSha256 = pins.CandidateSchemaRawSha256,
                CandidateSchemaSemanticSha256 = pins.CandidateSchemaSemanticSha256,
                CandidateStageLockSha256 = pins.CandidateStageLockSha256,
                CatalogPinSetSha256 = pins.CatalogPinSetSha256
            },
            DocumentSemanticSha256 = documentSha,
            CreatedAtUtc = createdAtUtc
        };
    }

    private static DateTime ResolveCreatedAtUtc(
        IReadOnlyList<StatisticReconciliationObservation> existing,
        DateTime requestedCreatedAtUtc)
    {
        if (requestedCreatedAtUtc.Kind != DateTimeKind.Utc ||
            requestedCreatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw Invalid("CREATED_AT_NOT_CANONICAL_UTC_MILLISECOND");
        if (existing.Count == 0)
            return requestedCreatedAtUtc;
        var timestamps = existing.Select(item => item.CreatedAtUtc)
            .Distinct().ToArray();
        if (timestamps.Length != 1 || timestamps[0].Kind != DateTimeKind.Utc ||
            timestamps[0].Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw Invalid("STORED_CREATED_AT_INCONSISTENT");
        return timestamps[0];
    }

    private static void ValidateExisting(
        IReadOnlyList<StatisticReconciliationObservation> actual,
        IReadOnlyDictionary<string, StatisticReconciliationObservation> expected,
        bool allowIncomplete)
    {
        if (actual.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != actual.Count)
            throw Invalid("STORED_ID_DUPLICATE");
        foreach (var item in actual)
        {
            ValidateStoredDocument(item);
            if (!expected.TryGetValue(item.Id, out var match) ||
                !StringComparer.Ordinal.Equals(item.RecordKind, match.RecordKind) ||
                !StringComparer.Ordinal.Equals(
                    item.DocumentSemanticSha256,
                    match.DocumentSemanticSha256))
                throw Invalid("IMMUTABLE_REPLAY_CONFLICT");
        }
        if (!allowIncomplete && actual.Count != expected.Count)
            throw Invalid("GENERATION_INCOMPLETE");
    }

    private static void EnsureAllContent(
        IReadOnlyList<StatisticReconciliationObservation> content,
        IReadOnlyList<StatisticReconciliationObservation> actual)
    {
        var ids = actual.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (content.Any(item => !ids.Contains(item.Id)))
            throw Invalid("CONTENT_APPEND_INCOMPLETE");
    }

    private static bool HasCommit(
        IEnumerable<StatisticReconciliationObservation> documents)
        => documents.Any(item =>
            item.RecordKind == StatisticReconciliationActualPublicationRecordKinds.Commit);

    private static void ValidateComplete(
        IReadOnlyList<StatisticReconciliationObservation> documents,
        string manifestSha256,
        int documentCount)
    {
        if (documents.Count == 0 || documents.Count > MaximumDocumentCount ||
            documents.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() != documents.Count)
            throw Invalid("ACTUAL_GENERATION_DOCUMENT_SET_INVALID");

        var bindings = documents.Select(ValidateStoredDocument)
            .Distinct(StringComparer.Ordinal).ToArray();
        var commits = documents.Where(item =>
                item.RecordKind == StatisticReconciliationActualPublicationRecordKinds.Commit)
            .ToArray();
        var layers = documents.Where(item =>
                item.RecordKind == StatisticReconciliationActualPublicationRecordKinds.Layer)
            .OrderBy(item => item.ActualLayer!.Ordinal)
            .ToArray();
        var atoms = ExtractTypedObservations(documents);
        var sourceDecisions = NormalizeSourceDecisions(
            ExtractSourceDecisions(documents));
        var sourceDecisionManifest =
            SourceDecisionManifestSha256(sourceDecisions);
        var membershipSemantic = MembershipSemanticSha256(sourceDecisions);
        RequireTypedBounds(atoms);
        if (bindings.Length != 1 || commits.Length != 1 ||
            documents.Count != documentCount ||
            documentCount != sourceDecisions.Length + layers.Length +
                atoms.Length + 1 ||
            layers.Length !=
            StatisticReconciliationActualCoherentLayers.RequiredOrder.Length ||
            atoms.Length < 1 ||
            commits[0].Commit?.DocumentCount != documentCount ||
            commits[0].Commit?.SourceDecisionCount != sourceDecisions.Length ||
            commits[0].Commit?.ConfigurationPinCount != 0 ||
            commits[0].Commit?.LineagePinCount !=
                layers.Length + atoms.Length ||
            commits[0].Commit?.AtomCount != atoms.Length ||
            !StringComparer.Ordinal.Equals(
                commits[0].Commit?.ManifestSha256,
                manifestSha256) ||
            !StringComparer.Ordinal.Equals(
                commits[0].Commit?.MembershipSemanticSha256,
                membershipSemantic) ||
            !StringComparer.Ordinal.Equals(
                commits[0].ActualMembershipSemanticSha256,
                membershipSemantic) ||
            !StringComparer.Ordinal.Equals(
                commits[0].ActualSourceDecisionManifestSha256,
                sourceDecisionManifest) ||
            !layers.Select(item => item.ActualLayer!.Ordinal)
                .SequenceEqual(Enumerable.Range(0, layers.Length)) ||
            !layers.Select(item => item.LineagePin!.Layer)
                .SequenceEqual(
                    StatisticReconciliationActualCoherentLayers.RequiredOrder,
                    StringComparer.Ordinal) ||
            documents.Any(item => item.RecordKind is not (
                StatisticReconciliationActualPublicationRecordKinds.SourceDecision or
                StatisticReconciliationActualPublicationRecordKinds.Layer or
                StatisticReconciliationActualPublicationRecordKinds.Atom or
                StatisticReconciliationActualPublicationRecordKinds.Commit)))
            throw Invalid("COMMIT_INTEGRITY_INVALID");

        var lifecycleMetricScope = ExactSha(
            commits[0].LifecycleMetricScopeSha256,
            "STORED_LIFECYCLE_METRIC_SCOPE_SHA256");

        foreach (var layerDocument in layers)
        {
            var layer = layerDocument.LineagePin!.Layer;
            var layerAtoms = StatisticReconciliationActualTypedObservationCanonical
                .NormalizeSet(atoms.Where(item => item.Layer == layer));
            var typedManifest =
                StatisticReconciliationActualTypedObservationCanonical
                    .ManifestSha256(layerAtoms);
            if (layerAtoms.Any(item =>
                    item.OwnerId != layerDocument.LineagePin!.OwnerId ||
                    item.OwnerVersionSha256 !=
                        layerDocument.LineagePin.VersionId) ||
                layerDocument.ActualLayer!.TypedObservationCount !=
                    layerAtoms.Length ||
                !StringComparer.Ordinal.Equals(
                    layerDocument.ActualLayer.TypedObservationManifestSha256,
                    typedManifest) ||
                !StringComparer.Ordinal.Equals(
                    layerDocument.TypedSemanticSha256,
                    typedManifest))
                throw Invalid("LAYER_TYPED_MANIFEST_MISMATCH");
        }

        var reconciliationIds = documents.Select(item => item.ReconciliationId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var generationIds = documents.Select(item => item.GenerationId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var generationHashes = documents
            .Select(item => item.GenerationSemanticSha256)
            .Distinct(StringComparer.Ordinal).ToArray();
        var inputBindings = documents.Select(item => item.InputBindingSha256)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (reconciliationIds.Length != 1 || generationIds.Length != 1 ||
            generationHashes.Length != 1 || inputBindings.Length != 1)
            throw Invalid("GENERATION_BINDING_CARDINALITY_INVALID");

        var layerManifestSha256 = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_COHERENT_LAYER_MANIFEST_V2",
            layers.Select(item => item.ActualLayer!.LayerSemanticSha256));
        var coherentGenerationSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_COHERENT_GENERATION_V2",
            reconciliationIds[0],
            inputBindings[0],
            inputBindings[0],
            layerManifestSha256,
            StatisticReconciliationActualPublicationStates.Ready,
            null,
            StatisticReconciliationActualCanonical.Boolean(true),
            StatisticReconciliationActualCanonical.Boolean(true));
        var storedSummaryPlan = SummaryPlanBindingFromStoredDocument(commits[0]);
        var storedSummaryMapping = ExactSha(
            commits[0].SummaryMappingManifestSha256,
            "STORED_SUMMARY_MAPPING_MANIFEST_SHA256");
        var recomputedSummaryMapping =
            StatisticReconciliationActualSummaryMappingManifest.Create(
                storedSummaryPlan,
                layers.Select(item => (
                    item.LineagePin!.Layer,
                    item.LineagePin.OwnerId,
                    item.LineagePin.VersionId,
                    item.ActualLayer!.TypedObservationManifestSha256)));
        if (storedSummaryMapping != recomputedSummaryMapping)
            throw Invalid("STORED_SUMMARY_MAPPING_MANIFEST_MISMATCH");
        var lifecycleManifest = OptionalSha(
            commits[0].LifecycleManifestSha256,
            "STORED_LIFECYCLE_MANIFEST_SHA256");
        var lifecycleCount = commits[0].LifecycleObservationCount;
        if ((lifecycleManifest is null) != (lifecycleCount is null) ||
            (lifecycleManifest is not null && lifecycleCount is not > 0))
            throw Invalid("STORED_LIFECYCLE_MANIFEST_BINDING_INVALID");
        var storedRelational =
            RelationalProofBindingFromStoredDocument(commits[0]);
        var storedFacts = storedRelational.Facts;
        StatisticReconciliationActualRelationalProofOwner
            .RequirePlanProjections(storedFacts, storedSummaryPlan);
        if (storedFacts.SummaryPlanBindingSha256 !=
                storedSummaryPlan.SemanticSha256 ||
            storedFacts.SourceCaptureSha256 !=
                layers[0].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.DirectCaptureSha256 !=
                layers[1].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.AggregateCaptureSha256 !=
                layers[2].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.BasicCaptureSha256 !=
                layers[3].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.AdvancedCaptureSha256 !=
                layers[4].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.DiffCaptureSha256 !=
                layers[5].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.ApiCaptureSha256 !=
                layers[6].ActualLayer!.CaptureSemanticSha256 ||
            storedFacts.ExportCaptureSha256 !=
                layers[7].ActualLayer!.CaptureSemanticSha256)
            throw Invalid("STORED_RELATIONAL_PROOF_LAYER_MISMATCH");
        var derivedGenerationSha256 =
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_COMMITTED_GENERATION_V7",
                coherentGenerationSha256,
                commits[0].RecheckCaptureBindingSha256 is null
                    ? "~"
                    : ExactSha(commits[0].RecheckCaptureBindingSha256,
                        "STORED_RECHECK_CAPTURE_BINDING_SHA256"),
                Required(commits[0].RuntimeKind, "STORED_RUNTIME_KIND")
                    .ToUpperInvariant(),
                membershipSemantic,
                sourceDecisionManifest,
                StatisticReconciliationActualCanonical.Integer(
                    sourceDecisions.Length),
                lifecycleMetricScope,
                storedSummaryPlan.SemanticSha256,
                storedSummaryMapping,
                lifecycleManifest ?? "~",
                lifecycleCount?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "~",
                storedRelational.SemanticSha256);
        var derivedGenerationId =
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_COMMITTED_GENERATION_ID_V7",
                reconciliationIds[0],
                derivedGenerationSha256);
        if (!StringComparer.Ordinal.Equals(
                derivedGenerationSha256,
                generationHashes[0]) ||
            !StringComparer.Ordinal.Equals(derivedGenerationId, generationIds[0]))
            throw Invalid("COHERENT_GENERATION_RECOMPUTE_MISMATCH");

        var derivedManifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_OBSERVATION_MANIFEST_V7",
            documents.Where(item =>
                    item.RecordKind !=
                    StatisticReconciliationActualPublicationRecordKinds.Commit)
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.DocumentSemanticSha256));
        if (!StringComparer.Ordinal.Equals(derivedManifest, manifestSha256))
            throw Invalid("MANIFEST_MISMATCH");
    }

    private static string ValidateStoredDocument(
        StatisticReconciliationObservation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (StringComparer.Ordinal.Equals(
                item.SchemaVersion, LegacySchemaVersionV6) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmRevision, LegacyAlgorithmRevisionV6) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmSha256, LegacyAlgorithmSha256V6))
            throw Invalid("LEGACY_V6_NOT_RELATIONAL_SIGNABLE");
        if (StringComparer.Ordinal.Equals(
                item.SchemaVersion, LegacySchemaVersion) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmRevision, LegacyAlgorithmRevision) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmSha256, LegacyAlgorithmSha256))
            throw Invalid("LEGACY_V5_NOT_FULL_SCOPE_SIGNABLE");
        if (StringComparer.Ordinal.Equals(
                item.SchemaVersion, LegacySchemaVersionV4) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmRevision, LegacyAlgorithmRevisionV4) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmSha256, LegacyAlgorithmSha256V4))
            throw Invalid("LEGACY_V4_NOT_FULL_SCOPE_SIGNABLE");
        if (StringComparer.Ordinal.Equals(
                item.SchemaVersion, LegacySchemaVersionV3) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmRevision, LegacyAlgorithmRevisionV3) &&
            StringComparer.Ordinal.Equals(
                item.AlgorithmSha256, LegacyAlgorithmSha256V3))
            throw Invalid("LEGACY_V3_NOT_FULL_SCOPE_SIGNABLE");
        if (!StringComparer.Ordinal.Equals(item.SchemaVersion, SchemaVersion) ||
            !StringComparer.Ordinal.Equals(item.AlgorithmRevision, AlgorithmRevision) ||
            !StringComparer.Ordinal.Equals(item.AlgorithmSha256, AlgorithmSha256) ||
            item.TenantUnitId is not null ||
            item.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            item.CreatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw Invalid("ACTUAL_DOCUMENT_COMMON_INVALID");

        if ((item.RecordKind ==
                StatisticReconciliationActualPublicationRecordKinds.Commit) !=
            (item.ActualRelationalProof is not null))
            throw Invalid("ACTUAL_RELATIONAL_PROOF_PAYLOAD_INVALID");
        var binding = GenerationBindingSha256(item);
        if (!StringComparer.Ordinal.Equals(
                binding,
                item.ContributionSemanticSha256))
            throw Invalid("ACTUAL_DOCUMENT_BINDING_MISMATCH");

        string expectedId;
        string expectedDocumentSha256;
        if (item.RecordKind ==
                StatisticReconciliationActualPublicationRecordKinds.SourceDecision)
        {
            if (item.ActualSourceDecision is null ||
                item.LineagePin is not null || item.ActualLayer is not null ||
                item.Atom is not null || item.Commit is not null ||
                item.SourceDecision is not null ||
                item.ConfigurationPin is not null)
                throw Invalid("ACTUAL_SOURCE_DECISION_PAYLOAD_INVALID");
            var sourceDecision = FromStoredSourceDecision(item);
            if (item.TypedSemanticSha256 != sourceDecision.SemanticSha256)
                throw Invalid("ACTUAL_SOURCE_DECISION_TYPED_MISMATCH");
            expectedId = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_ID_V7",
                item.ReconciliationId,
                item.GenerationId,
                StatisticReconciliationActualPublicationRecordKinds
                    .SourceDecision,
                StatisticReconciliationActualCanonical.Integer(
                    sourceDecision.Ordinal),
                sourceDecision.NeutralStableIdentitySha256);
            expectedDocumentSha256 =
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_OBSERVATION_SOURCE_DECISION_DOCUMENT_V1",
                    binding,
                    sourceDecision.SemanticSha256);
        }
        else if (item.RecordKind == StatisticReconciliationActualPublicationRecordKinds.Layer)
        {
            var lineage = item.LineagePin
                ?? throw Invalid("ACTUAL_LAYER_LINEAGE_REQUIRED");
            var actualLayer = item.ActualLayer
                ?? throw Invalid("ACTUAL_LAYER_PAYLOAD_REQUIRED");
            if (item.Commit is not null || item.SourceDecision is not null ||
                item.ActualSourceDecision is not null ||
                item.ConfigurationPin is not null || item.Atom is not null ||
                actualLayer.Ordinal < 0 || actualLayer.ObservationCount < 0 ||
                actualLayer.TypedObservationCount < 0 ||
                lineage.Revision != actualLayer.Ordinal + 1L ||
                actualLayer.Ordinal >=
                StatisticReconciliationActualCoherentLayers.RequiredOrder.Length)
                throw Invalid("ACTUAL_LAYER_PAYLOAD_INVALID");
            var layer = Required(lineage.Layer, "STORED_LAYER");
            var ownerId = Required(lineage.OwnerId, "STORED_LAYER_OWNER_ID");
            var versionId = ExactSha(
                lineage.VersionId,
                "STORED_LAYER_VERSION_ID");
            var captureSha256 = ExactSha(
                actualLayer.CaptureSemanticSha256,
                "STORED_LAYER_CAPTURE_SHA256");
            var typedManifest = ExactSha(
                actualLayer.TypedObservationManifestSha256,
                "STORED_LAYER_TYPED_MANIFEST_SHA256");
            var semanticSha256 = ExactSha(
                actualLayer.LayerSemanticSha256,
                "STORED_LAYER_SEMANTIC_SHA256");
            if (layer != StatisticReconciliationActualCoherentLayers.RequiredOrder[
                    actualLayer.Ordinal])
                throw Invalid("ACTUAL_LAYER_ORDINAL_MISMATCH");
            var recomputedLayerSemantic =
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_COHERENT_LAYER_V2",
                    StatisticReconciliationActualCanonical.Integer(
                        actualLayer.Ordinal),
                    layer,
                    item.InputBindingSha256,
                    item.InputBindingSha256,
                    captureSha256,
                    StatisticReconciliationActualCanonical.Integer(
                        actualLayer.ObservationCount),
                    StatisticReconciliationActualCanonical.Integer(
                        actualLayer.TypedObservationCount),
                    StatisticReconciliationActualPublicationStates.Ready,
                    null,
                    StatisticReconciliationActualCanonical.Boolean(true),
                    ownerId,
                    versionId,
                    typedManifest);
            if (lineage.Sha256 != semanticSha256 ||
                item.TypedSemanticSha256 != typedManifest ||
                recomputedLayerSemantic != semanticSha256)
                throw Invalid("ACTUAL_LAYER_SEMANTIC_MISMATCH");
            expectedId = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_ID_V7",
                item.ReconciliationId,
                item.GenerationId,
                StatisticReconciliationActualPublicationRecordKinds.Layer,
                layer,
                ownerId);
            expectedDocumentSha256 = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_LAYER_DOCUMENT_V7",
                binding,
                StatisticReconciliationActualPublicationRecordKinds.Layer,
                StatisticReconciliationActualCanonical.Integer(
                    actualLayer.Ordinal),
                layer,
                ownerId,
                versionId,
                StatisticReconciliationActualCanonical.Integer(
                    actualLayer.ObservationCount),
                captureSha256,
                StatisticReconciliationActualCanonical.Integer(
                    actualLayer.TypedObservationCount),
                typedManifest,
                semanticSha256);
        }
        else if (item.RecordKind ==
                 StatisticReconciliationActualPublicationRecordKinds.Atom)
        {
            if (item.ActualLayer is not null || item.Commit is not null ||
                item.SourceDecision is not null ||
                item.ActualSourceDecision is not null ||
                item.ConfigurationPin is not null)
                throw Invalid("ACTUAL_ATOM_PAYLOAD_INVALID");
            var typed = FromStoredAtom(item);
            if (item.TypedSemanticSha256 != typed.AtomSemanticSha256)
                throw Invalid("ACTUAL_ATOM_TYPED_SEMANTIC_MISMATCH");
            expectedId = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_ID_V7",
                item.ReconciliationId,
                item.GenerationId,
                StatisticReconciliationActualPublicationRecordKinds.Atom,
                typed.Layer,
                StatisticReconciliationActualCanonical.Integer(typed.Ordinal),
                typed.IdentitySha256,
                typed.AtomKind,
                typed.ValueIdentitySha256);
            expectedDocumentSha256 = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_ATOM_DOCUMENT_V7",
                binding,
                StatisticReconciliationActualCanonical.Integer(typed.Ordinal),
                typed.Layer,
                typed.OwnerId,
                typed.OwnerVersionSha256,
                typed.ProvenanceSha256,
                typed.IdentitySha256,
                typed.AtomKind,
                typed.ValueIdentitySha256,
                typed.AtomSemanticSha256);
        }
        else if (item.RecordKind ==
                 StatisticReconciliationActualPublicationRecordKinds.Commit)
        {
            var commit = item.Commit
                ?? throw Invalid("ACTUAL_COMMIT_PAYLOAD_REQUIRED");
            if (item.LineagePin is not null || item.ActualLayer is not null ||
                item.SourceDecision is not null ||
                item.ActualSourceDecision is not null ||
                item.ConfigurationPin is not null || item.Atom is not null ||
                commit.DocumentCount < 1 || commit.SourceDecisionCount < 0 ||
                commit.LineagePinCount < 0 || commit.AtomCount < 0 ||
                item.TypedSemanticSha256 != item.GenerationSemanticSha256)
                throw Invalid("ACTUAL_COMMIT_PAYLOAD_INVALID");
            _ = RelationalProofBindingFromStoredDocument(item);
            var storedManifest = ExactSha(
                commit.ManifestSha256,
                "STORED_MANIFEST_SHA256");
            expectedId = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_ID_V7",
                item.ReconciliationId,
                item.GenerationId,
                StatisticReconciliationActualPublicationRecordKinds.Commit);
            expectedDocumentSha256 = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_OBSERVATION_COMMIT_V7",
                binding,
                storedManifest,
                StatisticReconciliationActualCanonical.Integer(
                    commit.SourceDecisionCount),
                StatisticReconciliationActualCanonical.Integer(
                    commit.LineagePinCount),
                StatisticReconciliationActualCanonical.Integer(
                    commit.AtomCount),
                StatisticReconciliationActualCanonical.Integer(
                    commit.DocumentCount));
        }
        else
        {
            throw Invalid("ACTUAL_RECORD_KIND_INVALID");
        }

        if (!StringComparer.Ordinal.Equals(item.Id, expectedId) ||
            !StringComparer.Ordinal.Equals(
                item.DocumentSemanticSha256,
                expectedDocumentSha256))
            throw Invalid("ACTUAL_DOCUMENT_SEMANTIC_MISMATCH");
        return binding;
    }

    private static StatisticReconciliationActualTypedObservation FromStoredAtom(
        StatisticReconciliationObservation item)
    {
        var lineage = item.LineagePin
            ?? throw Invalid("ACTUAL_ATOM_LINEAGE_REQUIRED");
        var atom = item.Atom
            ?? throw Invalid("ACTUAL_ATOM_VALUE_REQUIRED");
        if (lineage.Revision < 1 || lineage.Revision > int.MaxValue)
            throw Invalid("ACTUAL_ATOM_ORDINAL_INVALID");
        var stored = new StatisticReconciliationActualTypedObservation(
            checked((int)lineage.Revision - 1),
            lineage.Layer,
            lineage.OwnerId,
            lineage.VersionId,
            lineage.Sha256,
            atom.IdentitySha256,
            atom.Family,
            atom.Kind,
            atom.MetricId,
            atom.FieldId,
            atom.TableId,
            atom.RowId,
            atom.LabelId,
            atom.BasicScope,
            atom.BasicScopeId,
            atom.FlowBranchId,
            atom.FlowStepId,
            atom.AdvancedGrain,
            atom.DiffKind,
            atom.PeriodKey ?? throw Invalid("ACTUAL_ATOM_PERIOD_REQUIRED"),
            atom.AtomKind,
            atom.ValueType,
            atom.ValueState,
            atom.CanonicalValue,
            atom.DecimalScale,
            atom.OccurrenceCount,
            atom.ReportCount,
            atom.RowCount,
            atom.NumericValueCount,
            atom.ValueIdentitySha256,
            atom.AtomSemanticSha256,
            atom.TransitionLeg ?? "NONE",
            atom.TransitionKind,
            atom.CollectionSemantics);
        return StatisticReconciliationActualTypedObservationCanonical.Normalize(
            stored);
    }

    private static ImmutableArray<StatisticReconciliationActualTypedObservation>
        ExtractTypedObservations(
            IEnumerable<StatisticReconciliationObservation> documents)
        => documents.Where(item =>
                item.RecordKind ==
                StatisticReconciliationActualPublicationRecordKinds.Atom)
            .Select(FromStoredAtom)
            .OrderBy(item =>
                StatisticReconciliationActualCoherentLayers.RequiredOrder
                    .IndexOf(item.Layer))
            .ThenBy(item => item.Ordinal)
            .ToImmutableArray();

    private static void RequireTypedBounds(
        IEnumerable<StatisticReconciliationActualTypedObservation> values)
    {
        var count = 0;
        long bytes = 0;
        foreach (var value in values)
        {
            count++;
            if (count > MaximumTypedAtomCount)
                throw Invalid("TYPED_ATOM_COUNT_LIMIT_EXCEEDED");
            bytes += Encoding.UTF8.GetByteCount(value.CanonicalValue);
            if (bytes > MaximumTypedCanonicalBytes)
                throw Invalid("TYPED_ATOM_BYTES_LIMIT_EXCEEDED");
        }
    }
    private static string GenerationBindingSha256(
        StatisticReconciliationObservation item)
    {
        ArgumentNullException.ThrowIfNull(item.CatalogPins);
        var pins = NormalizeCatalog(new StatisticReconciliationActualPublicationCatalogPins(
            item.CatalogPins.P9CatalogVersion,
            item.CatalogPins.P9CatalogRawSha256,
            item.CatalogPins.P9CatalogSemanticSha256,
            item.CatalogPins.P9SchemaRawSha256,
            item.CatalogPins.P9SchemaSemanticSha256,
            item.CatalogPins.P9StageLockSha256,
            item.CatalogPins.CandidateChainId,
            item.CatalogPins.CandidatePromptId,
            item.CatalogPins.CandidateCatalogVersion,
            item.CatalogPins.CandidateCatalogRawSha256,
            item.CatalogPins.CandidateCatalogSemanticSha256,
            item.CatalogPins.CandidateSchemaRawSha256,
            item.CatalogPins.CandidateSchemaSemanticSha256,
            item.CatalogPins.CandidateStageLockSha256,
            item.CatalogPins.CatalogPinSetSha256));
        var runtimeKind = Required(item.RuntimeKind, "STORED_RUNTIME_KIND")
            .ToUpperInvariant();
        string flowVersion;
        string flowPayload;
        string flowInstance;
        string executionEpochId;
        string executionEpoch;
        string executionEpochRevision;
        if (runtimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            if (item.ExecutionEpoch is not > 0 ||
                item.ExecutionEpochRevision is not > 0)
                throw Invalid("STORED_EXECUTION_EPOCH_INVALID");
            flowVersion = Required(
                item.FlowTemplateVersionId, "STORED_FLOW_VERSION_ID");
            flowPayload = ExactSha(
                item.FlowPayloadSha256, "STORED_FLOW_PAYLOAD_SHA256");
            flowInstance = Required(
                item.FlowInstanceId, "STORED_FLOW_INSTANCE_ID");
            executionEpochId = Required(
                item.ExecutionEpochId, "STORED_EXECUTION_EPOCH_ID");
            executionEpoch = StatisticReconciliationActualCanonical.Integer(
                item.ExecutionEpoch.Value);
            executionEpochRevision =
                StatisticReconciliationActualCanonical.Integer(
                    item.ExecutionEpochRevision.Value);
        }
        else if (runtimeKind ==
                     StatisticReconciliationExpectedRuntimeKinds.NonFlow &&
                 item.FlowTemplateVersionId is null &&
                 item.FlowPayloadSha256 is null &&
                 item.FlowInstanceId is null && item.ExecutionEpochId is null &&
                 item.ExecutionEpoch is null &&
                 item.ExecutionEpochRevision is null)
        {
            flowVersion = flowPayload = flowInstance = executionEpochId =
                executionEpoch = executionEpochRevision = "~";
        }
        else
        {
            throw Invalid("STORED_RUNTIME_BINDING_INVALID");
        }
        return StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_PUBLICATION_GENERATION_BINDING_V7",
            Required(item.ReconciliationId, "STORED_RECONCILIATION_ID"),
            ExactSha(item.ImmutableIdentitySha256, "STORED_IMMUTABLE_IDENTITY_SHA256"),
            ExactSha(item.ImmutableHeaderSha256, "STORED_IMMUTABLE_HEADER_SHA256"),
            Required(item.WorkId, "STORED_WORK_ID"),
            Required(item.ScopeAssignmentId, "STORED_SCOPE_ASSIGNMENT_ID"),
            Required(item.PeriodKey, "STORED_PERIOD_KEY"),
            Required(item.PeriodInstanceKey, "STORED_PERIOD_INSTANCE_KEY"),
            Required(item.ConceptKey, "STORED_CONCEPT_KEY"),
            Required(item.Grain, "STORED_GRAIN"),
            Required(item.TimeAxis, "STORED_TIME_AXIS"),
            ExactSha(item.FilterSha256, "STORED_FILTER_SHA256"),
            Required(item.DynamicFormVersionId, "STORED_FORM_VERSION_ID"),
            ExactSha(item.DynamicFormSchemaSha256, "STORED_FORM_SCHEMA_SHA256"),
            runtimeKind,
            flowVersion,
            flowPayload,
            flowInstance,
            executionEpochId,
            executionEpoch,
            executionEpochRevision,
            Required(item.P8ConfigurationOwnerId, "STORED_P8_OWNER_ID"),
            ExactSha(item.P8ConfigurationBundleSha256, "STORED_P8_BUNDLE_SHA256"),
            ExactSha(
                item.ActualConfigurationBundleSha256,
                "STORED_ACTUAL_CONFIGURATION_BUNDLE_SHA256"),
            OptionalSha(item.RecheckCaptureBindingSha256,
                "STORED_RECHECK_CAPTURE_BINDING_SHA256") ?? "~",
            ExactSha(item.MetricPlanSha256, "STORED_COHERENT_CATALOG_PIN_SET_SHA256"),
            pins.CatalogPinSetSha256,
            ExactSha(item.GenerationId, "STORED_GENERATION_ID"),
            ExactSha(item.GenerationSemanticSha256, "STORED_GENERATION_SHA256"),
            ExactSha(item.SourceSetSha256, "STORED_SOURCE_SET_SHA256"),
            ExactSha(item.ActualMembershipSemanticSha256,
                "STORED_ACTUAL_MEMBERSHIP_SEMANTIC_SHA256"),
            ExactSha(item.ActualSourceDecisionManifestSha256,
                "STORED_ACTUAL_SOURCE_DECISION_MANIFEST_SHA256"),
            ExactSha(item.LifecycleMetricScopeSha256,
                "STORED_LIFECYCLE_METRIC_SCOPE_SHA256"),
            ExactSha(item.InputBindingSha256, "STORED_INPUT_BINDING_SHA256"),
            ExactSha(item.LifecycleSemanticSha256, "STORED_LIFECYCLE_SHA256"),
            ExactSha(item.CapturedResultPinSetSha256,
                "STORED_RESULT_PIN_SET_SHA256"),
            ExactSha(item.CapturedExportPinSetSha256,
                "STORED_EXPORT_PIN_SET_SHA256"),
            Required(item.SummaryExpectedReconciliationId,
                "STORED_SUMMARY_EXPECTED_RECONCILIATION_ID"),
            ExactSha(item.SummaryExpectedGenerationId,
                "STORED_SUMMARY_EXPECTED_GENERATION_ID"),
            ExactSha(item.SummaryExpectedGenerationSha256,
                "STORED_SUMMARY_EXPECTED_GENERATION_SHA256"),
            ExactSha(item.SummaryExpectedMetricPlanSha256,
                "STORED_SUMMARY_EXPECTED_METRIC_PLAN_SHA256"),
            item.SummaryExpectedMetricPlanEntryCount is > 0
                ? StatisticReconciliationActualCanonical.Integer(
                    item.SummaryExpectedMetricPlanEntryCount.Value)
                : throw Invalid("STORED_SUMMARY_METRIC_PLAN_COUNT_INVALID"),
            ExactSha(item.SummaryExpectedManifestSha256,
                "STORED_SUMMARY_EXPECTED_MANIFEST_SHA256"),
            item.SummaryExpectedDocumentCount is > 0
                ? StatisticReconciliationActualCanonical.Integer(
                    item.SummaryExpectedDocumentCount.Value)
                : throw Invalid("STORED_SUMMARY_DOCUMENT_COUNT_INVALID"),
            ExactSha(item.SummaryExpectedMembershipSemanticSha256,
                "STORED_SUMMARY_EXPECTED_MEMBERSHIP_SHA256"),
            Required(item.SummaryExpectedRuntimeKind,
                "STORED_SUMMARY_EXPECTED_RUNTIME_KIND"),
            ExactSha(item.SummaryExpectedIdentitySetSha256,
                "STORED_SUMMARY_EXPECTED_IDENTITY_SET_SHA256"),
            item.SummaryMetricIdentityCount is > 0
                ? StatisticReconciliationActualCanonical.Integer(
                    item.SummaryMetricIdentityCount.Value)
                : throw Invalid("STORED_SUMMARY_IDENTITY_COUNT_INVALID"),
            ExactSha(item.SummaryPlanBindingSha256,
                "STORED_SUMMARY_PLAN_BINDING_SHA256"),
            ExactSha(item.SummaryMappingManifestSha256,
                "STORED_SUMMARY_MAPPING_MANIFEST_SHA256"),
            OptionalSha(item.LifecycleManifestSha256,
                "STORED_LIFECYCLE_MANIFEST_SHA256") ?? "~",
            item.LifecycleObservationCount?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            ExactSha(item.ActualRelationalProofBindingSha256,
                "STORED_RELATIONAL_PROOF_BINDING_SHA256"),
            StatisticReconciliationActualPublicationStates.Ready,
            StatisticReconciliationActualCanonical.Instant(item.CreatedAtUtc));
    }
    private static string GenerationBindingSha256(
        StatisticReconciliationActualPublicationGeneration generation,
        DateTime createdAtUtc)
    {
        var context = generation.Context;
        var pins = context.CatalogPins;
        return StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_PUBLICATION_GENERATION_BINDING_V7",
            context.ReconciliationId,
            context.ImmutableIdentitySha256,
            context.ImmutableHeaderSha256,
            context.WorkId,
            context.ScopeAssignmentId,
            context.PeriodKey,
            context.PeriodInstanceKey,
            context.ConceptKey,
            context.Grain,
            context.TimeAxis,
            context.FilterSha256,
            context.DynamicFormVersionId,
            context.DynamicFormSchemaSha256,
            context.RuntimeKind!,
            context.FlowTemplateVersionId ?? "~",
            context.FlowPayloadSha256 ?? "~",
            context.FlowInstanceId ?? "~",
            context.ExecutionEpochId ?? "~",
            context.ExecutionEpoch.HasValue
                ? StatisticReconciliationActualCanonical.Integer(
                    context.ExecutionEpoch.Value)
                : "~",
            context.ExecutionEpochRevision.HasValue
                ? StatisticReconciliationActualCanonical.Integer(
                    context.ExecutionEpochRevision.Value)
                : "~",
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            context.ActualConfigurationBundleSha256,
            context.RecheckCaptureBindingSha256 ?? "~",
            context.CoherentCatalogPinSetSha256,
            pins.CatalogPinSetSha256,
            generation.GenerationId,
            generation.GenerationSemanticSha256,
            generation.SourceSetSha256,
            context.ActualMembershipSemanticSha256!,
            context.ActualSourceDecisionManifestSha256!,
            context.LifecycleMetricScopeSha256!,
            generation.InputBindingSha256,
            generation.LifecycleSemanticSha256,
            generation.ResultPinSetSha256,
            generation.ExportPinSetSha256,
            context.SummaryPlanBinding.ExpectedReconciliationId,
            context.SummaryPlanBinding.ExpectedGenerationId,
            context.SummaryPlanBinding.ExpectedGenerationSha256,
            context.SummaryPlanBinding.ExpectedMetricPlanSha256,
            StatisticReconciliationActualCanonical.Integer(
                context.SummaryPlanBinding.ExpectedMetricPlanEntryCount),
            context.SummaryPlanBinding.ExpectedManifestSha256,
            StatisticReconciliationActualCanonical.Integer(
                context.SummaryPlanBinding.ExpectedDocumentCount),
            context.SummaryPlanBinding.ExpectedMembershipSemanticSha256,
            context.SummaryPlanBinding.ExpectedRuntimeKind,
            context.SummaryPlanBinding.ExpectedIdentitySetSha256,
            StatisticReconciliationActualCanonical.Integer(
                context.SummaryPlanBinding.MetricIdentityCount),
            context.SummaryPlanBinding.SemanticSha256,
            generation.SummaryMappingManifestSha256!,
            context.LifecycleManifestSha256 ?? "~",
            context.LifecycleObservationCount?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            context.RelationalProofBinding!.SemanticSha256,
            generation.CaptureState,
            StatisticReconciliationActualCanonical.Instant(createdAtUtc));
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string? OptionalSha(string? value, string name)
        => value is null ? null : ExactSha(value, name);

    private static string ExactSha(string? value, string name)
    {
        var normalized = Required(value, name);
        if (normalized.Length != 64 ||
            normalized.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
            throw Invalid($"{name}_INVALID");
        return normalized;
    }

    private static StatisticReconciliationActualObservationException Invalid(string reason)
        => new($"ACTUAL_PUBLICATION_{reason}");
}
