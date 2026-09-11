using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.BasicSummary;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-T26-OWNER-SURFACE-01", () => SurfaceAsync(
        StatisticReconciliationActualApiSurfaces.DirectField,
        "{\"periodInstanceKey\":\"period-01\"}",
        FixtureIds.ResultId,
        "totalValueCount")),
    ("P10-T26-OWNER-SURFACE-02", () => SurfaceAsync(
        StatisticReconciliationActualApiSurfaces.DirectTable,
        "{\"periodInstanceKey\":\"period-01\"}",
        FixtureIds.ResultId,
        "totalValueCount")),
    ("P10-T26-OWNER-SURFACE-03", () => SurfaceAsync(
        StatisticReconciliationActualApiSurfaces.DirectLabel,
        "{\"periodInstanceKey\":\"period-01\"}",
        FixtureIds.ResultId,
        "totalRowCount")),
    ("P10-T26-OWNER-SURFACE-04", () => SurfaceAsync(
        StatisticReconciliationActualApiSurfaces.BasicSource,
        "{}",
        "snapshot-01",
        "totalRows")),
    ("P10-T26-OWNER-SURFACE-05", () => SurfaceAsync(
        StatisticReconciliationActualApiSurfaces.P9Diff,
        "{}",
        FixtureIds.ResultId,
        "totalChangedRowCount")),
    ("P10-T26-OWNER-BASIC-06", BasicDirtyAndMissingZeroWriteAsync),
    ("P10-T26-OWNER-OVERLAP-07", DuplicateIdentityAcrossPagesAsync)
};

foreach (var test in cases)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Id}");
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {test.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Console.WriteLine(
    "P10_T26_OWNER_PROJECTION_OK cases=7 surfaces=5 basicStaleZeroWrite=true " +
    "crossPageDuplicateRejected=true pinnedDirect=true");
return 0;

static async Task SurfaceAsync(
    string surface,
    string filterJson,
    string? ownerResultId,
    string requiredTotal)
{
    var fixture = new ProjectionFixture();
    var query = fixture.Query(surface, filterJson, ownerResultId);
    var page = await fixture.Service.ReadPageProjectionAsync(
        fixture.Authorization,
        query,
        default);
    Equal(surface, page.Surface, "SURFACE");
    Equal(query.RouteId, page.RouteId, "ROUTE");
    Equal(query.RequestSha256, page.RequestSha256, "REQUEST_SHA");
    Equal(1, page.ReturnedRows, "RETURNED_ROWS");
    Equal(1, page.Rows.Length, "ROW_COUNT");
    Require(page.FullFilterTotals.Any(total => total.Name == requiredTotal),
        "REQUIRED_FULL_FILTER_TOTAL");
    Require(page.GenerationSha256?.Length == 64 &&
            page.ETag == $"\"sha256-{page.GenerationSha256}\"",
        "STRONG_GENERATION_PIN");
    Require(page.Rows[0].RowSemanticSha256 ==
            StatisticReconciliationActualApiObservationAdapter.RowSha(
                page.Rows[0].Identity,
                page.Rows[0].CanonicalRowJson),
        "ROW_SEMANTIC_PIN");
    if (surface.StartsWith("DIRECT_", StringComparison.Ordinal))
    {
        Require(fixture.Direct.PinnedRunIds.SequenceEqual(
                [FixtureIds.ResultId],
                StringComparer.Ordinal),
            "DIRECT_EXACT_PINNED_RUN_ID");
    }
    if (surface == StatisticReconciliationActualApiSurfaces.DirectField)
    {
        fixture.Direct.IncludeForeignPublication = true;
        await ExpectStatusAsync(
            () => fixture.Service.ReadPageProjectionAsync(
                fixture.Authorization,
                query,
                default),
            StatusCodes.Status409Conflict);
    }
}

static async Task BasicDirtyAndMissingZeroWriteAsync()
{
    var fixture = new ProjectionFixture();
    var query = fixture.Query(
        StatisticReconciliationActualApiSurfaces.BasicSource,
        "{}",
        "snapshot-01");

    fixture.Basic.Mode = BasicOwnerMode.Dirty;
    await ExpectStatusAsync(
        () => fixture.Service.ReadPageProjectionAsync(
            fixture.Authorization,
            query,
            default),
        StatusCodes.Status409Conflict);
    fixture.Basic.Mode = BasicOwnerMode.Missing;
    await ExpectStatusAsync(
        () => fixture.Service.ReadPageProjectionAsync(
            fixture.Authorization,
            query,
            default),
        StatusCodes.Status404NotFound);

    Equal(2, fixture.Basic.ReadCalls, "BASIC_READ_CALLS");
    Equal(0, fixture.Basic.WriteCalls, "BASIC_ZERO_WRITE");
    Equal(0, fixture.Direct.ReadCalls, "BASIC_NO_DIRECT_READ");
    Equal(0, fixture.Diff.ReadCalls, "BASIC_NO_DIFF_READ");
}

static async Task DuplicateIdentityAcrossPagesAsync()
{
    var capture = await new StatisticReconciliationActualApiObservationAdapter(
            new DuplicateIdentityOwner())
        .CaptureAsync(new StatisticReconciliationActualApiCaptureRequest(
            StatisticReconciliationActualApiSurfaces.DirectField,
            FixtureIds.WorkId,
            FixtureIds.ScopeId,
            FixtureIds.TemplateId,
            null,
            "{}",
            2,
            [new(0, 1), new(1, 1)]));
    Equal(StatisticReconciliationActualApiCaptureStates.Invalid,
        capture.CaptureState,
        "DUPLICATE_IDENTITY_CAPTURE_STATE");
    Equal("API_PAGE_OVERLAP_DRIFT", capture.CaptureReason!,
        "DUPLICATE_IDENTITY_REASON");
    Require(!capture.OverlapStable, "DUPLICATE_IDENTITY_OVERLAP_REJECTED");
}

static async Task ExpectStatusAsync(Func<Task> action, int expected)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualApiEndpointException error)
    {
        Equal(expected, error.StatusCode, "ENDPOINT_STATUS");
        return;
    }
    throw new InvalidOperationException("ENDPOINT_EXCEPTION_REQUIRED");
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

