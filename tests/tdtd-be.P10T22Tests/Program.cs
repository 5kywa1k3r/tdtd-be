using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Func<TestFixture, Task> Run)[]
{
    ("P10-API-01", AuthBeforeExistenceAsync),
    ("P10-API-02", DirectFullFilterTotalsAsync),
    ("P10-API-03", BasicAndDiffPinsAsync),
    ("P10-API-04", DriftAndZeroWriteAsync)
};

await using var fixture = await TestFixture.StartAsync();
var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run(fixture);
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {item.Id} {ex.GetType().Name}:{ex.Message}");
        return 1;
    }
}

Require(passed == 4, "EXACT_CASE_COUNT");
Console.WriteLine("P10_T22_ACTUAL_API_OK cases=4 cumulative=28 stopBefore=P10-T23");
return 0;

static async Task AuthBeforeExistenceAsync(TestFixture fixture)
{
    fixture.Reset();
    var outsider = fixture.Adapter("outsider");
    var malformed = new StatisticReconciliationActualApiCaptureRequest(
        "not-a-surface",
        "missing-work",
        "missing-scope",
        null,
        null,
        "not-json",
        -1,
        ImmutableArray.Create(new StatisticReconciliationActualApiPageSelector(-1, 0)));
    await ExpectReasonAsync(
        () => outsider.CaptureAsync(malformed),
        "API_TARGET_HIDDEN");
    Require(fixture.State.AuthorizationCalls == 1, "AUTH_CALLED_ONCE");
    Require(fixture.State.PageCalls == 0, "NO_PAGE_BEFORE_AUTH");

    var hiddenValid = Request(
        StatisticReconciliationActualApiSurfaces.DirectField,
        Pages((0, 1)));
    await ExpectReasonAsync(
        () => outsider.CaptureAsync(hiddenValid),
        "API_TARGET_HIDDEN");
    Require(fixture.State.AuthorizationCalls == 2, "HIDDEN_PARITY_AUTH_COUNT");
    Require(fixture.State.PageCalls == 0, "HIDDEN_PARITY_ZERO_PAGE");
    Require(fixture.State.WriteCount == 0, "AUTH_ZERO_WRITE");
}

static async Task DirectFullFilterTotalsAsync(TestFixture fixture)
{
    fixture.Reset();
    var adapter = fixture.Adapter("manager");
    var field = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.DirectField,
        Pages((0, 1), (0, 50), (1, 50))));
    AssertReady(field, 57);
    Require(field.Pages.Select(page => page.TotalRows).Distinct().Single() == 57,
        "FIELD_TOTAL_ROWS_STABLE");
    Require(field.Pages[0].ReturnedRows == 1, "FIELD_PAGE_SIZE_1");
    Require(field.Pages[1].ReturnedRows == 50, "FIELD_PAGE_SIZE_50");
    Require(field.Pages[2].ReturnedRows == 7, "FIELD_TAIL_PAGE");
    Require(field.Pages[0].Rows[0].Identity == field.Pages[1].Rows[0].Identity,
        "FIELD_OVERLAP_IDENTITY");
    Require(field.Pages.All(page => page.FullFilterTotals.Any(total =>
            total.Name == "totalValueCount" && total.CanonicalValue == "114")),
        "FIELD_FULL_FILTER_VALUE_TOTAL");

    var table = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.DirectTable,
        Pages((0, 1), (0, 50))));
    AssertReady(table, 57);
    Require(table.RouteId == "POST:/api/work-report-table-statistics/summary",
        "TABLE_ROUTE_PIN");

    var label = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.DirectLabel,
        Pages((0, 1), (0, 50))));
    AssertReady(label, 57);
    Require(label.Pages.All(page => page.FullFilterTotals.Any(total =>
            total.Name == "totalRowCount" && total.CanonicalValue == "171")),
        "LABEL_FULL_FILTER_ROW_TOTAL");
    Require(fixture.State.WriteCount == 0, "DIRECT_ZERO_WRITE");
}

