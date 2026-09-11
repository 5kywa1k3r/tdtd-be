using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record StatisticReconciliationExpectedGenerationBinding(
    string ReconciliationId,
    string GenerationId,
    string GenerationSemanticSha256,
    string MetricPlanSha256,
    int MetricPlanEntryCount,
    string ManifestSha256,
    int DocumentCount,
    string MembershipSemanticSha256,
    string RuntimeKind);

internal sealed record StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
    string SchemaVersion,
    StatisticReconciliationExpectedMetricIdentity Identity,
    string JsonPointer,
    string ValueType,
    bool Unordered,
    string? CollectionSemantics,
    bool ExpandArray,
    ImmutableArray<string> Operations,
    string TransitionMode,
    string? BeforeJsonPointer,
    string? AfterJsonPointer,
    string? DifferenceOperation,
    string? TransitionKind,
    ImmutableArray<string> TransitionLegs,
    string PlanEntrySha256);

internal sealed record StatisticReconciliationExpectedCanonicalSourceEnvelope(
    string SourceStableIdentitySha256,
    string PayloadDocumentId,
    string ReportId,
    string WorkAssignmentId,
    int PayloadRevision,
    string PayloadOwnerSha256,
    string PayloadCanonicalSha256,
    int TableBlockCount,
    string TableBlockManifestSha256,
    string CanonicalPayloadJson);

internal sealed record StatisticReconciliationExpectedProjectionInput(
    StatisticReconciliationExpectedGenerationBinding Binding,
    ImmutableArray<StatisticReconciliationExpectedValueFreeMetricPlanDescriptor>
        MetricPlan,
    ImmutableArray<StatisticReconciliationExpectedCanonicalSourceEnvelope>
        IncludedSourceEnvelopes);

internal sealed record StatisticReconciliationExpectedAuthoritativeLifecycleRow(
    string SourceStableIdentitySha256,
    string StableSourceId,
    string IdentityKey,
    string WorkId,
    string WorkAssignmentId,
    string ReportId,
    string PayloadDocumentId,
    int PayloadRevision,
    string PayloadOwnerSha256,
    string PayloadCanonicalSha256,
    int LifecycleRevision,
    string LifecycleSha256,
    string LifecycleStatus,
    bool IsEffective,
    bool IsLocked,
    string RuntimeDisposition,
    string Disposition,
    string ReasonCode,
    string ContributionPolicy,
    string ContributionVersionId,
    long ContributionRevision,
    string ContributionPolicySha256,
    string ContributionProvenanceId,
    string ContributionProvenanceSha256,
    StatisticReconciliationExpectedAuthoritativeRuntimePin RuntimePin,
    string DecisionSemanticSha256,
    string DocumentSemanticSha256);

internal sealed record
    StatisticReconciliationExpectedAuthoritativeLifecycleProjection(
        StatisticReconciliationExpectedGenerationBinding Binding,
        ImmutableArray<StatisticReconciliationExpectedAuthoritativeLifecycleRow> Rows,
        string LifecycleManifestSha256,
        string DoubleCollectProofSha256);

internal interface IStatisticReconciliationExpectedGenerationBindingReader
{
    Task<StatisticReconciliationExpectedGenerationBinding> ResolveAsync(
        StatisticReconciliationRun run,
        string generationId,
        string generationSemanticSha256,
        string metricPlanSha256,
        CancellationToken cancellationToken);
}

internal interface
    IStatisticReconciliationExpectedAuthoritativeLifecycleProjectionReader
{
    Task<StatisticReconciliationExpectedAuthoritativeLifecycleProjection>
        ResolveAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationExpectedGenerationBinding exactBinding,
            CancellationToken cancellationToken);
}

internal interface IStatisticReconciliationExpectedProjectionInputReader
{
    Task<StatisticReconciliationExpectedProjectionInput> ResolveAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedGenerationBinding exactBinding,
        CancellationToken cancellationToken);
}

internal sealed class StatisticReconciliationExpectedProjectionInputException(
    string reason)
    : InvalidOperationException(reason)
{
    internal string Reason { get; } = reason;
}

