using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Middleware;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class RuntimeCallabilityCases
{
    internal static void MissingAndLegacyPlanFailBeforeHiddenOwners()
    {
        var now = Utc(10);
        foreach (var planSha in new string?[] { null, Hash("orphan-plan-sha") })
        {
            var run = ClaimedRun(now);
            run.ActualCapturePlan = null;
            run.ActualCapturePlanSha256 = planSha;
            var readIntegrity = 0;
            var planIntegrity = 0;
            var hiddenOwnerReads = 0;
            var reason = CaptureReason(() =>
            {
                _ = StatisticReconciliationActualClaimedRunPreOwnerGuard.Require(
                    run,
                    "worker-01",
                    "claim-01",
                    now,
                    _ => readIntegrity++,
                    (_, _) => planIntegrity++);
                hiddenOwnerReads++;
            });
            Require(reason == "ACTUAL_TRUSTED_MATERIAL_CAPTURE_PLAN_REQUIRED",
                "MISSING_PLAN_REASON");
            Require(readIntegrity == 1 && planIntegrity == 0 &&
                    hiddenOwnerReads == 0,
                "MISSING_PLAN_HIDDEN_OWNER_COUNTERS");
        }
    }

    internal static void WrongAndExpiredFenceReadZeroHiddenOwners()
    {
        var now = Utc(11);
        var wrongClaim = ClaimedRun(now);
        wrongClaim.ClaimToken = "wrong-claim";
        var expiredLease = ClaimedRun(now);
        expiredLease.LeaseUntilUtc = now.AddSeconds(-1);
        var runs = new[] { wrongClaim, expiredLease };
        foreach (var run in runs)
        {
            run.ActualCapturePlan = new StatisticReconciliationActualCapturePlan();
            var readIntegrity = 0;
            var planIntegrity = 0;
            var hiddenOwnerReads = 0;
            var reason = CaptureReason(() =>
            {
                _ = StatisticReconciliationActualClaimedRunPreOwnerGuard.Require(
                    run,
                    "worker-01",
                    "claim-01",
                    now,
                    _ => readIntegrity++,
                    (_, _) => planIntegrity++);
                hiddenOwnerReads++;
            });
            Require(reason == "ACTUAL_TRUSTED_MATERIAL_STALE_WORKER_FENCE",
                "STALE_FENCE_REASON");
            Require(readIntegrity == 1 && planIntegrity == 0 &&
                    hiddenOwnerReads == 0,
                "STALE_FENCE_HIDDEN_OWNER_COUNTERS");
        }
    }

    internal static void ConfigurationLayerDrift()
    {
        var direct = new WorkReportStatisticRebuildJob
        {
            DynamicFormTemplateId = Id(), ConfigId = Id(),
            ConfigVersionId = Id(), ConfigVersionNo = 1, ConfigRevision = 1,
            ConfigHash = Hash("direct-config")
        };
        var basic = new WorkAssignmentBasicSummarySnapshot
        {
            ConfigId = Id(), ConfigVersionId = Id(), ConfigVersionNo = 2,
            ConfigRevision = 2, ConfigHash = Hash("basic-config"),
            ConfigDependencyPins = ["basic-pin"]
        };
        var days = new List<WorkAssignmentAdvancedSummaryDayNode>
        {
            new()
            {
                Id = Id(), ConfigId = Id(), ConfigVersionId = Id(),
                ConfigVersionNo = 3, ConfigRevision = 3,
                ConfigHash = Hash("advanced-config"),
                DependencyPins = ["advanced-pin"]
            }
        };
        IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months = [];
        IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years = [];
        var diff = new WorkReportStatisticDiffResult
        {
            ConfigId = Id(), ConfigVersionId = Id(), ConfigVersionNo = 4,
            ConfigRevision = 4, ConfigHash = Hash("diff-config"),
            DependencyPins = ["diff-pin"]
        };
        string Build() =>
            StatisticReconciliationActualCapturePlanIntegrity
                .ConfigurationBundleSha(
                    Hash("direct-p8-bundle"), direct, basic, days, months,
                    years, diff);
        var baseline = Build();
        direct.ConfigRevision++;
        var directDrift = Build();
        direct.ConfigRevision--;
        basic.ConfigRevision++;
        var basicDrift = Build();
        basic.ConfigRevision--;
        days[0].ConfigRevision++;
        var advancedDrift = Build();
        days[0].ConfigRevision--;
        diff.ConfigRevision++;
        var diffDrift = Build();
        Require(new[] { directDrift, basicDrift, advancedDrift, diffDrift }
                .All(value => value != baseline),
            "EVERY_ACTUAL_CONFIG_LAYER_MUST_CHANGE_COMPOSITE");
    }
    internal static async Task EndpointAuthorizationAndInvocationAsync()
    {
        const string token = "p10-callability-token";
        var fake = new RuntimeClaimedCaptureWorker();
        var activation = new WorkerCaptureEnabledTestActivation();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));
        builder.Services
            .AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, RuntimeBearerHandler>(
                "Bearer",
                options => options.ClaimsIssuer = token);
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<ApiExceptionMiddleware>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<MeAccessor>();
        builder.Services.AddSingleton<
            IStatisticReconciliationActualClaimedCaptureWorker>(fake);
        builder.Services.AddSingleton<
            IStatisticReconciliationCandidateActivation>(activation);
        builder.Services.AddControllers().AddApplicationPart(
            typeof(StatisticReconciliationActualCaptureOperationsController)
                .Assembly);
        var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
                context.Items[MeAccessor.MeItemKey] =
                    context.Request.Headers.Authorization ==
                    "Bearer p10-nonadmin-token"
                        ? NonAdminActor()
                        : Actor();
            await next();
        });
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                              .Features.Get<IServerAddressesFeature>()?
                              .Addresses.Single()
                          ?? throw new InvalidOperationException(
                              "CALL_ENDPOINT_ADDRESS_MISSING");
            using var client = new HttpClient();
            var uri = address.TrimEnd('/') +
                      "/api/admin/internal/statistics-reconciliation/" +
                      "run-route-01/actual-capture/claimed";
            using (var hidden = await client.PostAsync(
                       uri,
                       new StringContent("{malformed", Encoding.UTF8,
                           "application/json")))
            {
                Require(hidden.StatusCode == HttpStatusCode.Unauthorized,
                    "UNAUTHORIZED_BODY_MUST_BE_HIDDEN");
            }
            Require(fake.Calls == 0, "UNAUTHORIZED_ZERO_WORKER_CALL");

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", "p10-nonadmin-token");
            using (var forbidden = await client.PostAsync(
                       uri,
                       new StringContent("{malformed", Encoding.UTF8,
                           "application/json")))
            {
                Require(forbidden.StatusCode == HttpStatusCode.Forbidden,
                    "NON_ADMIN_FORBIDDEN");
            }
            Require(fake.Calls == 0, "NON_ADMIN_ZERO_WORKER_CALL");

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            using var accepted = await client.PostAsJsonAsync(
                uri,
                new StatisticReconciliationActualClaimedCaptureHttpRequest(
                    "worker-route-01",
                    "claim-route-01"));
            Require(accepted.StatusCode == HttpStatusCode.OK,
                "AUTHORIZED_CAPTURE_ROUTE_OK");
            Require(fake.Calls == 1 &&
                    fake.ReconciliationId == "run-route-01" &&
                    fake.WorkerId == "worker-route-01" &&
                    fake.ClaimToken == "claim-route-01" &&
                    fake.ActorId == Actor().Id &&
                    activation.RequireFoundationCalls == 1,
                "AUTHORIZED_CAPTURE_EXACT_INVOCATION");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    internal static void DeterministicBoundary()
    {
        var ordered = Pins().ToImmutableArray();
        var reversed = ordered.Reverse().ToImmutableArray();
        var first = CreateBoundary(ordered);
        var second = CreateBoundary(reversed);
        Require(first.BoundarySemanticSha256 ==
                second.BoundarySemanticSha256 &&
                first.Pins.SequenceEqual(second.Pins),
            "BOUNDARY_MUST_BE_DETERMINISTIC_FOR_OWNER_PIN_ORDER");
        Require(first.Pins.Length == 6,
            "BOUNDARY_EXACT_DOMAIN_SET");
    }

    internal static async Task PinnedAndFileDriftNeverPublishAsync()
    {
        var before = Boundary();
        var after = Boundary(18, Hash("result-drift"));
        var backend = new RuntimeMemoryBackend();
        var cas = new RuntimeCountingCas();
        var gate = Gate(backend, cas);
        var pinnedDrift = await gate.ExecuteAsync(
            before.ReconciliationId,
            new RuntimeBoundaryReader(after),
            Steps(before),
            Context(before),
            Utc(12),
            "worker-01",
            "claim-01",
            Actor(),
            default,
            before);
        Require(!pinnedDrift.Published &&
                pinnedDrift.Generation.CaptureState ==
                StatisticReconciliationActualCoherentCaptureStates.Stale &&
                backend.TotalWrites == 0 && cas.PublishCalls == 0,
            "PINNED_OWNER_DRIFT_ZERO_APPEND_ZERO_CAS");

        await FileDriftAsync(delete: false);
        await FileDriftAsync(delete: true);
    }

    internal static async Task ReadyPublishesExactlyOnceAsync()
    {
        var boundary = Boundary();
        var backend = new RuntimeMemoryBackend();
        var cas = new RuntimeCountingCas();
        var outcome = await Gate(backend, cas).ExecuteAsync(
            boundary.ReconciliationId,
            new RuntimeBoundaryReader(boundary),
            Steps(boundary),
            Context(boundary),
            Utc(13),
            "worker-ready",
            "claim-ready",
            Actor(),
            default,
            boundary,
            null,
            PublicationV7Fixture.PrepareAsync);
        Require(outcome.Published && backend.ContentWrites == 1 &&
                backend.CommitWrites == 1 && cas.PublishCalls == 1,
            "READY_EXACT_APPEND_AND_ONE_CAS");
    }

    private static async Task FileDriftAsync(bool delete)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "p10-callability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "artifact.csv");
        var expected = Encoding.UTF8.GetBytes("owner,row\n1,ready\n");
        await File.WriteAllBytesAsync(path, expected);
        try
        {
            var boundary = Boundary();
            var backend = new RuntimeMemoryBackend();
            var cas = new RuntimeCountingCas();
            var steps = Steps(boundary, () =>
            {
                if (delete)
                    File.Delete(path);
                else
                    File.WriteAllText(path, "owner,row\n1,mutated\n");
            });
            async Task<string?> Guard(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                    return "EXPORT_ARTIFACT_DRIFT";
                var actual = await File.ReadAllBytesAsync(path,
                    cancellationToken);
                return actual.SequenceEqual(expected)
                    ? null
                    : "EXPORT_ARTIFACT_DRIFT";
            }
            var outcome = await Gate(backend, cas).ExecuteAsync(
                boundary.ReconciliationId,
                new RuntimeBoundaryReader(boundary),
                steps,
                Context(boundary),
                Utc(delete ? 15 : 14),
                "worker-file",
                "claim-file",
                Actor(),
                default,
                boundary,
                Guard);
            Require(!outcome.Published &&
                    outcome.Generation.CaptureState ==
                    StatisticReconciliationActualCoherentCaptureStates.Stale &&
                    outcome.Generation.StaleReason ==
                    "FINAL_GUARD:EXPORT_ARTIFACT_DRIFT" &&
                    backend.TotalWrites == 0 && cas.PublishCalls == 0,
                delete
                    ? "EXPORT_DELETE_ZERO_APPEND_ZERO_CAS"
                    : "EXPORT_MUTATION_ZERO_APPEND_ZERO_CAS");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static StatisticReconciliationRun ClaimedRun(DateTime now)
        => new()
        {
            Status = StatisticReconciliationRunStatuses.Running,
            LeaseOwnerId = "worker-01",
            ClaimToken = "claim-01",
            LeaseUntilUtc = now.AddMinutes(5),
            DeadlineAtUtc = now.AddMinutes(10)
        };

    private static string? CaptureReason(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (StatisticReconciliationActualObservationException error)
        {
            return error.Reason;
        }
    }

    private static StatisticReconciliationActualCapturePublicationGate Gate(
        RuntimeMemoryBackend backend,
        RuntimeCountingCas cas)
        => new(new StatisticReconciliationActualGenerationPublisher(
            backend,
            cas));

    private static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep>
        Steps(
            StatisticReconciliationActualCoherentBoundary boundary,
            Action? afterExport = null)
        => StatisticReconciliationActualCoherentLayers.RequiredOrder
            .Select((layer, ordinal) =>
            {
                var ownerId = $"owner-{ordinal}";
                var ownerVersion = Hash($"owner-version-{ordinal}");
                var typed = layer ==
                            StatisticReconciliationActualCoherentLayers
                                .DirectProjection
                    ? ImmutableArray.Create(
                        StatisticReconciliationActualTypedObservationCanonical
                            .Create(
                                0,
                                layer,
                                ownerId,
                                ownerVersion,
                                "DIRECT",
                                "FIELD",
                                "metric-01",
                                "2026-08",
                                "COUNT",
                                "NUMBER",
                                "1",
                                occurrenceCount: 1,
                                reportCount: 1,
                                fieldId: "field-01"))
                    : ImmutableArray<
                        StatisticReconciliationActualTypedObservation>.Empty;
                return new StatisticReconciliationActualCoherentCaptureStep(
                    layer,
                    (_, cancellationToken) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = new StatisticReconciliationActualLayerCapture(
                            layer,
                            boundary.BoundarySemanticSha256,
                            Hash($"capture-{ordinal}"),
                            1,
                            StatisticReconciliationActualCoherentCaptureStates
                                .Ready,
                            null,
                            ownerId,
                            ownerVersion,
                            typed);
                        if (layer ==
                            StatisticReconciliationActualCoherentLayers.Export)
                            afterExport?.Invoke();
                        return Task.FromResult(result);
                    });
            })
            .ToImmutableArray();

    private static StatisticReconciliationActualCoherentBoundary Boundary(
        long resultRevision = 17,
        string? resultSemanticSha256 = null)
    {
        var pins = Pins(resultRevision, resultSemanticSha256)
            .ToImmutableArray();
        return CreateBoundary(pins);
    }

    private static IEnumerable<StatisticReconciliationActualBoundaryPin> Pins(
        long resultRevision = 17,
        string? resultSemanticSha256 = null)
    {
        yield return Pin(StatisticReconciliationActualBoundaryDomains.Source,
            "source-owner", 4, Hash("source"));
        yield return Pin(
            StatisticReconciliationActualBoundaryDomains.Configuration,
            "configuration-owner", 5, Hash("configuration"));
        yield return Pin(StatisticReconciliationActualBoundaryDomains.Catalog,
            "catalog-owner", 6, Hash("catalog"));
        yield return Pin(StatisticReconciliationActualBoundaryDomains.Runtime,
            "runtime-owner", 7, Hash("runtime"));
        yield return Pin(StatisticReconciliationActualBoundaryDomains.Result,
            "result-owner", resultRevision,
            resultSemanticSha256 ?? Hash("result"));
        yield return Pin(StatisticReconciliationActualBoundaryDomains.Export,
            "export-owner", 8, Hash("export"));
    }

    private static StatisticReconciliationActualCoherentBoundary CreateBoundary(
        ImmutableArray<StatisticReconciliationActualBoundaryPin> pins)
        => StatisticReconciliationActualCoherentCaptureCoordinator.CreateBoundary(
            "reconciliation-01",
            "work-01",
            "scope-01",
            Hash("source-set"),
            Hash("configuration-bundle"),
            Hash("filter"),
            Hash("authorization"),
            pins);

    private static StatisticReconciliationActualBoundaryPin Pin(
        string domain,
        string owner,
        long revision,
        string semanticSha256)
        => new(domain, owner, "p9-generation-17", revision, semanticSha256);

    private static StatisticReconciliationActualPublicationContext Context(
        StatisticReconciliationActualCoherentBoundary boundary)
    {
        var pins = CatalogPins();
        return new StatisticReconciliationActualPublicationContext(
            boundary.ReconciliationId,
            Hash("immutable-identity"),
            Hash("immutable-header"),
            boundary.WorkId,
            boundary.ScopeAssignmentId,
            "month:2026-08",
            "2026-08",
            "concept-01",
            "MONTH",
            "PERIOD_END",
            boundary.FilterSha256,
            "form-version-01",
            Hash("form-schema"),
            "flow-template-01",
            Hash("flow-payload"),
            "flow-instance-01",
            "epoch-01",
            3,
            9,
            "p8-owner-01",
            Hash("p8-config-bundle"),
            boundary.ConfigurationBundleSha256,
            boundary.CatalogPinSetSha256,
            pins);
    }

    private static StatisticReconciliationActualPublicationCatalogPins
        CatalogPins()
    {
        var values = new[]
        {
            "v1.6", Hash("p9-catalog-raw"), Hash("p9-catalog-semantic"),
            Hash("p9-schema-raw"), Hash("p9-schema-semantic"),
            Hash("p9-stage-lock"), "p10-chain-01", "P10-03",
            "v1.7-candidate", Hash("candidate-catalog-raw"),
            Hash("candidate-catalog-semantic"), Hash("candidate-schema-raw"),
            Hash("candidate-schema-semantic"), Hash("candidate-stage-lock")
        };
        var pinSet = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CATALOG_PIN_SET_V1",
            values);
        return new StatisticReconciliationActualPublicationCatalogPins(
            values[0], values[1], values[2], values[3], values[4], values[5],
            values[6], values[7], values[8], values[9], values[10], values[11],
            values[12], values[13], pinSet);
    }

    internal static MeResponse Actor()
        => new(
            "000000000000000000000001",
            "p10-worker",
            "P10 Worker",
            ["SYSTEM"],
            "000000000000000000000002",
            "SYS",
            "System",
            "SYS",
            ["SYSTEM_ADMIN"],
            "ADMIN",
            false,
            "SYSTEM_ADMIN");

    private static MeResponse NonAdminActor()
    {
        var actor = Actor();
        actor.Roles = [];
        actor.AccountKind = "USER";
        return actor;
    }

    private static StatisticReconciliationActualCaptureOutcome RouteOutcome()
    {
        var boundary = Boundary();
        var generation = new StatisticReconciliationActualCoherentGeneration(
            "run-route-01",
            "generation-route-01",
            Hash("route-generation"),
            boundary,
            boundary,
            ImmutableArray<
                StatisticReconciliationActualCoherentLayerObservation>.Empty,
            Hash("route-manifest"),
            StatisticReconciliationActualCoherentCaptureStates.Ready,
            null,
            true,
            true,
            false,
            true,
            true);
        return new StatisticReconciliationActualCaptureOutcome(generation, null);
    }

    private static DateTime Utc(int hour)
        => new(2026, 8, 11, hour, 0, 0, DateTimeKind.Utc);

    private static string Id()
        => MongoDB.Bson.ObjectId.GenerateNewId().ToString();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    private sealed class RuntimeClaimedCaptureWorker
        : IStatisticReconciliationActualClaimedCaptureWorker
    {
        internal int Calls { get; private set; }
        internal string? ReconciliationId { get; private set; }
        internal string? WorkerId { get; private set; }
        internal string? ClaimToken { get; private set; }
        internal string? ActorId { get; private set; }

        public Task<StatisticReconciliationActualCaptureOutcome> CaptureAsync(
            string reconciliationId,
            string workerId,
            string claimToken,
            MeResponse workerActor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            ReconciliationId = reconciliationId;
            WorkerId = workerId;
            ClaimToken = claimToken;
            ActorId = workerActor.Id;
            return Task.FromResult(RouteOutcome());
        }
    }
}