static async Task BasicAndDiffPinsAsync(TestFixture fixture)
{
    fixture.Reset();
    var adapter = fixture.Adapter("manager");
    var basic = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.BasicSource,
        Pages((0, 1), (0, 50)),
        filterJson: "{\"group\":\"SMALL\"}"));
    AssertReady(basic, 7);
    Require(basic.CanonicalFilterJson == "{\"group\":\"SMALL\"}",
        "BASIC_FILTER_CANONICAL");
    Require(basic.Authorization.ActorUserId == "manager", "SERVER_ACTOR_PIN");
    Require(basic.Authorization.PermissionCodes.SequenceEqual(
        new[] { "RECONCILIATION_DETAIL", "STATISTICS_READ" },
        StringComparer.Ordinal), "CURRENT_PERMISSION_SNAPSHOT");
    Require(basic.Authorization.RowCountBeforeRedaction == 57 &&
            basic.Authorization.RowCountAfterRedaction == 57,
        "REDACTION_COUNTS_PINNED");

    var diff = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.P9Diff,
        Pages((0, 1), (0, 50)),
        ownerResultId: "result-01"));
    AssertReady(diff, 57);
    Require(diff.RouteId.StartsWith("GET:/api/work-report-statistic-diffs/", StringComparison.Ordinal),
        "DIFF_ROUTE_PIN");
    Require(diff.OwnerResultId == "result-01", "DIFF_RESULT_PIN");
    Require(diff.Pages[0].TotalPages == 57 && diff.Pages[1].TotalPages == 2,
        "TOTAL_PAGES_PAGE_LOCAL");
    Require(diff.Pages.All(page => page.FullFilterTotals.All(total =>
            total.Name != "totalPages")),
        "TOTAL_PAGES_NOT_FULL_FILTER_TOTAL");
    Require(diff.Pages.All(page => page.GenerationId == Hash('a') &&
                                   page.GenerationSha256 == Hash('b') &&
                                   page.ETag == $"\"sha256-{Hash('b')}\""),
        "DIFF_ETAG_GENERATION_PINS");
    Require(fixture.State.WriteCount == 0, "RESULT_API_ZERO_WRITE");
}

static async Task DriftAndZeroWriteAsync(TestFixture fixture)
{
    fixture.Reset();
    var adapter = fixture.Adapter("manager");
    var request = Request(
        StatisticReconciliationActualApiSurfaces.DirectField,
        Pages((0, 1), (0, 50)));

    fixture.State.DriftGeneration = true;
    var generationDrift = await adapter.CaptureAsync(request);
    Require(generationDrift.CaptureState == StatisticReconciliationActualApiCaptureStates.Stale,
        "GENERATION_DRIFT_STALE");
    Require(generationDrift.CaptureReason == "API_GENERATION_DRIFT",
        "GENERATION_DRIFT_REASON");

    fixture.State.DriftGeneration = false;
    fixture.State.DriftTotals = true;
    var totalDrift = await adapter.CaptureAsync(request);
    Require(totalDrift.CaptureState == StatisticReconciliationActualApiCaptureStates.Invalid,
        "TOTAL_DRIFT_INVALID");
    Require(totalDrift.CaptureReason == "API_FULL_FILTER_TOTAL_DRIFT",
        "TOTAL_DRIFT_REASON");

    fixture.State.DriftTotals = false;
    var expectedTotalDrift = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.DirectField,
        Pages((0, 1), (0, 50)),
        expectedTotalRows: 56));
    Require(expectedTotalDrift.CaptureState ==
            StatisticReconciliationActualApiCaptureStates.Stale,
        "EXPECTED_TOTAL_DRIFT_STALE");
    Require(expectedTotalDrift.CaptureReason == "API_EXPECTED_TOTAL_DRIFT",
        "EXPECTED_TOTAL_DRIFT_REASON");

    fixture.State.DriftOverlap = true;
    var overlapDrift = await adapter.CaptureAsync(request);
    Require(overlapDrift.CaptureState == StatisticReconciliationActualApiCaptureStates.Invalid,
        "OVERLAP_DRIFT_INVALID");
    Require(overlapDrift.CaptureReason == "API_PAGE_OVERLAP_DRIFT",
        "OVERLAP_DRIFT_REASON");

    fixture.State.DriftOverlap = false;
    fixture.State.DuplicateAcrossPageBoundary = true;
    var boundaryDuplicate = await adapter.CaptureAsync(Request(
        StatisticReconciliationActualApiSurfaces.DirectField,
        Pages((0, 50), (1, 50))));
    Require(boundaryDuplicate.CaptureState ==
            StatisticReconciliationActualApiCaptureStates.Invalid,
        "CROSS_PAGE_DUPLICATE_INVALID");
    Require(boundaryDuplicate.CaptureReason == "API_PAGE_OVERLAP_DRIFT",
        "CROSS_PAGE_DUPLICATE_REASON");

    fixture.State.DuplicateAcrossPageBoundary = false;
    fixture.State.ReverseStorage = true;
    var shuffled = await adapter.CaptureAsync(request);
    fixture.State.ReverseStorage = false;
    var canonical = await adapter.CaptureAsync(request);
    Require(shuffled.CaptureSemanticSha256 == canonical.CaptureSemanticSha256,
        "PHYSICAL_ORDER_INDEPENDENT");
    Require(fixture.State.WriteCount == 0, "ALL_CAPTURE_ZERO_WRITE");
    Require(fixture.State.ExportCalls == 0, "STOP_BEFORE_EXPORT_PARSER");
}

