using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkAssignments.Queue;

namespace tdtd_be.Services.WorkAssignments.Progress;

public sealed partial class WorkCompletionWorkflowService
{
    private readonly MongoDbContext _db;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _projection;
    private readonly ILogger<WorkCompletionWorkflowService> _log;
    private readonly IWorkAssignmentQueueService _queue;
    private readonly IWorkAssignmentMaterializeJobService _materialize;
    private IMongoCollection<WorkAssignmentCompletionRequest> Requests => _db.Db.GetCollection<WorkAssignmentCompletionRequest>(tdtd_be.Data.Indexes.WorkCompletionIndexes.CollectionName);

    public WorkCompletionWorkflowService(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner transactions,
        IWorkAssignmentStatusSyncService sync, IDocRoleReadModelProjectionService projection, ILogger<WorkCompletionWorkflowService> log,
        IWorkAssignmentQueueService queue, IWorkAssignmentMaterializeJobService materialize)
        => (_db, _transactions, _sync, _projection, _log, _queue, _materialize) = (db, transactions, sync, projection, log, queue, materialize);

    public async Task<AssignmentCompletionState> ReadAsync(string id, string actor, CancellationToken ct)
    {
        var (a, w, parent) = await LoadAsync(id, null, ct);
        var requester = WorkCompletionAuthority.CanRequest(a, actor);
        var reviewer = WorkCompletionAuthority.CanDecide(a, w, parent, actor);
        if (!requester && !reviewer) throw Forbidden();
        var facts = await WorkExecutionReader.ReadAsync(_db, a, w, DateTime.UtcNow, ct);
        var history = await Requests.Find(r => r.AssignmentId == id).SortByDescending(r => r.RequestedAtUtc).Limit(100).ToListAsync(ct);
        var completed = WorkExecutionProgressPolicy.IsCompleted(a);
        var periodRows = await _db.WorkReportPeriods.Find(p => p.WorkAssignmentId == id && p.IsActive && !p.IsDeleted).ToListAsync(ct);
        var periods = periodRows.Select(p => {
            var u = a.Assignees?.FirstOrDefault(u => u.UserId == p.AssigneeUserId);
            return new CompletionPeriodChoice(p.Id, p.PeriodKey, p.AssigneeUserId,
                u?.FullName ?? u?.Username ?? "Người nhận cũ");
        }).ToList();
        return new(id, w.Id, a.Name, a.CompletionRevision, completed,
            requester && !completed && a.PendingCompletionRequestId == null,
            reviewer && !completed && a.PendingCompletionRequestId != null,
            reviewer && !facts.ReopenHold && periods.Count > 0,
            a.CompletionProjectionPending, facts, history, periods);
    }

    public async Task<IReadOnlyList<AssignmentCompletionState>> ReadWorkAsync(string workId, string actor, CancellationToken ct)
    {
        var w = await _db.Works.Find(x => x.Id == workId && !x.IsDeleted).FirstOrDefaultAsync(ct) ?? throw NotFound();
        var nodes = await _db.WorkAssignments.Find(a => a.WorkId == workId && !a.IsDeleted && a.IsActive && a.FlowInstanceId == null).ToListAsync(ct);
        var map = nodes.ToDictionary(a => a.Id);
        var visible = nodes.Where(a => WorkCompletionAuthority.CanRequest(a, actor) ||
            WorkCompletionAuthority.CanDecide(a, w, a.ParentAssignmentId != null ? map.GetValueOrDefault(a.ParentAssignmentId) : null, actor)).ToList();
        if (visible.Count == 0 && w.Owner?.UserId != actor) throw Forbidden();
        var result = new List<AssignmentCompletionState>();
        foreach (var a in visible) result.Add(await ReadAsync(a.Id, actor, ct));
        return result;
    }

