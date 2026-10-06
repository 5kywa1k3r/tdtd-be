using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Short-lived read evidence, never a write/submit entitlement. Stores references and
// counts only; full text remains in the immutable List store. Actor/session are mandatory.
internal sealed class AggregateListReadScopes(MongoDbContext db, IWorkReportPayloadReader payloads)
{
    internal const string Collection = "aggregate_list_read_scopes";
    private IMongoCollection<BsonDocument> Rows => db.Db.GetCollection<BsonDocument>(Collection);
    private sealed record Evidence(AggregatePeriodContextDto Context, AggregateFormPinDto[] Forms,
        string[] Reports, JsonElement References, IReadOnlyDictionary<string, long> Instances,
        AggregateExpectedRevisionsDto? SubmissionRevisions, AggregateSourcePinDto[]? SubmissionSources);

    internal async Task<string?> Issue(AggregatePeriodContextDto context, IReadOnlyList<AggregatePreviewEnvelope> previews,
        string actor, string session, CancellationToken ct, IReadOnlyDictionary<string, long>? savedInstances = null)
    {
        // A diff's "before" may belong to an older, now forbidden source set. It is
        // not covered by the current preview authority and gets no read entitlement.
        var roots = previews.Select(p => JsonSerializer.SerializeToElement(new { p.Preview.Results, p.SourceValues, p.ListOperations }, AggregateCanonical.Json)).ToArray();
        var references = roots.SelectMany(References).DistinctBy(r => r.GetRawText()).ToArray();
        if (references.Length == 0) return null;
        var evidence = new Evidence(context, previews.SelectMany(p => p.SourceValues).Select(v => v.Form).Distinct().ToArray(),
            previews.SelectMany(p => p.Preview.LinkedSources).Select(p => p.ReportId).Distinct().ToArray(),
            JsonSerializer.SerializeToElement(new { references, envelopes = previews.Select(p => new { p.ListOperations }) }, AggregateCanonical.Json),
            savedInstances ?? previews.ToDictionary(p => p.Preview.InstanceId, p => p.Preview.Revisions.InstanceRevision),
            savedInstances == null ? previews.FirstOrDefault()?.Preview.Revisions : null,
            savedInstances == null ? previews.SelectMany(p => p.Preview.LinkedSources).Distinct().ToArray() : null);
        // Old envelopes without source identities cannot acquire a new read entitlement.
        if (evidence.Reports.Length > 0 && evidence.Forms.Length == 0) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        var stamp = await Stamp(evidence, actor, session, ct);
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await Rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
            new() { Name = "aggregate_list_read_expiry", ExpireAfter = TimeSpan.Zero }), cancellationToken: ct);
        await Rows.InsertOneAsync(new BsonDocument { ["_id"] = id, ["actor"] = actor, ["session"] = session,
            ["target"] = AggregateTargetIdentity.Id(context)!, ["stamp"] = stamp, ["expiresAt"] = DateTime.UtcNow.AddMinutes(5),
            ["evidence"] = JsonSerializer.Serialize(evidence, AggregateCanonical.Json) }, cancellationToken: ct);
        return id;
    }
    internal async Task<JsonElement> Authorize(AggregateListPageRequestDto request, string actor, string session, CancellationToken ct)
    {
        var row = await Rows.Find(new BsonDocument { ["_id"] = request.ReadId, ["actor"] = actor, ["session"] = session,
            ["target"] = request.TargetId, ["expiresAt"] = new BsonDocument("$gt", DateTime.UtcNow) }).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
        var evidence = JsonSerializer.Deserialize<Evidence>(row["evidence"].AsString, AggregateCanonical.Json)!;
        if (request.IncludeLineage)
            AggregateCommandService.Allow(AggregateAction.ReadLineage,
                await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(evidence.Context, actor, session, ct));
        if (!References(evidence.References).Any(r => r.GetProperty("id").GetString() == request.SnapshotId
            && r.GetProperty("hash").GetString() == request.SnapshotHash)) throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
        if (row["stamp"].AsString != await Stamp(evidence, actor, session, ct)) throw new AggregatePreviewException("AGG_INPUT_STALE");
        return evidence.References;
    }
    private async Task<string> Stamp(Evidence evidence, string actor, string session, CancellationToken ct)
    {
        var authority = await new AggregateMongoCommandReader(db, payloads, true).AuthorizeAsync(evidence.Context, actor, session, ct);
        AggregateCommandService.Allow(AggregateAction.ReadSource, authority);
        if (evidence.SubmissionRevisions is { } expected && (expected.PayloadRevision != authority.Read.Revisions.PayloadRevision
            || expected.LifecycleRevision != authority.Read.Revisions.LifecycleRevision || expected.TargetSchemaHash != authority.Read.Revisions.TargetSchemaHash))
            throw new AggregatePreviewException("AGG_INPUT_STALE");
        var reader = new AggregateMongoPreviewReader(db, payloads, new AggregateMongoDataWindows(db), true);
        var reports = new HashSet<string>(StringComparer.Ordinal); var sources = new List<object>();
        foreach (var form in evidence.Forms)
        {
            var schema = await reader.ReadSchemaAsync(form, authority.Read, actor, ct);
            // A page entitlement checks actual report identities and authority. It
            // does not calculate expected schedule coverage (including future slots).
            var listing = await reader.ListReportSetSourcesAsync(authority.Read, form, actor, ct);
            if (!listing.Complete || listing.Headers.Any(h => !h.WholeReportReadable) || listing.Slots.Any(s => !s.Readable))
                throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
            foreach (var header in listing.Headers) reports.Add(header.Pin.ReportId);
            if (evidence.SubmissionSources != null && evidence.SubmissionSources.Where(p => listing.Headers.Any(h => h.Pin.ReportId == p.ReportId))
                .Any(p => !listing.Headers.Any(h => h.Pin == p))) throw new AggregatePreviewException("AGG_INPUT_STALE");
            sources.Add(new { schema, listing.MembershipRevision,
                Choices = schema.Members.Values.OrderBy(m => m.Id).Select(m => new { m.Id, m.AllowedChoiceCodes,
                    Fields = m.List?.Fields.Select(f => new { f.Id, f.AllowedChoiceCodes }) }) });
        }
        if (evidence.Reports.Any(id => !reports.Contains(id))) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        var instances = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
            .Find(new BsonDocument("target", AggregateTargetIdentity.Id(evidence.Context))).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var states = instances.Select(r => AggregateMongoTransaction.Read<AggregateInstanceState>(r).Value).ToArray();
        if (evidence.Instances.Any(pin => !states.Any(i => i.Id == pin.Key && i.Revision == pin.Value)))
            throw new AggregatePreviewException("AGG_INPUT_STALE");
        return AggregateCanonical.Hash(new { authority.Read.Context, authority.Read.Revisions, authority.Read.AuthorizationFingerprint,
            authority.Pins, sources, Instances = instances.Select(r => r["body"].AsString).ToArray() });
    }
    private static IEnumerable<JsonElement> References(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && (AggregateListWire.Supported(kind.GetString()) || kind.GetString() == AggregateContentTableStore.Kind))
                yield return node.Clone();
            else foreach (var p in node.EnumerateObject()) foreach (var value in References(p.Value)) yield return value;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) foreach (var value in References(item)) yield return value;
    }
}
