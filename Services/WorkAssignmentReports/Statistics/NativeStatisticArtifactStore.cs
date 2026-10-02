using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal sealed record NativeStatisticStorageLimits(int MaxArtifactBytes, int ChunkBytes, int MaxChunks);
internal sealed record NativeStatisticArtifactReceipt(string RunId, string GenerationId, string ArtifactHash,
    string ManifestHash, int Bytes, int ChunkCount);

// Separate native result storage, not a second config writer, publication ledger,
// or replacement for any existing Direct store. A caller must retain this exact
// receipt in a versioned publication contract before exposing the result.
internal sealed class NativeStatisticArtifactStore(IMongoDatabase database)
{
    private sealed record Manifest(int Version, string RunId, string GenerationId, string ArtifactHash,
        int Bytes, int ChunkBytes, string[] ChunkHashes);
    internal const string CollectionName = "work_report_native_statistic_artifacts";
    private readonly IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(CollectionName);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task<NativeStatisticArtifactReceipt> StageAsync(NativeStatisticGenerationArtifact artifact,
        NativeStatisticStorageLimits limits, CancellationToken ct = default)
    {
        ValidateLimits(limits);
        ValidateIdentity(artifact);
        var canonical = StatConfigCanonicalJson.Canonicalize(artifact);
        var bytes = Utf8.GetBytes(canonical);
        if (bytes.Length > limits.MaxArtifactBytes) throw Invalid("ARTIFACT_BYTES_LIMIT");
        // Binary chunks preserve exact UTF-8 bytes, even at a multi-byte boundary.
        // JSON decoding happens only after full hash/count/order validation.
        var count = checked((int)(((long)bytes.Length + limits.ChunkBytes - 1) / limits.ChunkBytes));
        if (count > limits.MaxChunks) throw Invalid("ARTIFACT_CHUNK_LIMIT");
        var bodyHash = StatRunCanonicalJson.HashText(canonical);
        var chunkHashes = Enumerable.Range(0, count).Select(i => Hash(bytes.AsSpan(i * limits.ChunkBytes,
            Math.Min(limits.ChunkBytes, bytes.Length - i * limits.ChunkBytes)))).ToArray();
        var manifestJson = StatConfigCanonicalJson.Canonicalize(new { version = 2,
            artifact.Generation.RunId, artifact.Generation.GenerationId, artifactHash = bodyHash,
            bytes = bytes.Length, chunkBytes = limits.ChunkBytes, chunkHashes });
        var manifestHash = StatRunCanonicalJson.HashText(manifestJson);
        var headerId = HeaderId(artifact.Generation.RunId, artifact.Generation.GenerationId);
        var header = new BsonDocument { { "_id", headerId }, { "kind", "manifest" }, { "version", 2 },
            { "runId", artifact.Generation.RunId }, { "generationId", artifact.Generation.GenerationId },
            { "manifestJson", manifestJson }, { "manifestHash", manifestHash }, { "state", "STAGING" } };
        // Reserve immutable intent before writing chunks. A retry with different
        // options/sources/limits cannot replace or finish somebody else's partial result.
        await InsertExactAsync(header, allowState: true, ct);
        var receipt = new NativeStatisticArtifactReceipt(artifact.Generation.RunId, artifact.Generation.GenerationId,
            bodyHash, manifestHash, bytes.Length, count);
        var reserved = await collection.Find(new BsonDocument("_id", headerId)).FirstOrDefaultAsync(ct);
        if (reserved?.GetValue("state", "") == "READY")
        {
            // A completed artifact is immutable, including damage. Do not repair
            // missing chunks by replaying the calculator against corrupt history.
            _ = await ReadCoreAsync(receipt, limits, requireReady: true, ct);
            return receipt;
        }
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var part = bytes.AsSpan(i * limits.ChunkBytes, Math.Min(limits.ChunkBytes, bytes.Length - i * limits.ChunkBytes)).ToArray();
            var row = new BsonDocument { { "_id", ChunkId(headerId, i) }, { "kind", "chunk" },
                { "manifestId", headerId }, { "index", i }, { "sha256", chunkHashes[i] }, { "data", new BsonBinaryData(part) } };
            await InsertExactAsync(row, allowState: false, ct);
        }
        _ = await ReadCoreAsync(receipt, limits, requireReady: false, ct);
        var updated = await collection.UpdateOneAsync(header,
            Builders<BsonDocument>.Update.Set("state", "READY"), cancellationToken: ct);
        if (updated.MatchedCount != 1)
        {
            // Exact concurrent replay may already have changed only this state.
            _ = await ReadCoreAsync(receipt, limits, requireReady: true, ct);
        }
        return receipt;
    }

    // Internal read of the exact retained receipt; never a "latest" lookup.
    // The eventual result API must authorize the job/sources before calling this.
    internal Task<NativeStatisticGenerationArtifact> ReadAsync(NativeStatisticArtifactReceipt receipt,
        NativeStatisticStorageLimits limits, CancellationToken ct = default, IClientSessionHandle? session = null)
        => ReadCoreAsync(receipt, limits, requireReady: true, ct, session);

    private async Task<NativeStatisticGenerationArtifact> ReadCoreAsync(NativeStatisticArtifactReceipt receipt,
        NativeStatisticStorageLimits limits, bool requireReady, CancellationToken ct, IClientSessionHandle? session = null)
    {
        ValidateLimits(limits);
        if (!ObjectId.TryParse(receipt.RunId, out var run) || run.ToString() != receipt.RunId
            || !StatRunCanonicalJson.IsCanonicalSha256(receipt.GenerationId)
            || !StatRunCanonicalJson.IsCanonicalSha256(receipt.ManifestHash)
            || !StatRunCanonicalJson.IsCanonicalSha256(receipt.ArtifactHash)
            || receipt.Bytes < 1 || receipt.Bytes > limits.MaxArtifactBytes
            || receipt.ChunkCount < 1 || receipt.ChunkCount > limits.MaxChunks)
            throw Invalid("ARTIFACT_RECEIPT_INVALID");
        var id = HeaderId(receipt.RunId, receipt.GenerationId);
        async Task<BsonDocument?> FindAsync(string key) => session is null
            ? await collection.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            : await collection.Find(session, new BsonDocument("_id", key), new FindOptions { MaxTime = TimeSpan.FromSeconds(3) }).FirstOrDefaultAsync(ct);
        var header = await FindAsync(id)
            ?? throw Invalid("ARTIFACT_MANIFEST_MISSING");
        if (header.ElementCount != 8 || header.GetValue("kind", "") != "manifest" || header.GetValue("version", 0) != 2
            || header.GetValue("runId", "") != receipt.RunId || header.GetValue("generationId", "") != receipt.GenerationId
            || header.GetValue("manifestHash", "") != receipt.ManifestHash
            || header.GetValue("state", "") != "READY" && (requireReady || header.GetValue("state", "") != "STAGING"))
            throw Invalid("ARTIFACT_MANIFEST_INVALID_OR_PENDING");
        var manifestJson = header["manifestJson"].AsString;
        if (manifestJson.Length > 1024L + 70L * receipt.ChunkCount) throw Invalid("ARTIFACT_MANIFEST_SIZE");
        if (StatRunCanonicalJson.HashText(manifestJson) != receipt.ManifestHash) throw Invalid("ARTIFACT_MANIFEST_HASH");
        using var parsed = JsonDocument.Parse(manifestJson);
        var manifest = StatConfigCanonicalJson.DeserializeStrict<Manifest>(parsed.RootElement);
        if (StatConfigCanonicalJson.Canonicalize(manifest) != manifestJson) throw Invalid("ARTIFACT_MANIFEST_CANONICAL");
        var chunkBytes = manifest.ChunkBytes;
        var hashes = manifest.ChunkHashes;
        if (manifest.Version != 2 || manifest.RunId != receipt.RunId
            || manifest.GenerationId != receipt.GenerationId || manifest.ArtifactHash != receipt.ArtifactHash
            || manifest.Bytes != receipt.Bytes || hashes is null || hashes.Length != receipt.ChunkCount
            || chunkBytes < 4 || chunkBytes > limits.ChunkBytes
            || hashes.Any(h => !StatRunCanonicalJson.IsCanonicalSha256(h))
            || ((long)receipt.Bytes + chunkBytes - 1) / chunkBytes != receipt.ChunkCount)
            throw Invalid("ARTIFACT_MANIFEST_RECEIPT_MISMATCH");
        // The built-in _id index bounds reads to this artifact; no scan of other
        // generations and no index creation in the calculation/request path.
        var chunkFilter = new BsonDocument("_id", new BsonDocument { { "$gte", id + "/" }, { "$lt", id + "0" } });
        var actualCount = session is null ? await collection.CountDocumentsAsync(chunkFilter, cancellationToken: ct)
            : await collection.CountDocumentsAsync(session, chunkFilter, new CountOptions { MaxTime = TimeSpan.FromSeconds(3) }, ct);
        if (actualCount != receipt.ChunkCount) throw Invalid("ARTIFACT_CHUNK_SET_INCOMPLETE");
        using var output = new MemoryStream(receipt.Bytes);
        for (var i = 0; i < hashes.Length; i++)
        {
            var chunk = await FindAsync(ChunkId(id, i))
                ?? throw Invalid("ARTIFACT_CHUNK_MISSING");
            if (chunk.ElementCount != 6 || chunk.GetValue("kind", "") != "chunk"
                || chunk.GetValue("manifestId", "") != id || chunk.GetValue("index", -1) != i
                || chunk.GetValue("sha256", "") != hashes[i] || !chunk.GetValue("data", BsonNull.Value).IsBsonBinaryData)
                throw Invalid("ARTIFACT_CHUNK_IDENTITY");
            var part = chunk["data"].AsBsonBinaryData.Bytes;
            if (part.Length != Math.Min(chunkBytes, receipt.Bytes - i * chunkBytes) || Hash(part) != hashes[i])
                throw Invalid("ARTIFACT_CHUNK_HASH_OR_SIZE");
            await output.WriteAsync(part, ct);
        }
        var bytes = output.ToArray();
        if (bytes.Length != receipt.Bytes || Hash(bytes) != receipt.ArtifactHash) throw Invalid("ARTIFACT_CONTENT_HASH");
        using var body = JsonDocument.Parse(Utf8.GetString(bytes));
        var artifact = StatConfigCanonicalJson.DeserializeStrict<NativeStatisticGenerationArtifact>(body.RootElement);
        ValidateIdentity(artifact);
        if (artifact.Generation.RunId != receipt.RunId || artifact.Generation.GenerationId != receipt.GenerationId
            || StatConfigCanonicalJson.Canonicalize(artifact) != Utf8.GetString(bytes)) throw Invalid("ARTIFACT_CONTENT_IDENTITY");
        return artifact;
    }

    private async Task InsertExactAsync(BsonDocument expected, bool allowState, CancellationToken ct)
    {
        try { await collection.InsertOneAsync(expected, cancellationToken: ct); }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey) { }
        var stored = await collection.Find(new BsonDocument("_id", expected["_id"])).FirstOrDefaultAsync(ct);
        if (stored is null) throw Invalid("ARTIFACT_WRITE_READBACK_MISSING");
        if (allowState)
        {
            if (stored.GetValue("state", "") is not BsonString state || state.Value is not ("STAGING" or "READY"))
                throw Invalid("ARTIFACT_STATE_INVALID");
            stored["state"] = "STAGING";
        }
        if (!stored.Equals(expected)) throw Invalid("ARTIFACT_IMMUTABLE_CONFLICT");
    }

    private static void ValidateIdentity(NativeStatisticGenerationArtifact artifact)
    {
        var pin = artifact.Generation;
        static bool Id(string value) => ObjectId.TryParse(value, out var parsed) && parsed.ToString() == value;
        if (artifact.Version != 2 || !new[] { pin.RunId, artifact.WorkId, pin.DynamicFormFamilyId,
                pin.DynamicFormTemplateId, pin.ConfigId, pin.ConfigVersionId }.All(Id)
            || !new[] { pin.GenerationId, pin.ConfigHash, pin.DynamicFormSchemaHash, pin.LifecycleEventKey,
                pin.SourceMembershipSignature, pin.CatalogRawSha256, pin.CatalogSemanticSha256,
                pin.SchemaRawSha256, pin.SchemaSemanticSha256, pin.StageLockSha256, artifact.Result.PlanContentDigest }
                .All(StatRunCanonicalJson.IsCanonicalSha256)
            || pin.ConfigRevision < 1 || pin.ConfigVersionNo < 1 || pin.DynamicFormVersionNo < 1 || pin.DirectSourceRevision < 1
            || string.IsNullOrWhiteSpace(pin.CandidateChainId) || string.IsNullOrWhiteSpace(pin.CatalogVersion)
            || string.IsNullOrWhiteSpace(artifact.PeriodInstanceKey)
            || pin.ComputedAtUtc.Kind != DateTimeKind.Utc || pin.ComputedAtUtc == DateTime.MinValue
            || pin.ComputedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0
            || artifact.Result.Version != 2 || artifact.Result.SchemaHash != pin.DynamicFormSchemaHash
            || artifact.Sources.Select(s => s.ReportId).Distinct(StringComparer.Ordinal).Count() != artifact.Sources.Count
            || artifact.Sources.Any(s => !Id(s.ReportId) || !Id(s.AssignmentId) || s.ReportId != s.Payload.ReportId || s.LifecycleRevision < 1
                || s.Payload.PayloadRevision < 1 || !StatRunCanonicalJson.IsCanonicalSha256(s.Payload.PayloadHash)
                || s.Payload.PayloadUpdatedAtUtc is DateTime time && (time.Kind != DateTimeKind.Utc || time == DateTime.MinValue
                    || time.Ticks % TimeSpan.TicksPerMillisecond != 0))
            || !artifact.Sources.Select(s => s.ReportId).Order(StringComparer.Ordinal).SequenceEqual(pin.SourceContributionBindings.Keys.Order(StringComparer.Ordinal))
            || !artifact.Sources.Select(s => s.Payload).OrderBy(s => s.ReportId, StringComparer.Ordinal)
                .SequenceEqual(artifact.Result.Sources.OrderBy(s => s.ReportId, StringComparer.Ordinal))
            || WorkReportNativeSourcePin.Digest(artifact.Result.Sources) != artifact.Result.SourceOrderDigest)
            throw Invalid("ARTIFACT_GENERATION_IDENTITY");
        _ = new NativeStatisticCalculationBudget(artifact.Limits, CancellationToken.None);
        foreach (var source in artifact.Sources)
            NativeStatisticGenerationStage.RequireContribution(pin.SourceContributionBindings[source.ReportId], source.ContributionPolicyJson);
        using var definition = JsonDocument.Parse(artifact.DefinitionJson);
        using var configuration = JsonDocument.Parse(artifact.ConfigurationJson);
        if (definition.RootElement.ValueKind != JsonValueKind.Object || configuration.RootElement.ValueKind != JsonValueKind.Object)
            throw Invalid("ARTIFACT_SNAPSHOT_REQUIRED");
    }

    private static void ValidateLimits(NativeStatisticStorageLimits limits)
    {
        // BSON hard ceiling is 16 MiB; keep each artifact part <= 1 MiB.
        // Total quota is explicit at the caller, never a preview/sample default.
        if (limits.MaxArtifactBytes < 1 || limits.ChunkBytes < 4 || limits.ChunkBytes > 1024 * 1024
            || limits.MaxChunks < 1 || limits.MaxChunks > 100_000) throw Invalid("ARTIFACT_EXPLICIT_LIMITS_REQUIRED");
    }
    private static string HeaderId(string run, string generation) => StatRunCanonicalJson.HashText("NATIVE_STAGE_V2\n" + run + "\n" + generation);
    private static string ChunkId(string header, int index) => header + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private static Exception Invalid(string reason) => NativeStatisticCalculationError.Invalid(reason);
}