    public async Task<AssignmentCompletionState> RequestAsync(string id, string actor, CompletionRequestCommand command, CancellationToken ct)
    {
        ValidateCommand(command.CommandId, command.ExpectedRevision);
        var reason = RequireReason(command.Reason);
        var requestId = ReceiptId(id, actor, command.CommandId);
        await _transactions.ExecuteAsync(async (s, token) =>
        {
            var (a, w, _) = await LoadAsync(id, s, token);
            if (!WorkCompletionAuthority.CanRequest(a, actor)) throw Forbidden();
            var replay = await Requests.Find(s, r => r.Id == requestId).FirstOrDefaultAsync(token);
            if (replay != null)
            {
                if (replay.Reason != reason || replay.RequesterUserId != actor) throw Conflict("COMMAND_REUSED");
                return;
            }
            EnsureRevision(a, command.ExpectedRevision);
            if (WorkExecutionProgressPolicy.IsCompleted(a) || a.PendingCompletionRequestId != null) throw Conflict("COMPLETION_ALREADY_CLOSED_OR_PENDING");
            var now = DateTime.UtcNow;
            await Requests.InsertOneAsync(s, new() { Id = requestId, WorkId = w.Id, AssignmentId = id,
                CommandId = command.CommandId, RequesterUserId = actor, Reason = reason, RequestedAtUtc = now }, cancellationToken: token);
            await UpdateAssignmentAsync(s, a, Builders<WorkAssignment>.Update.Set(x => x.PendingCompletionRequestId, requestId), actor, token);
            await WorkDirectSourceRevisionFence.IncrementAsync(_db, s, w.Id, token);
        }, ct);
        return await ReadAsync(id, actor, ct);
    }

    public async Task<AssignmentCompletionState> DecideAsync(string id, string requestId, string actor, CompletionDecisionCommand command, CancellationToken ct)
    {
        ValidateCommand(command.CommandId, command.ExpectedRevision);
        var reason = RequireReason(command.Reason);
        await _transactions.ExecuteAsync(async (s, token) =>
        {
            var (a, w, parent) = await LoadAsync(id, s, token);
            if (!WorkCompletionAuthority.CanDecide(a, w, parent, actor)) throw Forbidden();
            var request = await Requests.Find(s, r => r.Id == requestId && r.AssignmentId == id).FirstOrDefaultAsync(token) ?? throw NotFound();
            var next = command.Approve ? "APPROVED" : "REJECTED";
            if (request.State != "PENDING")
            {
                if (request.DecisionCommandId == command.CommandId && request.DecidedByUserId == actor &&
                    request.State == next && request.DecisionReason == reason) return;
                throw Conflict("COMPLETION_REQUEST_ALREADY_DECIDED");
            }
            EnsureRevision(a, command.ExpectedRevision);
            if (a.PendingCompletionRequestId != requestId || WorkExecutionProgressPolicy.IsCompleted(a)) throw Conflict("COMPLETION_REQUEST_STALE");
            var now = DateTime.UtcNow;
            await Requests.UpdateOneAsync(s, r => r.Id == requestId && r.State == "PENDING",
                Builders<WorkAssignmentCompletionRequest>.Update.Set(r => r.State, next).Set(r => r.DecisionCommandId, command.CommandId)
                    .Set(r => r.DecidedByUserId, actor).Set(r => r.DecisionReason, reason).Set(r => r.DecidedAtUtc, now), cancellationToken: token);
            var update = Builders<WorkAssignment>.Update.Set(x => x.PendingCompletionRequestId, null);
            if (command.Approve) update = Builders<WorkAssignment>.Update.Combine(update, CompletionUpdate(now, actor, "APPROVED_REQUEST"));
            await UpdateAssignmentAsync(s, a, update, actor, token);
            await WorkDirectSourceRevisionFence.IncrementAsync(_db, s, w.Id, token);
            if (command.Approve) await RelationshipAsync(s, a, command.CommandId, token);
        }, ct);
        await TryConvergeAsync(id, ct);
        return await ReadAsync(id, actor, ct);
    }

