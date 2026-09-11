using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string BasicCommandLineSwitch = "--p9-basic-probe";
    private const string BasicPromptId = "P9-04";
    private const string BasicGroupId = "P9-BAS";
    private const string BasicCatalogRawSha256 =
        "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509";
    private const string BasicCatalogSemanticSha256 =
        "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1";
    private const string BasicStageLockSha256 =
        "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad";
    private static readonly string[] BasicRequiredOracles =
    [
        "API_KESTREL",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "CONFIG_HASH_RECOMPUTE",
        "QUEUE_TRACE",
        "INDEX_EXPLAIN",
        "P10_ZERO_WRITE"
    ];
    internal static readonly string[] BasicExpectedCaseIds =
    [
        "P9-BAS-BASIC-01", "P9-BAS-BASIC-02", "P9-BAS-BASIC-03", "P9-BAS-BASIC-04",
        "P9-BAS-BASIC-05", "P9-BAS-BASIC-06", "P9-BAS-BASIC-07", "P9-BAS-BASIC-08",
        "P9-BAS-FLOW-BRANCH-01", "P9-BAS-FLOW-BRANCH-02",
        "P9-BAS-FLOW-BRANCH-03", "P9-BAS-FLOW-BRANCH-04",
        "P9-BAS-FLOW-STEP-01", "P9-BAS-FLOW-STEP-02",
        "P9-BAS-FLOW-STEP-03", "P9-BAS-FLOW-STEP-04",
        "P9-BAS-FLOW-EFFECTIVE-PATH-01", "P9-BAS-FLOW-EFFECTIVE-PATH-02",
        "P9-BAS-FLOW-EFFECTIVE-PATH-03", "P9-BAS-FLOW-EFFECTIVE-PATH-04",
        "P9-BAS-FLOW-FINAL-01", "P9-BAS-FLOW-FINAL-02",
        "P9-BAS-FLOW-FINAL-03", "P9-BAS-FLOW-FINAL-04"
    ];

    private IReadOnlyDictionary<string, P9CollectionState>? _basBefore;
    private IReadOnlyDictionary<string, P9CollectionState>? _basAfter;
    private int _basCommandSequence;
    private string? _basFlowBranchId;
    private P9BasicIndexEvidence? _basIndexEvidence;

    public static async Task<int> RunBasicAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-BAS probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }
        var runKey =
            $"p904_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, BasicPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteBasicAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteBasicAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;
        try
        {
            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                _iterationRoot,
                _runKey,
                _mongo,
                ct,
                new BackendServerOptions
                {
                    P9StatRunCandidate = BuildBasicCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareBasicFixtureAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildBasicEnvironmentEvidence(startedAtUtc),
                ct);
            _basBefore = await CaptureDatabaseSnapshotAsync(ct);
            await RunBasicCasesAsync(ct);
            _basIndexEvidence = await CaptureBasicIndexEvidenceAsync(ct);
            _basAfter = await CaptureDatabaseSnapshotAsync(ct);
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
        }
        finally
        {
            await StopBackendAsync(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
                               _backend is not null &&
                               _backend.StopVerified &&
                               _backend.PortReleaseVerified &&
                               _mongo is not null &&
                               _mongo.DatabaseDropVerified &&
                               _mongo.ProcessStopVerified &&
                               _mongo.PortReleaseVerified &&
                               _mongo.DataDirectoryRemovalVerified;
        var completedAtUtc = DateTime.UtcNow;
        await WriteBasicCleanupArtifactAsync(
            cleanupSucceeded, cleanupErrors, completedAtUtc, ct);
        var passed = await WriteBasicEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-BAS passed 24/24; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-BAS failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildBasicCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            BasicPromptId);
        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            BasicCatalogRawSha256,
            BasicCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256,
            SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            BasicStageLockSha256);
    }

    private object BuildBasicEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_BAS_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = BasicPromptId,
            groupId = BasicGroupId,
            runKey = _runKey,
            startedAtUtc,
            workspaceRoot = _paths.WorkspaceRoot,
            runRoot = _paths.RunRoot,
            databaseName = RequireMongo().DatabaseName,
            replicaSetName = RequireMongo().ReplicaSetName,
            backendProcessId = RequireBackend().ProcessId,
            backendPort = RequireBackend().Port,
            candidate = new
            {
                stage = 2,
                stageLockSha256 = BasicStageLockSha256,
                catalogRawSha256 = BasicCatalogRawSha256,
                catalogSemanticSha256 = BasicCatalogSemanticSha256,
                promotions = new[] { "BASIC_SUMMARY", "FLOW_SCOPES" }
            }
        };

    private async Task PrepareBasicFixtureAsync(
        CancellationToken ct,
        bool preserveApprovedPairedSource = false)
    {
        var fixture = Fixture();
        await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_form_templates")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id",
                    ObjectId.Parse(fixture.TemplateId)),
                Builders<BsonDocument>.Update
                    .Unset("statisticConfigId")
                    .Unset("statisticConfigVersionId")
                    .Unset("statisticConfigPreviousVersionId")
                    .Unset("statisticConfigVersionNo")
                    .Unset("statisticConfigRevision")
                    .Unset("statisticConfigStatus")
                    .Unset("statisticConfigHash")
                    .Unset("statisticConfigDependencyPins")
                    .Unset("statisticConfigSections")
                    .Unset("statisticConfigSnapshots")
                    .Set("updatedAtUtc", DateTime.UtcNow),
                cancellationToken: ct);
        var assignments = RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments");
        var rootId = ObjectId.Parse(fixture.AssignmentId);
        var siblingId = ObjectId.Parse(fixture.SiblingAssignmentId);
        var root = await assignments
            .Find(new BsonDocument("_id", rootId))
            .SingleAsync(ct);
        _basFlowBranchId = root["flowBranchId"].AsObjectId.ToString();
        var now = DateTime.UtcNow;
        await assignments.UpdateOneAsync(
            new BsonDocument("_id", rootId),
            Builders<BsonDocument>.Update
                .Set("assignmentType", "ONCE")
                .Set("isFlowFinalNode", true)
                .Set("updatedAtUtc", now),
            cancellationToken: ct);
        await assignments.UpdateOneAsync(
            new BsonDocument("_id", siblingId),
            Builders<BsonDocument>.Update
                .Set("assignmentType", "ONCE")
                .Set("parentAssignmentId", fixture.AssignmentId)
                .Set("rootAssignmentId", fixture.AssignmentId)
                .Set("path", $"{fixture.AssignmentId}/{fixture.SiblingAssignmentId}")
                .Set("flowTemplateId", ObjectId.Parse(fixture.FlowFamilyId))
                .Set("flowTemplateVersionNo", 1)
                .Set("flowInstanceId", ObjectId.Parse(fixture.FlowInstanceId))
                .Set("flowStepId", "P9_CORE_STEP")
                .Set("flowStepCode", "P9_CORE_STEP")
                .Set("flowStepOrder", 1)
                .Set("flowBranchId", ObjectId.Parse(_basFlowBranchId))
                .Set("flowAttemptNo", 1)
                .Set("flowExecutionEpoch", 1)
                .Set("flowEffectiveStatus", "EFFECTIVE")
                .Set("isFlowFinalNode", true)
                .Set("isActive", true)
                .Set("updatedAtUtc", now),
            cancellationToken: ct);

        if (!preserveApprovedPairedSource)
        {
            await RequireDatabase()
                .GetCollection<BsonDocument>("work_assignment_report")
                .UpdateOneAsync(
                    new BsonDocument(
                        "_id",
                        ObjectId.Parse(fixture.PairedReportId)),
                    Builders<BsonDocument>.Update
                        .Set("status", 0)
                        .Unset("submittedAtUtc")
                        .Unset("approvedAtUtc")
                        .Unset("approvedByUserId")
                        .Set("updatedAtUtc", now),
                    cancellationToken: ct);
        }
    }

    private string BasicConfigRoute()
        => $"api/work-assignment-basic-summary/assignments/{Fixture().AssignmentId}/" +
           $"templates/{Fixture().TemplateId}/config";

    private async Task<P9BasicConfigIdentity> ReadBasicIdentityAsync(
        CancellationToken ct,
        string actorKey = "executor")
    {
        var response = await RequireApi().GetAsync(
            BasicConfigRoute(), Actor(actorKey).Token, ct: ct);
        ApiHarnessClient.ExpectStatus(
            response, HttpStatusCode.OK, "P9-BAS config GET");
        return ParseBasicIdentity(response.Json);
    }

    private static P9BasicConfigIdentity ParseBasicIdentity(JsonNode? node)
    {
        var root = ApiHarnessClient.RequiredObject(node, "P9-BAS config response");
        var identity = root["identity"] as JsonObject
            ?? throw new InvalidOperationException(
                $"P9-BAS config identity missing: {root.ToJsonString()}");
        return new P9BasicConfigIdentity(
            RequiredJsonString(identity, "configId"),
            RequiredJsonString(identity, "versionId"),
            RequiredJsonInt(identity, "versionNo"),
            RequiredJsonLong(identity, "revision"),
            RequiredJsonString(identity, "status"),
            RequiredJsonString(identity, "configHash"));
    }

    private async Task<P9BasicConfigIdentity> ConfigureBasicAsync(
        string mode,
        CancellationToken ct,
        string actorKey = "executor")
    {
        var current = await ReadBasicIdentityAsync(ct, actorKey);
        if (string.Equals(current.Status, "LOCKED", StringComparison.Ordinal))
        {
            var next = await RequireApi().PostAsync(
                $"{BasicConfigRoute()}/next-draft",
                BasicEnvelope(current, new JsonObject()),
                Actor(actorKey).Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                next, HttpStatusCode.OK, "P9-BAS next draft");
            current = ParseBasicIdentity(next.Json);
        }

        var put = await RequireApi().PutAsync(
            BasicConfigRoute(),
            BasicEnvelope(current, BuildBasicPayload(mode)),
            Actor(actorKey).Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            put, HttpStatusCode.OK, $"P9-BAS put {mode}");
        current = ParseBasicIdentity(put.Json);
        var locked = await RequireApi().PostAsync(
            $"{BasicConfigRoute()}/lock",
            BasicEnvelope(current, new JsonObject()),
            Actor(actorKey).Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            locked, HttpStatusCode.OK, $"P9-BAS lock {mode}");
        var identity = ParseBasicIdentity(locked.Json);
        HarnessAssert.Equal("LOCKED", identity.Status, $"P9-BAS {mode} lock state");
        return identity;
    }

    private JsonObject BasicEnvelope(
        P9BasicConfigIdentity current,
        JsonNode payload)
        => new()
        {
            ["commandId"] = $"p9-bas-{++_basCommandSequence:000}",
            ["expectedRevision"] = current.Revision,
            ["expectedConfigHash"] = current.ConfigHash,
            ["payload"] = payload.DeepClone()
        };

    private JsonObject BuildBasicPayload(string mode)
    {
        var source = new JsonObject { ["mode"] = mode };
        if (mode.StartsWith("FLOW_", StringComparison.Ordinal))
        {
            source["flowInstanceId"] = Fixture().FlowInstanceId;
            source["flowEffectiveStatus"] = "EFFECTIVE";
            if (mode == "FLOW_BRANCH")
                source["flowBranchId"] = _basFlowBranchId;
            if (mode == "FLOW_STEP")
                source["flowStepId"] = "P9_CORE_STEP";
        }
        return new JsonObject
        {
            ["sourceScope"] = source,
            ["periodRule"] = new JsonObject { ["mode"] = "ALL_PERIODS" },
            ["groupingHints"] = new JsonArray(),
            ["detailHints"] = new JsonObject
            {
                ["includeSourceRows"] = true,
                ["maxTextChars"] = 12000
            },
            ["targets"] = new JsonArray()
        };
    }

    private JsonObject BasicRequest(bool forceRefresh = false)
        => new()
        {
            ["scopeAssignmentId"] = Fixture().AssignmentId,
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["forceRefresh"] = forceRefresh,
            ["includeSourceRows"] = true,
            ["maxTextChars"] = 12000
        };

    private async Task<JsonObject> ReadBasicSummaryAsync(
        string route,
        JsonObject request,
        string token,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            route, request, token, ct: ct);
        ApiHarnessClient.ExpectStatus(
            response, HttpStatusCode.OK, $"P9-BAS {route}");
        return ApiHarnessClient.RequiredObject(
            response.Json, $"P9-BAS {route} response");
    }

    private static JsonObject BasicMeta(JsonObject root)
        => root["meta"] as JsonObject
           ?? throw new InvalidOperationException(
               $"P9-BAS meta missing: {root.ToJsonString()}");

    private static string RequiredJsonString(JsonObject root, string property)
        => root[property]?.GetValue<string>()
           ?? throw new InvalidOperationException(
               $"P9-BAS string '{property}' missing: {root.ToJsonString()}");

    private static int RequiredJsonInt(JsonObject root, string property)
        => root[property]?.GetValue<int>()
           ?? throw new InvalidOperationException(
               $"P9-BAS int '{property}' missing: {root.ToJsonString()}");

    private static long RequiredJsonLong(JsonObject root, string property)
        => root[property]?.GetValue<long>()
           ?? throw new InvalidOperationException(
               $"P9-BAS long '{property}' missing: {root.ToJsonString()}");

    private async Task SetSiblingActiveAsync(bool active, CancellationToken ct)
    {
        await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id",
                    ObjectId.Parse(Fixture().SiblingAssignmentId)),
                Builders<BsonDocument>.Update
                    .Set("isActive", active)
                    .Set("updatedAtUtc", DateTime.UtcNow),
                cancellationToken: ct);
    }

    private async Task TouchRootMembershipAsync(CancellationToken ct)
    {
        await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id",
                    ObjectId.Parse(Fixture().AssignmentId)),
                Builders<BsonDocument>.Update
                    .Set("updatedAtUtc", DateTime.UtcNow),
                cancellationToken: ct);
    }

    private async Task AwaitBasicJobsIdleAsync(CancellationToken ct)
    {
        var collection = RequireDatabase()
            .GetCollection<BsonDocument>("work_assignment_basic_summary_snapshots");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var active = await collection.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.In(
                    "refreshStatus",
                    new[] { "QUEUED", "RUNNING" }),
                cancellationToken: ct);
            if (active == 0)
                return;
            await Task.Delay(150, ct);
        }
        throw new TimeoutException("P9-BAS snapshot jobs did not become idle.");
    }
}

internal sealed record P9BasicConfigIdentity(
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash);

internal sealed record P9BasicIndexEvidence(
    IReadOnlyList<string> IndexNames,
    IReadOnlyList<string> WinningStages,
    bool RequestIndexUsed,
    bool BlockingSortAbsent,
    bool Passed);
