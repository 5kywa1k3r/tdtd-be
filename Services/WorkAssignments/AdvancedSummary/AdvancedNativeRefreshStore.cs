using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

internal sealed record AdvancedNativeRefreshIntent(int Version, string RefreshId, string ActorId,
    string ActorHash, AdvancedNativeSummaryRequest Request,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommandHash = null);

// Durable dispatch intent and attempt receipt. Hangfire remains the scheduler.
// Immutable input is hash-checked before every transition; damaged raw is retained.
internal sealed class AdvancedNativeRefreshStore(IMongoDatabase db)
{
    internal const string CollectionName = "work_assignment_advanced_summary_native_refreshes";
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);
    private IMongoCollection<BsonDocument> Rows => db.GetCollection<BsonDocument>(CollectionName);
    internal sealed record Entry(AdvancedNativeRefreshIntent Intent, BsonDocument Row)
    {
        internal string State => Row["state"].AsString;
        internal AdvancedNativeRefreshResponse Response => new(Intent.RefreshId, State, Row["attempt"].AsInt32,
            Intent.Request.SnapshotId!, Optional("snapshotHash"), Optional("backgroundJobId"), Optional("errorCode"),
            Row["updatedAtUtc"].ToUniversalTime());
        private string? Optional(string field) => Row[field].IsBsonNull ? null : Row[field].AsString;
    }

    internal async Task<Entry> CreateAsync(IClientSessionHandle session, AdvancedNativeRefreshIntent intent, CancellationToken ct)
    {
        if (!session.IsInTransaction) throw Error("ADVANCED_NATIVE_REFRESH_TRANSACTION_REQUIRED");
        var json = StatConfigCanonicalJson.Canonicalize(intent);
        var row = new BsonDocument {
            { "_id", intent.RefreshId }, { "json", json }, { "hash", StatRunCanonicalJson.HashText(json) },
            { "state", "QUEUED" }, { "attempt", 0 }, { "leaseToken", BsonNull.Value }, { "leaseUntilUtc", BsonNull.Value },
            { "snapshotHash", BsonNull.Value }, { "backgroundJobId", BsonNull.Value }, { "errorCode", BsonNull.Value },
            { "updatedAtUtc", DateTime.UtcNow } };
        var entry = Decode(row);
        var existing = await TryReadAsync(intent.RefreshId, ct, session);
        if (existing is not null)
        {
            if (existing.Intent.ActorId != intent.ActorId || existing.Intent.CommandHash != intent.CommandHash
                || intent.CommandHash is null && existing.Row["json"].AsString != json)
                throw Error("ADVANCED_NATIVE_REFRESH_COMMAND_CONFLICT");
            entry = existing; // Replay keeps the originally pinned capture and mutable attempt state.
        }
        else await Rows.InsertOneAsync(session, row, cancellationToken: ct);
        var saved = entry.Intent;
        await new NativeStorageReferenceStore(db).EnsureAsync(session, new("ADVANCED_REFRESH", saved.RefreshId,
            saved.Request.DynamicFormTemplateId!, saved.Request.SnapshotId!, saved.Request.ScopeAssignmentId,
            saved.ActorId, entry.Row["hash"].AsString, saved.Request.SectionId, saved.Request.Grain, saved.Request.GrainKey), ct);
        return entry;
    }

    internal async Task<Entry> ReadAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var parsed) || parsed.ToString() != id) throw Error("ADVANCED_NATIVE_REFRESH_NOT_FOUND");
        return await TryReadAsync(id, ct) ?? throw Error("ADVANCED_NATIVE_REFRESH_NOT_FOUND");
    }

    internal async Task<Entry?> TryReadAsync(string id, CancellationToken ct, IClientSessionHandle? session = null)
    {
        var filter = new BsonDocument("_id", id);
        var row = session is null ? await Rows.Find(filter).SingleOrDefaultAsync(ct)
            : await Rows.Find(session, filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(3) }).SingleOrDefaultAsync(ct);
        return row is null ? null : Decode(row);
    }

    private static Entry Decode(BsonDocument row)
    {
        var fields = new[] { "_id", "json", "hash", "state", "attempt", "leaseToken", "leaseUntilUtc",
            "snapshotHash", "backgroundJobId", "errorCode", "updatedAtUtc" };
        if (row.ElementCount != fields.Length || fields.Any(f => !row.Contains(f))
            || !row["_id"].IsString || !row["json"].IsString || !row["hash"].IsString || !row["state"].IsString
            || !row["attempt"].IsInt32 || row["attempt"].AsInt32 < 0 || !row["updatedAtUtc"].IsValidDateTime
            || new[] { "leaseToken", "snapshotHash", "backgroundJobId", "errorCode" }.Any(f => !row[f].IsBsonNull && !row[f].IsString)
            || !row["leaseUntilUtc"].IsBsonNull && !row["leaseUntilUtc"].IsValidDateTime)
            throw Error("ADVANCED_NATIVE_REFRESH_INTEGRITY");
        var json = row["json"].AsString;
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 256 * 1024 || StatRunCanonicalJson.HashText(json) != row["hash"].AsString)
            throw Error("ADVANCED_NATIVE_REFRESH_INTEGRITY");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var intent = StatConfigCanonicalJson.DeserializeStrict<AdvancedNativeRefreshIntent>(doc.RootElement);
            if (intent.Version != 1 || intent.RefreshId != row["_id"].AsString
                || !ObjectId.TryParse(intent.RefreshId, out var id) || id.ToString() != intent.RefreshId
                || !ObjectId.TryParse(intent.ActorId, out var actor) || actor.ToString() != intent.ActorId
                || !StatRunCanonicalJson.IsCanonicalSha256(intent.ActorHash) || intent.Request is null
                || !NativeRefreshCommand.Matches("ADVANCED", intent.ActorId, intent.RefreshId, intent.Request.CommandId, intent.CommandHash)
                || intent.Request.Historical || !StatRunCanonicalJson.IsCanonicalSha256(intent.Request.SnapshotId)
                || StatConfigCanonicalJson.Canonicalize(intent) != json
                || row["state"].AsString is not ("QUEUED" or "RUNNING" or "FAILED" or "COMPLETED")
                || row["state"] == "RUNNING" && (row["leaseToken"].IsBsonNull || row["leaseUntilUtc"].IsBsonNull)
                || row["state"] == "COMPLETED" && (row["snapshotHash"].IsBsonNull || !StatRunCanonicalJson.IsCanonicalSha256(row["snapshotHash"].AsString))
                || row["state"] != "COMPLETED" && !row["snapshotHash"].IsBsonNull)
                throw Error("ADVANCED_NATIVE_REFRESH_INTEGRITY");
            return new(intent, row);
        }
        catch (JsonException) { throw Error("ADVANCED_NATIVE_REFRESH_INTEGRITY"); }
    }

    internal async Task<Entry> ClaimAsync(Entry entry, CancellationToken ct)
    {
        if (entry.State == "COMPLETED") return entry;
        if (entry.State == "RUNNING" && entry.Row["leaseUntilUtc"].ToUniversalTime() > DateTime.UtcNow)
            throw Error("ADVANCED_NATIVE_REFRESH_BUSY");
        var update = Builders<BsonDocument>.Update.Set("state", "RUNNING").Inc("attempt", 1)
            .Set("leaseToken", Guid.NewGuid().ToString("N")).Set("leaseUntilUtc", DateTime.UtcNow + LeaseDuration)
            .Set("errorCode", BsonNull.Value).Set("updatedAtUtc", DateTime.UtcNow);
        var row = await Rows.FindOneAndUpdateAsync(entry.Row, update,
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return row is null ? throw Error("ADVANCED_NATIVE_REFRESH_BUSY") : Decode(row);
    }

    internal async Task DispatchedAsync(Entry entry, string jobId, CancellationToken ct)
    {
        // Dispatch can race a fast worker; never overwrite its state or lease.
        await Rows.UpdateOneAsync(entry.Row, Builders<BsonDocument>.Update.Set("backgroundJobId", jobId), cancellationToken: ct);
    }

    internal async Task FailAsync(Entry lease, string code, CancellationToken ct)
    {
        // A superseded/cancelled attempt must not mark the next attempt failed.
        await Rows.UpdateOneAsync(lease.Row, Builders<BsonDocument>.Update.Set("state", "FAILED")
            .Set("errorCode", code).Set("updatedAtUtc", DateTime.UtcNow), cancellationToken: ct);
    }

    internal async Task CompleteAsync(IClientSessionHandle session, Entry lease, string snapshotHash, CancellationToken ct)
    {
        if (!StatRunCanonicalJson.IsCanonicalSha256(snapshotHash)) throw Error("ADVANCED_NATIVE_REFRESH_INTEGRITY");
        var filter = Builders<BsonDocument>.Filter.And(lease.Row,
            Builders<BsonDocument>.Filter.Gt("leaseUntilUtc", DateTime.UtcNow));
        var result = await Rows.UpdateOneAsync(session, filter, Builders<BsonDocument>.Update.Set("state", "COMPLETED")
            .Set("snapshotHash", snapshotHash).Set("updatedAtUtc", DateTime.UtcNow), cancellationToken: ct);
        if (result.MatchedCount != 1) throw Error("ADVANCED_NATIVE_REFRESH_LEASE_LOST");
    }

    private static Exception Error(string reason) => tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
        tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED, new { reason });
}
