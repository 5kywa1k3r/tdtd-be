using System.Globalization;
using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.StatisticsRun;

internal sealed record StatRunNonFlowContributionPolicy(
    string Version,
    string Policy,
    string OwnerId,
    long Revision,
    string PolicySha256);

public sealed partial class StatRunDirectProjectionService
{
    private const string ContributionOperationVersion = "P9_CONTRIBUTION_V2";
    private const string NonFlowContributionPolicyVersion =
        "P10_NON_FLOW_CONTRIBUTION_POLICY_V1";

    internal static StatRunNonFlowContributionPolicy?
        ResolveNonFlowContributionAuditPolicyForP10(
            WorkAssignmentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var locked = StatisticReconciliationExpectedMongoSnapshotReader
            .ResolveLockedNonFlowContribution(report);
        return locked is null
            ? null
            : new StatRunNonFlowContributionPolicy(
                NonFlowContributionPolicyVersion,
                locked.Policy,
                locked.OwnerId,
                locked.Revision,
                locked.PolicySha256);
    }

    private static IReadOnlyList<WorkReportNonFlowContributionSourceAudit>
        BuildNonFlowContributionSources(
            WorkReportDirectGenerationContext context,
            IReadOnlyCollection<DirectMember> members)
    {
        var result = new List<WorkReportNonFlowContributionSourceAudit>();
        foreach (var member in members
                     .Where(static item => item.Runtime.FlowInstanceId is null)
                     .OrderBy(static item => item.Report.Id, StringComparer.Ordinal))
        {
            var policy = ResolveNonFlowContributionAuditPolicyForP10(
                member.Report);
            if (policy is null || string.Equals(
                    policy.Policy,
                    StatisticReconciliationExpectedContributionPolicies.Exclude,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(
                    policy.Policy,
                    StatisticReconciliationExpectedContributionPolicies.Include,
                    StringComparison.Ordinal))
            {
                throw Fail("NON_FLOW_CONTRIBUTION_POLICY_INVALID");
            }
            result.Add(BuildNonFlowContributionSource(context, member, policy));
        }
        return result;
    }

    private static WorkReportNonFlowContributionSourceAudit
        BuildNonFlowContributionSource(
            WorkReportDirectGenerationContext context,
            DirectMember member,
            StatRunNonFlowContributionPolicy policy)
    {
        var material = new
        {
            version = "P9_NON_FLOW_CONTRIBUTION_SOURCE_V1",
            Id = member.Report.Id,
            member.Report.PayloadRevision,
            PayloadHash = member.Report.PayloadHash ?? string.Empty,
            member.Report.LifecycleRevision,
            approvalCommandId = member.ApprovalEntry.CommandId,
            approvalEventKey = member.ApprovalEntry.EntryKey,
            member.Report.WorkId,
            member.Report.WorkAssignmentId,
            member.Report.PeriodInstanceKey,
            context.ConfigVersionId,
            context.ConfigHash,
            membershipSignature = context.SourceMembershipSignature,
            policyVersion = policy.Version,
            policyOwnerId = policy.OwnerId,
            policyRevision = policy.Revision,
            policy = policy.Policy,
            policySha256 = policy.PolicySha256,
            contributionOperation = DynamicFlowContributionPolicyContract.Include
        };
        var sourceAuditHash = StatRunCanonicalJson.HashObject(material);
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_NON_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            "P9_NON_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n" +
            $"{sourceAuditId}\n{context.GenerationId}");
        return new WorkReportNonFlowContributionSourceAudit
        {
            SourceAuditId = sourceAuditId,
            SourceReportId = member.Report.Id,
            SourcePayloadRevision = member.Report.PayloadRevision,
            SourcePayloadHash = member.Report.PayloadHash ?? string.Empty,
            SourceLifecycleRevision = member.Report.LifecycleRevision,
            ApprovalCommandId = member.ApprovalEntry.CommandId,
            ApprovalEventKey = member.ApprovalEntry.EntryKey,
            WorkId = member.Report.WorkId,
            WorkAssignmentId = member.Report.WorkAssignmentId,
            PeriodInstanceKey = member.Report.PeriodInstanceKey,
            ConfigVersionId = context.ConfigVersionId,
            ConfigHash = context.ConfigHash,
            MembershipSignature = context.SourceMembershipSignature,
            PolicyVersion = policy.Version,
            PolicyOwnerId = policy.OwnerId,
            PolicyRevision = policy.Revision,
            Policy = policy.Policy,
            PolicySha256 = policy.PolicySha256,
            ContributionOperation = DynamicFlowContributionPolicyContract.Include,
            ReversalIdentity = reversalIdentity,
            SourceAuditHash = sourceAuditHash
        };
    }

