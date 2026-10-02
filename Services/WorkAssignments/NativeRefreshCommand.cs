using tdtd_be.Common.Errors;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignments;

// Caller-owned identity survives a lost HTTP response. It is scoped to actor and
// summary kind, never to mutable source data. Inputs are compared before replay.
internal static class NativeRefreshCommand
{
    internal static bool IsValid(string? commandId) => commandId is { Length: > 0 and <= 128 }
        && commandId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    internal static string Id(string kind, string actorId, string commandId)
    {
        if (!IsValid(commandId)) throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason = kind + "_NATIVE_REFRESH_COMMAND_INVALID" });
        return StatConfigCanonicalJson.HashObject(new { kind, actorId, commandId })[..24];
    }

    internal static bool Matches(string kind, string actorId, string refreshId, string? commandId, string? commandHash)
        => commandId is null ? commandHash is null
            : IsValid(commandId) && StatRunCanonicalJson.IsCanonicalSha256(commandHash)
              && refreshId == Id(kind, actorId, commandId);
}
