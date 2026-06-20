using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public static class AdvancedSummaryReportSourceDayResolver
{
    public static string Resolve(WorkAssignmentReport report)
    {
        if (TryResolve(report, out var dayKey))
            return dayKey;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reportId = report.Id, reason = "ADVANCED_SUMMARY_SOURCE_DAY_UNRESOLVABLE" },
            "Cannot resolve source day for approved report.");
    }

    public static bool TryResolve(WorkAssignmentReport report, out string dayKey)
    {
        if (report.CompletedDate.HasValue)
        {
            dayKey = AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.CompletedDate.Value);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(report.PeriodKey))
        {
            try
            {
                dayKey = AdvancedSummaryHierarchyKeyHelper.ToDayKey(
                    AdvancedSummaryHierarchyKeyHelper.ParseDayKey(report.PeriodKey));
                return true;
            }
            catch (ArgumentException)
            {
            }
        }

        if (report.PeriodStart.HasValue)
        {
            dayKey = AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.PeriodStart.Value);
            return true;
        }

        if (report.ReportDate.HasValue)
        {
            dayKey = AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.ReportDate.Value);
            return true;
        }

        if (report.ApprovedAtUtc.HasValue)
        {
            dayKey = AdvancedSummaryHierarchyKeyHelper.ToDayKey(report.ApprovedAtUtc.Value);
            return true;
        }

        dayKey = string.Empty;
        return false;
    }
}
