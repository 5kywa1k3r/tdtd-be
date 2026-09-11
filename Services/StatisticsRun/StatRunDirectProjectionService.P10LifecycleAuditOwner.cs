using System.Collections.Immutable;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsRun;

internal sealed record StatRunLifecycleContributionSourceAuditSnapshot(
    string SourceAuditId,
    string SourceAuditHash,
    string RuntimeKind,
    string SourceReportId,
    int SourcePayloadRevision,
    string SourcePayloadHash,
    int SourceLifecycleRevision,
    string ApprovalCommandId,
    string ApprovalEventKey,
    string? MappingReceiptId,
    string? MappingProvenanceId,
    string? MappingProvenanceHash,
    string? MappingResultSemanticHash,
    int? MappingResultPayloadRevision,
    string? MappingResultPayloadHash,
    string? MappingFlowVersionId,
    int? MappingFlowVersionNo,
    string? MappingFlowPayloadHash,
    string? FlowTemplateVersionId,
    string? FlowPayloadHash,
    string? FlowInstanceId,
    int? FlowExecutionEpoch,
    string? FlowStepInstanceId,
    string? FlowBranchId,
    string? FlowStepId,
    int? FlowAttemptNo,
    string? NonFlowPolicyVersion,
    string? NonFlowPolicyOwnerId,
    long? NonFlowPolicyRevision,
    string? NonFlowPolicy,
    string WorkId,
    string WorkAssignmentId,
    string PeriodInstanceKey,
    string ConfigVersionId,
    string ConfigHash,
    string MembershipSignature,
    string ContributionPolicy,
    string ContributionPolicyHash,
    string ReversalIdentity);

internal sealed record StatRunLifecycleContributionTargetAuditSnapshot(
    string ContributionId,
    string SourceAuditId,
    string SourceReportId,
    string TargetStore,
    string TargetStatisticId,
    string TargetIdentityHash,
    string Operation,
    string OperationVersion,
    string RunId,
    string ReceiptId,
    string GenerationId,
    string ReversalIdentity,
    string State,
    string LedgerEntryHash);

internal sealed record StatRunLifecycleReversalAuditSnapshot(
    string Operation,
    string EventKey,
    string SourceReportId,
    int SourceLifecycleRevision,
    string ReceiptId,
    string PriorRunId,
    string PriorGenerationId,
    string PriorGenerationHash,
    string? PriorLedgerHash,
    string? PriorReversalBaselineHash,
    int PriorSourceCount,
    int PriorTargetCount,
    ImmutableArray<string> PriorSourceAuditHashes,
    ImmutableArray<string> PriorTargetLedgerHashes,
    string AuditHash);

internal sealed record StatRunLifecycleContributionAuditSnapshot(
    StatRunDirectSourceOwnerScope Scope,
    string ReceiptId,
    string TriggerReportId,
    int TriggerLifecycleRevision,
    string TriggerLifecycleEventKey,
    string? OperationVersion,
    string? LedgerHash,
    string? ReversalBaselineHash,
    ImmutableArray<StatRunLifecycleContributionSourceAuditSnapshot> Sources,
    ImmutableArray<StatRunLifecycleContributionTargetAuditSnapshot> Targets,
    int ValidatedSourceCount,
    int ValidatedTargetCount,
    string SourceAuditSetSha256,
    string TargetLedgerSetSha256,
    string? ZeroTargetProofSha256,
    StatRunLifecycleReversalAuditSnapshot? Reversal,
    string SnapshotSemanticSha256);

internal interface IStatRunDirectProjectionLifecycleAuditOwner
{
    Task<StatRunLifecycleContributionAuditSnapshot> ReadLifecycleAuditAsync(
        StatRunDirectSourceOwnerScope scope,
        CancellationToken cancellationToken);
}

