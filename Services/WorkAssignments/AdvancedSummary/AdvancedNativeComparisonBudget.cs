namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

internal sealed class AdvancedNativeComparisonBudgetException(string reason) : Exception(reason);

// Per-process concurrent work cap, no queue and no retained actor dictionary.
// Existing source/calculator limits still apply. Not a distributed quota ledger.
internal static class AdvancedNativeComparisonBudget
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim Slots = new(2, 2);
    internal static IDisposable Enter()
    {
        if (!Slots.Wait(0)) throw new AdvancedNativeComparisonBudgetException("ADVANCED_NATIVE_COMPARISON_BUSY");
        return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Slots.Release(); }
    }
}
