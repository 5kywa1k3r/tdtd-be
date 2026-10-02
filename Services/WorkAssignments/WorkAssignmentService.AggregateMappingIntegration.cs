using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments;

public sealed partial class WorkAssignmentService
{
    private Task<UpdateResult> CommitAggregateAwareCompletionAsync(WorkAssignment observed, Work observedWork, DateTime completedDate,
        FilterDefinition<WorkAssignment> filter, UpdateDefinition<WorkAssignment> update, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(observed.FlowInstanceId))
            return _ctx.WorkAssignments.UpdateOneAsync(filter, update, cancellationToken: ct);
        return _dynamicFlowTransactions.ExecuteAsync(async (session, token) =>
        {
            var current = await _ctx.WorkAssignments.Find(session, filter).FirstOrDefaultAsync(token);
            if (current == null) return new UpdateResult.Acknowledged(0, 0, null);
            var work = await _ctx.Works.Find(session, w => w.Id == current.WorkId && !w.IsDeleted).FirstOrDefaultAsync(token);
            if (work == null || work.CompletedAtUtc.HasValue || work.Status == WorkStatus.S3)
                throw new AggregatePreviewException("AGG_MUTATION_SCOPE_CLOSED");
            var assignments = await _ctx.WorkAssignments.Find(session, a => a.WorkId == current.WorkId && !a.IsDeleted).ToListAsync(token);
            var byId = assignments.ToDictionary(a => a.Id!);
            var ancestor = current.ParentAssignmentId;
            var visited = new HashSet<string>();
            while (!string.IsNullOrEmpty(ancestor))
            {
                if (!visited.Add(ancestor) || !byId.TryGetValue(ancestor, out var parent) || parent.CompletedAtUtc.HasValue)
                    throw new AggregatePreviewException("AGG_MUTATION_SCOPE_CLOSED");
                ancestor = parent.ParentAssignmentId;
            }
            var scope = ResolveAssignmentScopeIds(assignments, current);
            var bindings = await _ctx.WorkTemplateAssignees.Find(session, b => scope.Contains(b.WorkAssignmentId) && !b.IsDeleted).ToListAsync(token);
            var periods = await _ctx.WorkReportPeriods.Find(session, p => scope.Contains(p.WorkAssignmentId) && !p.IsDeleted && p.IsActive
                && (p.PeriodKind == null || p.PeriodKind == WorkReportPeriodKind.Scheduled)).ToListAsync(token);
            var readiness = WorkAssignmentCompletionReadiness.Evaluate(work, assignments, scope, bindings, periods, completedDate, DateTime.UtcNow);
            AggregateMutationParticipant.EnsureParentCompletion(readiness.CanComplete,
                assignments.Where(a => a.ParentAssignmentId == current.Id).Select(a => a.CompletedAtUtc.HasValue).ToArray());
            var result = await _ctx.WorkAssignments.UpdateOneAsync(session,
                filter & Builders<WorkAssignment>.Filter.Eq(a => a.UpdatedAtUtc, observed.UpdatedAtUtc), update, cancellationToken: token);
            if (result.ModifiedCount != 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            await WorkDirectSourceRevisionFence.IncrementAsync(_ctx, session, current.WorkId, token);
            return result;
        }, ct);
    }
}
