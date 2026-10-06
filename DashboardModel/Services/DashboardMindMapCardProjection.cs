using tdtd_be.DashboardModel.DTOs.MindMap;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;

namespace tdtd_be.DashboardModel.Services;

internal static class DashboardMindMapCardProjection
{
    internal static (int Units, int Users, int UnknownUnits) Recipients(IEnumerable<WorkTemplateAssignee> bindings)
    {
        var rows = bindings.Where(b => b.IsActive && !b.IsDeleted && !string.IsNullOrWhiteSpace(b.AssigneeUserId)).ToList();
        var users = rows.GroupBy(b => b.AssigneeUserId, StringComparer.Ordinal).ToList();
        return (rows.Select(b => b.AssigneeUnitId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Count(),
            users.Count, users.Count(group => !group.Any(b => !string.IsNullOrWhiteSpace(b.AssigneeUnitId))));
    }

    internal static bool Matches(WorkAssignmentReport r, WorkReportPeriod p) => r.IsActive && !r.IsDeleted && r.IsCurrent
        && r.Id == p.CurrentReportId && r.WorkId == p.WorkId && r.WorkAssignmentId == p.WorkAssignmentId
        && r.WorkReportPeriodId == p.Id && r.AssigneeUserId == p.AssigneeUserId
        && r.DynamicFormTemplateId == p.DynamicFormTemplateId
        && r.Status is WorkAssignmentReportStatus.Draft or WorkAssignmentReportStatus.Submitted or WorkAssignmentReportStatus.Approved;

    internal static (string ReadState, WorkAssignmentReport? Report) ResolveCurrentReport(WorkReportPeriod period, WorkAssignmentReport? candidate)
    {
        if (string.IsNullOrWhiteSpace(period.CurrentReportId)) return ("MISSING", null);
        return candidate is not null && Matches(candidate, period) ? ("AVAILABLE", candidate) : ("UNAVAILABLE", null);
    }

    internal static (string Kind, string Label) Schedule(WorkAssignment node)
    {
        if (string.Equals(node.AssignmentType, WorkAssignmentTypes.Once, StringComparison.OrdinalIgnoreCase)) return (WorkAssignmentTypes.Once, "Một lần");
        if (!string.Equals(node.AssignmentType, WorkAssignmentTypes.PeriodicReport, StringComparison.OrdinalIgnoreCase)) return ("UNKNOWN", "Chưa rõ lịch");
        var cycle = node.Schedule?.CycleType?.Trim().ToUpperInvariant() switch
        {
            "DAILY" => "Hằng ngày", "WEEKLY" => "Hằng tuần", "MONTHLY" => "Hằng tháng",
            "QUARTERLY" => "Hằng quý", "SEMI_ANNUAL" => "6 tháng", _ => "Chưa rõ chu kỳ",
        };
        return (WorkAssignmentTypes.PeriodicReport, "Định kỳ · " + cycle);
    }

    internal static DashboardMindMapCardSummaryDto Build(WorkAssignment node, IReadOnlyList<WorkTemplateAssignee> bindings,
        IReadOnlyList<WorkReportPeriod> periods, IReadOnlyDictionary<string, WorkAssignmentReport> reports)
    {
        var active = bindings.Where(b => b.WorkId == node.WorkId && b.WorkAssignmentId == node.Id && b.IsActive && !b.IsDeleted).ToList();
        var materialized = periods.Where(p => p.WorkId == node.WorkId && p.WorkAssignmentId == node.Id && p.IsActive && !p.IsDeleted).ToList();
        var recipients = Recipients(active);
        var schedule = Schedule(node);
        var result = new DashboardMindMapCardSummaryDto { AssignmentId = node.Id, ScheduleKind = schedule.Kind, ScheduleLabel = schedule.Label,
            RecipientUnitCount = recipients.Units, RecipientUserCount = recipients.Users, UnknownRecipientUnitCount = recipients.UnknownUnits };
        var bindingsByForm = active.Where(b => !string.IsNullOrWhiteSpace(b.DynamicFormTemplateId)).ToLookup(b => b.DynamicFormTemplateId!, StringComparer.Ordinal);
        var periodsByForm = materialized.Where(p => !string.IsNullOrWhiteSpace(p.DynamicFormTemplateId)).ToLookup(p => p.DynamicFormTemplateId!, StringComparer.Ordinal);
        var formIds = bindingsByForm.Select(g => g.Key).Concat(periodsByForm.Select(g => g.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var formId in formIds)
        {
            var formBindings = bindingsByForm[formId].ToList(); var formPeriods = periodsByForm[formId].ToList();
            var count = Recipients(formBindings);
            var form = new DashboardMindMapCardFormSummaryDto { DynamicFormTemplateId = formId,
                DynamicFormTemplateCode = formBindings.FirstOrDefault()?.DynamicFormTemplateCode ?? formPeriods.FirstOrDefault()?.DynamicFormTemplateCode ?? "",
                DynamicFormTemplateName = formBindings.FirstOrDefault()?.DynamicFormTemplateName ?? formPeriods.FirstOrDefault()?.DynamicFormTemplateName ?? "",
                RecipientUnitCount = count.Units, RecipientUserCount = count.Users, UnknownRecipientUnitCount = count.UnknownUnits,
                PeriodCount = formPeriods.Count, OverdueCount = formPeriods.Count(p => WorkReportPeriodStatusHelper.IsOverdue(p.Status)),
                NoDueDateCount = formPeriods.Count(p => !p.DueAtUtc.HasValue) };
            foreach (var period in formPeriods)
            {
                if (string.IsNullOrWhiteSpace(period.CurrentReportId)) { form.MissingCount++; continue; }
                if (!reports.TryGetValue(period.CurrentReportId, out var report) || !Matches(report, period)) { form.UnavailableCurrentReportCount++; continue; }
                form.ReportedCount++;
                if (report.Status == WorkAssignmentReportStatus.Submitted) form.SubmittedCount++;
                else if (report.Status == WorkAssignmentReportStatus.Approved) form.ApprovedCount++;
                else if (report.Status == WorkAssignmentReportStatus.Draft && report.ReturnedAtUtc.HasValue) form.ReturnedCount++;
            }
            if (form.UnavailableCurrentReportCount > 0 || formPeriods.Any(p => (int)p.Status is < 0 or > 7)) form.ReadState = "PARTIAL";
            result.Forms.Add(form);
        }
        if (result.Forms.Any(f => f.ReadState == "PARTIAL") || active.Any(b => string.IsNullOrWhiteSpace(b.DynamicFormTemplateId) || string.IsNullOrWhiteSpace(b.AssigneeUserId))
            || materialized.Any(p => string.IsNullOrWhiteSpace(p.DynamicFormTemplateId))) result.ReadState = "PARTIAL";
        return result;
    }
}
