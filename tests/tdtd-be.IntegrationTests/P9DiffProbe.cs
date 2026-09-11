using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string DiffCommandLineSwitch = "--p9-diff-probe";
    private const string DiffPromptId = "P9-06";
    private const string DiffGroupId = "P9-DIF";
    private const string DiffCatalogRawSha256 =
        "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68";
    private const string DiffCatalogSemanticSha256 =
        "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36";
    private const string DiffStageLockSha256 =
        "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397";
    private const string DiffFieldId = "p9-diff-field";
    private const string DiffBlockId = "p9-diff-block";
    private const string DiffMetricKey = "metric:number";
    private static readonly string[] DiffRequiredOracles =
    [
        "API_KESTREL", "AUTH_BEFORE_EXISTENCE", "DIRECT_MONGO",
        "COLLECTION_DELTA", "CONFIG_HASH_RECOMPUTE", "RECEIPT",
        "QUEUE_TRACE", "INDEX_EXPLAIN"
    ];

    internal static readonly string[] DiffExpectedCaseIds =
    [
        "P9-DIF-FIELD-01", "P9-DIF-FIELD-02", "P9-DIF-FIELD-03",
        "P9-DIF-FIELD-04", "P9-DIF-FIELD-05", "P9-DIF-FIELD-06",
        "P9-DIF-TABLE-METRIC-01", "P9-DIF-TABLE-METRIC-02",
        "P9-DIF-TABLE-METRIC-03", "P9-DIF-TABLE-METRIC-04",
        "P9-DIF-TABLE-METRIC-05", "P9-DIF-TABLE-METRIC-06",
        "P9-DIF-ROW-LABEL-01", "P9-DIF-ROW-LABEL-02",
        "P9-DIF-ROW-LABEL-03", "P9-DIF-ROW-LABEL-04",
        "P9-DIF-ROW-LABEL-05", "P9-DIF-ROW-LABEL-06",
        "P9-DIF-OWNER-01", "P9-DIF-OWNER-02",
        "P9-DIF-OWNER-03", "P9-DIF-OWNER-04"
    ];

    private IReadOnlyDictionary<string, P9CollectionState>? _difBefore;
    private IReadOnlyDictionary<string, P9CollectionState>? _difAfter;
    private readonly List<P9DiffConfigPin> _difConfigs = [];
    private readonly List<string> _difResultIds = [];
    private P9DiffIndexEvidence? _difIndexEvidence;
    private JsonObject? _difFieldResult;
    private JsonObject? _difTableResult;
    private JsonObject? _difLabelResult;
    private bool _difStaleZeroWrite;
    private bool _difForbiddenZeroWrite;

    public static async Task<int> RunDiffAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-DIF probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }
        var runKey =
            $"p906_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, DiffPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteDiffAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteDiffAsync(CancellationToken ct)
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
                    P9StatRunCandidate = BuildDiffCandidateOptions(),
                    HangfireServerEnabled = false
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareDiffFixtureAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildDiffEnvironmentEvidence(startedAtUtc),
                ct);
            _difBefore = await CaptureDatabaseSnapshotAsync(ct);
            await RunDiffCasesAsync(ct);
            _difIndexEvidence = await CaptureDiffIndexEvidenceAsync(ct);
            _difAfter = await CaptureDatabaseSnapshotAsync(ct);
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
        await WriteDiffCleanupArtifactAsync(
            cleanupSucceeded, cleanupErrors, completedAtUtc, ct);
        var passed = await WriteDiffEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-DIF passed 22/22; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-DIF failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildDiffCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            DiffPromptId);
        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            DiffCatalogRawSha256,
            DiffCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256,
            SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            DiffStageLockSha256);
    }

    private object BuildDiffEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_DIF_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            groupId = DiffGroupId,
            runKey = _runKey,
            startedAtUtc,
            workspaceRoot = _paths.WorkspaceRoot,
            runRoot = _paths.RunRoot,
            databaseName = RequireMongo().DatabaseName,
            replicaSetName = RequireMongo().ReplicaSetName,
            backendProcessId = RequireBackend().ProcessId,
            backendPort = RequireBackend().Port,
            hangfireServer = false,
            resultOwner = "work_report_statistic_diff_results",
            runIdEqualsResultId = true,
            candidate = new
            {
                stage = 4,
                stageLockSha256 = DiffStageLockSha256,
                catalogRawSha256 = DiffCatalogRawSha256,
                catalogSemanticSha256 = DiffCatalogSemanticSha256,
                promotions = new[] { "DIFF" }
            }
        };

    private static string DiffSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed record P9DiffConfigPin(
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string ConfigHash,
    string Status,
    string ConceptKind,
    string ConceptKey,
    string ConceptCode);

internal sealed record P9DiffIndexEvidence(
    IReadOnlyList<string> IndexNames,
    IReadOnlyList<string> ExplainStages,
    IReadOnlyList<string> UsedIndexes,
    bool RequiredIndexesPresent,
    bool TtlVerified,
    bool IdentityIndexUsed,
    bool BlockingSortAbsent,
    bool Passed);
