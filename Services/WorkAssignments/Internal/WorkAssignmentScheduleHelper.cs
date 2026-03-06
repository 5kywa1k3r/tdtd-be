using tdtd_be.Common.Time;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentScheduleHelper
{
    public static SaveWorkAssignmentRequest NormalizeRequest(SaveWorkAssignmentRequest req)
    {
        return new SaveWorkAssignmentRequest
        {
            ParentAssignmentId = string.IsNullOrWhiteSpace(req.ParentAssignmentId)
                ? null
                : req.ParentAssignmentId.Trim(),

            DynamicExcelId = req.DynamicExcelId?.Trim() ?? string.Empty,
            AssignmentType = req.AssignmentType?.Trim() ?? string.Empty,
            AggregationType = req.AggregationType?.Trim() ?? string.Empty,

            Schedule = NormalizeScheduleDto(req.Schedule),

            AssigneeUserIds = (req.AssigneeUserIds ?? new List<string>())
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

            IsActive = req.IsActive
        };
    }

    public static void ValidateRequest(SaveWorkAssignmentRequest req, Work work)
    {
        if (string.IsNullOrWhiteSpace(req.DynamicExcelId))
            throw new InvalidOperationException("Thiếu biểu mẫu/bảng động.");

        if (!WorkAssignmentTypes.All.Contains(req.AssignmentType))
            throw new InvalidOperationException("Loại giao không hợp lệ.");

        if (!WorkAggregationTypes.All.Contains(req.AggregationType))
            throw new InvalidOperationException("Kiểu tổng hợp không hợp lệ.");

        if (req.AssigneeUserIds is null || req.AssigneeUserIds.Count == 0)
            throw new InvalidOperationException("Phải chọn ít nhất 1 người được giao.");

        if (req.AssignmentType == WorkAssignmentTypes.Once)
            return;

        if (req.AssignmentType == WorkAssignmentTypes.PeriodicReport)
        {
            ValidatePeriodicSchedule(req.Schedule, work);
            return;
        }

        throw new InvalidOperationException("Loại giao không được hỗ trợ.");
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