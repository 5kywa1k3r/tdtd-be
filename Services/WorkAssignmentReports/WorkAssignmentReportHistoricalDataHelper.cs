using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.WorkAssignmentReports;

internal static class WorkAssignmentReportHistoricalDataHelper
{
    public static bool ResolveIsLateSubmission(
        bool isHistoricalData,
        DateTime? completedDate,
        DateTime? dueAtUtc,
        DateTime now)
    {
        return isHistoricalData
            ? IsHistoricalCompletedAfterDue(completedDate, dueAtUtc)
            : dueAtUtc.HasValue && now > dueAtUtc.Value;
    }

    public static WorkReportPeriodStatus ResolveDraftPeriodStatus(
        bool isHistoricalData,
        DateTime? completedDate,
        DateTime? dueAtUtc,
        DateTime now)
    {
        if (isHistoricalData)
            return IsHistoricalCompletedAfterDue(completedDate, dueAtUtc)
                ? WorkReportPeriodStatus.OverdueDraft
                : WorkReportPeriodStatus.Draft;

        return WorkReportPeriodStatusHelper.ResolveDraftStatus(dueAtUtc, now);
    }

    public static WorkReportPeriodStatus ResolveSubmittedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return ResolveSubmittedPeriodStatus(
            report.IsHistoricalData || period.IsHistoricalData,
            report.CompletedDate ?? period.CompletedDate,
            report.DueAtUtc ?? period.DueAtUtc,
            now);
    }

    public static WorkReportPeriodStatus ResolveSubmittedPeriodStatus(
        bool isHistoricalData,
        DateTime? completedDate,
        DateTime? dueAtUtc,
        DateTime now)
    {
        if (isHistoricalData)
            return IsHistoricalCompletedAfterDue(completedDate, dueAtUtc)
                ? WorkReportPeriodStatus.OverdueSubmitted
                : WorkReportPeriodStatus.Submitted;

        return WorkReportPeriodStatusHelper.ResolveSubmittedStatus(dueAtUtc, now);
    }

    public static WorkReportPeriodStatus ResolveApprovedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        if (report.IsHistoricalData && report.HistoricalDataApproved)
            return IsHistoricalCompletedAfterDue(
                    report.CompletedDate ?? period.CompletedDate,
                    report.DueAtUtc ?? period.DueAtUtc)
                ? WorkReportPeriodStatus.OverdueApproved
                : WorkReportPeriodStatus.Approved;

        return WorkReportPeriodStatusHelper.ResolveApprovedStatus(
            period.Status,
            period.DueAtUtc,
            report.IsLateSubmission,
            now);
    }

    public static DateTime? NormalizeDate(DateTime? value)
        => value?.Date;

    private static bool IsHistoricalCompletedAfterDue(DateTime? completedDate, DateTime? dueAtUtc)
        => completedDate.HasValue &&
           dueAtUtc.HasValue &&
           completedDate.Value.Date > dueAtUtc.Value.Date;
}
