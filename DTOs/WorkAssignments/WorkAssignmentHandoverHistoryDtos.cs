using tdtd_be.DTOs.Users;

namespace tdtd_be.DTOs.WorkAssignments;

public sealed record WorkAssignmentHandoverHistorySearchRequest(
    string? WorkAssignmentId = null,
    int Page = 0,
    int PageSize = 20
);

public sealed record WorkAssignmentHandoverHistoryRow(
    string Id,
    string WorkId,
    string WorkAssignmentId,
    string AssignmentCode,
    string? DynamicFormTemplateId,
    string? DynamicFormTemplateCode,
    string? DynamicFormTemplateName,
    UserRefDTO? FromAssignee,
    UserRefDTO? ToAssignee,
    UserRefDTO? Actor,
    string? Reason,
    string? Comment,
    string? WorkTemplateAssigneeId,
    long PeriodCount,
    long ReportCount,
    long QueueItemCount,
    string Result,
    DateTime CreatedAtUtc
);