static StatisticReconciliationActualApiCaptureRequest Request(
    string surface,
    ImmutableArray<StatisticReconciliationActualApiPageSelector> pages,
    string filterJson = "{\"kind\":\"ALL\"}",
    string? ownerResultId = null,
    long? expectedTotalRows = null)
    => new(
        surface,
        "work-01",
        "scope-01",
        "template-01",
        ownerResultId,
        filterJson,
        expectedTotalRows ??
        (filterJson == "{\"group\":\"SMALL\"}" ? 7 : 57),
        pages);

static ImmutableArray<StatisticReconciliationActualApiPageSelector> Pages(
    params (int Page, int PageSize)[] values)
    => values.Select(value => new StatisticReconciliationActualApiPageSelector(
            value.Page,
            value.PageSize))
        .ToImmutableArray();

static void AssertReady(
    StatisticReconciliationActualApiCapture capture,
    long totalRows)
{
    Require(capture.CaptureState == StatisticReconciliationActualApiCaptureStates.Ready,
        "CAPTURE_READY");
    Require(capture.FullFilterTotalsStable, "TOTALS_STABLE");
    Require(capture.GenerationStable, "GENERATION_STABLE");
    Require(capture.OverlapStable, "OVERLAP_STABLE");
    Require(capture.PagingContractsValid, "PAGING_VALID");
    Require(capture.Pages.All(page => page.TotalRows == totalRows), "TOTAL_ROWS_EXPECTED");
}

