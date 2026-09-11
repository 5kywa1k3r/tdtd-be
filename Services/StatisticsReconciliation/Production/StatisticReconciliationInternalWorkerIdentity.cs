using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.Services.StatisticsReconciliation.Production;

internal sealed class StatisticReconciliationProductionWorkerOptions
{
    internal const string SectionName =
        "StatisticReconciliation:ProductionWorker";

    public string? ActorId { get; set; }
    public string? WorkerId { get; set; }
    public int HeartbeatSeconds { get; set; } = 10;
}

internal interface IStatisticReconciliationInternalWorkerIdentity
{
    string WorkerId { get; }
    MeResponse Actor { get; }
}

/// <summary>
/// Creates an in-process, capability-only worker identity. It is not a user,
/// JWT, bearer token or administrator credential and is never accepted by an
/// HTTP controller as an administrator.
/// </summary>
internal sealed class StatisticReconciliationInternalWorkerIdentity
    : IStatisticReconciliationInternalWorkerIdentity
{
    internal const string AccountKind =
        "INTERNAL_STATISTIC_RECONCILIATION_WORKER";
    internal const string CapabilityRole =
        "STATISTIC_RECONCILIATION_PRODUCTION_WORKER";
    private const string DefaultActorIdentityDomain =
        "TDTD_P10_PRODUCTION_RECONCILIATION_WORKER_V1";

    public StatisticReconciliationInternalWorkerIdentity(
        IOptions<StatisticReconciliationProductionWorkerOptions> configured)
    {
        var options = configured?.Value ??
            throw new InvalidOperationException(
                "Statistic reconciliation production worker options are missing.");
        var actorId = string.IsNullOrWhiteSpace(options.ActorId)
            ? DefaultActorId()
            : options.ActorId.Trim();
        if (!ObjectId.TryParse(actorId, out var parsedActorId))
            throw new InvalidOperationException(
                "Statistic reconciliation worker actor id must be an ObjectId.");

        var workerId = string.IsNullOrWhiteSpace(options.WorkerId)
            ? $"p10-reconciliation:{Environment.MachineName}:{Environment.ProcessId}"
            : options.WorkerId.Trim();
        if (workerId.Length is < 1 or > 128)
            throw new InvalidOperationException(
                "Statistic reconciliation worker id length is invalid.");

        WorkerId = workerId;
        Actor = new MeResponse(
            parsedActorId.ToString(),
            "internal-p10-reconciliation-worker",
            "Internal P10 reconciliation worker",
            [],
            string.Empty,
            null,
            null,
            null,
            [CapabilityRole],
            null,
            false,
            AccountKind);
    }

    public string WorkerId { get; }
    public MeResponse Actor { get; }

    private static string DefaultActorId()
        => Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(DefaultActorIdentityDomain)))
            .ToLowerInvariant()[..24];
}

/// <summary>
/// P10-only authorization seam. HTTP controllers retain their explicit
/// RequireSystemAdmin checks, while production orchestration can use a much
/// narrower identity that no other business service recognizes.
/// </summary>
internal static class StatisticReconciliationInternalWorkerAccess
{
    internal static void RequireWorkerOrSystemAdmin(MeResponse actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (RoleGuard.IsSystemAdmin(actor) || IsWorker(actor))
            return;
        throw AppExceptionFactory.Forbidden(
            AppErrorCode.AUTH_SYSTEM_ADMIN_REQUIRED);
    }

    internal static bool IsWorker(MeResponse actor)
        => !actor.IsDeleted &&
           ObjectId.TryParse(actor.Id, out _) &&
           string.IsNullOrEmpty(actor.UnitId) &&
           string.Equals(
               actor.AccountKind,
               StatisticReconciliationInternalWorkerIdentity.AccountKind,
               StringComparison.Ordinal) &&
           actor.Roles is { Count: 1 } &&
           string.Equals(
               actor.Roles[0],
               StatisticReconciliationInternalWorkerIdentity.CapabilityRole,
               StringComparison.Ordinal);
}
