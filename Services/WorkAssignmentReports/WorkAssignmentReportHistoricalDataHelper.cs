using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.WorkAssignmentReports;

internal static class WorkAssignmentReportHistoricalDataHelper
{
    public static WorkReportPeriodStatus ResolveApprovedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        if (report.IsHistoricalData && report.HistoricalDataApproved)
            return WorkReportPeriodStatus.Approved;

        return WorkReportPeriodStatusHelper.ResolveApprovedStatus(
            period.Status,
            period.DueAtUtc,
            report.IsLateSubmission,
            now);
    }

    public static DateTime? NormalizeDate(DateTime? value)
        => value?.Date;

    public static bool IsHistoricalUserCreatedData(
        string? periodKind,
        DateTime? reportDate,
        DateTime? periodStart,
        DateTime? periodEnd,
        DateTime now)
    {
        if (!WorkReportPeriodKind.IsUserCreated(periodKind))
            return false;

        var anchor = periodEnd?.Date
                     ?? reportDate?.Date
                     ?? periodStart?.Date;

        return anchor.HasValue && anchor.Value < now.Date;
    }
}
