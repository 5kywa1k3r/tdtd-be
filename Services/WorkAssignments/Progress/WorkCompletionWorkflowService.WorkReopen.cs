using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping.Persistence;

namespace tdtd_be.Services.WorkAssignments.Progress;

public sealed partial class WorkCompletionWorkflowService
{
    public async Task<WorkCompletionState> ReadWorkCompletionAsync(string id, string actor, CancellationToken ct)
    {
        var w = await _db.Works.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(ct) ?? throw NotFound();
        if (w.Owner?.UserId != actor) throw Forbidden();
        var roots = await _db.WorkAssignments.Find(x => x.WorkId == id && x.ParentAssignmentId == null &&
            x.IsActive && !x.IsDeleted && x.FlowInstanceId == null).ToListAsync(ct);
        var map = roots.ToDictionary(x => x.Id);
        var ids = map.Keys.ToArray();
        var periods = await _db.WorkReportPeriods.Find(x => x.WorkId == id && ids.Contains(x.WorkAssignmentId) &&
            x.IsActive && !x.IsDeleted && x.CurrentReportId != null).ToListAsync(ct);
        var flow = !string.IsNullOrEmpty(w.DynamicFlowRuntimeInstanceId) || w.AssignmentTopologyOwner == WorkAssignmentTopologyOwners.P5FlowRuntime ||
            await _db.DynamicFlowInstances.Find(x => x.WorkId == id && !x.IsDeleted).AnyAsync(ct);
        var completed = w.CompletedAtUtc.HasValue || w.Status == WorkStatus.S3;
        var requiresReportSelection = await HasWorkLevelReportAsync(id, ct);
        return new(id, w.Name, w.CompletionRevision, completed, completed && !flow && (!requiresReportSelection || periods.Count > 0),
            await WorkReopenHoldAsync(w, ct), w.CompletionProjectionPending,
            periods.Select(p => new WorkCompletionPeriodChoice(p.Id, p.WorkAssignmentId, map[p.WorkAssignmentId].Name,
                p.PeriodKey, map[p.WorkAssignmentId].Assignees?.FirstOrDefault(u => u.UserId == p.AssigneeUserId)?.FullName ?? "Người nhận")).ToList(),
            requiresReportSelection);
    }

