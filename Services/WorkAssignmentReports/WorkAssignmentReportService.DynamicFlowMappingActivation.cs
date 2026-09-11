using tdtd_be.Common.Errors;

namespace tdtd_be.Services.WorkAssignmentReports;

internal readonly record struct DynamicFlowMappingActivation(
    bool CanPreview,
    bool CanIssuePreviewToken,
    bool CanPreflightApply,
    bool CanApply,
    bool CanLifecycleMapping);

public sealed partial class WorkAssignmentReportService
{
    private const string DynamicFlowMappingTestingActivationThroughKey =
        "DynamicFlowMapping:TestingCandidateActivationThrough";
    private const int DynamicFlowMappingPreviewPhase = 4;
    private const int DynamicFlowMappingPreviewTokenPhase = 7;
    private const int DynamicFlowMappingApplyPhase = 8;
    private const int DynamicFlowMappingLifecyclePhase = 9;
    private const int DynamicFlowMappingCurrentPhase = 9;

    internal static DynamicFlowMappingActivation
        ResolveDynamicFlowMappingActivation(
            bool runtimeEnabled,
            int? configuredThrough)
    {
        if (!runtimeEnabled)
            return default;

        var activationThrough =
            configuredThrough ?? DynamicFlowMappingCurrentPhase;
        return new DynamicFlowMappingActivation(
            CanPreview:
                activationThrough >= DynamicFlowMappingPreviewPhase,
            CanIssuePreviewToken:
                activationThrough >= DynamicFlowMappingPreviewTokenPhase,
            CanPreflightApply:
                activationThrough >= DynamicFlowMappingPreviewTokenPhase,
            CanApply:
                activationThrough >= DynamicFlowMappingApplyPhase,
            CanLifecycleMapping:
                activationThrough >= DynamicFlowMappingLifecyclePhase);
    }

    private DynamicFlowMappingActivation
        ResolveDynamicFlowMappingActivation()
        => ResolveDynamicFlowMappingActivation(
            _dynamicFlowRuntimeActivationPolicy
                .P7MappingCandidateExecutionEnabled,
            _configuration.GetValue<int?>(
                DynamicFlowMappingTestingActivationThroughKey));

    private bool IsDynamicFlowMappingPreviewTokenEnabled()
    {
        var activation = ResolveDynamicFlowMappingActivation();
        EnsureDynamicFlowMappingPreviewEnabled(activation);
        return activation.CanIssuePreviewToken;
    }

    private bool IsDynamicFlowMappingApplyEnabled()
        => ResolveDynamicFlowMappingActivation().CanApply;

    private static void EnsureDynamicFlowMappingPreviewEnabled(
        DynamicFlowMappingActivation activation)
    {
        if (activation.CanPreview)
            return;

        throw DynamicFlowMappingActivationBlocked(
            "DYNAMIC_FLOW_MAPPING_PREVIEW_BLOCKED_UNTIL_P7_04",
            "P7-04");
    }

    private void EnsureDynamicFlowMappingApplyEnabled()
    {
        if (ResolveDynamicFlowMappingActivation().CanPreflightApply)
            return;

        throw DynamicFlowMappingActivationBlocked(
            "DYNAMIC_FLOW_MAPPING_APPLY_BLOCKED_UNTIL_P7_08",
            "P7-08");
    }

    private void EnsureDynamicFlowMappingPersistenceEnabled()
    {
        if (IsDynamicFlowMappingApplyEnabled())
            return;

        throw DynamicFlowMappingActivationBlocked(
            "DYNAMIC_FLOW_MAPPING_APPLY_BLOCKED_UNTIL_P7_08",
            "P7-08");
    }

    private static AppException DynamicFlowMappingActivationBlocked(
        string reason,
        string blockedUntilPhase)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                field = "mappingSlice",
                reason,
                executionEligibility = "BLOCKED_UNTIL_TARGET_PHASE",
                blockedUntilPhase,
                canExecute = false
            });
}
