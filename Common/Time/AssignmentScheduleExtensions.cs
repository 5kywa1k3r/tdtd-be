using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Common.Time;

public static class AssignmentScheduleOccurrenceHelper
{
    public static List<DateTime> GenerateOccurrences(
        AssignmentSchedule schedule,
        DateTime workStart,
        DateTime workEnd)
    {
        if (!ScheduleValidator.IsValid(schedule))
            return new List<DateTime>();

        var start = schedule.StartDate?.Date ?? workStart.Date;
        if (start < workStart.Date) start = workStart.Date;

        var end = workEnd.Date;
        if (end < start) return new List<DateTime>();

        var type = schedule.CycleType!.Trim().ToUpperInvariant();

        var result = type switch
        {
            ReportCycleTypes.Daily => GenerateDailyOccurrences(start, end),
            ReportCycleTypes.Weekly => GenerateWeeklyOccurrences(schedule, start, end),
            ReportCycleTypes.Monthly => GenerateMonthlyOccurrences(schedule, start, end),
            ReportCycleTypes.Quarterly => GenerateQuarterlyOccurrences(schedule, start, end),
            ReportCycleTypes.SemiAnnual => GenerateSemiAnnualOccurrences(schedule, start, end),
            _ => new List<DateTime>()
        };

        return result
            .Where(x => x.Date >= workStart.Date && x.Date <= workEnd.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }

    private static List<DateTime> GenerateDailyOccurrences(DateTime start, DateTime end)
    {
        var result = new List<DateTime>();
        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            result.Add(d);
        return result;
    }

    private static List<DateTime> GenerateWeeklyOccurrences(
        AssignmentSchedule schedule,
        DateTime start,
        DateTime end)
    {
        var allowed = new HashSet<int>(schedule.WeekDays ?? new List<int>());
        var result = new List<DateTime>();

        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
        {
            if (allowed.Contains(ScheduleWeekHelper.GetBusinessWeekday(d)))
                result.Add(d);
        }

        return result;
    }

    private static List<DateTime> GenerateMonthlyOccurrences(
        AssignmentSchedule schedule,
        DateTime start,
        DateTime end)
    {
        var result = new List<DateTime>();
        var monthDays = (schedule.MonthDays ?? new List<int>())
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var cursor = new DateTime(start.Year, start.Month, 1);
        var limit = new DateTime(end.Year, end.Month, 1);

        while (cursor <= limit)
        {
            var daysInMonth = DateTime.DaysInMonth(cursor.Year, cursor.Month);

            foreach (var day in monthDays)
            {
                if (day > daysInMonth) continue;
                result.Add(new DateTime(cursor.Year, cursor.Month, day));
            }

            cursor = cursor.AddMonths(1);
        }

        return result;
    }

    private static List<DateTime> GenerateQuarterlyOccurrences(
        AssignmentSchedule schedule,
        DateTime start,
        DateTime end)
    {
        var result = new List<DateTime>();
        var quarterDays = (schedule.QuarterDays ?? Array.Empty<int>())
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        for (var year = start.Year; year <= end.Year; year++)
        {
            for (var quarter = 1; quarter <= 4; quarter++)
            {
                var quarterStart = SchedulePeriodHelper.GetQuarterStartDate(year, quarter);
                var quarterEnd = quarterStart.AddMonths(3).AddDays(-1);

                foreach (var day in quarterDays)
                {
                    var candidate = quarterStart.AddDays(day - 1);
                    if (candidate <= quarterEnd)
                        result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static List<DateTime> GenerateSemiAnnualOccurrences(
        AssignmentSchedule schedule,
        DateTime start,
        DateTime end)
    {
        var result = new List<DateTime>();
        var halfDays = (schedule.SemiAnnualDays ?? Array.Empty<int>())
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        for (var year = start.Year; year <= end.Year; year++)
        {
            for (var half = 1; half <= 2; half++)
            {
                var halfStart = SchedulePeriodHelper.GetHalfStartDate(year, half);
                var halfEnd = halfStart.AddMonths(6).AddDays(-1);

                foreach (var day in halfDays)
                {
                    var candidate = halfStart.AddDays(day - 1);
                    if (candidate <= halfEnd)
                        result.Add(candidate);
                }
            }
        }

        return result;
    }
}