    public async Task<AssignmentCompletionState> ReopenAsync(string id, string actor, CompletionReopenCommand command, CancellationToken ct)
    {
        ValidateCommand(command.CommandId, command.ExpectedRevision);
        var reason = RequireReason(command.Reason);
        var receiptId = ReceiptId(id, actor, "reopen:" + command.CommandId);
        await _transactions.ExecuteAsync(async (s, token) =>
        {
            var (a, w, parent) = await LoadAsync(id, s, token);
            if (!WorkCompletionAuthority.CanDecide(a, w, parent, actor)) throw Forbidden();
            var prior = await Requests.Find(s, r => r.Id == receiptId).FirstOrDefaultAsync(token);
            if (prior != null)
            {
                if (prior.Reason != reason || prior.DecisionReason != command.PeriodId) throw Conflict("COMMAND_REUSED");
                return;
            }
            EnsureRevision(a, command.ExpectedRevision);
            var period = await _db.WorkReportPeriods.Find(s, p => p.Id == command.PeriodId && p.WorkAssignmentId == id && p.IsActive && !p.IsDeleted)
                .FirstOrDefaultAsync(token) ?? throw NotFound();
            if (!WorkExecutionScopeGuard.IsEffective(a, w, await WorkExecutionScopeGuard.ReadAncestorsAsync(_db, a, token, s)))
                throw Conflict("COMPLETION_BRANCH_INACTIVE");
            // Do not leave a reopened hold when a submitted parent still locks this period.
            var binding = await _db.WorkTemplateAssignees.Find(s, b => b.Id == period.WorkTemplateAssigneeId && !b.IsDeleted)
                .FirstOrDefaultAsync(token) ?? throw NotFound();
            await AggregateLifecycleParticipant.EnsureMutationAsync(AggregateHostIntegration.Transaction(_db, s),
                period.CurrentReportId ?? "", binding.Id, binding.AssignmentType == "ONCE" ? "ONCE" : period.PeriodKey, token);
            var now = DateTime.UtcNow;
            var facts = await WorkExecutionReader.ReadAsync(_db, a, w, now, token, s);
            if (facts.ReopenHold) throw Conflict("PERIOD_ALREADY_REOPENED");
            // Durable hold survives an interrupted return and is released only by a new approval of this period.
            await UpdateAssignmentAsync(s, a, Builders<WorkAssignment>.Update
                .Set(x => x.CompletedAtUtc, null).Set(x => x.CompletedDate, null).Set(x => x.CompletedByUserId, null)
                .Set(x => x.CompletionMode, null).Set(x => x.CompletionReopenedAtUtc, now)
                .Set(x => x.CompletionReviewPeriodId, period.Id).Set(x => x.CompletionProjectionPending, true)
                .Set(x => x.FirstApprovedAtUtc, facts.FirstApprovedAtUtc)
                .Set(x => x.ProgressStatus, facts.ExecutionStatus(false)), actor, token);
            await Requests.InsertOneAsync(s, new() { Id = receiptId, WorkId = w.Id, AssignmentId = id,
                CommandId = command.CommandId, RequesterUserId = actor, Reason = reason, State = "REOPENED",
                RequestedAtUtc = now, DecidedAtUtc = now, DecidedByUserId = actor, DecisionReason = command.PeriodId }, cancellationToken: token);
            await WorkDirectSourceRevisionFence.IncrementAsync(_db, s, w.Id, token);
            await RelationshipAsync(s, a, command.CommandId, token);
        }, ct);
        await TryConvergeAsync(id, ct);
        return await ReadAsync(id, actor, ct);
    }

