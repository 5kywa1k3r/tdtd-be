using System.Collections.Concurrent;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

public interface IStatConfigOperationsService
{
    Task<StatConfigValidationJobStatusResponse> EnqueueAsync(
        string ownerKind,
        string ownerId,
        StatConfigMutationEnvelope<StatConfigValidationEnqueuePayload> request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigValidationJobStatusResponse> GetSafeStatusAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<PagedResult<StatConfigValidationJobDiagnosticsResponse>> SearchDiagnosticsAsync(
        StatConfigValidationJobSearchRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigValidationJobDiagnosticsResponse> GetDiagnosticsAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigValidationProcessResponse> ProcessPendingAsync(
        int maxJobs,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigValidationJobStatusResponse> ResetAsync(
        string jobId,
        StatConfigMutationEnvelope<StatConfigValidationResetPayload> request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigValidationJobStatusResponse> CancelAsync(
        string jobId,
        StatConfigMutationEnvelope<StatConfigValidationCancelPayload> request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigValidationCleanupResponse> CleanupAsync(
        StatConfigMutationEnvelope<StatConfigValidationCleanupPayload> request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatConfigIndexReadinessResponse> ValidateIndexesAsync(
        MeResponse actor,
        CancellationToken ct = default);
}

public interface IStatConfigOperationsFaultInjector
{
    void ThrowIfConfigured(string commandId, string faultPoint);
}

public static class StatConfigOperationsFaultPoints
{
    public const string BeforeJobWrite = "BEFORE_JOB_WRITE";
    public const string AfterJobWrite = "AFTER_JOB_WRITE";
    public const string BeforeReceiptWrite = "BEFORE_RECEIPT_WRITE";
    public const string AfterReceiptWrite = "AFTER_RECEIPT_WRITE";
    public const string BeforeOutboxWrite = "BEFORE_OUTBOX_WRITE";
    public const string AfterOutboxWrite = "AFTER_OUTBOX_WRITE";
    public const string ValidationTransient = "VALIDATION_TRANSIENT";
    public const string ValidationPermanent = "VALIDATION_PERMANENT";
    public const string CleanupTransient = "CLEANUP_TRANSIENT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[]
        {
            BeforeJobWrite,
            AfterJobWrite,
            BeforeReceiptWrite,
            AfterReceiptWrite,
            BeforeOutboxWrite,
            AfterOutboxWrite,
            ValidationTransient,
            ValidationPermanent,
            CleanupTransient
        },
        StringComparer.Ordinal);
}

public sealed class StatConfigOperationsInjectedFaultException(string message)
    : Exception(message);

public sealed class StatConfigOperationsFaultInjector
    : IStatConfigOperationsFaultInjector
{
    public const string CommandIdConfigurationKey =
        "StatConfigOperations:TestingFault:CommandId";
    public const string CommandIdPrefixConfigurationKey =
        "StatConfigOperations:TestingFault:CommandIdPrefix";
    public const string PointsConfigurationKey =
        "StatConfigOperations:TestingFault:Points";
    public const string FailureMessage =
        "TEST_ONLY_STAT_CONFIG_OPERATIONS_FAILURE";

    private readonly string? _commandId;
    private readonly string? _commandIdPrefix;
    private readonly IReadOnlySet<string> _points;
    private readonly ConcurrentDictionary<string, byte> _consumed =
        new(StringComparer.Ordinal);

    public StatConfigOperationsFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            _points = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        _commandId = Normalize(configuration[CommandIdConfigurationKey]);
        _commandIdPrefix = Normalize(
            configuration[CommandIdPrefixConfigurationKey]);
        var configured = ReadPoints(configuration)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unsupported = configured
            .Where(x => !StatConfigOperationsFaultPoints.All.Contains(x))
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unsupported stat-config operations test fault point(s): {string.Join(", ", unsupported)}.");
        }

        _points = new HashSet<string>(configured, StringComparer.Ordinal);
    }

    public void ThrowIfConfigured(string commandId, string faultPoint)
    {
        if (!StatConfigOperationsFaultPoints.All.Contains(faultPoint))
        {
            throw new ArgumentOutOfRangeException(
                nameof(faultPoint),
                faultPoint,
                "Unknown stat-config operations fault point.");
        }

        var normalized = Normalize(commandId);
        var matches =
            (_commandId is not null &&
             string.Equals(_commandId, normalized, StringComparison.Ordinal)) ||
            (_commandIdPrefix is not null &&
             normalized?.StartsWith(
                 _commandIdPrefix,
                 StringComparison.Ordinal) == true);
        if (!matches ||
            !_points.Contains(faultPoint) ||
            !_consumed.TryAdd($"{normalized}\n{faultPoint}", 0))
        {
            return;
        }

        throw new StatConfigOperationsInjectedFaultException(
            $"{FailureMessage}:{faultPoint}");
    }

    private static IEnumerable<string> ReadPoints(
        IConfiguration configuration)
    {
        var section = configuration.GetSection(PointsConfigurationKey);
        var values = section.GetChildren()
            .Select(x => Normalize(x.Value))
            .Where(x => x is not null)
            .Cast<string>()
            .ToArray();
        if (values.Length > 0)
            return values;

        return (section.Value ?? string.Empty)
            .Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(x => x is not null)
            .Cast<string>();
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