static void Equal<T>(T expected, T actual, string reason)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{reason}:expected={expected};actual={actual}");
}

internal static class FixtureIds
{
    internal const string WorkId = "507f1f77bcf86cd799439021";
    internal const string ScopeId = "507f1f77bcf86cd799439022";
    internal const string TemplateId = "507f1f77bcf86cd799439023";
    internal const string ResultId = "507f1f77bcf86cd799439024";
}

internal sealed class ProjectionFixture
{
    internal FixtureDirectOwner Direct { get; } = new();
    internal FixtureDiffOwner Diff { get; } = new();
    internal FixtureBasicOwner Basic { get; } = new();
    internal StatisticReconciliationActualApiProjectionService Service { get; }
    internal StatisticReconciliationActualApiAuthorizationContext Authorization
        { get; }

    internal ProjectionFixture()
    {
        Authorization = AuthorizationFor("service-actor", ["STATISTICS_READ"]);
        Service = new StatisticReconciliationActualApiProjectionService(
            null!,
            null!,
            Direct,
            Diff,
            Basic);
    }

    internal StatisticReconciliationActualApiOwnerPageQuery Query(
        string surface,
        string filterJson,
        string? ownerResultId)
    {
        var route = StatisticReconciliationActualApiProtocol.RouteId(surface);
        using var filter = StatisticReconciliationActualJson.ParseStrict(
            filterJson,
            "FIXTURE_FILTER");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            filter.RootElement);
        var filterSha = StatisticReconciliationActualJson.RawSha256(canonical);
        var requestSha = StatisticReconciliationActualApiProtocol.RequestSha(
            surface,
            route,
            FixtureIds.WorkId,
            FixtureIds.ScopeId,
            FixtureIds.TemplateId,
            ownerResultId,
            filterSha,
            Authorization.AuthorizationSnapshotSha256,
            0,
            1);
        return new StatisticReconciliationActualApiOwnerPageQuery(
            surface,
            route,
            FixtureIds.WorkId,
            FixtureIds.ScopeId,
            FixtureIds.TemplateId,
            ownerResultId,
            canonical,
            filterSha,
            Authorization.AuthorizationSnapshotSha256,
            0,
            1,
            requestSha);
    }

    internal static StatisticReconciliationActualApiAuthorizationContext
        AuthorizationFor(string actor, ImmutableArray<string> permissions)
    {
        var sha = StatisticReconciliationActualApiObservationAdapter.AuthorizationSha(
            actor,
            FixtureIds.WorkId,
            FixtureIds.ScopeId,
            permissions,
            1,
            1);
        return new StatisticReconciliationActualApiAuthorizationContext(
            actor,
            FixtureIds.WorkId,
            FixtureIds.ScopeId,
            permissions,
            1,
            1,
            sha,
            true);
    }
}