public sealed partial class StatRunDirectProjectionService
    : IStatRunDirectProjectionLifecycleAuditOwner
{
    async Task<StatRunLifecycleContributionAuditSnapshot>
        IStatRunDirectProjectionLifecycleAuditOwner.ReadLifecycleAuditAsync(
            StatRunDirectSourceOwnerScope scope,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireReadOwnerScope(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var jobs = await _ctx.WorkReportStatisticRebuildJobs
            .Find(job =>
                job.Id == scope.RunId &&
                job.WorkId == scope.WorkId &&
                job.PeriodInstanceKey == scope.PeriodInstanceKey &&
                job.DynamicFormTemplateId == scope.DynamicFormTemplateId &&
                job.GenerationId == scope.GenerationId &&
                job.GenerationHash == scope.GenerationSha256 &&
                job.SourceMembershipSignature == scope.MembershipSignature &&
                job.DirectSourceRevision == scope.DirectSourceRevision &&
                job.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                job.IsCurrentPublication &&
                !job.IsDeleted)
            .SortBy(job => job.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (jobs.Count != 1)
            throw Fail("P10_LIFECYCLE_AUDIT_PUBLICATION_NOT_EXACT");

        var job = jobs[0];
        await ValidateCompletedPublicationAsync(
            job,
            requireCurrent: true,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(job.ReceiptId) ||
            string.IsNullOrWhiteSpace(job.SourceReportId) ||
            job.SourceLifecycleRevision is not > 0 ||
            string.IsNullOrWhiteSpace(job.SourceLifecycleEventKey))
        {
            throw Fail("P10_LIFECYCLE_AUDIT_TRIGGER_PIN_INVALID");
        }

        var sources = (job.FlowContributionSources ?? [])
            .Select(static value => new StatRunLifecycleContributionSourceAuditSnapshot(
                SourceAuditId: value.SourceAuditId,
                SourceAuditHash: value.SourceAuditHash,
                RuntimeKind: "FLOW",
                SourceReportId: value.SourceReportId,
                SourcePayloadRevision: value.SourcePayloadRevision,
                SourcePayloadHash: value.SourcePayloadHash,
                SourceLifecycleRevision: value.SourceLifecycleRevision,
                ApprovalCommandId: value.ApprovalCommandId,
                ApprovalEventKey: value.ApprovalEventKey,
                MappingReceiptId: value.MappingReceiptId,
                MappingProvenanceId: value.MappingProvenanceId,
                MappingProvenanceHash: value.MappingProvenanceHash,
                MappingResultSemanticHash: value.MappingResultSemanticHash,
                MappingResultPayloadRevision: value.MappingResultPayloadRevision,
                MappingResultPayloadHash: value.MappingResultPayloadHash,
                MappingFlowVersionId: value.MappingFlowVersionId,
                MappingFlowVersionNo: value.MappingFlowVersionNo,
                MappingFlowPayloadHash: value.MappingFlowPayloadHash,
                FlowTemplateVersionId: value.FlowTemplateVersionId,
                FlowPayloadHash: value.FlowPayloadHash,
                FlowInstanceId: value.FlowInstanceId,
                FlowExecutionEpoch: value.FlowExecutionEpoch,
                FlowStepInstanceId: value.FlowStepInstanceId,
                FlowBranchId: value.FlowBranchId,
                FlowStepId: value.FlowStepId,
                FlowAttemptNo: value.FlowAttemptNo,
                NonFlowPolicyVersion: null,
                NonFlowPolicyOwnerId: null,
                NonFlowPolicyRevision: null,
                NonFlowPolicy: null,
                WorkId: value.WorkId,
                WorkAssignmentId: value.WorkAssignmentId,
                PeriodInstanceKey: value.PeriodInstanceKey,
                ConfigVersionId: value.ConfigVersionId,
                ConfigHash: value.ConfigHash,
                MembershipSignature: value.MembershipSignature,
                ContributionPolicy: value.ContributionPolicy,
                ContributionPolicyHash: value.ContributionPolicyHash,
                ReversalIdentity: value.ReversalIdentity))
            .Concat((job.NonFlowContributionSources ?? [])
                .Select(static value =>
                    new StatRunLifecycleContributionSourceAuditSnapshot(
                        SourceAuditId: value.SourceAuditId,
                        SourceAuditHash: value.SourceAuditHash,
                        RuntimeKind: "NON_FLOW",
                        SourceReportId: value.SourceReportId,
                        SourcePayloadRevision: value.SourcePayloadRevision,
                        SourcePayloadHash: value.SourcePayloadHash,
                        SourceLifecycleRevision: value.SourceLifecycleRevision,
                        ApprovalCommandId: value.ApprovalCommandId,
                        ApprovalEventKey: value.ApprovalEventKey,
                        MappingReceiptId: null,
                        MappingProvenanceId: null,
                        MappingProvenanceHash: null,
                        MappingResultSemanticHash: null,
                        MappingResultPayloadRevision: null,
                        MappingResultPayloadHash: null,
                        MappingFlowVersionId: null,
                        MappingFlowVersionNo: null,
                        MappingFlowPayloadHash: null,
                        FlowTemplateVersionId: null,
                        FlowPayloadHash: null,
                        FlowInstanceId: null,
                        FlowExecutionEpoch: null,
                        FlowStepInstanceId: null,
                        FlowBranchId: null,
                        FlowStepId: null,
                        FlowAttemptNo: null,
                        NonFlowPolicyVersion: value.PolicyVersion,
                        NonFlowPolicyOwnerId: value.PolicyOwnerId,
                        NonFlowPolicyRevision: value.PolicyRevision,
                        NonFlowPolicy: value.Policy,
                        WorkId: value.WorkId,
                        WorkAssignmentId: value.WorkAssignmentId,
                        PeriodInstanceKey: value.PeriodInstanceKey,
                        ConfigVersionId: value.ConfigVersionId,
                        ConfigHash: value.ConfigHash,
                        MembershipSignature: value.MembershipSignature,
                        ContributionPolicy: value.ContributionOperation,
                        ContributionPolicyHash: value.PolicySha256,
                        ReversalIdentity: value.ReversalIdentity)))
            .OrderBy(static value => value.SourceReportId, StringComparer.Ordinal)
            .ThenBy(static value => value.RuntimeKind, StringComparer.Ordinal)
            .ToImmutableArray();
        var targets = (job.FlowContributionTargets ?? [])
            .Select(static value => new StatRunLifecycleContributionTargetAuditSnapshot(
                value.ContributionId,
                value.SourceAuditId,
                value.SourceReportId,
                value.TargetStore,
                value.TargetStatisticId,
                value.TargetIdentityHash,
                value.Operation,
                value.OperationVersion,
                value.RunId,
                value.ReceiptId,
                value.GenerationId,
                value.ReversalIdentity,
                value.State,
                value.LedgerEntryHash))
            .OrderBy(static value => value.TargetStore, StringComparer.Ordinal)
            .ThenBy(static value => value.TargetStatisticId, StringComparer.Ordinal)
            .ToImmutableArray();
        var reversal = job.ReversalAudit is null
            ? null
            : new StatRunLifecycleReversalAuditSnapshot(
                job.ReversalAudit.Operation,
                job.ReversalAudit.EventKey,
                job.ReversalAudit.SourceReportId,
                job.ReversalAudit.SourceLifecycleRevision,
                job.ReversalAudit.ReceiptId,
                job.ReversalAudit.PriorRunId,
                job.ReversalAudit.PriorGenerationId,
                job.ReversalAudit.PriorGenerationHash,
                job.ReversalAudit.PriorLedgerHash,
                job.ReversalAudit.PriorReversalBaselineHash,
                job.ReversalAudit.PriorSourceCount,
                job.ReversalAudit.PriorTargetCount,
                (job.ReversalAudit.PriorSourceAuditHashes ?? [])
                    .ToImmutableArray(),
                (job.ReversalAudit.PriorTargetLedgerHashes ?? [])
                    .ToImmutableArray(),
                job.ReversalAudit.AuditHash);
        var snapshot = new StatRunLifecycleContributionAuditSnapshot(
            scope,
            job.ReceiptId!,
            job.SourceReportId!,
            job.SourceLifecycleRevision.Value,
            job.SourceLifecycleEventKey!,
            job.FlowContributionOperationVersion,
            job.FlowContributionLedgerHash,
            job.FlowContributionReversalBaselineHash,
            sources,
            targets,
            sources.Length,
            targets.Length,
            string.Empty,
            string.Empty,
            null,
            reversal,
            string.Empty);
        snapshot = StatRunLifecycleContributionAuditCanonical.Normalize(snapshot);
        StatRunLifecycleContributionAuditCanonical.RequireValid(snapshot);
        return snapshot;
    }
}

internal static class StatRunLifecycleContributionAuditCanonical
{
    internal static StatRunLifecycleContributionAuditSnapshot Normalize(
        StatRunLifecycleContributionAuditSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sources = (value.Sources.IsDefault ? [] : value.Sources)
            .OrderBy(static item => item.SourceReportId, StringComparer.Ordinal)
            .ThenBy(static item => item.SourceAuditId, StringComparer.Ordinal)
            .ToImmutableArray();
        var targets = (value.Targets.IsDefault ? [] : value.Targets)
            .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
            .ThenBy(static item => item.ContributionId, StringComparer.Ordinal)
            .ToImmutableArray();
        var normalized = value with
        {
            Sources = sources,
            Targets = targets,
            ValidatedSourceCount = sources.Length,
            ValidatedTargetCount = targets.Length,
            SourceAuditSetSha256 = string.Empty,
            TargetLedgerSetSha256 = string.Empty,
            ZeroTargetProofSha256 = null,
            SnapshotSemanticSha256 = string.Empty
        };
        normalized = normalized with
        {
            SourceAuditSetSha256 = ComputeSourceSet(normalized),
            TargetLedgerSetSha256 = ComputeTargetSet(normalized)
        };
        normalized = normalized with
        {
            ZeroTargetProofSha256 = targets.Length == 0
                ? ComputeZeroTargetProof(normalized)
                : null
        };
        return normalized with
        {
            SnapshotSemanticSha256 = ComputeCore(normalized)
        };
    }

    internal static void RequireValid(
        StatRunLifecycleContributionAuditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var canonical = Normalize(snapshot);
        if (snapshot.ValidatedSourceCount != canonical.ValidatedSourceCount ||
            snapshot.ValidatedTargetCount != canonical.ValidatedTargetCount ||
            !string.Equals(snapshot.SourceAuditSetSha256,
                canonical.SourceAuditSetSha256, StringComparison.Ordinal) ||
            !string.Equals(snapshot.TargetLedgerSetSha256,
                canonical.TargetLedgerSetSha256, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ZeroTargetProofSha256,
                canonical.ZeroTargetProofSha256, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotSemanticSha256,
                canonical.SnapshotSemanticSha256, StringComparison.Ordinal) ||
            snapshot.Sources.Select(static value => value.SourceAuditId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Sources.Length ||
            snapshot.Sources.Select(static value => value.SourceReportId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Sources.Length ||
            !string.Equals(snapshot.OperationVersion,
                "P9_CONTRIBUTION_V2", StringComparison.Ordinal) ||
            snapshot.Sources.Any(source => !ExactSource(snapshot, source)) ||
            snapshot.Targets.Select(static value => value.ContributionId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Targets.Length ||
            snapshot.Targets.Select(static value => value.ReversalIdentity)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Targets.Length ||
            snapshot.Targets.Select(static value =>
                    $"{value.TargetStore}\n{value.TargetStatisticId}")
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Targets.Length ||
            snapshot.Targets.Any(target => !ExactTarget(snapshot, target)) ||
            (snapshot.Reversal is not null &&
             !string.Equals(snapshot.Reversal.AuditHash,
                 ComputeReversalAudit(snapshot, snapshot.Reversal),
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "P10_LIFECYCLE_AUDIT_SNAPSHOT_INVALID");
        }
    }

    internal static bool HasValidatedZeroTargetProof(
        StatRunLifecycleContributionAuditSnapshot snapshot)
    {
        RequireValid(snapshot);
        return snapshot.ValidatedTargetCount == 0 &&
            snapshot.Targets.IsEmpty &&
            snapshot.ZeroTargetProofSha256 is not null;
    }

    internal static string Compute(
        StatRunLifecycleContributionAuditSnapshot value) =>
        Normalize(value).SnapshotSemanticSha256;

    private static bool ExactSource(
        StatRunLifecycleContributionAuditSnapshot snapshot,
        StatRunLifecycleContributionSourceAuditSnapshot source)
    {
        var common = IsCanonicalSha(source.SourceAuditId) &&
            IsCanonicalSha(source.SourceAuditHash) &&
            ObjectId.TryParse(source.SourceReportId, out _) &&
            source.SourcePayloadRevision > 0 &&
            IsCanonicalSha(source.SourcePayloadHash) &&
            source.SourceLifecycleRevision > 0 &&
            !string.IsNullOrWhiteSpace(source.ApprovalCommandId) &&
            IsCanonicalSha(source.ApprovalEventKey) &&
            ObjectId.TryParse(source.WorkId, out _) &&
            ObjectId.TryParse(source.WorkAssignmentId, out _) &&
            !string.IsNullOrWhiteSpace(source.PeriodInstanceKey) &&
            ObjectId.TryParse(source.ConfigVersionId, out _) &&
            IsCanonicalSha(source.ConfigHash) &&
            IsCanonicalSha(source.MembershipSignature) &&
            string.Equals(source.WorkId,
                snapshot.Scope.WorkId, StringComparison.Ordinal) &&
            string.Equals(source.PeriodInstanceKey,
                snapshot.Scope.PeriodInstanceKey, StringComparison.Ordinal) &&
            string.Equals(source.MembershipSignature,
                snapshot.Scope.MembershipSignature, StringComparison.Ordinal) &&
            string.Equals(source.ContributionPolicy,
                "INCLUDE", StringComparison.Ordinal) &&
            IsCanonicalSha(source.ContributionPolicyHash) &&
            IsCanonicalSha(source.ReversalIdentity);
        if (!common)
            return false;
        if (string.Equals(source.RuntimeKind, "FLOW", StringComparison.Ordinal))
        {
            return source.NonFlowPolicyVersion is null &&
                source.NonFlowPolicyOwnerId is null &&
                source.NonFlowPolicyRevision is null &&
                source.NonFlowPolicy is null &&
                ObjectId.TryParse(source.MappingReceiptId, out _) &&
                ObjectId.TryParse(source.MappingProvenanceId, out _) &&
                IsCanonicalSha(source.MappingProvenanceHash) &&
                IsCanonicalSha(source.MappingResultSemanticHash) &&
                source.MappingResultPayloadRevision is > 0 &&
                IsCanonicalSha(source.MappingResultPayloadHash) &&
                ObjectId.TryParse(source.MappingFlowVersionId, out _) &&
                source.MappingFlowVersionNo is > 0 &&
                IsCanonicalSha(source.MappingFlowPayloadHash) &&
                ObjectId.TryParse(source.FlowTemplateVersionId, out _) &&
                IsCanonicalSha(source.FlowPayloadHash) &&
                ObjectId.TryParse(source.FlowInstanceId, out _) &&
                source.FlowExecutionEpoch is > 0 &&
                ObjectId.TryParse(source.FlowStepInstanceId, out _) &&
                ObjectId.TryParse(source.FlowBranchId, out _) &&
                !string.IsNullOrWhiteSpace(source.FlowStepId) &&
                source.FlowAttemptNo is > 0;
        }
        return string.Equals(source.RuntimeKind,
                   "NON_FLOW", StringComparison.Ordinal) &&
            source.MappingReceiptId is null &&
            source.MappingProvenanceId is null &&
            source.MappingProvenanceHash is null &&
            source.MappingResultSemanticHash is null &&
            source.MappingResultPayloadRevision is null &&
            source.MappingResultPayloadHash is null &&
            source.MappingFlowVersionId is null &&
            source.MappingFlowVersionNo is null &&
            source.MappingFlowPayloadHash is null &&
            source.FlowTemplateVersionId is null &&
            source.FlowPayloadHash is null &&
            source.FlowInstanceId is null &&
            source.FlowExecutionEpoch is null &&
            source.FlowStepInstanceId is null &&
            source.FlowBranchId is null &&
            source.FlowStepId is null &&
            source.FlowAttemptNo is null &&
            string.Equals(source.NonFlowPolicyVersion,
                "P10_NON_FLOW_CONTRIBUTION_POLICY_V1",
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(source.NonFlowPolicyOwnerId) &&
            source.NonFlowPolicyRevision is > 0 &&
            string.Equals(source.NonFlowPolicy,
                "V_INCLUDE", StringComparison.Ordinal) &&
            ExactNonFlowSourceHash(snapshot, source);
    }

    private static bool ExactNonFlowSourceHash(
        StatRunLifecycleContributionAuditSnapshot snapshot,
        StatRunLifecycleContributionSourceAuditSnapshot source)
    {
        if (source.NonFlowPolicyOwnerId is null ||
            source.NonFlowPolicyRevision is not > 0 ||
            source.NonFlowPolicy is null)
        {
            return false;
        }
        var expectedPolicySha =
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_NON_FLOW_LOCKED_POLICY_V1",
                source.SourceReportId,
                source.NonFlowPolicyOwnerId,
                source.NonFlowPolicyRevision.Value.ToString(
                    CultureInfo.InvariantCulture),
                source.NonFlowPolicy);
        var sourceAuditHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_NON_FLOW_CONTRIBUTION_SOURCE_V1",
            Id = source.SourceReportId,
            PayloadRevision = source.SourcePayloadRevision,
            PayloadHash = source.SourcePayloadHash,
            LifecycleRevision = source.SourceLifecycleRevision,
            approvalCommandId = source.ApprovalCommandId,
            approvalEventKey = source.ApprovalEventKey,
            source.WorkId,
            source.WorkAssignmentId,
            source.PeriodInstanceKey,
            source.ConfigVersionId,
            source.ConfigHash,
            membershipSignature = source.MembershipSignature,
            policyVersion = source.NonFlowPolicyVersion,
            policyOwnerId = source.NonFlowPolicyOwnerId,
            policyRevision = source.NonFlowPolicyRevision.Value,
            policy = source.NonFlowPolicy,
            policySha256 = source.ContributionPolicyHash,
            contributionOperation = source.ContributionPolicy
        });
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_NON_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            "P9_NON_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n" +
            $"{sourceAuditId}\n{snapshot.Scope.GenerationId}");
        return string.Equals(source.ContributionPolicyHash,
                   expectedPolicySha, StringComparison.Ordinal) &&
            string.Equals(source.SourceAuditHash,
                sourceAuditHash, StringComparison.Ordinal) &&
            string.Equals(source.SourceAuditId,
                sourceAuditId, StringComparison.Ordinal) &&
            string.Equals(source.ReversalIdentity,
                reversalIdentity, StringComparison.Ordinal);
    }

    private static bool ExactTarget(
        StatRunLifecycleContributionAuditSnapshot snapshot,
        StatRunLifecycleContributionTargetAuditSnapshot target)
    {
        var source = snapshot.Sources.SingleOrDefault(item =>
            string.Equals(item.SourceAuditId,
                target.SourceAuditId, StringComparison.Ordinal) &&
            string.Equals(item.SourceReportId,
                target.SourceReportId, StringComparison.Ordinal));
        if (source is null ||
            target.TargetStore is not (
                "work_report_field_stat_values" or
                "work_report_table_stat_values" or
                "work_report_label_stat_values") ||
            !ObjectId.TryParse(target.TargetStatisticId, out _) ||
            !IsCanonicalSha(target.TargetIdentityHash) ||
            !string.Equals(target.Operation, "INCLUDE", StringComparison.Ordinal) ||
            !string.Equals(target.OperationVersion,
                snapshot.OperationVersion, StringComparison.Ordinal) ||
            !string.Equals(target.RunId,
                snapshot.Scope.RunId, StringComparison.Ordinal) ||
            !string.Equals(target.ReceiptId,
                snapshot.ReceiptId, StringComparison.Ordinal) ||
            !string.Equals(target.GenerationId,
                snapshot.Scope.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(target.State, "APPLIED", StringComparison.Ordinal))
        {
            return false;
        }

        var contributionId = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_CONTRIBUTION_TARGET_V2",
            source.RuntimeKind,
            source.SourceAuditId,
            source.SourceAuditHash,
            store = target.TargetStore,
            targetId = target.TargetStatisticId,
            targetIdentityHash = target.TargetIdentityHash,
            operation = "INCLUDE",
            operationVersion = snapshot.OperationVersion,
            RunId = snapshot.Scope.RunId,
            receiptId = snapshot.ReceiptId,
            GenerationId = snapshot.Scope.GenerationId,
            SourceMembershipSignature = snapshot.Scope.MembershipSignature
        });
        var reversalIdentity = StatRunCanonicalJson.HashText(
            $"P9_CONTRIBUTION_TARGET_INVERSE_V2\n{contributionId}");
        var ledgerEntryHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_CONTRIBUTION_LEDGER_ENTRY_V2",
            contributionId,
            source.RuntimeKind,
            source.SourceAuditId,
            source.SourceReportId,
            store = target.TargetStore,
            targetId = target.TargetStatisticId,
            targetIdentityHash = target.TargetIdentityHash,
            operation = "INCLUDE",
            operationVersion = snapshot.OperationVersion,
            RunId = snapshot.Scope.RunId,
            receiptId = snapshot.ReceiptId,
            GenerationId = snapshot.Scope.GenerationId,
            reversalIdentity,
            state = "APPLIED"
        });
        return string.Equals(target.ContributionId,
                contributionId, StringComparison.Ordinal) &&
            string.Equals(target.ReversalIdentity,
                reversalIdentity, StringComparison.Ordinal) &&
            string.Equals(target.LedgerEntryHash,
                ledgerEntryHash, StringComparison.Ordinal);
    }

    private static string ComputeSourceSet(
        StatRunLifecycleContributionAuditSnapshot value) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P10_LIFECYCLE_P9_SOURCE_AUDIT_SET_V2",
            value.Scope,
            value.ReceiptId,
            sourceAudits = value.Sources
                .Select(static item => new
                {
                    item.RuntimeKind,
                    item.SourceAuditId,
                    item.SourceAuditHash
                })
                .ToArray()
        });

    private static string ComputeTargetSet(
        StatRunLifecycleContributionAuditSnapshot value) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P10_LIFECYCLE_P9_TARGET_LEDGER_SET_V2",
            value.Scope,
            value.ReceiptId,
            targetLedgerHashes = value.Targets
                .Select(static item => item.LedgerEntryHash)
                .ToArray()
        });

    private static string ComputeZeroTargetProof(
        StatRunLifecycleContributionAuditSnapshot value) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P10_LIFECYCLE_P9_ZERO_TARGET_PROOF_V2",
            value.Scope,
            value.ReceiptId,
            value.OperationVersion,
            value.LedgerHash,
            value.ReversalBaselineHash,
            value.ValidatedSourceCount,
            value.ValidatedTargetCount,
            value.SourceAuditSetSha256,
            value.TargetLedgerSetSha256
        });

    private static string ComputeReversalAudit(
        StatRunLifecycleContributionAuditSnapshot value,
        StatRunLifecycleReversalAuditSnapshot reversal) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_AUDIT_V1",
            RunId = value.Scope.RunId,
            GenerationId = value.Scope.GenerationId,
            reversal.Operation,
            reversal.EventKey,
            reversal.SourceReportId,
            reversal.SourceLifecycleRevision,
            reversal.ReceiptId,
            reversal.PriorRunId,
            reversal.PriorGenerationId,
            reversal.PriorGenerationHash,
            reversal.PriorLedgerHash,
            reversal.PriorReversalBaselineHash,
            reversal.PriorSourceCount,
            reversal.PriorTargetCount,
            reversal.PriorSourceAuditHashes,
            reversal.PriorTargetLedgerHashes
        });

    private static string ComputeCore(
        StatRunLifecycleContributionAuditSnapshot value) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P10_LIFECYCLE_P9_AUDIT_SNAPSHOT_V3",
            value.Scope,
            value.ReceiptId,
            value.TriggerReportId,
            value.TriggerLifecycleRevision,
            value.TriggerLifecycleEventKey,
            value.OperationVersion,
            value.LedgerHash,
            value.ReversalBaselineHash,
            value.ValidatedSourceCount,
            value.ValidatedTargetCount,
            value.SourceAuditSetSha256,
            value.TargetLedgerSetSha256,
            value.ZeroTargetProofSha256,
            sources = value.Sources,
            targets = value.Targets,
            reversal = value.Reversal
        });

    private static bool IsCanonicalSha(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
