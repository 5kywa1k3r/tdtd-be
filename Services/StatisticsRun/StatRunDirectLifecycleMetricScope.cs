namespace tdtd_be.Services.StatisticsRun;

internal static class StatRunDirectLifecycleMetricFamilies
{
    internal const string Direct = "DIRECT";
    internal const string Basic = "BASIC";
    internal const string Advanced = "ADVANCED";
    internal const string Diff = "DIFF";
    internal const string AllCandidates = "ALL_CANDIDATES";

    internal static bool IsSupported(string value)
        => value is Direct or Basic or Advanced or Diff;
}

internal sealed record StatRunDirectLifecycleMetricScope(
    string MetricFamily,
    string ScopeMode,
    string? ScopeId,
    string ScopeAssignmentId,
    string? FlowInstanceId,
    string? RuntimeFlowBranchId,
    string? RuntimeFlowStepId,
    string? FlowEffectiveStatus,
    string? ExecutionEpochId,
    int? ExecutionEpoch,
    long? ExecutionEpochRevision,
    string ConfigurationKind,
    string ConfigurationOwnerId,
    string ConfigurationVersionId,
    int ConfigurationVersionNo,
    long ConfigurationRevision,
    string ConfigurationOwnerSha256,
    string ConfigurationCanonicalJsonSha256,
    string CapturePlanSha256,
    string ImmutableSelectorSha256,
    string SemanticSha256);

internal static class StatRunDirectLifecycleMetricScopeCanonical
{
    internal const string Domain = "P10_P9_LIFECYCLE_METRIC_SCOPE_V1";

    internal static StatRunDirectLifecycleMetricScope Create(
        string metricFamily,
        string scopeMode,
        string? scopeId,
        string scopeAssignmentId,
        string? flowInstanceId,
        string? runtimeFlowBranchId,
        string? runtimeFlowStepId,
        string? flowEffectiveStatus,
        string? executionEpochId,
        int? executionEpoch,
        long? executionEpochRevision,
        string configurationKind,
        string configurationOwnerId,
        string configurationVersionId,
        int configurationVersionNo,
        long configurationRevision,
        string configurationOwnerSha256,
        string configurationCanonicalJsonSha256,
        string capturePlanSha256,
        string immutableSelectorSha256)
    {
        var draft = NormalizeFields(new(
            metricFamily,
            scopeMode,
            scopeId,
            scopeAssignmentId,
            flowInstanceId,
            runtimeFlowBranchId,
            runtimeFlowStepId,
            flowEffectiveStatus,
            executionEpochId,
            executionEpoch,
            executionEpochRevision,
            configurationKind,
            configurationOwnerId,
            configurationVersionId,
            configurationVersionNo,
            configurationRevision,
            configurationOwnerSha256,
            configurationCanonicalJsonSha256,
            capturePlanSha256,
            immutableSelectorSha256,
            "~"));
        return draft with { SemanticSha256 = ComputeSemanticSha256(draft) };
    }

    internal static StatRunDirectLifecycleMetricScope RequireValid(
        StatRunDirectLifecycleMetricScope value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = NormalizeFields(value);
        var exact = normalized with { SemanticSha256 = value.SemanticSha256 };
        if (!StatRunCanonicalJson.IsCanonicalSha256(value.SemanticSha256) ||
            !string.Equals(value.SemanticSha256,
                ComputeSemanticSha256(normalized), StringComparison.Ordinal) ||
            value != exact)
        {
            throw Fail("P10_LIFECYCLE_METRIC_SCOPE_SEMANTIC_MISMATCH");
        }
        return exact;
    }

