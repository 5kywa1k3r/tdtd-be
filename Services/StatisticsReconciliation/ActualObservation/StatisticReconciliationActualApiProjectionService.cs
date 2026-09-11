using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualApiProjectionService(
    MongoDbContext context,
    MeAccessor me,
    IStatisticReconciliationActualPinnedDirectResultService directResults,
    IWorkReportStatisticDiffService diffResults,
    IStatisticReconciliationActualBasicApiReadOwner basicResults)
    : StatisticReconciliationActualApiEndpointService
{
    private static readonly JsonSerializerOptions RowJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        MaxDepth = 128,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal override async Task<
        StatisticReconciliationActualApiAuthorizationContext?>
        AuthorizeBeforeExistenceAsync(
            string? workId,
            string? scopeAssignmentId,
            CancellationToken cancellationToken)
    {
        if (!ExactObjectId(workId) || !ExactObjectId(scopeAssignmentId))
            return null;

        var actor = me.RequireMe();
        if (actor.IsDeleted || !ExactObjectId(actor.Id))
            return null;

        var isAdmin = RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor);
        var filters = Builders<WorkAssignment>.Filter;
        var target = filters.Eq(item => item.Id, scopeAssignmentId!) &
                     filters.Eq(item => item.WorkId, workId!) &
                     filters.Eq(item => item.IsDeleted, false) &
                     filters.Eq(item => item.IsActive, true);
        if (!isAdmin)
        {
            target &= filters.Or(
                filters.Eq(item => item.CreatedByUserId, actor.Id),
                filters.AnyEq(item => item.LeaderWatcherUserIds, actor.Id),
                filters.ElemMatch(
                    item => item.Assignees,
                    assignee => assignee.UserId == actor.Id));
        }

        // This is the only target query. Missing and unauthorized scopes both
        // return the same hidden result and no result owner is inspected.
        var authorizedScopeId = await context.WorkAssignments
            .Find(target)
            .Project(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (authorizedScopeId is null)
            return null;

        var permissionCodes = PermissionCodes(actor).ToImmutableArray();
        const long rowCountBeforeRedaction = 1;
        const long rowCountAfterRedaction = 1;
        var authorizationSha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                actor.Id,
                workId!,
                scopeAssignmentId!,
                permissionCodes,
                rowCountBeforeRedaction,
                rowCountAfterRedaction);
        return new StatisticReconciliationActualApiAuthorizationContext(
            actor.Id,
            workId!,
            scopeAssignmentId!,
            permissionCodes,
            rowCountBeforeRedaction,
            rowCountAfterRedaction,
            authorizationSha,
            true);
    }

    internal override async Task<StatisticReconciliationActualApiOwnerPage>
        ReadPageProjectionAsync(
            StatisticReconciliationActualApiAuthorizationContext authorization,
            StatisticReconciliationActualApiOwnerPageQuery query,
            CancellationToken cancellationToken)
    {
        ValidateQueryBinding(authorization, query);
        using var filterDocument = StatisticReconciliationActualJson.ParseStrict(
            query.CanonicalFilterJson,
            "API_ENDPOINT_FILTER_JSON");
        if (filterDocument.RootElement.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                StatisticReconciliationActualJson.Canonicalize(
                    filterDocument.RootElement),
                query.CanonicalFilterJson,
                StringComparison.Ordinal) ||
            !string.Equals(
                StatisticReconciliationActualJson.RawSha256(
                    query.CanonicalFilterJson),
                query.FilterSha256,
                StringComparison.Ordinal))
        {
            throw BadRequest("API_FILTER_BINDING_INVALID");
        }

        return query.Surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                await ReadFieldAsync(
                    authorization,
                    query,
                    new FilterReader(
                        filterDocument.RootElement,
                        FieldFilterProperties),
                    cancellationToken),
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                await ReadTableAsync(
                    authorization,
                    query,
                    new FilterReader(
                        filterDocument.RootElement,
                        TableFilterProperties),
                    cancellationToken),
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                await ReadLabelAsync(
                    authorization,
                    query,
                    new FilterReader(
                        filterDocument.RootElement,
                        LabelFilterProperties),
                    cancellationToken),
            StatisticReconciliationActualApiSurfaces.BasicSource =>
                await ReadBasicAsync(
                    authorization,
                    query,
                    new FilterReader(
                        filterDocument.RootElement,
                        BasicFilterProperties),
                    cancellationToken),
            StatisticReconciliationActualApiSurfaces.P9Diff =>
                await ReadDiffAsync(
                    authorization,
                    query,
                    new FilterReader(
                        filterDocument.RootElement,
                        DiffFilterProperties),
                    cancellationToken),
            _ => throw BadRequest("API_SURFACE_UNSUPPORTED")
        };
    }

    private async Task<StatisticReconciliationActualApiOwnerPage> ReadFieldAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        FilterReader filter,
        CancellationToken cancellationToken)
    {
        var ownerRunId = RequiredOwnerResult(query, "API_DIRECT_RUN_ID");
        var response = await directResults.ReadFieldPinnedAsync(
            new FieldStatisticSummaryRequest
            {
                WorkId = query.WorkId,
                ScopeType = "ASSIGNMENT",
                ScopeId = query.ScopeAssignmentId,
                DynamicFormTemplateId = query.DynamicFormTemplateId,
                PeriodInstanceKey = filter.RequiredString("periodInstanceKey"),
                FieldId = filter.OptionalString("fieldId"),
                FieldKey = filter.OptionalString("fieldKey"),
                StatisticLabelCode = filter.OptionalString("statisticLabelCode"),
                FieldType = filter.OptionalString("fieldType"),
                BucketKey = filter.OptionalString("bucketKey"),
                ShowInTree = filter.OptionalBoolean("showInTree"),
                ShowInDetail = filter.OptionalBoolean("showInDetail"),
                PeriodKey = filter.OptionalString("periodKey"),
                PeriodKeyFrom = filter.OptionalString("periodKeyFrom"),
                PeriodKeyTo = filter.OptionalString("periodKeyTo"),
                ReportStatus = filter.OptionalInt32("reportStatus"),
                IncludeDrilldown = false,
                Page = query.Page,
                PageSize = query.PageSize
            },
            ownerRunId,
            cancellationToken);
        var generation = DirectGeneration(response.Metadata, ownerRunId);
        return Page(
            authorization,
            query,
            generation,
            response.TotalRows,
            response.ReturnedRows,
            Totals(
                IntegerTotal("totalRows", response.TotalRows),
                IntegerTotal("totalValueCount", response.TotalValueCount),
                DecimalTotal("totalSum", response.TotalSum),
                IntegerTotal("totalReportCount", response.TotalReportCount)),
            response.Rows.Select(row => OwnerRow(row, FieldIdentity(row))));
    }

    private async Task<StatisticReconciliationActualApiOwnerPage> ReadTableAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        FilterReader filter,
        CancellationToken cancellationToken)
    {
        var ownerRunId = RequiredOwnerResult(query, "API_DIRECT_RUN_ID");
        var response = await directResults.ReadTablePinnedAsync(
            new TableStatisticSummaryRequest
            {
                WorkId = query.WorkId,
                ScopeType = "ASSIGNMENT",
                ScopeId = query.ScopeAssignmentId,
                DynamicFormTemplateId = query.DynamicFormTemplateId,
                PeriodInstanceKey = filter.RequiredString("periodInstanceKey"),
                DynamicExcelTemplateId = filter.OptionalString(
                    "dynamicExcelTemplateId"),
                BlockId = filter.OptionalString("blockId"),
                TableMode = filter.OptionalString("tableMode"),
                MetricKey = filter.OptionalString("metricKey"),
                MetricLabelCode = filter.OptionalString("metricLabelCode"),
                DataType = filter.OptionalString("dataType"),
                BucketKey = filter.OptionalString("bucketKey"),
                PeriodKey = filter.OptionalString("periodKey"),
                ReportStatus = filter.OptionalInt32("reportStatus"),
                IncludeDrilldown = false,
                Page = query.Page,
                PageSize = query.PageSize
            },
            ownerRunId,
            cancellationToken);
        var generation = DirectGeneration(response.Metadata, ownerRunId);
        return Page(
            authorization,
            query,
            generation,
            response.TotalRows,
            response.ReturnedRows,
            Totals(
                IntegerTotal("totalRows", response.TotalRows),
                IntegerTotal("totalValueCount", response.TotalValueCount),
                DecimalTotal("totalSum", response.TotalSum),
                IntegerTotal("totalReportCount", response.TotalReportCount)),
            response.Rows.Select(row => OwnerRow(row, TableIdentity(row))));
    }

    private async Task<StatisticReconciliationActualApiOwnerPage> ReadLabelAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        FilterReader filter,
        CancellationToken cancellationToken)
    {
        var ownerRunId = RequiredOwnerResult(query, "API_DIRECT_RUN_ID");
        var response = await directResults.ReadLabelPinnedAsync(
            new LabelStatisticSummaryRequest
            {
                WorkId = query.WorkId,
                ScopeType = "ASSIGNMENT",
                ScopeId = query.ScopeAssignmentId,
                DynamicFormTemplateId = query.DynamicFormTemplateId,
                PeriodInstanceKey = filter.RequiredString("periodInstanceKey"),
                DynamicExcelTemplateId = filter.OptionalString(
                    "dynamicExcelTemplateId"),
                LabelCode = filter.OptionalString("labelCode"),
                PeriodKey = filter.OptionalString("periodKey"),
                ReportStatus = filter.OptionalInt32("reportStatus"),
                IncludeDrilldown = false,
                Page = query.Page,
                PageSize = query.PageSize
            },
            ownerRunId,
            cancellationToken);
        var generation = DirectGeneration(response.Metadata, ownerRunId);
        return Page(
            authorization,
            query,
            generation,
            response.TotalRows,
            response.ReturnedRows,
            Totals(
                IntegerTotal("totalRows", response.TotalRows),
                IntegerTotal("totalRowCount", response.TotalRowCount),
                IntegerTotal("totalReportCount", response.TotalReportCount)),
            response.Rows.Select(row => OwnerRow(row, LabelIdentity(row))));
    }

    private async Task<StatisticReconciliationActualApiOwnerPage> ReadBasicAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        FilterReader filter,
        CancellationToken cancellationToken)
    {
        var snapshotId = RequiredOwnerResult(query, "API_BASIC_SNAPSHOT_ID");
        var response = await basicResults.ReadSourcesPageAsync(
            snapshotId,
            query.WorkId,
            query.ScopeAssignmentId,
            query.DynamicFormTemplateId,
            new StatisticReconciliationActualBasicApiSourceFilter(
                filter.OptionalString("q"),
                filter.OptionalString("periodKey"),
                filter.OptionalString("unitId"),
                filter.OptionalString("assigneeUserId")),
            query.Page,
            query.PageSize,
            cancellationToken);
        var generation = new GenerationPin(
            response.SnapshotId,
            response.GenerationSha256,
            StrongETag(response.GenerationSha256));
        return Page(
            authorization,
            query,
            generation,
            response.TotalRows,
            response.Rows.Count,
            Totals(IntegerTotal("totalRows", response.TotalRows)),
            response.Rows.Select(row => OwnerRow(
                row,
                StatisticReconciliationActualCanonical.Required(
                    row.WorkAssignmentReportId,
                    "API_BASIC_SOURCE_ROW_ID"))));
    }

    private async Task<StatisticReconciliationActualApiOwnerPage> ReadDiffAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        FilterReader filter,
        CancellationToken cancellationToken)
    {
        filter.RequireEmpty();
        var resultId = RequiredOwnerResult(query, "API_DIFF_RESULT_ID");
        var response = await diffResults.GetP9ResultAsync(
            query.ScopeAssignmentId,
            query.DynamicFormTemplateId,
            resultId,
            query.Page,
            query.PageSize,
            cancellationToken);
        if (!string.Equals(
                response.Status,
                P9StatisticDiffResultStatuses.Completed,
                StringComparison.Ordinal) ||
            !response.IsCurrent || !response.IsFresh || response.IsDirty ||
            response.CompletedAtUtc is null)
        {
            throw Stale("API_DIFF_RESULT_NOT_CURRENT");
        }

        string resultSha;
        try
        {
            resultSha = StatisticReconciliationActualCanonical.Sha256(
                response.ResultHash,
                "API_DIFF_RESULT_SHA256");
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw Stale("API_DIFF_RESULT_HASH_INVALID");
        }
        if (!string.Equals(response.ResultId, resultId, StringComparison.Ordinal) ||
            !string.Equals(
                response.AssignmentId,
                query.ScopeAssignmentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                response.DynamicFormTemplateId,
                query.DynamicFormTemplateId,
                StringComparison.Ordinal))
        {
            throw Stale("API_DIFF_RESULT_BINDING_DRIFT");
        }

        var generation = new GenerationPin(
            response.ResultId,
            resultSha,
            StrongETag(resultSha));
        return Page(
            authorization,
            query,
            generation,
            response.TotalRowCount,
            response.Rows.Count,
            Totals(
                IntegerTotal("totalRows", response.TotalRowCount),
                IntegerTotal("totalRowCount", response.TotalRowCount),
                IntegerTotal("totalEqualRowCount", response.EqualRowCount),
                IntegerTotal("totalChangedRowCount", response.ChangedRowCount)),
            response.Rows.Select(row => OwnerRow(
                row,
                StatisticReconciliationActualCanonical.Required(
                    row.RowId,
                    "API_DIFF_ROW_ID"))));
    }

    private static StatisticReconciliationActualApiOwnerPage Page(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        GenerationPin generation,
        long totalRows,
        int returnedRows,
        ImmutableArray<StatisticReconciliationActualApiTotalValue> totals,
        IEnumerable<StatisticReconciliationActualApiOwnerRow> rows)
    {
        if (totalRows < 0 || returnedRows < 0)
            throw Stale("API_OWNER_COUNTS_INVALID");
        var ownerRows = rows.ToImmutableArray();
        if (ownerRows.Length != returnedRows)
            throw Stale("API_OWNER_RETURNED_ROWS_INVALID");
        var totalPages = totalRows == 0
            ? 0
            : checked((int)Math.Ceiling(totalRows / (double)query.PageSize));
        return new StatisticReconciliationActualApiOwnerPage(
            query.Surface,
            query.RouteId,
            query.WorkId,
            query.ScopeAssignmentId,
            query.DynamicFormTemplateId,
            query.OwnerResultId,
            query.FilterSha256,
            authorization.AuthorizationSnapshotSha256,
            query.RequestSha256,
            generation.ETag,
            generation.Id,
            generation.Sha256,
            query.Page,
            query.PageSize,
            totalPages,
            returnedRows,
            totals,
            ownerRows);
    }

    private static void ValidateQueryBinding(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);
        var surface = RequiredExact(query.Surface, "API_SURFACE");
        var workId = RequiredExact(query.WorkId, "API_WORK_ID");
        var scopeId = RequiredExact(
            query.ScopeAssignmentId,
            "API_SCOPE_ASSIGNMENT_ID");
        _ = RequiredExact(
            query.DynamicFormTemplateId,
            "API_DYNAMIC_FORM_TEMPLATE_ID");
        var ownerResultId = StatisticReconciliationActualCanonical.Optional(
            query.OwnerResultId,
            "API_OWNER_RESULT_ID");
        if (!string.Equals(ownerResultId, query.OwnerResultId,
                StringComparison.Ordinal) ||
            !StatisticReconciliationActualApiSurfaces.IsSupported(surface) ||
            !string.Equals(surface, surface.ToUpperInvariant(),
                StringComparison.Ordinal) ||
            !string.Equals(
                StatisticReconciliationActualApiProtocol.RouteId(surface),
                query.RouteId,
                StringComparison.Ordinal) ||
            !authorization.IsAuthorized ||
            !string.Equals(authorization.WorkId, workId,
                StringComparison.Ordinal) ||
            !string.Equals(authorization.ScopeAssignmentId, scopeId,
                StringComparison.Ordinal))
        {
            throw BadRequest("API_PAGE_TARGET_BINDING_INVALID");
        }

        var suppliedAuthorizationSha = Sha256(
            query.AuthorizationSnapshotSha256,
            "API_AUTHORIZATION_SNAPSHOT_SHA256");
        if (!string.Equals(
                suppliedAuthorizationSha,
                authorization.AuthorizationSnapshotSha256,
                StringComparison.Ordinal))
        {
            throw Stale("API_AUTHORIZATION_SNAPSHOT_STALE");
        }
        if (query.Page is < 0 or > StatisticReconciliationActualApiObservationAdapter.MaxPage ||
            query.PageSize is < 1 or > StatisticReconciliationActualApiObservationAdapter.MaxPageSize)
        {
            throw BadRequest("API_PAGE_INVALID");
        }

        var suppliedFilterSha = Sha256(
            query.FilterSha256,
            "API_FILTER_SHA256");
        var suppliedRequestSha = Sha256(
            query.RequestSha256,
            "API_REQUEST_SHA256");
        var expectedRequestSha = StatisticReconciliationActualApiProtocol
            .RequestSha(query);
        if (!string.Equals(suppliedFilterSha, query.FilterSha256,
                StringComparison.Ordinal) ||
            !string.Equals(suppliedRequestSha, expectedRequestSha,
                StringComparison.Ordinal))
        {
            throw BadRequest("API_REQUEST_BINDING_INVALID");
        }
    }

    private static GenerationPin DirectGeneration(
        P9DirectResultMetadata metadata,
        string expectedRunId)
    {
        if (!string.Equals(metadata.State, P9ResultStates.Ready,
                StringComparison.Ordinal) ||
            metadata.Publications is null || metadata.Publications.Count != 1 ||
            !string.Equals(
                metadata.Publications[0].RunId,
                expectedRunId,
                StringComparison.Ordinal))
        {
            throw Stale("API_DIRECT_PUBLICATION_NOT_READY");
        }
        var pins = metadata.Publications
            .OrderBy(item => item.GenerationId, StringComparer.Ordinal)
            .ThenBy(item => item.RunId, StringComparer.Ordinal)
            .Select(item =>
            {
                var generationId = RequiredExact(
                    item.GenerationId,
                    "API_DIRECT_GENERATION_ID");
                var generationHash = Sha256(
                    item.GenerationHash,
                    "API_DIRECT_GENERATION_SHA256");
                return new
                {
                    GenerationId = generationId,
                    Semantic = StatisticReconciliationActualCanonical.Hash(
                        "P10_ACTUAL_API_DIRECT_PUBLICATION_PIN_V1",
                        item.RunId,
                        generationId,
                        generationHash,
                        StatisticReconciliationActualCanonical.Integer(
                            item.DirectSourceRevision),
                        StatisticReconciliationActualCanonical.Integer(
                            item.DirectPublicationRevision))
                };
            })
            .ToArray();
        var generationSha = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_API_DIRECT_GENERATION_V1",
            pins.Select(item => item.Semantic));
        var generationId = pins.Length == 1
            ? pins[0].GenerationId
            : StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_DIRECT_GENERATION_IDS_V1",
                pins.Select(item => item.GenerationId));
        return new GenerationPin(
            generationId,
            generationSha,
            StrongETag(generationSha));
    }

    private static StatisticReconciliationActualApiOwnerRow OwnerRow<T>(
        T row,
        string identity)
    {
        identity = RequiredExact(identity, "API_ROW_IDENTITY");
        var serialized = JsonSerializer.Serialize(row, RowJsonOptions);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            serialized,
            "API_OWNER_ROW_JSON");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        return new StatisticReconciliationActualApiOwnerRow(
            identity,
            canonical,
            StatisticReconciliationActualApiObservationAdapter.RowSha(
                identity,
                canonical));
    }

    private static string FieldIdentity(FieldStatisticSummaryRow row)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_DIRECT_FIELD_ROW_ID_V1",
            row.WorkId,
            row.ScopeType,
            row.ScopeId,
            row.RootAssignmentId,
            row.DynamicFormTemplateId,
            row.FieldId,
            row.FieldKey,
            row.FieldType,
            row.BucketKey,
            row.PeriodKey,
            row.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(row.ReportStatus));

    private static string TableIdentity(TableStatisticSummaryRow row)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_DIRECT_TABLE_ROW_ID_V1",
            row.WorkId,
            row.ScopeType,
            row.ScopeId,
            row.RootAssignmentId,
            row.DynamicFormTemplateId,
            row.DynamicExcelTemplateId,
            row.BlockId,
            row.TableMode,
            row.MetricKey,
            row.RowKey,
            row.ColumnKey,
            row.DataType,
            row.BucketKey,
            row.PeriodKey,
            row.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(row.ReportStatus));

    private static string LabelIdentity(LabelStatisticSummaryRow row)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_DIRECT_LABEL_ROW_ID_V1",
            row.WorkId,
            row.ScopeType,
            row.ScopeId,
            row.RootAssignmentId,
            row.DynamicFormTemplateId,
            row.DynamicExcelTemplateId,
            row.BlockId,
            row.LabelCode,
            row.PeriodKey,
            row.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(row.ReportStatus));

    private static ImmutableArray<StatisticReconciliationActualApiTotalValue>
        Totals(params StatisticReconciliationActualApiTotalValue[] values)
        => values.OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToImmutableArray();

    private static StatisticReconciliationActualApiTotalValue IntegerTotal(
        string name,
        long value)
        => new(
            name,
            "INTEGER",
            StatisticReconciliationActualCanonical.Integer(value));

    private static StatisticReconciliationActualApiTotalValue DecimalTotal(
        string name,
        decimal value)
        => new(
            name,
            "DECIMAL",
            StatisticReconciliationActualCanonical.Number(value));

    private static string RequiredOwnerResult(
        StatisticReconciliationActualApiOwnerPageQuery query,
        string name)
        => StatisticReconciliationActualCanonical.Required(
            query.OwnerResultId,
            name);

    private static string StrongETag(string sha256)
        => $"\"sha256-{sha256}\"";

    private static string RequiredExact(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string Sha256(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static bool ExactObjectId(string? value)
        => value is not null &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           MongoDB.Bson.ObjectId.TryParse(value, out _);

    private static IEnumerable<string> PermissionCodes(
        tdtd_be.DTOs.Auth.MeResponse actor)
    {
        var result = new HashSet<string>(StringComparer.Ordinal)
        {
            "STATISTICS_READ"
        };
        foreach (var role in actor.Roles ?? [])
        {
            if (!string.IsNullOrWhiteSpace(role))
                result.Add($"ROLE_{PermissionPart(role)}");
        }
        if (!string.IsNullOrWhiteSpace(actor.AccountKind))
            result.Add($"ACCOUNT_KIND_{PermissionPart(actor.AccountKind)}");
        if (RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor))
            result.Add("RECONCILIATION_DETAIL");
        return result.OrderBy(value => value, StringComparer.Ordinal);
    }

    private static string PermissionPart(string value)
    {
        var characters = value.Trim().ToUpperInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character)
                ? character
                : '_')
            .ToArray();
        return new string(characters);
    }

    private static StatisticReconciliationActualApiEndpointException BadRequest(
        string reason)
        => new(StatusCodes.Status400BadRequest, reason);

    private static StatisticReconciliationActualApiEndpointException Stale(
        string reason)
        => new(StatusCodes.Status409Conflict, reason);

    private static readonly string[] FieldFilterProperties =
    [
        "periodInstanceKey", "fieldId", "fieldKey", "statisticLabelCode",
        "fieldType", "bucketKey", "showInTree", "showInDetail", "periodKey",
        "periodKeyFrom", "periodKeyTo", "reportStatus"
    ];

    private static readonly string[] TableFilterProperties =
    [
        "periodInstanceKey", "dynamicExcelTemplateId", "blockId", "tableMode",
        "metricKey", "metricLabelCode", "dataType", "bucketKey", "periodKey",
        "reportStatus"
    ];

    private static readonly string[] LabelFilterProperties =
    [
        "periodInstanceKey", "dynamicExcelTemplateId", "labelCode", "periodKey",
        "reportStatus"
    ];

    private static readonly string[] BasicFilterProperties =
    [
        "q", "periodKey", "unitId", "assigneeUserId"
    ];

    private static readonly string[] DiffFilterProperties = [];

    private sealed record GenerationPin(
        string Id,
        string Sha256,
        string ETag);

    private sealed class FilterReader
    {
        private readonly IReadOnlyDictionary<string, JsonElement> _properties;

        internal FilterReader(JsonElement value, IEnumerable<string> allowed)
        {
            var allowedNames = allowed.ToHashSet(StringComparer.Ordinal);
            var properties = value.EnumerateObject().ToArray();
            var unknown = properties
                .Where(property => !allowedNames.Contains(property.Name))
                .Select(property => property.Name)
                .FirstOrDefault();
            if (unknown is not null)
                throw BadRequest($"API_FILTER_PROPERTY_UNSUPPORTED:{unknown}");
            _properties = properties.ToDictionary(
                property => property.Name,
                property => property.Value,
                StringComparer.Ordinal);
        }

        internal void RequireEmpty()
        {
            if (_properties.Count != 0)
                throw BadRequest("API_DIFF_FILTER_MUST_BE_EMPTY");
        }

        internal string RequiredString(string name)
            => OptionalString(name) ??
               throw BadRequest($"API_FILTER_{name}_REQUIRED");

        internal string? OptionalString(string name)
        {
            if (!_properties.TryGetValue(name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw BadRequest($"API_FILTER_{name}_TYPE_INVALID");
            try
            {
                return StatisticReconciliationActualCanonical.Required(
                    value.GetString(),
                    $"API_FILTER_{name}");
            }
            catch (StatisticReconciliationActualObservationException)
            {
                throw BadRequest($"API_FILTER_{name}_INVALID");
            }
        }

        internal bool? OptionalBoolean(string name)
        {
            if (!_properties.TryGetValue(name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw BadRequest($"API_FILTER_{name}_TYPE_INVALID")
            };
        }

        internal int? OptionalInt32(string name)
        {
            if (!_properties.TryGetValue(name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
                return null;
            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var result))
                throw BadRequest($"API_FILTER_{name}_TYPE_INVALID");
            return result;
        }
    }
}