    public async Task<WorkCompletionState> ReopenWorkAsync(string id, string actor, WorkCompletionReopenCommand command, CancellationToken ct)
    {
        ValidateCommand(command.CommandId, command.ExpectedRevision);
        var reason = RequireReason(command.Reason);
        var selectedPeriodId = string.IsNullOrWhiteSpace(command.PeriodId) ? null : command.PeriodId.Trim();
        var receiptId = ReceiptId(id, actor, "work-reopen:" + command.CommandId);
        await _transactions.ExecuteAsync(async (s, token) =>
        {
            var w = await _db.Works.Find(s, x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(token) ?? throw NotFound();
            if (w.Owner?.UserId != actor) throw Forbidden();
            if (!string.IsNullOrEmpty(w.DynamicFlowRuntimeInstanceId) || w.AssignmentTopologyOwner == WorkAssignmentTopologyOwners.P5FlowRuntime ||
                await _db.DynamicFlowInstances.Find(s, x => x.WorkId == id && !x.IsDeleted).AnyAsync(token))
                throw Invalid("Nhiệm vụ thuộc luồng riêng, chưa hỗ trợ mở lại tại đây.");
            var prior = await _db.WorkHistories.Find(s, x => x.Id == receiptId).FirstOrDefaultAsync(token);
            if (prior != null)
            {
                if (prior.Data?.GetValueOrDefault("reason")?.ToString() != reason ||
                    prior.Data?.GetValueOrDefault("periodId")?.ToString() != (selectedPeriodId ?? "") ||
                    prior.Data?.GetValueOrDefault("expectedRevision")?.ToString() != command.ExpectedRevision.ToString())
                    throw Conflict("COMMAND_REUSED");
                return;
            }
            if (w.CompletionRevision != command.ExpectedRevision || (!w.CompletedAtUtc.HasValue && w.Status != WorkStatus.S3))
                throw Conflict("WORK_COMPLETION_STALE");
            if (selectedPeriodId == null)
            {
                // Empty choices are not proof of absence: hidden, draft and unresolved reports still count.
                if (await HasWorkLevelReportAsync(id, token, s))
                    throw AppExceptionFactory.Create(AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
                        details: new { reason = "WORK_REOPEN_REPORT_REQUIRED" },
                        message: "Nhiệm vụ đã có báo cáo tại cấp này. Tải lại và chọn báo cáo cần sửa; lý do đã nhập vẫn được giữ.");
            }
            else
            {
                var period = await _db.WorkReportPeriods.Find(s, p => p.Id == selectedPeriodId && p.WorkId == id &&
                    p.IsActive && !p.IsDeleted && p.CurrentReportId != null).FirstOrDefaultAsync(token) ?? throw NotFound();
                var root = await _db.WorkAssignments.Find(s, a => a.Id == period.WorkAssignmentId && a.WorkId == id &&
                    a.ParentAssignmentId == null && a.IsActive && !a.IsDeleted && a.FlowInstanceId == null).FirstOrDefaultAsync(token) ?? throw NotFound();
                var binding = await _db.WorkTemplateAssignees.Find(s, b => b.Id == period.WorkTemplateAssigneeId && b.WorkAssignmentId == root.Id &&
                    b.IsActive && !b.IsDeleted).FirstOrDefaultAsync(token) ?? throw NotFound();
                await AggregateLifecycleParticipant.EnsureMutationAsync(AggregateHostIntegration.Transaction(_db, s),
                    period.CurrentReportId!, binding.Id, binding.AssignmentType == "ONCE" ? "ONCE" : period.PeriodKey, token);
            }
            var now = DateTime.UtcNow;
            var filter = Builders<Work>.Filter;
            var revision = w.CompletionRevision == 0 ? filter.Eq(x => x.CompletionRevision, 0) | filter.Exists(x => x.CompletionRevision, false)
                : filter.Eq(x => x.CompletionRevision, w.CompletionRevision);
            var result = await _db.Works.UpdateOneAsync(s, filter.Eq(x => x.Id, id) & revision &
                filter.Eq(x => x.UpdatedAtUtc, w.UpdatedAtUtc) & filter.Eq(x => x.IsDeleted, false),
                Builders<Work>.Update.Set(x => x.Status, w.FirstApprovedAtUtc.HasValue ? WorkStatus.S2 : WorkStatus.S1)
                    .Set(x => x.CompletedAtUtc, null).Set(x => x.CompletedDate, null).Set(x => x.CompletedByUserId, null)
                    .Set(x => x.CompletionMode, null).Set(x => x.CompletionReason, reason)
                    .Set(x => x.CompletionReopenedAtUtc, now).Set(x => x.CompletionReopenedByUserId, actor)
                    .Set(x => x.CompletionReviewPeriodId, selectedPeriodId).Set(x => x.CompletionProjectionPending, true)
                    .Set(x => x.UpdatedAtUtc, now).Set(x => x.UpdatedByUserId, actor)
                    .Inc(x => x.CompletionRevision, 1).Inc(x => x.DirectSourceRevision, 1), cancellationToken: token);
            if (result.ModifiedCount != 1) throw Conflict("WORK_COMPLETION_STALE");
            await _db.WorkHistories.InsertOneAsync(s, new() { Id = receiptId, WorkId = id, Type = WorkHistoryType.UPDATED,
                AtUtc = now, ByUserId = actor, CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = actor, UpdatedByUserId = actor,
                Data = new() { ["action"] = "WORK_REOPENED", ["reason"] = reason, ["periodId"] = selectedPeriodId ?? "",
                    ["expectedRevision"] = command.ExpectedRevision!.Value, ["previousCompletionRevision"] = w.CompletionRevision,
                    ["previousCompletedAtUtc"] = w.CompletedAtUtc ?? DateTime.MinValue } }, cancellationToken: token);
            // No descendant completion/activation/report state is rewritten.
            var nodes = await _db.WorkAssignments.Find(s, a => a.WorkId == id && !a.IsDeleted).Project(a => a.Id).ToListAsync(token);
            await AggregateHostIntegration.RelationshipAsync(_db, s, id, nodes.ToArray(), "WORK_REOPEN:" + receiptId, token, preserveIdentity: true);
        }, ct);
        await TryConvergeWorkAsync(id, ct);
        return await ReadWorkCompletionAsync(id, actor, ct);
    }

    private async Task<bool> HasWorkLevelReportAsync(string workId, CancellationToken ct, IClientSessionHandle? s = null)
    {
        // Only known descendants are excluded. Missing assignment/pointer data must not turn into "no report".
        var children = await (s == null
            ? _db.WorkAssignments.Find(a => a.WorkId == workId && !a.IsDeleted && a.ParentAssignmentId != null)
            : _db.WorkAssignments.Find(s, a => a.WorkId == workId && !a.IsDeleted && a.ParentAssignmentId != null))
            .Project(a => a.Id).ToListAsync(ct);
        var reports = s == null
            ? _db.WorkAssignmentReports.Find(r => r.WorkId == workId && !r.IsDeleted && !children.Contains(r.WorkAssignmentId))
            : _db.WorkAssignmentReports.Find(s, r => r.WorkId == workId && !r.IsDeleted && !children.Contains(r.WorkAssignmentId));
        if (await reports.AnyAsync(ct)) return true;
        return await (s == null
            ? _db.WorkReportPeriods.Find(p => p.WorkId == workId && !p.IsDeleted && p.CurrentReportId != null && !children.Contains(p.WorkAssignmentId))
            : _db.WorkReportPeriods.Find(s, p => p.WorkId == workId && !p.IsDeleted && p.CurrentReportId != null && !children.Contains(p.WorkAssignmentId)))
            .AnyAsync(ct);
    }

    private async Task<bool> WorkReopenHoldAsync(Work w, CancellationToken ct, IClientSessionHandle? s = null)
    {
        if (w.CompletionReviewPeriodId == null || !w.CompletionReopenedAtUtc.HasValue || w.CompletedAtUtc.HasValue || w.Status == WorkStatus.S3) return false;
        var p = await (s == null ? _db.WorkReportPeriods.Find(x => x.Id == w.CompletionReviewPeriodId && x.WorkId == w.Id && x.IsActive && !x.IsDeleted)
            : _db.WorkReportPeriods.Find(s, x => x.Id == w.CompletionReviewPeriodId && x.WorkId == w.Id && x.IsActive && !x.IsDeleted)).FirstOrDefaultAsync(ct);
        if (p?.CurrentReportId == null) return true;
        var r = await (s == null ? _db.WorkAssignmentReports.Find(x => x.Id == p.CurrentReportId)
            : _db.WorkAssignmentReports.Find(s, x => x.Id == p.CurrentReportId)).FirstOrDefaultAsync(ct);
        if (r == null || r.WorkId != w.Id || r.WorkAssignmentId != p.WorkAssignmentId) return true;
        return !HasFreshCorrectionApproval(r, p.Id, w.CompletionReopenedAtUtc.Value);
    }

    public static bool HasFreshCorrectionApproval(WorkAssignmentReport? report, string periodId, DateTime reopenedAtUtc) =>
        report is { IsActive: true, IsCurrent: true, IsDeleted: false, Status: WorkAssignmentReportStatus.Approved } &&
        report.WorkReportPeriodId == periodId && (report.LifecycleProjectionOutbox ?? new()).Any(e =>
            e.CreatedAtUtc > reopenedAtUtc && e.ToIsActive && string.Equals(e.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(e.FromStatus, "DRAFT", StringComparison.OrdinalIgnoreCase) || string.Equals(e.FromStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase)));

    public async Task TryConvergeWorkAsync(string id, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var w = await _db.Works.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(ct);
                if (w == null || !w.CompletionProjectionPending) return;
                var completed = w.CompletedAtUtc.HasValue || w.Status == WorkStatus.S3;
                var actor = completed ? w.CompletedByUserId : w.CompletionReopenedByUserId;
                if ((!completed || actor != null) && !MongoDB.Bson.ObjectId.TryParse(actor, out _))
                    throw new InvalidOperationException("WORK_COMPLETION_AUDIT_ACTOR_MISSING");
                var nodes = await _db.WorkAssignments.Find(a => a.WorkId == id && !a.IsDeleted && a.FlowInstanceId == null).ToListAsync(ct);
                await RestoreExecutionNodesAsync(nodes, actor, ct);
                await _sync.RebuildWorkSnapshotsAsync(id, ct);
                await _projection.RebuildWorkAsync(id, actor ?? "system", ct);
                var settled = await _db.Works.UpdateOneAsync(x => x.Id == id && x.CompletionRevision == w.CompletionRevision &&
                    x.DirectSourceRevision == w.DirectSourceRevision && !x.IsDeleted,
                    Builders<Work>.Update.Set(x => x.CompletionProjectionPending, false), cancellationToken: ct);
                if (settled.MatchedCount == 1) return;
            }
            throw new InvalidOperationException("WORK_REOPEN_PROJECTION_DID_NOT_CONVERGE");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log.LogWarning(ex, "Work reopen committed; projection pending for {WorkId}", id); }
    }

    private async Task RestoreOpenSubtreeAsync(WorkAssignment root, string actor, CancellationToken ct)
    {
        var nodes = await _db.WorkAssignments.Find(a => a.WorkId == root.WorkId && !a.IsDeleted && a.FlowInstanceId == null).ToListAsync(ct);
        var selected = new HashSet<string> { root.Id };
        bool added;
        do
        {
            added = false;
            foreach (var node in nodes)
                if (node.ParentAssignmentId != null && selected.Contains(node.ParentAssignmentId)) added |= selected.Add(node.Id);
        } while (added);
        await RestoreExecutionNodesAsync(nodes.Where(n => selected.Contains(n.Id)), actor, ct);
    }

    private async Task RestoreExecutionNodesAsync(IEnumerable<WorkAssignment> nodes, string? actor, CancellationToken ct)
    {
        foreach (var a in nodes)
        {
            if (await WorkExecutionScopeGuard.IsOpenAsync(_db, a, null, ct))
                await _materialize.EnqueueOrTouchAsync(a, actor!, ct);
            else await _materialize.DisableByAssignmentIdAsync(a.Id, actor, ct);
            var periods = await _db.WorkReportPeriods.Find(p => p.WorkAssignmentId == a.Id && !p.IsDeleted).ToListAsync(ct);
            foreach (var p in periods) await _queue.UpsertPeriodAsync(p, actor, ct);
        }
    }
}
