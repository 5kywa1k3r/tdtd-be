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
        var aggregationType = string.IsNullOrWhiteSpace(req.AggregationType)
            ? WorkAggregationTypes.Matrix
            : req.AggregationType.Trim();

        var normalizedDueAtUtc = req.DueAtUtc;
        var normalizedStartDate = req.StartDate?.Date;
        var normalizedDueDate = (req.DueDate ?? req.CompletedDate)?.Date;

        var normalizedSchedule = assignmentType == WorkAssignmentTypes.Once
            ? null
            : NormalizeScheduleDto(req.Schedule);

        if (assignmentType == WorkAssignmentTypes.PeriodicReport &&
            normalizedSchedule is not null &&
            normalizedStartDate.HasValue &&
            !normalizedSchedule.StartDate.HasValue)
        {
            normalizedSchedule = new AssignmentScheduleDto(
                CycleType: normalizedSchedule.CycleType,
                StartDate: normalizedStartDate.Value,
                WeekDays: normalizedSchedule.WeekDays,
                MonthDays: normalizedSchedule.MonthDays,
                QuarterDays: normalizedSchedule.QuarterDays,
                SemiAnnualDays: normalizedSchedule.SemiAnnualDays,
                Note: normalizedSchedule.Note);
        }

        return new SaveWorkAssignmentRequest
        {
            ParentAssignmentId = string.IsNullOrWhiteSpace(req.ParentAssignmentId)
                ? null
                : req.ParentAssignmentId.Trim(),

            DynamicFormTemplateId = string.IsNullOrWhiteSpace(req.DynamicFormTemplateId)
                ? null
                : req.DynamicFormTemplateId.Trim(),
            AssignmentType = assignmentType,
            AggregationType = aggregationType,

            DueAtUtc = assignmentType == WorkAssignmentTypes.Once
                ? normalizedDueAtUtc
                : null,

            StartDate = normalizedStartDate,
            DueDate = normalizedDueDate,
            CompletedDate = null,

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

            AutoApproveConditionJson = string.IsNullOrWhiteSpace(req.AutoApproveConditionJson)
                ? null
                : req.AutoApproveConditionJson.Trim(),

            Description = string.IsNullOrWhiteSpace(req.Description)
                ? null
                : req.Description.Trim(),

            IsActive = req.IsActive
        };
    }

    public static SaveWorkAssignmentRequest ApplyEffectiveDateDefaults(
        SaveWorkAssignmentRequest req,
        Work work,
        WorkAssignment? parent,
        DateTime nowUtc,
        DateTime? inheritedParentDueDate = null)
    {
        var effectiveStartDate = WorkAssignmentDatePolicy.ResolveEffectiveStartDate(req.StartDate, nowUtc);
        var effectiveDueDate = req.DueDate?.Date
            ?? inheritedParentDueDate?.Date
            ?? WorkAssignmentDatePolicy.ResolveEffectiveDueDate(
                req.DueDate,
                work,
                parent);

        var effectiveSchedule = req.Schedule;
        if (req.AssignmentType == WorkAssignmentTypes.PeriodicReport &&
            effectiveSchedule is not null &&
            !effectiveSchedule.StartDate.HasValue)
        {
            effectiveSchedule = new AssignmentScheduleDto(
                CycleType: effectiveSchedule.CycleType,
                StartDate: effectiveStartDate,
                WeekDays: effectiveSchedule.WeekDays,
                MonthDays: effectiveSchedule.MonthDays,
                QuarterDays: effectiveSchedule.QuarterDays,
                SemiAnnualDays: effectiveSchedule.SemiAnnualDays,
                Note: effectiveSchedule.Note);
        }

        return new SaveWorkAssignmentRequest
        {
            ParentAssignmentId = req.ParentAssignmentId,
            DynamicFormTemplateId = req.DynamicFormTemplateId,
            AssignmentType = req.AssignmentType,
            AggregationType = req.AggregationType,
            DueAtUtc = req.DueAtUtc,
            StartDate = effectiveStartDate,
            DueDate = effectiveDueDate,
            CompletedDate = null,
            Schedule = effectiveSchedule,
            AssigneeUserIds = req.AssigneeUserIds?.ToList() ?? new List<string>(),
            AssigneeUnitIds = req.AssigneeUnitIds?.ToList() ?? new List<string>(),
            LeaderWatcherUserIds = req.LeaderWatcherUserIds?.ToList(),
            DynamicFormDataSourceRulesJson = req.DynamicFormDataSourceRulesJson,
            AutoApproveConditionJson = req.AutoApproveConditionJson,
            Description = req.Description,
            IsActive = req.IsActive
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

        ValidateAssignmentDates(req, work);

        if (req.AssignmentType == WorkAssignmentTypes.Once)
        {
            if (!req.DueAtUtc.HasValue)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_REQUIRED);

            if (req.Schedule is not null)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_ONCE_SCHEDULE_NOT_ALLOWED);

            ValidateOnceDueAt(req.DueAtUtc.Value, work, req.StartDate, req.DueDate);
            return;
        }

        if (req.AssignmentType == WorkAssignmentTypes.PeriodicReport)
        {
            ValidatePeriodicSchedule(req.Schedule, work, req.StartDate, req.DueDate);
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_TYPE_UNSUPPORTED,
            new { req.AssignmentType });
    }

    private static void ValidateAssignmentDates(SaveWorkAssignmentRequest req, Work work)
    {
        var startDate = req.StartDate?.Date;
        var dueDate = req.DueDate?.Date;

        if (startDate.HasValue)
            ValidateDateWithinWorkRange(
                startDate.Value,
                work,
                AppErrorCode.WORK_ASSIGNMENT_START_OUT_OF_RANGE,
                "startDate");

        if (dueDate.HasValue)
            ValidateDateWithinWorkRange(
                dueDate.Value,
                work,
                AppErrorCode.WORK_ASSIGNMENT_COMPLETED_OUT_OF_RANGE,
                "dueDate");

        if (startDate.HasValue && dueDate.HasValue && dueDate.Value < startDate.Value)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_COMPLETED_BEFORE_START,
                new { req.StartDate, req.DueDate });
    }

    private static void ValidateDateWithinWorkRange(
        DateTime date,
        Work work,
        AppErrorCode errorCode,
        string field)
    {
        if (work.StartDate.HasValue && date < work.StartDate.Value.Date)
            throw AppExceptionFactory.BadRequest(
                errorCode,
                new { field, value = date, work.StartDate });

        var workEnd = WorkAssignmentDatePolicy.ResolveWorkBoundaryEndDate(work);
        if (workEnd.HasValue && date > workEnd.Value.Date)
            throw AppExceptionFactory.BadRequest(
                errorCode,
                new { field, value = date, work.EndDate, work.DueDate });
    }

    private static void ValidateOnceDueAt(
        DateTime dueAtUtc,
        Work work,
        DateTime? startDate,
        DateTime? assignmentDueDate)
    {
        var dueDate = dueAtUtc.Date;

        if (work.StartDate.HasValue && dueDate < work.StartDate.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_BEFORE_WORK_START,
                new { dueAtUtc, work.StartDate });

        var workEnd = WorkAssignmentDatePolicy.ResolveWorkBoundaryEndDate(work);
        if (workEnd.HasValue && dueDate > workEnd.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_AFTER_WORK_END,
                new { dueAtUtc, work.EndDate, work.DueDate });

        if (startDate.HasValue && dueDate < startDate.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_BEFORE_ASSIGNMENT_START,
                new { dueAtUtc, startDate });

        if (assignmentDueDate.HasValue && dueDate > assignmentDueDate.Value.Date)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_AFTER_ASSIGNMENT_COMPLETED,
                new { dueAtUtc, assignmentDueDate });
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

    private static void ValidatePeriodicSchedule(
        AssignmentScheduleDto? scheduleDto,
        Work work,
        DateTime? startDate,
        DateTime? assignmentDueDate)
    {
        if (scheduleDto is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PERIODIC_SCHEDULE_REQUIRED);

        var workEnd = WorkAssignmentDatePolicy.ResolveWorkBoundaryEndDate(work);
        if (!workEnd.HasValue && !assignmentDueDate.HasValue)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_PERIODIC_WORK_DATE_RANGE_REQUIRED,
                new { work.Id, work.StartDate, work.EndDate, work.DueDate });

        var workStart = work.StartDate?.Date;
        var assignmentStart = startDate?.Date ?? scheduleDto.StartDate?.Date ?? workStart;
        var assignmentEnd = assignmentDueDate?.Date ?? workEnd?.Date;

        if (!assignmentStart.HasValue || !assignmentEnd.HasValue)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_PERIODIC_WORK_DATE_RANGE_REQUIRED,
                new { work.Id, work.StartDate, work.EndDate, work.DueDate });

        if (workStart.HasValue && workEnd.HasValue && workEnd.Value < workStart.Value)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_WORK_DATE_RANGE_INVALID,
                new { work.Id, work.StartDate, work.EndDate, work.DueDate });

        if ((workStart.HasValue && assignmentStart.Value < workStart.Value) ||
            (workEnd.HasValue && assignmentStart.Value > workEnd.Value))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_START_OUT_OF_RANGE,
                new { startDate = assignmentStart, work.StartDate, work.EndDate, work.DueDate });

        if ((workStart.HasValue && assignmentEnd.Value < workStart.Value) ||
            (workEnd.HasValue && assignmentEnd.Value > workEnd.Value))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_COMPLETED_OUT_OF_RANGE,
                new { dueDate = assignmentEnd, work.StartDate, work.EndDate, work.DueDate });

        if (assignmentEnd.Value < assignmentStart.Value)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_COMPLETED_BEFORE_START,
                new { startDate = assignmentStart, dueDate = assignmentEnd });

        if (scheduleDto.StartDate.HasValue)
        {
            var scheduleStart = scheduleDto.StartDate.Value.Date;
            if (scheduleStart < assignmentStart.Value || scheduleStart > assignmentEnd.Value)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_PERIODIC_START_OUT_OF_RANGE,
                    new
                    {
                        scheduleStartDate = scheduleDto.StartDate,
                        assignmentStartDate = assignmentStart,
                        assignmentDueDate = assignmentEnd
                    });
        }

        var schedule = MapSchedule(scheduleDto, WorkAssignmentTypes.PeriodicReport);

        if (!ScheduleValidator.IsValid(schedule))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PERIODIC_SCHEDULE_INVALID);

        var occurrences = AssignmentScheduleOccurrenceHelper.GenerateOccurrences(
            schedule!,
            assignmentStart.Value,
            assignmentEnd.Value);

        if (occurrences.Count == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_PERIODIC_NO_OCCURRENCES);
    }
}