static async Task ExpectReasonAsync(Func<Task> action, string reason)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualObservationException ex)
    {
        Require(ex.Reason == reason, $"EXPECTED_{reason}_GOT_{ex.Reason}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{reason}");
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

static string Hash(char value) => new(value, 64);

internal sealed class TestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private TestFixture(WebApplication app, HttpClient client, TestServerState state)
    {
        _app = app;
        _client = client;
        State = state;
    }

    internal TestServerState State { get; }

    internal static async Task<TestFixture> StartAsync()
    {
        var state = new TestServerState();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        app.MapGet("/authorize", (HttpContext context) =>
        {
            state.AuthorizationCalls++;
            state.Events.Add("AUTH");
            var actor = context.Request.Headers["X-Actor"].ToString();
            if (actor != "manager")
                return Results.NotFound();
            var workId = context.Request.Query["workId"].ToString();
            var scopeId = context.Request.Query["scopeId"].ToString();
            var permissions = new[] { "RECONCILIATION_DETAIL", "STATISTICS_READ" };
            var sha = StatisticReconciliationActualApiObservationAdapter.AuthorizationSha(
                actor,
                workId,
                scopeId,
                permissions,
                57,
                57);
            return Results.Json(new AuthorizationWire(
                actor,
                workId,
                scopeId,
                permissions,
                57,
                57,
                sha));
        });

        app.MapPost("/page", async (HttpContext context) =>
        {
            state.PageCalls++;
            state.Events.Add("PAGE");
            if (context.Request.Headers["X-Actor"].ToString() != "manager")
                return Results.NotFound();
            var query = await context.Request.ReadFromJsonAsync<PageQueryWire>()
                        ?? throw new InvalidOperationException("PAGE_QUERY_REQUIRED");
            var source = Enumerable.Range(0, 57)
                .Select(index => new SourceRow($"row-{index:D3}", index))
                .ToList();
            if (state.ReverseStorage)
                source.Reverse();
            source = source.OrderBy(row => row.Id, StringComparer.Ordinal).ToList();
            if (query.CanonicalFilterJson.Contains("SMALL", StringComparison.Ordinal))
                source = source.Take(7).ToList();
            var logicalTotal = source.Count;
            var reportedTotal = state.DriftTotals && query.PageSize == 50
                ? logicalTotal - 1
                : logicalTotal;
            var pageRows = source
                .Skip(query.Page * query.PageSize)
                .Take(query.PageSize)
                .ToList();
            if (state.DriftOverlap && query.PageSize == 50 && pageRows.Count > 0)
                pageRows[0] = new SourceRow("row-overlap-drift", -1);
            if (state.DuplicateAcrossPageBoundary && query.Page == 1 &&
                query.PageSize == 50 && pageRows.Count > 0)
                pageRows[0] = new SourceRow("row-049", pageRows[0].Value);
            var generationId = state.DriftGeneration && query.PageSize == 50
                ? new string('c', 64)
                : new string('a', 64);
            var generationSha = state.DriftGeneration && query.PageSize == 50
                ? new string('d', 64)
                : new string('b', 64);
            var rows = pageRows.Select(row =>
            {
                var json = $"{{\"id\":{JsonSerializer.Serialize(row.Id)},\"value\":{row.Value.ToString(CultureInfo.InvariantCulture)}}}";
                return new RowWire(
                    row.Id,
                    json,
                    StatisticReconciliationActualApiObservationAdapter.RowSha(row.Id, json));
            }).ToArray();
            var totals = Totals(query.Surface, reportedTotal);
            var totalPages = reportedTotal == 0
                ? 0
                : (int)Math.Ceiling(reportedTotal / (double)query.PageSize);
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
                $"\"sha256-{generationSha}\"",
                generationId,
                generationSha,
                query.Page,
                query.PageSize,
                totalPages,
                rows.Length,
                totals,
                rows));
        });

        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                      ?? throw new InvalidOperationException("KESTREL_ADDRESS_MISSING");
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new TestFixture(app, client, state);
    }

    internal StatisticReconciliationActualApiObservationAdapter Adapter(string actor)
        => new(new KestrelOwnerReader(_client, actor));

    internal void Reset() => State.Reset();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static TotalWire[] Totals(string surface, int total)
        => surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField or
                StatisticReconciliationActualApiSurfaces.DirectTable =>
                new[]
                {
                    new TotalWire("totalRows", "INTEGER", total.ToString(CultureInfo.InvariantCulture)),
                    new TotalWire("totalValueCount", "INTEGER", (total * 2).ToString(CultureInfo.InvariantCulture)),
                    new TotalWire("totalSum", "DECIMAL", (total * (total - 1) / 2m).ToString(CultureInfo.InvariantCulture)),
                    new TotalWire("totalReportCount", "INTEGER", total.ToString(CultureInfo.InvariantCulture))
                },
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                new[]
                {
                    new TotalWire("totalRows", "INTEGER", total.ToString(CultureInfo.InvariantCulture)),
                    new TotalWire("totalRowCount", "INTEGER", (total * 3).ToString(CultureInfo.InvariantCulture)),
                    new TotalWire("totalReportCount", "INTEGER", total.ToString(CultureInfo.InvariantCulture))
                },
            StatisticReconciliationActualApiSurfaces.P9Diff =>
                new[]
                {
                    new TotalWire("totalRowCount", "INTEGER", total.ToString(CultureInfo.InvariantCulture)),
                    new TotalWire("totalEqualRowCount", "INTEGER", "0"),
                    new TotalWire("totalChangedRowCount", "INTEGER", total.ToString(CultureInfo.InvariantCulture))
                },
            _ => new[]
            {
                new TotalWire("totalRows", "INTEGER", total.ToString(CultureInfo.InvariantCulture))
            }
        };
}

internal sealed class KestrelOwnerReader : IStatisticReconciliationActualApiOwnerReader
{
    private readonly HttpClient _client;
    private readonly string _actor;

    internal KestrelOwnerReader(HttpClient client, string actor)
    {
        _client = client;
        _actor = actor;
    }

