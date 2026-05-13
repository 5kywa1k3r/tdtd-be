namespace tdtd_be.DTOs.Operations;

public sealed class JobRunSearchRequest
{
    public string? Status { get; init; }
    public string? Action { get; init; }
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? DynamicFormTemplateId { get; init; }
    public string? UserId { get; init; }
    public string? Query { get; init; }
    public bool IncludeInactive { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed class MaterializeJobRow
{
    public string Id { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string WorkAssignmentId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastHeartbeatAtUtc { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? LastError { get; init; }
    public int CursorAssigneeIndex { get; init; }
    public int CursorDueIndex { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class ProjectionRetryJobRow
{
    public string Id { get; init; } = string.Empty;
    public string DedupeKey { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? AssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? DynamicExcelId { get; init; }
    public string? UserId { get; init; }
    public string? DocType { get; init; }
    public string? DocId { get; init; }
    public string ByUserId { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? LastErrorType { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorAtUtc { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatisticRebuildJobRow
{
    public string Id { get; init; } = string.Empty;
    public string DedupeKey { get; init; } = string.Empty;
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public string? DynamicFormTemplateCode { get; init; }
    public string? DynamicFormTemplateName { get; init; }
    public string Status { get; init; } = string.Empty;
    public string RequestedByUserId { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public long TotalReportCount { get; init; }
    public long ProcessedReportCount { get; init; }
    public long FailedReportCount { get; init; }
    public string? LastReportId { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? LastErrorType { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorAtUtc { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}