    private static IReadOnlyList<ContributionSourceAuditRef>
        BuildContributionSourceRefs(
            IReadOnlyCollection<WorkReportFlowContributionSourceAudit> flow,
            IReadOnlyCollection<WorkReportNonFlowContributionSourceAudit> nonFlow)
    {
        var result = flow.Select(static source => new ContributionSourceAuditRef(
                "FLOW", source.SourceAuditId, source.SourceAuditHash,
                source.SourceReportId, source.SourceLifecycleRevision,
                source.ReversalIdentity))
            .Concat(nonFlow.Select(static source =>
                new ContributionSourceAuditRef(
                    "NON_FLOW", source.SourceAuditId, source.SourceAuditHash,
                    source.SourceReportId, source.SourceLifecycleRevision,
                    source.ReversalIdentity)))
            .OrderBy(static source => source.SourceReportId, StringComparer.Ordinal)
            .ThenBy(static source => source.RuntimeKind, StringComparer.Ordinal)
            .ThenBy(static source => source.SourceAuditId, StringComparer.Ordinal)
            .ToArray();
        if (result.Select(static source => source.SourceReportId)
                .Distinct(StringComparer.Ordinal).Count() != result.Length ||
            result.Select(static source => source.SourceAuditId)
                .Distinct(StringComparer.Ordinal).Count() != result.Length ||
            result.Select(static source => source.ReversalIdentity)
                .Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw Fail("CONTRIBUTION_SOURCE_AUDIT_AMBIGUOUS");
        }
        return result;
    }

    private static void AddContributionTargetsV2<T>(
        ICollection<WorkReportFlowContributionTargetAudit> targets,
        string store,
        IEnumerable<T> rows,
        Func<T, string> id,
        Func<T, WorkReportDirectProjectionPin?> pin,
        IReadOnlyDictionary<string, ContributionSourceAuditRef> sources,
        WorkReportDirectGenerationContext context,
        string receiptId)
    {
        foreach (var row in rows)
        {
            var projection = pin(row);
            if (projection is null ||
                !sources.TryGetValue(projection.SourceReportId, out var source))
                continue;
            var targetId = id(row);
            var targetIdentityHash = StatRunCanonicalJson.HashText(
                CanonicalPersistedRow(row).CanonicalJson);
            var contributionId = StatRunCanonicalJson.HashObject(new
            {
                version = "P9_CONTRIBUTION_TARGET_V2",
                source.RuntimeKind,
                source.SourceAuditId,
                source.SourceAuditHash,
                store,
                targetId,
                targetIdentityHash,
                operation = DynamicFlowContributionPolicyContract.Include,
                operationVersion = ContributionOperationVersion,
                context.RunId,
                receiptId,
                context.GenerationId,
                context.SourceMembershipSignature
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
                store,
                targetId,
                targetIdentityHash,
                operation = DynamicFlowContributionPolicyContract.Include,
                operationVersion = ContributionOperationVersion,
                context.RunId,
                receiptId,
                context.GenerationId,
                reversalIdentity,
                state = WorkReportFlowContributionStates.Applied
            });
            targets.Add(new WorkReportFlowContributionTargetAudit
            {
                ContributionId = contributionId,
                SourceAuditId = source.SourceAuditId,
                SourceReportId = source.SourceReportId,
                TargetStore = store,
                TargetStatisticId = targetId,
                TargetIdentityHash = targetIdentityHash,
                Operation = DynamicFlowContributionPolicyContract.Include,
                OperationVersion = ContributionOperationVersion,
                RunId = context.RunId,
                ReceiptId = receiptId,
                GenerationId = context.GenerationId,
                ReversalIdentity = reversalIdentity,
                State = WorkReportFlowContributionStates.Applied,
                LedgerEntryHash = ledgerEntryHash
            });
        }
    }

