using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Bson;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IDynamicFlowMappingReconcileFaultInjector
{
    void ThrowIfConfigured(string commandId, string faultPoint);
    void PauseAfterOutboxClaimIfConfigured(
        string commandId,
        string outboxId);
}

public static class DynamicFlowMappingReconcileFaultPoints
{
    public const string BeforeProjectors = "BEFORE_PROJECTORS";
    public const string BeforeFinalize = "BEFORE_FINALIZE";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(
            new[]
            {
                BeforeProjectors,
                BeforeFinalize
            },
            StringComparer.Ordinal);
}

public static class DynamicFlowMappingReconcilePausePoints
{
    public const string AfterOutboxClaim = "AFTER_OUTBOX_CLAIM";
}

public sealed class DynamicFlowMappingReconcileInjectedFaultException(
    string message)
    : Exception(message);

/// <summary>
/// One-shot, command-bound post-commit faults used by the P7 durability probe.
/// Configuration is ignored outside the Testing environment.
/// </summary>
public sealed class DynamicFlowMappingReconcileFaultInjector
    : IDynamicFlowMappingReconcileFaultInjector
{
    public const string CommandIdConfigurationKey =
        "DynamicFlowMapping:TestingReconcileFault:CommandId";
    public const string PointsConfigurationKey =
        "DynamicFlowMapping:TestingReconcileFault:Points";
    public const string FailureMessage =
        "TEST_ONLY_DYNAMIC_FLOW_MAPPING_RECONCILE_FAILURE";
    public const string PauseBindingsConfigurationKey =
        "DynamicFlowMapping:TestingReconcilePause:Bindings";
    public const string PauseTimeoutMessage =
        "TEST_ONLY_DYNAMIC_FLOW_MAPPING_RECONCILE_PAUSE_TIMEOUT";

    private readonly string? _commandId;
    private readonly IReadOnlySet<string> _points;
    private readonly ConcurrentDictionary<string, byte> _consumed =
        new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, ReconcilePauseBinding>
        _pauseBindings;
    private readonly ConcurrentDictionary<string, byte> _pauseConsumed =
        new(StringComparer.Ordinal);

    public DynamicFlowMappingReconcileFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            _points = new HashSet<string>(StringComparer.Ordinal);
            _pauseBindings =
                new Dictionary<string, ReconcilePauseBinding>(
                    StringComparer.Ordinal);
            return;
        }

        _commandId = Normalize(
            configuration[CommandIdConfigurationKey]);
        var configuredPoints = ReadConfiguredPoints(configuration)
            .Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unsupported = configuredPoints
            .Where(value =>
                !DynamicFlowMappingReconcileFaultPoints.All
                    .Contains(value))
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unsupported Dynamic Flow mapping reconcile test fault point(s): {string.Join(", ", unsupported)}.");
        }

        _points = new HashSet<string>(
            configuredPoints,
            StringComparer.Ordinal);
        _pauseBindings = ReadPauseBindings(configuration);
    }

    public void ThrowIfConfigured(
        string commandId,
        string faultPoint)
    {
        if (!DynamicFlowMappingReconcileFaultPoints.All.Contains(
                faultPoint))
        {
            throw new ArgumentOutOfRangeException(
                nameof(faultPoint),
                faultPoint,
                "Unknown Dynamic Flow mapping reconcile fault point.");
        }

        commandId = commandId?.Trim() ?? string.Empty;
        if (_commandId is null ||
            !string.Equals(
                commandId,
                _commandId,
                StringComparison.Ordinal) ||
            !_points.Contains(faultPoint) ||
            !_consumed.TryAdd(
                $"{commandId}\n{faultPoint}",
                0))
        {
            return;
        }

        throw new DynamicFlowMappingReconcileInjectedFaultException(
            $"{FailureMessage}:{faultPoint}");
    }

    public void PauseAfterOutboxClaimIfConfigured(
        string commandId,
        string outboxId)
    {
        var normalizedCommandId = Normalize(commandId);
        var normalizedOutboxId = Normalize(outboxId);
        if (normalizedCommandId is null ||
            normalizedOutboxId is null ||
            !ObjectId.TryParse(normalizedOutboxId, out _) ||
            !_pauseBindings.TryGetValue(
                BuildPauseKey(
                    normalizedCommandId,
                    normalizedOutboxId),
                out var binding) ||
            !_pauseConsumed.TryAdd(
                BuildPauseKey(
                    normalizedCommandId,
                    normalizedOutboxId),
                0))
        {
            return;
        }

        DynamicFlowMappingApplyFaultInjector.WriteReachedMarker(
            binding.ReachedFile,
            $"{normalizedCommandId}\n{normalizedOutboxId}\n{DynamicFlowMappingReconcilePausePoints.AfterOutboxClaim}\n");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(binding.ReleaseFile))
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(120))
            {
                throw new DynamicFlowMappingReconcileInjectedFaultException(
                    $"{PauseTimeoutMessage}:{DynamicFlowMappingReconcilePausePoints.AfterOutboxClaim}");
            }
            Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }
    }

    private static IReadOnlyDictionary<string, ReconcilePauseBinding>
        ReadPauseBindings(IConfiguration configuration)
    {
        var bindings =
            new Dictionary<string, ReconcilePauseBinding>(
                StringComparer.Ordinal);
        foreach (var section in configuration
                     .GetSection(PauseBindingsConfigurationKey)
                     .GetChildren())
        {
            var commandId = Normalize(section["CommandId"]) ??
                            throw new InvalidOperationException(
                                "Dynamic Flow mapping reconcile test pause requires an exact CommandId.");
            var outboxId = Normalize(section["OutboxId"]) ??
                           throw new InvalidOperationException(
                               $"Dynamic Flow mapping reconcile test pause {commandId} requires an exact OutboxId.");
            if (!ObjectId.TryParse(outboxId, out _))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow mapping reconcile test pause {commandId} OutboxId is invalid.");
            }
            var point = Normalize(section["Point"])
                ?.ToUpperInvariant() ??
                        throw new InvalidOperationException(
                            $"Dynamic Flow mapping reconcile test pause {commandId} requires a Point.");
            if (!string.Equals(
                    point,
                    DynamicFlowMappingReconcilePausePoints
                        .AfterOutboxClaim,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unsupported Dynamic Flow mapping reconcile test pause point: {point}.");
            }
            var (reachedFile, releaseFile) =
                DynamicFlowMappingApplyFaultInjector
                    .ValidateMarkerPaths(
                        section["ReachedFile"],
                        section["ReleaseFile"],
                        $"Dynamic Flow mapping reconcile test pause {commandId}/{outboxId}");
            var key = BuildPauseKey(commandId, outboxId);
            if (!bindings.TryAdd(
                    key,
                    new ReconcilePauseBinding(
                        reachedFile,
                        releaseFile)))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow mapping reconcile test pause {commandId}/{outboxId} is bound more than once.");
            }
        }
        return bindings;
    }

    private static string BuildPauseKey(
        string commandId,
        string outboxId)
        => $"{commandId}\n{outboxId}";

    private static IEnumerable<string> ReadConfiguredPoints(
        IConfiguration configuration)
    {
        var section = configuration.GetSection(
            PointsConfigurationKey);
        var children = section.GetChildren()
            .Select(child => Normalize(child.Value))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        if (children.Length > 0)
            return children;

        return (section.Value ?? string.Empty)
            .Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(value => value is not null)
            .Cast<string>();
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record ReconcilePauseBinding(
        string ReachedFile,
        string ReleaseFile);
}
