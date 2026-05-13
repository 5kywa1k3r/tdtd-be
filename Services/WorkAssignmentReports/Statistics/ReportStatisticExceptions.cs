using tdtd_be.Common.Errors;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal static class ReportStatisticExceptions
{
    public static AppException WorkIdRequired(string statisticKind, string? workId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTICS_WORK_ID_REQUIRED,
            new { statisticKind, workId });

    public static AppException ScopeIdRequired(
        string statisticKind,
        string? workId,
        string? scopeType,
        string? scopeId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTICS_SCOPE_ID_REQUIRED,
            new { statisticKind, workId, scopeType, scopeId });

    public static AppException ScopeTypeInvalid(
        string statisticKind,
        string? workId,
        string? scopeType)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTICS_SCOPE_TYPE_INVALID,
            new { statisticKind, workId, scopeType });

    public static AppException AssignmentNotFound(
        string statisticKind,
        string? workId,
        string? scopeType,
        string? scopeId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_REPORT_STATISTICS_ASSIGNMENT_NOT_FOUND,
            new { statisticKind, workId, scopeType, scopeId });

    public static AppException ReadForbidden(
        string statisticKind,
        string? workId,
        string? scopeType,
        string? scopeId,
        string? actorUserId)
        => AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_REPORT_STATISTICS_READ_FORBIDDEN,
            new { statisticKind, workId, scopeType, scopeId, actorUserId });

    public static AppException RebuildForbidden(
        string statisticKind,
        string? workId,
        string? actorUserId)
        => AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_REPORT_STATISTICS_REBUILD_FORBIDDEN,
            new { statisticKind, workId, actorUserId });

    public static AppException DynamicFormTemplateIdRequired(
        string statisticKind,
        string? workId,
        string? dynamicFormTemplateId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTICS_DYNAMIC_FORM_TEMPLATE_ID_REQUIRED,
            new { statisticKind, workId, dynamicFormTemplateId });

    public static AppException FieldSelectorRequired(
        string statisticKind,
        string? workId,
        string? dynamicFormTemplateId,
        string? fieldId,
        string? fieldKey)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_REPORT_STATISTICS_FIELD_SELECTOR_REQUIRED,
            new { statisticKind, workId, dynamicFormTemplateId, fieldId, fieldKey });

    public static AppException DynamicFormTemplateNotFound(
        string statisticKind,
        string? workId,
        string? dynamicFormTemplateId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_REPORT_STATISTICS_DYNAMIC_FORM_TEMPLATE_NOT_FOUND,
            new { statisticKind, workId, dynamicFormTemplateId });

    public static AppException TextFieldNotFound(
        string statisticKind,
        string? workId,
        string? dynamicFormTemplateId,
        string? fieldId,
        string? fieldKey)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_REPORT_STATISTICS_TEXT_FIELD_NOT_FOUND,
            new { statisticKind, workId, dynamicFormTemplateId, fieldId, fieldKey });
}
