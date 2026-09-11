using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigValidationEnqueuePayload(
    string? ConfigId,
    string? VersionId,
    int? VersionNo);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigValidationResetPayload(string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigValidationCancelPayload(string? Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigValidationCleanupPayload(
    DateTime? CompletedBeforeUtc,
    int Limit = 100,
    bool DryRun = true);

public sealed class StatConfigValidationJobSearchRequest
{
    public string? Status { get; init; }
    public string? OwnerKind { get; init; }
    public string? OwnerId { get; init; }
    public string? ConfigId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Query { get; init; }
    public bool IncludeInactive { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed record StatConfigValidationJobStatusResponse(
    string JobId,
    string CorrelationId,
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long ConfigRevision,
    string ConfigHash,
    string BundleHash,
    string Status,
    int RetryCount,
    int MaxRetryCount,
    string? SafeCode,
    string? SafeMessage,
    DateTime? NextRetryAtUtc,
    DateTime? LastRunAtUtc,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record StatConfigValidationJobDiagnosticsResponse(
    StatConfigValidationJobStatusResponse SafeStatus,
    long StateRevision,
    string StateHash,
    string DependencyPinsHash,
    string RequestedByUserId,
    bool LeaseActive,
    DateTime? LeaseUntilUtc,
    DateTime? LastHeartbeatAtUtc,
    DateTime? FailedAtUtc,
    DateTime? ResetAtUtc,
    string? ResetByUserId,
    int ResetCount,
    string? DiagnosticCode,
    string? DiagnosticMessage,
    string? FailureFingerprint,
    DateTime? ExpiresAtUtc);

public sealed record StatConfigValidationProcessResponse(
    int Claimed,
    int Completed,
    int Retrying,
    int Failed,
    IReadOnlyList<StatConfigValidationJobStatusResponse> Jobs);

public sealed record StatConfigValidationCleanupResponse(
    bool Ok,
    bool DryRun,
    string CommandId,
    int Limit,
    long MatchedCount,
    int SelectedCount,
    long DeletedCount,
    bool HasMore,
    IReadOnlyList<string> JobIds);

public sealed record StatConfigIndexDefinitionResponse(
    string Collection,
    string Name,
    string KeyJson,
    bool Unique,
    string? PartialFilterJson,
    int? ExpireAfterSeconds,
    bool IsExact);

public sealed record StatConfigIndexReadinessResponse(
    bool Ready,
    string ContractHash,
    DateTime CheckedAtUtc,
    IReadOnlyList<StatConfigIndexDefinitionResponse> Indexes);

public static class StatConfigValidationExternalStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Retrying = "RETRYING";
    public const string Done = "DONE";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Reset = "RESET";
}
