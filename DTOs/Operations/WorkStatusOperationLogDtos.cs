namespace tdtd_be.DTOs.Operations;

public sealed class WorkStatusOperationLogSearchRequest
{
    public string? Operation { get; init; }
    public string? Scope { get; init; }
    public string? Result { get; init; }
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public string? ActorUserId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string? Query { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 50;
    public bool IncludeStackTrace { get; init; }
}

public sealed class WorkStatusOperationLogRow
{
    public string Id { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public string? ActorUserId { get; init; }
    public string? FromStatus { get; init; }
    public string? ToStatus { get; init; }
    public string? PeriodFromStatus { get; init; }
    public string? PeriodToStatus { get; init; }
    public string? AssignmentFromStatus { get; init; }
    public string? AssignmentToStatus { get; init; }
    public string? WorkFromStatus { get; init; }
    public string? WorkToStatus { get; init; }
    public string? Summary { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
