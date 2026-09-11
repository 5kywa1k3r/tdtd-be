using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task<ApiHarnessResponse> ProcessLifecycleOutboxAsync(
        int maxReports,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            $"api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports={maxReports}",
            body: null,
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P9-LFC lifecycle outbox worker");
        return response;
    }

    private static int ReadProcessed(ApiHarnessResponse response)
    {
        if (response.Json is JsonObject root &&
            root["processed"] is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number))
                return number;
            if (value.TryGetValue<long>(out var wide))
                return checked((int)wide);
        }
        throw new InvalidOperationException(
            $"Lifecycle worker response lacks processed. Body={response.Body}");
    }

    private async Task<IReadOnlyDictionary<string, P9CollectionState>>
        CaptureLifecycleDirectSnapshotAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var existing = (await (await database.ListCollectionNamesAsync(
                    cancellationToken: ct))
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var names = LifecycleDirectCollections
            .Append(LifecycleJobCollection)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, P9CollectionState>(
            StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!existing.Contains(name))
            {
                result[name] = new P9CollectionState(
                    name,
                    false,
                    0,
                    HashBytes(Array.Empty<byte>()));
                continue;
            }

            var documents = await database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var document in documents)
            {
                var bytes = document.ToBson();
                hash.AppendData(BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(bytes.Length)));
                hash.AppendData(bytes);
            }
            result[name] = new P9CollectionState(
                name,
                true,
                documents.Count,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        return result;
    }

    private static string LifecycleSnapshotSha256(
        IReadOnlyDictionary<string, P9CollectionState> snapshot)
        => HashBytes(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            snapshot.Values
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Collection}|{item.Exists}|{item.Count}|{item.DocumentSetSha256}"))));

    private static void AssertLifecycleSnapshotEqual(
        IReadOnlyDictionary<string, P9CollectionState> expected,
        IReadOnlyDictionary<string, P9CollectionState> actual,
        string context)
        => HarnessAssert.Equal(
            LifecycleSnapshotSha256(expected),
            LifecycleSnapshotSha256(actual),
            $"{context} Direct+publication snapshot");

    private static void AssertLifecycleDirectCounts(
        IReadOnlyDictionary<string, P9CollectionState> snapshot,
        int expectedDirectRows,
        int expectedJobRows,
        string context)
    {
        var directRows = LifecycleDirectCollections.Sum(name =>
            snapshot.TryGetValue(name, out var state) ? state.Count : 0L);
        var jobs = snapshot.TryGetValue(LifecycleJobCollection, out var jobState)
            ? jobState.Count
            : 0L;
        HarnessAssert.Equal((long)expectedDirectRows, directRows, $"{context} Direct row count");
        HarnessAssert.Equal((long)expectedJobRows, jobs, $"{context} lifecycle job count");
    }

    private void AddLifecycleMilestone(
        string state,
        IReadOnlyDictionary<string, P9CollectionState> snapshot)
        => _lfcMilestones.Add(new P9LifecycleMilestone(
            state,
            DateTime.UtcNow,
            LifecycleSnapshotSha256(snapshot),
            snapshot.Values
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .ToArray()));

    private async Task AssertPublishedGenerationAsync(
        IReadOnlyDictionary<string, P9CollectionState> snapshot,
        CancellationToken ct)
    {
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(_lfcRunId) && ObjectId.TryParse(_lfcRunId, out _),
            "Published Direct run id is missing or invalid");
        HarnessAssert.True(IsCanonicalSha(_lfcGenerationId), "Published generation id is not SHA-256");
        HarnessAssert.True(IsCanonicalSha(_lfcGenerationHash), "Published generation hash is not SHA-256");
        HarnessAssert.Equal(1L, snapshot[LifecycleJobCollection].Count, "Published lifecycle job count");
        foreach (var collection in LifecycleDirectCollections)
        {
            HarnessAssert.True(
                snapshot.TryGetValue(collection, out var state) && state.Count > 0,
                $"Published generation has no rows in {collection}");
            var rows = await RequireDatabase().GetCollection<BsonDocument>(collection)
                .Find(new BsonDocument("directProjection.generationId", _lfcGenerationId))
                .ToListAsync(ct);
            HarnessAssert.Equal(state!.Count, rows.Count, $"Published generation row coverage {collection}");
            HarnessAssert.True(
                rows.All(row =>
                    string.Equals(
                        BsonString(row.GetValue("directProjection").AsBsonDocument, "runId"),
                        _lfcRunId,
                        StringComparison.Ordinal)),
                $"Published rows in {collection} do not share one run id");
        }
    }

    private async Task AssertAllGenerationPinsAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        var sourceReport = await LoadLifecycleReportAsync(ct);
        var job = await LoadLifecycleJobAsync(ct);
        var work = await RequireDatabase()
            .GetCollection<BsonDocument>("works")
            .Find(new BsonDocument("_id", ObjectId.Parse(fixture.WorkId)))
            .SingleAsync(ct);
        var directSourceRevision = BsonLong(job, "directSourceRevision");
        HarnessAssert.True(directSourceRevision > 0, "Generation source revision fence");
        HarnessAssert.Equal(
            BsonLong(work, "directSourceRevision"),
            directSourceRevision,
            "Generation Work source revision fence");
        var sourceSchemaHash = BsonString(
            sourceReport,
            "dynamicFormSchemaHash");
        foreach (var collection in LifecycleDirectCollections)
        {
            var rows = await RequireDatabase().GetCollection<BsonDocument>(collection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(ct);
            HarnessAssert.True(rows.Count > 0, $"Generation pin check found no {collection} rows");
            foreach (var row in rows)
            {
                var pin = row.GetValue("directProjection", BsonNull.Value);
                HarnessAssert.True(pin.IsBsonDocument, $"{collection} row lacks directProjection pin");
                var document = pin.AsBsonDocument;
                HarnessAssert.Equal(_lfcRunId, BsonString(document, "runId"), $"{collection} run pin");
                HarnessAssert.Equal(_lfcGenerationId, BsonString(document, "generationId"), $"{collection} generation pin");
                HarnessAssert.Equal(_lfcApproveEventKey, BsonString(document, "lifecycleEventKey"), $"{collection} lifecycle event pin");
                HarnessAssert.Equal(fixture.ReportId, BsonString(document, "sourceReportId"), $"{collection} source report pin");
                HarnessAssert.Equal(BsonInt(sourceReport, "payloadRevision"), BsonInt(document, "sourcePayloadRevision"), $"{collection} source payload revision pin");
                HarnessAssert.Equal(BsonString(sourceReport, "payloadHash"), BsonString(document, "sourcePayloadHash"), $"{collection} source payload hash pin");
                HarnessAssert.Equal(3, BsonInt(document, "sourceLifecycleRevision"), $"{collection} source lifecycle revision pin");
                HarnessAssert.Equal(directSourceRevision, BsonLong(document, "directSourceRevision"), $"{collection} source revision fence pin");
                HarnessAssert.Equal(fixture.TemplateId, BsonString(document, "dynamicFormFamilyId"), $"{collection} form family pin");
                HarnessAssert.Equal(fixture.TemplateId, BsonString(document, "dynamicFormTemplateId"), $"{collection} form template pin");
                HarnessAssert.Equal(1, BsonInt(document, "dynamicFormVersionNo"), $"{collection} form version pin");
                HarnessAssert.Equal(sourceSchemaHash, BsonString(document, "dynamicFormSchemaHash"), $"{collection} form schema pin");
                HarnessAssert.Equal(fixture.ConfigId, BsonString(document, "configId"), $"{collection} config id pin");
                HarnessAssert.Equal(fixture.ConfigVersionId, BsonString(document, "configVersionId"), $"{collection} config version id pin");
                HarnessAssert.Equal(fixture.ConfigVersionNo, BsonInt(document, "configVersionNo"), $"{collection} config version number pin");
                HarnessAssert.Equal(fixture.ConfigRevision, BsonLong(document, "configRevision"), $"{collection} config revision pin");
                HarnessAssert.Equal(fixture.ConfigHash, BsonString(document, "configHash"), $"{collection} config pin");
                HarnessAssert.Equal(ChainId, BsonString(document, "candidateChainId"), $"{collection} candidate chain pin");
                HarnessAssert.Equal("1.6", BsonString(document, "catalogVersion"), $"{collection} catalog version pin");
                HarnessAssert.Equal(LifecycleCatalogRawSha256, BsonString(document, "catalogRawSha256"), $"{collection} catalog raw pin");
                HarnessAssert.Equal(LifecycleCatalogSemanticSha256, BsonString(document, "catalogSemanticSha256"), $"{collection} catalog semantic pin");
                HarnessAssert.Equal(SchemaRawSha256, BsonString(document, "schemaRawSha256"), $"{collection} schema raw pin");
                HarnessAssert.Equal(SchemaSemanticSha256, BsonString(document, "schemaSemanticSha256"), $"{collection} schema semantic pin");
                HarnessAssert.Equal(LifecycleStageLockSha256, BsonString(document, "stageLockSha256"), $"{collection} stage lock pin");
                HarnessAssert.True(IsCanonicalSha(BsonString(document, "sourceMembershipSignature")), $"{collection} membership signature");
            }
        }
    }

    private async Task<BsonDocument> LoadLifecycleJobAsync(CancellationToken ct)
    {
        if (!ObjectId.TryParse(_lfcRunId, out var runId))
            throw new InvalidOperationException("P9-LFC run id is unavailable.");
        return await RequireDatabase()
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .Find(new BsonDocument("_id", runId))
            .SingleAsync(ct);
    }

    private async Task RunNegativeLifecycleVariantAsync(
        string name,
        Func<Task> mutateSource,
        Func<Task> restoreSource,
        Action<BsonDocument>? entryMutator,
        CancellationToken ct)
    {
        var publishedEntry = _lfcPublishedApproveEntry is null
            ? throw new InvalidOperationException("Published approve entry is unavailable.")
            : (BsonDocument)_lfcPublishedApproveEntry.DeepClone();
        var before = await CaptureLifecycleDirectSnapshotAsync(ct);
        ApiHarnessResponse? worker = null;
        string? observedState = null;
        string? observedDirectState = null;
        string? observedError = null;
        try
        {
            await ReopenApproveEntryAsync(publishedEntry, entryMutator, ct);
            await mutateSource();
            worker = await ProcessLifecycleOutboxAsync(20, ct);
            var report = await LoadLifecycleReportAsync(ct);
            var observed = FindApproveCommandEntry(report);
            observedState = BsonNullableString(observed, "state");
            observedDirectState = BsonNullableString(observed, "directProjectionState");
            observedError = BsonNullableString(observed, "lastError") ??
                            BsonNullableString(report, "lifecycleProjectionLastError");
            var after = await CaptureLifecycleDirectSnapshotAsync(ct);
            AssertLifecycleSnapshotEqual(before, after, $"negative {name}");
            _lfcNegativeEvidence.Add(new P9LifecycleNegativeEvidence(
                name,
                (int)worker.StatusCode,
                ReadProcessed(worker),
                LifecycleSnapshotSha256(before),
                LifecycleSnapshotSha256(after),
                observedState,
                observedDirectState,
                StableEvidenceError(observedError),
                true));
        }
        finally
        {
            await restoreSource();
            await RestoreApproveEntryAsync(publishedEntry, ct);
        }
    }

    private async Task ReopenApproveEntryAsync(
        BsonDocument publishedEntry,
        Action<BsonDocument>? mutator,
        CancellationToken ct)
    {
        var report = await LoadLifecycleReportAsync(ct);
        var entries = report.GetValue("lifecycleProjectionOutbox", new BsonArray()).AsBsonArray;
        var replacement = (BsonDocument)publishedEntry.DeepClone();
        replacement["state"] = "PENDING";
        replacement["completedAtUtc"] = BsonNull.Value;
        replacement["lastAttemptedAtUtc"] = BsonNull.Value;
        replacement["lastError"] = BsonNull.Value;
        replacement["directProjectionState"] = BsonNull.Value;
        replacement["directProjectionReason"] = BsonNull.Value;
        replacement["directProjectionRunId"] = BsonNull.Value;
        replacement["directProjectionGenerationId"] = BsonNull.Value;
        replacement["directProjectionGenerationHash"] = BsonNull.Value;
        replacement["directProjectionCompletedAtUtc"] = BsonNull.Value;
        mutator?.Invoke(replacement);
        ReplaceApproveEntry(entries, replacement);
        await RequireDatabase().GetCollection<BsonDocument>("work_assignment_report")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().ReportId)),
                Builders<BsonDocument>.Update
                    .Set("lifecycleProjectionOutbox", entries)
                    .Set("lifecycleProjectionClaimToken", BsonNull.Value)
                    .Set("lifecycleProjectionClaimedAtUtc", BsonNull.Value)
                    .Set("lifecycleProjectionClaimExpiresAtUtc", BsonNull.Value)
                    .Set("lifecycleProjectionLastError", BsonNull.Value),
                cancellationToken: ct);
    }

    private async Task RestoreApproveEntryAsync(
        BsonDocument publishedEntry,
        CancellationToken ct)
    {
        var report = await LoadLifecycleReportAsync(ct);
        var entries = report.GetValue("lifecycleProjectionOutbox", new BsonArray()).AsBsonArray;
        ReplaceApproveEntry(entries, (BsonDocument)publishedEntry.DeepClone());
        await RequireDatabase().GetCollection<BsonDocument>("work_assignment_report")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().ReportId)),
                Builders<BsonDocument>.Update
                    .Set("lifecycleProjectionOutbox", entries)
                    .Set("lifecycleProjectionClaimToken", BsonNull.Value)
                    .Set("lifecycleProjectionClaimedAtUtc", BsonNull.Value)
                    .Set("lifecycleProjectionClaimExpiresAtUtc", BsonNull.Value)
                    .Set("lifecycleProjectionLastError", BsonNull.Value),
                cancellationToken: ct);
    }

    private async Task ClearCompletedDirectLinkAsync(CancellationToken ct)
    {
        var report = await LoadLifecycleReportAsync(ct);
        var entries = report.GetValue("lifecycleProjectionOutbox", new BsonArray()).AsBsonArray;
        var entry = FindApproveCommandEntry(report);
        entry["directProjectionState"] = BsonNull.Value;
        entry["directProjectionReason"] = BsonNull.Value;
        entry["directProjectionRunId"] = BsonNull.Value;
        entry["directProjectionGenerationId"] = BsonNull.Value;
        entry["directProjectionGenerationHash"] = BsonNull.Value;
        entry["directProjectionCompletedAtUtc"] = BsonNull.Value;
        await RequireDatabase().GetCollection<BsonDocument>("work_assignment_report")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().ReportId)),
                Builders<BsonDocument>.Update.Set("lifecycleProjectionOutbox", entries),
                cancellationToken: ct);
    }

    private static BsonDocument FindApproveCommandEntry(BsonDocument report)
        => report.GetValue("lifecycleProjectionOutbox", new BsonArray()).AsBsonArray
            .Select(value => value.AsBsonDocument)
            .Single(entry => string.Equals(
                BsonNullableString(entry, "commandId"),
                BackendServerLease.P3LifecycleProjectionFailureCommandId,
                StringComparison.Ordinal));

    private static void ReplaceApproveEntry(
        BsonArray entries,
        BsonDocument replacement)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not BsonDocument entry ||
                !string.Equals(
                    BsonNullableString(entry, "commandId"),
                    BackendServerLease.P3LifecycleProjectionFailureCommandId,
                    StringComparison.Ordinal))
            {
                continue;
            }
            entries[index] = replacement;
            return;
        }
        throw new InvalidOperationException("Approve outbox entry was not found.");
    }

    private static string StableEvidenceError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static bool IsCanonicalSha(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static int BsonInt(BsonDocument document, string field)
        => document.TryGetValue(field, out var value) && value.IsNumeric
            ? value.ToInt32()
            : throw new InvalidOperationException($"BSON field '{field}' is not numeric.");

    private static bool BsonBool(BsonDocument document, string field)
        => document.TryGetValue(field, out var value) && value.IsBoolean
            ? value.AsBoolean
            : throw new InvalidOperationException($"BSON field '{field}' is not boolean.");

    private static string BsonString(BsonDocument document, string field)
        => BsonNullableString(document, field)
           ?? throw new InvalidOperationException($"BSON field '{field}' is missing/null.");

    private static string? BsonNullableString(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return null;
        return value.IsObjectId ? value.AsObjectId.ToString() : value.AsString;
    }
}

internal sealed record P9LifecycleMilestone(
    string State,
    DateTime CapturedAtUtc,
    string SnapshotSha256,
    IReadOnlyList<P9CollectionState> Collections);

internal sealed record P9LifecycleNegativeEvidence(
    string Name,
    int HttpStatus,
    int Processed,
    string BeforeSha256,
    string AfterSha256,
    string? LifecycleState,
    string? DirectProjectionState,
    string Diagnostic,
    bool ZeroWriteVerified);

internal sealed record P9LifecycleQueueEvidence(
    string Step,
    string EventKey,
    string State,
    int AttemptCount,
    string? LastError,
    string? RunId,
    string? GenerationId,
    DateTime ObservedAtUtc);
