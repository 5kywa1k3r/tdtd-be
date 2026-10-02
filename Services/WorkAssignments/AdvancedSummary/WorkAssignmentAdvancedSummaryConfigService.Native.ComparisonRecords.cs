using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using MongoDB.Driver;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    public async Task<AdvancedNativeComparisonRecordDiagnostic> DiagnoseNativeComparisonRecordAsync(string id, string hash, CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        // Reuse current actor/root/child ACL checks; never expose an unverified locator.
        var authorized = await ReadNativeComparisonRecordAsync(id, hash, token);
        bool found;
        using (var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: token)) {
            session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
            try {
                var record = await new AdvancedNativeComparisonRecordStore(_ctx.Db).ReadAsync(id, hash, token, session)
                    ?? throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_RECORD_NOT_FOUND");
                if (record.ActorId != authorized.ActorId) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_RECORD_NOT_FOUND");
                var o = record.Observation;
                found = await new NativeStorageReferenceStore(_ctx.Db).VerifyAsync(session,
                    new("ADVANCED_COMPARISON_RECORD", id, o.DynamicFormTemplateId, o.SnapshotId, o.ScopeAssignmentId,
                        record.ActorId, hash, o.SectionId, o.Grain, o.GrainKey), token);
            } finally { if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None); }
        }
        _ = await ReadNativeComparisonRecordAsync(id, hash, token);
        return new(1, id, hash, "SELECTED_RECORD_ONLY", "SNAPSHOT", found ? "VERIFIED" : "MISSING_COVERAGE_UNKNOWN", false, false);
    }

    public async Task<AdvancedNativeComparisonRecordResponse> ReadNativeComparisonRecordAsync(string id, string hash, CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        RequireNativeAdvancedResultGate();
        var actor = await LoadNativeAdvancedActorAsync(_me.RequireMe().Id, null, ct);
        var store = new AdvancedNativeComparisonRecordStore(_ctx.Db);
        var record = await store.ReadAsync(id, hash, ct);
        if (record is null || record.ActorId != actor.Me.Id) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_RECORD_NOT_FOUND");
        async Task Access()
        {
            var current = await LoadNativeAdvancedActorAsync(actor.Me.Id, actor.Hash, ct);
            _ = await P805LoadAuthorizedAssignmentAsync(null, record.Observation.ScopeAssignmentId, current.Me, true, ct);
            foreach (var assignment in record.AssignmentIds) _ = await P805LoadAuthorizedAssignmentAsync(null, assignment, current.Me, false, ct);
        }
        await Access();
        _ = await store.ReadAsync(id, hash, ct) ?? throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_RECORD_NOT_FOUND");
        RequireNativeAdvancedResultGate(); await Access();
        return new(1, record.Id, hash, record.ActorId, record.RecordedAtUtc, "HISTORICAL_OBSERVATION", record.Observation);
    }
}
