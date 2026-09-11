namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunFlowContributionCasesAsync(CancellationToken ct)
    {
        await SeedP807FixturesAsync(ct);
        await RunP807VersionCasesAsync(ct);
        await RunP807OverrideCasesAsync(ct);
        await RunP807ProfileCasesAsync(ct);
        await RunP807RegressionCasesAsync(ct);
    }
}