internal sealed class FixtureDirectOwner :
    IP9DirectResultService,
    IStatisticReconciliationActualPinnedDirectResultService
{
    internal int ReadCalls { get; private set; }
    internal List<string> PinnedRunIds { get; } = [];
    internal bool IncludeForeignPublication { get; set; }

    public Task<FieldStatisticSummaryResponse> ReadFieldPinnedAsync(
        FieldStatisticSummaryRequest request,
        string runId,
        CancellationToken ct)
    {
        PinnedRunIds.Add(runId);
        return ReadFieldAsync(request, ct);
    }

    public Task<TableStatisticSummaryResponse> ReadTablePinnedAsync(
        TableStatisticSummaryRequest request,
        string runId,
        CancellationToken ct)
    {
        PinnedRunIds.Add(runId);
        return ReadTableAsync(request, ct);
    }

    public Task<LabelStatisticSummaryResponse> ReadLabelPinnedAsync(
        LabelStatisticSummaryRequest request,
        string runId,
        CancellationToken ct)
    {
        PinnedRunIds.Add(runId);
        return ReadLabelAsync(request, ct);
    }

    public Task<FieldStatisticSummaryResponse> ReadFieldAsync(
        FieldStatisticSummaryRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult(new FieldStatisticSummaryResponse
        {
            Metadata = Metadata(),
            Page = request.Page,
            PageSize = request.PageSize,
            ReturnedRows = 1,
            TotalRows = 3,
            TotalValueCount = 7,
            TotalSum = 11m,
            TotalReportCount = 2,
            Rows =
            [
                new FieldStatisticSummaryRow
                {
                    WorkId = FixtureIds.WorkId,
                    ScopeType = "ASSIGNMENT",
                    ScopeId = FixtureIds.ScopeId,
                    DynamicFormTemplateId = FixtureIds.TemplateId,
                    FieldId = "field-01",
                    FieldKey = "field-key-01",
                    FieldLabel = "Field 01",
                    FieldType = "NUMBER",
                    PeriodKey = "2026-08",
                    PeriodInstanceKey = "period-01",
                    PeriodKind = "MONTH",
                    ReportStatus = 3,
                    ValueCount = 7,
                    NumericValueCount = 7,
                    Sum = 11m,
                    ReportCount = 2,
                    UpdatedAtUtc = Utc()
                }
            ]
        });
    }

    public Task<TableStatisticSummaryResponse> ReadTableAsync(
        TableStatisticSummaryRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult(new TableStatisticSummaryResponse
        {
            Metadata = Metadata(),
            Page = request.Page,
            PageSize = request.PageSize,
            ReturnedRows = 1,
            TotalRows = 3,
            TotalValueCount = 7,
            TotalSum = 11m,
            TotalReportCount = 2,
            Rows =
            [
                new TableStatisticSummaryRow
                {
                    WorkId = FixtureIds.WorkId,
                    ScopeType = "ASSIGNMENT",
                    ScopeId = FixtureIds.ScopeId,
                    DynamicFormTemplateId = FixtureIds.TemplateId,
                    DynamicExcelTemplateId = "excel-01",
                    BlockId = "block-01",
                    TableMode = "FIXED_GRID",
                    MetricKey = "metric-01",
                    RowKey = "row-01",
                    ColumnKey = "column-01",
                    DataType = "NUMBER",
                    PeriodKey = "2026-08",
                    PeriodInstanceKey = "period-01",
                    PeriodKind = "MONTH",
                    ReportStatus = 3,
                    ValueCount = 7,
                    NumericValueCount = 7,
                    Sum = 11m,
                    ReportCount = 2,
                    UpdatedAtUtc = Utc()
                }
            ]
        });
    }

    public Task<LabelStatisticSummaryResponse> ReadLabelAsync(
        LabelStatisticSummaryRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult(new LabelStatisticSummaryResponse
        {
            Metadata = Metadata(),
            Page = request.Page,
            PageSize = request.PageSize,
            ReturnedRows = 1,
            TotalRows = 3,
            TotalRowCount = 7,
            TotalReportCount = 2,
            Rows =
            [
                new LabelStatisticSummaryRow
                {
                    WorkId = FixtureIds.WorkId,
                    ScopeType = "ASSIGNMENT",
                    ScopeId = FixtureIds.ScopeId,
                    DynamicFormTemplateId = FixtureIds.TemplateId,
                    DynamicExcelTemplateId = "excel-01",
                    BlockId = "block-01",
                    LabelCode = "label-01",
                    LabelName = "Label 01",
                    PeriodKey = "2026-08",
                    PeriodInstanceKey = "period-01",
                    PeriodKind = "MONTH",
                    ReportStatus = 3,
                    RowCount = 7,
                    ReportCount = 2,
                    UpdatedAtUtc = Utc()
                }
            ]
        });
    }

    public Task<FieldTextConcatResponse> ReadTextAsync(
        FieldTextConcatRequest request,
        CancellationToken ct)
        => throw new NotSupportedException();

    private P9DirectResultMetadata Metadata()
    {
        var publications = new List<P9DirectPublicationPin>
        {
            new()
            {
                RunId = FixtureIds.ResultId,
                GenerationId = "generation-01",
                GenerationHash = new string('b', 64),
                DirectSourceRevision = 1,
                DirectPublicationRevision = 1
            }
        };
        if (IncludeForeignPublication)
        {
            publications.Add(new P9DirectPublicationPin
            {
                RunId = "507f1f77bcf86cd799439099",
                GenerationId = "generation-foreign",
                GenerationHash = new string('c', 64),
                DirectSourceRevision = 1,
                DirectPublicationRevision = 2
            });
        }
        return new P9DirectResultMetadata
        {
            State = P9ResultStates.Ready,
            Freshness = "FRESH",
            WorkId = FixtureIds.WorkId,
            PeriodInstanceKey = "period-01",
            Publications = publications
        };
    }

    private static DateTime Utc()
        => new(2026, 8, 11, 1, 0, 0, DateTimeKind.Utc);
}

