using MongoDB.Bson;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class StatisticReconciliationTrustedRemediationEvidenceCanonical
{
    internal const string CollectionName =
        "work_report_statistic_reconciliation_reviews";

    internal static StatisticReconciliationTrustedRemediationEvidence Build(
        StatisticReconciliationRun run,
        StatisticReconciliationReview baseVerdict,
        StatisticReconciliationRecheckCaptureBinding successor,
        WorkReportStatisticRebuildJob successorP9,
        string actorUserId,
        string activationEvidenceSha256,
        DateTime authorizedAtUtc)
    {
        var current = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCurrentRun(run);
        RequireAuthorizedP9Operation(successorP9, successor);
        var evidence = new StatisticReconciliationTrustedRemediationEvidence
        {
            ReconciliationId = run.Id,
            BaseVerdictGenerationId = baseVerdict.VerdictGenerationId,
            BaseVerdictGenerationSha256 =
                baseVerdict.VerdictGenerationSha256,
            BaseActualGenerationId = run.CurrentGenerationId!,
            BaseActualGenerationSha256 = run.CurrentGenerationHash!,
            RootCauseClass = baseVerdict.RootCauseClass!,
            ReferenceType = StatisticReconciliationRemediationReferenceTypes
                .AuthorizedP9Operation,
            ReferenceId = successorP9.ReceiptId!,
            ReferenceSha256 = AuthorizedOperationReferenceSha(successorP9),
            BeforeSourceSha256 = current.SourcePayloadHash,
            AfterSourceSha256 = successor.SourcePayloadHash,
            BeforeResultSha256 = current.P9GenerationHash,
            SuccessorP9GenerationId = successor.P9GenerationId,
            SuccessorP9GenerationSha256 = successor.P9GenerationHash,
            SuccessorCapturePlanSha256 = successor.ActualCapturePlanSha256,
            AuthorizationEvidenceSha256 =
                StatisticReconciliationCanonicalJson.HashObject(new
                {
                    schema = "P10_REMEDIATION_AUTHORIZATION_EVIDENCE_V1",
                    actorUserId,
                    run.WorkId,
                    run.ScopeAssignmentId,
                    baseVerdict.VerdictGenerationId,
                    successor.P9GenerationId,
                    activationEvidenceSha256
                }),
            AuthorizedByUserId = actorUserId,
            AuthorizedAtUtc = authorizedAtUtc
        };
        evidence.Id = StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_TRUSTED_REMEDIATION_EVIDENCE_ID_V1",
            evidence.ReconciliationId,
            evidence.BaseVerdictGenerationId,
            evidence.SuccessorP9GenerationId
        });
        evidence.EvidenceSha256 = Hash(evidence);
        RequireValid(evidence);
        return evidence;
    }

    internal static StatisticReconciliationRecheckRemediationBinding
        ToBinding(
            StatisticReconciliationTrustedRemediationEvidence evidence)
    {
        RequireValid(evidence);
        var binding = new StatisticReconciliationRecheckRemediationBinding
        {
            EvidenceId = evidence.Id,
            EvidenceSha256 = evidence.EvidenceSha256,
            RootCauseClass = evidence.RootCauseClass,
            ReferenceType = evidence.ReferenceType,
            ReferenceId = evidence.ReferenceId,
            ReferenceSha256 = evidence.ReferenceSha256,
            BeforeSourceSha256 = evidence.BeforeSourceSha256,
            AfterSourceSha256 = evidence.AfterSourceSha256,
            BeforeResultSha256 = evidence.BeforeResultSha256,
            SuccessorP9GenerationId = evidence.SuccessorP9GenerationId,
            SuccessorP9GenerationSha256 =
                evidence.SuccessorP9GenerationSha256,
            SuccessorCapturePlanSha256 =
                evidence.SuccessorCapturePlanSha256,
            AuthorizationEvidenceSha256 =
                evidence.AuthorizationEvidenceSha256
        };
        binding.BindingSha256 = BindingHash(binding);
        RequireValid(binding);
        return binding;
    }

    internal static StatisticReconciliationVerdictRemediationRequest
        BuildVerdictRequest(
            StatisticReconciliationRecheckRemediationBinding binding,
            string successorActualGenerationId,
            string successorActualGenerationSha256)
    {
        RequireValid(binding);
        RequireSha(successorActualGenerationId, "successorActualGenerationId");
        RequireSha(successorActualGenerationSha256,
            "successorActualGenerationSha256");
        return new(
            binding.ReferenceType,
            binding.ReferenceId,
            binding.ReferenceSha256,
            binding.BeforeSourceSha256,
            binding.AfterSourceSha256,
            binding.BeforeResultSha256,
            binding.SuccessorP9GenerationSha256,
            successorActualGenerationId,
            successorActualGenerationSha256);
    }

    internal static bool MatchesStored(
        StatisticReconciliationRecheckRemediationBinding? expected,
        StatisticReconciliationVerdictRemediation? actual,
        string successorActualGenerationId,
        string successorActualGenerationSha256)
    {
        if (expected is null)
            return actual is null;
        if (actual is null)
            return false;
        RequireValid(expected);
        return actual.ReferenceType == expected.ReferenceType &&
               actual.ReferenceId == expected.ReferenceId &&
               actual.ReferenceSha256 == expected.ReferenceSha256 &&
               actual.BeforeSourceSha256 == expected.BeforeSourceSha256 &&
               actual.AfterSourceSha256 == expected.AfterSourceSha256 &&
               actual.BeforeResultSha256 == expected.BeforeResultSha256 &&
               actual.AfterResultSha256 ==
                   expected.SuccessorP9GenerationSha256 &&
               actual.RecheckGenerationId ==
                   successorActualGenerationId &&
               actual.RecheckGenerationSha256 ==
                   successorActualGenerationSha256;
    }

    internal static bool SameTarget(
        StatisticReconciliationTrustedRemediationEvidence left,
        StatisticReconciliationTrustedRemediationEvidence right)
    {
        RequireValid(left);
        RequireValid(right);
        return left.Id == right.Id &&
               left.ReconciliationId == right.ReconciliationId &&
               left.BaseVerdictGenerationId ==
                   right.BaseVerdictGenerationId &&
               left.BaseVerdictGenerationSha256 ==
                   right.BaseVerdictGenerationSha256 &&
               left.BaseActualGenerationId ==
                   right.BaseActualGenerationId &&
               left.BaseActualGenerationSha256 ==
                   right.BaseActualGenerationSha256 &&
               left.RootCauseClass == right.RootCauseClass &&
               left.ReferenceType == right.ReferenceType &&
               left.ReferenceId == right.ReferenceId &&
               left.ReferenceSha256 == right.ReferenceSha256 &&
               left.BeforeSourceSha256 == right.BeforeSourceSha256 &&
               left.AfterSourceSha256 == right.AfterSourceSha256 &&
               left.BeforeResultSha256 == right.BeforeResultSha256 &&
               left.SuccessorP9GenerationId ==
                   right.SuccessorP9GenerationId &&
               left.SuccessorP9GenerationSha256 ==
                   right.SuccessorP9GenerationSha256 &&
               left.SuccessorCapturePlanSha256 ==
                   right.SuccessorCapturePlanSha256;
    }

    internal static bool Matches(
        StatisticReconciliationTrustedRemediationEvidence evidence,
        StatisticReconciliationRun run,
        StatisticReconciliationReview baseVerdict,
        StatisticReconciliationRecheckCaptureBinding successor,
        WorkReportStatisticRebuildJob successorP9)
    {
        RequireValid(evidence);
        RequireAuthorizedP9Operation(successorP9, successor);
        var current = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCurrentRun(run);
        return evidence.ReconciliationId == run.Id &&
               evidence.BaseVerdictGenerationId ==
                   baseVerdict.VerdictGenerationId &&
               evidence.BaseVerdictGenerationSha256 ==
                   baseVerdict.VerdictGenerationSha256 &&
               evidence.BaseActualGenerationId == run.CurrentGenerationId &&
               evidence.BaseActualGenerationSha256 ==
                   run.CurrentGenerationHash &&
               evidence.RootCauseClass == baseVerdict.RootCauseClass &&
               evidence.ReferenceId == successorP9.ReceiptId &&
               evidence.ReferenceSha256 ==
                   AuthorizedOperationReferenceSha(successorP9) &&
               evidence.BeforeSourceSha256 == current.SourcePayloadHash &&
               evidence.BeforeResultSha256 == current.P9GenerationHash &&
               evidence.SuccessorP9GenerationId ==
                   successor.P9GenerationId &&
               evidence.SuccessorP9GenerationSha256 ==
                   successor.P9GenerationHash &&
               evidence.AfterSourceSha256 == successor.SourcePayloadHash &&
               evidence.SuccessorCapturePlanSha256 ==
                   successor.ActualCapturePlanSha256;
    }
    internal static void RequireValid(
        StatisticReconciliationTrustedRemediationEvidence? value)
    {
        if (value is null ||
            value.SchemaVersion !=
                StatisticReconciliationTrustedRemediationEvidence
                    .CurrentSchemaVersion ||
            value.Kind !=
                StatisticReconciliationTrustedRemediationEvidence.RecordKind ||
            !Sha(value.Id) ||
            !ObjectId.TryParse(value.ReconciliationId, out _) ||
            !Sha(value.BaseVerdictGenerationId) ||
            !Sha(value.BaseVerdictGenerationSha256) ||
            !Sha(value.BaseActualGenerationId) ||
            !Sha(value.BaseActualGenerationSha256) ||
            value.RootCauseClass is not (
                StatisticReconciliationRootCauseClasses.MissingIdentity or
                StatisticReconciliationRootCauseClasses.ExtraIdentity) ||
            value.ReferenceType !=
                StatisticReconciliationRemediationReferenceTypes
                    .AuthorizedP9Operation ||
            string.IsNullOrWhiteSpace(value.ReferenceId) ||
            value.ReferenceId.Length > 256 ||
            value.ReferenceId.Any(character =>
                character < 0x21 || character > 0x7e) ||
            !Sha(value.ReferenceSha256) ||
            !Sha(value.BeforeSourceSha256) ||
            !Sha(value.AfterSourceSha256) ||
            !Sha(value.BeforeResultSha256) ||
            !Sha(value.SuccessorP9GenerationId) ||
            !Sha(value.SuccessorP9GenerationSha256) ||
            !Sha(value.SuccessorCapturePlanSha256) ||
            !Sha(value.AuthorizationEvidenceSha256) ||
            !ObjectId.TryParse(value.AuthorizedByUserId, out _) ||
            value.AuthorizedAtUtc == default ||
            value.AuthorizedAtUtc.Kind != DateTimeKind.Utc ||
            !Sha(value.EvidenceSha256) ||
            value.BeforeSourceSha256 == value.AfterSourceSha256 &&
            value.BeforeResultSha256 ==
                value.SuccessorP9GenerationSha256 ||
            value.Id != StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_TRUSTED_REMEDIATION_EVIDENCE_ID_V1",
                value.ReconciliationId,
                value.BaseVerdictGenerationId,
                value.SuccessorP9GenerationId
            }) ||
            value.EvidenceSha256 != Hash(value))
            throw new InvalidOperationException(
                "P10_REMEDIATION_EVIDENCE_INVALID");
    }

    internal static void RequireValid(
        StatisticReconciliationRecheckRemediationBinding? value)
    {
        if (value is null ||
            value.SchemaVersion !=
                StatisticReconciliationRecheckRemediationBinding
                    .CurrentSchemaVersion ||
            !Sha(value.EvidenceId) ||
            !Sha(value.EvidenceSha256) ||
            value.RootCauseClass is not (
                StatisticReconciliationRootCauseClasses.MissingIdentity or
                StatisticReconciliationRootCauseClasses.ExtraIdentity) ||
            value.ReferenceType !=
                StatisticReconciliationRemediationReferenceTypes
                    .AuthorizedP9Operation ||
            string.IsNullOrWhiteSpace(value.ReferenceId) ||
            value.ReferenceId.Length > 256 ||
            !Sha(value.ReferenceSha256) ||
            !Sha(value.BeforeSourceSha256) ||
            !Sha(value.AfterSourceSha256) ||
            !Sha(value.BeforeResultSha256) ||
            !Sha(value.SuccessorP9GenerationId) ||
            !Sha(value.SuccessorP9GenerationSha256) ||
            !Sha(value.SuccessorCapturePlanSha256) ||
            !Sha(value.AuthorizationEvidenceSha256) ||
            !Sha(value.BindingSha256) ||
            value.BeforeSourceSha256 == value.AfterSourceSha256 &&
            value.BeforeResultSha256 ==
                value.SuccessorP9GenerationSha256 ||
            value.BindingSha256 != BindingHash(value))
            throw new InvalidOperationException(
                "P10_RECHECK_REMEDIATION_BINDING_INVALID");
    }

    internal static string AuthorizedOperationReferenceSha(
        WorkReportStatisticRebuildJob value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (StatRunService.IsFoundationRefreshLifecycleProjection(value))
        {
            return StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_AUTHORIZED_P9_OPERATION_REFERENCE_V2",
                value.DirectProjectionIdentityVersion,
                value.DirectProjectionIdentityKey,
                value.Id,
                value.DedupeKey,
                value.ReceiptId,
                value.CommandId,
                value.RequestHash,
                value.ReceiptResponseHash,
                value.ImmutableHeaderHash,
                value.ReceiptAcceptedAtUtc,
                value.ActorUserId,
                value.RequestedByUserId,
                value.RunKind,
                value.CapabilityId,
                value.RouteId,
                value.GenerationId,
                value.GenerationHash,
                value.SourceReportId,
                value.SourcePayloadRevision,
                value.SourcePayloadHash,
                value.SourceLifecycleRevision,
                value.SourceLifecycleEventKey,
                value.SourceMembershipSignature,
                value.PublicationScopeKey,
                value.ConfigId,
                value.ConfigVersionId,
                value.ConfigVersionNo,
                value.ConfigRevision,
                value.ConfigHash,
                value.CandidateChainId,
                value.CandidatePromptId
            });
        }

        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_AUTHORIZED_P9_OPERATION_REFERENCE_V1",
            value.Id,
            value.ReceiptId,
            value.CommandId,
            value.RequestHash,
            value.ReceiptResponseHash,
            value.ImmutableHeaderHash,
            value.ReceiptAcceptedAtUtc,
            value.ActorUserId,
            value.RequestedByUserId,
            value.RunKind,
            value.CapabilityId,
            value.RouteId,
            value.GenerationId,
            value.GenerationHash,
            value.SourceReportId,
            value.SourcePayloadRevision,
            value.SourcePayloadHash,
            value.SourceLifecycleRevision,
            value.SourceLifecycleEventKey,
            value.CandidateChainId,
            value.CandidatePromptId
        });
    }
    internal static void RequireAuthorizedP9Operation(
        WorkReportStatisticRebuildJob value,
        StatisticReconciliationRecheckCaptureBinding successor)
    {
        ArgumentNullException.ThrowIfNull(value);
        StatRunService.RequireP10TrustedLifecycleOperationIntegrity(value);
        var isFoundationRefresh =
            StatRunService.IsFoundationRefreshLifecycleProjection(value);
        if (value.Id != successor.P9RunId ||
            (!isFoundationRefresh &&
             (string.IsNullOrWhiteSpace(value.CommandId) ||
              value.CommandId.Length > 256)) ||
            (isFoundationRefresh && value.CommandId is not null) ||
            !Sha(value.RequestHash) ||
            value.RequestHash != value.ImmutableHeaderHash ||
            value.ReceiptResponseHash is not null ||
            value.ReceiptAcceptedAtUtc is not { Kind: DateTimeKind.Utc } ||
            !ObjectId.TryParse(value.ActorUserId, out _) ||
            !ObjectId.TryParse(value.RequestedByUserId, out _) ||
            value.RequestedByUserId != value.ActorUserId ||
            value.RunKind != WorkReportStatisticRebuildJobRunKinds
                .LifecycleDirectProjection ||
            value.CapabilityId != StatRunCapabilities.DirectFieldTableLabel ||
            value.RouteId != StatRunRouteRegistry.LifecycleDirectProjector ||
            value.GenerationId != successor.P9GenerationId ||
            value.GenerationHash != successor.P9GenerationHash ||
            value.SourceReportId != successor.SourceReportId ||
            value.SourcePayloadRevision != successor.SourcePayloadRevision ||
            value.SourcePayloadHash != successor.SourcePayloadHash ||
            value.SourceLifecycleRevision !=
                successor.SourceLifecycleRevision ||
            value.SourceLifecycleEventKey !=
                successor.SourceLifecycleEventKey ||
            value.CandidateChainId != successor.P9CandidateChainId ||
            value.CandidatePromptId != successor.P9CandidatePromptId)
            throw new InvalidOperationException(
                "P10_AUTHORIZED_P9_OPERATION_INVALID");
    }
    internal static string BindingHash(
        StatisticReconciliationRecheckRemediationBinding value)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = value.SchemaVersion,
            value.EvidenceId,
            value.EvidenceSha256,
            value.RootCauseClass,
            value.ReferenceType,
            value.ReferenceId,
            value.ReferenceSha256,
            value.BeforeSourceSha256,
            value.AfterSourceSha256,
            value.BeforeResultSha256,
            value.SuccessorP9GenerationId,
            value.SuccessorP9GenerationSha256,
            value.SuccessorCapturePlanSha256,
            value.AuthorizationEvidenceSha256
        });

    private static string Hash(
        StatisticReconciliationTrustedRemediationEvidence value)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = value.SchemaVersion,
            recordKind = value.Kind,
            value.Id,
            value.ReconciliationId,
            value.BaseVerdictGenerationId,
            value.BaseVerdictGenerationSha256,
            value.BaseActualGenerationId,
            value.BaseActualGenerationSha256,
            value.RootCauseClass,
            value.ReferenceType,
            value.ReferenceId,
            value.ReferenceSha256,
            value.BeforeSourceSha256,
            value.AfterSourceSha256,
            value.BeforeResultSha256,
            value.SuccessorP9GenerationId,
            value.SuccessorP9GenerationSha256,
            value.SuccessorCapturePlanSha256,
            value.AuthorizationEvidenceSha256,
            value.AuthorizedByUserId,
            value.AuthorizedAtUtc
        });

    private static bool Sha(string? value) =>
        StatisticReconciliationCanonicalJson.IsCanonicalSha256(value);

    private static void RequireSha(string? value, string field)
    {
        if (!Sha(value))
            throw new InvalidOperationException(
                $"P10_REMEDIATION_{field}_INVALID");
    }
}
