using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    public async Task<DynamicFormDependencyAssessment> GetDependencyAssessmentAsync(string id, string expectedStorageSha256, CancellationToken ct)
    {
        var me = _me.RequireMe(); var ownerId = NormalizeDynamicFormTemplateId(id);
        if (!CanvasStoredDependencyInspector.Sha(expectedStorageSha256))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "STORAGE_HASH_REQUIRED" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: timeout.Token);
        session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
        try
        {
            var stored = await _ctx.Db.GetCollection<BsonDocument>(_ctx.DynamicFormTemplates.CollectionNamespace.CollectionName)
                .Find(session, new BsonDocument("_id", ObjectId.Parse(ownerId))).FirstOrDefaultAsync(timeout.Token)
                ?? throw DynamicFormNotFound(ownerId);
            RequireStorageDiagnosticAccess(stored, ownerId, me);
            var storageHash = ReferenceHash(stored.ToBson());
            if (storageHash != expectedStorageSha256)
                throw AppExceptionFactory.Create(AppErrorCode.STAT_CONFIG_CAS_CONFLICT, new { reason = "CANVAS_STORAGE_DIAGNOSTIC_CHANGED" });
            var inspector = new CanvasStoredDependencyInspector(timeout.Token); inspector.Read(stored);
            var visibility = DynamicFormStatisticConfigCommandService.GetP804LabelVisibilityFilter(me)
                & Builders<LabelCatalogItem>.Filter.Eq(label => label.IsDeleted, false);
            var rawVisibility = visibility.Render(new RenderArgs<LabelCatalogItem>(_ctx.Labels.DocumentSerializer, _ctx.Labels.Settings.SerializerRegistry));
            var labels = _ctx.Db.GetCollection<BsonDocument>(_ctx.Labels.CollectionNamespace.CollectionName);
            var cache = new Dictionary<string, (BsonDocument? Row, string? Hash, bool Limited)>(StringComparer.Ordinal);
            var results = new List<CanvasDependencyPin>(); long remaining = 8 * 1024 * 1024;
            foreach (var pin in inspector.Pins)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!cache.TryGetValue(pin.LabelId, out var target))
                {
                    if (cache.Count >= 100 || remaining <= 0) target = (null, null, true);
                    else
                    {
                        var filter = Builders<BsonDocument>.Filter.And(rawVisibility, new BsonDocument("_id", ObjectId.Parse(pin.LabelId)));
                        var row = await labels.Find(session, filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(3) }).FirstOrDefaultAsync(timeout.Token);
                        var bytes = row?.ToBson(); remaining -= bytes?.Length ?? 0;
                        target = remaining < 0 ? (null, null, true) : (row, bytes is null ? null : ReferenceHash(bytes), false);
                    }
                    cache[pin.LabelId] = target;
                }
                if (target.Limited) { inspector.Limit(pin.Path); results.Add(new(pin.Path, "NOT_SCANNED", null)); continue; }
                // Missing, hidden and deleted targets deliberately share one state; never expose their contents.
                if (target.Row is null) { results.Add(new(pin.Path, "TARGET_UNAVAILABLE", null)); continue; }
                results.Add(new(pin.Path, CompareDependencyPin(pin, target.Row), target.Hash));
            }
            string[] gaps = ["JSON_SYNTAX_IS_NOT_SCHEMA_VALIDATION", "LABEL_HISTORY_HASH_NOT_RECOMPUTED",
                "EXTERNAL_SNAPSHOT_AND_BINARY_REFERENCES", "FLOW_AND_RECONCILIATION_REFERENCES", "FUTURE_WRITER_CONCURRENCY_FENCE"];
            var hash = ReferenceHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { version = 1, ownerId, storageHash, inspector.Truncated, inspector.Slots, pins = results, inspector.Issues, gaps })));
            return new("dynamic-form-dependency-assessment", 1, ownerId, storageHash, hash, "SNAPSHOT", true, false, false,
                inspector.Truncated, inspector.Slots, results, inspector.Issues, gaps);
        }
        finally { if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None); }
    }

    private static string CompareDependencyPin(CanvasStoredDependencyInspector.Pin pin, BsonDocument target)
    {
        static string? Id(BsonValue value) => value.IsObjectId ? value.AsObjectId.ToString() : value.IsString ? value.AsString : null;
        static bool Number(BsonValue value, int expected) => value.IsInt32 && value.AsInt32 == expected || value.IsInt64 && value.AsInt64 == expected;
        if (target.GetValue("versionSnapshots", BsonNull.Value) is not BsonArray versions || versions.Any(v => !v.IsBsonDocument))
            return "TARGET_HISTORY_INVALID";
        var matches = versions.Select(v => v.AsBsonDocument).Where(v => Id(v.GetValue("versionId", BsonNull.Value)) == pin.VersionId).ToArray();
        if (matches.Length != 1) return matches.Length == 0 ? "VERSION_NOT_FOUND" : "VERSION_AMBIGUOUS";
        var version = matches[0];
        if (Id(version.GetValue("labelId", BsonNull.Value)) != pin.LabelId
            || !Number(version.GetValue("versionNo", BsonNull.Value), pin.VersionNo)
            || version.GetValue("configHash", BsonNull.Value) != new BsonString(pin.Hash)) return "PIN_MISMATCH";
        if (pin.Snapshot is { } snapshot)
        {
            foreach (var key in new[] { "code", "dataType", "usage", "scopeType" })
                if (!snapshot.TryGetProperty(key, out var input) || input.ValueKind != JsonValueKind.String
                    || !version.TryGetValue(key, out var value) || !value.IsString || value.AsString != input.GetString()) return "METADATA_MISMATCH";
            if (!snapshot.TryGetProperty("scopeId", out var scope) || !version.TryGetValue("scopeId", out var storedScope)
                || (scope.ValueKind == JsonValueKind.Null ? !storedScope.IsBsonNull
                    : scope.ValueKind != JsonValueKind.String || scope.GetString() != Id(storedScope))) return "METADATA_MISMATCH";
            if (!snapshot.TryGetProperty("isActive", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !version.TryGetValue("isActive", out var storedActive) || !storedActive.IsBoolean || active.GetBoolean() != storedActive.AsBoolean)
                return "METADATA_MISMATCH";
        }
        // Exact stored declaration match only. Historical snapshots omit some inputs used by the original hash.
        return "DECLARATION_MATCH";
    }
}