internal enum BasicOwnerMode
{
    Ready,
    Dirty,
    Missing
}

internal sealed class FixtureBasicOwner :
    IStatisticReconciliationActualBasicApiReadOwner
{
    internal BasicOwnerMode Mode { get; set; }
    internal int ReadCalls { get; private set; }
    internal int WriteCalls { get; private set; }

    public Task<StatisticReconciliationActualBasicApiSourcePage>
        ReadSourcesPageAsync(
            string snapshotId,
            string workId,
            string scopeAssignmentId,
            string dynamicFormTemplateId,
            StatisticReconciliationActualBasicApiSourceFilter filter,
            int page,
            int pageSize,
            CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ReadCalls++;
        if (Mode == BasicOwnerMode.Dirty)
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status409Conflict,
                "API_BASIC_SNAPSHOT_NOT_CLEAN");
        }
        if (Mode == BasicOwnerMode.Missing)
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status404NotFound,
                "API_BASIC_OWNER_NOT_FOUND");
        }
        return Task.FromResult(
            new StatisticReconciliationActualBasicApiSourcePage(
                [new WorkAssignmentBasicSummarySourceDto
                {
                    WorkAssignmentId = scopeAssignmentId,
                    WorkAssignmentReportId = FixtureIds.ResultId,
                    WorkReportPeriodId = "507f1f77bcf86cd799439025",
                    PeriodKey = "2026-08",
                    PeriodInstanceKey = "period-01",
                    PeriodKind = "MONTH",
                    ReportStatus = 3,
                    PayloadRevision = 1,
                    PayloadHash = new string('c', 64)
                }],
                1,
                snapshotId,
                new string('d', 64)));
    }
}

