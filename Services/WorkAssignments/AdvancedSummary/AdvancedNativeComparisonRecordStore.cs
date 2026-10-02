using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

internal sealed record AdvancedNativeComparisonRecord(int Version, string Id, string ActorId, string ActorHash,
    DateTime RecordedAtUtc, AdvancedNativeComparisonResponse Observation, NativeComparisonIdentity ExpectedIdentity,
    NativeComparisonIdentity ActualIdentity, string ExpectedResultHash, string ActualResultHash, IReadOnlyList<string> AssignmentIds,
    AdvancedNativeComparisonEvidence ExpectedEvidence, AdvancedNativeComparisonEvidence ActualEvidence);
internal sealed record AdvancedNativeComparisonEvidence(string SchemaHash, string PlanContentDigest, string SourceOrderDigest,
    IReadOnlyList<NativeStatisticSourceDto> Sources);

// Versioned native evidence, deliberately not a fabricated P10 generation/metric ledger.
internal sealed class AdvancedNativeComparisonRecordStore(IMongoDatabase db)
{
    internal const string CollectionName = "work_assignment_advanced_native_comparison_records_v1";
    internal const int MaximumBytes = 512 * 1024;
    private IMongoCollection<BsonDocument> Rows => db.GetCollection<BsonDocument>(CollectionName);
    internal async Task<string> AppendAsync(IClientSessionHandle session, AdvancedNativeComparisonRecord record, CancellationToken ct)
    {
        if (!session.IsInTransaction) throw Invalid();
        Validate(record);
        var json = StatConfigCanonicalJson.Canonicalize(record); var size = Encoding.UTF8.GetByteCount(json);
        if (size > MaximumBytes) throw Invalid();
        var hash = StatRunCanonicalJson.HashText(json);
        var row = new BsonDocument { { "_id", record.Id }, { "version", 1 }, { "json", json }, { "bytes", size }, { "hash", hash } };
        var old = await Rows.Find(session, new BsonDocument("_id", record.Id)).SingleOrDefaultAsync(ct);
        if (old is not null && !old.Equals(row)) throw Invalid();
        if (old is null) await Rows.InsertOneAsync(session, row, cancellationToken: ct);
        // Pair the locator with its immutable source in the caller's transaction.
        // Reads never backfill; missing legacy coverage remains explicitly unknown.
        var observation = record.Observation;
        await new NativeStorageReferenceStore(db).EnsureAsync(session, new("ADVANCED_COMPARISON_RECORD", record.Id,
            observation.DynamicFormTemplateId, observation.SnapshotId, observation.ScopeAssignmentId, record.ActorId, hash,
            observation.SectionId, observation.Grain, observation.GrainKey), ct);
        return hash;
    }
    internal async Task<AdvancedNativeComparisonRecord?> ReadAsync(string id, string hash, CancellationToken ct, IClientSessionHandle? session = null)
    {
        if (!Id(id) || !StatRunCanonicalJson.IsCanonicalSha256(hash)) throw Invalid();
        var filter = new BsonDocument("_id", id);
        var row = session is null ? await Rows.Find(filter).SingleOrDefaultAsync(ct)
            : await Rows.Find(session, filter).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        if (row.ElementCount != 5 || !row.TryGetValue("version", out var version) || version != 1
            || !row.TryGetValue("json", out var json) || !json.IsString || !row.TryGetValue("bytes", out var size) || !size.IsInt32
            || !row.TryGetValue("hash", out var stored) || !stored.IsString || stored.AsString != hash
            || size.AsInt32 > MaximumBytes || Encoding.UTF8.GetByteCount(json.AsString) != size.AsInt32
            || StatRunCanonicalJson.HashText(json.AsString) != hash) throw Invalid();
        try {
            using var doc = JsonDocument.Parse(json.AsString);
            var record = StatConfigCanonicalJson.DeserializeStrict<AdvancedNativeComparisonRecord>(doc.RootElement);
            Validate(record);
            if (record.Id != id || StatConfigCanonicalJson.Canonicalize(record) != json.AsString) throw Invalid();
            return record;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException) { throw Invalid(); }
    }
    private static bool Id(string? id) => ObjectId.TryParse(id, out var value) && value.ToString() == id;
    private static void Validate(AdvancedNativeComparisonRecord r)
    {
        var o = r.Observation; var e = r.ExpectedIdentity; var a = r.ActualIdentity;
        if (r.Version != 1 || !Id(r.Id) || !Id(r.ActorId) || !StatRunCanonicalJson.IsCanonicalSha256(r.ActorHash)
            || !StatRunCanonicalJson.IsCanonicalSha256(r.ExpectedResultHash) || !StatRunCanonicalJson.IsCanonicalSha256(r.ActualResultHash)
            || o.Version != 1 || o.Coverage != "ADVANCED_NATIVE_CURRENT" || o.Verdict is not ("MATCH" or "DIFFERENT" or "INCOMPARABLE")
            || o.Differences.Count > 256 || (o.Verdict == "MATCH") != (o.Differences.Count == 0)
            || !StatRunCanonicalJson.IsCanonicalSha256(o.SnapshotId) || !StatRunCanonicalJson.IsCanonicalSha256(o.SnapshotHash)
            || r.RecordedAtUtc < o.CheckedAtUtc || r.AssignmentIds.Count > 1000 || r.AssignmentIds.Any(id => !Id(id))
            || r.AssignmentIds.Distinct(StringComparer.Ordinal).Count() != r.AssignmentIds.Count
            || o.ScopeAssignmentId != e.ScopeAssignmentId || o.DynamicFormTemplateId != e.DynamicFormTemplateId
            || o.SectionId != e.SectionId || o.Grain != e.Grain || o.GrainKey != e.GrainKey || o.SourceSetHash != e.SourceSetHash
            || e.ScopeAssignmentId != a.ScopeAssignmentId || e.DynamicFormTemplateId != a.DynamicFormTemplateId
            || e.SectionId != a.SectionId || e.Grain != a.Grain || e.GrainKey != a.GrainKey) throw Invalid();
        foreach (var evidence in new[] { r.ExpectedEvidence, r.ActualEvidence }) {
            if (!StatRunCanonicalJson.IsCanonicalSha256(evidence.SchemaHash)
                || !StatRunCanonicalJson.IsCanonicalSha256(evidence.PlanContentDigest)
                || !StatRunCanonicalJson.IsCanonicalSha256(evidence.SourceOrderDigest)
                || evidence.Sources.Count > 12000 || evidence.Sources.Any(s => !Id(s.ReportId) || s.PayloadRevision < 1 || !StatRunCanonicalJson.IsCanonicalSha256(s.PayloadHash))
                || evidence.Sources.Select(s => s.ReportId).Distinct(StringComparer.Ordinal).Count() != evidence.Sources.Count) throw Invalid();
        }
    }
    private static Exception Invalid() => tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
        tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "ADVANCED_NATIVE_COMPARISON_RECORD_INTEGRITY" });
}
