using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using tdtd_be.Controllers;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-T26-OWNER-ROUTE-01", ServiceAuthAndAuthBeforeBodyAsync),
    ("P10-T26-OWNER-ROUTE-02", FrozenAuthorizationSnapshotAsync),
    ("P10-T26-OWNER-ROUTE-03", BasicOwnerStaticZeroWriteGateAsync)
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
    "P10_T26_OWNER_ROUTE_OK cases=3 bearer=true authBeforeBody=true " +
    "frozenSnapshot=true basicGetSummary=false basicWrites=false");
return 0;

static async Task ServiceAuthAndAuthBeforeBodyAsync()
{
    var fixture = await OwnerRouteFixture.StartAsync();
    await using var app = fixture.App;
    using var client = fixture.Client;

    var authorizeUri = fixture.Uri("authorize");
    Equal(HttpStatusCode.Unauthorized,
        (await client.GetAsync(authorizeUri)).StatusCode,
        "MISSING_BEARER");
    Require(fixture.Endpoint.Events.IsEmpty,
        "AUTH_HANDLER_MUST_PRECEDE_CONTROLLER");

    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", "wrong-token");
    Equal(HttpStatusCode.Unauthorized,
        (await client.GetAsync(authorizeUri)).StatusCode,
        "WRONG_BEARER");
    Require(fixture.Endpoint.Events.IsEmpty,
        "WRONG_BEARER_MUST_NOT_REACH_BUSINESS_AUTH");

    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", OwnerRouteFixture.ServiceToken);
    fixture.Endpoint.Authorized = false;
    using (var hidden = await client.PostAsync(
               fixture.Uri("page"),
               new StringContent("{malformed", System.Text.Encoding.UTF8,
                   "application/json")))
    {
        Equal(HttpStatusCode.NotFound, hidden.StatusCode,
            "UNAUTHORIZED_TARGET_IS_HIDDEN");
    }
    EqualSequence(["AUTH"], fixture.Endpoint.DrainEvents(),
        "UNAUTHORIZED_BODY_MUST_NOT_BE_PARSED");

    fixture.Endpoint.Authorized = true;
    using (var malformed = await client.PostAsync(
               fixture.Uri("page"),
               new StringContent("{malformed", System.Text.Encoding.UTF8,
                   "application/json")))
    {
        Equal(HttpStatusCode.BadRequest, malformed.StatusCode,
            "AUTHORIZED_MALFORMED_BODY");
    }
    EqualSequence(["AUTH"], fixture.Endpoint.DrainEvents(),
        "MALFORMED_BODY_MUST_NOT_REACH_OWNER_READ");

    var query = fixture.Query(fixture.Endpoint.Authorization);
    using (var valid = await client.PostAsJsonAsync(
               fixture.Uri("page"),
               query,
               OwnerRouteFixture.JsonOptions))
    {
        Equal(HttpStatusCode.OK, valid.StatusCode, "VALID_PAGE");
    }
    EqualSequence(["AUTH", "BIND", "OWNER_READ"], fixture.Endpoint.DrainEvents(),
        "AUTH_THEN_BIND_THEN_OWNER_READ");
}

static async Task FrozenAuthorizationSnapshotAsync()
{
    var fixture = await OwnerRouteFixture.StartAsync();
    await using var app = fixture.App;
    using var client = fixture.Client;
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", OwnerRouteFixture.ServiceToken);

    var frozen = fixture.Endpoint.Authorization;
    var query = fixture.Query(frozen);
    fixture.Endpoint.Authorization = OwnerRoutePermissionDrift.Create();
    using var response = await client.PostAsJsonAsync(
        fixture.Uri("page"),
        query,
        OwnerRouteFixture.JsonOptions);
    Equal(HttpStatusCode.Conflict, response.StatusCode,
        "AUTHORIZATION_SNAPSHOT_STALE");
    EqualSequence(["AUTH", "BIND"], fixture.Endpoint.DrainEvents(),
        "STALE_AUTH_MUST_READ_ZERO_RESULT_ROWS");
}

static Task BasicOwnerStaticZeroWriteGateAsync()
{
    var root = FindRepositoryRoot();
    var source = File.ReadAllText(Path.Combine(
        root,
        "tdtd-be",
        "Services",
        "WorkAssignments",
        "BasicSummary",
        "WorkAssignmentBasicSummaryService.P10ApiReadOwner.cs"));
    foreach (var forbidden in new[]
             {
                 "GetSummaryAsync(", "InsertOne", "UpdateOne", "UpdateMany",
                 "ReplaceOne", "DeleteOne", "FindOneAnd", "BulkWrite",
                 "Enqueue", "RefreshSnapshot", "MarkSnapshotDirty"
             })
    {
        Require(!source.Contains(forbidden, StringComparison.Ordinal),
            $"BASIC_OWNER_FORBIDDEN_TOKEN:{forbidden}");
    }
    Require(source.Contains("SourceSignatureHash", StringComparison.Ordinal) &&
            source.Contains("WorkAssignmentBasicSummaryRefreshStatuses.Done",
                StringComparison.Ordinal),
        "BASIC_OWNER_CLEAN_SNAPSHOT_GATE_REQUIRED");
    return Task.CompletedTask;
}

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

static void Equal<T>(T expected, T actual, string reason)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{reason}:expected={expected};actual={actual}");
}

static void EqualSequence(string[] expected, string[] actual, string reason)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        throw new InvalidOperationException(
            $"{reason}:expected={string.Join(',', expected)};" +
            $"actual={string.Join(',', actual)}");
}

