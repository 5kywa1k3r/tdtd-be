using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRollbackProbe
{
    internal static readonly P10RollbackLogicalRouteSpec[] LogicalRouteRegistry =
    [
        new(StatisticReconciliationRouteRegistry.Create,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.List,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.Read,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.Cancel,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.WorkerClaim,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.WorkerHeartbeat,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.WorkerRetry,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.WorkerPublish,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.WorkerFinalize,
            StatisticReconciliationCapabilities.ExpectedActualDelta),
        new(StatisticReconciliationRouteRegistry.RecheckBegin,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.RecheckClaim,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.RecheckRemediationAuthorize,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.RecheckFinalize,
            StatisticReconciliationCapabilities.ExpectedActualDelta),
        new(StatisticReconciliationRouteRegistry.WorkerCapture,
            StatisticReconciliationCapabilities.SourceToResultReconciliation),
        new(StatisticReconciliationRouteRegistry.ReviewSubmit,
            StatisticReconciliationCapabilities.IndependentReviewSignoff),
        new(StatisticReconciliationRouteRegistry.ReviewRead,
            StatisticReconciliationCapabilities.IndependentReviewSignoff),
        new(StatisticReconciliationRouteRegistry.ReviewSupersede,
            StatisticReconciliationCapabilities.IndependentReviewSignoff),
        new(StatisticReconciliationRouteRegistry.EvidenceCreate,
            StatisticReconciliationCapabilities.ReconciliationEvidenceExport),
        new(StatisticReconciliationRouteRegistry.EvidenceRead,
            StatisticReconciliationCapabilities.ReconciliationEvidenceExport)
    ];

    internal static readonly P10RollbackHttpSurfaceSpec[] RollbackHttpSurfaces =
    [
        new(StatisticReconciliationRouteRegistry.Create, "POST",
            "api/works/{workId}/statistics/{scopeId}/reconciliations",
            P10RollbackBodyKind.Create),
        new(StatisticReconciliationRouteRegistry.List, "GET",
            "api/works/{workId}/statistics/{scopeId}/reconciliations?page=1&pageSize=25",
            P10RollbackBodyKind.None),
        new(StatisticReconciliationRouteRegistry.Read, "GET",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}",
            P10RollbackBodyKind.None),
        new(StatisticReconciliationRouteRegistry.Cancel, "POST",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/cancel",
            P10RollbackBodyKind.Cancel),
        new(StatisticReconciliationRouteRegistry.WorkerClaim, "POST",
            "api/testing/p10/statistic-reconciliations/jobs/claim",
            P10RollbackBodyKind.WorkerClaim),
        new(StatisticReconciliationRouteRegistry.WorkerHeartbeat, "POST",
            "api/testing/p10/statistic-reconciliations/jobs/{id}/heartbeat",
            P10RollbackBodyKind.WorkerFence),
        new(StatisticReconciliationRouteRegistry.WorkerRetry, "POST",
            "api/testing/p10/statistic-reconciliations/jobs/{id}/fail",
            P10RollbackBodyKind.WorkerFail),
        new(StatisticReconciliationRouteRegistry.WorkerPublish, "POST",
            "api/testing/p10/statistic-reconciliations/jobs/{id}/publish-pending",
            P10RollbackBodyKind.WorkerPublish),
        new(StatisticReconciliationRouteRegistry.WorkerFinalize, "POST",
            "api/admin/internal/statistics-reconciliation/{id}/finalize-pending",
            P10RollbackBodyKind.EmptyObject),
        new(StatisticReconciliationRouteRegistry.RecheckBegin, "POST",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/recheck",
            P10RollbackBodyKind.RecheckBegin),
        new(StatisticReconciliationRouteRegistry.RecheckClaim, "POST",
            "api/admin/internal/p10/statistic-reconciliations/jobs/{id}/recheck/claim",
            P10RollbackBodyKind.WorkerClaim),
        new(StatisticReconciliationRouteRegistry.RecheckRemediationAuthorize,
            "POST",
            "api/admin/internal/p10/statistic-reconciliations/jobs/{id}/recheck/remediation/authorize",
            P10RollbackBodyKind.EmptyObject),
        new(StatisticReconciliationRouteRegistry.WorkerCapture, "POST",
            "api/admin/internal/statistics-reconciliation/{id}/actual-capture/claimed",
            P10RollbackBodyKind.WorkerFence),
        new(StatisticReconciliationRouteRegistry.ReviewSubmit, "POST",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/review-decisions",
            P10RollbackBodyKind.ReviewSubmit),
        new(StatisticReconciliationRouteRegistry.ReviewRead, "GET",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/review-decisions",
            P10RollbackBodyKind.None),
        new(StatisticReconciliationRouteRegistry.ReviewSupersede, "POST",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/review-supersessions",
            P10RollbackBodyKind.ReviewSupersede),
        new(StatisticReconciliationRouteRegistry.EvidenceCreate, "POST",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/evidence-exports",
            P10RollbackBodyKind.EvidenceCreate),
        new(StatisticReconciliationRouteRegistry.EvidenceRead, "GET",
            "api/works/{workId}/statistics/{scopeId}/reconciliations/{id}/evidence-exports/{exportId}/download",
            P10RollbackBodyKind.None)
    ];

    private async Task RunLogicalRegistryAsync(CancellationToken ct)
    {
        HarnessAssert.Equal(19, StatisticReconciliationRouteRegistry.Snapshot().Count,
            "P10-12 product logical route registry cardinality");
        HarnessAssert.Equal(19, LogicalRouteRegistry.Length,
            "P10-12 frozen logical route registry cardinality");
        var product = StatisticReconciliationRouteRegistry.Snapshot();
        foreach (var spec in LogicalRouteRegistry)
        {
            HarnessAssert.True(product.TryGetValue(spec.RouteId,
                    out var capability),
                "P10-12 product registry omitted " + spec.RouteId);
            HarnessAssert.Equal(spec.CapabilityId, capability,
                "P10-12 capability mapping drift " + spec.RouteId);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StatisticReconciliationCandidate:Enabled"] = "false"
            })
            .Build();
        var environment = new P10RollbackHostEnvironment
        {
            EnvironmentName = "Testing",
            ApplicationName = "tdtd-be",
            ContentRootPath = _paths.BackendRoot,
            ContentRootFileProvider = new Microsoft.Extensions.FileProviders
                .PhysicalFileProvider(_paths.BackendRoot)
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            environment);
        services.AddSingleton<IOptions<MongoOptions>>(Microsoft.Extensions.Options.Options.Create(
            new MongoOptions
            {
                ConnectionString = Mongo().ConnectionString,
                Database = Mongo().DatabaseName
            }));
        services.AddScoped<IStatisticReconciliationCandidateActivation,
            StatisticReconciliationCapabilityActivation>();
        await using var provider = services.BuildServiceProvider();

        foreach (var spec in LogicalRouteRegistry)
        {
            var before = await CaptureInventoryAsync(ct);
            using var scope = provider.CreateScope();
            var activation = scope.ServiceProvider.GetRequiredService<
                IStatisticReconciliationCandidateActivation>();
            var evaluation = activation.EvaluateCapability(
                spec.CapabilityId,
                spec.RouteId);
            int status;
            string? errorCode;
            string? entry = null;
            string? targetPhase = null;
            string? barrierReason = null;
            string? eligibility = null;
            string? freshness = null;
            var blocked = false;
            var enabled = false;

            if (_mode == RollbackMode)
            {
                HarnessAssert.True(!evaluation.Enabled &&
                    evaluation.Binding is null,
                    "P10-12 rollback activation unexpectedly enabled " +
                    spec.RouteId);
                HarnessAssert.Equal("CATALOG_STATE_ROLLED_BACK",
                    evaluation.Reason,
                    "P10-12 rollback activation reason " + spec.RouteId);
                try
                {
                    _ = activation.RequireCapability(
                        spec.CapabilityId,
                        spec.RouteId);
                    throw new InvalidOperationException(
                        "P10_ROLLBACK_LOGICAL_ROUTE_DID_NOT_BLOCK:" +
                        spec.RouteId);
                }
                catch (AppException error)
                {
                    status = error.Descriptor.HttpStatus;
                    errorCode = error.Code.ToString();
                    HarnessAssert.Equal(
                        AppErrorCode
                            .DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
                        error.Code,
                        "P10-12 logical barrier code " + spec.RouteId);
                    var details = JsonSerializer.SerializeToElement(
                        error.Details,
                        EvidenceJson.Options);
                    entry = RequiredDetail(details, "entry");
                    targetPhase = RequiredDetail(details, "targetPhase");
                    barrierReason = RequiredDetail(details, "reason");
                    eligibility = RequiredDetail(details, "eligibility");
                    freshness = RequiredDetail(details, "freshness");
                    RequireExactBarrier(
                        spec.RouteId,
                        status,
                        errorCode,
                        entry,
                        targetPhase,
                        barrierReason,
                        eligibility,
                        freshness);
                    blocked = true;
                }
            }
            else
            {
                HarnessAssert.True(evaluation.Enabled &&
                    evaluation.Binding is not null,
                    "P10-12 restored activation disabled " + spec.RouteId +
                    ":" + evaluation.Reason);
                var binding = activation.RequireCapability(
                    spec.CapabilityId,
                    spec.RouteId);
                HarnessAssert.Equal(ChainId, binding.ChainId,
                    "P10-12 restored binding chain " + spec.RouteId);
                HarnessAssert.Equal(PromptId, binding.PromptId,
                    "P10-12 restored binding prompt " + spec.RouteId);
                HarnessAssert.Equal("1.7", binding.CatalogVersion,
                    "P10-12 restored binding catalog " + spec.RouteId);
                HarnessAssert.True(binding.Promotions.Contains(
                        spec.CapabilityId,
                        StringComparer.Ordinal),
                    "P10-12 restored promotion missing " + spec.CapabilityId);
                status = 200;
                errorCode = null;
                enabled = true;
            }

            var after = await CaptureInventoryAsync(ct);
            RequireSameInventory(
                "P10-12 logical route " + spec.RouteId,
                before,
                after);
            _logicalRoutes.Add(new(
                spec.RouteId,
                spec.CapabilityId,
                "DI_REQUIRE_CAPABILITY",
                status,
                errorCode,
                evaluation.Reason,
                entry,
                targetPhase,
                barrierReason,
                eligibility,
                freshness,
                enabled,
                blocked,
                InventoryCollections.Length,
                before.SemanticSha256,
                after.SemanticSha256,
                0,
                blocked || enabled));
        }
    }

    private async Task RunRollbackHttpSurfacesAsync(CancellationToken ct)
    {
        HarnessAssert.Equal(18, RollbackHttpSurfaces.Length,
            "P10-12 representative reachable HTTP surface count");
        var fakeId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var exportId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        for (var index = 0; index < RollbackHttpSurfaces.Length; index++)
        {
            var spec = RollbackHttpSurfaces[index];
            var path = ResolvePath(spec.Path, fakeId, exportId);
            var body = BuildBody(spec.Body, index);
            var before = await CaptureInventoryAsync(ct);
            var response = await Api().SendAsync(
                new HttpMethod(spec.Method),
                path,
                body,
                Actor("admin").Token,
                headers: null,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.Conflict,
                $"P10-12 rollback HTTP {spec.Method} {path}");
            var errorCode = RequiredRecursive(response.Json, "errorCode", "code");
            var entry = RequiredRecursive(response.Json, "entry");
            var targetPhase = RequiredRecursive(response.Json, "targetPhase");
            var reason = RequiredRecursive(response.Json, "reason");
            var eligibility = RequiredRecursive(response.Json, "eligibility");
            var freshness = RequiredRecursive(response.Json, "freshness");
            RequireExactBarrier(
                spec.RouteId,
                (int)response.StatusCode,
                errorCode,
                entry,
                targetPhase,
                reason,
                eligibility,
                freshness);
            var after = await CaptureInventoryAsync(ct);
            RequireSameInventory(
                "P10-12 HTTP surface " + spec.RouteId,
                before,
                after);
            _httpSurfaces.Add(new(
                spec.RouteId,
                spec.Method,
                path,
                (int)response.StatusCode,
                errorCode,
                entry,
                targetPhase,
                reason,
                eligibility,
                freshness,
                InventoryCollections.Length,
                before.SemanticSha256,
                after.SemanticSha256,
                0,
                true));
        }
    }

    private string ResolvePath(string template, string id, string exportId)
        => template
            .Replace("{workId}", Fixture().WorkId, StringComparison.Ordinal)
            .Replace("{scopeId}", Fixture().ScopeAssignmentId,
                StringComparison.Ordinal)
            .Replace("{id}", id, StringComparison.Ordinal)
            .Replace("{exportId}", exportId, StringComparison.Ordinal);

    private JsonNode? BuildBody(P10RollbackBodyKind kind, int index)
    {
        var hashA = HashBytes(System.Text.Encoding.UTF8.GetBytes(
            "p10-rollback-hash-a"));
        var hashB = HashBytes(System.Text.Encoding.UTF8.GetBytes(
            "p10-rollback-hash-b"));
        return kind switch
        {
            P10RollbackBodyKind.None => null,
            P10RollbackBodyKind.EmptyObject => new JsonObject(),
            P10RollbackBodyKind.Create => new JsonObject
            {
                ["commandId"] = $"p1012-rollback-create-{index:00}",
                ["p9ResultKind"] = "DIRECT",
                ["p9ResultId"] = Fixture().P9ResultId,
                ["p9RunId"] = Fixture().P9RunId,
                ["conceptKey"] = Fixture().ConceptKey,
                ["grain"] = Fixture().Grain,
                ["filter"] = new JsonObject
                {
                    ["periodKey"] = Fixture().PeriodKey
                }
            },
            P10RollbackBodyKind.Cancel => new JsonObject
            {
                ["commandId"] = $"p1012-rollback-cancel-{index:00}",
                ["expectedStateRevision"] = 1,
                ["expectedStateHash"] = hashA
            },
            P10RollbackBodyKind.WorkerClaim => new JsonObject
            {
                ["workerId"] = $"p1012-rollback-worker-{index:00}"
            },
            P10RollbackBodyKind.WorkerFence => new JsonObject
            {
                ["workerId"] = $"p1012-rollback-worker-{index:00}",
                ["claimToken"] = hashA
            },
            P10RollbackBodyKind.WorkerFail => new JsonObject
            {
                ["workerId"] = $"p1012-rollback-worker-{index:00}",
                ["claimToken"] = hashA,
                ["failureCode"] = "P10_CLOSE_ROLLBACK",
                ["transient"] = false
            },
            P10RollbackBodyKind.WorkerPublish => new JsonObject
            {
                ["workerId"] = $"p1012-rollback-worker-{index:00}",
                ["claimToken"] = hashA,
                ["generationId"] = hashA,
                ["generationHash"] = hashB
            },
            P10RollbackBodyKind.RecheckBegin => new JsonObject
            {
                ["commandId"] = $"p1012-rollback-recheck-{index:00}",
                ["expectedStateRevision"] = 1,
                ["expectedStateHash"] = hashA
            },
            P10RollbackBodyKind.ReviewSubmit => new JsonObject
            {
                ["commandId"] = $"p1012-rollback-review-{index:00}",
                ["gate"] = "FORM",
                ["decision"] = "APPROVE",
                ["expectedStateRevision"] = 1
            },
            P10RollbackBodyKind.ReviewSupersede => new JsonObject
            {
                ["commandId"] = $"p1012-rollback-supersede-{index:00}",
                ["previousGenerationId"] = hashA,
                ["expectedStateRevision"] = 1
            },
            P10RollbackBodyKind.EvidenceCreate => new JsonObject
            {
                ["commandId"] = $"p1012-rollback-export-{index:00}",
                ["format"] = "JSON",
                ["includeOperatorDetail"] = true
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unknown P10 rollback HTTP body kind.")
        };
    }

    private static void RequireExactBarrier(
        string routeId,
        int status,
        string? errorCode,
        string? entry,
        string? targetPhase,
        string? reason,
        string? eligibility,
        string? freshness)
    {
        HarnessAssert.Equal(409, status,
            "P10-12 rollback status " + routeId);
        HarnessAssert.Equal(
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
            errorCode,
            "P10-12 rollback errorCode " + routeId);
        HarnessAssert.Equal("P10_RECONCILE", entry,
            "P10-12 rollback entry " + routeId);
        HarnessAssert.Equal("P10", targetPhase,
            "P10-12 rollback targetPhase " + routeId);
        HarnessAssert.Equal("P8_CONFIG_ONLY", reason,
            "P10-12 rollback reason " + routeId);
        HarnessAssert.Equal("BLOCKED_UNTIL_TARGET_PHASE", eligibility,
            "P10-12 rollback eligibility " + routeId);
        HarnessAssert.Equal("NOT_APPLICABLE", freshness,
            "P10-12 rollback freshness " + routeId);
    }

    private static string RequiredDetail(JsonElement value, string property)
        => value.TryGetProperty(property, out var item) &&
           item.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!
            : throw new InvalidOperationException(
                "P10_ROLLBACK_DETAIL_MISSING:" + property);

    private static string RequiredRecursive(
        JsonNode? value,
        params string[] properties)
    {
        foreach (var property in properties)
        {
            var found = ApiHarnessClient.FindStringRecursive(value, property);
            if (!string.IsNullOrWhiteSpace(found)) return found;
        }
        throw new InvalidOperationException(
            "P10_ROLLBACK_RESPONSE_FIELD_MISSING:" +
            string.Join("|", properties));
    }
}

