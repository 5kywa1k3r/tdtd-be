using MongoDB.Driver;
using tdtd_be.Common.Time;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed partial class AggregateMongoCommandReader
{
    private async Task<AggregateCommitAuthority> AuthorizeSlotAsync(AggregatePeriodContextDto selector, string actor, string sessionKey, CancellationToken ct)
    {
        var user = await db.Users.Find(u => u.Id == actor && !u.IsDeleted).FirstOrDefaultAsync(ct);
        var assignment = await db.WorkAssignments.Find(a => a.Id == selector.AssignmentId && a.WorkId == selector.WorkId && !a.IsDeleted).FirstOrDefaultAsync(ct);
        var binding = await db.WorkTemplateAssignees.Find(b => b.Id == selector.BindingId && b.WorkAssignmentId == selector.AssignmentId
            && b.WorkId == selector.WorkId && b.AssigneeUserId == actor && !b.IsDeleted).FirstOrDefaultAsync(ct);
        var work = await db.Works.Find(w => w.Id == selector.WorkId && !w.IsDeleted).FirstOrDefaultAsync(ct);
        if (user == null || assignment == null || binding == null || work == null || DynamicFlowBranchVisibility.IsFlowAssignment(assignment)
            || !(assignment.Assignees?.Any(a => a.UserId == actor) ?? false)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var kind = binding.AssignmentType == "ONCE" ? "ONCE" : "PERIODIC";
        var occurrence = kind == "ONCE" ? "ONCE" : selector.PeriodKey ?? "";
        var periods = await db.WorkReportPeriods.Find(p => p.WorkTemplateAssigneeId == binding.Id && (kind == "ONCE" || p.PeriodKey == occurrence) && !p.IsDeleted).Limit(2).ToListAsync(ct);
        if (periods.Count > 1 || periods.Any(p => !string.IsNullOrEmpty(p.CurrentReportId))) throw new AggregatePreviewException("AGG_SLOT_REPORT_EXISTS");
        var start = binding.StartDate ?? assignment.StartDate;
        var end = binding.DueDate ?? assignment.DueDate;
        var schedule = AggregateDigest.Of(new { binding.Schedule, binding.AssignmentType, start, end, binding.IsActive, assignment.ParentAssignmentId });
        if (periods.Count == 0 && kind != "ONCE")
        {
            if (start == null || end == null || binding.Schedule == null || !ScheduleValidator.IsValid(binding.Schedule)
                || (end.Value - start.Value).TotalDays > 3660) throw new AggregatePreviewException("AGG_SCHEDULE_HORIZON_UNRESOLVED");
            if (!AssignmentScheduleTimeHelper.GetDueDatesInRange(binding.Schedule, start.Value, end.Value)
                .Any(d => AssignmentScheduleTimeHelper.GetPeriodKey(binding.Schedule, d) == occurrence))
                throw new AggregatePreviewException("AGG_OCCURRENCE_UNRESOLVED");
        }
        var form = await db.DynamicFormTemplates.Find(f => f.Id == binding.DynamicFormTemplateId && f.IsPublished && !f.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var schema = AggregateNativePayloadAdapter.Schema(form);
        if (schema.Pin.SchemaHash != binding.DynamicFormSchemaHash) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var declaration = await new AggregateMongoDataWindows(db).ReadAsync("SLOT", binding.Id + ":" + occurrence, ct);
        var context = new AggregatePeriodContextDto(kind, selector.WorkId, assignment.Id!, binding.Id, null,
            periods.SingleOrDefault()?.Id, periods.SingleOrDefault()?.PeriodInstanceKey, occurrence,
            declaration?.StartDate, declaration?.EndDate, null, schedule);
        var pins = new List<AggregateAuthorityPin>();
        await Pin(pins, db.Users.CollectionNamespace.CollectionName, actor, ct);
        await Pin(pins, db.Works.CollectionNamespace.CollectionName, work.Id, ct);
        await Pin(pins, db.WorkAssignments.CollectionNamespace.CollectionName, assignment.Id!, ct);
        await Pin(pins, db.WorkTemplateAssignees.CollectionNamespace.CollectionName, binding.Id, ct);
        await Pin(pins, db.DynamicFormTemplates.CollectionNamespace.CollectionName, form.Id, ct);
        var scopeOpen = !work.CompletedAtUtc.HasValue && work.Status != WorkStatus.S3 && !assignment.CompletedAtUtc.HasValue;
        var ancestorId = assignment.ParentAssignmentId;
        var visited = new HashSet<string>();
        while (!string.IsNullOrEmpty(ancestorId))
        {
            if (!visited.Add(ancestorId)) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            var ancestor = await db.WorkAssignments.Find(a => a.Id == ancestorId && a.WorkId == work.Id && !a.IsDeleted).FirstOrDefaultAsync(ct)
                ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
            scopeOpen &= !ancestor.CompletedAtUtc.HasValue && ancestor.IsActive;
            await Pin(pins, db.WorkAssignments.CollectionNamespace.CollectionName, ancestor.Id!, ct);
            ancestorId = ancestor.ParentAssignmentId;
        }
        if (context.WorkReportPeriodId != null) await Pin(pins, db.WorkReportPeriods.CollectionNamespace.CollectionName, context.WorkReportPeriodId, ct);
        await PinDeclaration(pins, "SLOT:" + binding.Id + ":" + occurrence, ct);
        var facts = new AggregateAuthorityFacts(true, true, false, true, false, false, true, false, false, false,
            scopeOpen && (v2Enabled || !StatConfigPhaseBarrier.IsBlocked(StatConfigPhaseBarrierEntries.P9Run)), assignment.IsActive && binding.IsActive,
            "Draft", false, true, true, true, true);
        return new(actor, sessionKey, new(context, schema, facts, new(0, 0, 0, 0, schema.Pin.SchemaHash, ""),
            AggregateCanonical.Hash(new { actor, assignment.Assignees, binding.AssigneeUserId }), declaration,
            new Dictionary<string, AggregateValue>()), pins);
    }
}
