using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IDynamicFlowMappingApplyFaultInjector
{
    void ThrowIfConfigured(string commandId, string faultPoint);
}

public static class DynamicFlowMappingApplyFaultPoints
{
    public const string BeforeReceipt = "BEFORE_RECEIPT";
    public const string AfterReceipt = "AFTER_RECEIPT";
    public const string BeforePayload = "BEFORE_PAYLOAD";
    public const string AfterPayload = "AFTER_PAYLOAD";
    public const string BeforeProjection = "BEFORE_PROJECTION";
    public const string AfterProjection = "AFTER_PROJECTION";
    public const string BeforeIntentCommit = "BEFORE_INTENT_COMMIT";
    public const string AfterIntentCommit = "AFTER_INTENT_COMMIT";
    public const string BeforeOutbox = "BEFORE_OUTBOX";
    public const string AfterOutbox = "AFTER_OUTBOX";
    public const string BeforeAudit = "BEFORE_AUDIT";
    public const string AfterAudit = "AFTER_AUDIT";
    public const string BeforeTransactionCommit =
        "BEFORE_TRANSACTION_COMMIT";
    public const string AfterTransactionCommit =
        "AFTER_TRANSACTION_COMMIT";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(
            [
                BeforeReceipt,
                AfterReceipt,
                BeforePayload,
                AfterPayload,
                BeforeProjection,
                AfterProjection,
                BeforeIntentCommit,
                AfterIntentCommit,
                BeforeOutbox,
                AfterOutbox,
                BeforeAudit,
                AfterAudit,
                BeforeTransactionCommit,
                AfterTransactionCommit
            ],
            StringComparer.Ordinal);
}

/// <summary>
/// Deliberate Testing-only failure at an exact P7 mapping apply write
/// boundary. The exception intentionally does not derive from
/// InvalidOperationException so API middleware cannot reinterpret the
/// infrastructure failure as client validation.
/// </summary>
public sealed class DynamicFlowMappingApplyInjectedFaultException(
    string message)
    : Exception(message);

