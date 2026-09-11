using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualRunBindingGuard
{
    internal static bool Matches(
        StatisticReconciliationRun run,
        StatisticReconciliationActualPublicationContext committed)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(committed);
        var activeBindingSha256 =
            run.Recheck?.CaptureBinding.BindingSha256;
        var currentBindingSha256 =
            run.CurrentGenerationRecheckCaptureBinding?.BindingSha256;
        if (activeBindingSha256 is not null &&
            Same(committed.RecheckCaptureBindingSha256,
                activeBindingSha256))
        {
            run = StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(run);
        }
        else if (currentBindingSha256 is not null &&
                 Same(committed.RecheckCaptureBindingSha256,
                     currentBindingSha256))
        {
            run = StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCurrentRun(run);
        }
        else if (committed.RecheckCaptureBindingSha256 is null &&
                 currentBindingSha256 is null)
        {
            // Initial current remains readable while its first successor is
            // being captured under an active marker.
        }
        else
        {
            return false;
        }
        return MatchesImmutableCaptureRun(run, committed);
    }

    // An initial generation is compared against the persisted creation tuple,
    // even after rechecks. Never substitute an effective current/active view.
    internal static bool MatchesInitialCreation(
        StatisticReconciliationRun persisted,
        StatisticReconciliationActualPublicationContext committed)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(committed);
        return committed.RecheckCaptureBindingSha256 is null &&
            MatchesImmutableCaptureRun(persisted, committed);
    }

    private static bool MatchesImmutableCaptureRun(
        StatisticReconciliationRun run,
        StatisticReconciliationActualPublicationContext committed)
    {
        var pins = committed.CatalogPins;
        ArgumentNullException.ThrowIfNull(pins);
        var expectedActualConfigurationBundleSha256 =
            run.ActualCapturePlan is null
                ? run.P8ConfigBundleHash
                : run.ActualConfigurationBundleSha256;

        var headerMatches =
            Same(committed.ReconciliationId, run.Id) &&
            Same(committed.ImmutableIdentitySha256, run.ImmutableIdentityHash) &&
            Same(committed.ImmutableHeaderSha256, run.ImmutableHeaderHash) &&
            Same(committed.WorkId, run.WorkId) &&
            Same(committed.ScopeAssignmentId, run.ScopeAssignmentId) &&
            Same(committed.PeriodKey, run.PeriodKey) &&
            Same(committed.PeriodInstanceKey, run.PeriodInstanceKey) &&
            Same(committed.ConceptKey, run.ConceptKey) &&
            Same(committed.Grain, run.Grain) &&
            Same(committed.TimeAxis, run.TimeAxis) &&
            Same(committed.FilterSha256, run.FilterHash) &&
            Same(committed.DynamicFormVersionId, run.DynamicFormVersionId) &&
            Same(committed.DynamicFormSchemaSha256, run.DynamicFormSchemaHash) &&
            IsSha(committed.LifecycleMetricScopeSha256) &&
            Same(committed.FlowTemplateVersionId, run.FlowTemplateVersionId) &&
            Same(committed.FlowPayloadSha256, run.FlowPayloadHash) &&
            Same(committed.FlowInstanceId, run.FlowInstanceId) &&
            Same(committed.ExecutionEpochId, run.FlowExecutionEpochId) &&
            committed.ExecutionEpoch == run.FlowExecutionEpoch &&
            committed.ExecutionEpochRevision == run.FlowExecutionEpochRevision &&
            Same(committed.P8ConfigurationOwnerId, run.P8ConfigOwnerId) &&
            Same(committed.P8ConfigurationBundleSha256, run.P8ConfigBundleHash) &&
            Same(
                committed.ActualConfigurationBundleSha256,
                expectedActualConfigurationBundleSha256);
        if (!headerMatches)
            return false;

        var catalogMatches =
            Same(pins.P9CatalogVersion, run.P9CatalogVersion) &&
            Same(pins.P9CatalogRawSha256, run.P9CatalogRawSha256) &&
            Same(pins.P9CatalogSemanticSha256, run.P9CatalogSemanticSha256) &&
            Same(pins.P9SchemaRawSha256, run.P9SchemaRawSha256) &&
            Same(pins.P9SchemaSemanticSha256, run.P9SchemaSemanticSha256) &&
            Same(pins.P9StageLockSha256, run.P9StageLockSha256) &&
            Same(pins.CandidateChainId, run.CandidateChainId) &&
            Same(pins.CandidatePromptId, run.CandidatePromptId) &&
            Same(pins.CandidateCatalogVersion, run.CandidateCatalogVersion) &&
            Same(pins.CandidateCatalogRawSha256, run.CandidateCatalogRawSha256) &&
            Same(
                pins.CandidateCatalogSemanticSha256,
                run.CandidateCatalogSemanticSha256) &&
            Same(pins.CandidateSchemaRawSha256, run.CandidateSchemaRawSha256) &&
            Same(
                pins.CandidateSchemaSemanticSha256,
                run.CandidateSchemaSemanticSha256) &&
            Same(pins.CandidateStageLockSha256, run.CandidateStageLockSha256);
        if (!catalogMatches)
            return false;

        // The coherent catalog slice hash is generation evidence and is already
        // protected by the validated commit binding. The persisted run owns the
        // immutable catalog vocabulary below; rebuild its pin-set hash here.
        var expectedPinSetSha256 = StatisticReconciliationActualCanonical.Hash(
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
        return Same(pins.CatalogPinSetSha256, expectedPinSetSha256);
    }

    private static bool IsSha(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Same(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);
}
