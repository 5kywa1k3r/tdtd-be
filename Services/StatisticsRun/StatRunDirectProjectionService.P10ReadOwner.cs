using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Services.StatisticsRun;

internal sealed record StatRunDirectSourceOwnerScope(
    string RunId,
    string GenerationId,
    string GenerationSha256,
    string WorkId,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string MembershipSignature,
    long DirectSourceRevision,
    StatRunDirectLifecycleMetricScope LifecycleMetricScope);

internal sealed record StatRunDirectSourceFlowOwnerSnapshot(
    long FlowFamilyRevision,
    string FlowTemplateId,
    string FlowTemplateVersionId,
    int FlowTemplateVersionNo,
    string FlowOriginVersionId,
    string FlowPayloadSha256,
    string FlowCatalogVersion,
    string FlowCatalogSha256,
    string FlowInstanceId,
    long FlowInstanceRevision,
    string FlowInstanceState,
    int CurrentExecutionEpoch,
    string ExecutionEpochId,
    int ExecutionEpoch,
    long ExecutionEpochRevision,
    string ExecutionEpochState,
    bool IsCanonicalEpoch,
    string StepInstanceId,
    long StepRevision,
    string StepState,
    int StepExecutionEpoch,
    bool? StepIsCanonicalEpoch,
    string? StepReportId,
    int? StepReportLifecycleRevision,
    string? StepReportLifecycleStatus,
    bool? StepReportIsActive,
    DateTime? StepInvalidatedAtUtc,
    string? StepInvalidatedByEventId,
    string? StepSupersededByStepInstanceId,
    string FlowStepId,
    string? FlowBranchId,
    int FlowAttemptNo,
    string ContributionPolicy,
    string ContributionSha256,
    string? ContributionWarning);

internal sealed record StatRunDirectSourceMemberOwnerSnapshot(
    string WorkId,
    string WorkAssignmentId,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string ReportId,
    string PayloadDocumentId,
    int PayloadRevision,
    string PayloadSha256,
    int LifecycleRevision,
    string LifecycleSha256,
    string LifecycleStatus,
    bool IsCurrent,
    bool IsActive,
    bool IsDeleted,
    string? InvalidatedByFlowEventId,
    bool AssignmentIsActive,
    bool PeriodIsActive,
    string PeriodStatus,
    string? PeriodCurrentReportId,
    string? PeriodSourceLifecycleReportId,
    int PeriodSourceLifecycleRevision,
    bool PeriodSourceLifecycleApplied,
    int OwnerOrdinal,
    long? MappingRevision,
    string? MappingSemanticSha256,
    string? FlowEffectiveStatus,
    StatRunDirectSourceFlowOwnerSnapshot? FlowRuntime,
    bool OwnerIncluded,
    string ContributionDecision,
    string OwnerDecisionCode,
    bool InLifecycleMetricScope);

internal sealed record StatRunDirectSourceOwnerSnapshot(
    StatRunDirectSourceOwnerScope Scope,
    IReadOnlyList<StatRunDirectSourceMemberOwnerSnapshot> Members);

internal interface IStatRunDirectProjectionReadOwner
{
    Task<StatRunDirectSourceOwnerSnapshot> ReadSourceOwnerAsync(
        StatRunDirectSourceOwnerScope scope,
        CancellationToken cancellationToken);
}

