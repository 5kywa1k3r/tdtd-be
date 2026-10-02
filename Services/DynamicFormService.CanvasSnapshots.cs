using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    public async Task<DynamicFormSnapshotAssessment> GetSnapshotAssessmentAsync(string id, string expectedStorageSha256,
        string kind, string snapshotId, string? refreshId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        if (!RoleGuard.IsSystemAdmin(me)) throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN);
        var ownerId = NormalizeDynamicFormTemplateId(id);
        if (!CanvasStoredDependencyInspector.Sha(expectedStorageSha256) || !CanvasStoredDependencyInspector.Sha(snapshotId)
            || kind is not ("BASIC" or "ADVANCED")
            || refreshId is not null && (!ObjectId.TryParse(refreshId, out var parsed) || parsed.ToString() != refreshId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "EXACT_SNAPSHOT_LOCATOR_REQUIRED" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: timeout.Token);
        session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
        try
        {
            var owner = await _ctx.Db.GetCollection<BsonDocument>(_ctx.DynamicFormTemplates.CollectionNamespace.CollectionName)
                .Find(session, new BsonDocument("_id", ObjectId.Parse(ownerId))).FirstOrDefaultAsync(timeout.Token)
                ?? throw DynamicFormNotFound(ownerId);
            RequireStorageDiagnosticAccess(owner, ownerId, me);
            var storageHash = ReferenceHash(owner.ToBson());
            if (storageHash != expectedStorageSha256) throw AppExceptionFactory.Create(AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { reason = "CANVAS_STORAGE_DIAGNOSTIC_CHANGED" });
            var state = "SNAPSHOT_NOT_FOUND"; string? snapshotHash = null, refreshHash = null;
            try
            {
                // Existing strict readers retain the original envelope and never repair or recalculate it.
                if (kind == "BASIC")
                {
                    var stored = await new BasicNativeSnapshotStore(_ctx.Db).ReadAsync(snapshotId, timeout.Token, session);
                    if (stored is not null)
                    {
                        var s = stored.Value.Snapshot;
                        state = s.Legacy?.Meta?.DynamicFormTemplateId == ownerId ? "STORED_ENVELOPE_MATCH" : "FORM_MISMATCH";
                        snapshotHash = stored.Value.Hash;
                        if (state == "STORED_ENVELOPE_MATCH" && refreshId is not null)
                        {
                            var refresh = await new BasicNativeRefreshStore(_ctx.Db).TryReadAsync(refreshId, timeout.Token, session);
                            if (refresh is null) state = "REFRESH_NOT_FOUND";
                            else
                            {
                                refreshHash = ReferenceHash(refresh.Row.ToBson());
                                var r = refresh.Intent.Request;
                                if (r.SnapshotId != snapshotId || r.ScopeAssignmentId != s.ScopeAssignmentId || refresh.Intent.ActorId != s.ActorId
                                    || r.DynamicFormTemplateId != ownerId
                                    || refresh.State == "COMPLETED" && refresh.Row["snapshotHash"].AsString != snapshotHash) state = "REFRESH_MISMATCH";
                            }
                        }
                    }
                }
                else
                {
                    var stored = await new AdvancedNativeSnapshotStore(_ctx.Db).ReadAsync(snapshotId, timeout.Token, session);
                    if (stored is not null)
                    {
                        var s = stored.Value.Snapshot;
                        state = s.TemplateId == ownerId ? "STORED_ENVELOPE_MATCH" : "FORM_MISMATCH";
                        snapshotHash = stored.Value.Hash;
                        if (state == "STORED_ENVELOPE_MATCH" && refreshId is not null)
                        {
                            var refresh = await new AdvancedNativeRefreshStore(_ctx.Db).TryReadAsync(refreshId, timeout.Token, session);
                            if (refresh is null) state = "REFRESH_NOT_FOUND";
                            else
                            {
                                refreshHash = ReferenceHash(refresh.Row.ToBson()); var r = refresh.Intent.Request;
                                if (r.SnapshotId != snapshotId || r.ScopeAssignmentId != s.ScopeAssignmentId || refresh.Intent.ActorId != s.ActorId
                                    || r.DynamicFormTemplateId != ownerId || r.SectionId != s.SectionId || r.Grain != s.Grain || r.GrainKey != s.GrainKey
                                    || refresh.State == "COMPLETED" && refresh.Row["snapshotHash"].AsString != snapshotHash) state = "REFRESH_MISMATCH";
                            }
                        }
                    }
                }
            }
            catch (Exception error) when (error is AppException or JsonException or FormatException or InvalidOperationException
                or ArgumentException or KeyNotFoundException or OverflowException)
            { state = "STORED_DATA_INVALID"; snapshotHash = null; refreshHash = null; }
            string[] gaps = ["EXPLICIT_LOCATOR_ONLY", "ORPHAN_AND_OTHER_SNAPSHOTS", "SEMANTIC_AND_CURRENT_ACL_NOT_REVALIDATED",
                "LABEL_HISTORY_HASH_NOT_RECOMPUTED", "FUTURE_WRITER_CONCURRENCY_FENCE"];
            var digest = ReferenceHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { ownerId, storageHash, kind, snapshotId, refreshId, state, snapshotHash, refreshHash, gaps })));
            return new("dynamic-form-snapshot-assessment", 1, ownerId, storageHash, digest, kind, snapshotId, refreshId,
                "SNAPSHOT", true, false, false, state, snapshotHash, refreshHash, gaps);
        }
        finally { if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None); }
    }
}
