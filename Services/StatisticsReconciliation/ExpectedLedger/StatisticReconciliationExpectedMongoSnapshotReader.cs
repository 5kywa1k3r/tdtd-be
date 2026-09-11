using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

/// <summary>
/// Production authoritative reader. It independently reconstructs lifecycle,
/// assignment, period, current-epoch and contribution membership from immutable
/// Mongo owners. P9 pins are validated only as a frozen comparison boundary; no
/// P9 member, result, projection, aggregate, API or export value is consumed.
/// Payload bytes are re-read from exact WorkReportPayload revisions and the P8
/// metric plan is re-read from its exact locked configuration owner.
/// </summary>
internal sealed class StatisticReconciliationExpectedMongoSnapshotReader(
    MongoDbContext context)
    : IStatisticReconciliationExpectedAuthoritativeSnapshotReader
{
    public async ValueTask<ExpectedAuthoritativeSourceSnapshot> ReadAsync(
        ExpectedLedgerCompilationContextPin requestedContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedContext);
        RequireProductionSourceOwnerPins(requestedContext);

        var runs = await context.StatisticReconciliationRuns
            .Find(run => run.Id == requestedContext.ReconciliationId)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (runs.Count != 1)
            throw Fail("RUN_NOT_EXACT");
        var persistedRun = runs[0];
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            persistedRun);
        var run = StatisticReconciliationRecheckCaptureBindingCanonical
            .EffectiveCaptureRun(persistedRun);
        RequireRunContext(run, requestedContext);

        await RequireFrozenSourceBoundaryAsync(
            requestedContext,
            cancellationToken).ConfigureAwait(false);
        var configuration = await ReadLockedConfigurationAsync(
            run,
            requestedContext,
            cancellationToken).ConfigureAwait(false);
        var usesDynamicFormConfiguration = configuration.Pins.Any(pin =>
            pin.Kind ==
            StatisticReconciliationExpectedLedgerConfigurationKinds.DynamicForm);
        var statisticConfig = await ReadStatisticConfigAsync(
            requestedContext,
            cancellationToken).ConfigureAwait(false);
        var metricScope = ReadMetricScope(
            requestedContext,
            configuration.ConfigurationJson);
        var sources = await ReadAuthoritativeSourcesAsync(
            requestedContext,
            metricScope,
            cancellationToken).ConfigureAwait(false);

        var lifecycle = ImmutableArray.CreateBuilder<
            ExpectedLifecycleRevisionCandidate>(sources.Count);
        var contributions = ImmutableArray.CreateBuilder<
            ExpectedContributionCandidate>(sources.Count);
        foreach (var source in sources
                     .OrderBy(item => item.Report.Id, StringComparer.Ordinal))
        {
            var report = source.Report;
            var payload = source.Payload;
            var authoritativePayload = await
                StatisticReconciliationExpectedAuthoritativePayloadOwner.LoadAsync(
                    context,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(
                    authoritativePayload.PayloadOwnerSha256,
                    report.PayloadHash))
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
                    "$.authoritativeSnapshot.payload",
                    StatisticReconciliationExpectedLedgerInputFailureReasons
                        .PayloadJsonInvalid);
            var stable =
                StatisticReconciliationExpectedMembershipIntegrity
                    .BuildStableIdentitySha256(
                        report.WorkId,
                        report.WorkAssignmentId,
                        report.Id);
            var lifecycleOwnerSha256 = BuildLifecycleOwnerSha256(report);
            var contribution = BuildContribution(stable, source);
            var runtimePin = BuildRuntimePin(
                requestedContext,
                source,
                contribution,
                lifecycleOwnerSha256,
                statisticConfig.VersionId,
                statisticConfig.Sha256);
            var identity = new ExpectedSourceIdentityPin(
                stable,
                report.Id,
                report.WorkId,
                requestedContext.ScopeAssignmentId,
                report.WorkAssignmentId,
                report.PayloadRevision,
                RequireSha(report.PayloadHash, "PAYLOAD_OWNER_SHA_INVALID"),
                canonical.Sha256,
                report.LifecycleRevision,
                lifecycleOwnerSha256,
                requestedContext.DynamicFormVersionId,
                requestedContext.RuntimeKind,
                requestedContext.FlowInstanceId,
                requestedContext.ExecutionEpochId);
            lifecycle.Add(new ExpectedLifecycleRevisionCandidate(
                stable,
                identity,
                payload.Id,
                canonical.Value,
                NormalizeLifecycleStatus(report),
                source.IsEffective,
                source.IsLocked,
                source.RuntimeDisposition,
                runtimePin));
            contributions.Add(contribution);
        }
        var lineage = BuildLineage(requestedContext, sources);
        var membership = new CurrentEpochFlowMembership(
            requestedContext,
            requestedContext.RuntimeKind,
            requestedContext.FlowTemplateVersionId,
            requestedContext.FlowPayloadSha256,
            requestedContext.FlowInstanceId,
            requestedContext.ExecutionEpochId,
            requestedContext.RuntimeKind ==
                    StatisticReconciliationExpectedRuntimeKinds.Flow
                ? run.FlowExecutionEpoch
                : null,
            requestedContext.RuntimeKind ==
                    StatisticReconciliationExpectedRuntimeKinds.Flow
                ? run.FlowExecutionEpochRevision
                : null,
            sources.Where(item => item.InCurrentEpoch)
                .Select(item => StatisticReconciliationExpectedMembershipIntegrity
                    .BuildStableIdentitySha256(
                        item.Report.WorkId,
                        item.Report.WorkAssignmentId,
                        item.Report.Id)));

        return new ExpectedAuthoritativeSourceSnapshot(
            requestedContext,
            lifecycle,
            membership,
            configuration,
            lineage,
            contributions);
    }

    private async Task<AuthoritativeStatisticConfig> ReadStatisticConfigAsync(
        ExpectedLedgerCompilationContextPin requested,
        CancellationToken cancellationToken)
    {
        var values = await context.DynamicFormTemplates
            .Find(item =>
                item.Id == requested.DynamicFormVersionId &&
                !item.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0].StatisticConfigVersionId) ||
            !IsLowerSha256(values[0].StatisticConfigHash) ||
            values[0].StatisticConfigVersionNo <= 0 ||
            values[0].StatisticConfigRevision <= 0)
            throw Fail("STATISTIC_CONFIG_PIN_REQUIRED");
        return new(
            values[0].StatisticConfigVersionId!,
            values[0].StatisticConfigHash!);
    }
    private async Task<LockedP8Configuration> ReadLockedConfigurationAsync(
        StatisticReconciliationRun run,
        ExpectedLedgerCompilationContextPin contextPin,
        CancellationToken cancellationToken)
    {
        if (run.P8ConfigOwnerId is null ||
            run.P8ConfigId is null ||
            run.P8ConfigVersionId is null ||
            run.P8ConfigVersionNo is null or <= 0 ||
            run.P8ConfigRevision is null or <= 0 ||
            run.P8ConfigHash is null)
            throw Fail("P8_CONFIG_PIN_REQUIRED");

        if (StringComparer.Ordinal.Equals(
                run.P9ResultKind,
                StatisticReconciliationP9ResultKinds.Direct))
        {
            var owners = await context.DynamicFormTemplates
                .Find(item =>
                    item.Id == run.P8ConfigOwnerId &&
                    item.Id == run.DynamicFormVersionId &&
                    !item.IsDeleted)
                .Limit(2)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var candidates = owners
                .Select(owner =>
                    StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                        .TryBuildCandidate(owner, run))
                .OfType<StatisticReconciliationExpectedLockedP8OwnerCandidate>();
            var dynamicFormOwner =
                StatisticReconciliationExpectedDynamicFormConfigurationAdapter
                    .RequireExact(candidates);
            return BuildLockedConfiguration(contextPin, dynamicFormOwner);
        }

        var matches = new List<(string Kind, string Id, string Json, string Hash,
            string VersionId, int VersionNo, long Revision)>();

        var basic = await context.WorkAssignmentBasicSummaryConfigs
            .Find(item =>
                item.Id == run.P8ConfigId &&
                item.WorkId == run.WorkId &&
                item.AssignmentId == run.ScopeAssignmentId &&
                item.IsActive)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        matches.AddRange(basic
            .Where(item =>
                item.VersionId == run.P8ConfigVersionId &&
                item.VersionNo == run.P8ConfigVersionNo &&
                item.Revision == run.P8ConfigRevision &&
                item.ConfigHash == run.P8ConfigHash &&
                string.Equals(item.Status, "LOCKED", StringComparison.Ordinal))
            .Select(item => (
                StatisticReconciliationExpectedLedgerConfigurationKinds.Basic,
                item.Id,
                item.ConfigJson,
                item.ConfigHash!,
                item.VersionId!,
                item.VersionNo,
                item.Revision)));

        var advanced = await context.WorkAssignmentAdvancedSummaryConfigs
            .Find(item =>
                item.Id == run.P8ConfigId &&
                item.WorkId == run.WorkId &&
                item.AssignmentId == run.ScopeAssignmentId)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        matches.AddRange(advanced
            .Where(item =>
                item.ConfigId == run.P8ConfigVersionId &&
                item.VersionNo == run.P8ConfigVersionNo &&
                item.Revision == run.P8ConfigRevision &&
                item.ConfigHash == run.P8ConfigHash &&
                item.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked)
            .Select(item => (
                StatisticReconciliationExpectedLedgerConfigurationKinds.Advanced,
                item.Id,
                item.ConfigJson,
                item.ConfigHash,
                item.ConfigId,
                item.VersionNo,
                item.Revision)));

        var diff = await context.WorkReportStatisticDiffConfigs
            .Find(item =>
                item.Id == run.P8ConfigId &&
                item.WorkId == run.WorkId &&
                item.AssignmentId == run.ScopeAssignmentId &&
                item.IsActive)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        matches.AddRange(diff
            .Where(item =>
                item.ConfigId == run.P8ConfigVersionId &&
                item.VersionNo == run.P8ConfigVersionNo &&
                item.Revision == run.P8ConfigRevision &&
                item.ConfigHash == run.P8ConfigHash &&
                string.Equals(item.Status, "LOCKED", StringComparison.Ordinal))
            .Select(item => (
                StatisticReconciliationExpectedLedgerConfigurationKinds.Diff,
                item.Id,
                item.ConfigJson,
                item.ConfigHash!,
                item.ConfigId!,
                item.VersionNo,
                item.Revision)));

        if (matches.Count != 1)
            throw Fail("P8_CONFIG_OWNER_NOT_EXACT");
        var owner = matches[0];
        var canonical =
            StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
                owner.Json,
                4 * 1024 * 1024,
                "$.authoritativeSnapshot.lockedP8Configuration",
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .ConfigurationInvalid);
        return new LockedP8Configuration(
            contextPin,
            contextPin.P8ConfigurationOwnerId,
            contextPin.P8ConfigurationBundleSha256,
            canonical.Sha256,
            canonical.Value,
            [
                new LockedP8ConfigurationPin(
                    owner.Kind,
                    contextPin.P8ConfigurationOwnerId,
                    run.P8ConfigId,
                    owner.VersionId,
                    owner.VersionNo,
                    owner.Revision,
                    owner.Hash)
            ]);
    }

    private static LockedP8Configuration BuildLockedConfiguration(
        ExpectedLedgerCompilationContextPin contextPin,
        StatisticReconciliationExpectedLockedP8OwnerCandidate owner)
    {
        var canonical =
            StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
                owner.ConfigurationJson,
                4 * 1024 * 1024,
                "$.authoritativeSnapshot.lockedP8Configuration",
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .ConfigurationInvalid);
        return new LockedP8Configuration(
            contextPin,
            owner.OwnerId,
            contextPin.P8ConfigurationBundleSha256,
            canonical.Sha256,
            canonical.Value,
            [
                new LockedP8ConfigurationPin(
                    owner.Kind,
                    owner.OwnerId,
                    owner.ConfigId,
                    owner.VersionId,
                    owner.VersionNo,
                    owner.Revision,
                    owner.ConfigSha256)
            ]);
    }

    private static AuthoritativeMetricScope ReadMetricScope(
        ExpectedLedgerCompilationContextPin requested,
        string configurationJson)
    {
        using var document = JsonDocument.Parse(configurationJson);
        if (!document.RootElement.TryGetProperty(
                "expectedMetrics",
                out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array ||
            metrics.GetArrayLength() == 0)
            throw Fail("EXPECTED_METRIC_PLAN_REQUIRED");
        string? family = null;
        string? scopeKind = null;
        string? scopeId = null;
        foreach (var metric in metrics.EnumerateArray())
        {
            if (metric.ValueKind != JsonValueKind.Object)
                throw Fail("EXPECTED_METRIC_ENTRY_INVALID");
            var currentFamily = JsonText(metric, "family")
                .ToUpperInvariant();
            var currentScope = JsonOptionalText(metric, "scopeKind")?
                .ToUpperInvariant();
            var currentScopeId = JsonOptionalText(metric, "scopeId");
            family ??= currentFamily;
            scopeKind ??= currentScope;
            scopeId ??= currentScopeId;
            if (family != currentFamily || scopeKind != currentScope ||
                scopeId != currentScopeId)
                throw Fail("EXPECTED_METRIC_SCOPE_MIXED");
        }
        if (family == StatisticReconciliationExpectedMetricFamilies.Basic)
        {
            if (scopeKind is null || scopeId is null)
                throw Fail("BASIC_SCOPE_REQUIRED");
            var flowScope = scopeKind is
                StatisticReconciliationExpectedBasicScopes.FlowBranch or
                StatisticReconciliationExpectedBasicScopes.FlowStep or
                StatisticReconciliationExpectedBasicScopes.FlowEffectivePath or
                StatisticReconciliationExpectedBasicScopes.FlowFinal;
            if (flowScope != (requested.RuntimeKind ==
                    StatisticReconciliationExpectedRuntimeKinds.Flow))
                throw Fail("BASIC_RUNTIME_SCOPE_MISMATCH");
            if ((scopeKind is
                    StatisticReconciliationExpectedBasicScopes
                        .DirectChildrenOrSelf or
                    StatisticReconciliationExpectedBasicScopes.DirectChildren) &&
                scopeId != requested.ScopeAssignmentId)
                throw Fail("BASIC_ASSIGNMENT_SCOPE_MISMATCH");
            if (scopeKind ==
                    StatisticReconciliationExpectedBasicScopes.FlowBranch &&
                scopeId.Length == 0)
                throw Fail("BASIC_FLOW_BRANCH_SCOPE_INVALID");
            if (scopeKind ==
                    StatisticReconciliationExpectedBasicScopes.FlowStep &&
                string.IsNullOrWhiteSpace(scopeId))
                throw Fail("BASIC_FLOW_STEP_SCOPE_INVALID");
        }
        else if (scopeKind is not null || scopeId is not null)
        {
            throw Fail("NON_BASIC_SCOPE_FORBIDDEN");
        }
        return new AuthoritativeMetricScope(family!, scopeKind, scopeId);
    }

    private static string JsonText(JsonElement owner, string property)
        => JsonOptionalText(owner, property) ??
           throw Fail($"EXPECTED_METRIC_{property.ToUpperInvariant()}_REQUIRED");

    private static string? JsonOptionalText(
        JsonElement owner,
        string property)
        => owner.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
    private async Task RequireFrozenSourceBoundaryAsync(
        ExpectedLedgerCompilationContextPin requested,
        CancellationToken cancellationToken)
    {
        var jobs = await context.WorkReportStatisticRebuildJobs
            .Find(job =>
                job.Id == requested.SourceOwnerRunId &&
                job.GenerationId == requested.SourceOwnerGenerationId &&
                job.GenerationHash == requested.SourceOwnerGenerationSha256 &&
                job.SourceMembershipSignature ==
                    requested.SourceOwnerMembershipSha256 &&
                job.DirectSourceRevision == requested.SourceOwnerRevision &&
                job.WorkId == requested.WorkId &&
                job.PeriodInstanceKey == requested.PeriodInstanceKey &&
                job.DynamicFormTemplateId == requested.DynamicFormVersionId &&
                job.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                !job.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (jobs.Count != 1)
            throw Fail("SOURCE_OWNER_BOUNDARY_NOT_EXACT");
    }

    private async Task<IReadOnlyList<AuthoritativeSource>>
        ReadAuthoritativeSourcesAsync(
            ExpectedLedgerCompilationContextPin requested,
            AuthoritativeMetricScope metricScope,
            CancellationToken cancellationToken)
    {
        var reports = await context.WorkAssignmentReports
            .Find(report =>
                report.WorkId == requested.WorkId &&
                report.PeriodInstanceKey == requested.PeriodInstanceKey &&
                report.DynamicFormTemplateId == requested.DynamicFormVersionId &&
                !report.IsDeleted)
            .SortBy(report => report.Id)
            .Limit(StatisticReconciliationExpectedSourcePlanningLimits
                       .MaxLifecycleCandidates + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (reports.Count >
            StatisticReconciliationExpectedSourcePlanningLimits
                .MaxLifecycleCandidates)
            throw Fail("SOURCE_CANDIDATE_LIMIT_EXCEEDED");

        var result = new List<AuthoritativeSource>(reports.Count);
        foreach (var report in reports)
        {
            if (report.PayloadRevision <= 0 || report.LifecycleRevision <= 0)
                throw Fail("SOURCE_REVISION_INVALID");
            var assignments = await context.WorkAssignments
                .Find(assignment =>
                    assignment.Id == report.WorkAssignmentId &&
                    assignment.WorkId == report.WorkId &&
                    !assignment.IsDeleted)
                .Limit(2)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (assignments.Count != 1)
                throw Fail("SOURCE_ASSIGNMENT_NOT_EXACT");
            var assignment = assignments[0];
            if (requested.RuntimeKind ==
                    StatisticReconciliationExpectedRuntimeKinds.Flow
                    ? !StringComparer.Ordinal.Equals(
                        assignment.FlowInstanceId,
                        requested.FlowInstanceId)
                    : assignment.FlowInstanceId is not null)
                continue;
            if (!AssignmentInMetricScope(
                    requested,
                    metricScope,
                    assignment))
                continue;
            var periods = await context.WorkReportPeriods
                .Find(period =>
                    period.Id == report.WorkReportPeriodId &&
                    period.WorkId == report.WorkId &&
                    period.WorkAssignmentId == report.WorkAssignmentId &&
                    period.PeriodInstanceKey == requested.PeriodInstanceKey &&
                    !period.IsDeleted)
                .Limit(2)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (periods.Count != 1)
                throw Fail("SOURCE_PERIOD_NOT_EXACT");
            var period = periods[0];
            var payloads = await context.WorkReportPayloads
                .Find(payload =>
                    payload.ReportId == report.Id &&
                    payload.PayloadRevision == report.PayloadRevision &&
                    payload.PayloadHash == report.PayloadHash &&
                    payload.Status == WorkReportPayloadStatus.Ready &&
                    !payload.IsDeleted)
                .Limit(2)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (payloads.Count != 1)
                throw Fail("PAYLOAD_REVISION_NOT_EXACT");

            RequireAuthoritativePins(requested, report, assignment, period);
            var runtime = await ReadRuntimeAsync(
                requested,
                report,
                assignment,
                cancellationToken).ConfigureAwait(false);
            RequireFlowRuntimePin(
                requested.RuntimeKind,
                runtime is not null);
            var currentEpoch = requested.RuntimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.NonFlow ||
                runtime?.IsCurrentEpoch == true;
            var effective = report.IsCurrent &&
                            report.IsActive &&
                            assignment.IsActive &&
                            string.IsNullOrWhiteSpace(
                                report.InvalidatedByFlowEventId) &&
                            string.IsNullOrWhiteSpace(
                                assignment.InvalidatedByFlowEventId) &&
                            currentEpoch &&
                            period.IsActive &&
                            string.Equals(
                                period.CurrentReportId,
                                report.Id,
                                StringComparison.Ordinal) &&
                            period.SourceLifecycleRevision ==
                                report.LifecycleRevision &&
                            string.Equals(
                                period.SourceLifecycleReportId,
                                report.Id,
                                StringComparison.Ordinal) &&
                            period.SourceLifecycleAppliedAtUtc.HasValue &&
                            period.Status is (
                                WorkReportPeriodStatus.Approved or
                                WorkReportPeriodStatus.OverdueApproved);
            var disposition = ResolveDisposition(
                report,
                assignment,
                currentEpoch);
            var locked = HasCanonicalLifecyclePin(report);
            var lockedPolicy = runtime?.FlowVersion.ContributionPolicy;
            var include = ResolveSourceInclude(
                report,
                requested.RuntimeKind,
                runtime is not null,
                lockedPolicy,
                runtime?.Mapping is not null);
            result.Add(new AuthoritativeSource(
                report,
                assignment,
                period,
                payloads[0],
                runtime,
                currentEpoch,
                effective,
                locked,
                disposition,
                include));
        }
        return result;
    }

    private async Task<AuthoritativeRuntime?> ReadRuntimeAsync(
        ExpectedLedgerCompilationContextPin requested,
        WorkAssignmentReport report,
        WorkAssignment assignment,
        CancellationToken cancellationToken)
    {
        if (requested.RuntimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.NonFlow)
        {
            if (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
                throw Fail("NON_FLOW_RUNTIME_MIXED");
            return null;
        }
        if (assignment.FlowInstanceId != requested.FlowInstanceId ||
            assignment.FlowExecutionEpoch is null or <= 0 ||
            assignment.FlowTemplateVersionNo is null or <= 0 ||
            !string.Equals(
                assignment.FlowEffectiveStatus,
                "EFFECTIVE",
                StringComparison.Ordinal))
            return null;

        var families = await context.DynamicFlowTemplates
            .Find(item =>
                item.Id == assignment.FlowTemplateId &&
                item.Status == DynamicFlowTemplateStatuses.Active &&
                item.HasLockedVersion &&
                !item.IsDeleted)
            .Limit(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var versions = await context.DynamicFlowTemplateVersions
            .Find(item =>
                item.TemplateId == assignment.FlowTemplateId &&
                item.VersionNo == assignment.FlowTemplateVersionNo &&
                item.Id == requested.FlowTemplateVersionId &&
                item.PayloadHash == requested.FlowPayloadSha256 &&
                item.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                !item.IsDeleted)
            .Limit(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var instances = await context.DynamicFlowInstances
            .Find(item =>
                item.Id == assignment.FlowInstanceId &&
                item.WorkId == requested.WorkId &&
                item.FlowTemplateVersionId ==
                    requested.FlowTemplateVersionId &&
                item.FlowPayloadHash == requested.FlowPayloadSha256 &&
                !item.IsDeleted)
            .Limit(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var epochs = await context.DynamicFlowExecutionEpochs
            .Find(item =>
                item.Id == requested.ExecutionEpochId &&
                item.FlowInstanceId == requested.FlowInstanceId &&
                item.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                item.IsCanonical &&
                !item.IsDeleted)
            .Limit(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var steps = await context.DynamicFlowStepInstances
            .Find(item =>
                item.FlowInstanceId == requested.FlowInstanceId &&
                item.AssignmentId == assignment.Id &&
                item.ExecutionEpoch == assignment.FlowExecutionEpoch &&
                !item.IsDeleted)
            .SortByDescending(item => item.Revision)
            .Limit(StatisticReconciliationExpectedSourcePlanningLimits
                .MaxLifecycleCandidates + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (steps.Count >
            StatisticReconciliationExpectedSourcePlanningLimits
                .MaxLifecycleCandidates)
            throw Fail("FLOW_STEP_HISTORY_LIMIT_EXCEEDED");
        if (families.Count != 1 || versions.Count != 1 ||
            instances.Count != 1 || epochs.Count != 1 || steps.Count == 0)
            return null;
        var eligibleSteps = steps.Where(item =>
                item.IsCanonicalEpoch == true &&
                item.State is (
                    DynamicFlowStepStates.Approved or
                    DynamicFlowStepStates.Completed) &&
                !item.InvalidatedAtUtc.HasValue &&
                string.IsNullOrWhiteSpace(item.InvalidatedByFlowEventId) &&
                string.IsNullOrWhiteSpace(item.SupersededByStepInstanceId) &&
                string.Equals(item.ReportId, report.Id, StringComparison.Ordinal) &&
                item.ReportLifecycleRevision == report.LifecycleRevision &&
                string.Equals(
                    item.ReportLifecycleStatus,
                    "APPROVED",
                    StringComparison.Ordinal) &&
                item.ReportLifecycleIsActive == true &&
                string.Equals(
                    item.FlowStepId,
                    assignment.FlowStepId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.BranchId,
                    assignment.FlowBranchId,
                    StringComparison.Ordinal) &&
                item.AttemptNo == assignment.FlowAttemptNo)
            .ToArray();
        if (eligibleSteps.Length > 1)
            throw Fail("FLOW_STEP_CURRENT_AMBIGUOUS");
        var step = eligibleSteps.SingleOrDefault() ??
            steps.FirstOrDefault(item =>
                string.Equals(item.ReportId, report.Id, StringComparison.Ordinal) &&
                item.ReportLifecycleRevision == report.LifecycleRevision) ??
            steps[0];
        var isCurrent = epochs[0].State is (
                            DynamicFlowExecutionEpochStates.Active or
                            DynamicFlowExecutionEpochStates.Finalized) &&
                        instances[0].ExecutionEpoch ==
                            assignment.FlowExecutionEpoch &&
                        eligibleSteps.Length == 1;
        var mapping = await DynamicFlowMappingLifecycleContract.ValidateAsync(
            context,
            report,
            DynamicFlowMappingIntegrityMode.RequireCurrent,
            cancellationToken,
            lifecycleOperation: "P10_EXPECTED_MEMBERSHIP")
            .ConfigureAwait(false);
        DynamicFlowMappingApplyReceipt? mappingReceipt = null;
        if (mapping is null)
        {
            isCurrent = false;
        }
        else
        {
            var receipts = await context.DynamicFlowMappingApplyReceipts
                .Find(item =>
                    item.Id == mapping.ReceiptId &&
                    item.TargetReportId == report.Id &&
                    item.TargetAssignmentId == assignment.Id &&
                    item.WorkId == requested.WorkId &&
                    item.ProvenanceId == mapping.ProvenanceId &&
                    (item.State == DynamicFlowMappingApplyStates.Committed ||
                     item.State == DynamicFlowMappingApplyStates.Reconciled))
                .Limit(2)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (receipts.Count != 1 ||
                receipts[0].ProvenanceHash != mapping.ProvenanceHash ||
                receipts[0].ResultPayloadRevision !=
                    mapping.ResultPayloadRevision ||
                receipts[0].ResultPayloadHash != mapping.ResultPayloadHash ||
                !IsLowerSha256(receipts[0].ResultSemanticHash) ||
                receipts[0].RuntimePin is null)
                throw Fail("MAPPING_RECEIPT_BINDING_INVALID");
            mappingReceipt = receipts[0];
        }
        return new AuthoritativeRuntime(
            families[0],
            versions[0],
            instances[0],
            epochs[0],
            step,
            mapping,
            mappingReceipt,
            isCurrent);
    }

    private static StatisticReconciliationExpectedAuthoritativeRuntimePin
        BuildRuntimePin(
            ExpectedLedgerCompilationContextPin requested,
            AuthoritativeSource source,
            ExpectedContributionCandidate contribution,
            string lifecycleOwnerSha256,
            string configVersionId,
            string configSha256)
    {
        var approval = ResolveApprovalEntry(source.Report);
        var policy = contribution.Policy ??
            StatisticReconciliationExpectedContributionPolicies.Exclude;
        if (requested.RuntimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.NonFlow)
        {
            return StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Create(
                    runtimeKind: requested.RuntimeKind,
                    flowTemplateVersionId: null,
                    flowPayloadSha256: null,
                    flowInstanceId: null,
                    flowBranchId: null,
                    flowStepId: null,
                    flowStepInstanceId: null,
                    flowStepRevision: null,
                    flowAttemptNo: null,
                    executionEpochId: null,
                    executionEpoch: null,
                    currentExecutionEpochId: null,
                    currentExecutionEpoch: null,
                    executionEpochRevision: null,
                    isCanonicalEpoch: null,
                    approvalCommandId: approval?.CommandId,
                    approvalEventKey: approval?.EntryKey,
                    mappingReceiptId: null,
                    mappingProvenanceId: null,
                    mappingProvenanceSha256: null,
                    mappingResultSemanticSha256: null,
                    mappingResultPayloadRevision: null,
                    mappingResultPayloadSha256: null,
                    mappingFlowVersionId: null,
                    mappingFlowVersionNo: null,
                    mappingFlowPayloadSha256: null,
                    mappingLocked: null,
                    configVersionId: configVersionId,
                    configSha256: configSha256,
                    membershipSignatureSha256:
                        requested.SourceOwnerMembershipSha256!,
                    contributionPolicy: policy,
                    contributionPolicySha256: contribution.PolicySha256,
                    contributionProvenanceId: contribution.ProvenanceId,
                    contributionProvenanceSha256:
                        contribution.ProvenanceSha256,
                    lifecycleOwnerSha256: lifecycleOwnerSha256);
        }
        var runtime = source.Runtime ?? throw Fail("FLOW_RUNTIME_PIN_REQUIRED");
        var receipt = runtime.MappingReceipt;
        return StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
            .Create(
                runtimeKind: requested.RuntimeKind,
                flowTemplateVersionId: runtime.FlowVersion.Id,
                flowPayloadSha256: runtime.FlowVersion.PayloadHash,
                flowInstanceId: runtime.FlowInstance.Id,
                flowBranchId: runtime.Step.BranchId,
                flowStepId: runtime.Step.FlowStepId,
                flowStepInstanceId: runtime.Step.Id,
                flowStepRevision: runtime.Step.Revision,
                flowAttemptNo: runtime.Step.AttemptNo,
                executionEpochId: runtime.Epoch.Id,
                executionEpoch: runtime.Epoch.ExecutionEpoch,
                currentExecutionEpochId: requested.ExecutionEpochId,
                currentExecutionEpoch: runtime.FlowInstance.ExecutionEpoch,
                executionEpochRevision: runtime.Epoch.Revision,
                isCanonicalEpoch: runtime.Epoch.IsCanonical,
                approvalCommandId: approval?.CommandId,
                approvalEventKey: approval?.EntryKey,
                mappingReceiptId: receipt?.Id,
                mappingProvenanceId: receipt?.ProvenanceId,
                mappingProvenanceSha256: receipt?.ProvenanceHash,
                mappingResultSemanticSha256: receipt?.ResultSemanticHash,
                mappingResultPayloadRevision: receipt?.ResultPayloadRevision,
                mappingResultPayloadSha256: receipt?.ResultPayloadHash,
                mappingFlowVersionId: receipt?.RuntimePin.FlowVersionId,
                mappingFlowVersionNo: receipt?.RuntimePin.FlowVersionNo,
                mappingFlowPayloadSha256: receipt?.RuntimePin.FlowPayloadHash,
                mappingLocked: receipt is null ? null : true,
                configVersionId: configVersionId,
                configSha256: configSha256,
                membershipSignatureSha256:
                    requested.SourceOwnerMembershipSha256!,
                contributionPolicy: policy,
                contributionPolicySha256: contribution.PolicySha256,
                contributionProvenanceId: contribution.ProvenanceId,
                contributionProvenanceSha256: contribution.ProvenanceSha256,
                lifecycleOwnerSha256: lifecycleOwnerSha256);
    }

    private static WorkReportLifecycleProjectionOutboxEntry?
        ResolveApprovalEntry(WorkAssignmentReport report)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved)
            return null;
        if (string.IsNullOrWhiteSpace(report.LastLifecycleCommandId) ||
            string.IsNullOrWhiteSpace(report.LastLifecycleCommandOperation) ||
            report.LastLifecycleCommandRevision is not > 0)
            throw Fail("APPROVAL_OWNER_PIN_REQUIRED");
        var candidates = (report.LifecycleProjectionOutbox ?? [])
            .Where(item =>
                StringComparer.Ordinal.Equals(
                    item.CommandId,
                    report.LastLifecycleCommandId) &&
                item.LifecycleRevision ==
                    report.LastLifecycleCommandRevision &&
                StringComparer.Ordinal.Equals(
                    item.Operation,
                    report.LastLifecycleCommandOperation))
            .ToArray();
        if (candidates.Length != 1)
            throw Fail("APPROVAL_OUTBOX_NOT_EXACT");
        var entry = candidates[0];
        var expectedKey =
            tdtd_be.Services.WorkAssignmentReports.Runtime
                .WorkReportLifecycleOutboxContract.ComputeEntryKey(
                    entry.CommandId,
                    entry.LifecycleRevision,
                    entry.Operation);
        if (!StringComparer.Ordinal.Equals(entry.EntryKey, expectedKey) ||
            entry.State is not (
                WorkReportLifecycleProjectionOutboxStates.Pending or
                WorkReportLifecycleProjectionOutboxStates.Completed) ||
            entry.PayloadRevision != report.PayloadRevision ||
            !StringComparer.Ordinal.Equals(entry.PayloadHash, report.PayloadHash) ||
            !StringComparer.Ordinal.Equals(entry.ToStatus, "APPROVED") ||
            !entry.ToIsActive)
            throw Fail("APPROVAL_OUTBOX_BINDING_INVALID");
        return entry;
    }
    private static string BuildLifecycleOwnerSha256(
        WorkAssignmentReport report)
        => IsLowerSha256(report.LastLifecycleCommandHash)
            ? report.LastLifecycleCommandHash!
            : H(
                "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_OWNER_V1",
                [report.Id,
                 report.WorkAssignmentId,
                 report.PayloadRevision.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 report.LifecycleRevision.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 report.PayloadHash ?? "~",
                 report.Status.ToString(),
                 report.IsActive ? "1" : "0",
                 report.IsCurrent ? "1" : "0",
                 report.LastLifecycleCommandOperation ?? "~",
                 report.InvalidatedByFlowEventId ?? "~"]);
    private static ExpectedContributionCandidate BuildContribution(
        string stable,
        AuthoritativeSource source)
    {
        if (source.Runtime is not null)
        {
            var runtime = source.Runtime;
            DynamicFlowContributionPolicyContract.ValidateLockedPolicy(
                runtime.FlowVersion);
            var lockedPolicy = runtime.FlowVersion.ContributionPolicy ??
                               DynamicFlowContributionPolicyContract.Exclude;
            var flowPolicySha = runtime.FlowVersion.ContributionPolicyHash ??
                            DynamicFlowContributionPolicyContract
                                .ComputePolicyHash(
                                    runtime.FlowVersion,
                                    lockedPolicy);
            var missingMappingSha = H(
                "P10_EXPECTED_NO_MAPPING_PROVENANCE_V1",
                [stable]);
            return new ExpectedContributionCandidate(
                stable,
                string.Equals(
                    lockedPolicy,
                    DynamicFlowContributionPolicyContract.Include,
                    StringComparison.Ordinal) &&
                runtime.Mapping is not null
                    ? StatisticReconciliationExpectedContributionPolicies.Include
                    : StatisticReconciliationExpectedContributionPolicies.Exclude,
                runtime.FlowVersion.Id,
                runtime.Epoch.Revision,
                flowPolicySha,
                runtime.Mapping?.ProvenanceId ?? "NONE",
                runtime.Mapping?.ProvenanceHash ?? missingMappingSha,
                isLocked: true);
        }
        var explicitPolicy = ResolveLockedNonFlowContribution(source.Report);
        var policy = explicitPolicy?.Policy ??
            StatisticReconciliationExpectedContributionPolicies.Exclude;
        var policySha = explicitPolicy?.PolicySha256 ?? H(
            "P10_EXPECTED_NON_FLOW_DEFAULT_EXCLUDE_V1",
            [stable, source.Report.Id,
             source.Report.LifecycleRevision.ToString(
                 System.Globalization.CultureInfo.InvariantCulture)]);
        return new ExpectedContributionCandidate(
            stable,
            policy,
            explicitPolicy?.OwnerId ?? source.Report.Id,
            explicitPolicy?.Revision ?? source.Report.LifecycleRevision,
            policySha,
            explicitPolicy?.OwnerId ?? source.Report.Id,
            explicitPolicy?.PolicySha256 ??
                BuildLifecycleOwnerSha256(source.Report),
            isLocked: true);
    }

    internal static LockedNonFlowContribution?
        ResolveLockedNonFlowContribution(WorkAssignmentReport report)
    {
        if (string.IsNullOrWhiteSpace(report.CumulativeContributionPolicyJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(
                report.CumulativeContributionPolicyJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                version.GetString() != "P10_NON_FLOW_CONTRIBUTION_POLICY_V1" ||
                !root.TryGetProperty("locked", out var locked) ||
                locked.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("policy", out var policyNode) ||
                policyNode.ValueKind != JsonValueKind.String ||
                policyNode.GetString() is not (
                    StatisticReconciliationExpectedContributionPolicies.Include or
                    StatisticReconciliationExpectedContributionPolicies.Exclude) ||
                !root.TryGetProperty("ownerId", out var ownerNode) ||
                ownerNode.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(ownerNode.GetString()) ||
                !root.TryGetProperty("revision", out var revisionNode) ||
                !revisionNode.TryGetInt64(out var revision) || revision <= 0 ||
                !root.TryGetProperty("policySha256", out var shaNode) ||
                shaNode.ValueKind != JsonValueKind.String ||
                !IsLowerSha256(shaNode.GetString()))
                throw Fail("NON_FLOW_CONTRIBUTION_POLICY_INVALID");
            var ownerId = ownerNode.GetString()!.Trim();
            var policy = policyNode.GetString()!;
            var expected = H(
                "P10_EXPECTED_NON_FLOW_LOCKED_POLICY_V1",
                [report.Id,
                 ownerId,
                 revision.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 policy]);
            if (!StringComparer.Ordinal.Equals(expected, shaNode.GetString()))
                throw Fail("NON_FLOW_CONTRIBUTION_POLICY_HASH_MISMATCH");
            return new(policy, ownerId, revision, expected);
        }
        catch (JsonException error)
        {
            throw Fail($"NON_FLOW_CONTRIBUTION_POLICY_JSON_INVALID:{error.GetType().Name}");
        }
    }
    internal static void RequireFlowRuntimePin(
        string requestedRuntimeKind,
        bool hasFlowRuntime)
    {
        if (string.Equals(
                requestedRuntimeKind,
                StatisticReconciliationExpectedRuntimeKinds.Flow,
                StringComparison.Ordinal) &&
            !hasFlowRuntime)
            throw Fail("FLOW_RUNTIME_PIN_REQUIRED");
    }
    internal static bool ResolveSourceInclude(
        WorkAssignmentReport report,
        string requestedRuntimeKind,
        bool hasFlowRuntime,
        string? lockedFlowPolicy,
        bool hasFlowMapping)
    {
        if (string.Equals(
                requestedRuntimeKind,
                StatisticReconciliationExpectedRuntimeKinds.Flow,
                StringComparison.Ordinal))
        {
            return hasFlowRuntime &&
                   string.Equals(
                       lockedFlowPolicy ??
                           DynamicFlowContributionPolicyContract.Exclude,
                       DynamicFlowContributionPolicyContract.Include,
                       StringComparison.Ordinal) &&
                   hasFlowMapping;
        }

        return ResolveLockedNonFlowContribution(report)
            is { Policy: StatisticReconciliationExpectedContributionPolicies.Include };
    }
    private static bool AssignmentInMetricScope(
        ExpectedLedgerCompilationContextPin requested,
        AuthoritativeMetricScope metricScope,
        WorkAssignment assignment)
    {
        if (metricScope.Family !=
            StatisticReconciliationExpectedMetricFamilies.Basic)
            return true;
        return metricScope.ScopeKind switch
        {
            StatisticReconciliationExpectedBasicScopes.DirectChildrenOrSelf =>
                assignment.Id == requested.ScopeAssignmentId ||
                assignment.ParentAssignmentId == requested.ScopeAssignmentId,
            StatisticReconciliationExpectedBasicScopes.DirectChildren =>
                assignment.ParentAssignmentId == requested.ScopeAssignmentId,
            StatisticReconciliationExpectedBasicScopes.FlowBranch =>
                assignment.FlowInstanceId == requested.FlowInstanceId &&
                assignment.FlowBranchId == metricScope.ScopeId,
            StatisticReconciliationExpectedBasicScopes.FlowStep =>
                assignment.FlowInstanceId == requested.FlowInstanceId &&
                assignment.FlowStepId == metricScope.ScopeId,
            StatisticReconciliationExpectedBasicScopes.FlowEffectivePath =>
                assignment.FlowInstanceId == requested.FlowInstanceId &&
                string.Equals(
                    assignment.FlowEffectiveStatus,
                    "EFFECTIVE",
                    StringComparison.Ordinal),
            StatisticReconciliationExpectedBasicScopes.FlowFinal =>
                assignment.FlowInstanceId == requested.FlowInstanceId &&
                assignment.IsFlowFinalNode == true &&
                string.Equals(
                    assignment.FlowEffectiveStatus,
                    "EFFECTIVE",
                    StringComparison.Ordinal),
            _ => throw Fail("BASIC_SCOPE_UNSUPPORTED")
        };
    }
    private static void RequireAuthoritativePins(
        ExpectedLedgerCompilationContextPin requested,
        WorkAssignmentReport report,
        WorkAssignment assignment,
        WorkReportPeriod period)
    {
        if (report.WorkId != requested.WorkId ||
            report.PeriodKey != requested.PeriodKey ||
            report.PeriodInstanceKey != requested.PeriodInstanceKey ||
            report.DynamicFormTemplateId !=
                requested.DynamicFormVersionId ||
            report.DynamicFormSchemaHash !=
                requested.DynamicFormSchemaSha256 ||
            assignment.WorkId != report.WorkId ||
            assignment.DynamicFormTemplateId !=
                requested.DynamicFormVersionId ||
            assignment.DynamicFormSchemaHash !=
                requested.DynamicFormSchemaSha256 ||
            period.WorkId != report.WorkId ||
            period.WorkAssignmentId != report.WorkAssignmentId ||
            period.PeriodKey != report.PeriodKey ||
            period.PeriodInstanceKey != report.PeriodInstanceKey)
            throw Fail("SOURCE_AUTHORITATIVE_PIN_MISMATCH");
    }

    private static bool HasCanonicalLifecyclePin(
        WorkAssignmentReport report)
        => !string.IsNullOrWhiteSpace(report.LastLifecycleCommandId) &&
           report.LastLifecycleCommandRevision == report.LifecycleRevision &&
           report.LastLifecycleCommandPayloadRevision ==
               report.PayloadRevision &&
           report.LastLifecycleCommandStatus == report.Status &&
           report.LastLifecycleCommandIsActive == report.IsActive &&
           IsLowerSha256(report.LastLifecycleCommandHash);

    private static string ResolveDisposition(
        WorkAssignmentReport report,
        WorkAssignment assignment,
        bool currentEpoch)
    {
        if (report.LastLifecycleCommandOperation?.Trim().ToUpperInvariant()
            is "TERMINATE" or "TERMINATED")
            return StatisticReconciliationExpectedRuntimeDispositions.Terminated;
        if (!string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId) ||
            !string.IsNullOrWhiteSpace(
                assignment.InvalidatedByFlowEventId))
            return StatisticReconciliationExpectedRuntimeDispositions.Invalidated;
        if (!report.IsCurrent || !currentEpoch)
            return StatisticReconciliationExpectedRuntimeDispositions.Superseded;
        return StatisticReconciliationExpectedRuntimeDispositions.Current;
    }
    private static P5P7RuntimeMappingContributionLineage BuildLineage(
        ExpectedLedgerCompilationContextPin contextPin,
        IReadOnlyList<AuthoritativeSource> members)
    {
        var pins = new List<ExpectedLedgerLineagePin>
        {
            new(
                StatisticReconciliationExpectedLedgerLineageLayers.P5Runtime,
                contextPin.SourceOwnerRunId!,
                contextPin.SourceOwnerGenerationId!,
                contextPin.SourceOwnerRevision!.Value,
                contextPin.SourceOwnerGenerationSha256!)
        };

        if (contextPin.RuntimeKind ==
            StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            var flow = members.Select(item => item.Runtime).ToArray();
            if (flow.Any(item => item is null))
                throw Fail("FLOW_MEMBER_RUNTIME_REQUIRED");
            var mappingHashes = members
                .Select(item => item.Runtime?.Mapping?.ProvenanceHash ??
                    H("P10_EXPECTED_NO_MAPPING_PROVENANCE_V1",
                      [StatisticReconciliationExpectedMembershipIntegrity
                          .BuildStableIdentitySha256(
                              item.Report.WorkId,
                              item.Report.WorkAssignmentId,
                              item.Report.Id)]))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var contributionHashes = flow
                .Select(item => RequireSha(
                    item!.FlowVersion.ContributionPolicyHash,
                    "FLOW_CONTRIBUTION_SHA_INVALID"))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            pins.Add(new(
                StatisticReconciliationExpectedLedgerLineageLayers.P6FlowTopology,
                contextPin.FlowInstanceId!,
                contextPin.ExecutionEpochId!,
                flow.Length == 0
                    ? contextPin.SourceOwnerRevision!.Value
                    : flow.Max(item => item!.Epoch.Revision),
                contextPin.FlowPayloadSha256!));
            pins.Add(new(
                StatisticReconciliationExpectedLedgerLineageLayers.P7Mapping,
                contextPin.FlowInstanceId!,
                contextPin.SourceOwnerGenerationId!,
                members.Count == 0
                    ? contextPin.SourceOwnerRevision!.Value
                    : members.Max(item =>
                        (long)(item.Runtime?.Mapping?.ResultPayloadRevision ?? 0)),
                H("P10_EXPECTED_MAPPING_LINEAGE_V1", mappingHashes)));
            pins.Add(new(
                StatisticReconciliationExpectedLedgerLineageLayers.P7Contribution,
                contextPin.FlowInstanceId!,
                contextPin.SourceOwnerGenerationId!,
                flow.Length == 0
                    ? contextPin.SourceOwnerRevision!.Value
                    : flow.Max(item => item!.Epoch.Revision),
                H("P10_EXPECTED_CONTRIBUTION_LINEAGE_V1", contributionHashes)));
        }
        else
        {
            pins.Add(new(
                StatisticReconciliationExpectedLedgerLineageLayers.P7Contribution,
                contextPin.ScopeAssignmentId,
                contextPin.SourceOwnerGenerationId!,
                contextPin.SourceOwnerRevision!.Value,
                contextPin.SourceOwnerMembershipSha256!));
        }
        return new P5P7RuntimeMappingContributionLineage(contextPin, pins);
    }

    private static void RequireProductionSourceOwnerPins(
        ExpectedLedgerCompilationContextPin context)
    {
        if (context.SourceOwnerRunId is null ||
            context.SourceOwnerGenerationId is null ||
            context.SourceOwnerGenerationSha256 is null ||
            context.SourceOwnerMembershipSha256 is null ||
            context.SourceOwnerRevision is null or <= 0)
            throw Fail("SOURCE_OWNER_PINS_REQUIRED");
    }

    private static void RequireRunContext(
        StatisticReconciliationRun run,
        ExpectedLedgerCompilationContextPin context)
    {
        var runtimeKind = run.FlowInstanceId is null
            ? StatisticReconciliationExpectedRuntimeKinds.NonFlow
            : StatisticReconciliationExpectedRuntimeKinds.Flow;
        if (run.Id != context.ReconciliationId ||
            run.ImmutableIdentityHash != context.ImmutableIdentitySha256 ||
            run.ImmutableHeaderHash != context.ImmutableHeaderSha256 ||
            run.WorkId != context.WorkId ||
            run.ScopeAssignmentId != context.ScopeAssignmentId ||
            run.PeriodKey != context.PeriodKey ||
            run.PeriodInstanceKey != context.PeriodInstanceKey ||
            run.DynamicFormVersionId != context.DynamicFormVersionId ||
            run.P8ConfigOwnerId != context.P8ConfigurationOwnerId ||
            run.P8ConfigBundleHash != context.P8ConfigurationBundleSha256 ||
            runtimeKind != context.RuntimeKind)
            throw Fail("RUN_CONTEXT_MISMATCH");
    }

    private static string NormalizeLifecycleStatus(WorkAssignmentReport report)
    {
        var operation = report.LastLifecycleCommandOperation?.Trim().ToUpperInvariant();
        if (operation is "TERMINATE" or "TERMINATED")
            return StatisticReconciliationExpectedLifecycleStatuses.Terminated;
        if (operation is "RECALL" or "RECALLED")
            return StatisticReconciliationExpectedLifecycleStatuses.Recalled;
        if (operation is "RETURN" or "RETURNED")
            return StatisticReconciliationExpectedLifecycleStatuses.Returned;
        if (!string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId))
            return StatisticReconciliationExpectedLifecycleStatuses.Invalidated;
        if (!report.IsCurrent)
            return StatisticReconciliationExpectedLifecycleStatuses.Superseded;
        return NormalizeStatus(report.Status.ToString());
    }

    private static string NormalizeStatus(string status)
        => status.Trim().ToUpperInvariant() switch
        {
            "APPROVED" => StatisticReconciliationExpectedLifecycleStatuses.Approved,
            "DRAFT" => StatisticReconciliationExpectedLifecycleStatuses.Draft,
            "SUBMITTED" => StatisticReconciliationExpectedLifecycleStatuses.Submitted,
            "RECALLED" => StatisticReconciliationExpectedLifecycleStatuses.Recalled,
            "RETURNED" => StatisticReconciliationExpectedLifecycleStatuses.Returned,
            "TERMINATED" => StatisticReconciliationExpectedLifecycleStatuses.Terminated,
            "INVALIDATED" => StatisticReconciliationExpectedLifecycleStatuses.Invalidated,
            "SUPERSEDED" => StatisticReconciliationExpectedLifecycleStatuses.Superseded,
            _ => throw Fail("LIFECYCLE_STATUS_INVALID")
        };

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or
               >= 'a' and <= 'f');

    private static string RequireSha(string? value, string reason)
    {
        if (!IsLowerSha256(value))
            throw Fail(reason);
        return value!;
    }

    internal sealed record LockedNonFlowContribution(
        string Policy,
        string OwnerId,
        long Revision,
        string PolicySha256);

    private sealed record AuthoritativeStatisticConfig(
        string VersionId,
        string Sha256);

    private sealed record AuthoritativeMetricScope(
        string Family,
        string? ScopeKind,
        string? ScopeId);

    private sealed record AuthoritativeRuntime(
        DynamicFlowTemplate FlowFamily,
        DynamicFlowTemplateVersion FlowVersion,
        DynamicFlowInstance FlowInstance,
        DynamicFlowExecutionEpoch Epoch,
        DynamicFlowStepInstance Step,
        DynamicFlowMappingLifecycleBinding? Mapping,
        DynamicFlowMappingApplyReceipt? MappingReceipt,
        bool IsCurrentEpoch);

    private sealed record AuthoritativeSource(
        WorkAssignmentReport Report,
        WorkAssignment Assignment,
        WorkReportPeriod Period,
        WorkReportPayload Payload,
        AuthoritativeRuntime? Runtime,
        bool InCurrentEpoch,
        bool IsEffective,
        bool IsLocked,
        string RuntimeDisposition,
        bool Include);
    private static string H(string domain, IEnumerable<string> values)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            domain,
            values);

    private static StatisticReconciliationExpectedLedgerInputException Fail(
        string reason)
        => new(
            StatisticReconciliationExpectedSourcePlanningFailureReasons.SnapshotInvalid,
            "$.authoritativeSnapshot",
            reason);
}