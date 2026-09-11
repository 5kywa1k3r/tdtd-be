using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class StatisticReconciliationRecheckCaptureBindingCanonical
{
    internal static void Refresh(
        StatisticReconciliationRecheckCaptureBinding binding)
        => binding.BindingSha256 = Hash(binding);

    internal static void RequireValid(
        StatisticReconciliationRecheckCaptureBinding? binding)
    {
        if (binding is null ||
            binding.SchemaVersion !=
                StatisticReconciliationRecheckCaptureBinding
                    .CurrentSchemaVersion ||
            !ObjectId.TryParse(binding.P9RunId, out _) ||
            binding.P9ResultId != binding.P9RunId ||
            !Sha(binding.P9GenerationId) ||
            !Sha(binding.P9GenerationHash) ||
            !ObjectId.TryParse(binding.SourceReportId, out _) ||
            binding.SourcePayloadRevision < 1 ||
            !Sha(binding.SourcePayloadHash) ||
            binding.SourceLifecycleRevision < 1 ||
            !Sha(binding.SourceLifecycleEventKey) ||
            !Sha(binding.SourceLifecycleHash) ||
            !ObjectId.TryParse(binding.DynamicFormVersionId, out _) ||
            !Sha(binding.DynamicFormSchemaHash) ||
            !Sha(binding.P8ConfigBundleHash) ||
            !Sha(binding.P9CatalogRawSha256) ||
            !Sha(binding.P9CatalogSemanticSha256) ||
            !Sha(binding.P9SchemaRawSha256) ||
            !Sha(binding.P9SchemaSemanticSha256) ||
            !Sha(binding.P9StageLockSha256) ||
            string.IsNullOrWhiteSpace(binding.PeriodKey) ||
            string.IsNullOrWhiteSpace(binding.PeriodInstanceKey) ||
            string.IsNullOrWhiteSpace(binding.PeriodKind) ||
            string.IsNullOrWhiteSpace(binding.TimeAxis) ||
            !Sha(binding.ActualCapturePlanSha256) ||
            !Sha(binding.ActualConfigurationBundleSha256) ||
            binding.RemediationEvidenceSha256 is not null &&
            !Sha(binding.RemediationEvidenceSha256) ||
            !Sha(binding.BindingSha256))
            throw Invalid("RECHECK_CAPTURE_BINDING_HEADER_INVALID");

        try
        {
            StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
                binding.ActualCapturePlan,
                binding.ActualCapturePlanSha256);
        }
        catch (InvalidOperationException)
        {
            throw Invalid("RECHECK_CAPTURE_PLAN_INVALID");
        }

        if (!PeriodShapeValid(binding) ||
            !StringComparer.Ordinal.Equals(
                binding.ActualCapturePlan.ActualConfigurationBundleSha256,
                binding.ActualConfigurationBundleSha256) ||
            !StringComparer.Ordinal.Equals(binding.BindingSha256,
                Hash(binding)))
            throw Invalid("RECHECK_CAPTURE_BINDING_HASH_INVALID");
    }

    private static bool PeriodShapeValid(
        StatisticReconciliationRecheckCaptureBinding binding)
    {
        var start = binding.PeriodStartUtc;
        var end = binding.PeriodEndUtc;
        var nullPairAllowed =
            StatisticReconciliationActualCapturePlanIntegrity.IsV4(
                binding.ActualCapturePlan) &&
            StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                binding.ActualCapturePlan.Advanced.Disposition);
        if (!start.HasValue || !end.HasValue)
            return nullPairAllowed && !start.HasValue && !end.HasValue;
        return start.Value.Kind == DateTimeKind.Utc &&
               end.Value.Kind == DateTimeKind.Utc &&
               end.Value >= start.Value;
    }
    internal static string Hash(
        StatisticReconciliationRecheckCaptureBinding binding)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_RECHECK_CAPTURE_BINDING_HASH_V1",
            binding.SchemaVersion,
            binding.P9ResultId,
            binding.P9RunId,
            binding.P9GenerationId,
            binding.P9GenerationHash,
            binding.P9RunKind,
            binding.P9CapabilityId,
            binding.P9RouteId,
            binding.P9CandidateChainId,
            binding.P9CandidatePromptId,
            binding.SourceReportId,
            binding.SourcePayloadRevision,
            binding.SourcePayloadHash,
            binding.SourceLifecycleRevision,
            binding.SourceLifecycleEventKey,
            binding.SourceLifecycleHash,
            binding.SourceLifecycleStatus,
            binding.DynamicFormFamilyId,
            binding.DynamicFormVersionId,
            binding.DynamicFormVersionNo,
            binding.DynamicFormSchemaHash,
            binding.FlowTemplateId,
            binding.FlowFamilyRevision,
            binding.FlowTemplateVersionId,
            binding.FlowPayloadHash,
            binding.FlowInstanceId,
            binding.FlowInstanceRevision,
            binding.FlowExecutionEpoch,
            binding.FlowExecutionEpochId,
            binding.FlowExecutionEpochRevision,
            binding.FlowStepId,
            binding.FlowBranchId,
            binding.FlowStepInstanceId,
            binding.FlowStepInstanceRevision,
            binding.FlowContributionPolicy,
            binding.FlowContributionPolicyHash,
            binding.FlowEffectiveStatus,
            binding.FlowContributionProvenanceHash,
            binding.P8ConfigOwnerId,
            binding.P8ConfigId,
            binding.P8ConfigVersionId,
            binding.P8ConfigVersionNo,
            binding.P8ConfigRevision,
            binding.P8ConfigHash,
            binding.P8ConfigBundleHash,
            binding.P9CatalogVersion,
            binding.P9CatalogRawSha256,
            binding.P9CatalogSemanticSha256,
            binding.P9SchemaRawSha256,
            binding.P9SchemaSemanticSha256,
            binding.P9StageLockSha256,
            binding.PeriodKey,
            binding.PeriodInstanceKey,
            binding.PeriodKind,
            binding.PeriodStartUtc,
            binding.PeriodEndUtc,
            binding.TimeAxis,
            binding.ActualCapturePlanSha256,
            binding.ActualConfigurationBundleSha256,
            binding.RemediationEvidenceSha256
        });

    /// <summary>
    /// Returns a transient capture view.  The persisted immutable creation
    /// tuple is never rewritten; only trusted capture code receives this view.
    /// </summary>
    internal static StatisticReconciliationRun EffectiveCurrentRun(
        StatisticReconciliationRun persisted)
        => ApplyBinding(persisted,
            persisted.CurrentGenerationRecheckCaptureBinding);

    internal static StatisticReconciliationRun EffectiveCaptureRun(
        StatisticReconciliationRun persisted)
        => ApplyBinding(persisted,
            persisted.Recheck?.CaptureBinding ??
            persisted.CurrentGenerationRecheckCaptureBinding);

    private static StatisticReconciliationRun ApplyBinding(
        StatisticReconciliationRun persisted,
        StatisticReconciliationRecheckCaptureBinding? binding)
    {
        if (binding is null)
            return persisted;
        RequireValid(binding);
        var run = BsonSerializer.Deserialize<StatisticReconciliationRun>(
            persisted.ToBson());
        run.P9ResultId = binding.P9ResultId;
        run.P9RunId = binding.P9RunId;
        run.P9GenerationId = binding.P9GenerationId;
        run.P9GenerationHash = binding.P9GenerationHash;
        run.P9RunKind = binding.P9RunKind;
        run.P9CapabilityId = binding.P9CapabilityId;
        run.P9RouteId = binding.P9RouteId;
        run.P9CandidateChainId = binding.P9CandidateChainId;
        run.P9CandidatePromptId = binding.P9CandidatePromptId;
        run.SourceReportId = binding.SourceReportId;
        run.SourcePayloadRevision = binding.SourcePayloadRevision;
        run.SourcePayloadHash = binding.SourcePayloadHash;
        run.SourceLifecycleRevision = binding.SourceLifecycleRevision;
        run.SourceLifecycleEventKey = binding.SourceLifecycleEventKey;
        run.SourceLifecycleHash = binding.SourceLifecycleHash;
        run.SourceLifecycleStatus = binding.SourceLifecycleStatus;
        run.DynamicFormFamilyId = binding.DynamicFormFamilyId;
        run.DynamicFormVersionId = binding.DynamicFormVersionId;
        run.DynamicFormVersionNo = binding.DynamicFormVersionNo;
        run.DynamicFormSchemaHash = binding.DynamicFormSchemaHash;
        run.FlowTemplateId = binding.FlowTemplateId;
        run.FlowFamilyRevision = binding.FlowFamilyRevision;
        run.FlowTemplateVersionId = binding.FlowTemplateVersionId;
        run.FlowPayloadHash = binding.FlowPayloadHash;
        run.FlowInstanceId = binding.FlowInstanceId;
        run.FlowInstanceRevision = binding.FlowInstanceRevision;
        run.FlowExecutionEpoch = binding.FlowExecutionEpoch;
        run.FlowExecutionEpochId = binding.FlowExecutionEpochId;
        run.FlowExecutionEpochRevision = binding.FlowExecutionEpochRevision;
        run.FlowStepId = binding.FlowStepId;
        run.FlowBranchId = binding.FlowBranchId;
        run.FlowStepInstanceId = binding.FlowStepInstanceId;
        run.FlowStepInstanceRevision = binding.FlowStepInstanceRevision;
        run.FlowContributionPolicy = binding.FlowContributionPolicy;
        run.FlowContributionPolicyHash = binding.FlowContributionPolicyHash;
        run.FlowEffectiveStatus = binding.FlowEffectiveStatus;
        run.FlowContributionProvenanceHash =
            binding.FlowContributionProvenanceHash;
        run.P8ConfigOwnerId = binding.P8ConfigOwnerId;
        run.P8ConfigId = binding.P8ConfigId;
        run.P8ConfigVersionId = binding.P8ConfigVersionId;
        run.P8ConfigVersionNo = binding.P8ConfigVersionNo;
        run.P8ConfigRevision = binding.P8ConfigRevision;
        run.P8ConfigHash = binding.P8ConfigHash;
        run.P8ConfigBundleHash = binding.P8ConfigBundleHash;
        run.P9CatalogVersion = binding.P9CatalogVersion;
        run.P9CatalogRawSha256 = binding.P9CatalogRawSha256;
        run.P9CatalogSemanticSha256 = binding.P9CatalogSemanticSha256;
        run.P9SchemaRawSha256 = binding.P9SchemaRawSha256;
        run.P9SchemaSemanticSha256 = binding.P9SchemaSemanticSha256;
        run.P9StageLockSha256 = binding.P9StageLockSha256;
        run.PeriodKey = binding.PeriodKey;
        run.PeriodInstanceKey = binding.PeriodInstanceKey;
        run.PeriodKind = binding.PeriodKind;
        run.PeriodStartUtc = binding.PeriodStartUtc;
        run.PeriodEndUtc = binding.PeriodEndUtc;
        run.TimeAxis = binding.TimeAxis;
        run.ActualCapturePlan = binding.ActualCapturePlan;
        run.ActualCapturePlanSha256 = binding.ActualCapturePlanSha256;
        run.ActualConfigurationBundleSha256 =
            binding.ActualConfigurationBundleSha256;
        return run;
    }

    private static bool Sha(string? value) =>
        StatisticReconciliationCanonicalJson.IsCanonicalSha256(value);

    private static InvalidOperationException Invalid(string code) => new(code);
}
