using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Controllers;
using tdtd_be.DTOs.StatisticsReconciliation;

var cases = new (string Id, Action Run)[]
{
    ("P10-T26-CALL-01", MissingPlanFailsBeforeOwners),
    ("P10-T26-CALL-02", ScopeAuthorizationPrecedesHiddenIds),
    ("P10-T26-CALL-03", StaleFenceReadsZeroHiddenOwners),
    ("P10-T26-CALL-04", RegistryIsFixedAndDtosHaveNoRawSlices),
    ("P10-T26-CALL-05", ProductionRouteCallsCaptureService),
    ("P10-T26-CALL-06", ExportUsesExactP9OwnerPins),
    ("P10-T26-CALL-07", ReadyUsesOneCasAndStaleUsesZero)
};

foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {test.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Require(cases.Length == 7, "EXACT_CASE_COUNT");
Console.WriteLine(
    "P10_T26_CALLABILITY_OK cases=7 planFailClosed=true " +
    "authBeforeHiddenIds=true staleLeaseZeroRead=true fixedRegistry=true " +
    "rawSliceDto=false productionCapture=true exportPinsExact=true oneCas=true " +
    "workerCaptureRollbackZero=true");
return 0;

static void MissingPlanFailsBeforeOwners()
{
    RuntimeCallabilityCases.MissingAndLegacyPlanFailBeforeHiddenOwners();
    var source = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualMongoClaimedCaptureMaterialOwner.cs");
    Ordered(source,
        "StatisticReconciliationActualClaimedRunPreOwnerGuard.Require(",
        "LoadP9Async(run, cancellationToken)");
    var guard = Read("Services", "StatisticsReconciliation",
        "ActualObservation",
        "StatisticReconciliationActualClaimedRunPreOwnerGuard.cs");
    Ordered(guard,
        "requireReadIntegrity(run);",
        "STALE_WORKER_FENCE",
        "run.ActualCapturePlan ?? throw Fail(\"CAPTURE_PLAN_REQUIRED\")",
        "requirePlanIntegrity(plan, run.ActualCapturePlanSha256);");
}

static void ScopeAuthorizationPrecedesHiddenIds()
{
    var create = Read("Services", "StatisticsReconciliation",
        "StatisticReconciliationRunService.cs");
    Ordered(create,
        "AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct)",
        "DeserializeStrict<StatisticReconciliationCreateRequest>(body)",
        "LoadP9PublicationAsync(normalized, scope, actor, ct)",
        "ResolveActualCapturePlanAsync(");

    var endpoint = Read("Controllers",
        "StatisticReconciliationActualCaptureOperationsController.cs");
    Ordered(endpoint,
        "var actor = me.RequireMe();",
        "RoleGuard.RequireSystemAdmin(actor);",
        "services.GetRequiredService<",
        "worker.CaptureAsync(");
}

static void StaleFenceReadsZeroHiddenOwners()
{
    RuntimeCallabilityCases.WrongAndExpiredFenceReadZeroHiddenOwners();
    var owner = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualMongoClaimedCaptureMaterialOwner.cs");
    Ordered(owner,
        "StatisticReconciliationActualClaimedRunPreOwnerGuard.Require(",
        "LoadP9Async(run, cancellationToken)");

    var worker = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualTrustedCaptureWorker.cs");
    Ordered(worker,
        "ValidateClaimedMaterial(command, material);",
        ".CaptureAsync(material.Source, sourceOwner, cancellationToken)");
    Contains(worker, "throw Fail(\"STALE_WORKER_FENCE\")");
}

static void RegistryIsFixedAndDtosHaveNoRawSlices()
{
    RuntimeCallabilityCases.DeterministicBoundary();
    var requestNames = typeof(StatisticReconciliationActualCapturePlanRequest)
        .GetProperties()
        .Select(property => property.Name)
        .ToArray();
    var claimedNames = typeof(StatisticReconciliationActualClaimedCaptureHttpRequest)
        .GetProperties()
        .Select(property => property.Name)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    Require(claimedNames.SequenceEqual(
        new[] { "ClaimToken", "WorkerId" }, StringComparer.Ordinal),
        "CLAIMED_HTTP_DTO_MUST_ONLY_CARRY_FENCE");
    foreach (var forbidden in new[]
             {
                 "Collection", "Projection", "Bson", "Slice", "FilterJson",
                 "Sha256", "Hash"
             })
    {
        Require(requestNames.All(name => !name.Contains(
                    forbidden, StringComparison.OrdinalIgnoreCase)),
            $"RAW_PLAN_DTO_PROPERTY:{forbidden}");
        Require(claimedNames.All(name => !name.Contains(
                    forbidden, StringComparison.OrdinalIgnoreCase)),
            $"RAW_CLAIM_DTO_PROPERTY:{forbidden}");
    }

    var registry = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualBoundaryRegistry.cs");
    Contains(registry,
        "internal const string Version = \"P10_ACTUAL_BOUNDARY_REGISTRY_V1\"");
    Contains(registry, ".OrderBy(owner => owner.OwnerOrdinal)");
    Contains(registry, "ProjectionChunks<");
    Contains(registry, "var immutableSlices = slices.ToImmutable();");
    Require(!registry.Contains(
            "var immutableSlices = slices.MoveToImmutable();",
            StringComparison.Ordinal),
        "BOUNDARY_DYNAMIC_BUILDER_MUST_NOT_MOVE_WITH_SLACK_CAPACITY");
    Contains(registry, "immutableSlices.Length > 64");
    Contains(registry, "throw Fail(\"SLICE_LIMIT_EXCEEDED\")");
}

static void ProductionRouteCallsCaptureService()
{
    WorkerCaptureActivationRuntimeCase
        .CurrentV16RollbackStopsBeforeOwnerReadAppendAndCas();
    RuntimeCallabilityCases.EndpointAuthorizationAndInvocationAsync()
        .GetAwaiter().GetResult();
    var controller = typeof(StatisticReconciliationActualCaptureOperationsController);
    Require(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Length == 1,
        "CONTROLLER_AUTHORIZE_REQUIRED");
    var explorer = controller
        .GetCustomAttributes(typeof(ApiExplorerSettingsAttribute), true)
        .Cast<ApiExplorerSettingsAttribute>()
        .Single();
    Require(explorer.IgnoreApi, "INTERNAL_ROUTE_MUST_BE_HIDDEN_FROM_API_EXPLORER");
    var route = controller
        .GetCustomAttributes(typeof(RouteAttribute), true)
        .Cast<RouteAttribute>()
        .Single();
    Require(route.Template == "api/admin/internal/statistics-reconciliation",
        "INTERNAL_ROUTE_PREFIX");
    var action = controller.GetMethod("CaptureClaimedAsync")
                 ?? throw new InvalidOperationException("CAPTURE_ACTION_MISSING");
    var post = action
        .GetCustomAttributes(typeof(HttpPostAttribute), true)
        .Cast<HttpPostAttribute>()
        .Single();
    Require(post.Template == "{reconciliationId}/actual-capture/claimed",
        "CLAIMED_CAPTURE_ROUTE");
    Require(action.GetParameters().All(parameter =>
            parameter.GetCustomAttributes(typeof(FromBodyAttribute), true).Length == 0 &&
            parameter.ParameterType !=
            typeof(StatisticReconciliationActualClaimedCaptureHttpRequest)),
        "CLAIMED_CAPTURE_NO_FRAMEWORK_BODY_BINDING");

    var endpoint = Read("Controllers",
        "StatisticReconciliationActualCaptureOperationsController.cs");
    Ordered(endpoint,
        "RoleGuard.RequireSystemAdmin(actor);",
        "StatisticReconciliationBoundedJsonBody.ReadAsync(",
        "DeserializeStrict<",
        "StatisticReconciliationActualWorkerCaptureActivationGate",
        "services.GetRequiredService<",
        "worker.CaptureAsync(");
    var worker = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualTrustedCaptureWorker.cs");
    Ordered(worker,
        "requestFactory.CreateAsync(",
        "captureService.CaptureAsync(request, cancellationToken)");
    var di = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualCaptureServiceCollectionExtensions.cs");
    Contains(di, "StatisticReconciliationActualTrustedCaptureRequestFactory");
    Contains(di, "StatisticReconciliationActualClaimedCaptureWorker");
}

static void ExportUsesExactP9OwnerPins()
{
    RuntimeCallabilityCases.ConfigurationLayerDrift();
    var owner = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualMongoClaimedCaptureMaterialOwner.cs");
    Contains(owner, "Sha(exportArtifact.SourceHash, \"EXPORT_SOURCE_OWNER\")");
    Contains(owner, "Sha(exportArtifact.ConfigHash, \"EXPORT_CONFIG_OWNER\")");
    Contains(owner, "Same(row.SourceHash, job.SourcePayloadHash)");
    Contains(owner, "Same(row.ConfigHash, job.ConfigHash)");

    var capture = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualCaptureService.cs");
    var comparison = Between(capture,
        "private static bool ExportMatchesBoundary(",
        "private static void ValidateRequest(");
    Contains(comparison, "request.Export.ExpectedSourceOwnerSha256");
    Contains(comparison, "request.Export.ExpectedConfigOwnerSha256");
    Require(!comparison.Contains("SourceSetSha256", StringComparison.Ordinal) &&
            !comparison.Contains("ConfigurationBundleSha256", StringComparison.Ordinal),
        "EXPORT_MUST_NOT_CROSS_COMPARE_P10_BOUNDARY_DOMAINS");
}

static void ReadyUsesOneCasAndStaleUsesZero()
{
    RuntimeCallabilityCases.PinnedAndFileDriftNeverPublishAsync()
        .GetAwaiter().GetResult();
    RuntimeCallabilityCases.ReadyPublishesExactlyOnceAsync()
        .GetAwaiter().GetResult();
    var integration = Read("tests", "tdtd-be.P10T26Integration", "Program.cs");
    Contains(integration, "Require(cas.PublishCalls == 1, \"ONE_CAS_PUBLICATION\")");
    Contains(integration, "cas.PublishCalls == 0,");
    Contains(integration, "\"DRIFT_ZERO_WRITE_ZERO_CAS\"");

    var coherent = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualCoherentCapture.cs");
    Ordered(coherent,
        "var finalBoundary = NormalizeBoundary(",
        "if (finalGuardAsync is not null)",
        "var readyForPersistence = state ==");

    var capture = Read("Services", "StatisticsReconciliation", "ActualObservation",
        "StatisticReconciliationActualCaptureService.cs");
    Ordered(capture,
        "async Task<string?> FinalOwnerGuardAsync(",
        "var finalSource = await _source.CaptureAsync(",
        "SOURCE_OWNER_DRIFT",
        "var finalRead = await exportOwner.ReadArtifactAsync(",
        "request.PinnedInitialBoundary,",
        "FinalOwnerGuardAsync,",
        "PreparePublicationAsync)");

    var publication = Read("Services", "StatisticsReconciliation",
        "ActualObservation", "StatisticReconciliationActualPublication.cs");
    Ordered(publication,
        "if (!coherent.ReadyForAppendOnlyPersistence ||",
        "return PublishCoreAsync(",
        "await generationCas.PublishAsync(");
}

static string Read(params string[] relative)
    => File.ReadAllText(Path.Combine(
        new[] { FindRepositoryRoot(), "tdtd-be" }.Concat(relative).ToArray()));

static string Between(string source, string startToken, string endToken)
{
    var start = source.IndexOf(startToken, StringComparison.Ordinal);
    var end = source.IndexOf(endToken, start + startToken.Length,
        StringComparison.Ordinal);
    Require(start >= 0 && end > start, $"SOURCE_RANGE:{startToken}");
    return source[start..end];
}

static void Ordered(string source, params string[] tokens)
{
    var cursor = -1;
    foreach (var token in tokens)
    {
        var next = source.IndexOf(token, cursor + 1, StringComparison.Ordinal);
        Require(next > cursor, $"ORDER_TOKEN:{token}");
        cursor = next;
    }
}

static void Contains(string source, string token)
    => Require(source.Contains(token, StringComparison.Ordinal),
        $"SOURCE_TOKEN:{token}");

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "tdtd-be")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("REPOSITORY_ROOT_NOT_FOUND");
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}
