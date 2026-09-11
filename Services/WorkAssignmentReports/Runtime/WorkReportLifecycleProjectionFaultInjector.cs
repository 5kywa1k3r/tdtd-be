namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IWorkReportLifecycleProjectionFaultInjector
{
    void ThrowIfConfigured(IReadOnlyCollection<tdtd_be.Models.WorkReportLifecycleProjectionOutboxEntry> pendingEntries);
}

/// <summary>
/// Deterministic integration-only fault. It is inert outside the Testing environment and
/// requires an exact command id, so no request header or public production switch can trigger it.
/// </summary>
public sealed class WorkReportLifecycleProjectionFaultInjector : IWorkReportLifecycleProjectionFaultInjector
{
    public const string ConfigurationKey =
        "WorkReportLifecycleProjectionOutbox:TestingFailOnceCommandId";
    public const string FailureMessage =
        "TEST_ONLY_LIFECYCLE_PROJECTION_FAILURE_AFTER_COMMIT";

    private readonly string? _commandId;
    private int _consumed;

    public WorkReportLifecycleProjectionFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _commandId = environment.IsEnvironment("Testing")
            ? Normalize(configuration[ConfigurationKey])
            : null;
    }

    public void ThrowIfConfigured(
        IReadOnlyCollection<tdtd_be.Models.WorkReportLifecycleProjectionOutboxEntry> pendingEntries)
    {
        if (_commandId is null || Volatile.Read(ref _consumed) != 0)
            return;
        if (!pendingEntries.Any(x => string.Equals(x.CommandId, _commandId, StringComparison.Ordinal)))
            return;
        if (Interlocked.CompareExchange(ref _consumed, 1, 0) == 0)
            throw new InvalidOperationException(FailureMessage);
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
