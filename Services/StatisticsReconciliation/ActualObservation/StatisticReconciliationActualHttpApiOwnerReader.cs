using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualHttpApiOwnerOptions
{
    internal const string SectionName =
        "StatisticReconciliation:ActualObservation:ApiOwner";
    internal const string AuthorizeRoute =
        "api/internal/statistic-reconciliation/actual/authorize";
    internal const string PageRoute =
        "api/internal/statistic-reconciliation/actual/page";

    public string? BaseUri { get; set; }
    public string AuthorizePath { get; set; } = AuthorizeRoute;
    public string PagePath { get; set; } = PageRoute;
    public string? ServiceAuthorizationScheme { get; set; }
    public string? ServiceAuthorizationParameter { get; set; }
}

/// <summary>
/// Production HTTP owner for the API-parity layer. There are exactly two
/// remote operations: authorization-before-existence and a read-only page
/// projection. There is no Mongo fallback and no refresh/export endpoint.
/// </summary>
internal sealed class StatisticReconciliationActualHttpApiOwnerReader(
    HttpClient client,
    IOptions<StatisticReconciliationActualHttpApiOwnerOptions> configured)
    : IStatisticReconciliationActualApiOwnerReader
{
    private const long MaximumResponseBytes = 32L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        MaxDepth = 64,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<StatisticReconciliationActualApiAuthorizationContext>
        AuthorizeAsync(
            StatisticReconciliationActualApiAuthorizationProbe probe,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var options = ResolveOptions();
        var workId = Required(probe.WorkId, "AUTH_WORK_ID");
        var scopeId = Required(probe.ScopeAssignmentId, "AUTH_SCOPE_ID");
        var uri = BuildUri(
            options.BaseUri,
            options.AuthorizePath,
            $"workId={Uri.EscapeDataString(workId)}&scopeId={Uri.EscapeDataString(scopeId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyServiceAuthorization(request, options);
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new StatisticReconciliationActualApiAuthorizationContext(
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                0,
                0,
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_HTTP_API_AUTHORIZATION_DENIED_V1",
                    workId,
                    scopeId),
                false);
        }
        RequireSuccess(response, "AUTHORIZATION");
        var wire = await ReadJsonAsync<AuthorizationWire>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        if (!Eq(wire.WorkId, workId) || !Eq(wire.ScopeAssignmentId, scopeId) ||
            wire.PermissionCodes is null || wire.RowCountBeforeRedaction < 0 ||
            wire.RowCountAfterRedaction < 0 ||
            wire.RowCountAfterRedaction > wire.RowCountBeforeRedaction)
            throw Fail("AUTHORIZATION_BINDING_INVALID");
        var permissions = wire.PermissionCodes.ToImmutableArray();
        var expectedSha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                wire.ActorUserId,
                wire.WorkId,
                wire.ScopeAssignmentId,
                permissions,
                wire.RowCountBeforeRedaction,
                wire.RowCountAfterRedaction);
        if (!Eq(expectedSha, wire.AuthorizationSnapshotSha256))
            throw Fail("AUTHORIZATION_DIGEST_MISMATCH");
        return new StatisticReconciliationActualApiAuthorizationContext(
            wire.ActorUserId,
            wire.WorkId,
            wire.ScopeAssignmentId,
            permissions,
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
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);
        if (!authorization.IsAuthorized ||
            !Eq(authorization.WorkId, query.WorkId) ||
            !Eq(authorization.ScopeAssignmentId, query.ScopeAssignmentId) ||
            !Eq(authorization.AuthorizationSnapshotSha256,
                query.AuthorizationSnapshotSha256) ||
            !StatisticReconciliationActualApiSurfaces.IsSupported(query.Surface))
            throw Fail("PAGE_AUTHORIZATION_BINDING_INVALID");

        var options = ResolveOptions();
        var pageQuery = $"workId={Uri.EscapeDataString(query.WorkId)}&scopeId={Uri.EscapeDataString(query.ScopeAssignmentId)}";
        var uri = BuildUri(options.BaseUri, options.PagePath, pageQuery);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(
                new PageQueryWire(
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
                    query.RequestSha256),
                options: JsonOptions)
        };
        ApplyServiceAuthorization(request, options);
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(response, "PAGE");
        var wire = await ReadJsonAsync<PageResponseWire>(response, cancellationToken)
            .ConfigureAwait(false);
        if (!Eq(wire.Surface, query.Surface) ||
            !Eq(wire.RouteId, query.RouteId) ||
            !Eq(wire.WorkId, query.WorkId) ||
            !Eq(wire.ScopeAssignmentId, query.ScopeAssignmentId) ||
            !Eq(wire.DynamicFormTemplateId, query.DynamicFormTemplateId) ||
            !Eq(wire.OwnerResultId, query.OwnerResultId) ||
            !Eq(wire.FilterSha256, query.FilterSha256) ||
            !Eq(wire.AuthorizationSnapshotSha256,
                query.AuthorizationSnapshotSha256) ||
            !Eq(wire.RequestSha256, query.RequestSha256) ||
            wire.Page != query.Page || wire.PageSize != query.PageSize ||
            wire.FullFilterTotals is null || wire.Rows is null)
            throw Fail("PAGE_TARGET_BINDING_MISMATCH");
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
            wire.Rows.Select(row =>
                    new StatisticReconciliationActualApiOwnerRow(
                        row.Identity,
                        row.CanonicalRowJson,
                        row.RowSemanticSha256))
                .ToImmutableArray());
    }

    private StatisticReconciliationActualHttpApiOwnerOptions
        ResolveOptions()
    {
        var options = configured?.Value ?? throw Fail("OPTIONS_MISSING");
        if (!Uri.TryCreate(options.BaseUri, UriKind.Absolute, out var baseUri) ||
            (!Eq(baseUri.Scheme, Uri.UriSchemeHttp) &&
             !Eq(baseUri.Scheme, Uri.UriSchemeHttps)) ||
            baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 ||
            baseUri.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(options.ServiceAuthorizationScheme) ||
            string.IsNullOrWhiteSpace(options.ServiceAuthorizationParameter) ||
            !Eq(options.AuthorizePath,
                StatisticReconciliationActualHttpApiOwnerOptions.AuthorizeRoute) ||
            !Eq(options.PagePath,
                StatisticReconciliationActualHttpApiOwnerOptions.PageRoute))
            throw Fail("OPTIONS_INCOMPLETE");
        return options;
    }

    private static Uri BuildUri(
        string? baseUriText,
        string relativePath,
        string? query)
    {
        var baseUri = new Uri(baseUriText!, UriKind.Absolute);
        var builder = new UriBuilder(new Uri(baseUri, relativePath));
        if (query is not null)
            builder.Query = query;
        return builder.Uri;
    }

    private static void ApplyServiceAuthorization(
        HttpRequestMessage request,
        StatisticReconciliationActualHttpApiOwnerOptions options)
        => request.Headers.Authorization = new AuthenticationHeaderValue(
            options.ServiceAuthorizationScheme!,
            options.ServiceAuthorizationParameter!);

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw Fail("RESPONSE_TOO_LARGE");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!Eq(mediaType, "application/json") &&
            !Eq(mediaType, "application/problem+json"))
            throw Fail("RESPONSE_CONTENT_TYPE_INVALID");
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaximumResponseBytes)
                throw Fail("RESPONSE_TOO_LARGE");
            await bounded.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(
                   bounded,
                   JsonOptions,
                   cancellationToken)
                   .ConfigureAwait(false)
               ?? throw Fail("RESPONSE_BODY_MISSING");
    }

    private static void RequireSuccess(
        HttpResponseMessage response,
        string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw Fail($"{operation}_HTTP_{(int)response.StatusCode}");
    }

    private static bool RelativePath(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           Uri.TryCreate(value, UriKind.Relative, out _) &&
           !value.StartsWith('/') &&
           !value.Contains("..", StringComparison.Ordinal);

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_HTTP_API_OWNER_{reason}");

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
        string? ETag,
        string? GenerationId,
        string? GenerationSha256,
        int Page,
        int PageSize,
        int? TotalPages,
        int ReturnedRows,
        TotalWire[] FullFilterTotals,
        RowWire[] Rows);
}