internal sealed class RuntimeBoundaryReader(
    StatisticReconciliationActualCoherentBoundary boundary)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(boundary);
    }
}

internal sealed class RuntimeMemoryBackend
    : IStatisticReconciliationActualObservationBackend
{
    private readonly List<StatisticReconciliationObservation> _documents = [];
    internal int ContentWrites { get; private set; }
    internal int CommitWrites { get; private set; }
    internal int TotalWrites => ContentWrites + CommitWrites;

    public Task<IReadOnlyList<StatisticReconciliationObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<StatisticReconciliationObservation>>(
            _documents.Where(item =>
                    item.ReconciliationId == reconciliationId &&
                    item.GenerationId == generationId)
                .ToArray());
    }

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentWrites++;
        Append(observations);
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitWrites++;
        Append([observation]);
        return Task.CompletedTask;
    }

    private void Append(IEnumerable<StatisticReconciliationObservation> values)
    {
        foreach (var value in values)
        {
            if (_documents.All(item => item.Id != value.Id))
                _documents.Add(value);
        }
    }
}

internal sealed class RuntimeCountingCas
    : IStatisticReconciliationActualGenerationCas
{
    internal int PublishCalls { get; private set; }

    public Task PublishAsync(
        string reconciliationId,
        string generationId,
        string generationSemanticSha256,
        string workerId,
        string claimToken,
        MeResponse actor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class RuntimeBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (authorization != $"Bearer {Options.ClaimsIssuer}" &&
            authorization != "Bearer p10-nonadmin-token")
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer."));
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "runtime-system-admin")],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                Scheme.Name)));
    }
}
