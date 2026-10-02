using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    public async Task<DynamicFormArtifactAssessment> GetArtifactAssessmentAsync(string id, string expectedStorageSha256, CancellationToken ct)
    {
        var me = _me.RequireMe();
        // Form ownership is not permission to inspect every assignment's stored result.
        // This storage-only diagnostic is admin-only until complete assignment ACL traversal exists.
        if (!RoleGuard.IsSystemAdmin(me)) throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN);
        var ownerId = NormalizeDynamicFormTemplateId(id);
        if (!CanvasStoredDependencyInspector.Sha(expectedStorageSha256))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "STORAGE_HASH_REQUIRED" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: timeout.Token);
        session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
        try
        {
            var stored = await _ctx.Db.GetCollection<BsonDocument>(_ctx.DynamicFormTemplates.CollectionNamespace.CollectionName)
                .Find(session, new BsonDocument("_id", ObjectId.Parse(ownerId))).FirstOrDefaultAsync(timeout.Token) ?? throw DynamicFormNotFound(ownerId);
            RequireStorageDiagnosticAccess(stored, ownerId, me);
            var storageHash = ReferenceHash(stored.ToBson());
            if (storageHash != expectedStorageSha256) throw AppExceptionFactory.Create(AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { reason = "CANVAS_STORAGE_DIAGNOSTIC_CHANGED" });
            var filter = new BsonDocument("dynamicFormTemplateId", new BsonDocument("$in", new BsonArray { ObjectId.Parse(ownerId), ownerId }));
            using var cursor = await _ctx.Db.GetCollection<BsonDocument>(_ctx.WorkReportStatisticRebuildJobs.CollectionNamespace.CollectionName)
                .FindAsync(session, filter, new FindOptions<BsonDocument> { Sort = new BsonDocument("_id", 1), Limit = 21, BatchSize = 1, MaxTime = TimeSpan.FromSeconds(3) }, timeout.Token);
            var checks = new List<CanvasArtifactCheck>(); long remaining = 8 * 1024 * 1024; var truncated = false;
            while (await cursor.MoveNextAsync(timeout.Token))
            {
                foreach (var row in cursor.Current)
                {
                    if (checks.Count == 20) { truncated = true; break; }
                    var bytes = row.ToBson(); remaining -= bytes.Length;
                    var jobHash = ReferenceHash(bytes); var state = "NO_NATIVE_RECEIPT"; string? artifactHash = null;
                    if (remaining <= 0) { state = "NOT_SCANNED"; truncated = true; }
                    else if (row.TryGetValue("nativeStatisticPublication", out var native) && !native.IsBsonNull)
                    {
                        // Check physical presence/type before the typed model can supply default receipt values.
                        if (native is not BsonDocument receipt || !ReceiptShape(receipt)) state = "RECEIPT_INVALID";
                        else if (receipt["bytes"].AsInt32 > remaining) { state = "NOT_SCANNED"; truncated = true; }
                        else
                        {
                            remaining -= receipt["bytes"].AsInt32;
                            try
                            {
                                var job = BsonSerializer.Deserialize<WorkReportStatisticRebuildJob>(row);
                                if (job.DynamicFormTemplateId != ownerId) throw new FormatException("Owner mismatch");
                                _ = await NativeStatisticPublicationContract.ReadAsync(_ctx.Db, job, timeout.Token, session);
                                state = "STORED_ARTIFACT_MATCH"; artifactHash = receipt["artifactHash"].AsString;
                            }
                            catch (Exception error) when (error is AppException or JsonException or FormatException or InvalidOperationException
                                or ArgumentException or KeyNotFoundException or OverflowException or BsonSerializationException)
                            { state = "ARTIFACT_INTEGRITY_ISSUE"; }
                        }
                    }
                    checks.Add(new(checks.Count, state, jobHash, artifactHash));
                    if (remaining <= 0) break;
                }
                if (checks.Count == 20 || remaining <= 0)
                {
                    // A full page requires one extra row to determine whether the registered job set is exhausted.
                    if (remaining <= 0) break;
                    if (await cursor.MoveNextAsync(timeout.Token) && cursor.Current.Any()) truncated = true;
                    break;
                }
            }
            string[] gaps = ["BASIC_ADVANCED_SNAPSHOT_DISCOVERY", "ORPHAN_ARTIFACTS_AND_REFRESHES",
                "JOB_PUBLICATION_CHAIN_AND_CURRENT_ACL", "LABEL_HISTORY_HASH_NOT_RECOMPUTED", "FUTURE_WRITER_CONCURRENCY_FENCE"];
            var assessment = ReferenceHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 1, ownerId, storageHash, truncated, checks, gaps })));
            return new("dynamic-form-artifact-assessment", 1, ownerId, storageHash, assessment, "SNAPSHOT", true, false, false, truncated, checks, gaps);
        }
        finally { if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None); }
    }

    private static bool ReceiptShape(BsonDocument receipt) => receipt.ElementCount == 6
        && receipt.GetValue("version", BsonNull.Value) is BsonInt32 version && version.Value == 2
        && receipt.GetValue("bytes", BsonNull.Value) is BsonInt32 bytes && bytes.Value > 0
        && receipt.GetValue("chunkCount", BsonNull.Value) is BsonInt32 chunks && chunks.Value is > 0 and <= 384
        && new[] { "artifactHash", "manifestHash", "sourceOrderHash" }.All(key => receipt.TryGetValue(key, out var value)
            && value.IsString && CanvasStoredDependencyInspector.Sha(value.AsString));
}