    public async Task ReconcileAssignmentAsync(string id, CancellationToken ct)
    {
        var needsProjection = await _transactions.ExecuteAsync(async (s, token) =>
        {
            var (a, w, _) = await LoadAsync(id, s, token);
            if (WorkExecutionProgressPolicy.IsCompleted(a)) return a.CompletionProjectionPending;
            var now = DateTime.UtcNow;
            var facts = await WorkExecutionReader.ReadAsync(_db, a, w, now, token, s);
            if (w.CompletionReviewPeriodId != null &&
                await _db.WorkReportPeriods.Find(s, p => p.Id == w.CompletionReviewPeriodId && p.WorkAssignmentId == a.Id).AnyAsync(token) &&
                await WorkReopenHoldAsync(w, token, s)) return a.CompletionProjectionPending;
            if (!facts.CanAutoComplete(now))
            {
                // Reconcile markers left by older hosts only after durable fresh-approval evidence.
                if (a.CompletionReviewPeriodId != null && !facts.ReopenHold)
                {
                    await UpdateAssignmentAsync(s, a, Builders<WorkAssignment>.Update
                        .Set(x => x.CompletionReviewPeriodId, null)
                        .Set(x => x.CompletionProjectionPending, true), null, token);
                    await WorkDirectSourceRevisionFence.IncrementAsync(_db, s, w.Id, token);
                    await RelationshipAsync(s, a, "settle-reopen:" + a.CompletionRevision, token);
                    return true;
                }
                return a.CompletionProjectionPending;
            }
            await UpdateAssignmentAsync(s, a, CompletionUpdate(now, null, "AUTO_REPORTS_AND_DEADLINE")
                .Set(x => x.FirstApprovedAtUtc, a.FirstApprovedAtUtc ?? facts.FirstApprovedAtUtc)
                .Set(x => x.PendingCompletionRequestId, null), null, token);
            if (a.PendingCompletionRequestId != null)
                await Requests.UpdateOneAsync(s, r => r.Id == a.PendingCompletionRequestId && r.State == "PENDING",
                    Builders<WorkAssignmentCompletionRequest>.Update.Set(r => r.State, "AUTO_COMPLETED").Set(r => r.DecidedAtUtc, now), cancellationToken: token);
            await Requests.InsertOneAsync(s, new() {
                Id = ReceiptId(id, "system", "auto:" + a.CompletionRevision), WorkId = w.Id, AssignmentId = id,
                CommandId = "auto:" + a.CompletionRevision, RequesterUserId = "", State = "AUTO_COMPLETED",
                Reason = "Đã duyệt đủ báo cáo cấp hiện tại và đã hết hạn phần việc.", RequestedAtUtc = now, DecidedAtUtc = now
            }, cancellationToken: token);
            await WorkDirectSourceRevisionFence.IncrementAsync(_db, s, w.Id, token);
            await RelationshipAsync(s, a, "auto:" + a.CompletionRevision, token);
            return true;
        }, ct);
        if (needsProjection) await TryConvergeAsync(id, ct);
    }

