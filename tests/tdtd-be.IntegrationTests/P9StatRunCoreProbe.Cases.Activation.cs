using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private static readonly (string CapabilityId, string RouteId)[] CoreActivations =
    [
        ("DIRECT_FIELD_TABLE_LABEL", "P9_CORE_DIRECT_JOB"),
        ("BASIC_SUMMARY", "P9_CORE_BASIC_JOB"),
        ("ADVANCED_SUMMARY", "P9_CORE_ADVANCED_JOB"),
        ("DIFF", "P9_CORE_DIFF_JOB"),
        ("FLOW_SCOPES", "P9_CORE_FLOW_SCOPES_JOB")
    ];

    private static readonly string[] FrozenP8ActualRoutes =
    [
        "POST api/work-report-field-statistics/rebuild",
        "POST api/work-report-table-statistics/rebuild",
        "POST api/work-report-statistic-diffs/run",
        "POST api/work-assignment-advanced-summary/configs/{id}/preview",
        "POST api/work-assignment-advanced-summary/configs/{id}/hierarchy/day/{day}/build",
        "POST api/work-assignment-advanced-summary/configs/{id}/hierarchy/month/{month}/build",
        "POST api/work-assignment-advanced-summary/configs/{id}/hierarchy/year/{year}/build",
        "POST api/work-assignment-reports/{id}/draft/apply-dynamic-form-aggregate",
        "POST api/work-assignment-reports/{id}/draft/preview-dynamic-form-aggregate",
        "POST api/admin/operations/job-runs/statistic-rebuild-jobs/process",
        "POST api/admin/operations/job-runs/statistic-rebuild-jobs/{id}/reset",
        "POST api/admin/operations/job-runs/basic-summary-jobs/{id}/reset",
        "POST api/admin/operations/job-runs/advanced-summary-nodes/DAY/{id}/reset",
        "POST api/admin/operations/job-runs/advanced-summary-nodes/cleanup",
        "POST api/work-report-field-statistics/summary",
        "POST api/work-report-field-statistics/text-concat",
        "POST api/work-report-table-statistics/summary",
        "POST api/work-report-label-statistics/summary",
        "POST api/work-assignment-basic-summary/summary",
        "POST api/work-assignment-basic-summary/once",
        "POST api/work-assignment-advanced-summary/configs/{id}/hierarchy/query",
        "GET api/dashboard-mindmap/nodes/{id}/summary",
        "POST api/dashboard-mindmap/nodes/{id}/table-metrics/reports/search",
        "POST api/dashboard-mindmap/nodes/{id}/field-metrics/reports/search",
        "POST api/dashboard-mindmap/nodes/{id}/labels/reports/search",
        "POST api/work-assignment-aggregate-table/table",
        "POST api/work-assignment-aggregate-table/dynamic-form/table",
        "GET api/admin/operations/job-runs/statistic-rebuild-jobs",
        "GET api/admin/operations/job-runs/flow-statistics/diagnostics",
        "GET api/admin/operations/job-runs/basic-summary-jobs",
        "GET api/admin/operations/job-runs/advanced-summary-nodes",
        "POST api/admin/operations/job-runs/advanced-summary-nodes/diagnostics/day",
        "POST api/admin/operations/job-runs/advanced-summary-nodes/diagnostics/month",
        "POST api/admin/operations/job-runs/advanced-summary-nodes/diagnostics/year",
        "POST api/work-report-field-statistics/text-concat/export"
    ];

    private async Task RunActivationCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-CORE-ACT-01",
            async () =>
            {
                foreach (var item in CoreActivations)
                {
                    var response = await EvaluateActivationAsync(
                        RequireApi(),
                        item.CapabilityId,
                        item.RouteId,
                        Actor("admin").Token,
                        ct);
                    HarnessAssert.True(
                        RequiredBool(response, "enabled"),
                        $"{item.RouteId} was not independently activated.");
                    var binding = ApiHarnessClient.RequiredObject(
                        response["binding"],
                        $"{item.RouteId} activation binding");
                    HarnessAssert.Equal(
                        ChainId,
                        RequiredString(binding, "chainId"),
                        $"{item.RouteId} chain pin");
                    HarnessAssert.Equal(
                        0,
                        ApiHarnessClient.RequiredInt(binding, "stage"),
                        $"{item.RouteId} stage");
                    HarnessAssert.Equal(
                        "1.6",
                        RequiredString(binding, "catalogVersion"),
                        $"{item.RouteId} catalog version");
                }
                return new CaseObservation(
                    "All five frozen foundation capability/route pairs activated only through the hash-bound test candidate.",
                    "capabilities=5;routes=5;stage=0;candidateOnly=true");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ACT-02",
            async () =>
            {
                var unknownCapability = await EvaluateActivationAsync(
                    RequireApi(),
                    "UNKNOWN_CAPABILITY",
                    "P9_CORE_DIRECT_JOB",
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    !RequiredBool(unknownCapability, "enabled"),
                    "Unknown capability unexpectedly activated.");
                HarnessAssert.Equal(
                    "CAPABILITY_CONFLICT",
                    RequiredString(unknownCapability, "reason"),
                    "Unknown capability reason");
                var unmappedRoute = await EvaluateActivationAsync(
                    RequireApi(),
                    "DIRECT_FIELD_TABLE_LABEL",
                    "P9_CORE_UNMAPPED_JOB",
                    Actor("admin").Token,
                    ct);
                HarnessAssert.True(
                    !RequiredBool(unmappedRoute, "enabled"),
                    "Unmapped route unexpectedly activated.");
                HarnessAssert.Equal(
                    "ROUTE_NOT_PROVEN",
                    RequiredString(unmappedRoute, "reason"),
                    "Unmapped route reason");
                return new CaseObservation(
                    "Unknown capability and unmapped route both failed closed before writes.",
                    "unknown=CAPABILITY_CONFLICT;unmapped=ROUTE_NOT_PROVEN;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ACT-03",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var invalid = BuildCandidateOptions() with
                {
                    StageLockSha256 = new string('0', 64)
                };
                await RunVariantAsync(
                    "P9-CORE-ACT-03",
                    "invalid-stage-lock-pin",
                    new BackendServerOptions
                    {
                        P9StatRunCandidate = invalid
                    },
                    async (api, token) =>
                    {
                        var response = await EvaluateActivationAsync(
                            api,
                            "DIRECT_FIELD_TABLE_LABEL",
                            "P9_CORE_DIRECT_JOB",
                            token,
                            ct);
                        HarnessAssert.True(
                            !RequiredBool(response, "enabled"),
                            "Changed stage-lock pin unexpectedly activated.");
                        HarnessAssert.Equal(
                            "CANDIDATE_PIN_MISMATCH",
                            RequiredString(response, "reason"),
                            "Changed stage-lock reason");
                        return true;
                    },
                    ct);
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Invalid stage-lock activation changed jobs");
                return new CaseObservation(
                    "A changed stage-lock pin failed closed in an isolated Kestrel process.",
                    "stageLock=changed;reason=CANDIDATE_PIN_MISMATCH;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ACT-04",
            async () =>
            {
                HarnessAssert.Equal(
                    35,
                    FrozenP8ActualRoutes.Length,
                    "Frozen P8 actual-route inventory count");
                var before = await CountJobsAsync(ct);
                var disabled = BuildCandidateOptions() with { Enabled = false };
                await RunVariantAsync(
                    "P9-CORE-ACT-04",
                    "current-v15-disabled",
                    new BackendServerOptions
                    {
                        P9StatRunCandidate = disabled
                    },
                    async (api, token) =>
                    {
                        foreach (var item in CoreActivations)
                        {
                            var owned = await EvaluateActivationAsync(
                                api,
                                item.CapabilityId,
                                item.RouteId,
                                token,
                                ct);
                            HarnessAssert.True(
                                !RequiredBool(owned, "enabled"),
                                $"CURRENT v1.5 unexpectedly opened {item.RouteId}.");
                            HarnessAssert.Equal(
                                "CANDIDATE_DISABLED",
                                RequiredString(owned, "reason"),
                                $"CURRENT v1.5 owned-route reason {item.RouteId}");
                        }
                        foreach (var route in FrozenP8ActualRoutes)
                        {
                            var response = await EvaluateActivationAsync(
                                api,
                                "DIRECT_FIELD_TABLE_LABEL",
                                route,
                                token,
                                ct);
                            HarnessAssert.True(
                                !RequiredBool(response, "enabled"),
                                $"Frozen P8 route unexpectedly activated: {route}");
                            HarnessAssert.Equal(
                                "ROUTE_NOT_PROVEN",
                                RequiredString(response, "reason"),
                                $"Frozen P8 route reason: {route}");
                        }
                        return true;
                    },
                    ct);
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "CURRENT v1.5 route denial changed jobs");
                return new CaseObservation(
                    "All five owned core routes and the exact frozen 35-route P8 rollback inventory stayed denied under CURRENT v1.5.",
                    "current=1.5;ownedRoutes=5;rollbackRoutes=35;enabled=0;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ACT-05",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var wrongDatabase = BuildCandidateOptions() with
                {
                    ExpectedDatabase = "tdtd_p9_wrong_database"
                };
                await RunVariantAsync(
                    "P9-CORE-ACT-05",
                    "database-mismatch",
                    new BackendServerOptions
                    {
                        P9StatRunCandidate = wrongDatabase
                    },
                    async (api, token) =>
                    {
                        var response = await EvaluateActivationAsync(
                            api,
                            "DIRECT_FIELD_TABLE_LABEL",
                            "P9_CORE_DIRECT_JOB",
                            token,
                            ct);
                        HarnessAssert.True(
                            !RequiredBool(response, "enabled"),
                            "Wrong candidate database unexpectedly activated.");
                        HarnessAssert.Equal(
                            "DATABASE_MISMATCH",
                            RequiredString(response, "reason"),
                            "Database mismatch reason");
                        return true;
                    },
                    ct);
                await RunVariantAsync(
                    "P9-CORE-ACT-05",
                    "environment-mismatch",
                    new BackendServerOptions
                    {
                        EnvironmentNameOverride = "Development",
                        P9StatRunCandidate = BuildCandidateOptions()
                    },
                    async (api, token) =>
                    {
                        var response = await api.PostAsync(
                            "api/stat-runs/DIRECT_FIELD_TABLE_LABEL/jobs",
                            BuildCreateRequest(
                                "p9-core-environment-mismatch-005"),
                            token,
                            ct: ct);
                        ExpectError(
                            response,
                            HttpStatusCode.Conflict,
                            "STAT_RUN_CANDIDATE_INVALID",
                            "Non-Testing candidate create");
                        HarnessAssert.Equal(
                            "ENVIRONMENT_MISMATCH",
                            ApiHarnessClient.FindStringRecursive(
                                response.Json,
                                "reason"),
                            "Environment mismatch reason");
                        return true;
                    },
                    ct);
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Candidate environment/database mismatch changed jobs");
                return new CaseObservation(
                    "Database and environment mismatches failed closed in separate owned Kestrel variants.",
                    "database=DATABASE_MISMATCH;environment=ENVIRONMENT_MISMATCH;writes=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-ACT-06",
            async () =>
            {
                HarnessAssert.Equal(
                    8,
                    StatConfigPhaseBarrier.CurrentPhase,
                    "Broad phase changed");
                var before = await CountJobsAsync(ct);
                var zeroWriteBefore =
                    await CaptureDatabaseSnapshotAsync(ct);
                AssertProhibitedRunsAbsent(
                    zeroWriteBefore,
                    "ACT-06 before P10/profile");
                var p10 = await RequireApi().PostAsync(
                    "api/stat-config/barriers/P10_RECONCILE",
                    new
                    {
                        ownerKind = "UNIT",
                        ownerId = Fixture().UnitAId,
                        commandId = "p9-core-p10-barrier-006",
                        expectedBundleHash = Fixture().ConfigHash
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    p10,
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    "P10 reconcile barrier");
                HarnessAssert.Equal(
                    "P10_RECONCILE",
                    ApiHarnessClient.FindStringRecursive(p10.Json, "entry"),
                    "P10 barrier entry");
                HarnessAssert.Equal(
                    "P10",
                    ApiHarnessClient.FindStringRecursive(p10.Json, "targetPhase"),
                    "P10 barrier target phase");

                var catalogPath = BuildCandidateOptions().CatalogPath;
                using var catalog = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(catalogPath, ct));
                var capabilities = catalog.RootElement
                    .GetProperty("domains")
                    .GetProperty("statisticsCapabilities")
                    .EnumerateArray();
                var profile = capabilities.Single(item =>
                    item.GetProperty("id").GetString() ==
                    "FLOW_STATISTIC_PROFILE");
                HarnessAssert.Equal(
                    "INTENTIONAL_BLOCK",
                    profile.GetProperty("status").GetString(),
                    "Candidate profile status");
                HarnessAssert.Equal(
                    JsonValueKind.Null,
                    profile.GetProperty("targetPhase").ValueKind,
                    "Candidate profile target phase");

                var currentPath = Path.Combine(
                    _paths.BackendRoot,
                    "Contracts",
                    "DynamicFormFlow",
                    "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
                HarnessAssert.Equal(
                    "d5e77af2cf8a4d4d959c8d642b82fa0bccf5b7b3c8369c6122c5875c01e4c535",
                    HashBytes(await File.ReadAllBytesAsync(currentPath, ct)),
                    "Production CURRENT raw SHA");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "P10/profile barrier changed jobs");
                var zeroWriteAfter =
                    await CaptureDatabaseSnapshotAsync(ct);
                AssertProhibitedRunsAbsent(
                    zeroWriteAfter,
                    "ACT-06 after P10/profile");
                HarnessAssert.Equal(
                    SnapshotSha256(zeroWriteBefore),
                    SnapshotSha256(zeroWriteAfter),
                    "P10/profile exact database snapshot");
                return new CaseObservation(
                    "Global phase stayed 8, P10 remained stable 409/zero-write, and FLOW_STATISTIC_PROFILE stayed intentional-block/null.",
                    "phase=8;p10=409;profile=INTENTIONAL_BLOCK/null;writes=0");
            },
            ct);
    }
}
