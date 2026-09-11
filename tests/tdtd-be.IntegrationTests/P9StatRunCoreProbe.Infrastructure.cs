using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private static readonly string[] ProhibitedRunCollections =
    [
        "stat_runs",
        "statistic_runs",
        "statistics_runs",
        "work_report_statistic_runs",
        "work_report_statistic_run_values",
        "work_report_statistic_reconciliations"
    ];

    private static readonly string[] HangfireInfrastructureSuffixes =
    [
        ".jobGraph",
        ".locks",
        ".migrationLock",
        ".notifications",
        ".schema",
        ".server",
        ".stateHistory"
    ];

    private JsonObject BuildCreateRequest(string commandId)
    {
        var fixture = Fixture();
        return new JsonObject
        {
            ["commandId"] = commandId,
            ["workId"] = fixture.WorkId,
            ["scopeType"] = "ASSIGNMENT",
            ["scopeId"] = fixture.AssignmentId,
            ["sourceReportId"] = fixture.ReportId,
            ["dynamicFormTemplateId"] = fixture.TemplateId,
            ["expectedConfigRevision"] = fixture.ConfigRevision,
            ["expectedConfigHash"] = fixture.ConfigHash,
            ["expectedSourceRevision"] = fixture.SourceRevision,
            ["expectedSourceHash"] = fixture.SourceHash,
            ["expectedLifecycleRevision"] = fixture.LifecycleRevision,
            ["period"] = new JsonObject
            {
                ["periodKey"] = fixture.PeriodKey,
                ["periodInstanceKey"] = fixture.PeriodInstanceKey,
                ["periodKind"] = fixture.PeriodKind,
                ["periodStart"] = fixture.PeriodStartUtc,
                ["periodEnd"] = fixture.PeriodEndUtc
            }
        };
    }

    private async Task<ApiHarnessResponse> CreateJobAsync(
        string capabilityId,
        JsonObject request,
        string token,
        CancellationToken ct)
        => await RequireApi().PostAsync(
            $"api/stat-runs/{capabilityId}/jobs",
            request.DeepClone(),
            token,
            ct: ct);

    private async Task<JsonObject> EvaluateActivationAsync(
        ApiHarnessClient api,
        string capabilityId,
        string routeId,
        string token,
        CancellationToken ct)
    {
        var response = await api.PostAsync(
            "api/testing/p9/stat-runs/activation/evaluate",
            new { capabilityId, routeId },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P9 activation {capabilityId}/{routeId}");
        return ApiHarnessClient.RequiredObject(
            response.Json,
            "P9 activation response");
    }

    private static string RequiredString(JsonNode? node, string property)
        => ApiHarnessClient.RequiredString(node, property);

    private static long RequiredLong(JsonNode? node, string property)
    {
        if (node is JsonObject obj &&
            obj[property] is JsonValue value &&
            value.TryGetValue<long>(out var number))
        {
            return number;
        }
        throw new InvalidOperationException(
            $"Response property '{property}' is missing or not an Int64. Body={node?.ToJsonString() ?? "<null>"}");
    }

    private static bool RequiredBool(JsonNode? node, string property)
        => ApiHarnessClient.RequiredBool(node, property);

    private static void ExpectError(
        ApiHarnessResponse response,
        HttpStatusCode status,
        string errorCode,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, status, context);
        var actual = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "errorCode") ??
                     ApiHarnessClient.FindStringRecursive(
                         response.Json,
                         "code");
        HarnessAssert.Equal(errorCode, actual, $"{context} error code");
    }

    private async Task<long> CountJobsAsync(CancellationToken ct)
        => await RequireDatabase()
            .GetCollection<BsonDocument>("work_report_statistic_rebuild_jobs")
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);

    private async Task<BsonDocument> LoadJobAsync(
        string jobId,
        CancellationToken ct)
        => await RequireDatabase()
               .GetCollection<BsonDocument>("work_report_statistic_rebuild_jobs")
               .Find(new BsonDocument("_id", ObjectId.Parse(jobId)))
               .SingleAsync(ct);

    private static string BsonText(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return string.Empty;
        return value.IsObjectId ? value.AsObjectId.ToString() : value.AsString;
    }

    private static long BsonLong(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || !value.IsNumeric)
            throw new InvalidOperationException($"BSON field '{field}' is not numeric.");
        return value.ToInt64();
    }

    private static string? BsonNullableText(
        BsonDocument document,
        string field)
        => !document.TryGetValue(field, out var value) || value.IsBsonNull
            ? null
            : value.IsObjectId
                ? value.AsObjectId.ToString()
                : value.AsString;

    private static DateTime? BsonNullableUtc(
        BsonDocument document,
        string field)
        => !document.TryGetValue(field, out var value) || value.IsBsonNull
            ? null
            : value.ToUniversalTime();

    private static string? FormatUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;
        var utc = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
        utc = new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string ComputeJobStateHash(
        BsonDocument job,
        DateTime? deadlineAtUtc = null)
        => CanonicalJsonSha256(new
        {
            jobId = BsonText(job, "_id"),
            status = BsonText(job, "status"),
            revision = BsonLong(job, "stateRevision"),
            retryCount = (int)BsonLong(job, "retryCount"),
            nextRetryAtUtc = FormatUtc(BsonNullableUtc(
                job,
                "nextRetryAtUtc")),
            leaseUntilUtc = FormatUtc(BsonNullableUtc(
                job,
                "leaseUntilUtc")),
            deadlineAtUtc = FormatUtc(
                deadlineAtUtc ?? BsonNullableUtc(job, "deadlineAtUtc")),
            claimToken = BsonNullableText(job, "claimToken"),
            leaseOwnerId = BsonNullableText(job, "leaseOwnerId"),
            lastHeartbeatAtUtc = FormatUtc(BsonNullableUtc(
                job,
                "lastHeartbeatAtUtc")),
            generationId = BsonNullableText(job, "generationId"),
            generationHash = BsonNullableText(job, "generationHash"),
            resetReceiptHistoryHash = BsonNullableText(
                job,
                "resetReceiptHistoryHash"),
            freshnessState = BsonText(job, "freshnessState"),
            diagnosticCode = BsonNullableText(job, "diagnosticCode")
        });

    private async Task<IReadOnlyDictionary<string, P9CollectionState>>
        CaptureDatabaseSnapshotAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var existing = (await (await database.ListCollectionNamesAsync(
                    cancellationToken: ct))
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var names = existing
            .Concat(ProhibitedRunCollections)
            .Append("work_report_statistic_rebuild_jobs")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var snapshot = new Dictionary<string, P9CollectionState>(
            StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!existing.Contains(name))
            {
                snapshot[name] = new P9CollectionState(
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
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            foreach (var document in documents)
            {
                var bytes = document.ToBson();
                hash.AppendData(BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(bytes.Length)));
                hash.AppendData(bytes);
            }
            snapshot[name] = new P9CollectionState(
                name,
                true,
                documents.Count,
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant());
        }
        return snapshot;
    }

    private async Task AwaitDatabaseInfrastructureQuiescenceAsync(
        CancellationToken ct,
        bool allowPreinitializedHangfireInfrastructure = false)
    {
        const string warmupSectionId = "p9-infrastructure-warmup";
        var hangfirePrefix = $"p1hf_{new string(
            _runKey.Where(char.IsLetterOrDigit)
                .Take(24)
                .ToArray()).ToLowerInvariant()}";
        var expected = HangfireInfrastructureSuffixes
            .Select(suffix => hangfirePrefix + suffix)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var exchangeStart = RequireApi().Exchanges.Count;
        var fixture = Fixture();
        var warmupRoute =
            "api/work-assignment-advanced-summary/assignments/" +
            $"{fixture.AssignmentId}/templates/{fixture.TemplateId}/" +
            $"sections/{warmupSectionId}/config";
        var warmupResponse = await RequireApi().GetAsync(
            warmupRoute,
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            warmupResponse,
            HttpStatusCode.OK,
            "P9 infrastructure read-only warmup");
        var warmupRoot = ApiHarnessClient.RequiredObject(
            warmupResponse.Json,
            "P9 infrastructure warmup response");
        var warmupIdentity = warmupRoot["identity"] as JsonObject
                             ?? throw new InvalidOperationException(
                                 "P9 infrastructure warmup response lacks identity.");
        var isVirtualEmpty = RequiredBool(warmupRoot, "isVirtualEmpty");
        var warmupRevision = RequiredLong(warmupIdentity, "revision");
        var warmupStatus = RequiredString(warmupIdentity, "status");
        var warmupOwnerKind = RequiredString(warmupIdentity, "ownerKind");
        var deadline = DateTime.UtcNow.AddSeconds(35);
        string[] missing;
        while (DateTime.UtcNow < deadline)
        {
            var names = await (await RequireDatabase()
                    .ListCollectionNamesAsync(cancellationToken: ct))
                .ToListAsync(ct);
            var nameSet = names.ToHashSet(StringComparer.Ordinal);
            missing = expected
                .Where(name => !nameSet.Contains(name))
                .ToArray();
            if (missing.Length == 0)
            {
                await Task.Delay(500, ct);
                break;
            }
            await Task.Delay(250, ct);
        }
        var after = await CaptureDatabaseSnapshotAsync(ct);
        missing = expected
            .Where(name =>
                !after.TryGetValue(name, out var state) || !state.Exists)
            .ToArray();
        var deltas = BuildDeltas(before, after);
        var changed = deltas
            .Where(delta => delta.Changed)
            .ToArray();
        var unexpected = changed
            .Where(delta => !expectedSet.Contains(delta.Collection))
            .ToArray();
        var prohibitedChanges = changed
            .Where(delta => ProhibitedRunCollections.Contains(
                delta.Collection,
                StringComparer.Ordinal))
            .ToArray();
        var changedAreExactInfrastructure = changed
            .Select(delta => delta.Collection)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(expectedSet);
        var changedAreAllowedInfrastructure =
            changedAreExactInfrastructure ||
            (allowPreinitializedHangfireInfrastructure &&
             changed.All(delta => expectedSet.Contains(delta.Collection)));
        var allExpectedPresent = expected.All(name =>
            after.TryGetValue(name, out var state) && state.Exists);
        _infrastructureWarmupVerified =
            missing.Length == 0 &&
            allExpectedPresent &&
            changedAreAllowedInfrastructure &&
            unexpected.Length == 0 &&
            prohibitedChanges.Length == 0;
        await WriteStrictJsonAsync(
            Path.Combine(
                _paths.RunRoot,
                "p9-01-infrastructure-warmup.json"),
            new
            {
                schemaVersion = "P9_CORE_INFRASTRUCTURE_WARMUP_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                warmup = new
                {
                    readOnlyVirtualAdvancedGet = true,
                    route = warmupRoute,
                    statusCode = (int)warmupResponse.StatusCode,
                    isVirtualEmpty,
                    revision = warmupRevision,
                    status = warmupStatus,
                    ownerKind = warmupOwnerKind,
                    exchangeSequences = RequireApi().Exchanges
                        .Skip(exchangeStart)
                        .Select(exchange => exchange.Sequence)
                        .ToArray()
                },
                hangfirePrefix,
                expectedInfrastructureCollections = expected,
                before = before.Values.OrderBy(
                    state => state.Collection,
                    StringComparer.Ordinal),
                after = after.Values.OrderBy(
                    state => state.Collection,
                    StringComparer.Ordinal),
                deltas,
                assertions = new
                {
                    changedCollectionsAreExactRunOwnedInfrastructure =
                        changedAreExactInfrastructure,
                    preinitializedHangfireInfrastructureAllowed =
                        allowPreinitializedHangfireInfrastructure,
                    changedCollectionsAreAllowedRunOwnedInfrastructure =
                        changedAreAllowedInfrastructure,
                    allExpectedInfrastructurePresent =
                        allExpectedPresent,
                    prohibitedCollectionDeltaZero =
                        prohibitedChanges.Length == 0,
                    domainCollectionDeltaZero =
                        unexpected.Length == 0,
                    caseSnapshotsRemainStrict = true
                }
            },
            ct);
        HarnessAssert.True(
            missing.Length == 0,
            "P9 warmup did not initialize exact run-owned Hangfire collections: " +
            string.Join(", ", missing));
        HarnessAssert.True(
            allExpectedPresent,
            "P9 warmup snapshot omitted a run-owned Hangfire collection.");
        HarnessAssert.True(
            changedAreAllowedInfrastructure,
            "P9 warmup changed collections outside the allowed run-owned Hangfire infrastructure set.");
        HarnessAssert.True(
            unexpected.Length == 0,
            "P9 warmup changed non-Hangfire collections: " +
            string.Join(", ", unexpected.Select(item => item.Collection)));
        HarnessAssert.True(
            prohibitedChanges.Length == 0,
            "P9 warmup changed prohibited run collections.");
        HarnessAssert.True(
            isVirtualEmpty,
            "P9 warmup GET did not preserve virtual empty config state.");
        HarnessAssert.Equal(
            0L,
            warmupRevision,
            "P9 warmup GET changed virtual config revision.");
        HarnessAssert.Equal(
            "DRAFT",
            warmupStatus,
            "P9 warmup GET returned a non-virtual config status.");
        HarnessAssert.Equal(
            "ADVANCED_SUMMARY",
            warmupOwnerKind,
            "P9 warmup GET returned an unexpected owner kind.");
    }

    private static P9CollectionState State(
        IReadOnlyDictionary<string, P9CollectionState> snapshot,
        string collection)
        => snapshot.TryGetValue(collection, out var value)
            ? value
            : new P9CollectionState(
                collection,
                false,
                0,
                HashBytes(Array.Empty<byte>()));

    private static IReadOnlyList<P9CollectionDelta> BuildDeltas(
        IReadOnlyDictionary<string, P9CollectionState> before,
        IReadOnlyDictionary<string, P9CollectionState> after)
        => before.Keys.Concat(after.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                var left = State(before, name);
                var right = State(after, name);
                return new P9CollectionDelta(
                    name,
                    left.Count,
                    right.Count,
                    right.Count - left.Count,
                    left.DocumentSetSha256,
                    right.DocumentSetSha256,
                    left.Exists != right.Exists ||
                    !string.Equals(
                        left.DocumentSetSha256,
                        right.DocumentSetSha256,
                        StringComparison.Ordinal));
            })
            .ToArray();

    private static string SnapshotSha256(
        IReadOnlyDictionary<string, P9CollectionState> snapshot)
        => HashBytes(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            snapshot.Values
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Collection}|{item.Exists}|{item.Count}|{item.DocumentSetSha256}"))));

    private static void AssertProhibitedRunsAbsent(
        IReadOnlyDictionary<string, P9CollectionState> snapshot,
        string context)
    {
        var emptySha = HashBytes(Array.Empty<byte>());
        foreach (var collection in ProhibitedRunCollections)
        {
            var state = State(snapshot, collection);
            HarnessAssert.True(
                !state.Exists,
                $"{context}: prohibited collection exists: {collection}");
            HarnessAssert.Equal(
                0L,
                state.Count,
                $"{context}: prohibited collection count {collection}");
            HarnessAssert.Equal(
                emptySha,
                state.DocumentSetSha256,
                $"{context}: prohibited collection hash {collection}");
        }
    }

    private async Task RunCaseAsync(
        string caseId,
        Func<Task<CaseObservation>> action,
        CancellationToken ct)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var exchangeStart = RequireApi().Exchanges.Count;
        await _cases.RunAsync(
            caseId,
            async () =>
            {
                try
                {
                    var observation = await action();
                    var after = await CaptureDatabaseSnapshotAsync(ct);
                    _caseEvidence.Add(new P9CaseEvidence(
                        caseId,
                        HarnessVerdict.DAT,
                        before.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                        after.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                        BuildDeltas(before, after),
                        RequireApi().Exchanges.Skip(exchangeStart)
                            .Where(exchange => string.Equals(
                                exchange.CaseId,
                                caseId,
                                StringComparison.Ordinal))
                            .Select(exchange => exchange.Sequence)
                            .ToArray(),
                        null));
                    return observation;
                }
                catch (Exception exception)
                {
                    IReadOnlyDictionary<string, P9CollectionState> after;
                    try
                    {
                        after = await CaptureDatabaseSnapshotAsync(
                            CancellationToken.None);
                    }
                    catch
                    {
                        after = new Dictionary<string, P9CollectionState>(
                            StringComparer.Ordinal);
                    }
                    _caseEvidence.Add(new P9CaseEvidence(
                        caseId,
                        exception is HarnessCaseNotRunnableException
                            ? HarnessVerdict.CHUA_CHAY
                            : HarnessVerdict.KHONG_DAT,
                        before.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                        after.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                        BuildDeltas(before, after),
                        RequireApi().Exchanges.Skip(exchangeStart)
                            .Where(exchange => string.Equals(
                                exchange.CaseId,
                                caseId,
                                StringComparison.Ordinal))
                            .Select(exchange => exchange.Sequence)
                            .ToArray(),
                        $"{exception.GetType().Name}: {exception.Message}"));
                    throw;
                }
            });
    }

    private async Task<T> RunVariantAsync<T>(
        string caseId,
        string name,
        BackendServerOptions options,
        Func<ApiHarnessClient, string, Task<T>> action,
        CancellationToken ct)
    {
        var root = Path.Combine(_iterationRoot, "variants", name);
        Directory.CreateDirectory(root);
        BackendServerLease? backend = null;
        ApiHarnessClient? api = null;
        try
        {
            backend = await BackendServerLease.StartAsync(
                _paths,
                root,
                $"{_runKey}_{name}",
                RequireMongo(),
                ct,
                options);
            api = new ApiHarnessClient(backend.BaseUri);
            var adminToken = await api.LoginAsync(
                "admin",
                _bootstrapPassword ??
                throw new HarnessCaseNotRunnableException(
                    "Bootstrap password is unavailable."),
                ct);
            RememberSecret(adminToken);
            var value = await action(api, adminToken);
            _variantEvidence.Add(new P9VariantEvidence(
                caseId,
                name,
                options.EnvironmentNameOverride ?? "Testing",
                options.P9StatRunCandidate?.Enabled,
                options.P9StatRunCandidate?.ExpectedDatabase,
                backend.ProcessId,
                backend.Port,
                api.Exchanges.ToArray(),
                "DAT",
                null));
            return value;
        }
        catch (Exception exception)
        {
            _variantEvidence.Add(new P9VariantEvidence(
                caseId,
                name,
                options.EnvironmentNameOverride ?? "Testing",
                options.P9StatRunCandidate?.Enabled,
                options.P9StatRunCandidate?.ExpectedDatabase,
                backend?.ProcessId,
                backend?.Port,
                api?.Exchanges.ToArray() ?? [],
                "KHONG_DAT",
                $"{exception.GetType().Name}: {exception.Message}"));
            throw;
        }
        finally
        {
            api?.Dispose();
            if (backend is not null)
            {
                await backend.StopAsync();
                await backend.DisposeAsync();
            }
        }
    }

    private async Task DrainFoundationJobsAsync(
        string workerId,
        CancellationToken ct)
    {
        var admin = Actor("admin");
        for (var index = 0; index < 100; index++)
        {
            var claim = await RequireApi().PostAsync(
                "api/testing/p9/stat-runs/jobs/claim",
                new { workerId },
                admin.Token,
                ct: ct);
            if (claim.StatusCode == HttpStatusCode.NoContent)
                return;
            ApiHarnessClient.ExpectStatus(
                claim,
                HttpStatusCode.OK,
                "P9 drain claim");
            var claimObject = ApiHarnessClient.RequiredObject(
                claim.Json,
                "P9 drain claim");
            var token = RequiredString(claim.Json, "claimToken");
            var job = ApiHarnessClient.RequiredObject(
                claimObject["job"],
                "P9 drain job");
            var jobId = RequiredString(job, "jobId");
            var complete = await RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                new { workerId, claimToken = token },
                admin.Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                complete,
                HttpStatusCode.OK,
                "P9 drain complete");
        }
        throw new InvalidOperationException(
            "P9 foundation queue did not drain within 100 jobs.");
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputeWorkReportPayloadHash(
        string values1DJson,
        string? fieldValuesJson,
        string? tableRootJson,
        string? summarySourceJson,
        IEnumerable<(string BlockId, int BlockOrder, string PayloadHash)>? blocks = null)
    {
        var builder = new StringBuilder()
            .Append(values1DJson).Append('\n')
            .Append(fieldValuesJson).Append('\n')
            .Append(tableRootJson).Append('\n')
            .Append(summarySourceJson).Append('\n');
        foreach (var block in (blocks ?? [])
                     .OrderBy(item => item.BlockOrder)
                     .ThenBy(item => item.BlockId, StringComparer.Ordinal))
        {
            builder.Append(block.BlockId)
                .Append(':')
                .Append(block.PayloadHash)
                .Append('\n');
        }
        return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string CanonicalJsonSha256<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonicalJson(writer, element);
        return HashBytes(stream.ToArray());
    }

    private static string CanonicalJsonFileSha256(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
            WriteCanonicalJson(writer, document.RootElement);
        return HashBytes(stream.ToArray());
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported canonical JSON token {element.ValueKind}.");
        }
    }
}

internal sealed record P9CollectionState(
    string Collection,
    bool Exists,
    long Count,
    string DocumentSetSha256);

internal sealed record P9CollectionDelta(
    string Collection,
    long BeforeCount,
    long AfterCount,
    long CountDelta,
    string BeforeSha256,
    string AfterSha256,
    bool Changed);

internal sealed record P9CaseEvidence(
    string CaseId,
    HarnessVerdict Verdict,
    IReadOnlyList<P9CollectionState> Before,
    IReadOnlyList<P9CollectionState> After,
    IReadOnlyList<P9CollectionDelta> Deltas,
    IReadOnlyList<int> ApiExchangeSequences,
    string? Failure);

internal sealed record P9VariantEvidence(
    string CaseId,
    string Name,
    string Environment,
    bool? CandidateEnabled,
    string? ExpectedDatabase,
    int? ProcessId,
    int? Port,
    IReadOnlyList<ApiExchangeEvidence> Exchanges,
    string Verdict,
    string? Failure);
