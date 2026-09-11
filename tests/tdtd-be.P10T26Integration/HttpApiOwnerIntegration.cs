using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class HttpApiOwnerIntegration
{
    internal static async Task RunAsync()
    {
        var events = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet(
            "/api/internal/statistic-reconciliation/actual/authorize",
            (HttpContext context) =>
            {
                RequireServiceAuthorization(context);
                events.Enqueue("AUTH");
                var workId = context.Request.Query["workId"].ToString();
                var scopeId = context.Request.Query["scopeId"].ToString();
                var permissions = new[] { "STATISTICS_READ" };
                var sha = StatisticReconciliationActualApiObservationAdapter
                    .AuthorizationSha(
                        "service-actor",
                        workId,
                        scopeId,
                        permissions,
                        2,
                        2);
                return Results.Json(new AuthorizationWire(
                    "service-actor",
                    workId,
                    scopeId,
                    permissions,
                    2,
                    2,
                    sha));
            });
        app.MapPost(
            "/api/internal/statistic-reconciliation/actual/page",
            async (HttpContext context) =>
            {
                RequireServiceAuthorization(context);
                if (!events.TryPeek(out var first) || first != "AUTH")
                    throw new InvalidOperationException("PAGE_BEFORE_AUTH");
                events.Enqueue("PAGE");
                var query = await context.Request.ReadFromJsonAsync<PageQueryWire>()
                            ?? throw new InvalidOperationException("PAGE_QUERY_REQUIRED");
                var all = new[]
                {
                    new SourceRow("row-001", 10),
                    new SourceRow("row-002", 20)
                };
                var selected = all
                    .Skip(query.Page * query.PageSize)
                    .Take(query.PageSize)
                    .Select(row =>
                    {
                        var json = $"{{\"id\":\"{row.Id}\",\"value\":{row.Value}}}";
                        return new RowWire(
                            row.Id,
                            json,
                            StatisticReconciliationActualApiObservationAdapter
                                .RowSha(row.Id, json));
                    })
                    .ToArray();
                return Results.Json(new PageResponseWire(
                    query.Surface,
                    query.RouteId,
                    query.WorkId,
                    query.ScopeAssignmentId,
                    query.DynamicFormTemplateId,
                    query.OwnerResultId,
                    query.FilterSha256,
                    query.AuthorizationSnapshotSha256,
                    query.RequestSha256,
                    "\"sha256-generation\"",
                    Hash("generation-id"),
                    Hash("generation-sha"),
                    query.Page,
                    query.PageSize,
                    (int)Math.Ceiling(all.Length / (double)query.PageSize),
                    selected.Length,
                    new[] { new TotalWire("totalRows", "INTEGER", "2") },
                    selected));
            });

        await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?
                              .Addresses.Single()
                          ?? throw new InvalidOperationException(
                              "KESTREL_ADDRESS_MISSING");
            using var client = new HttpClient();
            var options = Options.Create(
                new StatisticReconciliationActualHttpApiOwnerOptions
                {
                    BaseUri = address.TrimEnd('/') + "/",
                    ServiceAuthorizationScheme = "Bearer",
                    ServiceAuthorizationParameter = "p10-integration-secret"
                });
            var owner = new StatisticReconciliationActualHttpApiOwnerReader(
                client,
                options);
            var adapter = new StatisticReconciliationActualApiObservationAdapter(
                owner);
            var capture = await adapter.CaptureAsync(
                new StatisticReconciliationActualApiCaptureRequest(
                    StatisticReconciliationActualApiSurfaces.DirectField,
                    "work-01",
                    "scope-01",
                    "template-01",
                    null,
                    "{}",
                    2,
                    [new(0, 1), new(0, 50)]));
            if (capture.CaptureState !=
                    StatisticReconciliationActualApiCaptureStates.Ready ||
                !capture.FullFilterTotalsStable || !capture.GenerationStable ||
                !capture.OverlapStable || !capture.PagingContractsValid)
                throw new InvalidOperationException("HTTP_OWNER_CAPTURE_NOT_READY");
            var order = events.ToArray();
            if (!order.SequenceEqual(["AUTH", "PAGE", "PAGE"],
                    StringComparer.Ordinal))
                throw new InvalidOperationException("HTTP_AUTH_PAGE_ORDER_INVALID");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static void RequireServiceAuthorization(HttpContext context)
    {
        if (context.Request.Headers.Authorization !=
            "Bearer p10-integration-secret")
            throw new InvalidOperationException("SERVICE_AUTHORIZATION_MISSING");
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record SourceRow(string Id, int Value);

    private sealed record AuthorizationWire(
        string ActorUserId,
        string WorkId,
        string ScopeAssignmentId,
        string[] PermissionCodes,
        long RowCountBeforeRedaction,
        long RowCountAfterRedaction,
        string AuthorizationSnapshotSha256);

    private sealed record PageQueryWire(
        string Surface,
        string RouteId,
        string WorkId,
        string ScopeAssignmentId,
        string DynamicFormTemplateId,
        string? OwnerResultId,
        string CanonicalFilterJson,
        string FilterSha256,
        string AuthorizationSnapshotSha256,
        int Page,
        int PageSize,
        string RequestSha256);

    private sealed record TotalWire(
        string Name,
        string ValueType,
        string CanonicalValue);

    private sealed record RowWire(
        string Identity,
        string CanonicalRowJson,
        string RowSemanticSha256);

    private sealed record PageResponseWire(
        string Surface,
        string RouteId,
        string WorkId,
        string ScopeAssignmentId,
        string DynamicFormTemplateId,
        string? OwnerResultId,
        string FilterSha256,
        string AuthorizationSnapshotSha256,
        string RequestSha256,
        string ETag,
        string GenerationId,
        string GenerationSha256,
        int Page,
        int PageSize,
        int TotalPages,
        int ReturnedRows,
        TotalWire[] FullFilterTotals,
        RowWire[] Rows);
}
