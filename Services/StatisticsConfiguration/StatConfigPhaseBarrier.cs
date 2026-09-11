using System.Text.RegularExpressions;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

public static class StatConfigPhaseBarrierEntries
{
    public const string P9Run = "P9_RUN";
    public const string P9Projection = "P9_PROJECTION";
    public const string P9Result = "P9_RESULT";
    public const string P9Export = "P9_EXPORT";
    public const string P10Reconcile = "P10_RECONCILE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [P9Run, P9Projection, P9Result, P9Export, P10Reconcile],
        StringComparer.Ordinal);
}

public static class StatConfigPhaseBarrier
{
    public const int CurrentPhase = 8;

    private static readonly Regex CommandIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenRegex = new(
        "^[A-Z][A-Z0-9_]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Regex = new(
        "^[a-f0-9]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void ValidateProbeRequest(
        StatConfigPhaseBarrierRequest? request)
    {
        if (request is null)
            throw Schema("$", "BARRIER_REQUEST_REQUIRED");

        var ownerKind = request.OwnerKind?.Trim().ToUpperInvariant();
        if (ownerKind is null || !TokenRegex.IsMatch(ownerKind))
            throw Schema("$.ownerKind", "CANONICAL_OWNER_KIND_REQUIRED");

        var ownerId = request.OwnerId?.Trim().ToLowerInvariant();
        if (ownerId is null ||
            !ObjectId.TryParse(ownerId, out var parsed) ||
            !string.Equals(parsed.ToString(), ownerId, StringComparison.Ordinal))
        {
            throw Schema("$.ownerId", "CANONICAL_OBJECT_ID_REQUIRED");
        }

        var commandId = request.CommandId?.Trim();
        if (commandId is null || !CommandIdRegex.IsMatch(commandId))
            throw Schema("$.commandId", "CANONICAL_COMMAND_ID_REQUIRED");

        var expectedHash = request.ExpectedBundleHash?.Trim().ToLowerInvariant();
        if (expectedHash is null || !Sha256Regex.IsMatch(expectedHash))
            throw Schema("$.expectedBundleHash", "SHA256_REQUIRED");
    }

    public static void Reject(string entry, string? requestedOperation = null)
    {
        var barrier = Resolve(entry);
        if (CurrentPhase >= barrier.TargetPhase)
            return;

        throw AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                entry = barrier.Entry,
                targetPhase = $"P{barrier.TargetPhase}",
                reason = $"P{CurrentPhase}_CONFIG_ONLY",
                eligibility = "BLOCKED_UNTIL_TARGET_PHASE",
                freshness = "NOT_APPLICABLE",
                requestedOperation
            });
    }

    public static bool IsBlocked(string entry)
    {
        var barrier = Resolve(entry);
        return CurrentPhase < barrier.TargetPhase;
    }

    private static (string Entry, int TargetPhase) Resolve(string? entry)
    {
        var normalized = entry?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!StatConfigPhaseBarrierEntries.All.Contains(normalized))
            throw Schema("$.entry", "PHASE_BARRIER_ENTRY_INVALID");

        return (
            normalized,
            normalized == StatConfigPhaseBarrierEntries.P10Reconcile
                ? 10
                : 9);
    }

    private static AppException Schema(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });
}
