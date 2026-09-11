using System.Diagnostics;

namespace tdtd_be.IntegrationTests;

internal sealed record ScopedJobSnapshot(string Status, string? Detail = null);

internal sealed record ScopedJobTimelineEntry(
    int Attempt,
    long ElapsedMs,
    string Status,
    string? Detail);

internal sealed record ScopedJobPollResult(
    string FinalStatus,
    long DurationMs,
    IReadOnlyList<ScopedJobTimelineEntry> Timeline);

/// <summary>
/// Reusable foundation for future phase workers: invoke one explicitly scoped
/// operation, then observe its durable state until success/failure or timeout.
/// It never waits for a Hangfire timer.
/// </summary>
internal static class ScopedJobPoller
{
    public static async Task<ScopedJobPollResult> InvokeAndPollAsync(
        Func<CancellationToken, Task> invokeScopedWorker,
        Func<CancellationToken, Task<ScopedJobSnapshot>> readSnapshot,
        Func<ScopedJobSnapshot, bool> isTerminal,
        Func<ScopedJobSnapshot, bool> isSuccessful,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invokeScopedWorker);
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(isTerminal);
        ArgumentNullException.ThrowIfNull(isSuccessful);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        await invokeScopedWorker(ct);
        var timer = Stopwatch.StartNew();
        var timeline = new List<ScopedJobTimelineEntry>();
        var attempt = 0;
        while (timer.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await readSnapshot(ct);
            timeline.Add(new ScopedJobTimelineEntry(++attempt, timer.ElapsedMilliseconds, snapshot.Status, snapshot.Detail));
            if (isTerminal(snapshot))
            {
                if (!isSuccessful(snapshot))
                {
                    throw new InvalidOperationException(
                        $"Scoped job reached terminal failure '{snapshot.Status}'. Detail={snapshot.Detail ?? "<none>"}.");
                }

                return new ScopedJobPollResult(snapshot.Status, timer.ElapsedMilliseconds, timeline);
            }

            var remaining = timeout - timer.Elapsed;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, ct);
        }

        var last = timeline.LastOrDefault();
        throw new TimeoutException(
            $"Scoped job poll timed out after {timeout}. Last={last?.Status ?? "<none>"}; Detail={last?.Detail ?? "<none>"}.");
    }
}
