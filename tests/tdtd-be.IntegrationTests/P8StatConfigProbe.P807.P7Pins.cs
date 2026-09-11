namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly IReadOnlyDictionary<string, string> P7OwnerSourcePins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingEngine.cs"] =
                "3e4bdafd84eb5ff09a7e3bfde91f1b0c80e9e0ca127ffed0db4b0dd2dd81b085",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowPolicyEvaluator.cs"] =
                "bf14b57d130f0c8754058e51fc82f8d4ce0444364e99ce032296b210cec72832",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingCanonicalSourceContract.cs"] =
                "686e4415e0ff499274e87d69aac2bdba985992284e0c639da3f89695f4162e9e",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingRuntimeContract.cs"] =
                "2308cc01dd9fccb30c152e0afea4cad8767e4985efdde70413d7d95dab35240b",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingSecurityContract.cs"] =
                "09c7ab23f51fe5cfeab7a53dc82a4658a7ca510a548f0fbaa4cc3a365cf15c1a",
            ["tdtd-be/Models/DynamicFlowMappingPersistence.cs"] =
                "22eb3f1049147b82ea75213e0de2523b6115dafb7c228da5a06a55868bb5e290",
            ["tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs"] =
                "e2acb5fb4daa03dd6f6722466d644c6868ef6dc9d65ad4f4f1307d593af36cbf",
            ["tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingActivation.cs"] =
                "6cd581ca76be6b187fe64d5d4ff661c800eb7a2d7b386fedacca3cbd3951f374",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowLockedSnapshotIntegrity.cs"] =
                "9476dac746f92f02475fae26ab97b249bf9f6bc58dc3e2047609269c658790e4",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingCallerRedaction.cs"] =
                "79d703bb643695c0c49388ac547de96a5c2dc7626137a299f2d77b648ea7f522",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingExpressionEvaluator.cs"] =
                "5811f361a676f8e490181976b09ada66418a39e5cb6e8beade6faf80293761d9",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowMappingLifecycleContract.cs"] =
                "a7adc484583b58c324efd81b73de0053e8c8d26af8ef3169ed097a3b5e6403b7",
            ["tdtd-be/Services/DynamicFlows/DynamicFlowRuntimeMappingLifecycle.cs"] =
                "7d87c762fa62d7707198c02e439f28c6044da06d67e5a78ca010c2e3ca0ca539",
            ["tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingLifecycle.cs"] =
                "7301b62384444d46354bec241d3f9677625a6f29e56b9f0f031ea5046f3eb936"
        };
}
