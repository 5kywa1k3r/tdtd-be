using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualLifecycleMetricScopeOwner(
    MongoDbContext context)
{
    internal async Task<StatRunDirectLifecycleMetricScope> ResolveAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan,
        ActualBasicOwnerBoundary? basic,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(plan);
        if (StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan))
            return ResolveV4Direct(run, plan, basic);
        ArgumentNullException.ThrowIfNull(basic);
        var owner = await ReadExactConfigurationAsync(run, cancellationToken)
            .ConfigureAwait(false);
        ExpectedLedgerCanonicalJson canonical;
        try
        {
            canonical = StatisticReconciliationExpectedLedgerCanonicalizer
                .NormalizeObject(
                    owner.ConfigJson,
                    4 * 1024 * 1024,
                    "$.actual.lifecycleMetricScope.configuration",
                    "P10_LIFECYCLE_CONFIGURATION_JSON_INVALID");
        }
        catch (InvalidOperationException error)
        {
            throw Fail("P10_LIFECYCLE_CONFIGURATION_CANONICAL_INVALID:" +
                error.GetType().Name);
        }
        var metric = ReadMetricScope(canonical.Value);
        RequireScopeBinding(run, plan, basic, metric);

        var isBasic = string.Equals(
            metric.Family,
            StatRunDirectLifecycleMetricFamilies.Basic,
            StringComparison.Ordinal);
        var immutableSelectorSha256 = metric.Family switch
        {
            StatRunDirectLifecycleMetricFamilies.Basic =>
                plan.Basic.ImmutableSelectorSha256,
            StatRunDirectLifecycleMetricFamilies.Advanced =>
                plan.Advanced.ImmutableSelectorSha256,
            StatRunDirectLifecycleMetricFamilies.Diff =>
                plan.Diff.ImmutableSelectorSha256,
            StatRunDirectLifecycleMetricFamilies.Direct =>
                run.P9GenerationHash,
            _ => throw Fail("P10_LIFECYCLE_METRIC_FAMILY_UNSUPPORTED")
        };
        var isFlow = run.FlowInstanceId is not null;
        return StatRunDirectLifecycleMetricScopeCanonical.Create(
            metric.Family,
            isBasic
                ? metric.ScopeKind!
                : StatRunDirectLifecycleMetricFamilies.AllCandidates,
            metric.ScopeId,
            Required(run.ScopeAssignmentId, "SCOPE_ASSIGNMENT_ID"),
            isFlow ? Required(run.FlowInstanceId, "FLOW_INSTANCE_ID") : null,
            isFlow ? Required(run.FlowBranchId, "FLOW_BRANCH_ID") : null,
            isFlow ? Required(run.FlowStepId, "FLOW_STEP_ID") : null,
            isFlow
                ? Required(run.FlowEffectiveStatus, "FLOW_EFFECTIVE_STATUS")
                : null,
            isFlow
                ? Required(run.FlowExecutionEpochId, "EXECUTION_EPOCH_ID")
                : null,
            isFlow
                ? Positive(run.FlowExecutionEpoch, "EXECUTION_EPOCH")
                : null,
            isFlow
                ? Positive(run.FlowExecutionEpochRevision,
                    "EXECUTION_EPOCH_REVISION")
                : null,
            owner.Kind,
            owner.Id,
            owner.VersionId,
            owner.VersionNo,
            owner.Revision,
            owner.ConfigHash,
            canonical.Sha256,
            Sha(run.ActualCapturePlanSha256, "ACTUAL_CAPTURE_PLAN_SHA256"),
            Sha(immutableSelectorSha256,
                "METRIC_IMMUTABLE_SELECTOR_SHA256"));
    }

    private static StatRunDirectLifecycleMetricScope ResolveV4Direct(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan,
        ActualBasicOwnerBoundary? basic)
    {
        if (basic is not null ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Basic.Disposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Advanced.Disposition) ||
            !StatisticReconciliationActualCapturePlanIntegrity.IsNotApplicable(
                plan.Diff.Disposition) ||
            !string.Equals(plan.P8ConfigurationOwnerId, run.P8ConfigOwnerId,
                StringComparison.Ordinal) ||
            !string.Equals(plan.P8ConfigurationBundleSha256,
                run.P8ConfigBundleHash, StringComparison.Ordinal))
        {
            throw Fail("P10_LIFECYCLE_V4_APPLICABILITY_BINDING_INVALID");
        }

        var isFlow = run.FlowInstanceId is not null;
        return StatRunDirectLifecycleMetricScopeCanonical.Create(
            StatRunDirectLifecycleMetricFamilies.Direct,
            StatRunDirectLifecycleMetricFamilies.AllCandidates,
            null,
            Required(run.ScopeAssignmentId, "SCOPE_ASSIGNMENT_ID"),
            isFlow ? Required(run.FlowInstanceId, "FLOW_INSTANCE_ID") : null,
            isFlow ? Required(run.FlowBranchId, "FLOW_BRANCH_ID") : null,
            isFlow ? Required(run.FlowStepId, "FLOW_STEP_ID") : null,
            isFlow
                ? Required(run.FlowEffectiveStatus, "FLOW_EFFECTIVE_STATUS")
                : null,
            isFlow
                ? Required(run.FlowExecutionEpochId, "EXECUTION_EPOCH_ID")
                : null,
            isFlow
                ? Positive(run.FlowExecutionEpoch, "EXECUTION_EPOCH")
                : null,
            isFlow
                ? Positive(run.FlowExecutionEpochRevision,
                    "EXECUTION_EPOCH_REVISION")
                : null,
            StatisticReconciliationExpectedLedgerConfigurationKinds.DynamicForm,
            Required(plan.P8ConfigurationOwnerId, "P8_CONFIGURATION_OWNER_ID"),
            Required(run.P8ConfigVersionId, "P8_CONFIG_VERSION_ID"),
            Positive(run.P8ConfigVersionNo, "P8_CONFIG_VERSION_NO"),
            Positive(run.P8ConfigRevision, "P8_CONFIG_REVISION"),
            Sha(run.P8ConfigHash, "P8_CONFIG_HASH"),
            Sha(run.P8ConfigHash, "P8_CONFIG_CANONICAL_SHA256"),
            Sha(run.ActualCapturePlanSha256, "ACTUAL_CAPTURE_PLAN_SHA256"),
            Sha(run.P9GenerationHash, "P9_GENERATION_HASH"));
    }
    private async Task<LockedConfiguration> ReadExactConfigurationAsync(
        StatisticReconciliationRun run,
        CancellationToken cancellationToken)
    {
        var id = Required(run.P8ConfigId, "P8_CONFIG_ID");
        var versionId = Required(run.P8ConfigVersionId,
            "P8_CONFIG_VERSION_ID");
        var versionNo = Positive(run.P8ConfigVersionNo,
            "P8_CONFIG_VERSION_NO");
        var revision = Positive(run.P8ConfigRevision, "P8_CONFIG_REVISION");
        var hash = Sha(run.P8ConfigHash, "P8_CONFIG_HASH");
        var matches = new List<LockedConfiguration>();

        var basic = await context.WorkAssignmentBasicSummaryConfigs
            .Find(item =>
                item.Id == id && item.WorkId == run.WorkId &&
                item.AssignmentId == run.ScopeAssignmentId && item.IsActive)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        matches.AddRange(basic.Where(item =>
                item.VersionId == versionId && item.VersionNo == versionNo &&
                item.Revision == revision && item.ConfigHash == hash &&
                string.Equals(item.Status, "LOCKED", StringComparison.Ordinal))
            .Select(item => new LockedConfiguration(
                StatisticReconciliationExpectedLedgerConfigurationKinds.Basic,
                item.Id, item.VersionId!, item.VersionNo, item.Revision,
                item.ConfigHash!, item.ConfigJson)));

        var advanced = await context.WorkAssignmentAdvancedSummaryConfigs
            .Find(item =>
                item.Id == id && item.WorkId == run.WorkId &&
                item.AssignmentId == run.ScopeAssignmentId)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        matches.AddRange(advanced.Where(item =>
                item.ConfigId == versionId && item.VersionNo == versionNo &&
                item.Revision == revision && item.ConfigHash == hash &&
                item.Status == WorkAssignmentAdvancedSummaryConfigStatuses.Locked)
            .Select(item => new LockedConfiguration(
                StatisticReconciliationExpectedLedgerConfigurationKinds.Advanced,
                item.Id, item.ConfigId, item.VersionNo, item.Revision,
                item.ConfigHash, item.ConfigJson)));

        var diff = await context.WorkReportStatisticDiffConfigs
            .Find(item =>
                item.Id == id && item.WorkId == run.WorkId &&
                item.AssignmentId == run.ScopeAssignmentId && item.IsActive)
            .Limit(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        matches.AddRange(diff.Where(item =>
                item.ConfigId == versionId && item.VersionNo == versionNo &&
                item.Revision == revision && item.ConfigHash == hash &&
                string.Equals(item.Status, "LOCKED", StringComparison.Ordinal))
            .Select(item => new LockedConfiguration(
                StatisticReconciliationExpectedLedgerConfigurationKinds.Diff,
                item.Id, item.ConfigId!, item.VersionNo, item.Revision,
                item.ConfigHash!, item.ConfigJson)));

        if (matches.Count != 1)
            throw Fail("P10_LIFECYCLE_P8_CONFIG_OWNER_NOT_EXACT");
        return matches[0];
    }

    private static MetricScope ReadMetricScope(string canonicalJson)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        if (!document.RootElement.TryGetProperty(
                "expectedMetrics", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array ||
            metrics.GetArrayLength() == 0)
            throw Fail("P10_LIFECYCLE_METRIC_PLAN_REQUIRED");

        string? family = null;
        string? scopeKind = null;
        string? scopeId = null;
        foreach (var metric in metrics.EnumerateArray())
        {
            if (metric.ValueKind != JsonValueKind.Object)
                throw Fail("P10_LIFECYCLE_METRIC_ENTRY_INVALID");
            var currentFamily = JsonText(metric, "family").ToUpperInvariant();
            var currentScope = JsonOptionalText(metric, "scopeKind")?
                .ToUpperInvariant();
            var currentScopeId = JsonOptionalText(metric, "scopeId");
            family ??= currentFamily;
            scopeKind ??= currentScope;
            scopeId ??= currentScopeId;
            if (!string.Equals(family, currentFamily,
                    StringComparison.Ordinal) ||
                !string.Equals(scopeKind, currentScope,
                    StringComparison.Ordinal) ||
                !string.Equals(scopeId, currentScopeId,
                    StringComparison.Ordinal))
                throw Fail("P10_LIFECYCLE_METRIC_SCOPE_MIXED");
        }
        if (!StatRunDirectLifecycleMetricFamilies.IsSupported(family!))
            throw Fail("P10_LIFECYCLE_METRIC_FAMILY_UNSUPPORTED");
        var basic = family == StatRunDirectLifecycleMetricFamilies.Basic;
        if (basic && (scopeKind is null || scopeId is null))
            throw Fail("P10_LIFECYCLE_BASIC_SCOPE_REQUIRED");
        if (!basic && (scopeKind is not null || scopeId is not null))
            throw Fail("P10_LIFECYCLE_NON_BASIC_SCOPE_FORBIDDEN");
        return new MetricScope(family!, scopeKind, scopeId);
    }

    private static void RequireScopeBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationActualCapturePlan plan,
        ActualBasicOwnerBoundary? basic,
        MetricScope metric)
    {
        if (!string.Equals(plan.PlanSha256, run.ActualCapturePlanSha256,
                StringComparison.Ordinal) ||
            !string.Equals(plan.Basic.Mode, basic.SourceScopeMode,
                StringComparison.Ordinal) ||
            !string.Equals(run.ScopeAssignmentId, basic.ScopeAssignmentId,
                StringComparison.Ordinal))
            throw Fail("P10_LIFECYCLE_CAPTURE_PLAN_SCOPE_DRIFT");

        var isFlow = run.FlowInstanceId is not null;
        if (isFlow)
        {
            if (run.FlowExecutionEpochId is null ||
                run.FlowExecutionEpoch is not > 0 ||
                run.FlowExecutionEpochRevision is not > 0 ||
                run.FlowBranchId is null || run.FlowStepId is null ||
                run.FlowEffectiveStatus is null)
                throw Fail("P10_LIFECYCLE_FLOW_RUNTIME_TUPLE_REQUIRED");
        }
        else if (run.FlowExecutionEpochId is not null ||
                 run.FlowExecutionEpoch is not null ||
                 run.FlowExecutionEpochRevision is not null ||
                 run.FlowBranchId is not null || run.FlowStepId is not null ||
                 run.FlowEffectiveStatus is not null)
            throw Fail("P10_LIFECYCLE_NON_FLOW_RUNTIME_TUPLE_FORBIDDEN");

        if (metric.Family != StatRunDirectLifecycleMetricFamilies.Basic)
            return;
        if (!string.Equals(metric.ScopeKind, plan.Basic.Mode,
                StringComparison.Ordinal))
            throw Fail("P10_LIFECYCLE_BASIC_MODE_DRIFT");
        switch (metric.ScopeKind)
        {
            case "DIRECT_CHILDREN_OR_SELF":
            case "DIRECT_CHILDREN":
                if (!string.Equals(metric.ScopeId, run.ScopeAssignmentId,
                        StringComparison.Ordinal) ||
                    run.FlowInstanceId is not null)
                    throw Fail("P10_LIFECYCLE_BASIC_ASSIGNMENT_SCOPE_DRIFT");
                break;
            case "FLOW_BRANCH":
                RequireFlowScope(run, basic);
                if (!string.Equals(metric.ScopeId, run.FlowBranchId,
                        StringComparison.Ordinal) ||
                    !string.Equals(metric.ScopeId,
                        basic.SourceFlowBranchId, StringComparison.Ordinal))
                    throw Fail("P10_LIFECYCLE_BASIC_BRANCH_SCOPE_DRIFT");
                break;
            case "FLOW_STEP":
                RequireFlowScope(run, basic);
                if (!string.Equals(metric.ScopeId, run.FlowStepId,
                        StringComparison.Ordinal) ||
                    !string.Equals(metric.ScopeId,
                        basic.SourceFlowStepId, StringComparison.Ordinal))
                    throw Fail("P10_LIFECYCLE_BASIC_STEP_SCOPE_DRIFT");
                break;
            case "FLOW_EFFECTIVE_PATH":
            case "FLOW_FINAL":
                RequireFlowScope(run, basic);
                if (!string.Equals(basic.SourceFlowEffectiveStatus,
                        run.FlowEffectiveStatus, StringComparison.Ordinal))
                    throw Fail("P10_LIFECYCLE_BASIC_EFFECTIVE_SCOPE_DRIFT");
                break;
            default:
                throw Fail("P10_LIFECYCLE_BASIC_SCOPE_UNSUPPORTED");
        }
    }

    private static void RequireFlowScope(
        StatisticReconciliationRun run,
        ActualBasicOwnerBoundary basic)
    {
        if (run.FlowInstanceId is null || run.FlowExecutionEpochId is null ||
            run.FlowExecutionEpoch is not > 0 ||
            run.FlowExecutionEpochRevision is not > 0 ||
            !string.Equals(run.FlowInstanceId,
                basic.SourceFlowInstanceId, StringComparison.Ordinal))
            throw Fail("P10_LIFECYCLE_FLOW_SCOPE_DRIFT");
    }

    private static string JsonText(JsonElement owner, string property)
        => JsonOptionalText(owner, property) ??
           throw Fail($"P10_LIFECYCLE_METRIC_{property.ToUpperInvariant()}_REQUIRED");

    private static string? JsonOptionalText(JsonElement owner, string property)
        => owner.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string Required(string? value, string reason)
        => string.IsNullOrWhiteSpace(value)
            ? throw Fail($"P10_LIFECYCLE_{reason}_REQUIRED")
            : value.Trim();

    private static string Sha(string? value, string reason)
        => StatisticReconciliationActualCanonical.Sha256(value,
            "P10_LIFECYCLE_" + reason);

    private static int Positive(int? value, string reason)
        => value is > 0
            ? value.Value
            : throw Fail($"P10_LIFECYCLE_{reason}_INVALID");

    private static long Positive(long? value, string reason)
        => value is > 0
            ? value.Value
            : throw Fail($"P10_LIFECYCLE_{reason}_INVALID");

    private static StatisticReconciliationActualObservationException Fail(
        string reason) => new(reason);

    private sealed record LockedConfiguration(
        string Kind,
        string Id,
        string VersionId,
        int VersionNo,
        long Revision,
        string ConfigHash,
        string ConfigJson);

    private sealed record MetricScope(
        string Family,
        string? ScopeKind,
        string? ScopeId);
}