internal sealed record P10RollbackLogicalRouteSpec(
    string RouteId,
    string CapabilityId);

internal enum P10RollbackBodyKind
{
    None,
    EmptyObject,
    Create,
    Cancel,
    WorkerClaim,
    WorkerFence,
    WorkerFail,
    WorkerPublish,
    RecheckBegin,
    ReviewSubmit,
    ReviewSupersede,
    EvidenceCreate
}

internal sealed record P10RollbackHttpSurfaceSpec(
    string RouteId,
    string Method,
    string Path,
    P10RollbackBodyKind Body);

internal sealed record P10RollbackLogicalRouteEvidence(
    string RouteId,
    string CapabilityId,
    string ProbeKind,
    int Status,
    string? ErrorCode,
    string? EvaluationReason,
    string? Entry,
    string? TargetPhase,
    string? BarrierReason,
    string? Eligibility,
    string? Freshness,
    bool Enabled,
    bool Blocked,
    int StoreCount,
    string BeforeInventorySha256,
    string AfterInventorySha256,
    int Writes,
    bool Passed);

internal sealed record P10RollbackHttpSurfaceEvidence(
    string RouteId,
    string Method,
    string Path,
    int HttpStatus,
    string ErrorCode,
    string Entry,
    string TargetPhase,
    string Reason,
    string Eligibility,
    string Freshness,
    int StoreCount,
    string BeforeInventorySha256,
    string AfterInventorySha256,
    int Writes,
    bool Passed);
