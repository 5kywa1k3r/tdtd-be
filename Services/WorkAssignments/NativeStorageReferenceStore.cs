using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments;

// Derived immutable locators only. Never claims coverage of pre-existing storage.
internal sealed class NativeStorageReferenceStore(IMongoDatabase db)
{
    internal const string CollectionName = "canvas_native_storage_references_v1";
    internal sealed record Binding(string SourceKind, string SourceId, string DynamicFormTemplateId,
        string SnapshotId, string ScopeAssignmentId, string ActorId, string SourceContentSha256,
        string? SectionId = null, string? Grain = null, string? GrainKey = null);

    internal async Task EnsureAsync(IClientSessionHandle session, Binding binding, CancellationToken ct)
    {
        if (!session.IsInTransaction) throw Invalid("TRANSACTION_REQUIRED");
        var row = Build(binding);
        var rows = db.GetCollection<BsonDocument>(CollectionName);
        var previous = await rows.Find(session, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct);
        if (previous is not null)
        {
            if (!previous.Equals(row)) throw Invalid("IMMUTABLE_CONFLICT");
            return;
        }
        await rows.InsertOneAsync(session, row, cancellationToken: ct);
    }
    internal async Task<bool> VerifyAsync(IClientSessionHandle session, Binding binding, CancellationToken ct)
    {
        if (!session.IsInTransaction) throw Invalid("TRANSACTION_REQUIRED");
        var expected = Build(binding);
        var row = await db.GetCollection<BsonDocument>(CollectionName).Find(session, new BsonDocument("_id", expected["_id"])).SingleOrDefaultAsync(ct);
        if (row is null) return false;
        if (!row.Equals(expected)) throw Invalid("INTEGRITY");
        return true;
    }
    private static BsonDocument Build(Binding binding)
    {
        static bool Id(string? value) => ObjectId.TryParse(value, out var id) && id.ToString() == value;
        static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        var refresh = binding.SourceKind is "BASIC_REFRESH" or "ADVANCED_REFRESH";
        var comparison = binding.SourceKind == "ADVANCED_COMPARISON_RECORD";
        var advanced = binding.SourceKind is "ADVANCED_SNAPSHOT" or "ADVANCED_REFRESH" || comparison;
        if (binding.SourceKind is not ("BASIC_SNAPSHOT" or "ADVANCED_SNAPSHOT" or "BASIC_REFRESH" or "ADVANCED_REFRESH" or "ADVANCED_COMPARISON_RECORD")
            || !Id(binding.DynamicFormTemplateId) || !Id(binding.ScopeAssignmentId) || !Id(binding.ActorId)
            || !Hash(binding.SnapshotId) || !Hash(binding.SourceContentSha256)
            || (refresh || comparison ? !Id(binding.SourceId) : binding.SourceId != binding.SnapshotId)
            || (advanced ? string.IsNullOrWhiteSpace(binding.SectionId) || binding.Grain is not ("DAY" or "MONTH" or "YEAR" or "RANGE")
                || string.IsNullOrWhiteSpace(binding.GrainKey) : binding.SectionId is not null || binding.Grain is not null || binding.GrainKey is not null))
            throw Invalid("BINDING_INVALID");
        if (binding.Grain == "RANGE")
        {
            try { _ = AdvancedSummary.AdvancedNativeRange.Bounds(binding.GrainKey!); }
            catch (ArgumentException) { throw Invalid("BINDING_INVALID"); }
        }
        return new BsonDocument {
            { "_id", binding.SourceKind + ":" + binding.SourceId }, { "version", 1 },
            { "sourceKind", binding.SourceKind }, { "sourceId", binding.SourceId },
            { "dynamicFormTemplateId", binding.DynamicFormTemplateId }, { "snapshotId", binding.SnapshotId },
            { "refreshId", refresh ? (BsonValue)binding.SourceId : BsonNull.Value },
            { "scopeAssignmentId", binding.ScopeAssignmentId }, { "actorId", binding.ActorId },
            { "sourceContentSha256", binding.SourceContentSha256 },
            { "sectionId", (BsonValue?)binding.SectionId ?? BsonNull.Value },
            { "grain", (BsonValue?)binding.Grain ?? BsonNull.Value }, { "grainKey", (BsonValue?)binding.GrainKey ?? BsonNull.Value },
            { "bindingSha256", StatConfigCanonicalJson.HashObject(new { version = 1, binding }) } };
    }
    private static Exception Invalid(string reason) => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
        new { reason = "NATIVE_STORAGE_REFERENCE_" + reason });
}
