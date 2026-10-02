using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    private sealed record ReferenceSource(string Key, string Collection, string[] Paths, FilterDefinition<BsonDocument> Filter);
    private const int ReferenceDocumentLimit = 100;
    private const long ReferenceByteBudget = 8 * 1024 * 1024;
    private static string ReferenceHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public async Task<DynamicFormReferenceAssessment> GetReferenceAssessmentAsync(string id, string expectedStorageSha256, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var ownerId = NormalizeDynamicFormTemplateId(id);
        if (expectedStorageSha256 is null || !Regex.IsMatch(expectedStorageSha256, "\\A[a-f0-9]{64}\\z"))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "STORAGE_HASH_REQUIRED" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: timeout.Token);
        // Read-only snapshot, deliberately not the P8 mutation runner/catalog gate.
        // A standalone server must fail; never silently mix independent read times.
        session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary));
        try
        {
            var collection = _ctx.Db.GetCollection<BsonDocument>(_ctx.DynamicFormTemplates.CollectionNamespace.CollectionName);
            var stored = await collection.Find(session, new BsonDocument("_id", ObjectId.Parse(ownerId)))
                .FirstOrDefaultAsync(timeout.Token) ?? throw DynamicFormNotFound(ownerId);
            RequireStorageDiagnosticAccess(stored, ownerId, me);
            var storageHash = ReferenceHash(stored.ToBson());
            if (!string.Equals(storageHash, expectedStorageSha256, StringComparison.Ordinal))
                throw AppExceptionFactory.Create(AppErrorCode.STAT_CONFIG_CAS_CONFLICT, new { reason = "CANVAS_STORAGE_DIAGNOSTIC_CHANGED" });
            var groups = new List<DynamicFormReferenceGroup>();
            long remaining = ReferenceByteBudget;
            foreach (var source in ReferenceSources(stored, ownerId))
            {
                if (remaining <= 0) { groups.Add(new(source.Key, source.Paths, 0, "NOT_SCANNED", null)); continue; }
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var observed = 0; var truncated = false;
                var options = new FindOptions<BsonDocument> { Sort = new BsonDocument("_id", 1), BatchSize = 1,
                    Limit = ReferenceDocumentLimit + 1, MaxTime = TimeSpan.FromSeconds(3) };
                using var cursor = await _ctx.Db.GetCollection<BsonDocument>(source.Collection)
                    .FindAsync(session, source.Filter, options, timeout.Token);
                while (await cursor.MoveNextAsync(timeout.Token))
                {
                    foreach (var row in cursor.Current)
                    {
                        observed++;
                        var bytes = row.ToBson();
                        digest.AppendData(SHA256.HashData(bytes)); // fixed-length hashes preserve ordered document boundaries
                        remaining -= bytes.Length;
                        if (observed > ReferenceDocumentLimit || remaining <= 0) { truncated = true; break; }
                    }
                    if (truncated) break;
                }
                groups.Add(new(source.Key, source.Paths, observed, truncated ? "TRUNCATED" : "COMPLETE",
                    Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()));
            }
            var published = stored.GetValue("isPublished", BsonNull.Value) == BsonBoolean.True
                || new[] { "publishedSchemaSnapshotJson", "publishedSchemaHash" }.Any(key => stored.TryGetValue(key, out var v) && !v.IsBsonNull);
            string[] gaps = ["OPAQUE_JSON_AND_BINARY_REFERENCES", "LABEL_PIN_TARGET_VALIDATION", "FLOW_AND_RECONCILIATION_REFERENCES",
                "DERIVED_READ_MODELS_AND_EXTERNAL_CONSUMERS", "FUTURE_WRITER_CONCURRENCY_FENCE"];
            var assessmentHash = ReferenceHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { version = 1, ownerId, storageHash, published, groups, gaps })));
            return new("dynamic-form-reference-assessment", 1, ownerId, storageHash, assessmentHash,
                "SNAPSHOT", true, false, false, published, groups, gaps);
        }
        finally
        {
            // Nothing is committed, including on timeout/auth/drift failure.
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None);
        }
    }

    private IReadOnlyList<ReferenceSource> ReferenceSources(BsonDocument owner, string id)
    {
        var ids = new BsonArray { ObjectId.Parse(id), id };
        FilterDefinition<BsonDocument> Match(string[] paths, BsonArray values) => new BsonDocument("$or",
            new BsonArray(paths.Select(path => new BsonDocument(path, new BsonDocument("$in", values)))));
        ReferenceSource Source<T>(string key, IMongoCollection<T> collection, params string[] paths)
            => new(key, collection.CollectionNamespace.CollectionName, paths, Match(paths, ids));
        var family = new BsonArray(ids);
        if (owner.TryGetValue("familyId", out var familyId) && (familyId.IsObjectId || familyId.IsString))
        {
            family.Add(familyId);
            if (ObjectId.TryParse(familyId.ToString(), out var parsed)) { family.Add(parsed); family.Add(parsed.ToString()); }
        }
        var lineage = Match(["previousVersionId", "clonedFromVersionId"], ids)
            | Match(["familyId", "_id"], family);
        return [
            new("lineage", _ctx.DynamicFormTemplates.CollectionNamespace.CollectionName,
                ["previousVersionId", "clonedFromVersionId", "familyId", "_id"], lineage & Builders<BsonDocument>.Filter.Ne("_id", ObjectId.Parse(id))),
            Source("sections", _ctx.DynamicFormSections, "dynamicFormTemplateId"),
            Source("assignments", _ctx.WorkAssignments, "dynamicFormTemplateId"),
            Source("reports", _ctx.WorkAssignmentReports, "dynamicFormTemplateId"),
            Source("reportSections", _ctx.WorkAssignmentReportSections, "dynamicFormTemplateId"),
            Source("periods", _ctx.WorkReportPeriods, "dynamicFormTemplateId"),
            Source("assignees", _ctx.WorkTemplateAssignees, "dynamicFormTemplateId"),
            Source("aggregateConfigs", _ctx.WorkAssignmentAggregateConfigs, "sourceDynamicFormTemplateId", "targetDynamicFormTemplateId"),
            Source("basicConfigs", _ctx.WorkAssignmentBasicSummaryConfigs, "dynamicFormTemplateId"),
            Source("basicSnapshots", _ctx.WorkAssignmentBasicSummarySnapshots, "dynamicFormTemplateId"),
            Source("advancedConfigs", _ctx.WorkAssignmentAdvancedSummaryConfigs, "dynamicFormTemplateId"),
            Source("advancedDays", _ctx.WorkAssignmentAdvancedSummaryDayNodes, "dynamicFormTemplateId"),
            Source("advancedMonths", _ctx.WorkAssignmentAdvancedSummaryMonthNodes, "dynamicFormTemplateId"),
            Source("advancedYears", _ctx.WorkAssignmentAdvancedSummaryYearNodes, "dynamicFormTemplateId"),
            Source("fieldValues", _ctx.WorkReportFieldStatValues, "dynamicFormTemplateId", "directProjection.dynamicFormTemplateId"),
            Source("fieldAggregates", _ctx.WorkReportFieldStatAggregates, "dynamicFormTemplateId", "directProjection.dynamicFormTemplateId"),
            Source("tableValues", _ctx.WorkReportTableStatValues, "dynamicFormTemplateId", "directProjection.dynamicFormTemplateId"),
            Source("tableAggregates", _ctx.WorkReportTableStatAggregates, "dynamicFormTemplateId", "directProjection.dynamicFormTemplateId"),
            Source("labelValues", _ctx.WorkReportLabelStatValues, "dynamicFormTemplateId", "directProjection.dynamicFormTemplateId"),
            Source("labelAggregates", _ctx.WorkReportLabelStatAggregates, "dynamicFormTemplateId", "directProjection.dynamicFormTemplateId"),
            Source("rebuildJobs", _ctx.WorkReportStatisticRebuildJobs, "dynamicFormTemplateId"),
            new("p8Receipts", _ctx.StatConfigCommandReceipts.CollectionNamespace.CollectionName, ["ownerKind", "ownerId"],
                Match(["ownerId"], ids) & Builders<BsonDocument>.Filter.Eq("ownerKind", "DYNAMIC_FORM"))
        ];
    }
}
