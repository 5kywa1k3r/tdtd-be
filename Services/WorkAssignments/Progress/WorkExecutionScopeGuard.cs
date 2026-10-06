using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignments.Progress;

public static class WorkExecutionScopeGuard
{
    public static async Task RecordFirstApprovalAsync(MongoDbContext db, IClientSessionHandle session,
        string assignmentId, DateTime occurredAtUtc, CancellationToken ct,
        string? approvedPeriodId = null, WorkAssignmentReportStatus? previousStatus = null)
    {
        var a = await db.WorkAssignments.Find(session, x => x.Id == assignmentId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (a == null || !string.IsNullOrWhiteSpace(a.FlowInstanceId)) return;
        await db.WorkAssignments.UpdateOneAsync(session, x => x.Id == assignmentId && x.FirstApprovedAtUtc == null,
            Builders<WorkAssignment>.Update.Set(x => x.FirstApprovedAtUtc, occurredAtUtc), cancellationToken: ct);
        // End the correction exception in the same transaction as its fresh approval.
        // Reactivating an already approved report must not settle a correction.
        if (SettlesReopenedPeriod(a, approvedPeriodId, previousStatus, occurredAtUtc))
            await db.WorkAssignments.UpdateOneAsync(session, x => x.Id == a.Id &&
                    x.CompletionRevision == a.CompletionRevision && x.CompletionReviewPeriodId == approvedPeriodId,
                Builders<WorkAssignment>.Update.Set(x => x.CompletionReviewPeriodId, null)
                    .Set(x => x.CompletionProjectionPending, true).Inc(x => x.CompletionRevision, 1), cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(a.ParentAssignmentId))
        {
            await db.Works.UpdateOneAsync(session, w => w.Id == a.WorkId && w.FirstApprovedAtUtc == null,
                Builders<Work>.Update.Set(w => w.FirstApprovedAtUtc, occurredAtUtc), cancellationToken: ct);
            if (!string.IsNullOrEmpty(approvedPeriodId) && previousStatus is WorkAssignmentReportStatus.Draft or WorkAssignmentReportStatus.Submitted)
                await db.Works.UpdateOneAsync(session, w => w.Id == a.WorkId && w.CompletionReviewPeriodId == approvedPeriodId &&
                    w.CompletionReopenedAtUtc < occurredAtUtc && w.CompletedAtUtc == null && w.Status != WorkStatus.S3,
                    Builders<Work>.Update.Set(w => w.CompletionReviewPeriodId, null)
                        .Inc(w => w.CompletionRevision, 1), cancellationToken: ct);
        }
    }

    public static bool IsReopenedPeriod(WorkAssignment a, string? periodId) =>
        string.IsNullOrWhiteSpace(a.FlowInstanceId) && a.IsActive && !a.IsDeleted &&
        !WorkExecutionProgressPolicy.IsCompleted(a) && a.CompletionReopenedAtUtc.HasValue &&
        !string.IsNullOrEmpty(periodId) && a.CompletionReviewPeriodId == periodId;

    public static bool SettlesReopenedPeriod(WorkAssignment a, string? periodId,
        WorkAssignmentReportStatus? previousStatus, DateTime approvedAtUtc) =>
        IsReopenedPeriod(a, periodId) && approvedAtUtc > a.CompletionReopenedAtUtc &&
        previousStatus is WorkAssignmentReportStatus.Draft or WorkAssignmentReportStatus.Submitted;

    // A new parent/Work completion supersedes an exception granted before that decision.
    // Legacy completion records without a timestamp keep the existing explicit-period contract.
    public static bool IsCurrentReopenedPeriod(WorkAssignment a, Work w,
        IReadOnlyList<WorkAssignment> ancestors, string? periodId) =>
        IsReopenedPeriod(a, periodId) && !(w.CompletedAtUtc > a.CompletionReopenedAtUtc) &&
        !ancestors.Any(x => x.CompletedAtUtc > a.CompletionReopenedAtUtc);

    public static async Task<bool> IsOpenAsync(MongoDbContext db, WorkAssignment a, string? periodId,
        CancellationToken ct, IClientSessionHandle? session = null)
    {
        var w = await (session == null ? db.Works.Find(w => w.Id == a.WorkId && !w.IsDeleted)
            : db.Works.Find(session, w => w.Id == a.WorkId && !w.IsDeleted)).FirstOrDefaultAsync(ct);
        var ancestors = await ReadAncestorsAsync(db, a, ct, session);
        return IsOpen(a, w, ancestors, periodId);
    }

    // Parent links are authoritative; a stale denormalized Path must not grant a bypass.
    public static async Task<IReadOnlyList<WorkAssignment>> ReadAncestorsAsync(MongoDbContext db,
        WorkAssignment a, CancellationToken ct, IClientSessionHandle? session = null)
    {
        var rows = new List<WorkAssignment>();
        var seen = new HashSet<string> { a.Id };
        var id = a.ParentAssignmentId;
        while (!string.IsNullOrEmpty(id) && seen.Add(id))
        {
            var parent = await (session == null ? db.WorkAssignments.Find(x => x.Id == id)
                : db.WorkAssignments.Find(session, x => x.Id == id)).FirstOrDefaultAsync(ct);
            if (parent == null) break;
            rows.Add(parent);
            id = parent.ParentAssignmentId;
        }
        return rows;
    }

    public static bool IsEffective(WorkAssignment a, Work? w, IReadOnlyList<WorkAssignment> ancestors)
    {
        if (w == null || w.IsDeleted || w.Id != a.WorkId || !a.IsActive || a.IsDeleted) return false;
        var byId = ancestors.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string> { a.Id };
        var id = a.ParentAssignmentId;
        while (!string.IsNullOrEmpty(id))
        {
            if (!seen.Add(id) || !byId.TryGetValue(id, out var parent) || parent.WorkId != a.WorkId
                || parent.IsDeleted || !parent.IsActive) return false;
            id = parent.ParentAssignmentId;
        }
        return true;
    }

    public static bool IsOpen(WorkAssignment a, Work? w, IReadOnlyList<WorkAssignment> ancestors, string? periodId)
        => IsEffective(a, w, ancestors) && !WorkExecutionProgressPolicy.IsCompleted(a) &&
           (IsCurrentReopenedPeriod(a, w!, ancestors, periodId) || (!w!.CompletedAtUtc.HasValue && w.Status != WorkStatus.S3 &&
               ancestors.All(x => !WorkExecutionProgressPolicy.IsCompleted(x))));

    public static async Task EnsureOpenAsync(MongoDbContext db, string assignmentId, string? periodId,
        CancellationToken ct, IClientSessionHandle? session = null)
    {
        var a = await (session == null ? db.WorkAssignments.Find(x => x.Id == assignmentId && !x.IsDeleted)
            : db.WorkAssignments.Find(session, x => x.Id == assignmentId && !x.IsDeleted)).FirstOrDefaultAsync(ct);
        // Flow keeps its own lifecycle contract.
        if (a != null && !string.IsNullOrWhiteSpace(a.FlowInstanceId)) return;
        if (a == null || !await IsOpenAsync(db, a, periodId, ct, session))
            throw AppExceptionFactory.Create(AppErrorCode.WORK_ASSIGNMENT_REPORT_SCOPE_COMPLETED_LOCKED,
                new { assignmentId, periodId });
    }
}
