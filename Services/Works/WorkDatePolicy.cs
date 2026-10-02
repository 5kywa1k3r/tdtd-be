using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.Works;

internal static class WorkDatePolicy
{
    // Keep the two stored dates separate. Consumers agree on the effective deadline.
    internal static DateTime? EffectiveDueDate(Work work) => work.DueDate ?? work.EndDate;

    internal static DateTime? NormalizeDay(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc) : null;

    internal static void Validate(DateTime? startDate, DateTime? endDate, DateTime? dueDate)
    {
        if (!startDate.HasValue)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_START_DATE_REQUIRED);
        if (endDate.HasValue && endDate.Value.Date < startDate.Value.Date)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_END_BEFORE_START);
        if (dueDate.HasValue && dueDate.Value.Date < startDate.Value.Date)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_DUE_BEFORE_START);
    }
}
