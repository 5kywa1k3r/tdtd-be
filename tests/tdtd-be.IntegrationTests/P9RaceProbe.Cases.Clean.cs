using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunRaceCleanCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-RACE-CLEAN-01", async () =>
        {
            var created = await CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL",
                await BuildCurrentRaceCreateRequestAsync(
                    "p9-race-concurrent-build-001", ct),
                Actor("executor").Token,
                ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted,
                "P9-RACE concurrent build fixture");
            var jobId = RequiredString(created.Json, "jobId");
            var claim = await ClaimAsync("p9-race-concurrent-builder", ct);
            var claimToken = RequiredString(claim, "claimToken");
            var claimedJob = ApiHarnessClient.RequiredObject(
                claim["job"], "P9-RACE concurrent build claim");
            HarnessAssert.Equal(jobId, RequiredString(claimedJob, "jobId"),
                "P9-RACE concurrent build job identity");

            var csv = _raceCsvExport
                      ?? throw new HarnessCaseNotRunnableException(
                          "P9-RACE CSV export is unavailable.");
            var exportFixture = _exportFixtures[StatRunExportResultKinds.Basic];
            var exportQuery = ExportQuery(csv, exportFixture);
            var completeTask = RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                new
                {
                    workerId = "p9-race-concurrent-builder",
                    claimToken
                },
                Actor("admin").Token,
                ct: ct);
            var metadataTask = RequireApi().GetAsync(
                $"api/stat-runs/exports/{csv.ExportId}?{exportQuery}",
                Actor("executor").Token,
                ct: ct);
            var downloadTask = RequireApi().GetBytesAsync(
                $"api/stat-runs/exports/{csv.ExportId}/download?{exportQuery}",
                Actor("executor").Token,
                ct: ct);
            await Task.WhenAll(completeTask, metadataTask, downloadTask);
            ApiHarnessClient.ExpectStatus(completeTask.Result, HttpStatusCode.OK,
                "P9-RACE concurrent completion");
            ApiHarnessClient.ExpectStatus(metadataTask.Result, HttpStatusCode.OK,
                "P9-RACE concurrent export metadata");
            HarnessAssert.Equal(HttpStatusCode.OK, downloadTask.Result.StatusCode,
                "P9-RACE concurrent export download");
            HarnessAssert.Equal(csv.ContentHash,
                HashBytes(downloadTask.Result.Content),
                "P9-RACE concurrent export bytes hash");
            var durable = await LoadJobAsync(jobId, ct);
            HarnessAssert.Equal("COMPLETED", BsonString(durable, "status"),
                "P9-RACE concurrent build durable state");
            HarnessAssert.True(IsCanonicalSha(BsonString(durable, "generationHash")),
                "P9-RACE concurrent build generation hash");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CLEAN-01", "BUILD_READ_EXPORT",
                [(int)completeTask.Result.StatusCode,
                    (int)metadataTask.Result.StatusCode,
                    (int)downloadTask.Result.StatusCode],
                "build=DONE;metadata=immutable;downloadHashMatched=true"));
            return new CaseObservation(
                "A real build completion, immutable export metadata read, and byte download ran concurrently and converged on their pinned hashes.",
                "build=200/DONE;metadata=200;download=200;hashMatched=true");
        }, ct);

        await RunCaseAsync("P9-RACE-CLEAN-02", async () =>
        {
            var metadataBefore = await CountExportMetadataAsync(ct);
            var filesBefore = CountExportFiles();
            HarnessAssert.True(metadataBefore >= 2,
                "P9-RACE cleanup requires CSV and XLSX metadata.");
            HarnessAssert.True(filesBefore >= 2,
                "P9-RACE cleanup requires CSV and XLSX files.");
            await ExpireAllExportsAsync(ct);
            var preview = await CleanupExportsAsync(true, ct);
            HarnessAssert.Equal((int)metadataBefore, RequiredInt(preview, "selected"),
                "P9-RACE cleanup preview selected");
            HarnessAssert.Equal(0, RequiredInt(preview, "deleted"),
                "P9-RACE cleanup preview deleted");
            HarnessAssert.Equal(metadataBefore, await CountExportMetadataAsync(ct),
                "P9-RACE cleanup preview metadata delta");
            HarnessAssert.Equal(filesBefore, CountExportFiles(),
                "P9-RACE cleanup preview file delta");

            var applied = await CleanupExportsAsync(false, ct);
            HarnessAssert.Equal((int)metadataBefore, RequiredInt(applied, "deleted"),
                "P9-RACE cleanup apply deleted");
            HarnessAssert.Equal(0L, await CountExportMetadataAsync(ct),
                "P9-RACE cleanup active metadata");
            HarnessAssert.Equal(0, CountExportFiles(),
                "P9-RACE cleanup files");
            var repeated = await CleanupExportsAsync(false, ct);
            HarnessAssert.Equal(0, RequiredInt(repeated, "selected"),
                "P9-RACE repeated cleanup selected");
            HarnessAssert.Equal(0, RequiredInt(repeated, "deleted"),
                "P9-RACE repeated cleanup deleted");
            _raceCleanupVerified = true;
            return new CaseObservation(
                "Export cleanup preview was zero-write, apply deleted exactly the expired owned metadata/files, and repetition was idempotent.",
                $"preview={metadataBefore}/0;apply={metadataBefore}/{metadataBefore};repeat=0/0;files={filesBefore}->0");
        }, ct);

        await RunCaseAsync("P9-RACE-CLEAN-03", async () =>
        {
            await RunFixtureCycleAsync(11, ct);
            var cycle = _fixtureCycles.Single(item => item.Cycle == 11);
            HarnessAssert.True(cycle.ReturnedToBaseline,
                "P9-RACE autonomous fixture cycle leaked owned records.");
            HarnessAssert.True(
                !string.Equals(cycle.BaselineSha256, cycle.SeededSha256,
                    StringComparison.Ordinal),
                "P9-RACE fixture cycle did not change the seeded snapshot.");
            HarnessAssert.Equal(cycle.BaselineSha256, cycle.CleanedSha256,
                "P9-RACE fixture cycle baseline hash");
            return new CaseObservation(
                "An independent create/build/delete fixture cycle changed the database then returned the full tracked snapshot to its exact baseline.",
                "cycle=1;seedChanged=true;returnedToBaseline=true;ownedHandles=6");
        }, ct);

        await RunCaseAsync("P9-RACE-CLEAN-04", async () =>
        {
            await NormalizeDirectRestartProvenanceAsync(ct);
            var jobsBefore = await CountJobsAsync(ct);
            var exportMetadataBefore = await CountExportMetadataAsync(ct);
            var exportFilesBefore = CountExportFiles();
            var invalid = BuildRaceCandidateOptions() with
            {
                StageLockSha256 = new string('0', 64)
            };
            await RunVariantAsync(
                "P9-RACE-CLEAN-04",
                "race-invalid-stage-lock",
                new BackendServerOptions
                {
                    P9StatRunCandidate = invalid
                },
                async (api, token) =>
                {
                    var activation = await EvaluateActivationAsync(
                        api,
                        StatRunCapabilities.DirectFieldTableLabel,
                        "P9_CORE_DIRECT_JOB",
                        token,
                        ct);
                    HarnessAssert.True(!RequiredBool(activation, "enabled"),
                        "P9-RACE changed stage-lock pin activated.");
                    HarnessAssert.Equal(
                        "STAGE_LOCK_HASH_MISMATCH",
                        RequiredString(activation, "reason"),
                        "P9-RACE changed stage-lock reason");
                    return true;
                },
                ct);
            HarnessAssert.Equal(jobsBefore, await CountJobsAsync(ct),
                "P9-RACE invalid candidate P9 job zero-write");
            HarnessAssert.Equal(exportMetadataBefore, await CountExportMetadataAsync(ct),
                "P9-RACE invalid candidate export metadata zero-write");
            HarnessAssert.Equal(exportFilesBefore, CountExportFiles(),
                "P9-RACE invalid candidate export file zero-write");

            var currentPath = Path.Combine(
                _paths.BackendRoot,
                "Contracts",
                "DynamicFormFlow",
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
            var lockPath = Path.Combine(
                _paths.BackendRoot,
                "Contracts",
                "DynamicFormFlow",
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json");
            HarnessAssert.Equal(
                "d5e77af2cf8a4d4d959c8d642b82fa0bccf5b7b3c8369c6122c5875c01e4c535",
                HashBytes(await File.ReadAllBytesAsync(currentPath, ct)),
                "P9-RACE production CURRENT hash");
            HarnessAssert.Equal(
                "9e580106ccee20cd6ff7975dda652833123ef709024bea74087826c34986e42a",
                HashBytes(await File.ReadAllBytesAsync(lockPath, ct)),
                "P9-RACE production LOCK hash");
            return new CaseObservation(
                "A changed stage-lock pin failed closed in a second Kestrel without writing P9 jobs or exports, while production CURRENT/LOCK remained exact v1.5 bytes.",
                "reason=STAGE_LOCK_HASH_MISMATCH;p9JobDelta=0;exportMetadataDelta=0;exportFileDelta=0;current=v1.5;lock=unchanged");
        }, ct);

        await RunCaseAsync("P9-RACE-CLEAN-05", async () =>
        {
            HarnessAssert.Equal(8, StatConfigPhaseBarrier.CurrentPhase,
                "P9-RACE broad phase");
            var before = await CaptureDatabaseSnapshotAsync(ct);
            var p8 = await RequireApi().GetAsync(
                $"api/stat-config/bundle?ownerKind=DYNAMIC_FORM&ownerId={Fixture().TemplateId}",
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(p8, HttpStatusCode.OK,
                "P9-RACE P8 bundle regression");
            HarnessAssert.Equal(
                "P8-BUNDLE-1",
                p8.Json?["schemaVersion"]?.GetValue<string>(),
                "P9-RACE P8 bundle schema");
            _raceP8RegressionVerified = true;

            var p10 = await RequireApi().PostAsync(
                "api/stat-config/barriers/P10_RECONCILE",
                new
                {
                    ownerKind = "UNIT",
                    ownerId = Fixture().UnitAId,
                    commandId = "p9-race-p10-zero-write-005",
                    expectedBundleHash = Fixture().ConfigHash
                },
                Actor("admin").Token,
                ct: ct);
            ExpectError(
                p10,
                HttpStatusCode.Conflict,
                "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                "P9-RACE P10 barrier");
            HarnessAssert.Equal(
                "P10_RECONCILE",
                ApiHarnessClient.FindStringRecursive(p10.Json, "entry"),
                "P9-RACE P10 entry");
            _raceP10ZeroWriteVerified = true;

            var profileActivation = await EvaluateActivationAsync(
                RequireApi(),
                "FLOW_STATISTIC_PROFILE",
                "P9_CORE_DIRECT_JOB",
                Actor("admin").Token,
                ct);
            HarnessAssert.True(!RequiredBool(profileActivation, "enabled"),
                "P9-RACE statistic profile activated.");
            HarnessAssert.Equal(
                "CAPABILITY_CONFLICT",
                RequiredString(profileActivation, "reason"),
                "P9-RACE statistic profile activation reason");
            using (var catalog = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(
                           BuildRaceCandidateOptions().CatalogPath, ct)))
            {
                var profile = catalog.RootElement
                    .GetProperty("domains")
                    .GetProperty("statisticsCapabilities")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() ==
                                    "FLOW_STATISTIC_PROFILE");
                HarnessAssert.Equal(
                    "INTENTIONAL_BLOCK",
                    profile.GetProperty("status").GetString(),
                    "P9-RACE profile catalog status");
                HarnessAssert.Equal(
                    JsonValueKind.Null,
                    profile.GetProperty("targetPhase").ValueKind,
                    "P9-RACE profile target phase");
            }
            _raceProfileBlockVerified = true;

            var recomputedConfigHash = CanonicalJsonFileSha256(
                Encoding.UTF8.GetBytes(Fixture().ConfigPayloadJson));
            HarnessAssert.Equal(Fixture().ConfigHash, recomputedConfigHash,
                "P9-RACE locked config hash recompute");
            _raceConfigHashVerified = true;

            _raceIndexEvidence = await CaptureRaceIndexEvidenceAsync(ct);
            _raceIndexVerified = _raceIndexEvidence.Passed;
            HarnessAssert.True(_raceIndexVerified,
                "P9-RACE hinted claim query did not execute an IXSCAN.");

            _raceSourceAfter = ComputeRaceSourceFingerprint();
            _raceSourceVerified = string.Equals(
                _raceSourceBefore?.AggregateSha256,
                _raceSourceAfter.AggregateSha256,
                StringComparison.Ordinal);
            HarnessAssert.True(_raceSourceVerified,
                "P9-RACE source fingerprint drifted.");

            var after = await CaptureDatabaseSnapshotAsync(ct);
            HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after),
                "P9-RACE P8/P10/profile/index read boundary snapshot");
            return new CaseObservation(
                "P8 bundle read stayed green; P10/profile stayed blocked with zero writes; config, source fingerprint, CURRENT owner index and IXSCAN pins all recomputed.",
                "phase=8;p8=200;p10=409;profile=INTENTIONAL_BLOCK;databaseDelta=0;configHash=true;sourceFingerprint=true;index=IXSCAN");
        }, ct);
    }

    private JsonObject BuildPairedRaceCreateRequest(string commandId)
        => new()
        {
            ["commandId"] = commandId,
            ["workId"] = Fixture().WorkId,
            ["scopeType"] = "ASSIGNMENT",
            ["scopeId"] = Fixture().AssignmentId,
            ["sourceReportId"] = Fixture().PairedReportId,
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["expectedConfigRevision"] = Fixture().ConfigRevision,
            ["expectedConfigHash"] = Fixture().ConfigHash,
            ["expectedSourceRevision"] = 1,
            ["expectedSourceHash"] = Fixture().PairedSourceHash,
            ["expectedLifecycleRevision"] = 3,
            ["period"] = new JsonObject
            {
                ["periodKey"] = Fixture().PeriodKey,
                ["periodInstanceKey"] = $"{Fixture().PeriodInstanceKey}:B",
                ["periodKind"] = Fixture().PeriodKind,
                ["periodStart"] = Fixture().PeriodStartUtc,
                ["periodEnd"] = Fixture().PeriodEndUtc
            }
        };

    private async Task<P9RaceIndexEvidence> CaptureRaceIndexEvidenceAsync(
        CancellationToken ct)
    {
        const string collection = "work_report_statistic_rebuild_jobs";
        const string index = "ix_workReportStatisticRebuildJobs_p9_claim";
        var indexes = await RequireDatabase()
            .GetCollection<BsonDocument>(collection)
            .Indexes.ListAsync(ct);
        var names = (await indexes.ToListAsync(ct))
            .Select(document => document["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);
        HarnessAssert.True(names.Contains(index),
            "P9-RACE claim index inventory");
        var explain = await RequireDatabase().RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = collection,
                    ["filter"] = new BsonDocument("isActive", true),
                    ["hint"] = index,
                    ["limit"] = 1
                },
                ["verbosity"] = "queryPlanner"
            },
            cancellationToken: ct);
        var stages = new List<string>();
        CollectRaceExplainStages(explain, stages);
        var passed = stages.Contains("IXSCAN", StringComparer.Ordinal) &&
                     explain.ToJson().Contains(index, StringComparison.Ordinal);
        return new P9RaceIndexEvidence(
            collection,
            index,
            stages.Distinct(StringComparer.Ordinal).ToArray(),
            passed);
    }

    private static void CollectRaceExplainStages(
        BsonValue value,
        ICollection<string> stages)
    {
        if (value is BsonDocument document)
        {
            if (document.TryGetValue("stage", out var stage) && stage.IsString)
                stages.Add(stage.AsString);
            foreach (var element in document.Elements)
                CollectRaceExplainStages(element.Value, stages);
            return;
        }
        if (value is BsonArray array)
        {
            foreach (var item in array)
                CollectRaceExplainStages(item, stages);
        }
    }
}
