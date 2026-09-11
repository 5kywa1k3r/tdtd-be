namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    // The five owned ranges are intentionally separate so production route/DTO
    // adjustments cannot blur the exact manifest-owned P8-OPS case registry.
    private async Task RunP808QuotaRaceAndCleanupCasesAsync(
        CancellationToken ct)
    {
        await RunP808ActiveJobQuotaCaseAsync(ct);
        await RunP808ConcurrentDuplicateEnqueueCaseAsync(ct);
        await RunP808OwnedCleanupRetryCaseAsync(ct);
    }
}