internal sealed class OwnerRouteFixture : IAsyncDisposable
{
    internal const string ServiceToken = "p10-owner-service-token";
    internal const string WorkId = "507f1f77bcf86cd799439011";
    internal const string ScopeId = "507f1f77bcf86cd799439012";
    internal const string TemplateId = "507f1f77bcf86cd799439013";
    internal static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _address;
    internal WebApplication App { get; }
    internal HttpClient Client { get; }
    internal FixtureEndpointService Endpoint { get; }

    private OwnerRouteFixture(
        WebApplication app,
        HttpClient client,
        FixtureEndpointService endpoint,
        string address)
    {
        App = app;
        Client = client;
        Endpoint = endpoint;
        _address = address.TrimEnd('/');
    }

    internal static async Task<OwnerRouteFixture> StartAsync()
    {
        var endpoint = new FixtureEndpointService
        {
            Authorization = OwnerRouteAuthorization.Create("service-actor")
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));
        builder.Services
            .AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, FixtureBearerHandler>(
                "Bearer",
                _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers()
            .AddApplicationPart(
                typeof(StatisticReconciliationActualOwnerController).Assembly);
        builder.Services.AddSingleton<IControllerActivator>(
            new FixtureControllerActivator(endpoint));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
                          .Features.Get<IServerAddressesFeature>()?
                          .Addresses.Single()
                      ?? throw new InvalidOperationException(
                          "KESTREL_ADDRESS_MISSING");
        return new OwnerRouteFixture(app, new HttpClient(), endpoint, address);
    }

    internal string Uri(string action)
        => $"{_address}/api/internal/statistic-reconciliation/actual/{action}" +
           $"?workId={WorkId}&scopeId={ScopeId}";

    internal StatisticReconciliationActualApiOwnerPageQuery Query(
        StatisticReconciliationActualApiAuthorizationContext authorization)
    {
        const string filter = "{}";
        var route = StatisticReconciliationActualApiProtocol.RouteId(
            StatisticReconciliationActualApiSurfaces.DirectField);
        var filterSha = StatisticReconciliationActualJson.RawSha256(filter);
        var requestSha = StatisticReconciliationActualApiProtocol.RequestSha(
            StatisticReconciliationActualApiSurfaces.DirectField,
            route,
            WorkId,
            ScopeId,
            TemplateId,
            null,
            filterSha,
            authorization.AuthorizationSnapshotSha256,
            0,
            1);
        return new StatisticReconciliationActualApiOwnerPageQuery(
            StatisticReconciliationActualApiSurfaces.DirectField,
            route,
            WorkId,
            ScopeId,
            TemplateId,
            null,
            filter,
            filterSha,
            authorization.AuthorizationSnapshotSha256,
            0,
            1,
            requestSha);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

internal sealed class FixtureEndpointService :
    StatisticReconciliationActualApiEndpointService
{
    internal ConcurrentQueue<string> Events { get; } = new();
    internal bool Authorized { get; set; } = true;
    internal required StatisticReconciliationActualApiAuthorizationContext
        Authorization { get; set; }

    internal override Task<
        StatisticReconciliationActualApiAuthorizationContext?>
        AuthorizeBeforeExistenceAsync(
            string? workId,
            string? scopeAssignmentId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Enqueue("AUTH");
        if (!Authorized || workId != OwnerRouteFixture.WorkId ||
            scopeAssignmentId != OwnerRouteFixture.ScopeId)
        {
            return Task.FromResult<
                StatisticReconciliationActualApiAuthorizationContext?>(null);
        }
        return Task.FromResult<
            StatisticReconciliationActualApiAuthorizationContext?>(Authorization);
    }

    internal override Task<StatisticReconciliationActualApiOwnerPage>
        ReadPageProjectionAsync(
            StatisticReconciliationActualApiAuthorizationContext authorization,
            StatisticReconciliationActualApiOwnerPageQuery query,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Enqueue("BIND");
        if (query.AuthorizationSnapshotSha256 !=
            authorization.AuthorizationSnapshotSha256)
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status409Conflict,
                "API_AUTHORIZATION_SNAPSHOT_STALE");
        }
        Events.Enqueue("OWNER_READ");
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
            "\"sha256-fixture\"",
            "fixture-generation",
            new string('a', 64),
            query.Page,
            query.PageSize,
            0,
            0,
            [new StatisticReconciliationActualApiTotalValue(
                "totalRows", "INTEGER", "0")],
            []));
    }

    internal string[] DrainEvents()
    {
        var result = new List<string>();
        while (Events.TryDequeue(out var value))
            result.Add(value);
        return result.ToArray();
    }
}

internal sealed class FixtureControllerActivator(
    FixtureEndpointService endpoint) : IControllerActivator
{
    public object Create(ControllerContext context)
    {
        if (context.ActionDescriptor.ControllerTypeInfo.AsType() ==
            typeof(StatisticReconciliationActualOwnerController))
        {
            return new StatisticReconciliationActualOwnerController(endpoint);
        }
        throw new InvalidOperationException("UNEXPECTED_CONTROLLER_ACTIVATION");
    }

    public void Release(ControllerContext context, object controller)
    {
    }
}

internal sealed class FixtureBearerHandler(
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
        if (authorization != $"Bearer {OwnerRouteFixture.ServiceToken}")
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Invalid service bearer token."));
        }
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "service-actor")],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
