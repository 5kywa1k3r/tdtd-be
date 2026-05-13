using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentScheduleHelper
{
    public static SaveWorkAssignmentRequest NormalizeRequest(SaveWorkAssignmentRequest req)
    {
        var assignmentType = req.AssignmentType?.Trim() ?? string.Empty;

        var normalizedDueAtUtc = req.DueAtUtc;

        var normalizedSchedule = assignmentType == WorkAssignmentTypes.Once
            ? null
            : NormalizeScheduleDto(req.Schedule);

        return new SaveWorkAssignmentRequest
        {
            ParentAssignmentId = string.IsNullOrWhiteSpace(req.ParentAssignmentId)
                ? null
                : req.ParentAssignmentId.Trim(),

            DynamicFormTemplateId = string.IsNullOrWhiteSpace(req.DynamicFormTemplateId)
                ? null
                : req.DynamicFormTemplateId.Trim(),
            AssignmentType = assignmentType,
            AggregationType = req.AggregationType?.Trim() ?? string.Empty,

            DueAtUtc = assignmentType == WorkAssignmentTypes.Once
                ? normalizedDueAtUtc
                : null,

            Schedule = normalizedSchedule,

            AssigneeUserIds = (req.AssigneeUserIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList(),

            AssigneeUnitIds = (req.AssigneeUnitIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList(),

            LeaderWatcherUserIds = (req.LeaderWatcherUserIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList(),

            DynamicFormDataSourceRulesJson = string.IsNullOrWhiteSpace(req.DynamicFormDataSourceRulesJson)
                ? null
                : req.DynamicFormDataSourceRulesJson.Trim(),

            Description = string.IsNullOrWhiteSpace(req.Description)
                ? null
                : req.Description.Trim(),

            IsActive = req.IsActive,
            AllowUserCreatedReports = req.AllowUserCreatedReports
        };
    }

    public static void ValidateRequest(SaveWorkAssignmentRequest req, Work work)
    {
        if (string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.DYNAMIC_FORM_TEMPLATE_REQUIRED);

        if (!WorkAssignmentTypes.All.Contains(req.AssignmentType))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_TYPE_INVALID,
                new { req.AssignmentType });

        if (!WorkAggregationTypes.All.Contains(req.AggregationType))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_AGGREGATION_TYPE_INVALID,
                new { req.AggregationType });

        if (req.AssigneeUserIds is null || req.AssigneeUserIds.Count == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_REQUIRED);

        if (req.AssignmentType == WorkAssignmentTypes.Once)
        {
            if (!req.DueAtUtc.HasValue)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_REQUIRED);

            if (req.Schedule is not null)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ONCE_SCHEDULE_NOT_ALLOWED);

            ValidateOnceDueAt(req.DueAtUtc.Value, work);
            return;
        }

        if (req.AssignmentType == WorkAssignmentTypes.PeriodicReport)
        {
            ValidatePeriodicSchedule(req.Schedule, work);
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_TYPE_UNSUPPORTED,
            new { req.AssignmentType });
    }

    private static void ValidateOnceDueAt(DateTime dueAtUtc, Work work)
    {
        var dueDate = dueAtUtc.Date;

        if (work.StartDate.HasValue && dueDate < work.StartDate.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_BEFORE_WORK_START,
                new { dueAtUtc, work.StartDate });

        if (work.EndDate.HasValue && dueDate > work.EndDate.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_AFTER_WORK_END,
                new { dueAtUtc, work.EndDate });
    }

    public static AssignmentSchedule? MapSchedule(
        AssignmentScheduleDto? dto,
        string assignmentType)
    {
        if (assignmentType == WorkAssignmentTypes.Once)
            return null;

        if (dto is null)
            return null;

        return new AssignmentSchedule
        {
            CycleType = dto.CycleType,
            StartDate = dto.StartDate?.Date,
            WeekDays = dto.WeekDays ?? new List<int>(),
            MonthDays = dto.MonthDays ?? new List<int>(),
            QuarterDays = (dto.QuarterDays ?? new List<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray(),
            SemiAnnualDays = (dto.SemiAnnualDays ?? new List<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray(),
            Note = dto.Note
        };
    }

    public static AssignmentScheduleDto? ToScheduleDto(AssignmentSchedule? model)
    {
        if (model is null) return null;

        return new AssignmentScheduleDto(
            CycleType: model.CycleType,
            StartDate: model.StartDate,
            WeekDays: model.WeekDays ?? new List<int>(),
            MonthDays: model.MonthDays ?? new List<int>(),
            QuarterDays: (model.QuarterDays ?? Array.Empty<int>()).ToList(),
            SemiAnnualDays: (model.SemiAnnualDays ?? Array.Empty<int>()).ToList(),
            Note: model.Note
        );
    }

    private static AssignmentScheduleDto? NormalizeScheduleDto(AssignmentScheduleDto? dto)
    {
        if (dto is null) return null;

        var cycleType = string.IsNullOrWhiteSpace(dto.CycleType)
            ? null
            : dto.CycleType.Trim().ToUpperInvariant();

        return new AssignmentScheduleDto(
            CycleType: cycleType,
            StartDate: dto.StartDate?.Date,
            WeekDays: NormalizeIntList(dto.WeekDays),
            MonthDays: NormalizeIntList(dto.MonthDays),
            QuarterDays: NormalizeIntList(dto.QuarterDays),
            SemiAnnualDays: NormalizeIntList(dto.SemiAnnualDays),
            Note: string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim()
        );
    }

    private static List<int>? NormalizeIntList(List<int>? values)
    {
        if (values is null) return null;

        return values
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private static void ValidatePeriodicSchedule(AssignmentScheduleDto? scheduleDto, Work work)
    {
        if (scheduleDto is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PERIODIC_SCHEDULE_REQUIRED);

        if (!work.StartDate.HasValue || !work.EndDate.HasValue)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_PERIODIC_WORK_DATE_RANGE_REQUIRED,
                new { work.Id, work.StartDate, work.EndDate });

        var workStart = work.StartDate.Value.Date;
        var workEnd = work.EndDate.Value.Date;

        if (workEnd < workStart)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_WORK_DATE_RANGE_INVALID,
                new { work.Id, work.StartDate, work.EndDate });

        if (scheduleDto.StartDate.HasValue)
        {
            var scheduleStart = scheduleDto.StartDate.Value.Date;
            if (scheduleStart < workStart || scheduleStart > workEnd)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_PERIODIC_START_OUT_OF_RANGE,
                    new
                    {
                        scheduleStartDate = scheduleDto.StartDate,
                        workStartDate = work.StartDate,
                        workEndDate = work.EndDate
                    });
        }

        var schedule = MapSchedule(scheduleDto, WorkAssignmentTypes.PeriodicReport);

        if (!ScheduleValidator.IsValid(schedule))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PERIODIC_SCHEDULE_INVALID);

        var occurrences = AssignmentScheduleOccurrenceHelper.GenerateOccurrences(
            schedule!,
            workStart,
            workEnd);

        if (occurrences.Count == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PERIODIC_NO_OCCURRENCES);
    }
}