    private static void ValidatePersistedContributionAuditV2(
        WorkReportStatisticRebuildJob job)
    {
        var flow = job.FlowContributionSources ?? [];
        var nonFlow = job.NonFlowContributionSources ?? [];
        var targets = job.FlowContributionTargets ?? [];
        if (!ObjectId.TryParse(job.Id, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ReceiptId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.SourceMembershipSignature) ||
            !string.Equals(job.FlowContributionOperationVersion,
                ContributionOperationVersion, StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.FlowContributionLedgerHash) ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                job.FlowContributionReversalBaselineHash) ||
            job.FlowContributionSourceCount != flow.Count ||
            job.NonFlowContributionSourceCount != nonFlow.Count ||
            job.FlowContributionTargetCount != targets.Count)
        {
            throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
        }
        RequireOrderedSources(flow.Select(static item => item.SourceReportId),
            flow.Count);
        RequireOrderedSources(nonFlow.Select(static item => item.SourceReportId),
            nonFlow.Count);
        foreach (var source in flow)
            ValidatePersistedFlowContributionSource(job, source);
        foreach (var source in nonFlow)
            ValidatePersistedNonFlowContributionSource(job, source);
        var sourceRefs = BuildContributionSourceRefs(flow, nonFlow);
        ValidatePersistedContributionTargetsV2(job, sourceRefs, targets);
        var ledgerHash = ComputeContributionLedgerV2(
            job.Id, job.ReceiptId!, job.GenerationId!,
            job.SourceMembershipSignature!, sourceRefs, targets);
        var reversalBaselineHash = ComputeContributionReversalBaselineV2(
            job.Id, job.GenerationId!, ledgerHash, sourceRefs, targets);
        if (!string.Equals(job.FlowContributionLedgerHash,
                ledgerHash, StringComparison.Ordinal) ||
            !string.Equals(job.FlowContributionReversalBaselineHash,
                reversalBaselineHash, StringComparison.Ordinal))
        {
            throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
        }
        ValidatePersistedReversalAudit(job);
    }

    private static void RequireOrderedSources(
        IEnumerable<string> reportIds,
        int expectedCount)
    {
        var actual = reportIds.ToArray();
        var ordered = actual.OrderBy(static value => value,
            StringComparer.Ordinal).ToArray();
        if (actual.Length != expectedCount ||
            !actual.SequenceEqual(ordered, StringComparer.Ordinal) ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
        {
            throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
        }
    }

    private static void ValidatePersistedNonFlowContributionSource(
        WorkReportStatisticRebuildJob job,
        WorkReportNonFlowContributionSourceAudit source)
    {
        if (!ObjectId.TryParse(source.SourceReportId, out _) ||
            source.SourcePayloadRevision < 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.SourcePayloadHash) ||
            source.SourceLifecycleRevision < 1 ||
            string.IsNullOrWhiteSpace(source.ApprovalCommandId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.ApprovalEventKey) ||
            !ObjectId.TryParse(source.WorkId, out _) ||
            !ObjectId.TryParse(source.WorkAssignmentId, out _) ||
            string.IsNullOrWhiteSpace(source.PeriodInstanceKey) ||
            !ObjectId.TryParse(source.ConfigVersionId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.ConfigHash) ||
            !string.Equals(source.MembershipSignature,
                job.SourceMembershipSignature, StringComparison.Ordinal) ||
            !string.Equals(source.PolicyVersion,
                NonFlowContributionPolicyVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(source.PolicyOwnerId) ||
            source.PolicyRevision <= 0 ||
            !string.Equals(source.Policy,
                StatisticReconciliationExpectedContributionPolicies.Include,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(source.PolicySha256) ||
            !string.Equals(source.ContributionOperation,
                DynamicFlowContributionPolicyContract.Include,
                StringComparison.Ordinal))
        {
            throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
        }
        var expectedPolicySha =
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_NON_FLOW_LOCKED_POLICY_V1",
                source.SourceReportId,
                source.PolicyOwnerId,
                source.PolicyRevision.ToString(CultureInfo.InvariantCulture),
                source.Policy);
        var sourceAuditHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_NON_FLOW_CONTRIBUTION_SOURCE_V1",
            Id = source.SourceReportId,
            PayloadRevision = source.SourcePayloadRevision,
            PayloadHash = source.SourcePayloadHash,
            LifecycleRevision = source.SourceLifecycleRevision,
            approvalCommandId = source.ApprovalCommandId,
            approvalEventKey = source.ApprovalEventKey,
            WorkId = source.WorkId,
            WorkAssignmentId = source.WorkAssignmentId,
            PeriodInstanceKey = source.PeriodInstanceKey,
            ConfigVersionId = source.ConfigVersionId,
            ConfigHash = source.ConfigHash,
            membershipSignature = source.MembershipSignature,
            policyVersion = source.PolicyVersion,
            policyOwnerId = source.PolicyOwnerId,
            policyRevision = source.PolicyRevision,
            policy = source.Policy,
            policySha256 = source.PolicySha256,
            contributionOperation = source.ContributionOperation
        });
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_NON_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            "P9_NON_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n" +
            $"{sourceAuditId}\n{job.GenerationId}");
        if (!string.Equals(source.PolicySha256,
                expectedPolicySha, StringComparison.Ordinal) ||
            !string.Equals(source.SourceAuditHash,
                sourceAuditHash, StringComparison.Ordinal) ||
            !string.Equals(source.SourceAuditId,
                sourceAuditId, StringComparison.Ordinal) ||
            !string.Equals(source.ReversalIdentity,
                reversalIdentity, StringComparison.Ordinal))
        {
            throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
        }
    }

    private static void ValidatePersistedContributionTargetsV2(
        WorkReportStatisticRebuildJob job,
        IReadOnlyCollection<ContributionSourceAuditRef> sources,
        IReadOnlyCollection<WorkReportFlowContributionTargetAudit> targets)
    {
        var byAuditId = sources.ToDictionary(static item => item.SourceAuditId,
            StringComparer.Ordinal);
        var targetArray = targets.ToArray();
        var ordered = targetArray
            .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
            .ToArray();
        if (!targetArray.SequenceEqual(ordered) ||
            targetArray.Select(static item =>
                    $"{item.TargetStore}\n{item.TargetStatisticId}")
                .Distinct(StringComparer.Ordinal).Count() != targetArray.Length ||
            targetArray.Select(static item => item.ContributionId)
                .Distinct(StringComparer.Ordinal).Count() != targetArray.Length ||
            targetArray.Select(static item => item.ReversalIdentity)
                .Distinct(StringComparer.Ordinal).Count() != targetArray.Length)
        {
            throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
        }
        foreach (var target in targetArray)
        {
            if (!byAuditId.TryGetValue(target.SourceAuditId, out var source) ||
                !string.Equals(target.SourceReportId,
                    source.SourceReportId, StringComparison.Ordinal) ||
                target.TargetStore is not (
                    "work_report_field_stat_values" or
                    "work_report_table_stat_values" or
                    "work_report_label_stat_values") ||
                !ObjectId.TryParse(target.TargetStatisticId, out _) ||
                !StatRunCanonicalJson.IsCanonicalSha256(target.TargetIdentityHash) ||
                !string.Equals(target.Operation,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal) ||
                !string.Equals(target.OperationVersion,
                    ContributionOperationVersion, StringComparison.Ordinal) ||
                !string.Equals(target.RunId, job.Id, StringComparison.Ordinal) ||
                !string.Equals(target.ReceiptId,
                    job.ReceiptId, StringComparison.Ordinal) ||
                !string.Equals(target.GenerationId,
                    job.GenerationId, StringComparison.Ordinal) ||
                !string.Equals(target.State,
                    WorkReportFlowContributionStates.Applied,
                    StringComparison.Ordinal))
            {
                throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
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
                operation = DynamicFlowContributionPolicyContract.Include,
                operationVersion = ContributionOperationVersion,
                RunId = job.Id,
                receiptId = job.ReceiptId,
                GenerationId = job.GenerationId,
                SourceMembershipSignature = job.SourceMembershipSignature
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
                operation = DynamicFlowContributionPolicyContract.Include,
                operationVersion = ContributionOperationVersion,
                RunId = job.Id,
                receiptId = job.ReceiptId,
                GenerationId = job.GenerationId,
                reversalIdentity,
                state = WorkReportFlowContributionStates.Applied
            });
            if (!string.Equals(target.ContributionId,
                    contributionId, StringComparison.Ordinal) ||
                !string.Equals(target.ReversalIdentity,
                    reversalIdentity, StringComparison.Ordinal) ||
                !string.Equals(target.LedgerEntryHash,
                    ledgerEntryHash, StringComparison.Ordinal))
            {
                throw Fail("PERSISTED_CONTRIBUTION_V2_INVALID");
            }
        }
    }

    private static string ComputeContributionLedgerV2(
        string runId,
        string receiptId,
        string generationId,
        string membershipSignature,
        IReadOnlyCollection<ContributionSourceAuditRef> sources,
        IReadOnlyCollection<WorkReportFlowContributionTargetAudit> targets) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P9_CONTRIBUTION_LEDGER_V2",
            RunId = runId,
            receiptId,
            GenerationId = generationId,
            SourceMembershipSignature = membershipSignature,
            operationVersion = ContributionOperationVersion,
            sources = sources.Select(static source => new
            {
                source.RuntimeKind,
                source.SourceAuditId,
                source.SourceAuditHash,
                source.ReversalIdentity
            }).ToArray(),
            targets = targets.Select(static target => new
            {
                target.ContributionId,
                target.LedgerEntryHash,
                target.ReversalIdentity
            }).ToArray()
        });

    private static string ComputeContributionReversalBaselineV2(
        string runId,
        string generationId,
        string ledgerHash,
        IReadOnlyCollection<ContributionSourceAuditRef> sources,
        IReadOnlyCollection<WorkReportFlowContributionTargetAudit> targets) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P9_CONTRIBUTION_REVERSAL_BASELINE_V2",
            RunId = runId,
            GenerationId = generationId,
            ledgerHash,
            sourceReversals = sources.Select(static source => new
            {
                source.RuntimeKind,
                source.ReversalIdentity
            }).ToArray(),
            targetReversals = targets.Select(static target =>
                target.ReversalIdentity).ToArray()
        });

    private static int? FindContributionSourceLifecycleRevision(
        WorkReportStatisticRebuildJob job,
        string reportId)
    {
        var revisions = (job.FlowContributionSources ?? [])
            .Where(item => string.Equals(item.SourceReportId,
                reportId, StringComparison.Ordinal))
            .Select(static item => item.SourceLifecycleRevision)
            .Concat((job.NonFlowContributionSources ?? [])
                .Where(item => string.Equals(item.SourceReportId,
                    reportId, StringComparison.Ordinal))
                .Select(static item => item.SourceLifecycleRevision))
            .ToArray();
        if (revisions.Length > 1)
            throw Fail("CONTRIBUTION_SOURCE_AUDIT_AMBIGUOUS");
        return revisions.Length == 1 ? revisions[0] : null;
    }

    private static IReadOnlyList<string> ContributionSourceHashesForReport(
        WorkReportStatisticRebuildJob job,
        string reportId) =>
        (job.FlowContributionSources ?? [])
        .Where(item => string.Equals(item.SourceReportId,
            reportId, StringComparison.Ordinal))
        .Select(static item => new ContributionSourceHashRef(
            "FLOW", item.SourceAuditId, item.SourceAuditHash))
        .Concat((job.NonFlowContributionSources ?? [])
            .Where(item => string.Equals(item.SourceReportId,
                reportId, StringComparison.Ordinal))
            .Select(static item => new ContributionSourceHashRef(
                "NON_FLOW", item.SourceAuditId, item.SourceAuditHash)))
        .OrderBy(static item => item.RuntimeKind, StringComparer.Ordinal)
        .ThenBy(static item => item.SourceAuditId, StringComparer.Ordinal)
        .Select(static item => item.SourceAuditHash)
        .ToArray();

    private sealed record ContributionSourceAuditRef(
        string RuntimeKind,
        string SourceAuditId,
        string SourceAuditHash,
        string SourceReportId,
        int SourceLifecycleRevision,
        string ReversalIdentity);

    private sealed record ContributionSourceHashRef(
        string RuntimeKind,
        string SourceAuditId,
        string SourceAuditHash);
}