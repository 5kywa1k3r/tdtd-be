using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.IntegrationTests;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum HarnessVerdict
{
    DAT,
    KHONG_DAT,
    CHUA_CHAY
}

internal sealed record CaseObservation(string Detail, string Fingerprint);

internal sealed record HarnessCaseResult(
    string CaseId,
    HarnessVerdict Verdict,
    string Detail,
    string Fingerprint,
    long DurationMs);

internal sealed record IterationResult(
    int Iteration,
    string RunKey,
    string DatabaseName,
    string ReplicaSetName,
    IReadOnlyList<HarnessCaseResult> Cases,
    string NormalizedSha256,
    bool CleanupSucceeded,
    IReadOnlyList<string> CleanupErrors);

internal sealed record HarnessResult(
    string RunKey,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int RequestedIterations,
    IReadOnlyList<IterationResult> Iterations,
    bool Deterministic,
    bool Passed,
    string? FailureReason);

internal sealed class HarnessCaseRunner
{
    private static readonly AsyncLocal<string?> ActiveCase = new();
    private readonly List<HarnessCaseResult> _results = [];

    public IReadOnlyList<HarnessCaseResult> Results => _results;
    internal static string? ActiveCaseId => ActiveCase.Value;

    public async Task RunAsync(string caseId, Func<Task<CaseObservation>> action)
    {
        var timer = Stopwatch.StartNew();
        var previousCaseId = ActiveCase.Value;
        ActiveCase.Value = caseId;
        try
        {
            var observation = await action();
            Add(caseId, HarnessVerdict.DAT, observation.Detail, observation.Fingerprint, timer.ElapsedMilliseconds);
        }
        catch (HarnessCaseNotRunnableException ex)
        {
            Add(caseId, HarnessVerdict.CHUA_CHAY, ex.Message, ex.GetType().Name, timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Add(
                caseId,
                HarnessVerdict.KHONG_DAT,
                $"{ex.GetType().Name}: {ex.Message}",
                ex.GetType().Name,
                timer.ElapsedMilliseconds);
        }
        finally
        {
            ActiveCase.Value = previousCaseId;
        }
    }

    public string BuildNormalizedSha256()
    {
        var normalized = _results
            .OrderBy(x => x.CaseId, StringComparer.Ordinal)
            .Select(x => new { x.CaseId, x.Verdict, x.Fingerprint })
            .ToArray();
        var json = JsonSerializer.Serialize(normalized, EvidenceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private void Add(string caseId, HarnessVerdict verdict, string detail, string fingerprint, long durationMs)
    {
        var row = new HarnessCaseResult(caseId, verdict, detail, fingerprint, durationMs);
        _results.Add(row);
        Console.WriteLine($"[{verdict}] {caseId}: {detail}");
    }
}

internal sealed class HarnessCaseNotRunnableException : Exception
{
    public HarnessCaseNotRunnableException(string message) : base(message)
    {
    }
}

internal static class HarnessAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}. Expected={expected}; Actual={actual}.");
    }

    public static T Required<T>(T? value, string dependency) where T : class
        => value ?? throw new HarnessCaseNotRunnableException($"Missing prerequisite: {dependency}.");
}
