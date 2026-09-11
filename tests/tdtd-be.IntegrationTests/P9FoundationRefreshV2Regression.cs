using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Jobs;
using tdtd_be.Models.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string FoundationRefreshV2RegressionCommandLineSwitch =
        "--p9-foundation-refresh-v2-regression";

    public static async Task<int> RunFoundationRefreshV2RegressionAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9 Foundation refresh V2 regression refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p9refreshv2_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, LifecycleRuntimePromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteFoundationRefreshV2RegressionAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteFoundationRefreshV2RegressionAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;
        FoundationRefreshV2Evidence? evidence = null;

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
                    P9StatRunCandidate = BuildLifecycleCandidateOptions(),
                    HangfireServerEnabled = true,
                    HangfireRecurringRegistrationEnabled = false
                });
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await PrepareOperationsSingleReportRuntimeFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(
                ct,
                allowPreinitializedHangfireInfrastructure: true);
            await PrepareLifecycleFixtureAsync(ct);
            await PrepareFlowContributionVersionsAsync(ct);
            await SwitchToLockedIncludeVersionAsync(ct);

            var publishedA = await PublishFoundationRefreshBaselineAsync(ct);
            var aOperationsBeforeConfig =
                await AssertFoundationRefreshOperationsPublicationAsync(
                    publishedA.RunId,
                    expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                    expectedStaleReason: null,
                    expectedCurrent: true,
                    ct);
            var source = await LoadLifecycleReportAsync(ct);
            await AssertFoundationRefreshRuntimePinsAlignedAsync(source, ct);
            var configA = new FoundationRefreshConfig(
                Fixture().ConfigId,
                Fixture().ConfigVersionId,
                Fixture().ConfigVersionNo,
                Fixture().ConfigRevision,
                Fixture().ConfigHash);
            var storage = CreateFoundationRefreshHangfireStorage();
            var requestA = BuildFoundationRefreshRequest(
                "p9-foundation-refresh-v2-a-outer-001", configA, source);
            var acceptedA = await CreateFoundationRefreshOuterAsync(
                requestA, expectedReplay: false, ct);
            var hangfireA = await EnqueueAndAwaitFoundationWorkerAsync(
                storage, RequiredString(acceptedA, "jobId"), ct);
            HarnessAssert.Equal(
                publishedA.RunId,
                RequiredString(hangfireA.Outer, "projectionRunId"),
                "A outer must replay the existing inner A run");
            HarnessAssert.Equal(
                publishedA.GenerationId,
                RequiredString(hangfireA.Outer, "generationId"),
                "A outer must replay the existing inner A generation");
            HarnessAssert.Equal(
                publishedA.GenerationHash,
                RequiredString(hangfireA.Outer, "generationHash"),
                "A outer must replay the existing inner A generation hash");
            await AssertFoundationRefreshInnerCountAsync(
                publishedA.SourceLifecycleEventKey, expected: 1, ct);
            var aOuterBeforeConfig = await AssertFoundationRefreshOuterHttpAsync(
                RequiredString(acceptedA, "jobId"),
                configA,
                publishedA.RunId,
                publishedA.GenerationId,
                publishedA.GenerationHash,
                expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                expectedStaleReason: null,
                ct);

            var configB = await CreateFoundationRefreshConfigBAsync(ct);
            var aOuterAfterConfig = await AssertFoundationRefreshOuterHttpAsync(
                RequiredString(acceptedA, "jobId"),
                configA,
                publishedA.RunId,
                publishedA.GenerationId,
                publishedA.GenerationHash,
                expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Stale,
                expectedStaleReason: "CONFIG_CHANGED",
                ct);
            var requestB1 = BuildFoundationRefreshRequest(
                "p9-foundation-refresh-v2-b1-001", configB, source);
            var acceptedB1 = await CreateFoundationRefreshOuterAsync(
                requestB1, expectedReplay: false, ct);

            var hangfireB1 = await EnqueueAndAwaitFoundationWorkerAsync(
                storage, RequiredString(acceptedB1, "jobId"), ct);
            var projectedRunId = RequiredString(
                hangfireB1.Outer, "projectionRunId");
            var publishedB = await AssertFoundationRefreshPublicationAsync(
                publishedA, configB, projectedRunId, ct);
            var aOuterAfterRefresh = await AssertFoundationRefreshOuterHttpAsync(
                RequiredString(acceptedA, "jobId"),
                configA,
                projectionRunId: null,
                publishedA.GenerationId,
                publishedA.GenerationHash,
                expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Stale,
                expectedStaleReason: "CONFIG_CHANGED",
                ct);
            var aOperationsAfterRefresh =
                await AssertFoundationRefreshOperationsPublicationAsync(
                    publishedA.RunId,
                    expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Stale,
                    expectedStaleReason: "STAT_RUN_RESULT_STALE",
                    expectedCurrent: false,
                    ct);
            var bOperationsAfterRefresh =
                await AssertFoundationRefreshOperationsPublicationAsync(
                    publishedB.RunId,
                    expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                    expectedStaleReason: null,
                    expectedCurrent: true,
                    ct);
            var b1OuterAfterRefresh = await AssertFoundationRefreshOuterHttpAsync(
                RequiredString(acceptedB1, "jobId"),
                configB,
                publishedB.RunId,
                publishedB.GenerationId,
                publishedB.GenerationHash,
                expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                expectedStaleReason: null,
                ct);
            var publicResult = await AssertFoundationRefreshPublicResultAsync(
                publishedB, configB, ct);

            var bBeforeReplay = await LoadJobAsync(publishedB.RunId, ct);
            var bBeforeReplaySha256 = HashBytes(bBeforeReplay.ToBson());
            var bRowsBeforeReplay = await CaptureOperationsGenerationRowsHashAsync(
                publishedB.GenerationId, ct);

            var requestB2 = BuildFoundationRefreshRequest(
                "p9-foundation-refresh-v2-b2-001", configB, source);
            var acceptedB2 = await CreateFoundationRefreshOuterAsync(
                requestB2, expectedReplay: false, ct);
            HarnessAssert.True(
                !string.Equals(
                    RequiredString(acceptedB1, "jobId"),
                    RequiredString(acceptedB2, "jobId"),
                    StringComparison.Ordinal),
                "Distinct completed outer requests must have distinct job ids");

            var hangfireB2 = await EnqueueAndAwaitFoundationWorkerAsync(
                storage, RequiredString(acceptedB2, "jobId"), ct);
            HarnessAssert.Equal(
                publishedB.RunId,
                RequiredString(hangfireB2.Outer, "projectionRunId"),
                "B2 inner projection replay run id");
            HarnessAssert.Equal(
                publishedB.GenerationId,
                RequiredString(hangfireB2.Outer, "generationId"),
                "B2 inner projection replay generation id");
            HarnessAssert.Equal(
                publishedB.GenerationHash,
                RequiredString(hangfireB2.Outer, "generationHash"),
                "B2 inner projection replay generation hash");
            var b2OuterAfterReplay = await AssertFoundationRefreshOuterHttpAsync(
                RequiredString(acceptedB2, "jobId"),
                configB,
                publishedB.RunId,
                publishedB.GenerationId,
                publishedB.GenerationHash,
                expectedFreshness: WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                expectedStaleReason: null,
                ct);
            await AssertFoundationRefreshInnerCountAsync(
                publishedA.SourceLifecycleEventKey, expected: 2, ct);
            HarnessAssert.Equal(
                publishedA.ARowsSha256,
                await CaptureOperationsGenerationRowsHashAsync(
                    publishedA.GenerationId, ct),
                "B2 replay mutated A six-store rows");
            HarnessAssert.Equal(
                bBeforeReplaySha256,
                HashBytes((await LoadJobAsync(publishedB.RunId, ct)).ToBson()),
                "B2 replay mutated completed inner V2 job");
            HarnessAssert.Equal(
                bRowsBeforeReplay,
                await CaptureOperationsGenerationRowsHashAsync(
                    publishedB.GenerationId, ct),
                "B2 replay mutated six Direct stores");

            var exactReplay = await CreateFoundationRefreshOuterAsync(
                requestB2, expectedReplay: true, ct);
            HarnessAssert.Equal(
                RequiredString(acceptedB2, "jobId"),
                RequiredString(exactReplay, "jobId"),
                "Exact B2 receipt replay outer job id");
            await AssertFoundationRefreshInnerCountAsync(
                publishedA.SourceLifecycleEventKey, expected: 2, ct);

            evidence = new FoundationRefreshV2Evidence(
                ChainId,
                LifecycleRuntimePromptId,
                _runKey,
                publishedA.RunId,
                publishedA.GenerationId,
                publishedA.ConfigHash,
                publishedA.ARowsSha256,
                aOperationsBeforeConfig,
                RequiredString(acceptedA, "jobId"),
                hangfireA.HangfireJobId,
                hangfireA.HangfireState,
                hangfireA.HangfireResult,
                aOuterBeforeConfig,
                aOuterAfterConfig,
                configB.VersionId,
                configB.ConfigHash,
                publishedB.RunId,
                publishedB.IdentityKey,
                publishedB.GenerationId,
                aOperationsAfterRefresh,
                aOuterAfterRefresh,
                bOperationsAfterRefresh,
                RequiredString(acceptedB1, "jobId"),
                hangfireB1.HangfireJobId,
                hangfireB1.HangfireState,
                hangfireB1.HangfireResult,
                b1OuterAfterRefresh,
                RequiredString(acceptedB2, "jobId"),
                hangfireB2.HangfireJobId,
                hangfireB2.HangfireState,
                hangfireB2.HangfireResult,
                b2OuterAfterReplay,
                publicResult.PublicationCount,
                publicResult.RowCount,
                2,
                true,
                true);
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
        var passed = fatalFailure is null && evidence is not null && cleanupSucceeded;
        await WriteStrictJsonAsync(
            Path.Combine(
                _paths.RunRoot,
                "P9-FOUNDATION-REFRESH-V2.evidence.json"),
            new
            {
                schemaVersion = "P9_FOUNDATION_REFRESH_V2_REGRESSION_V1",
                passed,
                startedAtUtc,
                completedAtUtc = DateTime.UtcNow,
                fatalFailure,
                cleanupSucceeded,
                cleanupErrors,
                evidence
            },
            CancellationToken.None);

        Console.WriteLine(
            passed
                ? $"[DAT] P9 Foundation refresh V2 A->B regression passed; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9 Foundation refresh V2 A->B regression failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }
    private async Task<FoundationRefreshBaseline>
        PublishFoundationRefreshBaselineAsync(CancellationToken ct)
    {
        var save = await RequireApi().PutAsync(
            $"api/work-assignment-reports/{Fixture().ReportId}/draft",
            _lfcSaveRequest!.DeepClone(),
            Actor("executor").Token,
            ct: ct);
        ExpectSuccess(save, "P9 refresh V2 baseline draft save");
        await SeedCanonicalP7MappingLineageAsync(
            ct,
            "p9-foundation-refresh-v2-mapping-001",
            _flwIncludeVersionId);

        var report = await LoadLifecycleReportAsync(ct);
        var submit = await RequireApi().PostAsync(
            $"api/work-assignment-reports/{Fixture().ReportId}/submit",
            new JsonObject
            {
                ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                ["commandId"] = "p9-foundation-refresh-v2-submit-001"
            },
            Actor("executor").Token,
            ct: ct);
        ExpectSuccess(submit, "P9 refresh V2 baseline submit");

        var ownership = await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id", ObjectId.Parse(Fixture().AssignmentId)),
                Builders<BsonDocument>.Update.Set(
                    "createdByUserId", ObjectId.Parse(Actor("admin").Id)),
                cancellationToken: ct);
        HarnessAssert.Equal(1L, ownership.MatchedCount, "Baseline assignment owner update");

        report = await LoadLifecycleReportAsync(ct);
        var approve = await RequireApi().PostAsync(
            $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
            new JsonObject
            {
                ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                ["commandId"] = "p9-foundation-refresh-v2-approve-001",
                ["comment"] = "P9 Foundation refresh V2 baseline A"
            },
            Actor("admin").Token,
            ct: ct);
        ExpectSuccess(approve, "P9 refresh V2 baseline approve");

        var included = await DrainFlowApprovalStateAsync("PUBLISHED", ct);
        var entry = included.Entry;
        var runId = BsonString(entry, "directProjectionRunId");
        var generationId = BsonString(entry, "directProjectionGenerationId");
        var generationHash = BsonString(entry, "directProjectionGenerationHash");
        var eventKey = BsonString(entry, "entryKey");
        var job = await LoadJobAsync(runId, ct);
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
            BsonString(job, "runKind"),
            "Baseline A inner run kind");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobStatuses.Completed,
            BsonString(job, "status"),
            "Baseline A status");
        HarnessAssert.Equal(0, BsonInt(job, "retryCount"), "Baseline A retry count");
        HarnessAssert.True(BsonBool(job, "isCurrentPublication"), "Baseline A current publication");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            BsonString(job, "freshnessState"),
            "Baseline A freshness");
        HarnessAssert.True(
            BsonNullableString(job, "directProjectionIdentityVersion") is null &&
            BsonNullableString(job, "directProjectionIdentityKey") is null,
            "Baseline A must preserve legacy V1 identity shape");
        HarnessAssert.Equal(
            eventKey,
            BsonString(job, "sourceLifecycleEventKey"),
            "Baseline A event key");
        HarnessAssert.Equal(
            Fixture().ConfigHash,
            BsonString(job, "configHash"),
            "Baseline A config hash");
        HarnessAssert.Equal(
            generationId,
            BsonString(job, "generationId"),
            "Baseline A generation id");
        HarnessAssert.Equal(
            generationHash,
            BsonString(job, "generationHash"),
            "Baseline A generation hash");
        await AssertFoundationRefreshGenerationRowsAsync(
            runId,
            generationId,
            Fixture().ConfigHash,
            eventKey,
            ct);

        var aRowsSha256 = await CaptureOperationsGenerationRowsHashAsync(
            generationId,
            ct);
        return new FoundationRefreshBaseline(
            runId,
            generationId,
            generationHash,
            eventKey,
            Fixture().ConfigHash,
            NormalizeSupersessionMutableFieldsSha256(job),
            aRowsSha256);
    }
    private async Task<FoundationRefreshConfig>
        CreateFoundationRefreshConfigBAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        var initial = await RequireApi().GetAsync(
            $"api/dynamic-forms/{fixture.TemplateId}/statistics",
            Actor("admin").Token,
            ct: ct);
        ExpectSuccess(initial, "P9 refresh V2 config A read");
        var configA = ApiHarnessClient.RequiredObject(
            initial.Json, "P9 refresh V2 config A");
        HarnessAssert.Equal(
            fixture.ConfigHash,
            RequiredString(configA, "configHash"),
            "Config A hash");

        var persisted = await RequireApi().PatchAsync(
            $"api/dynamic-forms/{fixture.TemplateId}/statistics",
            new JsonObject
            {
                ["commandId"] = "p9-foundation-refresh-v2-config-b-001",
                ["expectedRevision"] = RequiredLong(configA, "revision"),
                ["expectedConfigHash"] = RequiredString(configA, "configHash"),
                ["payload"] = new JsonObject
                {
                    ["fields"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["fieldId"] = "field_amount",
                            ["isStatistic"] = true,
                            ["statisticLabelCodes"] = new JsonArray(),
                            ["statistic"] = new JsonObject
                            {
                                ["aggregateOps"] = new JsonArray { "SUM" },
                                ["bucketMode"] = "NONE",
                                ["showInDetail"] = true,
                                ["showInTree"] = false
                            }
                        },
                        new JsonObject
                        {
                            ["fieldId"] = "field_approved",
                            ["isStatistic"] = true,
                            ["statisticLabelCodes"] = new JsonArray(),
                            ["statistic"] = new JsonObject
                            {
                                ["aggregateOps"] = new JsonArray { "COUNT" },
                                ["bucketMode"] = "NONE",
                                ["showInDetail"] = true,
                                ["showInTree"] = true
                            }
                        }
                    }
                }
            },
            Actor("admin").Token,
            ct: ct);
        ExpectSuccess(persisted, "P9 refresh V2 config B mutation");
        var configB = ApiHarnessClient.RequiredObject(
            persisted.Json, "P9 refresh V2 config B");
        var identity = new FoundationRefreshConfig(
            RequiredString(configB, "configId"),
            RequiredString(configB, "versionId"),
            ApiHarnessClient.RequiredInt(configB, "versionNo"),
            RequiredLong(configB, "revision"),
            RequiredString(configB, "configHash"));

        HarnessAssert.Equal("LOCKED", RequiredString(configB, "status"), "Config B status");
        HarnessAssert.Equal(fixture.ConfigId, identity.ConfigId, "Config B owner identity");
        HarnessAssert.True(
            !string.Equals(
                fixture.ConfigVersionId,
                identity.VersionId,
                StringComparison.Ordinal),
            "Config B version id did not advance");
        HarnessAssert.Equal(
            fixture.ConfigVersionNo + 1,
            identity.VersionNo,
            "Config B version number");
        HarnessAssert.Equal(
            fixture.ConfigRevision + 1,
            identity.Revision,
            "Config B revision");
        HarnessAssert.True(
            !string.Equals(fixture.ConfigHash, identity.ConfigHash, StringComparison.Ordinal),
            "Config B hash did not change");

        var versions = configB["versions"] as JsonArray
            ?? throw new InvalidOperationException("Config B versions are missing.");
        var current = versions
            .OfType<JsonObject>()
            .Single(version => string.Equals(
                RequiredString(version, "versionId"),
                identity.VersionId,
                StringComparison.Ordinal));
        HarnessAssert.Equal(
            fixture.ConfigVersionId,
            current["previousVersionId"]?.GetValue<string>(),
            "Config B previous version identity");
        return identity;
    }

    private async Task AssertFoundationRefreshRuntimePinsAlignedAsync(
        BsonDocument report,
        CancellationToken ct)
    {
        await AssertOperationsFoundationExactReportPinAsync(ct);
        var include = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_flow_template_versions")
            .Find(new BsonDocument("_id", ObjectId.Parse(_flwIncludeVersionId!)))
            .SingleAsync(ct);
        var family = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_flow_templates")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().FlowFamilyId)))
            .SingleAsync(ct);
        var assignment = await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignments")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)))
            .SingleAsync(ct);
        var instance = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_flow_instances")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().FlowInstanceId)))
            .SingleAsync(ct);
        var provenanceId = BsonString(report, "dynamicFlowMappingProvenanceId");
        var provenance = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_flow_mapping_provenance")
            .Find(new BsonDocument("_id", ObjectId.Parse(provenanceId)))
            .SingleAsync(ct);
        var runtimePin = provenance.GetValue("runtimePin").AsBsonDocument;
        var includeVersionNo = BsonInt(include, "versionNo");
        var includePayloadHash = BsonString(include, "payloadHash");

        HarnessAssert.Equal(
            _flwIncludeVersionId,
            BsonString(family, "currentVersionId"),
            "Pre-outer flow family current version");
        HarnessAssert.Equal(
            includeVersionNo,
            BsonInt(family, "currentVersionNo"),
            "Pre-outer flow family version number");
        HarnessAssert.Equal(
            includePayloadHash,
            BsonString(family, "currentVersionHash"),
            "Pre-outer flow family payload hash");
        HarnessAssert.Equal(
            includeVersionNo,
            BsonInt(assignment, "flowTemplateVersionNo"),
            "Pre-outer assignment flow version number");
        HarnessAssert.Equal(
            _flwIncludeVersionId,
            BsonString(instance, "flowTemplateVersionId"),
            "Pre-outer flow instance version");
        HarnessAssert.Equal(
            includeVersionNo,
            BsonInt(instance, "flowTemplateVersionNo"),
            "Pre-outer flow instance version number");
        HarnessAssert.Equal(
            includePayloadHash,
            BsonString(instance, "flowPayloadHash"),
            "Pre-outer flow instance payload hash");
        HarnessAssert.Equal(
            _flwIncludeVersionId,
            BsonString(runtimePin, "flowVersionId"),
            "Pre-outer mapping runtime version");
        HarnessAssert.Equal(
            includeVersionNo,
            BsonInt(runtimePin, "flowVersionNo"),
            "Pre-outer mapping runtime version number");
        HarnessAssert.Equal(
            includePayloadHash,
            BsonString(runtimePin, "flowPayloadHash"),
            "Pre-outer mapping runtime payload hash");
    }
    private JsonObject BuildFoundationRefreshRequest(
        string commandId,
        FoundationRefreshConfig config,
        BsonDocument report)
        => new()
        {
            ["commandId"] = commandId,
            ["workId"] = Fixture().WorkId,
            ["scopeType"] = "ASSIGNMENT",
            ["scopeId"] = Fixture().AssignmentId,
            ["sourceReportId"] = Fixture().ReportId,
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["expectedConfigRevision"] = config.Revision,
            ["expectedConfigHash"] = config.ConfigHash,
            ["expectedSourceRevision"] = BsonInt(report, "payloadRevision"),
            ["expectedSourceHash"] = BsonString(report, "payloadHash"),
            ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
            ["period"] = new JsonObject
            {
                ["periodKey"] = BsonString(report, "periodKey"),
                ["periodInstanceKey"] = BsonString(report, "periodInstanceKey"),
                ["periodKind"] = BsonString(report, "periodKind"),
                ["periodStart"] = report.GetValue("periodStart", BsonNull.Value).IsBsonNull
                    ? null
                    : report["periodStart"].ToUniversalTime(),
                ["periodEnd"] = report.GetValue("periodEnd", BsonNull.Value).IsBsonNull
                    ? null
                    : report["periodEnd"].ToUniversalTime()
            }
        };

    private async Task<JsonObject> CreateFoundationRefreshOuterAsync(
        JsonObject request,
        bool expectedReplay,
        CancellationToken ct)
    {
        var response = await CreateJobAsync(
            "DIRECT_FIELD_TABLE_LABEL",
            request,
            Actor("admin").Token,
            ct);
        ApiHarnessClient.ExpectStatus(
            response,
            expectedReplay ? HttpStatusCode.OK : HttpStatusCode.Accepted,
            expectedReplay
                ? "P9 refresh V2 exact outer replay"
                : "P9 refresh V2 outer create");
        var root = ApiHarnessClient.RequiredObject(
            response.Json, "P9 refresh V2 outer response");
        HarnessAssert.Equal(
            expectedReplay,
            RequiredBool(root, "isReplay"),
            "Outer replay flag");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobRunKinds.Foundation,
            RequiredString(root, "runKind"),
            "Outer run kind");
        HarnessAssert.Equal(
            0,
            ApiHarnessClient.RequiredInt(root, "retryCount"),
            "Outer accepted retry count");
        return root;
    }
    private MongoStorage CreateFoundationRefreshHangfireStorage()
    {
        var prefix =
            $"p1hf_{new string(_runKey.Where(char.IsLetterOrDigit).Take(24).ToArray()).ToLowerInvariant()}";
        return new MongoStorage(
            RequireMongo().Client,
            RequireMongo().DatabaseName,
            new MongoStorageOptions
            {
                Prefix = prefix,
                CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.Poll,
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                MigrationOptions = new MongoMigrationOptions
                {
                    MigrationStrategy = new MigrateMongoMigrationStrategy(),
                    BackupStrategy = new CollectionMongoBackupStrategy()
                }
            });
    }

    private async Task<FoundationWorkerObservation>
        EnqueueAndAwaitFoundationWorkerAsync(
            MongoStorage storage,
            string outerJobId,
            CancellationToken ct)
    {
        GlobalConfiguration.Configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings();
        var client = new BackgroundJobClient(storage);
        var hangfireJobId = client.Enqueue<NonOverlappingRecurringJobRunner>(
            runner => runner.ProcessStatRunFoundationDirectJobsAsync(
                3,
                CancellationToken.None));
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(hangfireJobId),
            "Foundation Hangfire enqueue returned no id");

        var deadline = DateTime.UtcNow.AddSeconds(75);
        JsonObject? outer = null;
        while (DateTime.UtcNow < deadline)
        {
            var response = await RequireApi().GetAsync(
                $"api/stat-runs/jobs/{outerJobId}",
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                "P9 refresh V2 outer poll");
            outer = ApiHarnessClient.RequiredObject(
                response.Json, "P9 refresh V2 outer poll");
            var status = RequiredString(outer, "status");
            if (string.Equals(status, "DONE", StringComparison.Ordinal))
                break;
            if (status is "FAILED" or "RETRYING")
            {
                throw new InvalidOperationException(
                    $"Foundation outer reached {status}. response={outer.ToJsonString()}");
            }
            await Task.Delay(200, ct);
        }
        if (outer is null ||
            !string.Equals(
                RequiredString(outer, "status"),
                "DONE",
                StringComparison.Ordinal))
        {
            throw new TimeoutException(
                $"Foundation outer did not complete. outer={outer?.ToJsonString() ?? "<null>"}");
        }

        HarnessAssert.Equal(
            0,
            ApiHarnessClient.RequiredInt(outer, "retryCount"),
            "Terminal outer retry count");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            RequiredString(outer, "freshnessState"),
            "Terminal outer freshness");
        HarnessAssert.True(
            IsCanonicalSha(RequiredString(outer, "generationId")),
            "Terminal outer generation id");
        HarnessAssert.True(
            IsCanonicalSha(RequiredString(outer, "generationHash")),
            "Terminal outer generation hash");
        HarnessAssert.True(
            ObjectId.TryParse(RequiredString(outer, "projectionRunId"), out _),
            "Terminal outer projection run id");

        string? state = null;
        string? result = null;
        while (DateTime.UtcNow < deadline)
        {
            using var connection = storage.GetConnection();
            var data = connection.GetJobData(hangfireJobId);
            state = data?.State;
            var stateData = connection.GetStateData(hangfireJobId);
            if (stateData is not null)
                stateData.Data.TryGetValue("Result", out result);
            if (string.Equals(state, "Succeeded", StringComparison.Ordinal))
                break;
            if (state is "Failed" or "Deleted")
            {
                throw new InvalidOperationException(
                    $"Foundation Hangfire job reached {state}.");
            }
            await Task.Delay(100, ct);
        }

        HarnessAssert.Equal(
            "Succeeded",
            state,
            "Foundation Hangfire terminal state");
        HarnessAssert.Equal(
            "1",
            result,
            "Foundation worker claimed-job result");
        return new FoundationWorkerObservation(
            hangfireJobId,
            state!,
            result!,
            outer);
    }
    private async Task<FoundationRefreshPublication>
        AssertFoundationRefreshPublicationAsync(
            FoundationRefreshBaseline baseline,
            FoundationRefreshConfig configB,
            string runId,
            CancellationToken ct)
    {
        HarnessAssert.True(
            !string.Equals(baseline.RunId, runId, StringComparison.Ordinal),
            "Refresh B reused legacy A run id");
        var jobB = await LoadJobAsync(runId, ct);
        var generationId = BsonString(jobB, "generationId");
        var generationHash = BsonString(jobB, "generationHash");
        var identityKey = BsonString(jobB, "directProjectionIdentityKey");
        HarnessAssert.Equal(
            WorkReportDirectProjectionIdentityVersions.FoundationRefreshV2,
            BsonString(jobB, "directProjectionIdentityVersion"),
            "Refresh B identity version");
        HarnessAssert.True(IsCanonicalSha(identityKey), "Refresh B identity key");
        HarnessAssert.True(IsCanonicalSha(generationId), "Refresh B generation id");
        HarnessAssert.True(IsCanonicalSha(generationHash), "Refresh B generation hash");
        HarnessAssert.True(
            !string.Equals(
                baseline.GenerationId,
                generationId,
                StringComparison.Ordinal),
            "Refresh B reused A generation id");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobStatuses.Completed,
            BsonString(jobB, "status"),
            "Refresh B status");
        HarnessAssert.Equal(0, BsonInt(jobB, "retryCount"), "Refresh B retry count");
        HarnessAssert.True(BsonBool(jobB, "isCurrentPublication"), "Refresh B current flag");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            BsonString(jobB, "freshnessState"),
            "Refresh B freshness");
        HarnessAssert.True(
            BsonNullableString(jobB, "staleReason") is null,
            "Refresh B stale reason");
        HarnessAssert.True(
            BsonNullableString(jobB, "commandId") is null,
            "Refresh B inner commandId must be absent");
        HarnessAssert.Equal(configB.ConfigId, BsonString(jobB, "configId"), "Refresh B config id");
        HarnessAssert.Equal(configB.VersionId, BsonString(jobB, "configVersionId"), "Refresh B config version id");
        HarnessAssert.Equal(configB.VersionNo, BsonInt(jobB, "configVersionNo"), "Refresh B config version no");
        HarnessAssert.Equal(configB.Revision, BsonLong(jobB, "configRevision"), "Refresh B config revision");
        HarnessAssert.Equal(configB.ConfigHash, BsonString(jobB, "configHash"), "Refresh B config hash");
        HarnessAssert.Equal(
            baseline.SourceLifecycleEventKey,
            BsonString(jobB, "sourceLifecycleEventKey"),
            "Refresh B lifecycle event identity");

        var jobA = await LoadJobAsync(baseline.RunId, ct);
        HarnessAssert.True(
            !BsonBool(jobA, "isCurrentPublication"),
            "Refresh A remained current");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobFreshnessStates.Stale,
            BsonString(jobA, "freshnessState"),
            "Refresh A freshness");
        HarnessAssert.Equal(
            "LIFECYCLE_SUPERSEDED",
            BsonString(jobA, "staleReason"),
            "Refresh A supersession reason");
        HarnessAssert.Equal(
            baseline.ImmutableSha256,
            NormalizeSupersessionMutableFieldsSha256(jobA),
            "Refresh A immutable body changed during supersession");
        HarnessAssert.Equal(
            baseline.ARowsSha256,
            await CaptureOperationsGenerationRowsHashAsync(
                baseline.GenerationId, ct),
            "Refresh A six-store rows changed during B publication");

        await AssertFoundationRefreshInnerCountAsync(
            baseline.SourceLifecycleEventKey,
            expected: 2,
            ct);
        var family = await RequireDatabase()
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .Find(new BsonDocument
            {
                ["runKind"] = WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
                ["workId"] = jobB["workId"].DeepClone(),
                ["periodInstanceKey"] = jobB["periodInstanceKey"].DeepClone(),
                ["periodKind"] = jobB["periodKind"].DeepClone(),
                ["dynamicFormFamilyId"] = jobB["dynamicFormFamilyId"].DeepClone(),
                ["dynamicFormTemplateId"] = jobB["dynamicFormTemplateId"].DeepClone(),
                ["dynamicFormVersionNo"] = jobB["dynamicFormVersionNo"].DeepClone(),
                ["dynamicFormSchemaHash"] = jobB["dynamicFormSchemaHash"].DeepClone(),
                ["candidateChainId"] = jobB["candidateChainId"].DeepClone(),
                ["candidatePromptId"] = jobB["candidatePromptId"].DeepClone(),
                ["isCurrentPublication"] = true,
                ["isDeleted"] = false
            })
            .ToListAsync(ct);
        HarnessAssert.Equal(1, family.Count, "Current publication family count");
        HarnessAssert.Equal(
            runId,
            BsonString(family[0], "_id"),
            "Current publication family winner");

        await AssertFoundationRefreshGenerationRowsAsync(
            baseline.RunId,
            baseline.GenerationId,
            baseline.ConfigHash,
            baseline.SourceLifecycleEventKey,
            ct);
        await AssertFoundationRefreshGenerationRowsAsync(
            runId,
            generationId,
            configB.ConfigHash,
            baseline.SourceLifecycleEventKey,
            ct);

        return new FoundationRefreshPublication(
            runId,
            identityKey,
            generationId,
            generationHash);
    }

    private async Task<FoundationRefreshOperationsObservation>
        AssertFoundationRefreshOperationsPublicationAsync(
            string runId,
            string expectedFreshness,
            string? expectedStaleReason,
            bool expectedCurrent,
            CancellationToken ct)
    {
        var response = await RequireApi().GetAsync(
            $"api/operations/jobs/{runId}",
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P9 refresh V2 Lifecycle Direct operations detail");
        var root = ApiHarnessClient.RequiredObject(
            response.Json,
            "P9 refresh V2 Lifecycle Direct operations detail");
        HarnessAssert.Equal(runId, RequiredString(root, "jobId"), "Operations job id");
        HarnessAssert.Equal(runId, RequiredString(root, "runId"), "Operations run id");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
            RequiredString(root, "runKind"),
            "Operations inner run kind");
        HarnessAssert.Equal("DONE", RequiredString(root, "status"), "Operations inner status");
        HarnessAssert.True(
            RequiredLong(root, "stateRevision") > 0,
            "Operations inner state revision");
        HarnessAssert.True(
            IsCanonicalSha(RequiredString(root, "stateHash")),
            "Operations inner state hash");
        HarnessAssert.Equal(
            Fixture().WorkId,
            RequiredString(root, "workId"),
            "Operations inner work id");
        HarnessAssert.Equal(
            Fixture().ReportId,
            RequiredString(root, "sourceReportId"),
            "Operations inner source report");
        HarnessAssert.Equal(
            expectedFreshness,
            RequiredString(root, "freshnessState"),
            "Operations inner freshness");
        HarnessAssert.Equal(
            expectedStaleReason,
            root["staleReason"]?.GetValue<string>(),
            "Operations inner stale reason");
        HarnessAssert.Equal(
            expectedCurrent,
            RequiredBool(root, "isCurrentPublication"),
            "Operations inner current-publication flag");
        return new FoundationRefreshOperationsObservation(
            runId,
            RequiredString(root, "status"),
            RequiredString(root, "freshnessState"),
            root["staleReason"]?.GetValue<string>(),
            RequiredBool(root, "isCurrentPublication"));
    }

    private async Task<FoundationRefreshOuterObservation>
        AssertFoundationRefreshOuterHttpAsync(
            string outerJobId,
            FoundationRefreshConfig config,
            string? projectionRunId,
            string generationId,
            string generationHash,
            string expectedFreshness,
            string? expectedStaleReason,
            CancellationToken ct)
    {
        var response = await RequireApi().GetAsync(
            $"api/stat-runs/jobs/{outerJobId}",
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P9 refresh V2 Foundation outer detail");
        var root = ApiHarnessClient.RequiredObject(
            response.Json,
            "P9 refresh V2 Foundation outer detail");
        HarnessAssert.Equal(outerJobId, RequiredString(root, "jobId"), "Outer job id");
        HarnessAssert.Equal(outerJobId, RequiredString(root, "runId"), "Outer run id");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobRunKinds.Foundation,
            RequiredString(root, "runKind"),
            "Outer run kind");
        HarnessAssert.Equal("DONE", RequiredString(root, "status"), "Outer status");
        HarnessAssert.True(
            RequiredLong(root, "stateRevision") > 0,
            "Outer state revision");
        HarnessAssert.True(
            IsCanonicalSha(RequiredString(root, "stateHash")),
            "Outer state hash");
        HarnessAssert.Equal(config.ConfigId, RequiredString(root, "configId"), "Outer config id");
        HarnessAssert.Equal(
            config.VersionId,
            RequiredString(root, "configVersionId"),
            "Outer config version id");
        HarnessAssert.Equal(
            config.VersionNo,
            ApiHarnessClient.RequiredInt(root, "configVersionNo"),
            "Outer config version number");
        HarnessAssert.Equal(
            config.Revision,
            RequiredLong(root, "configRevision"),
            "Outer config revision");
        HarnessAssert.Equal(
            config.ConfigHash,
            RequiredString(root, "configHash"),
            "Outer config hash");
        var actualProjectionRunId =
            root["projectionRunId"]?.GetValue<string>();
        HarnessAssert.Equal(
            projectionRunId,
            actualProjectionRunId,
            "Outer projection run id");
        HarnessAssert.Equal(
            generationId,
            RequiredString(root, "generationId"),
            "Outer generation id");
        HarnessAssert.Equal(
            generationHash,
            RequiredString(root, "generationHash"),
            "Outer generation hash");
        HarnessAssert.Equal(
            expectedFreshness,
            RequiredString(root, "freshnessState"),
            "Outer freshness");
        HarnessAssert.Equal(
            expectedStaleReason,
            root["staleReason"]?.GetValue<string>(),
            "Outer stale reason");
        return new FoundationRefreshOuterObservation(
            outerJobId,
            RequiredString(root, "status"),
            RequiredString(root, "freshnessState"),
            root["staleReason"]?.GetValue<string>(),
            actualProjectionRunId,
            actualProjectionRunId is null,
            RequiredString(root, "generationId"),
            RequiredString(root, "generationHash"),
            RequiredString(root, "configVersionId"),
            RequiredLong(root, "configRevision"),
            RequiredString(root, "configHash"));
    }
    private async Task<FoundationRefreshPublicResult>
        AssertFoundationRefreshPublicResultAsync(
            FoundationRefreshPublication publishedB,
            FoundationRefreshConfig configB,
            CancellationToken ct)
    {
        var root = await ReadReadyAsync(
            DirectFieldRoute,
            DirectBody("FIELD"),
            Actor("executor").Token,
            ct);
        var metadata = DirectObject(root, "metadata");
        HarnessAssert.Equal(
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            DirectString(metadata, "freshness"),
            "Public Direct freshness");
        var publications = DirectArray(metadata, "publications");
        HarnessAssert.Equal(1, publications.Count, "Public Direct publication count");
        var publication = publications.Single()!.AsObject();
        HarnessAssert.Equal(
            publishedB.RunId,
            DirectString(publication, "runId"),
            "Public Direct B run id");
        HarnessAssert.Equal(
            publishedB.GenerationId,
            DirectString(publication, "generationId"),
            "Public Direct B generation id");
        HarnessAssert.Equal(
            publishedB.GenerationHash,
            DirectString(publication, "generationHash"),
            "Public Direct B generation hash");
        HarnessAssert.Equal(
            configB.ConfigHash,
            DirectString(publication, "configHash"),
            "Public Direct B config hash");
        var rowCount = DirectArray(root, "rows").Count;
        HarnessAssert.True(rowCount > 0, "Public Direct B returned no rows");
        return new FoundationRefreshPublicResult(publications.Count, rowCount);
    }
    private async Task AssertFoundationRefreshInnerCountAsync(
        string lifecycleEventKey,
        int expected,
        CancellationToken ct)
    {
        var jobs = await RequireDatabase()
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .Find(new BsonDocument
            {
                ["runKind"] = WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
                ["sourceReportId"] = ObjectId.Parse(Fixture().ReportId),
                ["sourceLifecycleEventKey"] = lifecycleEventKey,
                ["isDeleted"] = false
            })
            .ToListAsync(ct);
        HarnessAssert.Equal(
            expected,
            jobs.Count,
            "Lifecycle Direct A/B inner document count");
    }

    private async Task AssertFoundationRefreshGenerationRowsAsync(
        string runId,
        string generationId,
        string configHash,
        string lifecycleEventKey,
        CancellationToken ct)
    {
        var job = await LoadJobAsync(runId, ct);
        var digests = job.GetValue("directStoreDigests", new BsonArray())
            .AsBsonArray
            .Select(value => value.AsBsonDocument)
            .ToDictionary(
                value => BsonString(value, "store"),
                value => value,
                StringComparer.Ordinal);
        HarnessAssert.Equal(
            LifecycleDirectCollections.Length,
            digests.Count,
            "Generation six-store digest count");

        long totalRows = 0;
        foreach (var collectionName in LifecycleDirectCollections)
        {
            HarnessAssert.True(
                digests.TryGetValue(collectionName, out var digest),
                $"Generation digest lacks {collectionName}");
            HarnessAssert.True(
                IsCanonicalSha(BsonString(digest!, "sha256")),
                $"Generation digest hash {collectionName}");
            var rows = await RequireDatabase()
                .GetCollection<BsonDocument>(collectionName)
                .Find(new BsonDocument(
                    "directProjection.generationId",
                    generationId))
                .ToListAsync(ct);
            HarnessAssert.Equal(
                BsonLong(digest!, "rowCount"),
                (long)rows.Count,
                $"Generation digest row count {collectionName}");
            totalRows += rows.Count;
            foreach (var row in rows)
            {
                var pin = row.GetValue("directProjection", BsonNull.Value);
                HarnessAssert.True(
                    pin.IsBsonDocument,
                    $"{collectionName} row lacks Direct pin");
                var document = pin.AsBsonDocument;
                HarnessAssert.Equal(
                    runId,
                    BsonString(document, "runId"),
                    $"{collectionName} run pin");
                HarnessAssert.Equal(
                    generationId,
                    BsonString(document, "generationId"),
                    $"{collectionName} generation pin");
                HarnessAssert.Equal(
                    configHash,
                    BsonString(document, "configHash"),
                    $"{collectionName} config pin");
                HarnessAssert.Equal(
                    lifecycleEventKey,
                    BsonString(document, "lifecycleEventKey"),
                    $"{collectionName} event pin");
            }
        }
        HarnessAssert.True(totalRows > 0, "Generation six-store total row count");
    }
    private static string NormalizeSupersessionMutableFieldsSha256(
        BsonDocument source)
    {
        var normalized = (BsonDocument)source.DeepClone();
        foreach (var field in new[]
                 {
                     "isCurrentPublication",
                     "freshnessState",
                     "staleReason",
                     "updatedAtUtc",
                     "updatedByUserId"
                 })
        {
            normalized.Remove(field);
        }
        return HashBytes(normalized.ToBson());
    }

    private sealed record FoundationRefreshConfig(
        string ConfigId,
        string VersionId,
        int VersionNo,
        long Revision,
        string ConfigHash);

    private sealed record FoundationRefreshBaseline(
        string RunId,
        string GenerationId,
        string GenerationHash,
        string SourceLifecycleEventKey,
        string ConfigHash,
        string ImmutableSha256,
        string ARowsSha256);

    private sealed record FoundationRefreshPublication(
        string RunId,
        string IdentityKey,
        string GenerationId,
        string GenerationHash);

    private sealed record FoundationWorkerObservation(
        string HangfireJobId,
        string HangfireState,
        string HangfireResult,
        JsonObject Outer);

    private sealed record FoundationRefreshPublicResult(
        int PublicationCount,
        int RowCount);

    private sealed record FoundationRefreshOperationsObservation(
        string JobId,
        string Status,
        string FreshnessState,
        string? StaleReason,
        bool IsCurrentPublication);

    private sealed record FoundationRefreshOuterObservation(
        string JobId,
        string Status,
        string FreshnessState,
        string? StaleReason,
        string? ProjectionRunId,
        bool ProjectionRunIdIsNull,
        string GenerationId,
        string GenerationHash,
        string ConfigVersionId,
        long ConfigRevision,
        string ConfigHash);

    private sealed record FoundationRefreshV2Evidence(
        string ChainId,
        string PromptId,
        string RunKey,
        string ARunId,
        string AGenerationId,
        string AConfigHash,
        string ARowsSha256,
        FoundationRefreshOperationsObservation AOperationsBeforeConfig,
        string AOuterJobId,
        string AOuterHangfireJobId,
        string AOuterHangfireState,
        string AOuterHangfireResult,
        FoundationRefreshOuterObservation AOuterBeforeConfig,
        FoundationRefreshOuterObservation AOuterAfterConfig,
        string BConfigVersionId,
        string BConfigHash,
        string BRunId,
        string BIdentityKey,
        string BGenerationId,
        FoundationRefreshOperationsObservation AOperationsAfterRefresh,
        FoundationRefreshOuterObservation AOuterAfterRefresh,
        FoundationRefreshOperationsObservation BOperationsAfterRefresh,
        string B1OuterJobId,
        string B1HangfireJobId,
        string B1HangfireState,
        string B1HangfireResult,
        FoundationRefreshOuterObservation B1OuterAfterRefresh,
        string B2OuterJobId,
        string B2HangfireJobId,
        string B2HangfireState,
        string B2HangfireResult,
        FoundationRefreshOuterObservation B2OuterAfterReplay,
        int PublicPublicationCount,
        int PublicRowCount,
        int InnerDocumentCount,
        bool AImmutableExceptSupersession,
        bool BReplayByteStable);
}