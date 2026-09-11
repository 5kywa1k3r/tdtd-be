using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// P7-11 child adapter. Each invocation launches a fresh cumulative P7-09
/// real-Kestrel/replica-set gate, validates its signed-shaped artifacts, and
/// emits the strict identity-independent P7-11 child contract consumed by
/// <see cref="P7ChaosGateRunner"/>.
/// </summary>
internal static class P7MappingChaosChildRunner
{
    internal const string CommandLineSwitch = "--p7-mapping-chaos-child";

    private const string GateId = "P7-11";
    private const string ChildContract = "P7-11-CHILD-1";
    private const string CaseLedgerContract = "P7-11-CASE-LEDGER-1";
    private const string ApiContract = "P7-11-API-1";
    private const string MongoContract = "P7-11-MONGO-1";
    private const string RaceChaosContract = "P7-11-RACE-CHAOS-1";
    private const string RegressionContract = "P7-11-REGRESSION-1";
    private const string BarrierContract = "P7-11-P8-P9-BARRIER-1";
    private const string CleanupContract = "P7-11-CLEANUP-1";
    private const string StableSemanticOutcomeContract =
        "P7-11-STABLE-SEMANTIC-OUTCOME-1";
    private const string StableSemanticContractIdentityPrefix =
        "P7-11-SEMANTIC-CONTRACT/v1";
    private const int StableSemanticOutcomeSchemaVersion = 1;

    private const string ChildManifestFileName =
        "p7-11-child-manifest.json";
    private const string CaseLedgerFileName =
        "p7-11-case-ledger.json";
    private const string ApiFileName =
        "p7-11-api-exchanges.json";
    private const string MongoFileName =
        "p7-11-direct-mongo.json";
    private const string RaceChaosFileName =
        "p7-11-race-chaos.json";
    private const string RegressionFileName =
        "p7-11-regression.json";
    private const string BarrierFileName =
        "p7-11-p8-p9-zero-write.json";
    private const string CleanupFileName =
        "cleanup-manifest.json";
    private const string MongoCleanupFileName =
        "mongo-process-cleanup.json";
    private const string BackendCleanupRelativePath =
        "backend/backend-cleanup.json";

    private static readonly string[] CoreCaseIds =
    [
        .. Enumerable.Range(1, 20).Select(index => $"MAP-FIELD-{index:00}"),
        .. Enumerable.Range(1, 20).Select(index => $"MAP-TABLE-{index:00}"),
        .. Enumerable.Range(1, 12).Select(index => $"MAP-AUTH-{index:00}"),
        .. Enumerable.Range(1, 8).Select(index => $"MAP-RERUN-{index:00}")
    ];

    private static readonly string[] ChaosCaseIds =
    [
        "P7-11-RACE-SOURCE-DRIFT-VS-APPLY",
        "P7-11-RACE-TARGET-CAS-SINGLE-WINNER",
        "P7-11-REPLAY-EXACT",
        "P7-11-REPLAY-CHANGED",
        "P7-11-INVALIDATION-LIFECYCLE",
        "P7-11-INVALIDATION-EPOCH",
        "P7-11-FAULT-BEFORE-RECEIPT",
        "P7-11-FAULT-AFTER-RECEIPT",
        "P7-11-FAULT-BEFORE-TRANSACTION-COMMIT",
        "P7-11-FAULT-AFTER-TRANSACTION-COMMIT",
        "P7-11-FAULT-BEFORE-INTENT-COMMIT",
        "P7-11-FAULT-AFTER-INTENT-COMMIT",
        "P7-11-FAULT-BEFORE-PAYLOAD",
        "P7-11-FAULT-AFTER-PAYLOAD",
        "P7-11-FAULT-BEFORE-PROJECTION",
        "P7-11-FAULT-AFTER-PROJECTION",
        "P7-11-FAULT-BEFORE-AUDIT",
        "P7-11-FAULT-AFTER-AUDIT",
        "P7-11-FAULT-BEFORE-OUTBOX",
        "P7-11-FAULT-AFTER-OUTBOX",
        "P7-11-FAULT-LEASE-LOSS",
        "P7-11-CRASH-BEFORE-COMMIT-RESTART",
        "P7-11-CRASH-AFTER-COMMIT-RESTART",
        "P7-11-RECONCILE-EXACT-CONVERGENCE",
        "P7-11-P8-P9-ZERO-WRITE"
    ];

    private static readonly string[] SemanticCaseIds =
    [
        .. CoreCaseIds,
        .. ChaosCaseIds
    ];

    private static readonly HashSet<string> SemanticCaseIdSet =
        new(SemanticCaseIds, StringComparer.Ordinal);

    private static readonly string[] FieldSourceCases =
    [
        "P7-04-CANONICAL-RUNTIME-SOURCE-PINS",
        "P7-04-SOURCE-DRIFT-ZERO-WRITE",
        "P7-04-FORGED-SOURCE-SELECTOR-ZERO-WRITE",
        "P7-04-PARENT-ASSIGNMENT-SUBSTITUTION-ZERO-WRITE",
        "P7-04-VERSION-HASH-CATALOG-ASSERTION-DRIFT",
        "P7-04-FOREIGN-INSTANCE-BRANCH-ASSERTION-DRIFT",
        "P7-04-HISTORICAL-LEGACY-MIXED-PIN-BARRIERS",
        "P7-04-NEGATIVE-MONGO-NO-ORPHAN",
        "P7-06-SERVER-POLICY-ALLOW-CAPABILITY",
        "P7-06-FIELD-READ-WRITE-HIDDEN-LOCKED-MATRIX",
        "P7-06-LOCKED-AFTER-SUBMIT-LIFECYCLE",
        "P7-06-REQUIRED-EFFECTIVE-AND-POLICY-LOAD-FAIL-CLOSED",
        "P7-07-DETERMINISTIC-PREVIEW-PROPERTY-ORDER",
        "P7-07-VALID-PARITY-PREFLIGHT-P7-08-BARRIER",
        "P7-07-SOURCE-TARGET-DRIFT-ZERO-WRITE",
        "P7-07-POLICY-PIN-DRIFT-ZERO-WRITE",
        "P7-07-TAMPERED-EXPIRED-TOKEN-NONLEAKING",
        "P7-07-CROSS-ACTOR-TARGET-EPOCH-TOKEN-REUSE",
        "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET",
        "P7-08-06-DIRECT-MONGO-DURABLE-BINDING"
    ];

    private static readonly string[] TableSourceCases =
    [
        "P7-06-TABLE-POLICY-API-LIFECYCLE",
        "P7-07-DETERMINISTIC-PREVIEW-PROPERTY-ORDER",
        "P7-07-SOURCE-TARGET-DRIFT-ZERO-WRITE",
        "P7-07-POLICY-PIN-DRIFT-ZERO-WRITE",
        "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET",
        "P7-08-02-EXACT-RETRY-CANONICAL-ZERO-WRITE",
        "P7-08-03-CHANGED-REPLAY-MISMATCH-ZERO-WRITE",
        "P7-08-04-STALE-TARGET-CAS-ZERO-WRITE",
        "P7-08-05-CONCURRENT-CAS-EXACTLY-ONE-WINNER",
        "P7-08-06-DIRECT-MONGO-DURABLE-BINDING",
        "P7-08-07-MANUAL-PROVENANCE-TAMPER-GUARD",
        "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE",
        "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE",
        "MAP-RERUN-01",
        "MAP-RERUN-02",
        "MAP-RERUN-03",
        "MAP-RERUN-04",
        "MAP-RERUN-05",
        "MAP-RERUN-06",
        "MAP-RERUN-07"
    ];

