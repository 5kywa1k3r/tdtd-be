using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

internal sealed record BasicNativeSnapshot(int Version, string SnapshotId, string ScopeAssignmentId, string ActorId,
    IReadOnlyList<string> AssignmentIds,
    string DefinitionJson, string FormConfigurationJson, string BasicConfigurationJson, string PlanJson,
    string SourceSetHash, StatConfigIdentity Configuration, WorkAssignmentBasicSummaryResponse Legacy,
    NativeStatisticResultDocument Native);

// Derived immutable captures, never the legacy scalar v9 cache or a Direct job
// artifact. No mutable current pointer: each read resolves/revalidates its inputs.
internal sealed class BasicNativeSnapshotStore(IMongoDatabase db)
{
    internal const string CollectionName = "work_assignment_basic_summary_native_snapshots";
    internal const int MaxBytes = 12 * 1024 * 1024; // Below Mongo's single-document limit.
    private IMongoCollection<BsonDocument> Collection => db.GetCollection<BsonDocument>(CollectionName);

    internal async Task<(BasicNativeSnapshot Snapshot, string Hash)?> ReadAsync(string id, CancellationToken ct,
        IClientSessionHandle? session = null)
    {
        var filter = new BsonDocument("_id", id);
        var row = session is null ? await Collection.Find(filter).SingleOrDefaultAsync(ct)
            : await Collection.Find(session, filter).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        if (row.ElementCount != 5 || !row.Contains("version") || row["version"] != 1
            || !row.TryGetValue("json", out var json) || !json.IsString
            || !row.TryGetValue("hash", out var hash) || !hash.IsString
            || !row.TryGetValue("bytes", out var size) || !size.IsInt32)
            throw Invalid();
        var text = json.AsString;
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxBytes || bytes != size.AsInt32 || StatRunCanonicalJson.HashText(text) != hash.AsString)
            throw Invalid();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var artifact = StatConfigCanonicalJson.DeserializeStrict<BasicNativeSnapshot>(doc.RootElement);
            if (artifact.Version != 1 || artifact.SnapshotId != id || StatConfigCanonicalJson.Canonicalize(artifact) != text)
                throw Invalid();
            return (artifact, hash.AsString);
        }
        catch (JsonException) { throw Invalid(); }
    }

    internal async Task StoreAsync(IClientSessionHandle session, BasicNativeSnapshot artifact, CancellationToken ct)
    {
        if (!session.IsInTransaction) throw Invalid();
        var text = StatConfigCanonicalJson.Canonicalize(artifact);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxBytes) throw tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
            tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "BASIC_NATIVE_SNAPSHOT_QUOTA" });
        var hash = StatRunCanonicalJson.HashText(text);
        var row = new BsonDocument { { "_id", artifact.SnapshotId }, { "version", 1 }, { "json", text }, { "bytes", bytes }, { "hash", hash } };
        var existing = await Collection.Find(session, new BsonDocument("_id", artifact.SnapshotId)).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (!existing.Equals(row)) throw Invalid(); // Never repair damaged/conflicting captures.
        }
        // Concurrent inserts are serialized by _id uniqueness and the caller's
        // transaction retry, together with all source/config/ACL write fences.
        if (existing is null) await Collection.InsertOneAsync(session, row, cancellationToken: ct);
        await new NativeStorageReferenceStore(db).EnsureAsync(session, new("BASIC_SNAPSHOT", artifact.SnapshotId,
            artifact.Legacy?.Meta?.DynamicFormTemplateId!, artifact.SnapshotId, artifact.ScopeAssignmentId, artifact.ActorId, hash), ct);
    }

    private static Exception Invalid() => tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
        tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "BASIC_NATIVE_SNAPSHOT_INTEGRITY" });
}