    internal static bool IncludesAssignment(
        StatRunDirectLifecycleMetricScope scope,
        string assignmentId,
        string? parentAssignmentId,
        string? flowInstanceId,
        string? runtimeFlowBranchId,
        string? runtimeFlowStepId,
        string? flowEffectiveStatus,
        bool? isFlowFinalNode)
    {
        scope = RequireValid(scope);
        if (!RuntimeMatches(scope, flowInstanceId))
            return false;
        if (!string.Equals(scope.MetricFamily,
                StatRunDirectLifecycleMetricFamilies.Basic,
                StringComparison.Ordinal))
            return true;

        return scope.ScopeMode switch
        {
            "DIRECT_CHILDREN_OR_SELF" =>
                string.Equals(assignmentId, scope.ScopeAssignmentId,
                    StringComparison.Ordinal) ||
                string.Equals(parentAssignmentId, scope.ScopeAssignmentId,
                    StringComparison.Ordinal),
            "DIRECT_CHILDREN" =>
                string.Equals(parentAssignmentId, scope.ScopeAssignmentId,
                    StringComparison.Ordinal),
            "FLOW_BRANCH" =>
                string.Equals(flowInstanceId, scope.FlowInstanceId,
                    StringComparison.Ordinal) &&
                string.Equals(runtimeFlowBranchId, scope.ScopeId,
                    StringComparison.Ordinal),
            "FLOW_STEP" =>
                string.Equals(flowInstanceId, scope.FlowInstanceId,
                    StringComparison.Ordinal) &&
                string.Equals(runtimeFlowStepId, scope.ScopeId,
                    StringComparison.Ordinal),
            "FLOW_EFFECTIVE_PATH" =>
                string.Equals(flowInstanceId, scope.FlowInstanceId,
                    StringComparison.Ordinal) &&
                string.Equals(flowEffectiveStatus,
                    scope.FlowEffectiveStatus, StringComparison.Ordinal),
            "FLOW_FINAL" =>
                string.Equals(flowInstanceId, scope.FlowInstanceId,
                    StringComparison.Ordinal) && isFlowFinalNode == true &&
                string.Equals(flowEffectiveStatus,
                    scope.FlowEffectiveStatus, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool RuntimeMatches(
        StatRunDirectLifecycleMetricScope scope,
        string? assignmentFlowInstanceId)
        => scope.FlowInstanceId is null
            ? assignmentFlowInstanceId is null
            : string.Equals(assignmentFlowInstanceId, scope.FlowInstanceId,
                StringComparison.Ordinal);

    private static StatRunDirectLifecycleMetricScope NormalizeFields(
        StatRunDirectLifecycleMetricScope value)
    {
        var family = Text(value.MetricFamily, "METRIC_FAMILY")
            .ToUpperInvariant();
        var mode = Text(value.ScopeMode, "SCOPE_MODE").ToUpperInvariant();
        var scopeId = Optional(value.ScopeId);
        var assignment = Text(value.ScopeAssignmentId, "SCOPE_ASSIGNMENT_ID");
        var flowInstance = Optional(value.FlowInstanceId);
        var flowBranch = Optional(value.RuntimeFlowBranchId);
        var flowStep = Optional(value.RuntimeFlowStepId);
        var effective = Optional(value.FlowEffectiveStatus)?.ToUpperInvariant();
        var epochId = Optional(value.ExecutionEpochId);
        var configKind = Text(value.ConfigurationKind,
            "CONFIGURATION_KIND").ToUpperInvariant();
        var configOwner = Text(value.ConfigurationOwnerId,
            "CONFIGURATION_OWNER_ID");
        var configVersion = Text(value.ConfigurationVersionId,
            "CONFIGURATION_VERSION_ID");
        var configOwnerSha = Sha(value.ConfigurationOwnerSha256,
            "CONFIGURATION_OWNER_SHA256");
        var configCanonicalSha = Sha(value.ConfigurationCanonicalJsonSha256,
            "CONFIGURATION_CANONICAL_JSON_SHA256");
        var capturePlan = Sha(value.CapturePlanSha256,
            "CAPTURE_PLAN_SHA256");
        var selector = Sha(value.ImmutableSelectorSha256,
            "IMMUTABLE_SELECTOR_SHA256");

        if (value.ConfigurationVersionNo < 1 ||
            value.ConfigurationRevision < 1)
            throw Fail("P10_LIFECYCLE_CONFIGURATION_REVISION_INVALID");
        if (!StatRunDirectLifecycleMetricFamilies.IsSupported(family))
            throw Fail("P10_LIFECYCLE_METRIC_FAMILY_UNSUPPORTED");
        var isBasic = family == StatRunDirectLifecycleMetricFamilies.Basic;
        if (isBasic)
        {
            if (mode is not (
                    "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN" or
                    "FLOW_BRANCH" or "FLOW_STEP" or
                    "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL"))
                throw Fail("P10_LIFECYCLE_BASIC_SCOPE_MODE_INVALID");
            if (scopeId is null)
                throw Fail("P10_LIFECYCLE_BASIC_SCOPE_ID_REQUIRED");
        }
        else if (mode != StatRunDirectLifecycleMetricFamilies.AllCandidates ||
                 scopeId is not null)
        {
            throw Fail("P10_LIFECYCLE_NON_BASIC_SCOPE_INVALID");
        }

        var flowMode = isBasic && mode is (
            "FLOW_BRANCH" or "FLOW_STEP" or
            "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL");
        var hasRuntime = flowInstance is not null;
        if (flowMode != hasRuntime && isBasic)
            throw Fail("P10_LIFECYCLE_BASIC_RUNTIME_SCOPE_MISMATCH");
        if (hasRuntime)
        {
            if (epochId is null || value.ExecutionEpoch is not > 0 ||
                value.ExecutionEpochRevision is not > 0 ||
                flowBranch is null || flowStep is null || effective is null)
                throw Fail("P10_LIFECYCLE_FLOW_RUNTIME_SCOPE_REQUIRED");
        }
        else if (epochId is not null || value.ExecutionEpoch is not null ||
                 value.ExecutionEpochRevision is not null ||
                 flowBranch is not null || flowStep is not null ||
                 effective is not null)
            throw Fail("P10_LIFECYCLE_NON_FLOW_RUNTIME_SCOPE_FORBIDDEN");

        if ((mode is "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN") &&
            !string.Equals(scopeId, assignment, StringComparison.Ordinal))
            throw Fail("P10_LIFECYCLE_ASSIGNMENT_SCOPE_ID_MISMATCH");
        if (mode == "FLOW_BRANCH" &&
            !string.Equals(scopeId, flowBranch, StringComparison.Ordinal))
            throw Fail("P10_LIFECYCLE_FLOW_BRANCH_SCOPE_MISMATCH");
        if (mode == "FLOW_STEP" &&
            !string.Equals(scopeId, flowStep, StringComparison.Ordinal))
            throw Fail("P10_LIFECYCLE_FLOW_STEP_SCOPE_MISMATCH");
        if ((mode is "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL") &&
            effective is null)
            throw Fail("P10_LIFECYCLE_FLOW_EFFECTIVE_SCOPE_MISMATCH");

        return value with
        {
            MetricFamily = family,
            ScopeMode = mode,
            ScopeId = scopeId,
            ScopeAssignmentId = assignment,
            FlowInstanceId = flowInstance,
            RuntimeFlowBranchId = flowBranch,
            RuntimeFlowStepId = flowStep,
            FlowEffectiveStatus = effective,
            ExecutionEpochId = epochId,
            ConfigurationKind = configKind,
            ConfigurationOwnerId = configOwner,
            ConfigurationVersionId = configVersion,
            ConfigurationOwnerSha256 = configOwnerSha,
            ConfigurationCanonicalJsonSha256 = configCanonicalSha,
            CapturePlanSha256 = capturePlan,
            ImmutableSelectorSha256 = selector
        };
    }

    private static string ComputeSemanticSha256(
        StatRunDirectLifecycleMetricScope value)
        => StatRunCanonicalJson.HashObject(new
        {
            version = Domain,
            value.MetricFamily,
            value.ScopeMode,
            value.ScopeId,
            value.ScopeAssignmentId,
            value.FlowInstanceId,
            value.RuntimeFlowBranchId,
            value.RuntimeFlowStepId,
            value.FlowEffectiveStatus,
            value.ExecutionEpochId,
            value.ExecutionEpoch,
            value.ExecutionEpochRevision,
            value.ConfigurationKind,
            value.ConfigurationOwnerId,
            value.ConfigurationVersionId,
            value.ConfigurationVersionNo,
            value.ConfigurationRevision,
            value.ConfigurationOwnerSha256,
            value.ConfigurationCanonicalJsonSha256,
            value.CapturePlanSha256,
            value.ImmutableSelectorSha256
        });

    private static string Text(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw Fail($"P10_LIFECYCLE_{field}_REQUIRED")
            : value.Trim();

    private static string? Optional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Sha(string? value, string field)
        => StatRunCanonicalJson.IsCanonicalSha256(value)
            ? value!
            : throw Fail($"P10_LIFECYCLE_{field}_INVALID");

    private static InvalidOperationException Fail(string reason) => new(reason);
}