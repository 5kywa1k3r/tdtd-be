using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    public async Task<AdvancedNativeComparisonRecordPage> ListNativeComparisonRecordsAsync(string scopeId, string formId, string? after, int limit, CancellationToken ct)
    {
        // Tạm khóa luồng tổng hợp cũ; giữ nguyên triển khai bên dưới.
        LegacyAggregateRetirement.Reject();
        static bool Id(string? value) => ObjectId.TryParse(value, out var id) && id.ToString() == value;
        if (!Id(scopeId) || !Id(formId) || after is not null && !Id(after) || limit is < 1 or > 20)
            throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_LIST_INPUT");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        RequireNativeAdvancedResultGate();
        var actor = await LoadNativeAdvancedActorAsync(_me.RequireMe().Id, null, token);
        async Task Access(AdvancedNativeComparisonRecord? record = null) {
            var current = await LoadNativeAdvancedActorAsync(actor.Me.Id, actor.Hash, token);
            _ = await P805LoadAuthorizedAssignmentAsync(null, scopeId, current.Me, true, token);
            if (record is not null) foreach (var assignment in record.AssignmentIds)
                _ = await P805LoadAuthorizedAssignmentAsync(null, assignment, current.Me, false, token);
        }
        await Access();
        var captured = new List<(AdvancedNativeComparisonRecord Record, string Hash)>();
        bool more;
        using (var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: token)) {
            session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
            try {
                var filter = new BsonDocument { { "sourceKind", "ADVANCED_COMPARISON_RECORD" }, { "dynamicFormTemplateId", formId },
                    { "scopeAssignmentId", scopeId }, { "actorId", actor.Me.Id } };
                if (after is not null) filter.Add("_id", new BsonDocument("$gt", "ADVANCED_COMPARISON_RECORD:" + after));
                var rows = await _ctx.Db.GetCollection<BsonDocument>(NativeStorageReferenceStore.CollectionName)
                    .Find(session, filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(3) }).Sort(new BsonDocument("_id", 1)).Limit(limit + 1).ToListAsync(token);
                more = rows.Count > limit; long bytes = 0;
                foreach (var row in rows.Take(limit)) {
                    if (!row.TryGetValue("sourceId", out var id) || !id.IsString || !row.TryGetValue("sourceContentSha256", out var hash) || !hash.IsString)
                        throw NativeAdvancedError("NATIVE_STORAGE_REFERENCE_INTEGRITY");
                    if (!row.TryGetValue("_id", out var key) || !key.IsString || key.AsString != "ADVANCED_COMPARISON_RECORD:" + id.AsString)
                        throw NativeAdvancedError("NATIVE_STORAGE_REFERENCE_INTEGRITY");
                    var record = await new AdvancedNativeComparisonRecordStore(_ctx.Db).ReadAsync(id.AsString, hash.AsString, token, session)
                        ?? throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_RECORD_NOT_FOUND");
                    var o = record.Observation;
                    if (record.ActorId != actor.Me.Id || o.ScopeAssignmentId != scopeId || o.DynamicFormTemplateId != formId)
                        throw NativeAdvancedError("NATIVE_STORAGE_REFERENCE_INTEGRITY");
                    if (!await new NativeStorageReferenceStore(_ctx.Db).VerifyAsync(session, new("ADVANCED_COMPARISON_RECORD", record.Id,
                        formId, o.SnapshotId, scopeId, record.ActorId, hash.AsString, o.SectionId, o.Grain, o.GrainKey), token))
                        throw NativeAdvancedError("NATIVE_STORAGE_REFERENCE_INTEGRITY");
                    bytes += System.Text.Encoding.UTF8.GetByteCount(StatConfigCanonicalJson.Canonicalize(record));
                    if (bytes > 2 * 1024 * 1024) throw NativeAdvancedError("ADVANCED_NATIVE_COMPARISON_LIST_BUDGET");
                    await Access(record); captured.Add((record, hash.AsString));
                }
            } finally { if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None); }
        }
        RequireNativeAdvancedResultGate(); await Access();
        foreach (var (record, _) in captured) await Access(record);
        return new(1, scopeId, formId, "ACTOR_INDEXED_RECORDS_ONLY_COVERAGE_UNKNOWN", "SNAPSHOT_PER_PAGE",
            more ? captured[^1].Record.Id : null, false, false,
            captured.Select(x => new AdvancedNativeComparisonRecordResponse(1, x.Record.Id, x.Hash, x.Record.ActorId,
                x.Record.RecordedAtUtc, "HISTORICAL_OBSERVATION", x.Record.Observation)).ToList());
    }
}
