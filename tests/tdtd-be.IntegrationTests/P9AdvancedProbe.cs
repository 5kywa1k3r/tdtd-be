using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string AdvancedCommandLineSwitch = "--p9-advanced-probe";
    private const string AdvancedPromptId = "P9-05";
    private const string AdvancedGroupId = "P9-ADV";
    private const string AdvancedSectionId = "p9-advanced-main";
    private const string AdvancedCatalogRawSha256 =
        "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3";
    private const string AdvancedCatalogSemanticSha256 =
        "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f";
    private const string AdvancedStageLockSha256 =
        "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc";
    private static readonly string[] AdvancedRequiredOracles =
    [
        "API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA",
        "CONFIG_HASH_RECOMPUTE", "RECEIPT", "QUEUE_TRACE", "INDEX_EXPLAIN"
    ];
    internal static readonly string[] AdvancedExpectedCaseIds =
    [
        "P9-ADV-DAY-01", "P9-ADV-DAY-02", "P9-ADV-DAY-03",
        "P9-ADV-DAY-04", "P9-ADV-DAY-05", "P9-ADV-DAY-06",
        "P9-ADV-MONTH-01", "P9-ADV-MONTH-02", "P9-ADV-MONTH-03",
        "P9-ADV-MONTH-04", "P9-ADV-MONTH-05", "P9-ADV-MONTH-06",
        "P9-ADV-YEAR-01", "P9-ADV-YEAR-02", "P9-ADV-YEAR-03",
        "P9-ADV-YEAR-04", "P9-ADV-YEAR-05", "P9-ADV-YEAR-06",
        "P9-ADV-BUDGET-01", "P9-ADV-BUDGET-02",
        "P9-ADV-BUDGET-03", "P9-ADV-BUDGET-04"
    ];

    private IReadOnlyDictionary<string, P9CollectionState>? _advBefore;
    private IReadOnlyDictionary<string, P9CollectionState>? _advAfter;
    private string _advConfigId = string.Empty;
    private string _advVersionId = string.Empty;
    private string _advConfigHash = string.Empty;
    private long _advConfigRevision;
    private BsonDocument? _advDay;
    private BsonDocument? _advMonth;
    private BsonDocument? _advYear;
    private P9AdvancedIndexEvidence? _advIndexEvidence;
    private bool _advQuotaDenied;

    public static async Task<int> RunAdvancedAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-ADV probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }
        var runKey =
            $"p905_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, AdvancedPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteAdvancedAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteAdvancedAsync(CancellationToken ct)
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
                    P9StatRunCandidate = BuildAdvancedCandidateOptions(),
                    HangfireServerEnabled = true
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(
                ct,
                allowPreinitializedHangfireInfrastructure: true);
            await PrepareAdvancedFixtureAsync(ct);
            await ValidateAdvancedFixtureAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildAdvancedEnvironmentEvidence(startedAtUtc),
                ct);
            _advBefore = await CaptureDatabaseSnapshotAsync(ct);
            await RunAdvancedCasesAsync(ct);
            _advIndexEvidence = await CaptureAdvancedIndexEvidenceAsync(ct);
            _advAfter = await CaptureDatabaseSnapshotAsync(ct);
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
        await WriteAdvancedCleanupArtifactAsync(
            cleanupSucceeded, cleanupErrors, completedAtUtc, ct);
        var passed = await WriteAdvancedEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-ADV passed 22/22; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-ADV failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildAdvancedCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            AdvancedPromptId);
        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            AdvancedCatalogRawSha256,
            AdvancedCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256,
            SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            AdvancedStageLockSha256);
    }

    private object BuildAdvancedEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_ADV_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            groupId = AdvancedGroupId,
            runKey = _runKey,
            startedAtUtc,
            workspaceRoot = _paths.WorkspaceRoot,
            runRoot = _paths.RunRoot,
            databaseName = RequireMongo().DatabaseName,
            replicaSetName = RequireMongo().ReplicaSetName,
            backendProcessId = RequireBackend().ProcessId,
            backendPort = RequireBackend().Port,
            hangfireServer = true,
            candidate = new
            {
                stage = 3,
                stageLockSha256 = AdvancedStageLockSha256,
                catalogRawSha256 = AdvancedCatalogRawSha256,
                catalogSemanticSha256 = AdvancedCatalogSemanticSha256,
                promotions = new[] { "ADVANCED_SUMMARY" }
            }
        };

    private async Task PrepareAdvancedFixtureAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        var now = DateTime.UtcNow;
        var quotaCapacityUsers = Enumerable.Range(1, 12)
            .Select(index => NewActor(
                $"adv-capacity-{index:00}",
                $"p9_adv_capacity_{index:00}",
                $"P9 Advanced Capacity {index:00}",
                fixture.UnitAId,
                [],
                Actor("admin").Id,
                now).User)
            .ToArray();
        await RequireDatabase().GetCollection<AppUser>("users")
            .InsertManyAsync(quotaCapacityUsers, cancellationToken: ct);
        var sectionsJson = new JsonArray(
            new JsonObject
            {
                ["id"] = AdvancedSectionId,
                ["title"] = "P9 Advanced Main",
                ["description"] = null,
                ["tagCodes"] = new JsonArray(),
                ["order"] = 0
            }).ToJsonString();
        var fieldsJson = new JsonArray(
            new JsonObject
            {
                ["id"] = "amount",
                ["sectionId"] = AdvancedSectionId,
                ["key"] = "amount",
                ["name"] = "Amount",
                ["type"] = "number",
                ["required"] = false,
                ["order"] = 0
            }).ToJsonString();
        await RequireDatabase().GetCollection<BsonDocument>("dynamic_form_templates")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)),
                Builders<BsonDocument>.Update
                    .Set("sectionsJson", sectionsJson)
                    .Set("fieldsJson", fieldsJson)
                    .Set("blocksJson", "[]")
                    .Set("updatedAtUtc", now),
                cancellationToken: ct);

        await RequireDatabase().GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)),
                Builders<BsonDocument>.Update
                    .Set("assignmentType", "ONCE")
                    .Set("updatedAtUtc", now),
                cancellationToken: ct);

        var reports = RequireDatabase()
            .GetCollection<BsonDocument>("work_assignment_report");
        var fieldValuesJson = new JsonObject { ["amount"] = 1100 }.ToJsonString();
        var externalPayloadHash = Sha256Text(
            fieldValuesJson + "\n" + fieldValuesJson + "\n\n\n");
        await reports.UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(fixture.ReportId)),
            Builders<BsonDocument>.Update
                .Set("isCurrent", false)
                .Set("isActive", false)
                .Set("cumulativeContributionMode", "EXCLUDE")
                .Set("updatedAtUtc", now),
            cancellationToken: ct);
        await reports.UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(fixture.PairedReportId)),
            Builders<BsonDocument>.Update
                .Set("status", 2)
                .Set("completedDate", new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc))
                .Set("payloadRevision", 1)
                .Set("payloadHash", externalPayloadHash)
                .Set("payloadStatus", WorkReportPayloadStatus.Ready)
                .Set("values1DJson", fieldValuesJson)
                .Set("fieldValuesJson", fieldValuesJson)
                .Set("isCurrent", true)
                .Set("isActive", true)
                .Set("cumulativeContributionMode", "INCLUDE")
                .Set("updatedAtUtc", now),
            cancellationToken: ct);
        await RequireDatabase().GetCollection<WorkReportPayload>("work_report_payloads")
            .InsertOneAsync(
                new WorkReportPayload
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    ReportId = fixture.PairedReportId,
                    PayloadRevision = 1,
                    Values1DJson = fieldValuesJson,
                    FieldValuesJson = fieldValuesJson,
                    TableValuesRootJson = null,
                    SummarySourceJson = null,
                    PayloadHash = externalPayloadHash,
                    PayloadSizeBytes = Encoding.UTF8.GetByteCount(fieldValuesJson) * 2L,
                    Status = WorkReportPayloadStatus.Ready,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = Actor("executor").Id,
                    UpdatedByUserId = Actor("executor").Id,
                    IsDeleted = false
                },
                cancellationToken: ct);
        await RequireDatabase().GetCollection<BsonDocument>(
                "work_assignment_report_sections")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["workAssignmentReportId"] = ObjectId.Parse(fixture.PairedReportId),
                    ["workId"] = ObjectId.Parse(fixture.WorkId),
                    ["workAssignmentId"] = ObjectId.Parse(fixture.AssignmentId),
                    ["workReportPeriodId"] = ObjectId.Parse(fixture.PairedReportPeriodId),
                    ["dynamicFormTemplateId"] = ObjectId.Parse(fixture.TemplateId),
                    ["sectionId"] = AdvancedSectionId,
                    ["sectionTitle"] = "P9 Advanced Main",
                    ["sectionOrder"] = 0,
                    ["status"] = 2,
                    ["fieldValuesJson"] = fieldValuesJson,
                    ["fieldCount"] = 1,
                    ["blockCount"] = 0,
                    ["hasData"] = true,
                    ["sourcePayloadRevision"] = 1,
                    ["sourcePayloadHash"] = externalPayloadHash,
                    ["sourceLifecycleRevision"] = 3,
                    ["payloadHash"] = externalPayloadHash,
                    ["createdAtUtc"] = now,
                    ["updatedAtUtc"] = now,
                    ["isDeleted"] = false
                },
                cancellationToken: ct);

        var payload = new WorkAssignmentAdvancedSummaryConfigPayload(
            new WorkAssignmentAdvancedSummarySourceScopePayload(
                "DIRECT_CHILDREN_OR_SELF", null, null, null, null),
            [new WorkAssignmentAdvancedSummarySectionPayload(
                AdvancedSectionId,
                false,
                [new WorkAssignmentAdvancedSummaryTargetPayload(
                    "amount", "NUMBER", "SUM")])],
            ["DAY", "MONTH", "YEAR"],
            ["ASSIGNMENT", "PERIOD", "UNIT"],
            [],
            "P9-ADV isolated hierarchy fixture");
        var dependencyPins = Array.Empty<string>();
        _advConfigId = ObjectId.GenerateNewId().ToString();
        _advVersionId = ObjectId.GenerateNewId().ToString();
        _advConfigRevision = 2;
        _advConfigHash = StatConfigCanonicalJson.HashObject(
            new { payload, dependencyPins });
        await RequireDatabase().GetCollection<BsonDocument>(
                "work_assignment_advanced_summary_configs")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = ObjectId.Parse(_advVersionId),
                    ["configId"] = ObjectId.Parse(_advConfigId),
                    ["workId"] = ObjectId.Parse(fixture.WorkId),
                    ["assignmentId"] = ObjectId.Parse(fixture.AssignmentId),
                    ["dynamicFormTemplateId"] = ObjectId.Parse(fixture.TemplateId),
                    ["sectionId"] = AdvancedSectionId,
                    ["sectionTitle"] = "P9 Advanced Main",
                    ["sourceScopeMode"] = "DIRECT_CHILDREN_OR_SELF",
                    ["status"] = "LOCKED",
                    ["versionNo"] = 1,
                    ["draftRevision"] = 2,
                    ["revision"] = _advConfigRevision,
                    ["configJson"] = StatConfigCanonicalJson.Canonicalize(payload),
                    ["configHash"] = _advConfigHash,
                    ["dependencyPins"] = new BsonArray(),
                    ["lockedAtUtc"] = now,
                    ["lockedByUserId"] = ObjectId.Parse(Actor("executor").Id),
                    ["createdAtUtc"] = now,
                    ["updatedAtUtc"] = now,
                    ["createdByUserId"] = ObjectId.Parse(Actor("executor").Id),
                    ["updatedByUserId"] = ObjectId.Parse(Actor("executor").Id),
                    ["isDeleted"] = false
                },
                cancellationToken: ct);
    }

    private static string Sha256Text(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private async Task ValidateAdvancedFixtureAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var report = await database.GetCollection<WorkAssignmentReport>(
                "work_assignment_report")
            .Find(x => x.Id == Fixture().PairedReportId)
            .SingleAsync(ct);
        var section = await database.GetCollection<WorkAssignmentReportSection>(
                "work_assignment_report_sections")
            .Find(x => x.WorkAssignmentReportId == report.Id &&
                       x.SectionId == AdvancedSectionId && !x.IsDeleted)
            .SingleAsync(ct);
        var payload = await database.GetCollection<WorkReportPayload>(
                "work_report_payloads")
            .Find(x => x.ReportId == report.Id &&
                       x.PayloadRevision == report.PayloadRevision &&
                       x.Status == WorkReportPayloadStatus.Ready && !x.IsDeleted)
            .SingleAsync(ct);
        var recomputed = ComputeWorkReportPayloadHash(
            payload.Values1DJson,
            payload.FieldValuesJson,
            payload.TableValuesRootJson,
            payload.SummarySourceJson);
        var reportPinsMatch = report.PayloadRevision == payload.PayloadRevision &&
                              report.PayloadHash == payload.PayloadHash &&
                              report.PayloadStatus == WorkReportPayloadStatus.Ready;
        var sectionPinsMatch = section.SourcePayloadRevision == report.PayloadRevision &&
                               section.SourceLifecycleRevision == report.LifecycleRevision &&
                               section.Status == report.Status &&
                               section.SourcePayloadHash == report.PayloadHash;
        HarnessAssert.True(reportPinsMatch, "P9-ADV report/external payload pins drifted.");
        HarnessAssert.True(sectionPinsMatch, "P9-ADV report/section pins drifted.");
        HarnessAssert.Equal(payload.PayloadHash, recomputed,
            "P9-ADV external payload canonical hash");
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-ADV.fixture-preflight.json"),
            new
            {
                schemaVersion = "P9_ADV_FIXTURE_PREFLIGHT_V1",
                chainId = ChainId,
                promptId = AdvancedPromptId,
                reportId = report.Id,
                report.PayloadRevision,
                report.PayloadHash,
                report.PayloadStatus,
                report.LifecycleRevision,
                section.SourcePayloadRevision,
                section.SourcePayloadHash,
                section.SourceLifecycleRevision,
                externalPayloadHash = payload.PayloadHash,
                recomputed,
                reportPinsMatch,
                sectionPinsMatch,
                passed = reportPinsMatch && sectionPinsMatch &&
                         payload.PayloadHash == recomputed
            }, ct);
    }
}
