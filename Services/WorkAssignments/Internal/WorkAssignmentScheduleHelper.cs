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
            DynamicExcelId = string.IsNullOrWhiteSpace(req.DynamicExcelId)
                ? null
                : req.DynamicExcelId.Trim(),
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

            Description = string.IsNullOrWhiteSpace(req.Description)
                ? null
                : req.Description.Trim(),

            IsActive = req.IsActive,
            AllowUserCreatedReports = req.AllowUserCreatedReports
        };
    }

    public static void ValidateRequest(SaveWorkAssignmentRequest req, Work work)
    {
        if (string.IsNullOrWhiteSpace(req.DynamicFormTemplateId) &&
            string.IsNullOrWhiteSpace(req.DynamicExcelId))
            throw new InvalidOperationException("Thiếu Dynamic Form template.");

        if (!WorkAssignmentTypes.All.Contains(req.AssignmentType))
            throw new InvalidOperationException("Loại giao không hợp lệ.");

        if (!WorkAggregationTypes.All.Contains(req.AggregationType))
            throw new InvalidOperationException("Kiểu tổng hợp không hợp lệ.");

        if (req.AssigneeUserIds is null || req.AssigneeUserIds.Count == 0)
            throw new InvalidOperationException("Phải chọn ít nhất 1 người được giao.");

        if (req.AssignmentType == WorkAssignmentTypes.Once)
        {
            if (!req.DueAtUtc.HasValue)
                throw new InvalidOperationException("Giao một lần bắt buộc phải có hạn nộp.");

            if (req.Schedule is not null)
                throw new InvalidOperationException("Giao một lần không được có cấu hình lịch định kỳ.");

            ValidateOnceDueAt(req.DueAtUtc.Value, work);
            return;
        }

        if (req.AssignmentType == WorkAssignmentTypes.PeriodicReport)
        {
            ValidatePeriodicSchedule(req.Schedule, work);
            return;
        }

        throw new InvalidOperationException("Loại giao không được hỗ trợ.");
    }

    private static void ValidateOnceDueAt(DateTime dueAtUtc, Work work)
    {
        var dueDate = dueAtUtc.Date;

        if (work.StartDate.HasValue && dueDate < work.StartDate.Value.Date)
            throw new InvalidOperationException("Hạn nộp của giao một lần không được nhỏ hơn ngày bắt đầu công việc.");

        if (work.EndDate.HasValue && dueDate > work.EndDate.Value.Date)
            throw new InvalidOperationException("Hạn nộp của giao một lần không được lớn hơn ngày kết thúc công việc.");
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
            throw new InvalidOperationException("Giao theo kỳ báo cáo bắt buộc có cấu hình lịch.");

        if (!work.StartDate.HasValue || !work.EndDate.HasValue)
            throw new InvalidOperationException("Công việc phải có từ ngày và đến ngày trước khi cấu hình giao định kỳ.");

        var workStart = work.StartDate.Value.Date;
        var workEnd = work.EndDate.Value.Date;

        if (workEnd < workStart)
            throw new InvalidOperationException("Khoảng thời gian của công việc không hợp lệ.");

        if (scheduleDto.StartDate.HasValue)
        {
            var scheduleStart = scheduleDto.StartDate.Value.Date;
            if (scheduleStart < workStart || scheduleStart > workEnd)
                throw new InvalidOperationException("Ngày bắt đầu áp dụng lịch phải nằm trong khoảng thời gian của công việc.");
        }

        var schedule = MapSchedule(scheduleDto, WorkAssignmentTypes.PeriodicReport);

        if (!ScheduleValidator.IsValid(schedule))
            throw new InvalidOperationException("Cấu hình lịch báo cáo không hợp lệ.");

        var occurrences = AssignmentScheduleOccurrenceHelper.GenerateOccurrences(
            schedule!,
            workStart,
            workEnd);

        if (occurrences.Count == 0)
            throw new InvalidOperationException("Cấu hình kỳ báo cáo không tạo ra mốc báo cáo hợp lệ nào trong khoảng thời gian của công việc.");
    }
}
