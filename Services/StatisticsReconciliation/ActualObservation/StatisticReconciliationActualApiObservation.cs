using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualApiObservationAdapter
{
    internal const int MaxSelectors = 32;
    internal const int MaxPage = 100_000;
    internal const int MaxPageSize = 200;
    internal const int MaxTotals = 32;

    private static readonly Regex TotalName = new(
        "^total[A-Z][A-Za-z0-9]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly IStatisticReconciliationActualApiOwnerReader _owner;

    internal StatisticReconciliationActualApiObservationAdapter(
        IStatisticReconciliationActualApiOwnerReader owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    internal async Task<StatisticReconciliationActualApiCapture> CaptureAsync(
        StatisticReconciliationActualApiCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The owner resolves the current server-side actor and scope before this
        // adapter parses target identifiers, filters, or page selectors.
        var authority = await _owner.AuthorizeAsync(
            new StatisticReconciliationActualApiAuthorizationProbe(
                request.WorkId,
                request.ScopeAssignmentId),
            cancellationToken);
        if (!authority.IsAuthorized)
            throw Fail("API_TARGET_HIDDEN");

        var normalized = NormalizeAfterAuthorization(request, authority);
        var pageObservations = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualApiPageObservation>(normalized.Pages.Length);

        foreach (var selector in normalized.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestSha = RequestSha(normalized, authority, selector);
            var ownerPage = await _owner.ReadPageAsync(
                authority,
                new StatisticReconciliationActualApiOwnerPageQuery(
                    normalized.Surface,
                    normalized.RouteId,
                    normalized.WorkId,
                    normalized.ScopeAssignmentId,
                    normalized.DynamicFormTemplateId,
                    normalized.OwnerResultId,
                    normalized.CanonicalFilterJson,
                    normalized.FilterSha256,
                    authority.AuthorizationSnapshotSha256,
                    selector.Page,
                    selector.PageSize,
                    requestSha),
                cancellationToken);
            pageObservations.Add(ObservePage(
                normalized,
                authority,
                selector,
                requestSha,
                ownerPage));
        }

        var pages = pageObservations.MoveToImmutable();
        var totalsStable = StableTotals(pages);
        var expectedTotalStable = pages.All(page =>
            page.TotalRows == normalized.ExpectedTotalRows);
        var generationStable = StableGeneration(pages);
        var overlapStable = StableOverlap(pages);
        var pagingValid = pages.All(page =>
            page.TargetBindingMatches &&
            page.RequestBindingMatches &&
            page.PagingContractValid &&
            page.RowIdentitiesUnique &&
            page.RowSemanticsValid);

        var state = !generationStable || (totalsStable && !expectedTotalStable)
            ? StatisticReconciliationActualApiCaptureStates.Stale
            : totalsStable && overlapStable && pagingValid
                ? StatisticReconciliationActualApiCaptureStates.Ready
                : StatisticReconciliationActualApiCaptureStates.Invalid;
        var reason = state switch
        {
            StatisticReconciliationActualApiCaptureStates.Ready => null,
            StatisticReconciliationActualApiCaptureStates.Stale =>
                !generationStable
                    ? "API_GENERATION_DRIFT"
                    : "API_EXPECTED_TOTAL_DRIFT",
            _ when !totalsStable => "API_FULL_FILTER_TOTAL_DRIFT",
            _ when !overlapStable => "API_PAGE_OVERLAP_DRIFT",
            _ => "API_PAGE_CONTRACT_INVALID"
        };
        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_CAPTURE_V1",
            normalized.Surface,
            normalized.RouteId,
            normalized.WorkId,
            normalized.ScopeAssignmentId,
            normalized.DynamicFormTemplateId,
            normalized.OwnerResultId,
            normalized.FilterSha256,
            authority.AuthorizationSnapshotSha256,
            StatisticReconciliationActualCanonical.Integer(
                normalized.ExpectedTotalRows),
            state,
            reason,
            StatisticReconciliationActualCanonical.Boolean(totalsStable),
            StatisticReconciliationActualCanonical.Boolean(expectedTotalStable),
            StatisticReconciliationActualCanonical.Boolean(generationStable),
            StatisticReconciliationActualCanonical.Boolean(overlapStable),
            StatisticReconciliationActualCanonical.Boolean(pagingValid),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_PAGES_V1",
                pages.Select(page => page.PageSemanticSha256)));

        return new StatisticReconciliationActualApiCapture(
            normalized.Surface,
            normalized.RouteId,
            normalized.WorkId,
            normalized.ScopeAssignmentId,
            normalized.DynamicFormTemplateId,
            normalized.OwnerResultId,
            normalized.CanonicalFilterJson,
            normalized.FilterSha256,
            authority,
            pages,
            totalsStable,
            generationStable,
            overlapStable,
            pagingValid,
            state,
            reason,
            captureSha);
    }

    internal static string AuthorizationSha(
        string actorUserId,
        string workId,
        string scopeAssignmentId,
        IEnumerable<string> permissionCodes,
        long rowCountBeforeRedaction,
        long rowCountAfterRedaction)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_AUTHORIZATION_V1",
            actorUserId,
            workId,
            scopeAssignmentId,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_PERMISSION_CODES_V1",
                permissionCodes),
            StatisticReconciliationActualCanonical.Integer(rowCountBeforeRedaction),
            StatisticReconciliationActualCanonical.Integer(rowCountAfterRedaction));

    internal static string RowSha(string identity, string canonicalRowJson)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_ROW_V1",
            identity,
            canonicalRowJson);

    private static NormalizedRequest NormalizeAfterAuthorization(
        StatisticReconciliationActualApiCaptureRequest request,
        StatisticReconciliationActualApiAuthorizationContext authority)
    {
        var actor = StatisticReconciliationActualCanonical.Required(
            authority.ActorUserId,
            "API_ACTOR_USER_ID");
        var work = StatisticReconciliationActualCanonical.Required(
            request.WorkId,
            "API_WORK_ID");
        var scope = StatisticReconciliationActualCanonical.Required(
            request.ScopeAssignmentId,
            "API_SCOPE_ASSIGNMENT_ID");
        if (!StringComparer.Ordinal.Equals(work, authority.WorkId) ||
            !StringComparer.Ordinal.Equals(scope, authority.ScopeAssignmentId))
        {
            throw Fail("API_AUTHORIZATION_SCOPE_MISMATCH");
        }

        var permissions = authority.PermissionCodes;
        if (permissions.IsDefault || permissions.Length == 0 ||
            permissions.Length > 128)
            throw Fail("API_PERMISSION_CODES_INVALID");
        var prior = string.Empty;
        foreach (var permission in permissions)
        {
            var canonical = ExactUpper(permission, "API_PERMISSION_CODE");
            if (prior.Length > 0 && StringComparer.Ordinal.Compare(prior, canonical) >= 0)
                throw Fail("API_PERMISSION_CODES_NOT_CANONICAL");
            prior = canonical;
        }
        if (authority.RowCountBeforeRedaction < 0 ||
            authority.RowCountAfterRedaction < 0 ||
            authority.RowCountAfterRedaction > authority.RowCountBeforeRedaction)
            throw Fail("API_REDACTION_COUNTS_INVALID");
        var authSha = StatisticReconciliationActualCanonical.Sha256(
            authority.AuthorizationSnapshotSha256,
            "API_AUTHORIZATION_SNAPSHOT_SHA256");
        var expectedAuthSha = AuthorizationSha(
            actor,
            work,
            scope,
            permissions,
            authority.RowCountBeforeRedaction,
            authority.RowCountAfterRedaction);
        if (!StringComparer.Ordinal.Equals(authSha, expectedAuthSha))
            throw Fail("API_AUTHORIZATION_SNAPSHOT_SHA256_MISMATCH");

        var surface = ExactUpper(request.Surface, "API_SURFACE");
        if (!StatisticReconciliationActualApiSurfaces.IsSupported(surface))
            throw Fail("API_SURFACE_UNSUPPORTED");
        var template = StatisticReconciliationActualCanonical.Required(
            request.DynamicFormTemplateId,
            "API_FORM_TEMPLATE_ID");
        var resultId = StatisticReconciliationActualCanonical.Optional(
            request.OwnerResultId,
            "API_OWNER_RESULT_ID");
        if (surface == StatisticReconciliationActualApiSurfaces.P9Diff && resultId is null)
            throw Fail("API_OWNER_RESULT_ID_REQUIRED");

        using var filterDocument = StatisticReconciliationActualJson.ParseStrict(
            request.FilterJson,
            "API_FILTER_JSON");
        if (filterDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw Fail("API_FILTER_JSON_NOT_OBJECT");
        var canonicalFilter = StatisticReconciliationActualJson.Canonicalize(
            filterDocument.RootElement);
        var filterSha = StatisticReconciliationActualJson.RawSha256(canonicalFilter);

        if (request.ExpectedTotalRows is < 0 or > 2_000_000)
            throw Fail("API_EXPECTED_TOTAL_ROWS_INVALID");

        if (request.Pages.IsDefault || request.Pages.Length == 0 ||
            request.Pages.Length > MaxSelectors)
            throw Fail("API_PAGE_SELECTORS_INVALID");
        var pages = request.Pages
            .Select(selector => selector ?? throw Fail("API_PAGE_SELECTOR_REQUIRED"))
            .OrderBy(selector => selector.PageSize)
            .ThenBy(selector => selector.Page)
            .ToImmutableArray();
        var seen = new HashSet<(int Page, int PageSize)>();
        foreach (var selector in pages)
        {
            if (selector.Page is < 0 or > MaxPage)
                throw Fail("API_PAGE_INVALID");
            if (selector.PageSize is < 1 or > MaxPageSize)
                throw Fail("API_PAGE_SIZE_INVALID");
            if (!seen.Add((selector.Page, selector.PageSize)))
                throw Fail("API_PAGE_SELECTOR_DUPLICATE");
        }

        return new NormalizedRequest(
            surface,
            RouteId(surface),
            work,
            scope,
            template,
            resultId,
            canonicalFilter,
            filterSha,
            request.ExpectedTotalRows,
            pages);
    }

    private static StatisticReconciliationActualApiPageObservation ObservePage(
        NormalizedRequest request,
        StatisticReconciliationActualApiAuthorizationContext authority,
        StatisticReconciliationActualApiPageSelector selector,
        string requestSha,
        StatisticReconciliationActualApiOwnerPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var targetMatches =
            StringComparer.Ordinal.Equals(page.Surface, request.Surface) &&
            StringComparer.Ordinal.Equals(page.RouteId, request.RouteId) &&
            StringComparer.Ordinal.Equals(page.WorkId, request.WorkId) &&
            StringComparer.Ordinal.Equals(page.ScopeAssignmentId, request.ScopeAssignmentId) &&
            StringComparer.Ordinal.Equals(page.DynamicFormTemplateId, request.DynamicFormTemplateId) &&
            StringComparer.Ordinal.Equals(page.OwnerResultId, request.OwnerResultId) &&
            StringComparer.Ordinal.Equals(page.FilterSha256, request.FilterSha256) &&
            StringComparer.Ordinal.Equals(
                page.AuthorizationSnapshotSha256,
                authority.AuthorizationSnapshotSha256);
        var requestMatches = StringComparer.Ordinal.Equals(page.RequestSha256, requestSha) &&
                             page.Page == selector.Page &&
                             page.PageSize == selector.PageSize;

        var etag = NormalizeETag(page.ETag);
        var generationId = StatisticReconciliationActualCanonical.Optional(
            page.GenerationId,
            "API_GENERATION_ID");
        var generationSha = page.GenerationSha256 is null
            ? null
            : StatisticReconciliationActualCanonical.Sha256(
                page.GenerationSha256,
                "API_GENERATION_SHA256");
        if ((generationId is null) != (generationSha is null))
            throw Fail("API_GENERATION_PIN_INCOMPLETE");
        if (etag is null && generationSha is null)
            throw Fail("API_ETAG_OR_GENERATION_REQUIRED");

        var totals = NormalizeTotals(page.FullFilterTotals);
        var totalRows = TotalRows(totals);
        var rows = page.Rows.IsDefault
            ? ImmutableArray<StatisticReconciliationActualApiOwnerRow>.Empty
            : page.Rows;
        if (rows.Length > MaxPageSize)
            throw Fail("API_ROWS_TOO_MANY");
        var observedRows = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualApiRowObservation>(rows.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var rowSemanticsValid = true;
        for (var index = 0; index < rows.Length; index++)
        {
            var ownerRow = rows[index] ?? throw Fail("API_ROW_REQUIRED");
            var identity = StatisticReconciliationActualCanonical.Required(
                ownerRow.Identity,
                "API_ROW_IDENTITY",
                2048);
            using var rowDocument = StatisticReconciliationActualJson.ParseStrict(
                ownerRow.CanonicalRowJson,
                "API_ROW_JSON");
            if (rowDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw Fail("API_ROW_JSON_NOT_OBJECT");
            var canonicalRow = StatisticReconciliationActualJson.Canonicalize(
                rowDocument.RootElement);
            var canonicalInput = StringComparer.Ordinal.Equals(
                canonicalRow,
                ownerRow.CanonicalRowJson);
            var storedSha = StatisticReconciliationActualCanonical.Sha256(
                ownerRow.RowSemanticSha256,
                "API_ROW_SEMANTIC_SHA256");
            var expectedSha = RowSha(identity, canonicalRow);
            var semanticMatches = canonicalInput &&
                                  StringComparer.Ordinal.Equals(storedSha, expectedSha);
            rowSemanticsValid &= semanticMatches;
            identities.Add(identity);
            observedRows.Add(new StatisticReconciliationActualApiRowObservation(
                checked(selector.Page * selector.PageSize + index),
                identity,
                canonicalRow,
                storedSha,
                semanticMatches));
        }

        var uniqueRows = identities.Count == rows.Length;
        var expectedReturned = totalRows <= (long)selector.Page * selector.PageSize
            ? 0
            : (int)Math.Min(
                selector.PageSize,
                totalRows - (long)selector.Page * selector.PageSize);
        var expectedTotalPages = totalRows == 0
            ? 0
            : checked((int)Math.Ceiling(totalRows / (double)selector.PageSize));
        var pagingValid = page.ReturnedRows == rows.Length &&
                          page.ReturnedRows == expectedReturned &&
                          (!page.TotalPages.HasValue ||
                           page.TotalPages.Value == expectedTotalPages);
        var rowObservations = observedRows.MoveToImmutable();
        var pageSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_PAGE_V1",
            request.Surface,
            request.RouteId,
            request.WorkId,
            request.ScopeAssignmentId,
            request.DynamicFormTemplateId,
            request.OwnerResultId,
            request.FilterSha256,
            authority.AuthorizationSnapshotSha256,
            requestSha,
            etag,
            generationId,
            generationSha,
            StatisticReconciliationActualCanonical.Integer(selector.Page),
            StatisticReconciliationActualCanonical.Integer(selector.PageSize),
            StatisticReconciliationActualCanonical.Integer(page.ReturnedRows),
            StatisticReconciliationActualCanonical.Boolean(targetMatches),
            StatisticReconciliationActualCanonical.Boolean(requestMatches),
            StatisticReconciliationActualCanonical.Boolean(pagingValid),
            StatisticReconciliationActualCanonical.Boolean(uniqueRows),
            StatisticReconciliationActualCanonical.Boolean(rowSemanticsValid),
            TotalsSha(totals),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_PAGE_ROWS_V1",
                rowObservations.Select(row => row.RowSemanticSha256)));

        return new StatisticReconciliationActualApiPageObservation(
            selector.Page,
            selector.PageSize,
            page.TotalPages,
            page.ReturnedRows,
            totalRows,
            totals,
            rowObservations,
            etag,
            generationId,
            generationSha,
            targetMatches,
            requestMatches,
            pagingValid,
            uniqueRows,
            rowSemanticsValid,
            pageSha);
    }

    private static ImmutableArray<StatisticReconciliationActualApiTotalValue> NormalizeTotals(
        ImmutableArray<StatisticReconciliationActualApiTotalValue> ownerTotals)
    {
        if (ownerTotals.IsDefault || ownerTotals.Length == 0 || ownerTotals.Length > MaxTotals)
            throw Fail("API_TOTALS_INVALID");
        var builder = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualApiTotalValue>(ownerTotals.Length);
        foreach (var total in ownerTotals)
        {
            if (total is null)
                throw Fail("API_TOTAL_REQUIRED");
            var name = StatisticReconciliationActualCanonical.Required(
                total.Name,
                "API_TOTAL_NAME",
                64);
            if (!TotalName.IsMatch(name) || name == "totalPages")
                throw Fail("API_TOTAL_NAME_INVALID");
            var valueType = ExactUpper(total.ValueType, "API_TOTAL_VALUE_TYPE");
            string canonical;
            switch (valueType)
            {
                case "INTEGER":
                    if (!long.TryParse(
                            total.CanonicalValue,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var integer) ||
                        integer < 0)
                        throw Fail("API_TOTAL_INTEGER_INVALID");
                    canonical = StatisticReconciliationActualCanonical.Integer(integer);
                    break;
                case "DECIMAL":
                    if (!decimal.TryParse(
                            total.CanonicalValue,
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture,
                            out var decimalValue))
                        throw Fail("API_TOTAL_DECIMAL_INVALID");
                    canonical = StatisticReconciliationActualCanonical.Number(decimalValue);
                    break;
                default:
                    throw Fail("API_TOTAL_VALUE_TYPE_INVALID");
            }
            if (!StringComparer.Ordinal.Equals(canonical, total.CanonicalValue))
                throw Fail("API_TOTAL_VALUE_NON_CANONICAL");
            builder.Add(new StatisticReconciliationActualApiTotalValue(
                name,
                valueType,
                canonical));
        }
        var totals = builder
            .OrderBy(total => total.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        for (var index = 1; index < totals.Length; index++)
        {
            if (StringComparer.Ordinal.Equals(totals[index - 1].Name, totals[index].Name))
                throw Fail("API_TOTAL_NAME_DUPLICATE");
        }
        _ = TotalRows(totals);
        return totals;
    }

    private static long TotalRows(
        ImmutableArray<StatisticReconciliationActualApiTotalValue> totals)
    {
        var value = totals.SingleOrDefault(total => total.Name == "totalRows")
                    ?? totals.SingleOrDefault(total => total.Name == "totalRowCount")
                    ?? throw Fail("API_TOTAL_ROWS_REQUIRED");
        if (value.ValueType != "INTEGER" ||
            !long.TryParse(
                value.CanonicalValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var totalRows))
            throw Fail("API_TOTAL_ROWS_INVALID");
        return totalRows;
    }

    private static bool StableTotals(
        ImmutableArray<StatisticReconciliationActualApiPageObservation> pages)
    {
        var baseline = TotalsSha(pages[0].FullFilterTotals);
        return pages.All(page => StringComparer.Ordinal.Equals(
            baseline,
            TotalsSha(page.FullFilterTotals)));
    }

    private static bool StableGeneration(
        ImmutableArray<StatisticReconciliationActualApiPageObservation> pages)
    {
        var first = pages[0];
        return pages.All(page =>
            StringComparer.Ordinal.Equals(page.ETag, first.ETag) &&
            StringComparer.Ordinal.Equals(page.GenerationId, first.GenerationId) &&
            StringComparer.Ordinal.Equals(page.GenerationSha256, first.GenerationSha256));
    }

    private static bool StableOverlap(
        ImmutableArray<StatisticReconciliationActualApiPageObservation> pages)
    {
        var observed = new Dictionary<int, (string Identity, string Sha)>(
            pages.Sum(x => x.Rows.Length));
        var identityOrdinals =
            new Dictionary<string, (int Ordinal, string Sha)>(
                pages.Sum(x => x.Rows.Length),
                StringComparer.Ordinal);
        foreach (var page in pages)
        {
            foreach (var row in page.Rows)
            {
                if (observed.TryGetValue(row.AbsoluteOrdinal, out var prior) &&
                    (!StringComparer.Ordinal.Equals(prior.Identity, row.Identity) ||
                     !StringComparer.Ordinal.Equals(prior.Sha, row.RowSemanticSha256)))
                    return false;
                if (identityOrdinals.TryGetValue(
                        row.Identity,
                        out var identityPrior) &&
                    (identityPrior.Ordinal != row.AbsoluteOrdinal ||
                     !StringComparer.Ordinal.Equals(
                         identityPrior.Sha,
                         row.RowSemanticSha256)))
                    return false;
                observed[row.AbsoluteOrdinal] =
                    (row.Identity, row.RowSemanticSha256);
                identityOrdinals[row.Identity] =
                    (row.AbsoluteOrdinal, row.RowSemanticSha256);
            }
        }
        return true;
    }

    private static string TotalsSha(
        ImmutableArray<StatisticReconciliationActualApiTotalValue> totals)
        => StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_API_FULL_FILTER_TOTALS_V1",
            totals.Select(total => StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_API_TOTAL_V1",
                total.Name,
                total.ValueType,
                total.CanonicalValue)));

    private static string RequestSha(
        NormalizedRequest request,
        StatisticReconciliationActualApiAuthorizationContext authority,
        StatisticReconciliationActualApiPageSelector selector)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_REQUEST_V1",
            request.Surface,
            request.RouteId,
            request.WorkId,
            request.ScopeAssignmentId,
            request.DynamicFormTemplateId,
            request.OwnerResultId,
            request.FilterSha256,
            authority.AuthorizationSnapshotSha256,
            StatisticReconciliationActualCanonical.Integer(selector.Page),
            StatisticReconciliationActualCanonical.Integer(selector.PageSize));

    private static string RouteId(string surface)
        => surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                "POST:/api/work-report-field-statistics/summary",
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                "POST:/api/work-report-table-statistics/summary",
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                "POST:/api/work-report-label-statistics/summary",
            StatisticReconciliationActualApiSurfaces.BasicSource =>
                "POST:/api/work-assignment-basic-summary/summary#sourcesPage",
            StatisticReconciliationActualApiSurfaces.P9Diff =>
                "GET:/api/work-report-statistic-diffs/assignments/{assignmentId}/templates/{templateId}/results/{resultId}",
            _ => throw Fail("API_SURFACE_UNSUPPORTED")
        };

    private static string ExactUpper(string? value, string name)
    {
        var canonical = StatisticReconciliationActualCanonical.Required(value, name);
        if (!StringComparer.Ordinal.Equals(canonical, canonical.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL");
        return canonical;
    }

    private static string? NormalizeETag(string? value)
    {
        if (value is null)
            return null;
        var canonical = StatisticReconciliationActualCanonical.Required(value, "API_ETAG", 256);
        if (canonical.StartsWith("W/", StringComparison.Ordinal) ||
            canonical.Length < 3 || canonical[0] != '"' || canonical[^1] != '"')
            throw Fail("API_ETAG_INVALID");
        return canonical;
    }

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);

    private sealed record NormalizedRequest(
        string Surface,
        string RouteId,
        string WorkId,
        string ScopeAssignmentId,
        string DynamicFormTemplateId,
        string? OwnerResultId,
        string CanonicalFilterJson,
        string FilterSha256,
        long ExpectedTotalRows,
        ImmutableArray<StatisticReconciliationActualApiPageSelector> Pages);
}
