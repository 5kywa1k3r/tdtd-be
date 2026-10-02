using Hangfire;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    public async Task<AdvancedNativeRefreshResponse> QueueNativeRefreshAsync(AdvancedNativeSummaryRequest request, CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        RequireNativeAdvancedWorkerGate();
        if (request.Historical) throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_CURRENT_REQUIRED");
        var actor = await LoadNativeAdvancedActorAsync(_me.RequireMe().Id, null, ct);
        var store = new AdvancedNativeRefreshStore(_ctx.Db);
        var commandHash = request.CommandId is null ? null : StatConfigCanonicalJson.HashObject(request);
        var refreshId = request.CommandId is null ? ObjectId.GenerateNewId().ToString()
            : NativeRefreshCommand.Id("ADVANCED", actor.Me.Id, request.CommandId);
        if (request.CommandId is not null && await store.TryReadAsync(refreshId, ct) is { } previous)
        {
            if (previous.Intent.CommandHash != commandHash) throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_COMMAND_CONFLICT");
            _ = await LoadAuthorizedNativeRefreshAsync(refreshId, ct);
            await _statConfigTransactions.ExecuteAsync((session, token) => store.CreateAsync(session, previous.Intent, token), ct);
            return await RetryNativeRefreshAsync(refreshId, ct);
        }
        var capture = await CaptureNativeAdvancedAsync(request, actor.Me, ct);
        if (request.SnapshotId is not null && request.SnapshotId != capture.Key)
            throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_STALE");
        // Resolve omitted template now. Neither a queued worker nor a retry may
        // silently switch Form, config, membership or candidate/catalog.
        var pinned = request with { DynamicFormTemplateId = capture.Owner.Template.Id, SnapshotId = capture.Key };
        var intent = new AdvancedNativeRefreshIntent(1, refreshId, actor.Me.Id, actor.Hash, pinned, commandHash);
        AdvancedNativeRefreshStore.Entry entry;
        try { entry = await _statConfigTransactions.ExecuteAsync((session, token) => store.CreateAsync(session, intent, token), ct); }
        catch (MongoWriteException error) when (commandHash is not null && error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The aborted transaction must not be reused. A concurrent winner is
            // checked in a fresh transaction, including its immutable locator.
            entry = await _statConfigTransactions.ExecuteAsync((session, token) => store.CreateAsync(session, intent, token), ct);
        }
        // A concurrent caller may already have pinned this command. Revalidate
        // that persisted capture; never dispatch a replacement with newer inputs.
        if (request.CommandId is not null) return await RetryNativeRefreshAsync(refreshId, ct);
        await DispatchNativeAdvancedAsync(store, entry, ct);
        return (await store.ReadAsync(entry.Intent.RefreshId, ct)).Response;
    }

    public Task<AdvancedNativeRefreshResponse> ReadNativeRefreshByCommandAsync(string commandId, CancellationToken ct)
    {
        LegacyAggregateRetirement.Reject();
        return ReadNativeRefreshAsync(NativeRefreshCommand.Id("ADVANCED", _me.RequireMe().Id, commandId), ct);
    }

    public async Task<AdvancedNativeRefreshResponse> ReadNativeRefreshAsync(string refreshId, CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        var entry = await LoadAuthorizedNativeRefreshAsync(refreshId, ct);
        // QUEUED/FAILED is still inspectable after source/config drift. COMPLETED
        // is returned only with current capture + exact persisted hash verified.
        if (entry.State == "COMPLETED") await ValidateNativeAdvancedCompletionAsync(entry, ct);
        return entry.Response;
    }

    public async Task<AdvancedNativeRefreshResponse> RetryNativeRefreshAsync(string refreshId, CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        var store = new AdvancedNativeRefreshStore(_ctx.Db);
        var entry = await LoadAuthorizedNativeRefreshAsync(refreshId, ct);
        if (entry.State == "COMPLETED")
        {
            await ValidateNativeAdvancedCompletionAsync(entry, ct);
            return entry.Response;
        }
        var actor = await LoadNativeAdvancedActorAsync(entry.Intent.ActorId, entry.Intent.ActorHash, ct);
        var capture = await CaptureNativeAdvancedAsync(entry.Intent.Request, actor.Me, ct);
        RequireNativeAdvancedIntent(entry, capture);
        if (entry.State == "RUNNING" && entry.Row["leaseUntilUtc"].ToUniversalTime() > DateTime.UtcNow)
            throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_BUSY");
        await DispatchNativeAdvancedAsync(store, entry, ct);
        return (await store.ReadAsync(refreshId, ct)).Response;
    }

    private async Task DispatchNativeAdvancedAsync(AdvancedNativeRefreshStore store, AdvancedNativeRefreshStore.Entry entry, CancellationToken ct)
    {
        // Save intent first: an enqueue exception may be ambiguous. Retrying the
        // same ID dispatches at least once; lease CAS prevents double completion.
        ct.ThrowIfCancellationRequested();
        try
        {
            var id = _backgroundJobs.Enqueue<IWorkAssignmentAdvancedSummaryConfigService>(
                service => service.RefreshNativeSnapshotJobAsync(entry.Intent.RefreshId, CancellationToken.None));
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Dispatch was not acknowledged.");
            await store.DispatchedAsync(entry, id, ct);
        }
        catch (Exception)
        {
            // Expose the durable ID even when enqueue/ack failed: the caller can
            // inspect/retry it instead of creating an unrelated second request.
            throw tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "ADVANCED_NATIVE_REFRESH_DISPATCH_UNCONFIRMED", refreshId = entry.Intent.RefreshId });
        }
    }

    // The last automatic retry outlives a crashed attempt's ten-minute lease.
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 660 })]
    public async Task RefreshNativeSnapshotJobAsync(string refreshId, CancellationToken ct)
    {
        // Job/hook cũ dừng trước mọi I/O, không tạo vòng retry hay thay dữ liệu lịch sử.
        if (LegacyAggregateRetirement.IsDisabled) return;
        RequireNativeAdvancedWorkerGate();
        var store = new AdvancedNativeRefreshStore(_ctx.Db);
        var entry = await store.ReadAsync(refreshId, ct);
        if (entry.State == "COMPLETED")
        {
            await ValidateNativeAdvancedCompletionAsync(entry, ct);
            return;
        }
        var lease = await store.ClaimAsync(entry, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); // Less than the durable lease; no per-cell heartbeat.
        var token = timeout.Token;
        try
        {
            async Task<MeResponse> Actor(CancellationToken cancellation)
                => (await LoadNativeAdvancedActorAsync(entry.Intent.ActorId, entry.Intent.ActorHash, cancellation)).Me;
            var response = await GetNativeAdvancedCurrentAsync(entry.Intent.Request, Actor, token);
            var actor = await LoadNativeAdvancedActorAsync(entry.Intent.ActorId, entry.Intent.ActorHash, token);
            var capture = await CaptureNativeAdvancedAsync(entry.Intent.Request, actor.Me, token);
            RequireNativeAdvancedIntent(entry, capture);
            var stored = await new AdvancedNativeSnapshotStore(_ctx.Db).ReadAsync(capture.Key, token);
            if (stored is null || stored.Value.Hash != response.SnapshotHash)
                throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
            await _statConfigTransactions.ExecuteAsync(async (session, cancellation) =>
            {
                RequireNativeAdvancedWorkerGate();
                var binding = _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.AdvancedResult);
                if (StatConfigCanonicalJson.Canonicalize(binding) != StatConfigCanonicalJson.Canonicalize(capture.Candidate))
                    throw NativeAdvancedError("ADVANCED_NATIVE_CANDIDATE_CHANGED");
                await FenceNativeAdvancedAsync(session, capture, cancellation);
                try
                {
                    await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.Users, actor.Owner,
                        ["_id", "username", "fullName", "unitId", "roles", "positionCode", "accountKind", "isDeleted"], cancellation);
                }
                catch (InvalidOperationException ex) when (ex.Message == "P9_DIRECT_NATIVE_PUBLICATION_SOURCE_FENCE_STALE")
                { throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_ACTOR_CHANGED"); }
                var persisted = await new AdvancedNativeSnapshotStore(_ctx.Db).ReadAsync(capture.Key, cancellation, session);
                if (persisted is null || persisted.Value.Hash != response.SnapshotHash)
                    throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
                await store.CompleteAsync(session, lease, response.SnapshotHash, cancellation);
                return true;
            }, token);
            // Completion is evidence of the exact capture. Recheck after commit
            // for membership additions too; a stale response is never served.
            await ValidateNativeAdvancedCompletionAsync(await store.ReadAsync(refreshId, token), token);
        }
        catch
        {
            // A committed receipt or successor lease cannot be overwritten by
            // this failure path. Keep errors generic (no payload/actor details).
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await store.FailAsync(lease, timeout.IsCancellationRequested ? "ADVANCED_NATIVE_REFRESH_CANCELLED" : "ADVANCED_NATIVE_REFRESH_FAILED", cleanup.Token);
            throw;
        }
    }

    private async Task<AdvancedNativeRefreshStore.Entry> LoadAuthorizedNativeRefreshAsync(string id, CancellationToken ct)
    {
        RequireNativeAdvancedWorkerGate();
        var me = _me.RequireMe();
        var entry = await new AdvancedNativeRefreshStore(_ctx.Db).ReadAsync(id, ct);
        if (entry.Intent.ActorId != me.Id) throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_NOT_FOUND");
        var actor = await LoadNativeAdvancedActorAsync(me.Id, null, ct);
        _ = await P805LoadAuthorizedAssignmentAsync(null, entry.Intent.Request.ScopeAssignmentId, actor.Me, false, ct);
        return entry;
    }

    private async Task ValidateNativeAdvancedCompletionAsync(AdvancedNativeRefreshStore.Entry entry, CancellationToken ct)
    {
        if (entry.State != "COMPLETED") throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_NOT_COMPLETED");
        var stored = await new AdvancedNativeSnapshotStore(_ctx.Db).ReadAsync(entry.Intent.Request.SnapshotId!, ct);
        if (stored is null || stored.Value.Hash != entry.Response.SnapshotHash)
            throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
        async Task<MeResponse> Actor(CancellationToken token)
            => (await LoadNativeAdvancedActorAsync(entry.Intent.ActorId, entry.Intent.ActorHash, token)).Me;
        var response = await GetNativeAdvancedCurrentAsync(entry.Intent.Request, Actor, ct);
        if (response.SnapshotHash != entry.Response.SnapshotHash)
            throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
    }

    private static void RequireNativeAdvancedIntent(AdvancedNativeRefreshStore.Entry entry, NativeAdvancedCapture capture)
    {
        if (entry.Intent.Request.SnapshotId != capture.Key) throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_STALE");
    }

    private void RequireNativeAdvancedWorkerGate()
    {
        _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.AdvancedBuild);
        _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.AdvancedResult);
        _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.CoreJob(StatRunCapabilities.AdvancedSummary));
    }

    private async Task<(MeResponse Me, AppUser Owner, string Hash)> LoadNativeAdvancedActorAsync(string id, string? expectedHash, CancellationToken ct)
    {
        // Background work has no HTTP Me. Read current authorization fields;
        // never persist tokens, use cached roles or synthesize administrator rights.
        var owner = await _ctx.Users.Find(u => u.Id == id && !u.IsDeleted).Project(u => new AppUser {
            Id = u.Id, Username = u.Username, FullName = u.FullName, UnitId = u.UnitId, Roles = u.Roles,
            PositionCode = u.PositionCode, AccountKind = u.AccountKind, IsDeleted = u.IsDeleted }).SingleOrDefaultAsync(ct)
            ?? throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_ACTOR_UNAVAILABLE");
        var hash = StatConfigCanonicalJson.HashObject(new { owner.Id, owner.Username, owner.FullName, owner.UnitId,
            owner.Roles, owner.PositionCode, owner.AccountKind, owner.IsDeleted });
        if (expectedHash is not null && hash != expectedHash) throw NativeAdvancedError("ADVANCED_NATIVE_REFRESH_ACTOR_CHANGED");
        // Advanced access and P805 label visibility use ID/unit/roles/account kind.
        // Unit display/type metadata is not used by this bounded worker path.
        return (new(owner.Id, owner.Username, owner.FullName, [], owner.UnitId ?? "", null, null, null,
            owner.Roles, owner.PositionCode, owner.IsDeleted, owner.AccountKind), owner, hash);
    }
}
