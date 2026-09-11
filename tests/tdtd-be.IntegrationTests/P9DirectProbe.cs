using System.Net;
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
    public const string DirectCommandLineSwitch = "--p9-direct-probe";
    private const string DirectPromptId = "P9-03";
    private const string DirectGroupId = "P9-DIR";
    private const string DirectCatalogRawSha256 =
        "37181541b61cc765d2885ba06181748c2ca690a63059a1504b20f448099427bd";
    private const string DirectCatalogSemanticSha256 =
        "942108c8b2f8ab868b9c138e441a10bc697b78197beb1c3d22f1bebb15434c92";
    private const string DirectStageLockSha256 =
        "6ab0511c24e4726c200fff47fa593ffd2c4e117cf32f5d94b8c780a92e1fd853";

    private static readonly string[] DirectRequiredOracles =
    [
        "API_KESTREL",
        "AUTH_BEFORE_EXISTENCE",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "CONFIG_HASH_RECOMPUTE",
        "INDEX_EXPLAIN",
        "SECURITY_SCAN"
    ];

    internal static readonly string[] DirectExpectedCaseIds =
    [
        "P9-DIR-FIELD-01", "P9-DIR-FIELD-02", "P9-DIR-FIELD-03", "P9-DIR-FIELD-04",
        "P9-DIR-FIELD-05", "P9-DIR-FIELD-06", "P9-DIR-FIELD-07", "P9-DIR-FIELD-08",
        "P9-DIR-TABLE-01", "P9-DIR-TABLE-02", "P9-DIR-TABLE-03", "P9-DIR-TABLE-04",
        "P9-DIR-TABLE-05", "P9-DIR-TABLE-06", "P9-DIR-TABLE-07", "P9-DIR-TABLE-08",
        "P9-DIR-LABEL-01", "P9-DIR-LABEL-02", "P9-DIR-LABEL-03", "P9-DIR-LABEL-04",
        "P9-DIR-LABEL-05", "P9-DIR-LABEL-06", "P9-DIR-LABEL-07", "P9-DIR-LABEL-08",
        "P9-DIR-PAGE-FIELD-01", "P9-DIR-PAGE-FIELD-02", "P9-DIR-PAGE-FIELD-03", "P9-DIR-PAGE-FIELD-04",
        "P9-DIR-PAGE-TABLE-01", "P9-DIR-PAGE-TABLE-02", "P9-DIR-PAGE-TABLE-03", "P9-DIR-PAGE-TABLE-04",
        "P9-DIR-PAGE-LABEL-01", "P9-DIR-PAGE-LABEL-02", "P9-DIR-PAGE-LABEL-03", "P9-DIR-PAGE-LABEL-04"
    ];

    private IReadOnlyDictionary<string, P9CollectionState>? _dirBefore;
    private IReadOnlyDictionary<string, P9CollectionState>? _dirAfter;

    public static async Task<int> RunDirectAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-DIR probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p903_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, DirectPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteDirectAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteDirectAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;

        try
        {
            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);

            // P9-02 owns projection. Build its completed immutable generation
            // first, then restart only Kestrel under the P9-03 descendant.
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "projection"),
                $"{_runKey}_projection",
                _mongo,
                ct,
                new BackendServerOptions
                {
                    P9StatRunCandidate = BuildLifecycleCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareLifecycleFixtureAsync(ct);
            await RunDraftCasesAsync(ct);
            await RunSubmitCasesAsync(ct);
            await RunApproveCasesAsync(ct);
            await CaptureLifecycleOwnerIndexEvidenceAsync(ct);

            await NormalizeDirectRestartProvenanceAsync(ct);
            Console.WriteLine("[P9-DIR] projection and provenance ready; restarting Kestrel");
            await RestartBackendForDirectAsync(ct);
            Console.WriteLine("[P9-DIR] descendant Kestrel and actor sessions ready");
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildDirectEnvironmentEvidence(startedAtUtc),
                ct);
            Console.WriteLine("[P9-DIR] capturing zero-write baseline");
            _dirBefore = await CaptureDatabaseSnapshotAsync(ct);
            Console.WriteLine("[P9-DIR] running exact 36-case registry");
            await RunDirectCasesAsync(ct);
            Console.WriteLine("[P9-DIR] capturing zero-write terminal snapshot");
            _dirAfter = await CaptureDatabaseSnapshotAsync(ct);
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
        await WriteDirectCleanupArtifactAsync(
            cleanupSucceeded, cleanupErrors, completedAtUtc, ct);
        var passed = await WriteDirectEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);

        Console.WriteLine(
            passed
                ? $"[DAT] P9-DIR passed 36/36; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-DIR failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task RestartBackendForDirectAsync(CancellationToken ct)
    {
        var seededActorPassword = RequireBackend().ActorPassword;
        _api?.Dispose();
        if (_backend is not null)
        {
            await _backend.StopAsync();
            await _backend.DisposeAsync();
        }

        _backend = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, "direct"),
            $"{_runKey}_direct",
            RequireMongo(),
            ct,
            new BackendServerOptions
            {
                P9StatRunCandidate = BuildDirectCandidateOptions()
            });
        _api = new ApiHarnessClient(_backend.BaseUri);

        foreach (var key in _actors.Keys.ToArray())
        {
            var password = key == "admin"
                ? _bootstrapPassword ?? throw new InvalidOperationException("Bootstrap password unavailable.")
                : seededActorPassword;
            var token = await _api.LoginAsync(_actors[key].Username, password, ct);
            RememberSecret(token);
            _actors[key] = _actors[key] with { Token = token };
        }
    }

    private async Task NormalizeDirectRestartProvenanceAsync(
        CancellationToken ct)
    {
        var template = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_form_templates")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().TemplateId)))
            .SingleAsync(ct);
        var schemaHash = BsonString(template, "publishedSchemaHash");
        var templateId = ObjectId.Parse(Fixture().TemplateId);
        var runtimeOwners = new[]
        {
            "work_assignments",
            "work_template_assignees",
            "work_report_periods",
            "work_assignment_report",
            "work_assignment_report_sections",
            "assignment_list_doc_roles",
            "my_report_period_list_doc_roles",
            "review_report_list_doc_roles"
        };
        foreach (var owner in runtimeOwners)
        {
            await RequireDatabase()
                .GetCollection<BsonDocument>(owner)
                .UpdateManyAsync(
                    new BsonDocument("dynamicFormTemplateId", templateId),
                    Builders<BsonDocument>.Update.Set(
                        "dynamicFormSchemaHash",
                        schemaHash),
                    cancellationToken: ct);
        }
    }

    private P9StatRunCandidateOptions BuildDirectCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            DirectPromptId);
        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            DirectCatalogRawSha256,
            DirectCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256,
            SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            DirectStageLockSha256);
    }

    private object BuildDirectEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_DIR_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = DirectPromptId,
            groupId = DirectGroupId,
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
                catalogRawSha256 = DirectCatalogRawSha256,
                catalogSemanticSha256 = DirectCatalogSemanticSha256,
                schemaRawSha256 = SchemaRawSha256,
                schemaSemanticSha256 = SchemaSemanticSha256,
                stageLockSha256 = DirectStageLockSha256,
                stage = 1,
                promotions = new[] { StatRunCapabilities.DirectFieldTableLabel }
            },
            projection = new
            {
                ownerPrompt = LifecyclePromptId,
                stageLockSha256 = LifecycleStageLockSha256,
                runId = _lfcRunId,
                generationId = _lfcGenerationId,
                generationHash = _lfcGenerationHash
            }
        };

    private async Task RunDirectCaseAsync(
        string caseId,
        Func<Task<CaseObservation>> action)
        => await _cases.RunAsync(caseId, action);
}