/// <summary>
/// Exact-command, exact-point, one-shot apply fault injection. Configuration
/// is ignored outside Testing. Multiple command bindings let one isolated
/// backend execute a fault matrix without command prefixes or global points.
/// </summary>
public sealed class DynamicFlowMappingApplyFaultInjector
    : IDynamicFlowMappingApplyFaultInjector
{
    public const string BindingsConfigurationKey =
        "DynamicFlowMapping:TestingApplyFault:Bindings";
    public const string PauseBindingsConfigurationKey =
        "DynamicFlowMapping:TestingApplyPause:Bindings";
    public const string FailureMessage =
        "TEST_ONLY_DYNAMIC_FLOW_MAPPING_APPLY_FAILURE";
    public const string PauseTimeoutMessage =
        "TEST_ONLY_DYNAMIC_FLOW_MAPPING_APPLY_PAUSE_TIMEOUT";

    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        _bindings;
    private readonly ConcurrentDictionary<string, byte> _consumed =
        new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, ApplyPauseBinding>
        _pauseBindings;
    private readonly ConcurrentDictionary<string, byte> _pauseConsumed =
        new(StringComparer.Ordinal);

    public DynamicFlowMappingApplyFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            _bindings =
                new Dictionary<string, IReadOnlySet<string>>(
                    StringComparer.Ordinal);
            _pauseBindings =
                new Dictionary<string, ApplyPauseBinding>(
                    StringComparer.Ordinal);
            return;
        }

        var bindings =
            new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.Ordinal);
        foreach (var section in configuration
                     .GetSection(BindingsConfigurationKey)
                     .GetChildren())
        {
            var commandId = Normalize(section["CommandId"]) ??
                            throw new InvalidOperationException(
                                "Dynamic Flow mapping apply test fault binding requires an exact CommandId.");
            var configuredPoints = section
                .GetSection("Points")
                .GetChildren()
                .Select(child => Normalize(child.Value))
                .Where(value => value is not null)
                .Cast<string>()
                .Select(value => value.ToUpperInvariant())
                .ToArray();
            if (configuredPoints.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow mapping apply test fault binding {commandId} requires at least one point.");
            }
            if (configuredPoints.Length !=
                configuredPoints
                    .Distinct(StringComparer.Ordinal)
                    .Count())
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow mapping apply test fault binding {commandId} contains duplicate points.");
            }
            var unsupported = configuredPoints
                .Where(point =>
                    !DynamicFlowMappingApplyFaultPoints.All
                        .Contains(point))
                .ToArray();
            if (unsupported.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Unsupported Dynamic Flow mapping apply test fault point(s): {string.Join(", ", unsupported)}.");
            }
            if (!bindings.TryAdd(
                    commandId,
                    new HashSet<string>(
                        configuredPoints,
                        StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow mapping apply test fault CommandId {commandId} is bound more than once.");
            }
        }

        _bindings = bindings;
        _pauseBindings = ReadPauseBindings(configuration);
    }

    public void ThrowIfConfigured(
        string commandId,
        string faultPoint)
    {
        if (!DynamicFlowMappingApplyFaultPoints.All.Contains(
                faultPoint))
        {
            throw new ArgumentOutOfRangeException(
                nameof(faultPoint),
                faultPoint,
                "Unknown Dynamic Flow mapping apply fault point.");
        }

        var normalizedCommandId = Normalize(commandId);
        PauseIfConfigured(normalizedCommandId, faultPoint);
        if (normalizedCommandId is null ||
            !_bindings.TryGetValue(
                normalizedCommandId,
                out var points) ||
            !points.Contains(faultPoint) ||
            !_consumed.TryAdd(
                $"{normalizedCommandId}\n{faultPoint}",
                0))
        {
            return;
        }

        throw new DynamicFlowMappingApplyInjectedFaultException(
            $"{FailureMessage}:{faultPoint}");
    }

    private void PauseIfConfigured(
        string? commandId,
        string faultPoint)
    {
        if (commandId is null ||
            !_pauseBindings.TryGetValue(
                commandId,
                out var binding) ||
            !string.Equals(
                binding.Point,
                faultPoint,
                StringComparison.Ordinal) ||
            !_pauseConsumed.TryAdd(
                $"{commandId}\n{faultPoint}",
                0))
        {
            return;
        }

        WriteReachedMarker(
            binding.ReachedFile,
            $"{commandId}\n{faultPoint}\n");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(binding.ReleaseFile))
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(120))
            {
                throw new DynamicFlowMappingApplyInjectedFaultException(
                    $"{PauseTimeoutMessage}:{faultPoint}");
            }
            Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }
    }

    private static IReadOnlyDictionary<string, ApplyPauseBinding>
        ReadPauseBindings(IConfiguration configuration)
    {
        var bindings =
            new Dictionary<string, ApplyPauseBinding>(
                StringComparer.Ordinal);
        foreach (var section in configuration
                     .GetSection(PauseBindingsConfigurationKey)
                     .GetChildren())
        {
            var commandId = Normalize(section["CommandId"]) ??
                            throw new InvalidOperationException(
                                "Dynamic Flow mapping apply test pause requires an exact CommandId.");
            var point = Normalize(section["Point"])
                ?.ToUpperInvariant() ??
                        throw new InvalidOperationException(
                            $"Dynamic Flow mapping apply test pause {commandId} requires a Point.");
            if (!DynamicFlowMappingApplyFaultPoints.All.Contains(point))
            {
                throw new InvalidOperationException(
                    $"Unsupported Dynamic Flow mapping apply test pause point: {point}.");
            }
            var (reachedFile, releaseFile) = ValidateMarkerPaths(
                section["ReachedFile"],
                section["ReleaseFile"],
                $"Dynamic Flow mapping apply test pause {commandId}");
            if (!bindings.TryAdd(
                    commandId,
                    new ApplyPauseBinding(
                        point,
                        reachedFile,
                        releaseFile)))
            {
                throw new InvalidOperationException(
                    $"Dynamic Flow mapping apply test pause CommandId {commandId} is bound more than once.");
            }
        }
        return bindings;
    }

    internal static (string ReachedFile, string ReleaseFile)
        ValidateMarkerPaths(
            string? reachedFile,
            string? releaseFile,
            string subject)
    {
        reachedFile = Normalize(reachedFile);
        releaseFile = Normalize(releaseFile);
        if (reachedFile is null ||
            releaseFile is null ||
            !Path.IsPathFullyQualified(reachedFile) ||
            !Path.IsPathFullyQualified(releaseFile))
        {
            throw new InvalidOperationException(
                $"{subject} requires fully-qualified ReachedFile and ReleaseFile paths.");
        }
        reachedFile = Path.GetFullPath(reachedFile);
        releaseFile = Path.GetFullPath(releaseFile);
        var reachedDirectory = Path.GetDirectoryName(reachedFile);
        var releaseDirectory = Path.GetDirectoryName(releaseFile);
        if (string.IsNullOrWhiteSpace(reachedDirectory) ||
            !string.Equals(
                reachedDirectory,
                releaseDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                reachedFile,
                releaseFile,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetPathRoot(reachedDirectory),
                reachedDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{subject} marker paths must be distinct files in the same non-root directory.");
        }
        return (reachedFile, releaseFile);
    }

    internal static void WriteReachedMarker(
        string reachedFile,
        string content)
    {
        var directory = Path.GetDirectoryName(reachedFile)!;
        Directory.CreateDirectory(directory);
        var temporaryMarker =
            $"{reachedFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryMarker,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            {
                var marker = Encoding.UTF8.GetBytes(content);
                stream.Write(marker);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryMarker, reachedFile);
        }
        finally
        {
            if (File.Exists(temporaryMarker))
                File.Delete(temporaryMarker);
        }
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record ApplyPauseBinding(
        string Point,
        string ReachedFile,
        string ReleaseFile);
}
