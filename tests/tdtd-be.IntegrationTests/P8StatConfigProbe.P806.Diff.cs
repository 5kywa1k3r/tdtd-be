namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private Task RunDiffConfigurationCasesAsync(CancellationToken ct)
        => RunP806DiffConfigurationCasesAsync(ct);

    private async Task RunP806DiffConfigurationCasesAsync(CancellationToken ct)
    {
        await SeedP806FixturesAsync(ct);
        await AwaitP806InfrastructureBaselineAsync(ct);
        await RunP806SourceCasesAsync(ct);
        await RunP806SourceRejectionCasesAsync(ct);
        await RunP806PeriodPolicyCasesAsync(ct);
        await RunP806CrudCasesAsync(ct);
        await RunP806BarrierCasesAsync(ct);
    }
}