    public async Task ReconcileWorkAsync(string workId, CancellationToken ct)
    {
        await TryConvergeWorkAsync(workId, ct);
        await _transactions.ExecuteAsync(async (s, token) =>
        {
            var w = await _db.Works.Find(s, x => x.Id == workId && !x.IsDeleted).FirstOrDefaultAsync(token);
            if (w == null || w.CompletedAtUtc.HasValue || w.Status == WorkStatus.S3 || !string.IsNullOrEmpty(w.DynamicFlowRuntimeInstanceId)) return;
            if (await WorkReopenHoldAsync(w, token, s)) return;
            var now = DateTime.UtcNow;
            var deadline = WorkExecutionProgressPolicy.EndOfDay(w.DueDate ?? w.EndDate);
            if (!deadline.HasValue || now < deadline) return;
            var roots = await _db.WorkAssignments.Find(s, a => a.WorkId == workId && a.ParentAssignmentId == null && a.IsActive && !a.IsDeleted).ToListAsync(token);
            if (roots.Count == 0 || roots.Any(a => !string.IsNullOrEmpty(a.FlowInstanceId))) return;
            foreach (var a in roots)
            {
                var facts = await WorkExecutionReader.ReadAsync(_db, a, w, now, token, s);
                if (!facts.AllReportsApproved || facts.ReopenHold) return;
            }
            var result = await _db.Works.UpdateOneAsync(s, x => x.Id == w.Id && x.UpdatedAtUtc == w.UpdatedAtUtc && x.CompletedAtUtc == null,
                Builders<Work>.Update.Set(x => x.Status, WorkStatus.S3).Set(x => x.CompletedAtUtc, now)
                    .Set(x => x.CompletedDate, LocalDay(now)).Set(x => x.CompletedByUserId, null)
                    .Set(x => x.CompletionReviewPeriodId, null).Set(x => x.CompletionProjectionPending, true)
                    .Set(x => x.CompletionMode, "AUTO_REPORTS_AND_DEADLINE").Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.CompletionReason, "Đã duyệt đủ báo cáo cấp hiện tại và đã hết hạn công việc.")
                    .Inc(x => x.CompletionRevision, 1).Inc(x => x.DirectSourceRevision, 1), cancellationToken: token);
            if (result.ModifiedCount != 1) throw Conflict("WORK_COMPLETION_STALE");
            await _db.WorkHistories.InsertOneAsync(s, new() {
                Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(), WorkId = w.Id, Type = WorkHistoryType.UPDATED,
                AtUtc = now, ByUserId = null!, CreatedAtUtc = now, UpdatedAtUtc = now,
                Data = new() { ["action"] = "WORK_AUTO_COMPLETED", ["reason"] = "Đã duyệt đủ báo cáo cấp hiện tại và đã hết hạn công việc." }
            }, cancellationToken: token);
        }, ct);
        await TryConvergeWorkAsync(workId, ct);
        await _sync.RebuildWorkSnapshotsAsync(workId, ct);
    }

    public async Task TryConvergeAsync(string id, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
            var a = await _db.WorkAssignments.Find(x => x.Id == id && !x.IsDeleted).FirstOrDefaultAsync(ct);
            if (a == null) return;
            var actor = await ReadProjectionActorAsync(a, ct);
            if (a.IsActive)
            {
                if (WorkExecutionProgressPolicy.IsCompleted(a))
                {
                    await _queue.DisableByAssignmentAsync(id, actor, ct);
                    await _materialize.DisableByAssignmentIdAsync(id, actor, ct);
                }
                else if (a.CompletionReviewPeriodId is { } periodId)
                {
                    var period = await _db.WorkReportPeriods.Find(p => p.Id == periodId && !p.IsDeleted).FirstOrDefaultAsync(ct);
                    if (period != null) await _queue.UpsertPeriodAsync(period, actor, ct);
                    // Existing period reopened for review; do not create new obligations under a closed parent.
                    if (await WorkExecutionScopeGuard.IsOpenAsync(_db, a, null, ct))
                        await RestoreOpenSubtreeAsync(a, actor!, ct);
                }
            }
            await _sync.SyncFromAssignmentAsync(id, ct);
            // Doc-role projection explicitly normalizes its system label to a null audit ObjectId.
            await _projection.RebuildAssignmentAsync(id, actor ?? "system", ct);
            // Review summaries take their execution status from period read models.
            // Refresh every existing period after completion/reopen, not just the
            // selected report; a later period must not retain a stale Completed label.
            var periods = await _db.WorkReportPeriods.Find(p => p.WorkAssignmentId == id && !p.IsDeleted)
                .Project<WorkReportPeriod>(new MongoDB.Bson.BsonDocument("_id", 1)).ToListAsync(ct);
            foreach (var existingPeriod in periods)
                await _projection.RebuildReportPeriodAsync(existingPeriod.Id, actor ?? "system", ct);
            var settled = await _db.WorkAssignments.UpdateOneAsync(x => x.Id == id && x.CompletionRevision == a.CompletionRevision && x.IsActive == a.IsActive,
                Builders<WorkAssignment>.Update.Set(x => x.CompletionProjectionPending, false), cancellationToken: ct);
            if (settled.MatchedCount == 1) return;
            // A completion/reopen raced these side effects. Rebuild again from the new state.
            }
            throw new InvalidOperationException("COMPLETION_PROJECTION_DID_NOT_CONVERGE");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Completion committed; projection pending for {AssignmentId}", id);
        }
    }

    private async Task<string?> ReadProjectionActorAsync(WorkAssignment a, CancellationToken ct)
    {
        // Read the committed business actor, not UpdatedByUserId (snapshot jobs can overwrite it).
        // This also recovers pending completions written before the actor serialization fix.
        string? actor = a.CompletedByUserId;
        if (!WorkExecutionProgressPolicy.IsCompleted(a))
        {
            if (a.CompletionReviewPeriodId == null) return null;
            var receipt = await Requests.Find(r => r.AssignmentId == a.Id && r.State == "REOPENED" &&
                r.DecidedAtUtc == a.CompletionReopenedAtUtc && r.DecisionReason == a.CompletionReviewPeriodId)
                .FirstOrDefaultAsync(ct);
            actor = receipt?.DecidedByUserId;
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidOperationException("COMPLETION_REOPEN_AUDIT_ACTOR_MISSING");
        }
        // Autonomous completion uses BSON null, never a fabricated user or the label "system".
        if (actor != null && !MongoDB.Bson.ObjectId.TryParse(actor, out _))
            throw new InvalidOperationException("COMPLETION_AUDIT_ACTOR_INVALID");
        return actor;
    }

    private async Task<(WorkAssignment, Work, WorkAssignment?)> LoadAsync(string id, IClientSessionHandle? s, CancellationToken ct)
    {
        var a = await (s == null ? _db.WorkAssignments.Find(x => x.Id == id && !x.IsDeleted) : _db.WorkAssignments.Find(s, x => x.Id == id && !x.IsDeleted)).FirstOrDefaultAsync(ct)
            ?? throw NotFound();
        if (!a.IsActive || !string.IsNullOrEmpty(a.FlowInstanceId)) throw Invalid("Phần việc không còn hiệu lực hoặc thuộc luồng riêng.");
        var w = await (s == null ? _db.Works.Find(x => x.Id == a.WorkId && !x.IsDeleted) : _db.Works.Find(s, x => x.Id == a.WorkId && !x.IsDeleted)).FirstOrDefaultAsync(ct)
            ?? throw NotFound();
        var parent = a.ParentAssignmentId == null ? null : await (s == null
            ? _db.WorkAssignments.Find(x => x.Id == a.ParentAssignmentId && !x.IsDeleted)
            : _db.WorkAssignments.Find(s, x => x.Id == a.ParentAssignmentId && !x.IsDeleted)).FirstOrDefaultAsync(ct);
        return (a, w, parent);
    }

    private async Task UpdateAssignmentAsync(IClientSessionHandle s, WorkAssignment a, UpdateDefinition<WorkAssignment> update, string? actor, CancellationToken ct)
    {
        var f = Builders<WorkAssignment>.Filter;
        var revision = a.CompletionRevision == 0 ? f.Eq(x => x.CompletionRevision, 0) | f.Exists(x => x.CompletionRevision, false) : f.Eq(x => x.CompletionRevision, a.CompletionRevision);
        var result = await _db.WorkAssignments.UpdateOneAsync(s,
            f.Eq(x => x.Id, a.Id) & f.Eq(x => x.IsDeleted, false) & f.Eq(x => x.IsActive, true) & revision,
            update.Inc(x => x.CompletionRevision, 1).Set(x => x.UpdatedAtUtc, DateTime.UtcNow).Set(x => x.UpdatedByUserId, actor), cancellationToken: ct);
        if (result.ModifiedCount != 1) throw Conflict("COMPLETION_REVISION_CONFLICT");
    }

    private Task RelationshipAsync(IClientSessionHandle s, WorkAssignment a, string command, CancellationToken ct) =>
        AggregateHostIntegration.RelationshipAsync(_db, s, a.WorkId, [a.Id], "COMPLETION:" + a.Id + ":" + command, ct, preserveIdentity: true);

    private static UpdateDefinition<WorkAssignment> CompletionUpdate(DateTime now, string? actor, string mode) =>
        Builders<WorkAssignment>.Update.Set(x => x.CompletedAtUtc, now).Set(x => x.CompletedDate, LocalDay(now))
            .Set(x => x.CompletedByUserId, actor).Set(x => x.CompletionMode, mode).Set(x => x.ProgressStatus, 2)
            .Set(x => x.ProgressStatusUpdatedAtUtc, now).Set(x => x.HasOverduePeriod, false)
            .Set(x => x.CompletionReviewPeriodId, null).Set(x => x.CompletionProjectionPending, true);
    private static DateTime LocalDay(DateTime now) => WorkCompletionDate.FromUtc(now);
    private static string ReceiptId(string id, string actor, string command) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + actor + "\n" + command)))[..24].ToLowerInvariant();
    private static void ValidateCommand(string command, long? revision)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 128 || !revision.HasValue || revision < 0) throw Invalid("Thiếu mã yêu cầu hoặc phiên bản. Vui lòng tải lại.");
    }
    private static string RequireReason(string? reason) => string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 4000
        ? throw Invalid("Nhập lý do (tối đa 4.000 ký tự).") : reason.Trim();
    private static void EnsureRevision(WorkAssignment a, long? revision) { if (a.CompletionRevision != revision) throw Conflict("COMPLETION_REVISION_CONFLICT"); }
    private static AppException Invalid(string message) => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, message: message);
    private static AppException NotFound() => AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND);
    private static AppException Forbidden() => AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_COMPLETION_FORBIDDEN);
    private static AppException Conflict(string reason) => AppExceptionFactory.Create(AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
        new { reason }, "Dữ liệu hoàn thành đã thay đổi. Vui lòng tải lại để đối chiếu.");
}