internal sealed class FixtureDiffOwner : IWorkReportStatisticDiffService
{
    internal int ReadCalls { get; private set; }

    public Task<P9StatisticDiffResultResponse> GetP9ResultAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string resultId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ReadCalls++;
        return Task.FromResult(new P9StatisticDiffResultResponse(
            resultId,
            "507f1f77bcf86cd799439026",
            P9StatisticDiffResultStatuses.Completed,
            assignmentId,
            dynamicFormTemplateId,
            "507f1f77bcf86cd799439027",
            "507f1f77bcf86cd799439028",
            1,
            1,
            new string('e', 64),
            "command-01",
            new string('f', 64),
            "receipt-01",
            "UTC_GREGORIAN",
            1,
            0,
            1,
            page,
            pageSize,
            1,
            new string('a', 64),
            true,
            true,
            false,
            new DateTime(2026, 8, 11, 2, 0, 0, DateTimeKind.Utc),
            null,
            [new P9StatisticDiffRowResponse(
                "row-diff-01",
                0,
                "FIELD:field-01",
                "FIELD",
                "field-01",
                Value("1"),
                Value("2"),
                false,
                "VALUE_CHANGED",
                1m)]));
    }

    private static P9StatisticDiffTypedValueResponse Value(string value)
        => new(
            "VALUE", "NUMBER", value, decimal.Parse(value), null, null, []);

    public Task<WorkReportStatisticDiffConfigReadback> GetP8ConfigAsync(
        string assignmentId, string dynamicFormTemplateId, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffConfigVersionsResult> ListP8ConfigVersionsAsync(
        string assignmentId, string dynamicFormTemplateId, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffConfigReadback> GetP8ConfigVersionAsync(
        string assignmentId, string dynamicFormTemplateId, int versionNo,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffConfigReadback> PutP8ConfigAsync(
        string assignmentId, string dynamicFormTemplateId, JsonElement body,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffConfigReadback> LockP8ConfigAsync(
        string assignmentId, string dynamicFormTemplateId, JsonElement body,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffConfigReadback> CreateNextP8DraftAsync(
        string assignmentId, string dynamicFormTemplateId, JsonElement body,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<List<WorkReportStatisticDiffConfigDto>> ListConfigsAsync(
        string? workId, string? assignmentId, string? dynamicFormTemplateId,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffConfigDto> SaveConfigAsync(
        WorkReportStatisticDiffSaveRequest req, string? actorUserId,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteConfigAsync(
        string configId, string? actorUserId,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<WorkReportStatisticDiffRunResponse> RunAsync(
        WorkReportStatisticDiffRunRequest req,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<P9StatisticDiffResultResponse> RunP9Async(
        string assignmentId, string dynamicFormTemplateId,
        P9StatisticDiffRunRequest request,
        CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class DuplicateIdentityOwner :
    IStatisticReconciliationActualApiOwnerReader
{
    private readonly StatisticReconciliationActualApiAuthorizationContext _auth =
        ProjectionFixture.AuthorizationFor("service-actor", ["STATISTICS_READ"]);

    public Task<StatisticReconciliationActualApiAuthorizationContext>
        AuthorizeAsync(
            StatisticReconciliationActualApiAuthorizationProbe probe,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_auth);
    }

    public Task<StatisticReconciliationActualApiOwnerPage> ReadPageAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string identity = "duplicate-row";
        const string json = "{\"value\":1}";
        return Task.FromResult(new StatisticReconciliationActualApiOwnerPage(
            query.Surface,
            query.RouteId,
            query.WorkId,
            query.ScopeAssignmentId,
            query.DynamicFormTemplateId,
            query.OwnerResultId,
            query.FilterSha256,
            query.AuthorizationSnapshotSha256,
            query.RequestSha256,
            "\"sha256-duplicate-fixture\"",
            "generation-duplicate",
            new string('9', 64),
            query.Page,
            query.PageSize,
            2,
            1,
            [new StatisticReconciliationActualApiTotalValue(
                "totalRows", "INTEGER", "2")],
            [new StatisticReconciliationActualApiOwnerRow(
                identity,
                json,
                StatisticReconciliationActualApiObservationAdapter.RowSha(
                    identity,
                    json))]));
    }
}