    public async Task<StatisticReconciliationActualApiAuthorizationContext> AuthorizeAsync(
        StatisticReconciliationActualApiAuthorizationProbe probe,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"authorize?workId={Uri.EscapeDataString(probe.WorkId ?? string.Empty)}&scopeId={Uri.EscapeDataString(probe.ScopeAssignmentId ?? string.Empty)}");
        request.Headers.Add("X-Actor", _actor);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new StatisticReconciliationActualApiAuthorizationContext(
                string.Empty,
                string.Empty,
                string.Empty,
                ImmutableArray<string>.Empty,
                0,
                0,
                Hash('0'),
                false);
        }
        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<AuthorizationWire>(
                       cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("AUTH_WIRE_REQUIRED");
        return new StatisticReconciliationActualApiAuthorizationContext(
            wire.ActorUserId,
            wire.WorkId,
            wire.ScopeAssignmentId,
            wire.PermissionCodes.ToImmutableArray(),
            wire.RowCountBeforeRedaction,
            wire.RowCountAfterRedaction,
            wire.AuthorizationSnapshotSha256,
            true);
    }

    public async Task<StatisticReconciliationActualApiOwnerPage> ReadPageAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "page")
        {
            Content = JsonContent.Create(new PageQueryWire(
                query.Surface,
                query.RouteId,
                query.WorkId,
                query.ScopeAssignmentId,
                query.DynamicFormTemplateId,
                query.OwnerResultId,
                query.CanonicalFilterJson,
                query.FilterSha256,
                query.AuthorizationSnapshotSha256,
                query.Page,
                query.PageSize,
                query.RequestSha256))
        };
        request.Headers.Add("X-Actor", _actor);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var wire = await response.Content.ReadFromJsonAsync<PageResponseWire>(
                       cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("PAGE_WIRE_REQUIRED");
        return new StatisticReconciliationActualApiOwnerPage(
            wire.Surface,
            wire.RouteId,
            wire.WorkId,
            wire.ScopeAssignmentId,
            wire.DynamicFormTemplateId,
            wire.OwnerResultId,
            wire.FilterSha256,
            wire.AuthorizationSnapshotSha256,
            wire.RequestSha256,
            wire.ETag,
            wire.GenerationId,
            wire.GenerationSha256,
            wire.Page,
            wire.PageSize,
            wire.TotalPages,
            wire.ReturnedRows,
            wire.FullFilterTotals.Select(total =>
                    new StatisticReconciliationActualApiTotalValue(
                        total.Name,
                        total.ValueType,
                        total.CanonicalValue))
                .ToImmutableArray(),
            wire.Rows.Select(row => new StatisticReconciliationActualApiOwnerRow(
                    row.Identity,
                    row.CanonicalRowJson,
                    row.RowSemanticSha256))
                .ToImmutableArray());
    }

    private static string Hash(char value) => new(value, 64);
}

internal sealed class TestServerState
{
    internal int AuthorizationCalls { get; set; }
    internal int PageCalls { get; set; }
    internal int WriteCount { get; set; }
    internal int ExportCalls { get; set; }
    internal bool DriftGeneration { get; set; }
    internal bool DriftTotals { get; set; }
    internal bool DriftOverlap { get; set; }
    internal bool DuplicateAcrossPageBoundary { get; set; }
    internal bool ReverseStorage { get; set; }
    internal List<string> Events { get; } = new();

    internal void Reset()
    {
        AuthorizationCalls = 0;
        PageCalls = 0;
        WriteCount = 0;
        ExportCalls = 0;
        DriftGeneration = false;
        DriftTotals = false;
        DriftOverlap = false;
        DuplicateAcrossPageBoundary = false;
        ReverseStorage = false;
        Events.Clear();
    }
}

internal sealed record SourceRow(string Id, int Value);
internal sealed record AuthorizationWire(
    string ActorUserId,
    string WorkId,
    string ScopeAssignmentId,
    string[] PermissionCodes,
    long RowCountBeforeRedaction,
    long RowCountAfterRedaction,
    string AuthorizationSnapshotSha256);
internal sealed record PageQueryWire(
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
internal sealed record TotalWire(string Name, string ValueType, string CanonicalValue);
internal sealed record RowWire(string Identity, string CanonicalRowJson, string RowSemanticSha256);
internal sealed record PageResponseWire(
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
