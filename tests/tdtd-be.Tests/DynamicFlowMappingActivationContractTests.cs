using tdtd_be.Services.WorkAssignmentReports;

internal static class DynamicFlowMappingActivationContractTests
{
    public static void Run()
    {
        ThroughFiveEnablesPreviewOnly();
        ThroughSevenAddsPreviewTokenButNotApply();
        ThroughEightEnablesApplyAndNineEnablesLifecycle();
        DisabledRuntimeBlocksEveryMappingSlice();
        MissingThroughPreservesFullActivation();
    }

    private static void ThroughFiveEnablesPreviewOnly()
    {
        var activation = Resolve(runtimeEnabled: true, configuredThrough: 5);

        Require(activation.CanPreview, "P7-05 must enable mapping preview");
        Require(
            !activation.CanIssuePreviewToken,
            "P7-05 must not issue a P7-07 preview token");
        Require(
            !activation.CanPreflightApply,
            "P7-05 must not enter the P7-07 apply preflight");
        Require(
            !activation.CanApply,
            "P7-05 must keep durable apply blocked until P7-08");
        Require(
            !activation.CanLifecycleMapping,
            "P7-05 must keep mapping lifecycle blocked until P7-09");
    }

    private static void ThroughSevenAddsPreviewTokenButNotApply()
    {
        var activation = Resolve(runtimeEnabled: true, configuredThrough: 7);

        Require(activation.CanPreview, "P7-07 must keep preview enabled");
        Require(
            activation.CanIssuePreviewToken,
            "P7-07 must enable preview-token issuance");
        Require(
            activation.CanPreflightApply,
            "P7-07 must enable canonical apply preflight");
        Require(
            !activation.CanApply,
            "P7-07 must keep durable apply blocked until P7-08");
        Require(
            !activation.CanLifecycleMapping,
            "P7-07 must keep mapping lifecycle blocked until P7-09");
    }

    private static void ThroughEightEnablesApplyAndNineEnablesLifecycle()
    {
        foreach (var through in new[] { 8, 9, 10 })
        {
            var activation = Resolve(
                runtimeEnabled: true,
                configuredThrough: through);

            Require(
                activation.CanPreview,
                $"P7 through={through} must keep preview enabled");
            Require(
                activation.CanIssuePreviewToken,
                $"P7 through={through} must keep preview tokens enabled");
            Require(
                activation.CanPreflightApply,
                $"P7 through={through} must keep apply preflight enabled");
            Require(
                activation.CanApply,
                $"P7 through={through} must enable apply");
            Require(
                activation.CanLifecycleMapping == (through >= 9),
                $"P7 through={through} must enable mapping lifecycle only at P7-09 or later");
        }
    }

    private static void DisabledRuntimeBlocksEveryMappingSlice()
    {
        foreach (var through in new int?[] { null, 5, 7, 8, 9 })
        {
            var activation = Resolve(
                runtimeEnabled: false,
                configuredThrough: through);

            Require(
                !activation.CanPreview &&
                !activation.CanIssuePreviewToken &&
                !activation.CanPreflightApply &&
                !activation.CanApply &&
                !activation.CanLifecycleMapping,
                $"disabled runtime must block all mapping slices at through={through?.ToString() ?? "<absent>"}");
        }
    }

    private static void MissingThroughPreservesFullActivation()
    {
        var activation = Resolve(
            runtimeEnabled: true,
            configuredThrough: null);

        Require(
            activation.CanPreview &&
            activation.CanIssuePreviewToken &&
            activation.CanPreflightApply &&
            activation.CanApply &&
            activation.CanLifecycleMapping,
            "an absent staged override must preserve full mapping activation");
    }

    private static DynamicFlowMappingActivation Resolve(
        bool runtimeEnabled,
        int? configuredThrough)
        => WorkAssignmentReportService.ResolveDynamicFlowMappingActivation(
            runtimeEnabled,
            configuredThrough);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
