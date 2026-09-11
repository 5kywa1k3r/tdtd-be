using System.Collections.Concurrent;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowDefinitionFaultInjector
{
    void ThrowIfConfigured(string commandId, string faultPoint);
}

/// <summary>
/// Deliberate infrastructure failure used only by the Testing fault injector.
/// It must not derive from InvalidOperationException because the legacy API
/// middleware maps that type to a client validation response.
/// </summary>
public sealed class DynamicFlowDefinitionInjectedFaultException(string message) : Exception(message);

public static class DynamicFlowDefinitionFaultPoints
{
    public const string BeforeFamilyWrite = "BEFORE_FAMILY_WRITE";
    public const string AfterFamilyWrite = "AFTER_FAMILY_WRITE";
    public const string BeforeVersionWrite = "BEFORE_VERSION_WRITE";
    public const string AfterVersionWrite = "AFTER_VERSION_WRITE";
    public const string BeforeReceiptWrite = "BEFORE_RECEIPT_WRITE";
    public const string AfterReceiptWrite = "AFTER_RECEIPT_WRITE";
    public const string BeforeAuditWrite = "BEFORE_AUDIT_WRITE";
    public const string AfterAuditWrite = "AFTER_AUDIT_WRITE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[]
        {
            BeforeFamilyWrite,
            AfterFamilyWrite,
            BeforeVersionWrite,
            AfterVersionWrite,
            BeforeReceiptWrite,
            AfterReceiptWrite,
            BeforeAuditWrite,
            AfterAuditWrite
        },
        StringComparer.Ordinal);
}

/// <summary>
/// Deterministic, one-shot fault injection for transaction rollback tests. It
/// is inert outside the Testing environment and requires an exact command id
/// or command-id prefix plus an allow-listed write point from server configuration.
/// </summary>
public sealed class DynamicFlowDefinitionFaultInjector : IDynamicFlowDefinitionFaultInjector
{
    public const string CommandIdConfigurationKey =
        "DynamicFlowDefinition:TestingFault:CommandId";
    public const string CommandIdPrefixConfigurationKey =
        "DynamicFlowDefinition:TestingFault:CommandIdPrefix";
    public const string PointsConfigurationKey =
        "DynamicFlowDefinition:TestingFault:Points";
    public const string FailureMessage =
        "TEST_ONLY_DYNAMIC_FLOW_DEFINITION_TRANSACTION_FAILURE";

    private readonly string? _commandId;
    private readonly string? _commandIdPrefix;
    private readonly IReadOnlySet<string> _points;
    private readonly ConcurrentDictionary<string, byte> _consumed = new(StringComparer.Ordinal);

    public DynamicFlowDefinitionFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            _points = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        _commandId = Normalize(configuration[CommandIdConfigurationKey]);
        _commandIdPrefix = Normalize(configuration[CommandIdPrefixConfigurationKey]);
        var configuredPoints = ReadConfiguredPoints(configuration)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var unsupportedPoints = configuredPoints
            .Where(x => !DynamicFlowDefinitionFaultPoints.All.Contains(x))
            .ToArray();
        if (unsupportedPoints.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unsupported Dynamic Flow definition test fault point(s): {string.Join(", ", unsupportedPoints)}.");
        }

        _points = new HashSet<string>(configuredPoints, StringComparer.Ordinal);
    }

    public void ThrowIfConfigured(string commandId, string faultPoint)
    {
        if (!DynamicFlowDefinitionFaultPoints.All.Contains(faultPoint))
            throw new ArgumentOutOfRangeException(nameof(faultPoint), faultPoint, "Unknown Dynamic Flow definition fault point.");

        var normalizedCommandId = Normalize(commandId);
        var commandMatches =
            (_commandId is not null &&
             string.Equals(_commandId, normalizedCommandId, StringComparison.Ordinal)) ||
            (_commandIdPrefix is not null &&
             normalizedCommandId?.StartsWith(_commandIdPrefix, StringComparison.Ordinal) == true);
        if (!commandMatches ||
            !_points.Contains(faultPoint) ||
            !_consumed.TryAdd($"{normalizedCommandId}\n{faultPoint}", 0))
        {
            return;
        }

        throw new DynamicFlowDefinitionInjectedFaultException($"{FailureMessage}:{faultPoint}");
    }

    private static IEnumerable<string> ReadConfiguredPoints(IConfiguration configuration)
    {
        var section = configuration.GetSection(PointsConfigurationKey);
        var childValues = section.GetChildren()
            .Select(x => Normalize(x.Value))
            .Where(x => x is not null)
            .Cast<string>()
            .ToArray();
        if (childValues.Length > 0)
            return childValues;

        return (section.Value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Normalize(x))
            .Where(x => x is not null)
            .Cast<string>();
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
