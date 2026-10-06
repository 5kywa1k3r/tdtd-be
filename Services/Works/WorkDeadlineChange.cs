using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Works;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.Works;

internal sealed record WorkDeadlineImpact(string Id, string Code, string Name, string? ParentAssignmentId,
    DateTime? OldDueDate, DateTime NewDueDate);

internal sealed record WorkDeadlinePlan(string Token, DateTime WorkVersion, long SourceRevision,
    DateTime? Deadline, IReadOnlyList<WorkDeadlineImpact> Affected);

internal static class WorkDeadlineChange
{
    internal static WorkDeadlinePlan Plan(Work work, WorkUpdateRequest request, string actor,
        IReadOnlyList<WorkAssignment> assignments)
    {
        if (request.ExpectedUpdatedAtUtc.HasValue &&
            request.ExpectedUpdatedAtUtc.Value.Ticks / TimeSpan.TicksPerMillisecond != work.UpdatedAtUtc.Ticks / TimeSpan.TicksPerMillisecond)
            throw AppExceptionFactory.Create(AppErrorCode.WORK_DATES_CHANGED);
        var deadline = WorkDatePolicy.NormalizeDay(request.DueDate ?? request.EndDate);
        var datesChanged = work.StartDate?.Date != request.StartDate?.Date ||
            work.EndDate?.Date != request.EndDate?.Date || work.DueDate?.Date != request.DueDate?.Date;
        var affected = new List<WorkDeadlineImpact>();
        if (datesChanged && deadline.HasValue)
        {
            foreach (var a in assignments.Where(a => !a.IsDeleted && !a.CompletedAtUtc.HasValue))
            {
                var parent = assignments.FirstOrDefault(p => p.Id == a.ParentAssignmentId);
                var oldDue = a.DueDate ?? a.CompletedDate ?? a.DueAtUtc ?? a.LatestDueAtUtc ??
                    WorkAssignmentDatePolicy.ResolveEffectiveDueDate(a, work, parent);
                if (!oldDue.HasValue || oldDue.Value.Date <= deadline.Value.Date) continue;
                // Preserve the existing assignment invariant while the business
                // decision about moving future starts remains unresolved.
                if (a.StartDate?.Date > deadline.Value.Date || a.Schedule?.StartDate?.Date > deadline.Value.Date)
                    throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_COMPLETED_BEFORE_START,
                        new { assignmentId = a.Id, a.Code, a.Name, a.StartDate, newDueDate = deadline },
                        $"Hạn mới trước ngày bắt đầu của phần việc {a.Code} — {a.Name}. Vui lòng kiểm tra ngày bắt đầu.");
                if (!string.IsNullOrEmpty(a.FlowInstanceId))
                    throw AppExceptionFactory.Create(AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE);
                affected.Add(new(a.Id!, a.Code, a.Name, a.ParentAssignmentId, oldDue, deadline.Value));
            }
        }
        if (datesChanged && request.StartDate?.Date > work.StartDate?.Date)
        {
            var earlier = assignments.FirstOrDefault(a => !a.IsDeleted && !a.CompletedAtUtc.HasValue && a.StartDate?.Date < request.StartDate.Value.Date);
            if (earlier is not null)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_START_OUT_OF_RANGE,
                    new { assignmentId = earlier.Id, earlier.Code, earlier.StartDate, workStartDate = request.StartDate },
                    $"Từ ngày mới nằm sau ngày bắt đầu của phần việc {earlier.Code} — {earlier.Name}. Vui lòng kiểm tra ngày bắt đầu.");
        }
        // Bound the confirmation to the actor, requested dates and the observed topology.
        // A fresh read inside the transaction rejects changed reports/assignments.
        var payload = JsonSerializer.Serialize(new { work.Id, actor, work.UpdatedAtUtc, work.DirectSourceRevision,
            request.StartDate, request.EndDate, request.DueDate,
            assignments = assignments.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => new {
                a.Id, a.UpdatedAtUtc, a.DueDate, a.StartDate, a.CompletedAtUtc, a.ParentAssignmentId }) });
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return new(token, work.UpdatedAtUtc, work.DirectSourceRevision, deadline, affected);
    }

    internal static async Task<WorkDeadlinePlan> PrepareAsync(MongoDbContext ctx, Work work,
        WorkUpdateRequest request, string actor, CancellationToken ct)
    {
        var assignments = await ctx.WorkAssignments.Find(a => a.WorkId == work.Id && !a.IsDeleted).ToListAsync(ct);
        var plan = Plan(work, request, actor, assignments);
        if (plan.Affected.Count > 0 && request.DeadlineConfirmationToken != plan.Token)
            throw AppExceptionFactory.Create(AppErrorCode.WORK_DATES_CONFIRM_REQUIRED,
                new { confirmationToken = plan.Token, deadline = plan.Deadline, affected = plan.Affected,
                    retainedReportPeriods = true });
        return plan;
    }

    internal static async Task ApplyAsync(MongoDbContext ctx, IClientSessionHandle session, Work observed,
        WorkUpdateRequest request, string actor, WorkDeadlinePlan plan, UpdateDefinition<Work> update, CancellationToken ct)
    {
        var current = await ctx.Works.Find(session, w => w.Id == observed.Id && !w.IsDeleted).FirstOrDefaultAsync(ct);
        if (current is null) throw AppExceptionFactory.Create(AppErrorCode.WORK_DATES_CHANGED);
        var assignments = await ctx.WorkAssignments.Find(session, a => a.WorkId == observed.Id && !a.IsDeleted).ToListAsync(ct);
        var actual = Plan(current, request, actor, assignments);
        if (actual.Token != plan.Token) throw AppExceptionFactory.Create(AppErrorCode.WORK_DATES_CHANGED);
        var now = observed.UpdatedAtUtc;
        var result = await ctx.Works.UpdateOneAsync(session, w => w.Id == observed.Id && w.UpdatedAtUtc == plan.WorkVersion && !w.IsDeleted,
            update.Inc(w => w.DirectSourceRevision, 1L), cancellationToken: ct);
        if (result.MatchedCount != 1) throw AppExceptionFactory.Create(AppErrorCode.WORK_DATES_CHANGED);
        foreach (var impact in plan.Affected)
        {
            var assignment = assignments.Single(a => a.Id == impact.Id);
            var changed = await ctx.WorkAssignments.UpdateOneAsync(session,
                a => a.Id == impact.Id && !a.IsDeleted && a.UpdatedAtUtc == assignment.UpdatedAtUtc,
                Builders<WorkAssignment>.Update.Set(a => a.DueDate, impact.NewDueDate)
                    .Set(a => a.DeadlineRetainedPeriodsBeforeUtc, now)
                    .Set(a => a.UpdatedAtUtc, now).Set(a => a.UpdatedByUserId, actor), cancellationToken: ct);
            if (changed.MatchedCount != 1) throw AppExceptionFactory.Create(AppErrorCode.WORK_DATES_CHANGED);
            await ctx.WorkTemplateAssignees.UpdateManyAsync(session,
                a => a.WorkAssignmentId == impact.Id && !a.IsDeleted,
                Builders<WorkTemplateAssignee>.Update.Set(a => a.DueDate, impact.NewDueDate)
                    .Set(a => a.UpdatedAtUtc, now).Set(a => a.UpdatedByUserId, actor), cancellationToken: ct);
        }
        // Report-set filters may use Work dates. Recompute draft aggregates using the
        // existing durable queue; retained report periods and submitted results stay intact.
        if (current.StartDate != observed.StartDate || current.EndDate != observed.EndDate)
            await tdtd_be.Services.AggregateMapping.Persistence.AggregateHostIntegration.RelationshipAsync(
                ctx, session, observed.Id, assignments.Select(a => a.Id).ToArray(),
                "WORK_FILTER_DATES:" + observed.Id + ":" + now.Ticks, ct, preserveIdentity: true);
        // Deliberately no period/report rematerialization here.
    }
}