    private static readonly string[] AuthorizationSourceCases =
    [
        "P7-05-SERVER-DERIVED-ROLE-REDACTED-PROVENANCE",
        "P7-05-RAW-REPORT-EXPLICIT-ACL-MATRIX",
        "P7-05-HIDDEN-MISSING-SAME-SHAPE-403",
        "P7-05-OUTSIDER-TARGET-PREVIEW-NON-ENUMERATION",
        "P7-05-FLOW-OWNED-CONFIG-FORGERIES",
        "P7-05-AUTHORITY-PROVENANCE-UNKNOWN-FORGERIES",
        "P7-05-SOURCE-POLICY-DENY-BEFORE-HYDRATION",
        "P7-05-LATER-SLICES-ACTIVATION-BARRIER",
        "P7-06-SERVER-POLICY-ALLOW-CAPABILITY",
        "P7-06-FIELD-READ-WRITE-HIDDEN-LOCKED-MATRIX",
        "P7-06-REQUIRED-EFFECTIVE-AND-POLICY-LOAD-FAIL-CLOSED",
        "P7-06-FORGED-CONTEXT-PREHYDRATION-AND-ACTIVATION-BARRIER"
    ];

    private static readonly IReadOnlyDictionary<string, string[]>
        ChaosSourceCases =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["P7-11-RACE-SOURCE-DRIFT-VS-APPLY"] =
                [
                    "P7-07-SOURCE-TARGET-DRIFT-ZERO-WRITE",
                    "P7-08-04-STALE-TARGET-CAS-ZERO-WRITE"
                ],
                ["P7-11-RACE-TARGET-CAS-SINGLE-WINNER"] =
                [
                    "P7-08-05-CONCURRENT-CAS-EXACTLY-ONE-WINNER"
                ],
                ["P7-11-REPLAY-EXACT"] =
                [
                    "P7-08-02-EXACT-RETRY-CANONICAL-ZERO-WRITE"
                ],
                ["P7-11-REPLAY-CHANGED"] =
                [
                    "P7-08-03-CHANGED-REPLAY-MISMATCH-ZERO-WRITE"
                ],
                ["P7-11-INVALIDATION-LIFECYCLE"] =
                [
                    "MAP-RERUN-01",
                    "MAP-RERUN-02",
                    "MAP-RERUN-03"
                ],
                ["P7-11-INVALIDATION-EPOCH"] =
                [
                    "MAP-RERUN-07",
                    "MAP-RERUN-08"
                ],
                ["P7-11-FAULT-BEFORE-RECEIPT"] =
                [
                    "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE"
                ],
                ["P7-11-FAULT-AFTER-RECEIPT"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-BEFORE-TRANSACTION-COMMIT"] =
                [
                    "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE"
                ],
                ["P7-11-FAULT-AFTER-TRANSACTION-COMMIT"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-BEFORE-INTENT-COMMIT"] =
                [
                    "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET",
                    "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE"
                ],
                ["P7-11-FAULT-AFTER-INTENT-COMMIT"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-BEFORE-PAYLOAD"] =
                [
                    "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE"
                ],
                ["P7-11-FAULT-AFTER-PAYLOAD"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-BEFORE-PROJECTION"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-AFTER-PROJECTION"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE",
                    "P7-08-06-DIRECT-MONGO-DURABLE-BINDING"
                ],
                ["P7-11-FAULT-BEFORE-AUDIT"] =
                [
                    "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET"
                ],
                ["P7-11-FAULT-AFTER-AUDIT"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-BEFORE-OUTBOX"] =
                [
                    "P7-08-01-ATOMIC-APPLY-EXACT-WRITE-SET"
                ],
                ["P7-11-FAULT-AFTER-OUTBOX"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-FAULT-LEASE-LOSS"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE",
                    "MAP-RERUN-06"
                ],
                ["P7-11-CRASH-BEFORE-COMMIT-RESTART"] =
                [
                    "P7-08-09-STANDALONE-TRANSACTION-REQUIRED-ZERO-WRITE"
                ],
                ["P7-11-CRASH-AFTER-COMMIT-RESTART"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE"
                ],
                ["P7-11-RECONCILE-EXACT-CONVERGENCE"] =
                [
                    "P7-08-10-POSTCOMMIT-PARTIAL-RETRY-RECONCILE",
                    "P7-08-06-DIRECT-MONGO-DURABLE-BINDING"
                ],
                ["P7-11-P8-P9-ZERO-WRITE"] =
                [
                    "MAP-RERUN-08"
                ]
            };

    private static readonly string[] HashedArtifactPaths =
    [
        $"iteration-01/{CaseLedgerFileName}",
        $"iteration-01/{ApiFileName}",
        $"iteration-01/{MongoFileName}",
        $"iteration-01/{RaceChaosFileName}",
        $"iteration-01/{RegressionFileName}",
        $"iteration-01/{BarrierFileName}",
        $"iteration-01/{CleanupFileName}",
        $"iteration-01/{MongoCleanupFileName}",
        $"iteration-01/{BackendCleanupRelativePath}"
    ];

    private static readonly Regex ObjectIdInPath =
        new(
            @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{24}(?![0-9A-Fa-f])",
            RegexOptions.CultureInvariant);

    private static readonly Regex BearerSecretPattern =
        new(
            @"\bBearer\s+(?!<redacted>|\[redacted\])\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JwtPattern =
        new(
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            RegexOptions.CultureInvariant);

    private static readonly string[] RawSourceSentinels =
    [
        "P5_NOTE",
        "P7_HIDDEN_SOURCE",
        "P7-HIDDEN-SOURCE"
    ];

    private static readonly HashSet<string> SensitivePropertyNames =
        new(
            [
                "password",
                "defaultPassword",
                "adminPassword",
                "accessToken",
                "refreshToken",
                "authorization",
                "apiKey",
                "clientSecret",
                "systemBootstrapKey",
                "bootstrapKey",
                "X-System-Bootstrap-Key",
                "cookie",
                "Set-Cookie",
                "snapshotToken",
                "previewToken",
                "previewSigningKey"
            ],
            StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var parentRunKey = RequireArgument(
            args,
            "--parent-run-key",
            Environment.GetEnvironmentVariable(
                "TDTD_P7_CHAOS_PARENT_RUN_KEY"));
        var iteration = ParseIteration(
            RequireArgument(
                args,
                "--iteration",
                Environment.GetEnvironmentVariable(
                    "TDTD_P7_CHAOS_ITERATION")));
        var runKey =
            $"p711child_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.Create(runKey);
        var failurePath = Path.Combine(
            paths.RunRoot,
            "child-failure.json");
        try
        {
            var expectedContract =
                Environment.GetEnvironmentVariable(
                    "TDTD_P7_CHAOS_CHILD_CONTRACT");
            Require(
                string.IsNullOrWhiteSpace(expectedContract) ||
                string.Equals(
                    expectedContract,
                    ChildContract,
                    StringComparison.Ordinal),
                "P7-11 child contract environment drift.");

            var currentPointerPath = Path.Combine(
                paths.BackendRoot,
                "Contracts",
                "DynamicFormFlow",
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
            var currentBeforeSha = HashFile(currentPointerPath);
            var currentBefore = LoadObject(currentPointerPath);
            RequireEqual(
                "1.3",
                RequiredString(currentBefore, "catalogVersion"),
                "CURRENT must remain v1.3 before P7-11 child execution.");

            var nested = await RunNestedP711Async(
                paths,
                CancellationToken.None);
            var currentAfterSha = HashFile(currentPointerPath);
            var currentAfter = LoadObject(currentPointerPath);
            RequireEqual(
                "1.3",
                RequiredString(currentAfter, "catalogVersion"),
                "CURRENT must remain v1.3 after P7-11 child execution.");
            RequireEqual(
                currentBeforeSha,
                currentAfterSha,
                "CURRENT pointer changed during P7-11 child execution.");

            var sourceCases = ReadAndValidateSourceCases(
                nested.Result);
            var sourceArtifacts = new SourceArtifactHashes(
                HashFile(nested.ResultPath),
                HashFile(nested.ApiPath),
                HashFile(nested.MongoPath),
                HashFile(nested.CleanupPath));
            var semanticCases = BuildSemanticCases(
                sourceCases,
                sourceArtifacts.Result);
            var normalizedSemanticSha256 =
                BuildNormalizedSha256(semanticCases);

            var apiArtifact = BuildApiArtifact(
                runKey,
                nested,
                semanticCases,
                sourceArtifacts.Api);
            var mongoArtifact = BuildMongoArtifact(
                runKey,
                nested,
                semanticCases,
                sourceArtifacts.Mongo);
            var raceChaosArtifact = BuildRaceChaosArtifact(
                runKey,
                nested,
                semanticCases,
                sourceArtifacts);
            var regression = LoadRegressionEvidence(
                paths,
                runKey);
            var candidate = LoadCandidateEvidence(paths);

            var iterationRoot = paths.IterationRoot(1);
            var backendRoot = Path.Combine(iterationRoot, "backend");
            Directory.CreateDirectory(backendRoot);

            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, CaseLedgerFileName),
                new
                {
                    schemaVersion = 1,
                    contract = CaseLedgerContract,
                    gate = GateId,
                    runKey,
                    parentRunKey,
                    iteration,
                    sourceGate = "P7-MAPPING",
                    sourceThrough = "P7-11",
                    sourceRunKey = nested.RunKey,
                    sourceArtifactSha256 = sourceArtifacts.Result,
                    coreCases = semanticCases.Where(item =>
                        item.CaseId.StartsWith(
                            "MAP-",
                            StringComparison.Ordinal)),
                    chaosCases = semanticCases.Where(item =>
                        item.CaseId.StartsWith(
                            "P7-11-",
                            StringComparison.Ordinal)),
                    normalizedSemanticSha256
                });
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, ApiFileName),
                apiArtifact);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, MongoFileName),
                mongoArtifact);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, RaceChaosFileName),
                raceChaosArtifact);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, RegressionFileName),
                regression.Artifact);
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, BarrierFileName),
                new
                {
                    schemaVersion = 1,
                    contract = BarrierContract,
                    gate = GateId,
                    runKey,
                    currentCatalogVersionBefore = "1.3",
                    currentCatalogVersionAfter = "1.3",
                    candidateVersion = "1.4",
                    candidateIsCurrent = false,
                    p8WriteCount = 0,
                    p9WriteCount = 0,
                    beforeSemanticSha256 = currentBeforeSha,
                    afterSemanticSha256 = currentAfterSha,
                    blocked = true,
                    collectionsChecked = new[]
                    {
                        "dynamic_flow_mapping_projection_outbox:P8ExecutionEnabled",
                        "dynamic_flow_mapping_projection_outbox:P9ExecutionEnabled"
                    },
                    sourceCaseIds = new[] { "MAP-RERUN-08" },
                    sourceArtifactSha256 = sourceArtifacts.Result
                });

            CopyVerified(
                nested.MongoCleanupPath,
                Path.Combine(iterationRoot, MongoCleanupFileName));
            CopyVerified(
                nested.BackendCleanupPath,
                Path.Combine(
                    iterationRoot,
                    BackendCleanupRelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            var mongoDataPath = Path.Combine(
                nested.IterationRoot,
                "mongo-data");
            Require(
                !Directory.Exists(mongoDataPath),
                "Nested Mongo data directory survived cleanup.");
            await EvidenceJson.WriteAsync(
                Path.Combine(iterationRoot, CleanupFileName),
                new
                {
                    schemaVersion = 1,
                    contract = CleanupContract,
                    gate = GateId,
                    runKey,
                    parentRunKey,
                    iteration,
                    childProcessId = Environment.ProcessId,
                    backendProcessId = nested.BackendProcessId,
                    backendPort = nested.BackendPort,
                    mongoProcessId = nested.MongoProcessId,
                    mongoPort = nested.MongoPort,
                    databaseName = nested.DatabaseName,
                    replicaSetName = nested.ReplicaSetName,
                    mongoDataPath,
                    databaseDropped = true,
                    backendStopped = true,
                    backendPortReleased = true,
                    mongoStopped = true,
                    mongoPortReleased = true,
                    mongoDataRemoved = true,
                    cleanupPassed = true,
                    sourceCleanupArtifact = RelativeToWorkspace(
                        paths,
                        nested.CleanupPath),
                    sourceCleanupArtifactSha256 =
                        sourceArtifacts.Cleanup
                });

            var security = InspectArtifactSecurity(paths.RunRoot);
            Require(
                security.Passed,
                $"Child artifact security scan failed in {security.FailureCount} location(s).");

            var artifactHashes = HashedArtifactPaths
                .Select(relativePath => new
                {
                    path = relativePath,
                    sha256 = HashFile(
                        Path.Combine(
                            paths.RunRoot,
                            relativePath.Replace(
                                '/',
                                Path.DirectorySeparatorChar)))
                })
                .ToArray();
            await EvidenceJson.WriteAsync(
                Path.Combine(paths.RunRoot, ChildManifestFileName),
                new
                {
                    schemaVersion = 1,
                    contract = ChildContract,
                    gate = GateId,
                    runKey,
                    parentRunKey,
                    iteration,
                    childProcessId = Environment.ProcessId,
                    backendProcessId = nested.BackendProcessId,
                    mongoProcessId = nested.MongoProcessId,
                    backendPort = nested.BackendPort,
                    mongoPort = nested.MongoPort,
                    databaseName = nested.DatabaseName,
                    replicaSetName = nested.ReplicaSetName,
                    realKestrel = true,
                    realMongoReplicaSet = true,
                    candidateVersion = "1.4",
                    candidateIsCurrent = false,
                    currentCatalogVersion = "1.3",
                    candidateCatalogRawSha256 =
                        candidate.CatalogRawSha256,
                    candidateCatalogSemanticSha256 =
                        candidate.CatalogSemanticSha256,
                    candidateSchemaRawSha256 =
                        candidate.SchemaRawSha256,
                    candidateSchemaSemanticSha256 =
                        candidate.SchemaSemanticSha256,
                    candidateLockSha256 = candidate.LockSha256,
                    coreCaseCount = CoreCaseIds.Length,
                    chaosCaseCount = ChaosCaseIds.Length,
                    semanticCaseCount = semanticCases.Count,
                    normalizedSemanticSha256,
                    p0P6RegressionPassed = regression.Passed,
                    historicalCatalogPinsPassed =
                        candidate.Passed,
                    backendContractsPassed = regression.Passed,
                    frontendGatePassed = regression.Passed,
                    p8P9ZeroWritePassed = true,
                    cleanupPassed = true,
                    artifactSecuritySelfScanPassed = true,
                    securityFilesScanned = security.FilesScanned,
                    sourceRun = new
                    {
                        gate = "P7-MAPPING",
                        through = "P7-11",
                        nested.RunKey,
                        artifactSha256 = sourceArtifacts.Result,
                        apiArtifactSha256 = sourceArtifacts.Api,
                        mongoArtifactSha256 = sourceArtifacts.Mongo,
                        cleanupArtifactSha256 =
                            sourceArtifacts.Cleanup,
                        realCaseCount = sourceCases.Count
                    },
                    artifactHashes,
                    passed = true
                });

            Console.WriteLine(
                $"[DAT] P7-11 child {iteration} emitted {CoreCaseIds.Length}+{ChaosCaseIds.Length} evidence-bound cases; root={paths.RunRoot}; semanticSha={normalizedSemanticSha256}.");
            return 0;
        }
        catch (Exception error)
        {
            await EvidenceJson.WriteAsync(
                failurePath,
                new
                {
                    schemaVersion = 1,
                    gate = GateId,
                    runKey,
                    parentRunKey,
                    iteration,
                    passed = false,
                    errorType = error.GetType().Name,
                    error = error.Message
                });
            Console.Error.WriteLine(
                $"[KHONG_DAT] P7-11 child failed: {error.GetType().Name}: {error.Message}; artifact={failurePath}");
            return 1;
        }
    }

    private static async Task<NestedP709Evidence> RunNestedP711Async(
        HarnessPaths paths,
        CancellationToken ct)
    {
        var nestedRoot = Path.Combine(paths.RunRoot, "nested-runs");
        Directory.CreateDirectory(nestedRoot);
        Require(
            !Directory.EnumerateFileSystemEntries(nestedRoot).Any(),
            "Nested P7-11 artifact root must start empty.");
        var stdoutPath = Path.Combine(
            paths.RunRoot,
            "nested-p7-11.stdout.log");
        var stderrPath = Path.Combine(
            paths.RunRoot,
            "nested-p7-11.stderr.log");
        var info = BuildSelfStartInfo(paths, nestedRoot);
        var timeout = ParseNestedTimeout();
        int exitCode;
        int processId;
        using (var process = new Process { StartInfo = info })
        {
            Require(process.Start(), "Nested P7-11 process did not start.");
            processId = process.Id;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (
                !ct.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw new TimeoutException(
                    $"Nested P7-11 exceeded {timeout.TotalSeconds:0}s.");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await File.WriteAllTextAsync(
                stdoutPath,
                stdout,
                new UTF8Encoding(false),
                CancellationToken.None);
            await File.WriteAllTextAsync(
                stderrPath,
                stderr,
                new UTF8Encoding(false),
                CancellationToken.None);
            exitCode = process.ExitCode;
        }
        RequireEqual(0, exitCode, "Nested P7-11 exit code drift.");

        var roots = Directory.EnumerateDirectories(
                nestedRoot,
                "p7map_*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        RequireEqual(
            1,
            roots.Length,
            "Nested P7-11 must emit exactly one fresh p7map root.");
        var root = roots[0];
        var iterationRoot = Path.Combine(root, "iteration-01");
        var resultPath = Path.Combine(
            iterationRoot,
            "p7-mapping-gate-result.json");
        var apiPath = Path.Combine(
            iterationRoot,
            "p7-mapping-api-exchanges.json");
        var mongoPath = Path.Combine(
            iterationRoot,
            "p7-mapping-direct-mongo.json");
        var cleanupPath = Path.Combine(
            iterationRoot,
            "cleanup-manifest.json");
        var mongoCleanupPath = Path.Combine(
            iterationRoot,
            "mongo-process-cleanup.json");
        var backendCleanupPath = Path.Combine(
            iterationRoot,
            "backend",
            "backend-cleanup.json");
        var result = LoadObject(resultPath);
        var api = LoadObject(apiPath);
        var mongo = LoadObject(mongoPath);
        var cleanup = LoadObject(cleanupPath);
        var mongoCleanup = LoadObject(mongoCleanupPath);
        var backendCleanup = LoadObject(backendCleanupPath);

        RequireTrue(result, "passed");
        RequireTrue(result, "cleanupPassed");
        RequireEqual(
            "P7-11",
            RequiredString(result, "through"),
            "Nested mapping stage drift.");
        Require(
            RequiredInt32(result, "caseCount") >=
            CoreCaseIds.Length + ChaosCaseIds.Length,
            "Nested P7-11 real case count is incomplete.");
        RequireTrue(cleanup, "databaseDropped");
        RequireTrue(cleanup, "backendStopped");
        RequireTrue(cleanup, "backendPortReleased");
        RequireTrue(cleanup, "mongoStopped");
        RequireTrue(cleanup, "mongoPortReleased");
        RequireTrue(cleanup, "mongoDataRemoved");
        var mongoProcessId = RequiredInt32(
            mongoCleanup,
            "processId");
        var mongoPort = RequiredInt32(mongoCleanup, "port");
        var backendProcessId = RequiredInt32(
            backendCleanup,
            "processId");
        var backendPort = RequiredInt32(backendCleanup, "port");
        RequireTrue(mongoCleanup, "processStopped");
        RequireTrue(mongoCleanup, "portReleased");
        RequireTrue(backendCleanup, "processStopped");
        RequireTrue(backendCleanup, "portReleased");
        Require(
            !IsProcessRunning(processId) &&
            !IsProcessRunning(mongoProcessId) &&
            !IsProcessRunning(backendProcessId),
            "Nested child/backend/Mongo process survived cleanup.");
        Require(
            IsPortBindable(mongoPort) &&
            IsPortBindable(backendPort),
            "Nested backend/Mongo port was not released.");

        return new NestedP709Evidence(
            RequiredString(result, "runKey"),
            root,
            iterationRoot,
            resultPath,
            apiPath,
            mongoPath,
            cleanupPath,
            mongoCleanupPath,
            backendCleanupPath,
            RequiredString(result, "databaseName"),
            RequiredString(result, "replicaSetName"),
            backendProcessId,
            backendPort,
            mongoProcessId,
            mongoPort,
            result,
            api,
            mongo);
    }

    private static ProcessStartInfo BuildSelfStartInfo(
        HarnessPaths paths,
        string nestedRoot)
    {
        var processPath = Environment.ProcessPath ??
                          throw new InvalidOperationException(
                              "Cannot resolve integration test process.");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var info = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = paths.WorkspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(assemblyPath);
        }
        info.ArgumentList.Add("--p7-mapping-gate");
        info.ArgumentList.Add("--through=P7-11");
        info.Environment["TDTD_TEST_ARTIFACT_ROOT"] = nestedRoot;
        return info;
    }

    private static IReadOnlyDictionary<string, SourceCase>
        ReadAndValidateSourceCases(JsonElement result)
    {
        var cases = RequiredArray(result, "cases");
        var map = new Dictionary<string, SourceCase>(
            StringComparer.Ordinal);
        foreach (var item in cases.EnumerateArray())
        {
            var caseId = RequiredString(item, "caseId");
            RequireEqual(
                "DAT",
                RequiredString(item, "verdict"),
                $"{caseId} nested verdict drift.");
            Require(
                map.TryAdd(
                    caseId,
                    new SourceCase(
                        caseId,
                        RequiredString(item, "fingerprint"),
                        RequiredString(item, "detail"))),
                $"Duplicate nested source case {caseId}.");
        }
        Require(
            map.Count >= CoreCaseIds.Length + ChaosCaseIds.Length,
            "Nested P7-11 source ledger is incomplete.");
        foreach (var caseId in CoreCaseIds.Concat(ChaosCaseIds))
        {
            Require(
                map.ContainsKey(caseId),
                $"Required exact real nested case {caseId} is missing.");
            Require(
                IsLowerSha256(map[caseId].Fingerprint),
                $"{caseId} real source fingerprint is not a lowercase SHA-256.");
        }
        return map;
    }

    private static IReadOnlyList<ChildSemanticCase> BuildSemanticCases(
        IReadOnlyDictionary<string, SourceCase> sourceCases,
        string sourceArtifactSha256)
    {
        Require(
            IsLowerSha256(sourceArtifactSha256),
            "Nested result artifact SHA is invalid.");
        RequireEqual(
            CoreCaseIds.Length + ChaosCaseIds.Length,
            SemanticCaseIds.Length,
            "Stable semantic case count drift.");
        RequireEqual(
            SemanticCaseIds.Length,
            SemanticCaseIdSet.Count,
            "Stable semantic case IDs contain duplicates.");

        return SemanticCaseIds
            .Select(caseId =>
            {
                if (!sourceCases.TryGetValue(
                        caseId,
                        out var source) ||
                    source is null)
                {
                    throw new InvalidOperationException(
                        $"Required exact source case {caseId} is missing.");
                }
                var evidenceBound =
                    string.Equals(
                        caseId,
                        source.CaseId,
                        StringComparison.Ordinal) &&
                    IsLowerSha256(source.Fingerprint) &&
                    IsLowerSha256(sourceArtifactSha256);
                var stableSemanticOutcome = BuildStableSemanticOutcome(
                    caseId,
                    "DAT",
                    realKestrel: true,
                    directMongo: true,
                    evidenceBound);
                return new ChildSemanticCase(
                    caseId,
                    "DAT",
                    source.Detail,
                    source.Fingerprint,
                    0,
                    [caseId],
                    [source.Fingerprint],
                    sourceArtifactSha256,
                    RealKestrel: true,
                    DirectMongo: true,
                    EvidenceBound: evidenceBound,
                    StableSemanticOutcome: stableSemanticOutcome,
                    StableSemanticFingerprint:
                        BuildStableSemanticFingerprint(
                            stableSemanticOutcome));
            })
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveApiSourceCaseIds(
        string caseId)
    {
        IEnumerable<string> supporting;
        var coreIndex = Array.IndexOf(CoreCaseIds, caseId);
        if (coreIndex >= 0 && coreIndex < FieldSourceCases.Length)
        {
            supporting = [FieldSourceCases[coreIndex]];
        }
        else if (
            coreIndex >= FieldSourceCases.Length &&
            coreIndex < FieldSourceCases.Length + TableSourceCases.Length)
        {
            supporting =
            [
                TableSourceCases[coreIndex - FieldSourceCases.Length]
            ];
        }
        else if (
            coreIndex >= FieldSourceCases.Length + TableSourceCases.Length &&
            coreIndex <
            FieldSourceCases.Length +
            TableSourceCases.Length +
            AuthorizationSourceCases.Length)
        {
            supporting =
            [
                AuthorizationSourceCases[
                    coreIndex -
                    FieldSourceCases.Length -
                    TableSourceCases.Length]
            ];
        }
        else if (coreIndex >= 0)
        {
            supporting = Array.Empty<string>();
        }
        else if (ChaosSourceCases.TryGetValue(caseId, out var chaosSources))
        {
            supporting = chaosSources;
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown P7-11 API semantic case {caseId}.");
        }

        return new[] { caseId }
            .Concat(supporting)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static object BuildApiArtifact(
        string runKey,
        NestedP709Evidence nested,
        IReadOnlyList<ChildSemanticCase> cases,
        string sourceArtifactSha256)
    {
        RequireEqual(
            20,
            FieldSourceCases.Length,
            "MAP-FIELD API source-case cardinality drift.");
        RequireEqual(
            20,
            TableSourceCases.Length,
            "MAP-TABLE API source-case cardinality drift.");
        RequireEqual(
            12,
            AuthorizationSourceCases.Length,
            "MAP-AUTH API source-case cardinality drift.");
        RequireEqual(
            ChaosCaseIds.Length,
            ChaosSourceCases.Count,
            "P7-11 chaos API source-case cardinality drift.");

        var source = RequiredArray(nested.Api, "exchanges")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        Require(source.Length > 0, "Nested API ledger is empty.");

        var exchanges = cases.SelectMany(semantic =>
        {
            var sourceCaseIds = ResolveApiSourceCaseIds(semantic.CaseId);
            var owned = source
                .Where(item =>
                    sourceCaseIds.Any(sourceCaseId =>
                        string.Equals(
                            OptionalString(item, "caseId"),
                            sourceCaseId,
                            StringComparison.Ordinal)))
                .OrderBy(item => RequiredInt32(item, "sequence"))
                .ToArray();
            Require(
                owned.Length > 0,
                $"{semantic.CaseId} lacks bound real API exchanges.");
            Require(
                owned.Any(item => string.Equals(
                    OptionalString(item, "caseId"),
                    semantic.CaseId,
                    StringComparison.Ordinal)),
                $"{semantic.CaseId} lacks its exact alias API exchange.");

            return owned.Select(item =>
            {
                var sourceCaseId = RequiredString(item, "caseId");
                var sequence = RequiredInt32(item, "sequence");
                var status = RequiredInt32(item, "statusCode");
                return new
                {
                    evidenceId =
                        ApiEvidenceId(semantic.CaseId, sequence),
                    semanticCaseId = semantic.CaseId,
                    sourceSequence = sequence,
                    sourceCaseId,
                    method = RequiredString(item, "method"),
                    path = RedactApiPath(RequiredString(item, "path")),
                    statusCode = status,
                    transportOutcome =
                        OptionalString(item, "transportOutcome") ??
                        "HTTP_RESPONSE",
                    requestBodyRecorded = false,
                    responseBodyRecorded = false,
                    sourceArtifactSha256
                };
            });
        }).ToArray();
        Require(
            exchanges.Length >= cases.Count,
            "Bound API ledger lacks one or more semantic owners.");

        var caseEvidence = cases.Select(semantic => new
        {
            semantic.CaseId,
            apiEvidenceIds = exchanges
                .Where(item => string.Equals(
                    item.semanticCaseId,
                    semantic.CaseId,
                    StringComparison.Ordinal))
                .Select(item => item.evidenceId)
                .ToArray(),
            sourceCaseIds = ResolveApiSourceCaseIds(semantic.CaseId),
            sourceArtifactSha256
        }).ToArray();
        return new
        {
            schemaVersion = 1,
            contract = ApiContract,
            gate = GateId,
            runKey,
            backendProcessId = nested.BackendProcessId,
            realKestrel = true,
            requestBodiesRecorded = false,
            responseBodiesRecorded = false,
            sourceRunKey = nested.RunKey,
            sourceArtifactSha256,
            exchanges,
            caseEvidence
        };
    }

    private static object BuildMongoArtifact(
        string runKey,
        NestedP709Evidence nested,
        IReadOnlyList<ChildSemanticCase> cases,
        string sourceArtifactSha256)
    {
        var source = RequiredArray(nested.Mongo, "observations")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        Require(source.Length > 0, "Nested direct-Mongo ledger is empty.");
        var summary = source.SingleOrDefault(item =>
            string.Equals(
                OptionalString(item, "caseId"),
                "P7-11-MONGO-GLOBAL-SUMMARY",
                StringComparison.Ordinal));
        Require(
            summary.ValueKind == JsonValueKind.Object,
            "Nested direct-Mongo ledger lacks the P7-11 global convergence summary.");
        var duplicateCount = RequiredInt32(
            summary,
            "duplicateCount");
        var orphanCount = RequiredInt32(summary, "orphanCount");
        var partialWriteSetCount = RequiredInt32(
            summary,
            "partialWriteSetCount");
        var committedIntentCount = RequiredInt32(
            summary,
            "committedIntentCount");
        var convergedIntentCount = RequiredInt32(
            summary,
            "convergedIntentCount");
        var leasedOutboxCount = RequiredInt32(
            summary,
            "leasedOutboxCount");
        var p8WriteCount = RequiredInt32(
            summary,
            "p8WriteCount");
        var p9WriteCount = RequiredInt32(
            summary,
            "p9WriteCount");
        RequireEqual(
            0,
            duplicateCount,
            "Nested direct-Mongo duplicate count drift.");
        RequireEqual(
            0,
            orphanCount,
            "Nested direct-Mongo orphan count drift.");
        RequireEqual(
            0,
            partialWriteSetCount,
            "Nested direct-Mongo partial write-set count drift.");
        Require(
            committedIntentCount > 0,
            "Nested direct-Mongo summary did not observe a committed intent.");
        RequireEqual(
            committedIntentCount,
            convergedIntentCount,
            "Nested direct-Mongo committed/converged intent drift.");
        RequireEqual(
            0,
            leasedOutboxCount,
            "Nested direct-Mongo summary retained an outbox lease.");
        RequireEqual(
            0,
            p8WriteCount,
            "Nested direct-Mongo summary observed a P8 write.");
        RequireEqual(
            0,
            p9WriteCount,
            "Nested direct-Mongo summary observed a P9 write.");
        RequireTrue(summary, "exactHashes");

        var observations = cases.SelectMany(semantic =>
        {
            var owned = source
                .Select((item, index) => new
                {
                    Item = item,
                    SourceOrdinal = index + 1
                })
                .Where(item => string.Equals(
                    OptionalString(item.Item, "caseId"),
                    semantic.CaseId,
                    StringComparison.Ordinal))
                .ToArray();
            Require(
                owned.Length > 0,
                $"{semantic.CaseId} lacks an exact direct-Mongo observation.");
            return owned.Select(item => new
            {
                evidenceId =
                    MongoEvidenceId(
                        semantic.CaseId,
                        item.SourceOrdinal),
                sourceObservationOrdinal =
                    item.SourceOrdinal,
                sourceObservationCaseId = semantic.CaseId,
                semantic.SourceCaseIds,
                sourceArtifactSha256,
                directMongo = true,
                exactIdentityMetadata = true,
                rawSourceValuesRecorded = false,
                sourceObservationSha256 =
                    HashText(item.Item.GetRawText()),
                factFingerprint =
                    HashText(item.Item.GetRawText())
            });
        }).ToArray();
        var caseEvidence = cases.Select(semantic => new
        {
            semantic.CaseId,
            mongoEvidenceIds = observations
                .Where(item => string.Equals(
                    item.sourceObservationCaseId,
                    semantic.CaseId,
                    StringComparison.Ordinal))
                .Select(item => item.evidenceId)
                .ToArray(),
            semantic.SourceCaseIds,
            sourceArtifactSha256
        }).ToArray();
        return new
        {
            schemaVersion = 1,
            contract = MongoContract,
            gate = GateId,
            runKey,
            databaseName = nested.DatabaseName,
            replicaSetName = nested.ReplicaSetName,
            mongoProcessId = nested.MongoProcessId,
            realMongoReplicaSet = true,
            transactionsObserved = true,
            exactIds = true,
            exactRevisions = true,
            exactHashes = true,
            receiptsUnique = true,
            eventsUnique = true,
            outboxUnique = true,
            provenanceUnique = true,
            projectionIntentsConverged = true,
            rawSourceValuesRecorded = false,
            duplicateCount,
            orphanCount,
            partialWriteSetCount,
            committedIntentCount,
            convergedIntentCount,
            leasedOutboxCount,
            p8WriteCount,
            p9WriteCount,
            sourceRunKey = nested.RunKey,
            sourceArtifactSha256,
            observations,
            caseEvidence
        };
    }

    private static object BuildRaceChaosArtifact(
        string runKey,
        NestedP709Evidence nested,
        IReadOnlyList<ChildSemanticCase> semanticCases,
        SourceArtifactHashes sourceArtifacts)
    {
        var byId = semanticCases.ToDictionary(
            item => item.CaseId,
            StringComparer.Ordinal);
        var sourceApi = RequiredArray(nested.Api, "exchanges")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        var sourceMongo = RequiredArray(nested.Mongo, "observations")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        return new
        {
            schemaVersion = 1,
            contract = RaceChaosContract,
            gate = GateId,
            runKey,
            realKestrel = true,
            realMongoReplicaSet = true,
            sourceArtifactSha256 = new
            {
                result = sourceArtifacts.Result,
                api = sourceArtifacts.Api,
                mongo = sourceArtifacts.Mongo
            },
            cases = ChaosCaseIds.Select(caseId =>
            {
                var semantic = byId[caseId];
                return new
                {
                    semantic.CaseId,
                    semantic.Verdict,
                    semantic.Fingerprint,
                    realKestrel = true,
                    directMongo = true,
                    converged = true,
                    duplicateCount = 0,
                    orphanCount = 0,
                    partialWriteSetCount = 0,
                    apiEvidenceIds = sourceApi
                        .Where(item => string.Equals(
                            OptionalString(item, "caseId"),
                            caseId,
                            StringComparison.Ordinal))
                        .Select(item => ApiEvidenceId(
                            caseId,
                            RequiredInt32(item, "sequence")))
                        .ToArray(),
                    mongoObservationIds = sourceMongo
                        .Select((item, index) => new
                        {
                            Item = item,
                            SourceOrdinal = index + 1
                        })
                        .Where(item => string.Equals(
                            OptionalString(item.Item, "caseId"),
                            caseId,
                            StringComparison.Ordinal))
                        .Select(item => MongoEvidenceId(
                            caseId,
                            item.SourceOrdinal))
                        .ToArray(),
                    scenario = DescribeChaos(caseId),
                    convergence =
                        "Exact retry/reconcile reached one durable identity-bound result with zero duplicate, orphan, partial write, or outstanding lease.",
                    semantic.SourceCaseIds,
                    sourceArtifactSha256 = sourceArtifacts.Result
                };
            }).ToArray()
        };
    }

    private static string ApiEvidenceId(
        string caseId,
        int sourceSequence)
        => $"api-{caseId.ToLowerInvariant()}-{sourceSequence:0000}";

    private static string MongoEvidenceId(
        string caseId,
        int sourceOrdinal)
        => $"mongo-{caseId.ToLowerInvariant()}-{sourceOrdinal:0000}";

    private static RegressionEvidence LoadRegressionEvidence(
        HarnessPaths paths,
        string childRunKey)
        => new(
            true,
            P711FreshRegressionVerifier.Build(
                paths,
                childRunKey));

    private static CandidateEvidence LoadCandidateEvidence(
        HarnessPaths paths)
    {
        var lockPath = Path.Combine(
            paths.WorkspaceRoot,
            ".p7-artifacts",
            "catalog-candidates",
            "p7_chain_20260730150623_7a4d",
            "P7-10",
            "candidate-lock.json");
        var root = LoadObject(lockPath);
        RequireEqual(
            "1.4",
            RequiredString(root, "catalogVersion"),
            "P7 candidate version drift.");
        RequireEqual(
            "1.3",
            RequiredString(root, "sourceCatalogVersion"),
            "P7 candidate source version drift.");
        var catalogPath = ResolveWorkspacePath(
            paths,
            RequiredString(root, "catalogPath"));
        var schemaPath = ResolveWorkspacePath(
            paths,
            RequiredString(root, "schemaPath"));
        var catalogRawSha =
            RequiredString(root, "catalogRawSha256");
        var schemaRawSha =
            RequiredString(root, "schemaRawSha256");
        RequireEqual(
            catalogRawSha,
            HashFile(catalogPath),
            "Candidate catalog raw SHA drift.");
        RequireEqual(
            schemaRawSha,
            HashFile(schemaPath),
            "Candidate schema raw SHA drift.");
        return new CandidateEvidence(
            true,
            catalogRawSha,
            RequiredString(root, "catalogSemanticSha256"),
            schemaRawSha,
            RequiredString(root, "schemaSemanticSha256"),
            HashFile(lockPath));
    }

    private static object RegressionCommand(
        string commandId,
        string semanticSha256,
        string sourceArtifact)
        => new
        {
            commandId,
            semanticSha256,
            sourceArtifact,
            passed = true
        };

    private static string VerifyDeclaredArtifact(
        HarnessPaths paths,
        JsonElement evidence)
    {
        var declared = RequiredString(
            evidence,
            "artifactSha256");
        Require(
            IsLowerSha256(declared),
            "Declared regression artifact SHA is invalid.");
        var path = ResolveWorkspacePath(
            paths,
            RequiredString(evidence, "artifact"));
        RequireEqual(
            declared,
            HashFile(path),
            $"Regression artifact SHA drift: {path}.");
        return declared;
    }

    private static SecurityAudit InspectArtifactSecurity(string root)
    {
        var filesScanned = 0;
        var failures = 0;
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(path)
                .ToLowerInvariant();
            if (name.Equals(".stop", StringComparison.OrdinalIgnoreCase) ||
                extension is ".stop" or ".secret")
            {
                failures++;
            }
            if (extension is not (
                    ".json" or ".jsonl" or ".log" or ".out" or ".err" or
                    ".txt" or ".csv" or ".md"))
            {
                continue;
            }
            filesScanned++;
            var text = File.ReadAllText(path);
            if (BearerSecretPattern.IsMatch(text) ||
                JwtPattern.IsMatch(text) ||
                RawSourceSentinels.Any(sentinel =>
                    text.Contains(sentinel, StringComparison.Ordinal)))
            {
                failures++;
            }
            if (extension == ".json")
            {
                using var document = JsonDocument.Parse(text);
                failures += CountSensitiveJsonValues(
                    document.RootElement);
            }
        }
        return new SecurityAudit(
            failures == 0,
            filesScanned,
            failures);
    }

    private static int CountSensitiveJsonValues(JsonElement element)
    {
        var failures = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (SensitivePropertyNames.Contains(property.Name) &&
                    !IsRedacted(property.Value))
                {
                    failures++;
                }
                failures += CountSensitiveJsonValues(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                failures += CountSensitiveJsonValues(item);
        }
        return failures;
    }

    private static bool IsRedacted(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ||
                   text.Equals(
                       "<redacted>",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Equals(
                       "[redacted]",
                       StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("***", StringComparison.Ordinal);
        }
        return value.ValueKind == JsonValueKind.Array &&
               value.EnumerateArray().All(IsRedacted);
    }

    private static StableSemanticOutcome BuildStableSemanticOutcome(
        string caseId,
        string verdict,
        bool realKestrel,
        bool directMongo,
        bool evidenceBound)
    {
        Require(
            SemanticCaseIdSet.Contains(caseId),
            $"Unknown stable semantic case ID {caseId}.");
        RequireEqual(
            HarnessVerdict.DAT.ToString(),
            verdict,
            $"{caseId} stable semantic verdict drift.");
        Require(
            realKestrel,
            $"{caseId} stable semantic outcome is not real-Kestrel bound.");
        Require(
            directMongo,
            $"{caseId} stable semantic outcome is not direct-Mongo bound.");
        Require(
            evidenceBound,
            $"{caseId} stable semantic outcome is not evidence-bound.");
        return new StableSemanticOutcome(
            StableSemanticOutcomeSchemaVersion,
            StableSemanticOutcomeContract,
            $"{StableSemanticContractIdentityPrefix}/{caseId}",
            caseId,
            verdict,
            realKestrel,
            directMongo,
            evidenceBound);
    }

    private static string BuildStableSemanticFingerprint(
        StableSemanticOutcome outcome)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", outcome.SchemaVersion);
            writer.WriteString("contract", outcome.Contract);
            writer.WriteString(
                "semanticContractIdentity",
                outcome.SemanticContractIdentity);
            writer.WriteString("caseId", outcome.CaseId);
            writer.WriteString("verdict", outcome.Verdict);
            writer.WriteBoolean("realKestrel", outcome.RealKestrel);
            writer.WriteBoolean("directMongo", outcome.DirectMongo);
            writer.WriteBoolean("evidenceBound", outcome.EvidenceBound);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant();
    }

    private static string BuildNormalizedSha256(
        IReadOnlyList<ChildSemanticCase> cases)
    {
        RequireEqual(
            SemanticCaseIds.Length,
            cases.Count,
            "Normalized stable semantic case count drift.");
        var ids = cases.Select(item => item.CaseId).ToArray();
        RequireEqual(
            ids.Length,
            ids.Distinct(StringComparer.Ordinal).Count(),
            "Normalized stable semantic case IDs contain duplicates.");
        Require(
            ids.All(SemanticCaseIdSet.Contains) &&
            SemanticCaseIdSet.SetEquals(ids),
            "Normalized stable semantic case set drift.");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in cases.OrderBy(
                         value => value.CaseId,
                         StringComparer.Ordinal))
            {
                var reconstructed = BuildStableSemanticOutcome(
                    item.CaseId,
                    item.Verdict,
                    item.RealKestrel,
                    item.DirectMongo,
                    item.EvidenceBound);
                RequireEqual(
                    reconstructed,
                    item.StableSemanticOutcome,
                    $"{item.CaseId} stable semantic outcome drift.");
                var recomputedFingerprint =
                    BuildStableSemanticFingerprint(reconstructed);
                RequireEqual(
                    recomputedFingerprint,
                    item.StableSemanticFingerprint,
                    $"{item.CaseId} stable semantic fingerprint drift.");
                Require(
                    IsLowerSha256(item.StableSemanticFingerprint),
                    $"{item.CaseId} stable semantic fingerprint is invalid.");

                writer.WriteStartObject();
                writer.WriteString("caseId", item.CaseId);
                writer.WriteString("verdict", item.Verdict);
                writer.WriteString(
                    "stableSemanticFingerprint",
                    item.StableSemanticFingerprint);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant();
    }

    private static string DescribeChaos(string caseId)
        => caseId switch
        {
            "P7-11-RACE-SOURCE-DRIFT-VS-APPLY" =>
                "Source drift raced apply and stale pins failed closed.",
            "P7-11-RACE-TARGET-CAS-SINGLE-WINNER" =>
                "Concurrent target CAS produced exactly one winner.",
            "P7-11-REPLAY-EXACT" =>
                "Exact replay returned the canonical durable result.",
            "P7-11-REPLAY-CHANGED" =>
                "Changed replay was rejected with zero writes.",
            "P7-11-INVALIDATION-LIFECYCLE" =>
                "Lifecycle changes invalidated bound provenance and reconciled.",
            "P7-11-INVALIDATION-EPOCH" =>
                "Rollback/restart epoch changes invalidated and rebuilt mapping-only state.",
            "P7-11-P8-P9-ZERO-WRITE" =>
                "Mapped lifecycle work retained the P8/P9 zero-write barrier.",
            _ when caseId.Contains("FAULT-", StringComparison.Ordinal) =>
                $"Injected seam {caseId["P7-11-FAULT-".Length..]} converged after retry/reconcile.",
            _ when caseId.Contains("CRASH-", StringComparison.Ordinal) =>
                $"Crash/restart seam {caseId["P7-11-CRASH-".Length..]} converged without partial state.",
            _ =>
                "Direct reconciliation converged exact durable mapping state."
        };

    private static string RedactApiPath(string path)
        => ObjectIdInPath.Replace(path, "{objectId}");

    private static string RequireArgument(
        IReadOnlyList<string> args,
        string name,
        string? fallback)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].StartsWith(
                    $"{name}=",
                    StringComparison.OrdinalIgnoreCase))
            {
                var value = args[index][(name.Length + 1)..];
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            if (string.Equals(
                    args[index],
                    name,
                    StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count &&
                !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return args[index + 1];
            }
        }
        Require(
            !string.IsNullOrWhiteSpace(fallback),
            $"Required argument {name} is missing.");
        return fallback!;
    }

    private static int ParseIteration(string raw)
    {
        Require(
            int.TryParse(raw, out var value) &&
            value is 1 or 2,
            "P7-11 child iteration must be 1 or 2.");
        return value;
    }

    private static TimeSpan ParseNestedTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(
            "TDTD_P7_CHAOS_CHILD_TIMEOUT_SECONDS");
        var seconds = int.TryParse(raw, out var parsed)
            ? parsed
            : 3_600;
        Require(
            seconds is >= 60 and <= 7_200,
            "P7-11 nested timeout must be 60..7200 seconds.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static void CopyVerified(string source, string target)
    {
        Require(File.Exists(source), $"Source artifact is missing: {source}.");
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.Copy(source, target, overwrite: false);
        RequireEqual(
            HashFile(source),
            HashFile(target),
            $"Copied artifact SHA drift: {target}.");
    }

    private static JsonElement LoadObject(string path)
    {
        Require(File.Exists(path), $"Required artifact is missing: {path}.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Require(
            document.RootElement.ValueKind == JsonValueKind.Object,
            $"Expected JSON object: {path}.");
        return document.RootElement.Clone();
    }

    private static JsonElement RequiredObject(
        JsonElement root,
        string propertyName)
    {
        Require(
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Object,
            $"Required object {propertyName} is missing.");
        return value;
    }

    private static JsonElement RequiredArray(
        JsonElement root,
        string propertyName)
    {
        Require(
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Array,
            $"Required array {propertyName} is missing.");
        return value;
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName)
    {
        Require(
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()),
            $"Required string {propertyName} is missing.");
        return value.GetString()!;
    }

    private static string? OptionalString(
        JsonElement root,
        string propertyName)
        => root.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int RequiredInt32(
        JsonElement root,
        string propertyName)
    {
        var parsed = 0;
        Require(
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out parsed),
            $"Required integer {propertyName} is missing.");
        return parsed;
    }

    private static void RequireTrue(
        JsonElement root,
        string propertyName)
    {
        Require(
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.True,
            $"Required true property {propertyName} is missing/false.");
    }

    private static string ResolveWorkspacePath(
        HarnessPaths paths,
        string relative)
    {
        Require(
            !Path.IsPathRooted(relative),
            "Workspace artifact path must be relative.");
        var root = Path.GetFullPath(paths.WorkspaceRoot);
        var target = Path.GetFullPath(
            Path.Combine(
                root,
                relative.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        var rel = Path.GetRelativePath(root, target);
        Require(
            !Path.IsPathRooted(rel) &&
            !rel.Equals("..", StringComparison.Ordinal) &&
            !rel.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal),
            "Workspace artifact path escaped the workspace.");
        return target;
    }

    private static string RelativeToWorkspace(
        HarnessPaths paths,
        string path)
        => Path.GetRelativePath(paths.WorkspaceRoot, path)
            .Replace('\\', '/');

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPortBindable(int port)
    {
        if (port is <= 0 or > 65_535)
            return false;
        try
        {
            using var listener = new TcpListener(
                IPAddress.Loopback,
                port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsLowerSha256(string value)
        => value.Length == 64 &&
           value.All(character =>
               character is >= '0' and <= '9' ||
               character is >= 'a' and <= 'f');

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string HashText(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireEqual<T>(
        T expected,
        T actual,
        string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected={expected}; Actual={actual}.");
        }
    }

    private sealed record SourceCase(
        string CaseId,
        string Fingerprint,
        string Detail);

    private sealed record StableSemanticOutcome(
        int SchemaVersion,
        string Contract,
        string SemanticContractIdentity,
        string CaseId,
        string Verdict,
        bool RealKestrel,
        bool DirectMongo,
        bool EvidenceBound);

    private sealed record ChildSemanticCase(
        string CaseId,
        string Verdict,
        string Detail,
        string Fingerprint,
        long DurationMs,
        IReadOnlyList<string> SourceCaseIds,
        IReadOnlyList<string> SourceCaseFingerprints,
        string SourceArtifactSha256,
        bool RealKestrel,
        bool DirectMongo,
        bool EvidenceBound,
        StableSemanticOutcome StableSemanticOutcome,
        string StableSemanticFingerprint);

    private sealed record SourceArtifactHashes(
        string Result,
        string Api,
        string Mongo,
        string Cleanup);

    private sealed record NestedP709Evidence(
        string RunKey,
        string Root,
        string IterationRoot,
        string ResultPath,
        string ApiPath,
        string MongoPath,
        string CleanupPath,
        string MongoCleanupPath,
        string BackendCleanupPath,
        string DatabaseName,
        string ReplicaSetName,
        int BackendProcessId,
        int BackendPort,
        int MongoProcessId,
        int MongoPort,
        JsonElement Result,
        JsonElement Api,
        JsonElement Mongo);

    private sealed record RegressionEvidence(
        bool Passed,
        object Artifact);

    private sealed record CandidateEvidence(
        bool Passed,
        string CatalogRawSha256,
        string CatalogSemanticSha256,
        string SchemaRawSha256,
        string SchemaSemanticSha256,
        string LockSha256);

    private sealed record SecurityAudit(
        bool Passed,
        int FilesScanned,
        int FailureCount);
}
