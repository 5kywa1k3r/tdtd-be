using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP805IsolationCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-018",
            "system_admin+unit_manager_a+outsider_b",
            [
                "p8-adv-018-forbidden-grant",
                "p8-adv-018-legacy-grant"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var admin = Actor("system_admin");
                var manager = Actor("unit_manager_a");
                var outsider = Actor("outsider_b");
                var fixture = AdvancedFixture("018");
                var foreign = await _api.GetAsync(
                    AdvancedConfigRoute(fixture),
                    outsider.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(foreign, HttpStatusCode.Forbidden,
                    "P8 Advanced foreign scope barrier");

                var quota = await ReadTokenQuotaAsync(
                    manager,
                    manager.UnitId,
                    ct);
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-018/non-admin-grant",
                    () => _api.PostAsync(
                        TokenPoolGrantRoute(manager.UnitId),
                        Envelope(
                            "p8-adv-018-forbidden-grant",
                            quota.Revision,
                            quota.PoolHash,
                            TokenGrantPayload(1, "must be admin")),
                        manager.Token,
                        ct: ct),
                    HttpStatusCode.Forbidden,
                    "WORK_SUMMARY_TOKEN_GRANT_FORBIDDEN",
                    null,
                    null,
                    ct);
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-018/legacy-grant",
                    () => _api.PostAsync(
                        "api/work-summary-tokens/grants",
                        new JsonObject
                        {
                            ["ownerUnitId"] = manager.UnitId,
                            ["units"] = 1,
                            ["tokenKind"] =
                                "ADVANCED_SUMMARY_CONFIG_LOCK",
                            ["periodMonthKey"] = DateTime.UtcNow
                                .ToString("yyyy-MM"),
                            ["reason"] = "legacy writer must be blocked"
                        },
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.operation",
                    "WORK_SUMMARY_TOKEN_LEGACY_GRANT_BLOCKED",
                    ct);

                await RequireP805SourceIsolationAsync(ct);
                foreach (var collection in ProhibitedCollections)
                {
                    HarnessAssert.Equal(0L,
                        await _database.GetCollection<BsonDocument>(collection)
                            .CountDocumentsAsync(
                                FilterDefinition<BsonDocument>.Empty,
                                cancellationToken: ct),
                        $"P8-05 left prohibited documents in {collection}");
                }
                return new CaseObservation(
                    "Auth/scope/admin-only grants, legacy writer barrier and static source isolation held; every prohibited result/projection/export/reconcile collection stayed empty.",
                    "foreign=403;nonAdminGrant=403+0W;legacyGrant=400+0W;sourceIsolation=true;prohibitedAll=0;realKestrel+directMongo=true;cleanupOwned=true");
            },
            ct);
    }

    private async Task RequireP805SourceIsolationAsync(CancellationToken ct)
    {
        var advancedRoot = Path.Combine(
            _paths.BackendRoot,
            "Services",
            "WorkAssignments",
            "AdvancedSummary");
        var tokenRoot = Path.Combine(
            _paths.BackendRoot,
            "Services",
            "WorkAssignments",
            "SummaryTokens");
        var sources = Directory.EnumerateFiles(
                advancedRoot,
                "*P805*.cs",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                tokenRoot,
                "*P805*.cs",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => path,
                File.ReadAllText,
                StringComparer.Ordinal);
        HarnessAssert.True(sources.Count >= 2,
            "P8-05 configuration/quota source partials are absent");
        var forbiddenRuntimeDependencies = new[]
        {
            "RequestPreviewAsync(",
            "RunPreviewJobAsync(",
            "RequestDayNodeBuildAsync(",
            "RequestMonthNodeBuildAsync(",
            "RequestYearNodeBuildAsync(",
            "QueryHierarchyAsync(",
            "_ctx.WorkAssignmentReports",
            "_ctx.WorkAssignmentAdvancedSummaryDayNodes",
            "_ctx.WorkAssignmentAdvancedSummaryMonthNodes",
            "_ctx.WorkAssignmentAdvancedSummaryYearNodes"
        };
        foreach (var source in sources)
        {
            foreach (var token in forbiddenRuntimeDependencies)
            {
                HarnessAssert.True(
                    !source.Value.Contains(token, StringComparison.Ordinal),
                    $"P8-05 config partial {Path.GetFileName(source.Key)} references forbidden runtime dependency {token}");
            }
        }

        var controllerPath = Path.Combine(
            _paths.BackendRoot,
            "Controllers",
            "WorkAssignmentAdvancedSummaryController.cs");
        var controller = await File.ReadAllTextAsync(controllerPath, ct);
        var bindings = new[]
        {
            ("[HttpGet(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config\")]", "GetP8ConfigAsync("),
            ("[HttpGet(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/versions\")]", "ListP8ConfigVersionsAsync("),
            ("[HttpGet(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/versions/{versionNo:int}\")]", "GetP8ConfigVersionAsync("),
            ("[HttpPut(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config\")]", "PutP8ConfigAsync("),
            ("[HttpPost(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/lock\")]", "LockP8ConfigAsync("),
            ("[HttpPost(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/next-draft\")]", "CreateNextP8DraftAsync("),
            ("[HttpPost(\"assignments/{assignmentId}/templates/{dynamicFormTemplateId}/sections/{sectionId}/config/archive\")]", "ArchiveP8ConfigAsync(")
        };
        foreach (var binding in bindings)
        {
            var start = controller.IndexOf(binding.Item1, StringComparison.Ordinal);
            HarnessAssert.True(start >= 0,
                $"P8-05 controller route is absent: {binding.Item1}");
            var next = controller.IndexOf(
                "\n    [Http",
                start + binding.Item1.Length,
                StringComparison.Ordinal);
            var action = controller[start..(next >= 0 ? next : controller.Length)];
            HarnessAssert.True(action.Contains(binding.Item2,
                    StringComparison.Ordinal),
                $"P8-05 controller route does not bind {binding.Item2}");
            foreach (var forbidden in new[]
                     {
                         "RequestPreviewAsync(",
                         "RequestDayNodeBuildAsync(",
                         "QueryHierarchyAsync("
                     })
            {
                HarnessAssert.True(!action.Contains(forbidden,
                        StringComparison.Ordinal),
                    $"P8-05 config route leaked into runtime method {forbidden}");
            }
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-05-source-isolation.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                configurationPartialFiles = sources.Select(item => new
                {
                    file = Path.GetRelativePath(_paths.BackendRoot, item.Key),
                    sha256 = Sha256(Encoding.UTF8.GetBytes(item.Value))
                }),
                forbiddenRuntimeDependencies,
                forbiddenRuntimeDependencyCount = 0,
                controllerBindings = bindings.Select(binding => new
                {
                    route = binding.Item1,
                    serviceMethod = binding.Item2,
                    executorFree = true
                }),
                legacyPreviewBarrierVerifiedByApi = true,
                legacyHierarchyBarrierVerifiedByApi = true,
                legacyTokenGrantBarrierVerifiedByApi = true,
                realApiAndMongoOraclesRemainPrimary = true
            },
            ct);
    }
}
