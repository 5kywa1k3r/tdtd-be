using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    private async Task<NormalizedRequest> P9ApplyLockedRuntimeConfigAsync(
        WorkAssignment scope,
        DynamicFormTemplate template,
        NormalizedRequest request,
        StatRunCandidateBinding candidate,
        CancellationToken ct,
        bool nativeConsumer = false)
    {
        // This scalar/Excel consumer cannot read native results yet. Keep both
        // request and background-refresh paths closed before loading any cache.
        if (!nativeConsumer && template.NativeTablesVersion is not null)
            throw P804Schema("$.template", "BASIC_SUMMARY_NATIVE_RESULT_CONSUMER_REQUIRED");
        var state = await P804LoadStateAsync(null, scope, template, ct);
        if (state.IsVirtual ||
            !string.Equals(state.Status, StatConfigStatuses.Locked, StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_RUN_CANDIDATE_INVALID,
                new
                {
                    reason = "BASIC_SUMMARY_LOCKED_CONFIG_REQUIRED",
                    scopeAssignmentId = scope.Id,
                    dynamicFormTemplateId = template.Id,
                    writes = 0
                });
        }

        var payload = state.Payload;
        if (!nativeConsumer && payload.NativeTargets is { Count: > 0 })
            throw P804Schema("$.payload.nativeTargets", "BASIC_SUMMARY_NATIVE_RESULT_CONSUMER_REQUIRED");
        var configuredScope = payload.SourceScope
            ?? throw new InvalidOperationException("Locked Basic config has no source scope.");
        P9RejectSpoofedValue(request.SourceScopeMode, configuredScope.Mode, "sourceScopeMode");
        P9RejectSpoofedValue(request.SourceFlowInstanceId, configuredScope.FlowInstanceId, "sourceFlowInstanceId");
        P9RejectSpoofedValue(request.SourceFlowStepId, configuredScope.FlowStepId, "sourceFlowStepId");
        P9RejectSpoofedValue(request.SourceFlowBranchId, configuredScope.FlowBranchId, "sourceFlowBranchId");
        P9RejectSpoofedValue(request.SourceFlowEffectiveStatus, configuredScope.FlowEffectiveStatus, "sourceFlowEffectiveStatus");

        if (WorkAssignmentSummarySourceScope.IsFlowMode(configuredScope.Mode))
        {
            if (nativeConsumer)
                throw P804Schema("$.payload.sourceScope", "BASIC_NATIVE_FLOW_CONSUMER_REQUIRED");
            _candidateActivation.RequireCapability(
                StatRunCapabilities.FlowScopes,
                StatRunRouteRegistry.CoreJob(StatRunCapabilities.FlowScopes));
        }

        var period = payload.PeriodRule
            ?? throw new InvalidOperationException("Locked Basic config has no period rule.");
        var detail = payload.DetailHints
            ?? throw new InvalidOperationException("Locked Basic config has no detail hints.");
        var rules = NormalizeRules((payload.Targets ?? Array.Empty<WorkAssignmentBasicSummaryTargetPayload>())
            .Select(target => new WorkAssignmentBasicSummaryRuleDto
            {
                TargetKind = target.ConceptKind ?? string.Empty,
                TargetKey = target.ConceptKey ?? string.Empty,
                Operation = target.Operation ?? string.Empty
            })
            .ToList());
        var authoritative = request with
        {
            DynamicFormTemplateId = template.Id,
            DefaultMethods = NormalizeDefaultMethods(null),
            Rules = rules,
            PeriodScopeMode = period.Mode!,
            PeriodKey = period.PeriodKey,
            PeriodKeyFrom = period.PeriodKeyFrom,
            PeriodKeyTo = period.PeriodKeyTo,
            SourceScopeMode = configuredScope.Mode,
            SourceFlowInstanceId = configuredScope.FlowInstanceId,
            SourceFlowStepId = configuredScope.FlowStepId,
            SourceFlowBranchId = configuredScope.FlowBranchId,
            SourceFlowEffectiveStatus = configuredScope.FlowEffectiveStatus,
            IncludeSourceRows = detail.IncludeSourceRows ?? false,
            MaxTextChars = Math.Clamp(
                detail.MaxTextChars ?? DefaultMaxTextChars,
                WorkAssignmentBasicSummaryConfigContract.MinimumMaxTextChars,
                WorkAssignmentBasicSummaryConfigContract.MaximumMaxTextChars),
            RuntimePin = new P9BasicRuntimePin(
                state.ConfigId,
                state.VersionId,
                state.VersionNo,
                state.Revision,
                state.ConfigHash,
                state.DependencyPins.ToList(),
                candidate)
        };
        return AttachSourceScope(scope, authoritative);
    }

    private static void P9RejectSpoofedValue(
        string? requested,
        string? locked,
        string field)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return;
        if (string.Equals(requested.Trim(), locked, StringComparison.OrdinalIgnoreCase))
            return;
        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field,
                reason = "BASIC_SUMMARY_MEMBERSHIP_SPOOF_REJECTED",
                writes = 0
            });
    }

    private static void P9ApplyRuntimeMeta(
        WorkAssignmentBasicSummaryResponse response,
        P9BasicRuntimePin? pin)
    {
        if (pin is null)
            throw new InvalidOperationException("Basic summary runtime lineage pin is missing.");
        response.Meta.ConfigId = pin.ConfigId;
        response.Meta.ConfigVersionId = pin.VersionId;
        response.Meta.ConfigVersionNo = pin.VersionNo;
        response.Meta.ConfigRevision = pin.Revision;
        response.Meta.ConfigHash = pin.ConfigHash;
        response.Meta.ConfigDependencyPins = pin.DependencyPins.ToList();
        response.Meta.CandidateChainId = pin.Candidate.ChainId;
        response.Meta.CandidatePromptId = pin.Candidate.PromptId;
        response.Meta.CandidateStage = pin.Candidate.Stage;
        response.Meta.CandidateCatalogRawSha256 = pin.Candidate.CatalogRawSha256;
        response.Meta.CandidateCatalogSemanticSha256 = pin.Candidate.CatalogSemanticSha256;
        response.Meta.CandidateStageLockSha256 = pin.Candidate.StageLockSha256;
    }
}
