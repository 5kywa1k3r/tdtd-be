using tdtd_be.DTOs.Users;

namespace tdtd_be.DTOs.DynamicForms;

public sealed record CreateDynamicFormCloneRequestReq(
    string? Reason
);

public sealed record ReviewDynamicFormCloneRequestReq(
    string? Comment
);

public sealed record DynamicFormCloneRequestSearchReq(
    string? Status = null,
    int Page = 0,
    int PageSize = 20
);

public sealed record DynamicFormCloneRequestRow(
    string Id,
    string WorkId,
    string WorkAssignmentId,
    string AssignmentCode,
    string DynamicFormTemplateId,
    string? DynamicFormTemplateCode,
    string? DynamicFormTemplateName,
    UserRefDTO? Requester,
    UserRefDTO? AssignmentOwner,
    string Status,
    string? RequestReason,
    string? ReviewComment,
    DateTime? ReviewedAtUtc,
    string? ReviewedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);
