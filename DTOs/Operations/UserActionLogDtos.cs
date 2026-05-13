using tdtd_be.DTOs.Common;

namespace tdtd_be.DTOs.Operations;

public sealed class UserActionLogSearchRequest
{
    public string? Action { get; init; }
    public string? Scope { get; init; }
    public string? Result { get; init; }
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public string? ActorUserId { get; init; }
    public string? UserId { get; init; }
    public string? UnitId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string? Query { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed class UserActionLogRow
{
    public string Id { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; }
    public UserActionLogUserDto? Actor { get; init; }
    public UserActionLogUserDto? TargetUser { get; init; }
    public UserActionLogUserDto? FromUser { get; init; }
    public UserActionLogUserDto? ToUser { get; init; }
    public List<UserActionLogUserDto> Users { get; init; } = new();
    public List<UserActionLogUnitDto> UnitScopes { get; init; } = new();
    public string? WorkId { get; init; }
    public string? WorkAutoCode { get; init; }
    public string? WorkCode { get; init; }
    public string? WorkName { get; init; }
    public string? WorkType { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkAssignmentCode { get; init; }
    public string? DynamicFormTemplateId { get; init; }
    public string? DynamicFormTemplateCode { get; init; }
    public string? DynamicFormTemplateName { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? PeriodKey { get; init; }
    public string? PeriodInstanceKey { get; init; }
    public string? PeriodStatus { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public string? ReportStatus { get; init; }
    public string? Summary { get; init; }
    public Dictionary<string, string>? Data { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class UserActionLogUserDto
{
    public string UserId { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? FullName { get; init; }
    public string? UnitId { get; init; }
    public string? UnitCode { get; init; }
    public string? UnitName { get; init; }
    public int? UnitLevel { get; init; }
}

public sealed class UserActionLogUnitDto
{
    public string UnitId { get; init; } = string.Empty;
    public string? UnitCode { get; init; }
    public string? UnitName { get; init; }
    public int UnitLevel { get; init; }
}

public sealed class UserActionLogSeed
{
    public string Action { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string? ActorUserId { get; init; }
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public string? TargetUserId { get; init; }
    public List<string> TargetUserIds { get; init; } = new();
    public string? FromUserId { get; init; }
    public string? ToUserId { get; init; }
    public string? Summary { get; init; }
    public Dictionary<string, string>? Data { get; init; }
    public DateTime? OccurredAtUtc { get; init; }
}

public sealed class UserActionLogRetryJobRow
{
    public string Id { get; init; } = string.Empty;
    public string DedupeKey { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
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