public sealed partial class StatRunDirectProjectionService
    : IStatRunDirectProjectionReadOwner
{
    async Task<StatRunDirectSourceOwnerSnapshot>
        IStatRunDirectProjectionReadOwner.ReadSourceOwnerAsync(
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
            throw Fail("P10_READ_OWNER_PUBLICATION_NOT_EXACT");

        var job = jobs[0];
        await ValidateCompletedPublicationAsync(
            job,
            requireCurrent: true,
            cancellationToken);

        if (!ObjectId.TryParse(job.SourceReportId, out _) ||
            string.IsNullOrWhiteSpace(job.SourceLifecycleEventKey))
        {
            throw Fail("P10_READ_OWNER_TRIGGER_PIN_INVALID");
        }
        var trigger = await LoadSourceAsync(
            job.SourceReportId!,
            job.SourceLifecycleEventKey!,
            cancellationToken);
        var template = await LoadLockedTemplateAsync(
            trigger.Report,
            trigger.Assignment,
            cancellationToken);
        if (!string.Equals(
                template.Id,
                scope.DynamicFormTemplateId,
                StringComparison.Ordinal))
        {
            throw Fail("P10_READ_OWNER_TEMPLATE_PIN_DRIFT");
        }

        var members = await ResolveMembershipAsync(
            scope.WorkId,
            scope.PeriodInstanceKey,
            template,
            cancellationToken);
        var observedMembership = BuildMembershipSignature(members);
        if (!string.Equals(
                observedMembership,
                scope.MembershipSignature,
                StringComparison.Ordinal) ||
            members.Count != job.TotalReportCount)
        {
            throw Fail("P10_READ_OWNER_MEMBERSHIP_DRIFT");
        }

        if (!string.Equals(
                scope.LifecycleMetricScope.FlowInstanceId,
                job.FlowInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                scope.LifecycleMetricScope.ExecutionEpochId,
                job.FlowExecutionEpochId,
                StringComparison.Ordinal) ||
            scope.LifecycleMetricScope.ExecutionEpoch !=
                job.FlowExecutionEpoch ||
            scope.LifecycleMetricScope.ExecutionEpochRevision !=
                job.FlowExecutionEpochRevision ||
            !string.Equals(
                scope.LifecycleMetricScope.RuntimeFlowBranchId,
                job.FlowBranchId, StringComparison.Ordinal) ||
            !string.Equals(
                scope.LifecycleMetricScope.RuntimeFlowStepId,
                job.FlowStepId, StringComparison.Ordinal) ||
            !string.Equals(
                scope.LifecycleMetricScope.FlowEffectiveStatus,
                job.FlowEffectiveStatus, StringComparison.Ordinal))
            throw Fail("P10_READ_OWNER_LIFECYCLE_RUNTIME_SCOPE_DRIFT");

        var snapshots = await ReadLifecycleCandidateOwnerSnapshotsAsync(
            scope,
            members,
            cancellationToken);

        return new StatRunDirectSourceOwnerSnapshot(scope, snapshots);
    }

    private const int MaxLifecycleOwnerCandidates = 49_999;

    private async Task<IReadOnlyList<StatRunDirectSourceMemberOwnerSnapshot>>
        ReadLifecycleCandidateOwnerSnapshotsAsync(
            StatRunDirectSourceOwnerScope scope,
            IReadOnlyCollection<DirectMember> includedMembers,
            CancellationToken cancellationToken)
    {
        var reports = await _ctx.WorkAssignmentReports
            .Find(report =>
                report.WorkId == scope.WorkId &&
                report.PeriodInstanceKey == scope.PeriodInstanceKey &&
                report.DynamicFormTemplateId == scope.DynamicFormTemplateId &&
                !report.IsDeleted)
            .SortBy(report => report.Id)
            .Limit(MaxLifecycleOwnerCandidates + 1)
            .ToListAsync(cancellationToken);
        if (reports.Count > MaxLifecycleOwnerCandidates)
            throw Fail("P10_READ_OWNER_LIFECYCLE_CANDIDATE_LIMIT");

        var includedByReport = includedMembers.ToDictionary(
            static member => member.Report.Id,
            StringComparer.Ordinal);
        if (includedByReport.Count != includedMembers.Count)
            throw Fail("P10_READ_OWNER_INCLUDED_MEMBER_AMBIGUOUS");

        var snapshots = new List<StatRunDirectSourceMemberOwnerSnapshot>(
            reports.Count);
        var observedIncluded = 0;
        foreach (var report in reports)
        {
            if (includedByReport.TryGetValue(report.Id, out var included))
            {
                snapshots.Add(await ReadMemberOwnerSnapshotAsync(
                    included,
                    snapshots.Count,
                    InLifecycleMetricScope(
                        scope.LifecycleMetricScope,
                        included.Assignment),
                    cancellationToken));
                observedIncluded++;
                continue;
            }

            var excluded = await ReadExcludedCandidateOwnerSnapshotAsync(
                scope.LifecycleMetricScope,
                report,
                snapshots.Count,
                cancellationToken);
            if (excluded is not null)
                snapshots.Add(excluded);
        }
        if (observedIncluded != includedByReport.Count)
            throw Fail("P10_READ_OWNER_INCLUDED_MEMBER_NOT_IN_CANDIDATES");
        return snapshots;
    }

    private async Task<StatRunDirectSourceMemberOwnerSnapshot?>
        ReadExcludedCandidateOwnerSnapshotAsync(
            StatRunDirectLifecycleMetricScope lifecycleMetricScope,
            WorkAssignmentReport report,
            int ownerOrdinal,
            CancellationToken cancellationToken)
    {
        var assignments = await _ctx.WorkAssignments
            .Find(assignment =>
                assignment.Id == report.WorkAssignmentId &&
                assignment.WorkId == report.WorkId &&
                !assignment.IsDeleted)
            .SortBy(assignment => assignment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (assignments.Count != 1)
            throw Fail("P10_READ_OWNER_EXCLUDED_ASSIGNMENT_NOT_EXACT");
        var assignment = assignments[0];
        if (!InLifecycleMetricScope(lifecycleMetricScope, assignment))
            return null;

        var periods = await _ctx.WorkReportPeriods
            .Find(period =>
                period.Id == report.WorkReportPeriodId &&
                period.WorkId == report.WorkId &&
                period.WorkAssignmentId == report.WorkAssignmentId &&
                period.PeriodInstanceKey == report.PeriodInstanceKey &&
                !period.IsDeleted)
            .SortBy(period => period.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        var payloads = await _ctx.WorkReportPayloads
            .Find(payload =>
                payload.ReportId == report.Id &&
                payload.PayloadRevision == report.PayloadRevision &&
                payload.PayloadHash == report.PayloadHash &&
                payload.Status == WorkReportPayloadStatus.Ready &&
                !payload.IsDeleted)
            .SortBy(payload => payload.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (periods.Count != 1 || payloads.Count != 1)
            throw Fail("P10_READ_OWNER_EXCLUDED_OWNER_NOT_EXACT");

        var period = periods[0];
        if (report.PayloadRevision < 1 || report.LifecycleRevision < 1 ||
            !string.Equals(report.WorkId, assignment.WorkId,
                StringComparison.Ordinal) ||
            !string.Equals(report.DynamicFormTemplateId,
                assignment.DynamicFormTemplateId, StringComparison.Ordinal) ||
            !string.Equals(report.PeriodKey, period.PeriodKey,
                StringComparison.Ordinal) ||
            !string.Equals(report.PeriodInstanceKey,
                period.PeriodInstanceKey, StringComparison.Ordinal))
        {
            throw Fail("P10_READ_OWNER_EXCLUDED_OWNER_PIN_DRIFT");
        }

        var lifecycleEligible =
            report.Status == WorkAssignmentReportStatus.Approved &&
            report.IsCurrent && report.IsActive &&
            string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId) &&
            assignment.IsActive &&
            string.IsNullOrWhiteSpace(assignment.InvalidatedByFlowEventId);
        DirectRuntimePin? runtime = null;
        DynamicFlowMappingLifecycleBinding? mapping = null;
        if (lifecycleEligible)
        {
            runtime = await ResolveEffectiveRuntimeAsync(
                report,
                assignment,
                cancellationToken);
            if (runtime?.FlowInstanceId is not null)
            {
                mapping = await DynamicFlowMappingLifecycleContract.ValidateAsync(
                    _ctx,
                    report,
                    DynamicFlowMappingIntegrityMode.RequireCurrent,
                    cancellationToken,
                    lifecycleOperation: "P10_LIFECYCLE_CANDIDATE_OWNER");
            }
        }

        var reportIncludes = WorkReportCumulativeContributionPolicy
            .FromReport(report).IncludesReport;
        var contributionIncludes = runtime?.FlowInstanceId is null
            ? reportIncludes
            : string.Equals(
                  runtime.LockedContributionPolicy,
                  DynamicFlowContributionPolicyContract.Include,
                  StringComparison.Ordinal) && mapping is not null;
        if (lifecycleEligible && runtime is not null && contributionIncludes)
            throw Fail("P10_READ_OWNER_INCLUDED_CANDIDATE_OMITTED");

        var flow = runtime is null
            ? null
            : await ReadFlowOwnerSnapshotAsync(
                report,
                assignment,
                runtime,
                cancellationToken);
        return new StatRunDirectSourceMemberOwnerSnapshot(
            report.WorkId,
            report.WorkAssignmentId,
            report.PeriodInstanceKey,
            report.DynamicFormTemplateId
                ?? throw Fail("P10_READ_OWNER_REPORT_TEMPLATE_MISSING"),
            report.Id,
            payloads[0].Id,
            report.PayloadRevision,
            RequireSha(report.PayloadHash,
                "P10_READ_OWNER_PAYLOAD_SHA_INVALID"),
            report.LifecycleRevision,
            LifecycleOwnerSha256(report),
            NormalizeLifecycleOwnerStatus(report),
            report.IsCurrent,
            report.IsActive,
            report.IsDeleted,
            report.InvalidatedByFlowEventId,
            assignment.IsActive,
            period.IsActive,
            period.Status.ToString().ToUpperInvariant(),
            period.CurrentReportId,
            period.SourceLifecycleReportId,
            period.SourceLifecycleRevision,
            period.SourceLifecycleAppliedAtUtc.HasValue,
            ownerOrdinal,
            mapping?.ResultPayloadRevision,
            mapping is null
                ? null
                : await ReadMappingResultSemanticAsync(
                    report, mapping, cancellationToken),
            flow is null ? null : assignment.FlowEffectiveStatus,
            flow,
            OwnerIncluded: false,
            ContributionDecision: "EXCLUDE",
            OwnerDecisionCode: lifecycleEligible
                ? "P9_DIRECT_CANDIDATE_POLICY_EXCLUDED"
                : "P9_DIRECT_CANDIDATE_LIFECYCLE_EXCLUDED",
            InLifecycleMetricScope: true);
    }

    private static string NormalizeLifecycleOwnerStatus(
        WorkAssignmentReport report)
    {
        var operation = report.LastLifecycleCommandOperation?
            .Trim().ToUpperInvariant();
        if (operation is "TERMINATE" or "TERMINATED")
            return "TERMINATED";
        if (operation is "RECALL" or "RECALLED")
            return "RECALLED";
        if (operation is "RETURN" or "RETURNED")
            return "RETURNED";
        if (!string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId))
            return "INVALIDATED";
        if (!report.IsCurrent)
            return "SUPERSEDED";
        return report.Status.ToString().ToUpperInvariant();
    }

    private static string LifecycleOwnerSha256(WorkAssignmentReport report)
        => StatRunCanonicalJson.IsCanonicalSha256(
                report.LastLifecycleCommandHash)
            ? report.LastLifecycleCommandHash!
            : HashFields(
                "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_OWNER_V1",
                report.Id,
                report.WorkAssignmentId,
                report.PayloadRevision.ToString(CultureInfo.InvariantCulture),
                report.LifecycleRevision.ToString(CultureInfo.InvariantCulture),
                report.PayloadHash ?? "~",
                report.Status.ToString(),
                report.IsActive ? "1" : "0",
                report.IsCurrent ? "1" : "0",
                report.LastLifecycleCommandOperation ?? "~",
                report.InvalidatedByFlowEventId ?? "~");

    private static string HashFields(string domain, params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, domain);
        foreach (var field in fields)
            AppendHashField(hash, field);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = Encoding.ASCII.GetBytes(
            bytes.Length.ToString(CultureInfo.InvariantCulture));
        hash.AppendData(length);
        hash.AppendData([(byte)':']);
        hash.AppendData(bytes);
        hash.AppendData([(byte)'\n']);
    }
    private async Task<StatRunDirectSourceMemberOwnerSnapshot>
        ReadMemberOwnerSnapshotAsync(
            DirectMember member,
            int ownerOrdinal,
            bool inLifecycleMetricScope,
            CancellationToken cancellationToken)
    {
        var report = member.Report;
        var payloads = await _ctx.WorkReportPayloads
            .Find(payload =>
                payload.ReportId == report.Id &&
                payload.PayloadRevision == report.PayloadRevision &&
                payload.PayloadHash == report.PayloadHash &&
                payload.Status == WorkReportPayloadStatus.Ready &&
                !payload.IsDeleted)
            .SortBy(payload => payload.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (payloads.Count != 1)
            throw Fail("P10_READ_OWNER_PAYLOAD_NOT_EXACT");

        var runtime = await ReadFlowOwnerSnapshotAsync(
            member,
            cancellationToken);
        var mapping = member.Mapping;
        if (runtime is not null && mapping is null)
            throw Fail("P10_READ_OWNER_FLOW_MAPPING_PIN_MISSING");

        return new StatRunDirectSourceMemberOwnerSnapshot(
            report.WorkId,
            report.WorkAssignmentId,
            report.PeriodInstanceKey,
            report.DynamicFormTemplateId
                ?? throw Fail("P10_READ_OWNER_REPORT_TEMPLATE_MISSING"),
            report.Id,
            payloads[0].Id,
            report.PayloadRevision,
            RequireSha(report.PayloadHash, "P10_READ_OWNER_PAYLOAD_SHA_INVALID"),
            report.LifecycleRevision,
            RequireSha(
                report.LastLifecycleCommandHash,
                "P10_READ_OWNER_LIFECYCLE_SHA_INVALID"),
            report.Status.ToString(),
            report.IsCurrent,
            report.IsActive,
            report.IsDeleted,
            report.InvalidatedByFlowEventId,
            member.Assignment.IsActive,
            member.Period.IsActive,
            member.Period.Status.ToString().ToUpperInvariant(),
            member.Period.CurrentReportId,
            member.Period.SourceLifecycleReportId,
            member.Period.SourceLifecycleRevision,
            member.Period.SourceLifecycleAppliedAtUtc.HasValue,
            ownerOrdinal,
            mapping?.ResultPayloadRevision,
            mapping is null
                ? null
                : await ReadMappingResultSemanticAsync(
                    report, mapping, cancellationToken),
            runtime is null ? null : member.Assignment.FlowEffectiveStatus,
            runtime,
            OwnerIncluded: true,
            ContributionDecision: "INCLUDE",
            OwnerDecisionCode: "P9_DIRECT_MEMBER_INCLUDED",
            InLifecycleMetricScope: inLifecycleMetricScope);
    }

    private async Task<string> ReadMappingResultSemanticAsync(
        WorkAssignmentReport report,
        DynamicFlowMappingLifecycleBinding mapping,
        CancellationToken cancellationToken)
    {
        var receipts = await _ctx.DynamicFlowMappingApplyReceipts
            .Find(receipt =>
                receipt.Id == mapping.ReceiptId &&
                receipt.TargetReportId == report.Id &&
                receipt.ProvenanceId == mapping.ProvenanceId &&
                receipt.ProvenanceHash == mapping.ProvenanceHash &&
                receipt.ResultPayloadRevision == mapping.ResultPayloadRevision &&
                receipt.ResultPayloadHash == mapping.ResultPayloadHash &&
                (receipt.State == DynamicFlowMappingApplyStates.Committed ||
                 receipt.State == DynamicFlowMappingApplyStates.Reconciled))
            .SortBy(receipt => receipt.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (receipts.Count != 1 ||
            !StatRunCanonicalJson.IsCanonicalSha256(
                receipts[0].ResultSemanticHash))
            throw Fail("P10_READ_OWNER_MAPPING_RECEIPT_NOT_EXACT");
        return receipts[0].ResultSemanticHash;
    }
    private Task<StatRunDirectSourceFlowOwnerSnapshot?>
        ReadFlowOwnerSnapshotAsync(
            DirectMember member,
            CancellationToken cancellationToken)
        => ReadFlowOwnerSnapshotAsync(
            member.Report,
            member.Assignment,
            member.Runtime,
            cancellationToken);
    private async Task<StatRunDirectSourceFlowOwnerSnapshot?>
        ReadFlowOwnerSnapshotAsync(
            WorkAssignmentReport report,
            WorkAssignment assignment,
            DirectRuntimePin runtime,
            CancellationToken cancellationToken)
    {
        if (runtime.FlowInstanceId is null)
            return null;

        var epochId = RequireObjectId(
            runtime.ExecutionEpochId,
            "P10_READ_OWNER_EPOCH_ID_INVALID");
        var stepId = RequireObjectId(
            runtime.StepInstanceId,
            "P10_READ_OWNER_STEP_ID_INVALID");
        var epochs = await _ctx.DynamicFlowExecutionEpochs
            .Find(epoch =>
                epoch.Id == epochId &&
                epoch.FlowInstanceId == runtime.FlowInstanceId &&
                epoch.ExecutionEpoch == runtime.ExecutionEpoch &&
                !epoch.IsDeleted)
            .SortBy(epoch => epoch.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        var steps = await _ctx.DynamicFlowStepInstances
            .Find(step =>
                step.Id == stepId &&
                step.FlowInstanceId == runtime.FlowInstanceId &&
                step.AssignmentId == assignment.Id &&
                !step.IsDeleted)
            .SortBy(step => step.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        if (epochs.Count != 1 || steps.Count != 1)
            throw Fail("P10_READ_OWNER_RUNTIME_PIN_NOT_EXACT");

        var epoch = epochs[0];
        var step = steps[0];
        if (epoch.Revision != runtime.ExecutionEpochRevision ||
            !string.Equals(epoch.State, runtime.ExecutionEpochState,
                StringComparison.Ordinal) ||
            step.Revision != runtime.StepInstanceRevision ||
            !string.Equals(step.State, runtime.StepInstanceState,
                StringComparison.Ordinal) ||
            step.ExecutionEpoch != runtime.ExecutionEpoch ||
            step.ReportLifecycleRevision != report.LifecycleRevision ||
            !string.Equals(step.ReportLifecycleStatus, "APPROVED",
                StringComparison.Ordinal) ||
            step.ReportLifecycleIsActive != true)
        {
            throw Fail("P10_READ_OWNER_RUNTIME_PIN_DRIFT");
        }

        return new StatRunDirectSourceFlowOwnerSnapshot(
            RequirePositive(
                runtime.FlowFamilyRevision,
                "P10_READ_OWNER_FLOW_FAMILY_REVISION_INVALID"),
            RequireObjectId(
                assignment.FlowTemplateId,
                "P10_READ_OWNER_FLOW_TEMPLATE_ID_INVALID"),
            RequireObjectId(
                runtime.FlowTemplateVersionId,
                "P10_READ_OWNER_FLOW_VERSION_ID_INVALID"),
            RequirePositive(
                assignment.FlowTemplateVersionNo,
                "P10_READ_OWNER_FLOW_VERSION_NO_INVALID"),
            RequireObjectId(
                runtime.FlowContributionOriginVersionId,
                "P10_READ_OWNER_FLOW_ORIGIN_ID_INVALID"),
            RequireSha(
                runtime.FlowPayloadHash,
                "P10_READ_OWNER_FLOW_PAYLOAD_SHA_INVALID"),
            RequireText(
                runtime.FlowCatalogVersion,
                "P10_READ_OWNER_FLOW_CATALOG_VERSION_INVALID"),
            RequireSha(
                runtime.FlowCatalogSemanticHash,
                "P10_READ_OWNER_FLOW_CATALOG_SHA_INVALID"),
            RequireObjectId(
                runtime.FlowInstanceId,
                "P10_READ_OWNER_FLOW_INSTANCE_ID_INVALID"),
            RequirePositive(
                runtime.FlowInstanceRevision,
                "P10_READ_OWNER_FLOW_INSTANCE_REVISION_INVALID"),
            RequireText(
                runtime.FlowInstanceState,
                "P10_READ_OWNER_FLOW_INSTANCE_STATE_INVALID"),
            RequirePositive(
                assignment.FlowExecutionEpoch,
                "P10_READ_OWNER_CURRENT_EPOCH_INVALID"),
            epoch.Id,
            epoch.ExecutionEpoch,
            epoch.Revision,
            epoch.State,
            epoch.IsCanonical,
            step.Id,
            step.Revision,
            step.State,
            step.ExecutionEpoch,
            step.IsCanonicalEpoch,
            step.ReportId,
            step.ReportLifecycleRevision,
            step.ReportLifecycleStatus,
            step.ReportLifecycleIsActive,
            step.InvalidatedAtUtc,
            step.InvalidatedByFlowEventId,
            step.SupersededByStepInstanceId,
            step.FlowStepId,
            step.BranchId,
            step.AttemptNo,
            RequireText(
                runtime.LockedContributionPolicy,
                "P10_READ_OWNER_CONTRIBUTION_POLICY_INVALID"),
            RequireSha(
                runtime.LockedContributionPolicyHash,
                "P10_READ_OWNER_CONTRIBUTION_SHA_INVALID"),
            runtime.LockedContributionWarning);
    }

    internal static bool InLifecycleMetricScope(
        StatRunDirectLifecycleMetricScope scope,
        WorkAssignment assignment)
    {
        if (!StatRunDirectLifecycleMetricScopeCanonical.IncludesAssignment(
                scope,
                assignment.Id,
                assignment.ParentAssignmentId,
                assignment.FlowInstanceId,
                assignment.FlowBranchId,
                assignment.FlowStepId,
                assignment.FlowEffectiveStatus,
                assignment.IsFlowFinalNode))
            return false;
        if (scope.FlowInstanceId is null)
        {
            if (assignment.FlowExecutionEpoch is not null)
                throw Fail("P10_READ_OWNER_LIFECYCLE_EPOCH_INVALID");
            return true;
        }
        if (assignment.FlowExecutionEpoch is not > 0 ||
            assignment.FlowExecutionEpoch > scope.ExecutionEpoch)
        {
            throw Fail("P10_READ_OWNER_LIFECYCLE_EPOCH_OUT_OF_BOUND");
        }
        return true;
    }
    private static void RequireReadOwnerScope(
        StatRunDirectSourceOwnerScope scope)
    {
        _ = StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
            scope.LifecycleMetricScope);
        if (!ObjectId.TryParse(scope.RunId, out _) ||
            !ObjectId.TryParse(scope.WorkId, out _) ||
            !ObjectId.TryParse(scope.DynamicFormTemplateId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(scope.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(scope.GenerationSha256) ||
            !StatRunCanonicalJson.IsCanonicalSha256(scope.MembershipSignature) ||
            string.IsNullOrWhiteSpace(scope.PeriodInstanceKey) ||
            scope.DirectSourceRevision < 1)
        {
            throw Fail("P10_READ_OWNER_SCOPE_INVALID");
        }
    }

    private static string RequireText(string? value, string reason)
        => string.IsNullOrWhiteSpace(value)
            ? throw Fail(reason)
            : value.Trim();

    private static string RequireSha(string? value, string reason)
        => StatRunCanonicalJson.IsCanonicalSha256(value)
            ? value!
            : throw Fail(reason);

    private static int RequirePositive(int? value, string reason)
        => value is > 0 ? value.Value : throw Fail(reason);

    private static long RequirePositive(long? value, string reason)
        => value is > 0 ? value.Value : throw Fail(reason);
}
