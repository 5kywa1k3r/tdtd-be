using tdtd_be.DTOs.Users;
using tdtd_be.Models;

namespace tdtd_be.DTOs.Works;

public sealed record WorkCreateRequest(
    string Name,
    string? Description,
    string? Note,
    string? LeaderDirectiveUserId,
    List<string>? LeaderWatchUserIds,
    string? EvaluationTemplateId,
    DateTime? StartDate,
    DateTime? EndDate,
    DateTime? DueDate,
    string? Code,
    WorkPriority? Priority,
    WorkType Type
);

public sealed record WorkUpdateRequest(
    string? Name,
    string? Description,
    string? Note,
    string? LeaderDirectiveUserId,
    List<string>? LeaderWatchUserIds,
    string? EvaluationTemplateId,
    DateTime? StartDate,
    DateTime? EndDate,
    DateTime? DueDate,
    string? Code,
    WorkPriority? Priority
);

public sealed record CompleteWorkRequest(
    DateTime? CompletedDate,
    string? Note
);

public sealed record WorkResponse(
    string Id,
    string AutoCode,
    string? Code,
    string Name,
    string? Description,
    string? Note,
    WorkStatus Status,
    string? CreatedByUserId,
    string? LeaderDirectiveUserId,
    List<string> LeaderWatchUserIds,
    string? EvaluationTemplateId,
    string? EvaluationTemplateCode,
    string? EvaluationTemplateLabel,
    bool HasManualEvaluations,
    int EvaluatedAssignmentCount,
    string? WorstEvaluationCode,
    string? WorstEvaluationLabel,
    DateTime? StartDate,
    DateTime? EndDate,
    DateTime? DueDate,
    DateTime? CompletedDate,
    DateTime? CompletedAtUtc,
    string? CompletedByUserId,
    WorkPriority Priority,
    WorkType Type,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    UserRefDTO? Owner,
    UserRefDTO? LeaderDirective,
    List<UserRefDTO> LeaderWatch
);

public sealed record WorkListRow(
    string Id,
    string AutoCode,
    string? Code,
    string Name,
    WorkStatus Status,
    WorkPriority Priority,
    WorkType Type,
    string? CreatedByUserId,
    string? OwnerName,
    string? LeaderDirectiveUserId,
    int LeaderWatchCount,
    string? EvaluationTemplateId,
    string? EvaluationTemplateCode,
    string? EvaluationTemplateLabel,
    bool HasManualEvaluations,
    int EvaluatedAssignmentCount,
    string? WorstEvaluationCode,
    string? WorstEvaluationLabel,
    DateTime? DueDate,
    DateTime? CompletedDate,
    DateTime? CompletedAtUtc,
    string? CompletedByUserId,
    DateTime CreatedAtUtc
);

public sealed record WorkSearchRequest(
    string? Q,
    WorkStatus? Status,
    WorkType Type,
    WorkPriority? Priority,
    string? LeaderDirectiveUserId,
    int Page,
    int PageSize,
    string? SortField,
    string? SortDirection
);
