using System.Globalization;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignmentReports;

internal static class WorkAssignmentHistoricalMutationPolicy
{
    public const int RecentWindowMonths = 1;
    public const int ManagementWindowMonths = 3;

    public static HistoricalMutationDecision EvaluateApprovedMutation(
        WorkAssignmentReport report,
        WorkReportPeriod? period,
        MeResponse actor,
        DateTime now)
    {
        var sourceDate = ResolveMutationSourceDate(report, period);
        var today = now.Date;
        var recentCutoff = today.AddMonths(-RecentWindowMonths);
        var managementCutoff = today.AddMonths(-ManagementWindowMonths);

        if (!sourceDate.HasValue)
        {
            return new HistoricalMutationDecision(
                false,
                "SOURCE_DATE_REQUIRED",
                sourceDate,
                recentCutoff,
                managementCutoff);
        }

        if (sourceDate.Value.Date >= recentCutoff)
        {
            return new HistoricalMutationDecision(
                true,
                "RECENT_WINDOW",
                sourceDate,
                recentCutoff,
                managementCutoff);
        }

        if (sourceDate.Value.Date >= managementCutoff)
        {
            return new HistoricalMutationDecision(
                IsManagementOrAdmin(actor),
                "MANAGEMENT_OR_ADMIN",
                sourceDate,
                recentCutoff,
                managementCutoff);
        }

        return new HistoricalMutationDecision(
            IsAdmin(actor),
            "ADMIN_OR_SYSTEM_ADMIN",
            sourceDate,
            recentCutoff,
            managementCutoff);
    }

    public static void EnsureApprovedMutationAllowed(
        WorkAssignmentReport report,
        WorkReportPeriod? period,
        MeResponse actor,
        string operation,
        DateTime now)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved)
            return;

        var decision = EvaluateApprovedMutation(report, period, actor, now);
        if (decision.IsAllowed)
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_MUTATION_WINDOW_FORBIDDEN,
            new
            {
                operation,
                reportId = report.Id,
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.PeriodKey,
                sourceDate = decision.SourceDate,
                decision.RecentCutoffDate,
                decision.ManagementCutoffDate,
                required = decision.Required,
                actorUserId = actor.Id,
                actor.Username,
                actor.AccountKind,
                actor.Roles
            });
    }

    internal static DateTime? ResolveMutationSourceDate(
        WorkAssignmentReport report,
        WorkReportPeriod? period)
    {
        var completedDate = NormalizeDate(report.CompletedDate ?? period?.CompletedDate);
        if (completedDate.HasValue)
            return completedDate;

        if (TryParseDayKey(report.PeriodKey, out var periodKeyDate))
            return periodKeyDate;

        var reportWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(report);
        return NormalizeDate(
            reportWindow.PeriodStartDate
            ?? reportWindow.PeriodAnchorDate
            ?? reportWindow.PeriodEndDate
            ?? report.ReportDate
            ?? period?.ReportDate
            ?? period?.PeriodStart
            ?? period?.PeriodEnd
            ?? report.ApprovedAtUtc
            ?? report.SubmittedAtUtc
            ?? report.UpdatedAtUtc);
    }

    private static bool IsManagementOrAdmin(MeResponse actor)
        => IsAdmin(actor) ||
           RoleGuard.IsGeneratedManagementAccount(actor) ||
           RoleGuard.IsManagerLevel(actor) ||
           RoleGuard.TryGetManagerUnit(actor, out _);

    private static bool IsAdmin(MeResponse actor)
        => RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor);

    private static DateTime? NormalizeDate(DateTime? value)
        => value?.Date;

    private static bool TryParseDayKey(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        var digits = new string(normalized.Where(char.IsDigit).ToArray());
        if (DateTime.TryParseExact(
                digits,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
        {
            date = date.Date;
            return true;
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
        {
            date = date.Date;
            return true;
        }

        return false;
    }
}

internal readonly record struct HistoricalMutationDecision(
    bool IsAllowed,
    string Required,
    DateTime? SourceDate,
    DateTime RecentCutoffDate,
    DateTime ManagementCutoffDate);