internal sealed class StatisticReconciliationExpectedMongoProjectionInputReader(
    MongoDbContext context)
    : IStatisticReconciliationExpectedProjectionInputReader
{
    public async Task<StatisticReconciliationExpectedProjectionInput> ResolveAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedGenerationBinding exactBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(exactBinding);
        if (!StringComparer.Ordinal.Equals(run.Id, exactBinding.ReconciliationId))
            throw Fail("EXACT_RECONCILIATION_BINDING_MISMATCH");

        var documents = await context.StatisticReconciliationObservations
            .Find(item =>
                item.ReconciliationId == exactBinding.ReconciliationId &&
                item.GenerationId == exactBinding.GenerationId &&
                item.SchemaVersion ==
                    StatisticReconciliationExpectedObservationIntegrity
                        .ObservationSchemaVersion &&
                (item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.SourceDecision ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ConfigurationPin ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.LineagePin ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ExpectedAtom ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.GenerationCommit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        StatisticReconciliationObservation commitDocument;
        try
        {
            commitDocument =
                StatisticReconciliationExpectedObservationIntegrity
                    .ValidateGeneration(documents);
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
        {
            throw Fail($"GENERATION_INTEGRITY_{error.Reason}");
        }

        var commit = commitDocument.Commit ?? throw Fail("COMMIT_REQUIRED");
        RequireExactBinding(commitDocument, commit, exactBinding);
        var effectiveRun =
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(run);
        RequireRuntime(
            effectiveRun,
            commitDocument,
            exactBinding.RuntimeKind);
        var dynamicFormConfigurationPinCount = documents.Count(item =>
            item.RecordKind ==
                StatisticReconciliationObservationRecordKinds.ConfigurationPin &&
            item.ConfigurationPin is not null &&
            StringComparer.Ordinal.Equals(
                item.ConfigurationPin.Kind,
                StatisticReconciliationExpectedLedgerConfigurationKinds.DynamicForm));
        if (dynamicFormConfigurationPinCount > 1)
            throw Fail("DYNAMIC_FORM_CONFIGURATION_PIN_NOT_EXACT");
        var usesDynamicFormConfiguration =
            dynamicFormConfigurationPinCount == 1;
        if (usesDynamicFormConfiguration &&
            !StringComparer.Ordinal.Equals(
                effectiveRun.P9ResultKind,
                StatisticReconciliationP9ResultKinds.Direct))
            throw Fail("DYNAMIC_FORM_RUN_KIND_MISMATCH");

        var plan = documents
            .Where(item =>
                item.RecordKind ==
                StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan)
            .OrderBy(item => item.MetricPlan!.IdentitySha256, StringComparer.Ordinal)
            .Select(item => Descriptor(item.MetricPlan!))
            .ToImmutableArray();
        if (plan.Length != exactBinding.MetricPlanEntryCount || plan.Length == 0)
            throw Fail("SIGNABLE_METRIC_PLAN_REQUIRED");
        var rebuiltPlanSha =
            StatisticReconciliationExpectedMetricPlanIntegrity.BuildPlanSha256(
                plan.Select(PlanEntry));
        if (!StringComparer.Ordinal.Equals(
                rebuiltPlanSha,
                exactBinding.MetricPlanSha256))
            throw Fail("METRIC_PLAN_BINDING_MISMATCH");

        var included = documents
            .Where(item =>
                item.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.SourceDecision &&
                string.Equals(
                    item.SourceDecision!.ReasonCode,
                    StatisticReconciliationExpectedSourceDecisionReasons.Included,
                    StringComparison.Ordinal))
            .OrderBy(
                item => item.SourceDecision!.SourceStableIdentitySha256,
                StringComparer.Ordinal)
            .ToArray();
        var envelopes = ImmutableArray.CreateBuilder<
            StatisticReconciliationExpectedCanonicalSourceEnvelope>(included.Length);
        foreach (var item in included)
        {
            var source = item.SourceDecision!;
            if (source.WorkAssignmentId is null ||
                source.SourceStableIdentitySha256 is null)
                throw Fail("SOURCE_V2_IDENTITY_REQUIRED");
            var payloads = await context.WorkReportPayloads
                .Find(payload =>
                    payload.Id == source.PayloadDocumentId &&
                    payload.ReportId == source.ReportId &&
                    payload.PayloadRevision == source.PayloadRevision)
                .Limit(2)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (payloads.Count != 1)
                throw Fail("EXACT_PAYLOAD_REVISION_REQUIRED");
            var payload = payloads[0];
            if (!StringComparer.Ordinal.Equals(
                    payload.Status,
                    WorkReportPayloadStatus.Ready) ||
                !StringComparer.Ordinal.Equals(
                    payload.PayloadHash,
                    source.PayloadOwnerSha256))
                throw Fail("PAYLOAD_OWNER_BINDING_MISMATCH");
            var authoritativePayload = await
                StatisticReconciliationExpectedAuthoritativePayloadOwner.LoadAsync(
                    context,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(
                    authoritativePayload.PayloadOwnerSha256,
                    source.PayloadOwnerSha256))
                throw Fail("PAYLOAD_OWNER_BINDING_MISMATCH");
            var canonicalPayloadJson = usesDynamicFormConfiguration
                ? StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                    .ProjectApprovedRawPayload(
                        authoritativePayload.CanonicalPayloadJson)
                : authoritativePayload.CanonicalPayloadJson;
            var canonical =
                StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
                    canonicalPayloadJson,
                    16 * 1024 * 1024,
                    "$.projectionInput.payload",
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .PayloadJsonInvalid);
            if (!StringComparer.Ordinal.Equals(
                    canonical.Sha256,
                    source.PayloadCanonicalSha256))
                throw Fail("PAYLOAD_CANONICAL_BINDING_MISMATCH");

            var stable =
                StatisticReconciliationExpectedMembershipIntegrity
                    .BuildStableIdentitySha256(
                        commitDocument.WorkId,
                        source.WorkAssignmentId,
                        source.ReportId);
            if (!StringComparer.Ordinal.Equals(
                    stable,
                    source.SourceStableIdentitySha256))
                throw Fail("SOURCE_STABLE_IDENTITY_MISMATCH");
            envelopes.Add(new(
                stable,
                source.PayloadDocumentId,
                source.ReportId,
                source.WorkAssignmentId,
                source.PayloadRevision,
                source.PayloadOwnerSha256,
                source.PayloadCanonicalSha256,
                authoritativePayload.TableBlockCount,
                authoritativePayload.TableBlockManifestSha256,
                canonical.Value));
        }

        var membership =
            StatisticReconciliationExpectedMembershipIntegrity.BuildManifestSha256(
                envelopes.Select(item => item.SourceStableIdentitySha256));
        if (!StringComparer.Ordinal.Equals(
                membership,
                exactBinding.MembershipSemanticSha256))
            throw Fail("INCLUDED_MEMBERSHIP_BINDING_MISMATCH");

        return new StatisticReconciliationExpectedProjectionInput(
            exactBinding,
            plan,
            envelopes.ToImmutable());
    }

    internal static void RequireExactBinding(
        StatisticReconciliationObservation commitDocument,
        StatisticReconciliationObservationCommit commit,
        StatisticReconciliationExpectedGenerationBinding exact)
    {
        if (!StringComparer.Ordinal.Equals(
                commitDocument.GenerationId,
                exact.GenerationId) ||
            !StringComparer.Ordinal.Equals(
                commitDocument.GenerationSemanticSha256,
                exact.GenerationSemanticSha256) ||
            !StringComparer.Ordinal.Equals(
                commitDocument.MetricPlanSha256,
                exact.MetricPlanSha256) ||
            commit.MetricPlanEntryCount != exact.MetricPlanEntryCount ||
            !StringComparer.Ordinal.Equals(
                commit.ManifestSha256,
                exact.ManifestSha256) ||
            commit.DocumentCount != exact.DocumentCount ||
            !StringComparer.Ordinal.Equals(
                commit.MembershipSemanticSha256,
                exact.MembershipSemanticSha256))
            throw Fail("EXACT_GENERATION_BINDING_MISMATCH");
    }

    private static void RequireRuntime(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation commit,
        string runtimeKind)
    {
        var expected = run.FlowInstanceId is null
            ? StatisticReconciliationExpectedRuntimeKinds.NonFlow
            : StatisticReconciliationExpectedRuntimeKinds.Flow;
        if (!StringComparer.Ordinal.Equals(runtimeKind, expected) ||
            !StringComparer.Ordinal.Equals(commit.RuntimeKind, expected))
            throw Fail("RUNTIME_KIND_MISMATCH");
        if (expected == StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            if (!StringComparer.Ordinal.Equals(
                    commit.FlowTemplateVersionId,
                    run.FlowTemplateVersionId) ||
                !StringComparer.Ordinal.Equals(
                    commit.FlowPayloadSha256,
                    run.FlowPayloadHash) ||
                !StringComparer.Ordinal.Equals(
                    commit.FlowInstanceId,
                    run.FlowInstanceId) ||
                !StringComparer.Ordinal.Equals(
                    commit.ExecutionEpochId,
                    run.FlowExecutionEpochId))
                throw Fail("FLOW_RUNTIME_BINDING_MISMATCH");
        }
        else if (commit.FlowTemplateVersionId is not null ||
                 commit.FlowPayloadSha256 is not null ||
                 commit.FlowInstanceId is not null ||
                 commit.ExecutionEpochId is not null)
        {
            throw Fail("NON_FLOW_RUNTIME_MIXED");
        }
    }

    private static StatisticReconciliationExpectedValueFreeMetricPlanDescriptor
        Descriptor(StatisticReconciliationObservationMetricPlan plan)
    {
        var identity = new StatisticReconciliationExpectedMetricIdentity(
            StatisticReconciliationExpectedMetricIdentityCompiler.SchemaVersion,
            plan.Family,
            plan.Kind,
            plan.MetricId,
            plan.PeriodKey,
            plan.FieldId,
            plan.TableId,
            plan.RowId,
            plan.LabelId,
            plan.BasicScope,
            plan.BasicScopeId,
            plan.AdvancedGrain,
            plan.DiffKind,
            plan.IdentitySha256);
        var legs = TransitionLegs(plan.Family, plan.DifferenceOperation);
        return new(
            plan.SchemaVersion,
            identity,
            plan.JsonPointer,
            plan.ValueType,
            plan.Unordered,
            plan.ValueType ==
                StatisticReconciliationExpectedValueTypes.StringList
                ? plan.Unordered
                    ? StatisticReconciliationExpectedCollectionSemantics.Unordered
                    : StatisticReconciliationExpectedCollectionSemantics.Ordered
                : null,
            plan.ExpandArray,
            plan.Operations.ToImmutableArray(),
            plan.TransitionMode,
            plan.BeforeJsonPointer,
            plan.AfterJsonPointer,
            plan.DifferenceOperation,
            plan.TransitionKind,
            legs,
            plan.PlanEntrySha256);
    }

    internal static ImmutableArray<string> TransitionLegs(
        string family,
        string? differenceOperation)
    {
        if (family != StatisticReconciliationExpectedMetricFamilies.Diff)
            return ImmutableArray.Create(
                StatisticReconciliationExpectedTransitionLegs.None);
        var legs = ImmutableArray.Create(
            StatisticReconciliationExpectedTransitionLegs.Before,
            StatisticReconciliationExpectedTransitionLegs.After,
            StatisticReconciliationExpectedTransitionLegs.ChangeState);
        return differenceOperation ==
                StatisticReconciliationExpectedDifferenceOperations.Subtract
            ? legs.Add(StatisticReconciliationExpectedTransitionLegs.Delta)
            : legs;
    }
    private static StatisticReconciliationExpectedMetricPlanEntry PlanEntry(
        StatisticReconciliationExpectedValueFreeMetricPlanDescriptor item)
        => new(
            item.Identity,
            item.JsonPointer,
            item.ValueType,
            item.Unordered,
            item.ExpandArray,
            item.Operations,
            item.PlanEntrySha256,
            item.SchemaVersion,
            item.TransitionMode,
            item.BeforeJsonPointer,
            item.AfterJsonPointer,
            item.DifferenceOperation,
            item.TransitionKind);

    private static StatisticReconciliationExpectedProjectionInputException Fail(
        string reason)
        => new(reason);
}
internal sealed class StatisticReconciliationExpectedMongoGenerationBindingReader(
    MongoDbContext context)
    : IStatisticReconciliationExpectedGenerationBindingReader
{
    public async Task<StatisticReconciliationExpectedGenerationBinding> ResolveAsync(
        StatisticReconciliationRun run,
        string generationId,
        string generationSemanticSha256,
        string metricPlanSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var documents = await ReadGenerationAsync(
            context,
            run.Id,
            generationId,
            cancellationToken).ConfigureAwait(false);
        StatisticReconciliationObservation commitDocument;
        try
        {
            commitDocument = StatisticReconciliationExpectedObservationIntegrity
                .ValidateGeneration(documents);
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
        {
            throw Fail($"GENERATION_INTEGRITY_{error.Reason}");
        }
        var commit = commitDocument.Commit ?? throw Fail("COMMIT_REQUIRED");
        if (!StringComparer.Ordinal.Equals(
                commitDocument.GenerationSemanticSha256,
                generationSemanticSha256) ||
            !StringComparer.Ordinal.Equals(
                commitDocument.MetricPlanSha256,
                metricPlanSha256) ||
            commit.MetricPlanEntryCount <= 0 ||
            commitDocument.RuntimeKind is null)
            throw Fail("EXACT_GENERATION_BINDING_MISMATCH");
        RequireRun(
            StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(run),
            commitDocument);
        return new StatisticReconciliationExpectedGenerationBinding(
            run.Id,
            commitDocument.GenerationId,
            commitDocument.GenerationSemanticSha256,
            commitDocument.MetricPlanSha256,
            commit.MetricPlanEntryCount,
            commit.ManifestSha256,
            commit.DocumentCount,
            commit.MembershipSemanticSha256 ?? throw Fail(
                "MEMBERSHIP_SEMANTIC_REQUIRED"),
            commitDocument.RuntimeKind);
    }

    internal static async Task<List<StatisticReconciliationObservation>>
        ReadGenerationAsync(
            MongoDbContext context,
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
        => await context.StatisticReconciliationObservations
            .Find(item =>
                item.ReconciliationId == reconciliationId &&
                item.GenerationId == generationId &&
                item.SchemaVersion ==
                    StatisticReconciliationExpectedObservationIntegrity
                        .ObservationSchemaVersion &&
                (item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.SourceDecision ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ConfigurationPin ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.LineagePin ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.ExpectedAtom ||
                 item.RecordKind ==
                     StatisticReconciliationObservationRecordKinds.GenerationCommit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    internal static void RequireRun(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation commit)
    {
        var runtimeKind = run.FlowInstanceId is null
            ? StatisticReconciliationExpectedRuntimeKinds.NonFlow
            : StatisticReconciliationExpectedRuntimeKinds.Flow;
        if (!StringComparer.Ordinal.Equals(commit.ReconciliationId, run.Id) ||
            !StringComparer.Ordinal.Equals(
                commit.ImmutableIdentitySha256,
                run.ImmutableIdentityHash) ||
            !StringComparer.Ordinal.Equals(
                commit.ImmutableHeaderSha256,
                run.ImmutableHeaderHash) ||
            !StringComparer.Ordinal.Equals(commit.WorkId, run.WorkId) ||
            !StringComparer.Ordinal.Equals(
                commit.ScopeAssignmentId,
                run.ScopeAssignmentId) ||
            !StringComparer.Ordinal.Equals(commit.RuntimeKind, runtimeKind))
            throw Fail("EXPECTED_RUN_BINDING_MISMATCH");
    }

    private static StatisticReconciliationExpectedProjectionInputException Fail(
        string reason) => new(reason);
}

internal sealed class
    StatisticReconciliationExpectedMongoAuthoritativeLifecycleProjectionReader(
        MongoDbContext context,
        IStatisticReconciliationExpectedGenerationBindingReader bindingReader,
        IStatisticReconciliationExpectedAuthoritativeCurrentValidator
            currentValidator)
    : IStatisticReconciliationExpectedAuthoritativeLifecycleProjectionReader
{
    public async Task<
        StatisticReconciliationExpectedAuthoritativeLifecycleProjection>
        ResolveAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationExpectedGenerationBinding exactBinding,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(exactBinding);
        var current = await currentValidator.ValidateAsync(
            run,
            exactBinding,
            cancellationToken).ConfigureAwait(false);
        if (!current.Complete || !current.Current)
            throw Fail("AUTHORITATIVE_EXPECTED_GENERATION_DRIFT");
        var validated = await bindingReader.ResolveAsync(
            run,
            exactBinding.GenerationId,
            exactBinding.GenerationSemanticSha256,
            exactBinding.MetricPlanSha256,
            cancellationToken).ConfigureAwait(false);
        if (validated != exactBinding)
            throw Fail("EXACT_GENERATION_BINDING_MISMATCH");
        var documents = await StatisticReconciliationExpectedMongoGenerationBindingReader
            .ReadGenerationAsync(
                context,
                run.Id,
                exactBinding.GenerationId,
                cancellationToken).ConfigureAwait(false);
        var commitDocument = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        var rows = documents
            .Where(item => item.RecordKind ==
                StatisticReconciliationObservationRecordKinds.SourceDecision)
            .OrderBy(
                item => item.SourceDecision!.SourceStableIdentitySha256,
                StringComparer.Ordinal)
            .ThenBy(item => item.SourceDecision!.PayloadRevision)
            .ThenBy(item => item.SourceDecision!.ReasonCode, StringComparer.Ordinal)
            .Select(Row)
            .ToImmutableArray();
        if (rows.Length == 0)
            throw Fail("AUTHORITATIVE_LIFECYCLE_ROWS_REQUIRED");
        var lifecycleManifestSha256 = H(
            "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_MANIFEST_V1",
            rows.Select(item => H(
                "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_ROW_V1",
                [item.SourceStableIdentitySha256,
                 item.DecisionSemanticSha256,
                 item.DocumentSemanticSha256,
                 item.RuntimePin.RuntimeSemanticSha256])));
        var doubleCollectProofSha256 = H(
            "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_CURRENT_PROOF_V1",
            [current.DoubleCollectProofSha256,
             exactBinding.GenerationSemanticSha256,
             commitDocument.InputBindingSha256,
             commitDocument.LifecycleSemanticSha256,
             lifecycleManifestSha256,
             exactBinding.ManifestSha256,
             exactBinding.DocumentCount.ToString(
                 System.Globalization.CultureInfo.InvariantCulture)]);
        return new(
            exactBinding,
            rows,
            lifecycleManifestSha256,
            doubleCollectProofSha256);
    }

    private static StatisticReconciliationExpectedAuthoritativeLifecycleRow Row(
        StatisticReconciliationObservation document)
    {
        var source = document.SourceDecision ?? throw Fail(
            "SOURCE_DECISION_REQUIRED");
        var runtime = source.AuthoritativeRuntime ?? throw Fail(
            "AUTHORITATIVE_RUNTIME_PIN_REQUIRED");
        if (source.WorkAssignmentId is null ||
            source.SourceStableIdentitySha256 is null)
            throw Fail("SOURCE_V2_IDENTITY_REQUIRED");
        return new(
            source.SourceStableIdentitySha256,
            source.StableSourceId,
            source.IdentityKey,
            document.WorkId,
            source.WorkAssignmentId,
            source.ReportId,
            source.PayloadDocumentId,
            source.PayloadRevision,
            source.PayloadOwnerSha256,
            source.PayloadCanonicalSha256,
            source.LifecycleRevision,
            source.LifecycleSha256,
            source.LifecycleStatus,
            source.IsEffective,
            source.IsLocked,
            source.RuntimeDisposition,
            source.Disposition,
            source.ReasonCode,
            source.ContributionPolicy,
            source.ContributionVersionId,
            source.ContributionRevision,
            source.ContributionPolicySha256,
            source.ContributionProvenanceId,
            source.ContributionProvenanceSha256,
            Runtime(runtime),
            source.DecisionSemanticSha256,
            document.DocumentSemanticSha256);
    }

    private static StatisticReconciliationExpectedAuthoritativeRuntimePin Runtime(
        StatisticReconciliationObservationAuthoritativeRuntimePin value)
        => new(
            value.RuntimeKind,
            value.FlowTemplateVersionId,
            value.FlowPayloadSha256,
            value.FlowInstanceId,
            value.FlowBranchId,
            value.FlowStepId,
            value.FlowStepInstanceId,
            value.FlowStepRevision,
            value.FlowAttemptNo,
            value.ExecutionEpochId,
            value.ExecutionEpoch,
            value.CurrentExecutionEpochId,
            value.CurrentExecutionEpoch,
            value.ExecutionEpochRevision,
            value.IsCanonicalEpoch,
            value.ApprovalCommandId,
            value.ApprovalEventKey,
            value.MappingReceiptId,
            value.MappingProvenanceId,
            value.MappingProvenanceSha256,
            value.MappingResultSemanticSha256,
            value.MappingResultPayloadRevision,
            value.MappingResultPayloadSha256,
            value.MappingFlowVersionId,
            value.MappingFlowVersionNo,
            value.MappingFlowPayloadSha256,
            value.MappingLocked,
            value.ConfigVersionId,
            value.ConfigSha256,
            value.MembershipSignatureSha256,
            value.ContributionPolicy,
            value.ContributionPolicySha256,
            value.ContributionProvenanceId,
            value.ContributionProvenanceSha256,
            value.LifecycleOwnerSha256,
            value.RuntimeSemanticSha256);
    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            domain,
            fields);

    private static StatisticReconciliationExpectedProjectionInputException Fail(
        string reason) => new(reason);
}
