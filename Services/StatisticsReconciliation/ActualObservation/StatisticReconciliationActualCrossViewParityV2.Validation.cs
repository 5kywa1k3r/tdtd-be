using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static ValidatedApiBase ValidateApiBase(
        NormalizedPlan plan,
        StatisticReconciliationActualCrossViewParityV2Base? baseline)
    {
        if (baseline is null || !Eq(baseline.SchemaVersion, BaseSchema) ||
            baseline.Api is null)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PreimageMissing);
        var value = baseline.Api;
        RequireApiBaseTarget(plan, value);
        if (!Eq(value.SchemaVersion, BaseSchema) ||
            !Eq(value.Surface, plan.ApiSurface) ||
            !Eq(value.OwnerResultId, plan.ApiOwnerResultId) ||
            !Eq(value.GenerationId, plan.ApiGenerationId) ||
            !Eq(value.GenerationSha256, plan.ApiGenerationSha256) ||
            !Eq(value.CanonicalFilterJson, plan.CanonicalApiFilterJson) ||
            !Eq(value.FilterSha256, plan.ApiFilterSha256) ||
            value.Rows.IsDefault || value.FullFilterTotals.IsDefault ||
            value.Rows.Length != plan.ApiExpectedTotalRows)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .BaseInvalid);
        }
        var rows = value.Rows.Select((row, index) =>
        {
            if (row is null || row.AbsoluteOrdinal != index)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .BaseInvalid);
            return BaseApiRow(
                row.AbsoluteOrdinal,
                row.Identity,
                row.CanonicalRowJson,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .BaseInvalid);
        }).ToImmutableArray();
        if (rows.Select(row => row.Identity).Distinct(StringComparer.Ordinal)
                .Count() != rows.Length)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .BaseInvalid);
        var totals = NormalizeApiTotals(
            value.FullFilterTotals,
            StatisticReconciliationActualCrossViewParityV2Failures
                .BaseInvalid);
        RequireApiSemantics(plan, rows, totals,
            StatisticReconciliationActualCrossViewParityV2Failures.BaseInvalid);
        var rowsSha = HS(
            "P10_ACTUAL_CROSS_VIEW_API_BASE_ROWS_V2",
            rows.Select(row => row.SemanticSha256));
        var totalsSha = ApiTotalsSha(totals);
        var filterRelation = H(
            "P10_ACTUAL_CROSS_VIEW_API_BASE_FILTER_V2",
            value.CanonicalFilterJson,
            value.FilterSha256);
        var projectionSha = H(
            "P10_ACTUAL_CROSS_VIEW_API_BASE_V2",
            BaseSchema,
            value.WorkId,
            value.ScopeAssignmentId,
            value.DynamicFormTemplateId,
            value.PeriodInstanceKey,
            value.AuthorizationSnapshotSha256,
            plan.ApiSurface,
            value.OwnerResultId,
            value.GenerationId,
            value.GenerationSha256,
            value.CanonicalFilterJson,
            value.FilterSha256,
            rowsSha,
            I(rows.Length),
            totalsSha);
        return new(
            rows, totals, rowsSha, totalsSha, filterRelation, projectionSha);
    }

    private static ValidatedExportBase ValidateExportBase(
        NormalizedPlan plan,
        StatisticReconciliationActualCrossViewParityV2Base? baseline)
    {
        if (baseline is null || !Eq(baseline.SchemaVersion, BaseSchema) ||
            baseline.Export is null)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PreimageMissing);
        var value = baseline.Export;
        RequireExportBaseTarget(plan, value);
        if (!Eq(value.SchemaVersion, BaseSchema) ||
            !Eq(value.ResultKind, plan.ExportResultKind) ||
            !Eq(value.ResultId, plan.ExportResultId) ||
            !Eq(value.ResultSha256, plan.ExportResultSha256) ||
            !Eq(value.ConfigSha256, plan.ExportConfigSha256) ||
            !Eq(value.SourceSha256, plan.ExportSourceSha256) ||
            value.LifecycleRevision != plan.ExportLifecycleRevision ||
            !Eq(value.PeriodInstanceKey, plan.PeriodInstanceKey) ||
            !Eq(value.CanonicalFilterJson, plan.CanonicalExportFilterJson) ||
            !Eq(value.FilterSha256, plan.ExportFilterSha256) ||
            value.Columns.IsDefaultOrEmpty || value.Rows.IsDefault ||
            value.FullFilterTotals.IsDefault ||
            value.Rows.Length > MaximumRows)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .BaseInvalid);
        }
        ValidateColumns(value.Columns,
            StatisticReconciliationActualCrossViewParityV2Failures.BaseInvalid);
        var rows = value.Rows.Select((row, index) =>
        {
            if (row is null || row.Ordinal != index + 1 ||
                string.IsNullOrWhiteSpace(row.CanonicalSourceRowJson) ||
                row.Cells.IsDefault || row.Cells.Length != value.Columns.Length)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .BaseInvalid);
            var sourceJson = CanonicalAny(
                row.CanonicalSourceRowJson,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .BaseInvalid);
            var cells = row.Cells.Select((cell, cellIndex) =>
                    ValidateCell(value.Columns[cellIndex], cell,
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .BaseInvalid))
                .ToImmutableArray();
            var rowSha = HS("P10_ACTUAL_EXPORT_ROW_V1",
                cells.Select(cell => cell.SemanticSha256));
            if (!Eq(row.RowSemanticSha256, rowSha))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .BaseInvalid);
            return new ValidatedExportRow(
                row.Ordinal, sourceJson, cells, rowSha);
        }).ToImmutableArray();
        var totals = ValidateTotals(
            value.FullFilterTotals,
            StatisticReconciliationActualCrossViewParityV2Failures.BaseInvalid);
        RequireExportBaseSemantics(plan, value.Columns, rows, totals,
            StatisticReconciliationActualCrossViewParityV2Failures.BaseInvalid);
        var rowsSha = HS("P10_ACTUAL_CROSS_VIEW_EXPORT_BASE_ROWS_V2",
            rows.Select(row => H(
                "P10_ACTUAL_CROSS_VIEW_EXPORT_BASE_ROW_V2",
                I(row.Ordinal), row.CanonicalSourceRowJson,
                row.SemanticSha256)));
        var totalsSha = ExportTotalsSha(totals);
        var columnsSha = HS(
            "P10_ACTUAL_CROSS_VIEW_EXPORT_BASE_COLUMNS_V2",
            value.Columns.Select(ColumnSha));
        var projectionSha = H(
            "P10_ACTUAL_CROSS_VIEW_EXPORT_BASE_V2",
            BaseSchema,
            value.WorkId,
            value.ScopeType,
            value.ScopeId,
            value.DynamicFormTemplateId,
            value.AuthorizationSnapshotSha256,
            value.ResultKind,
            value.ResultId,
            value.ResultSha256,
            value.ConfigSha256,
            value.SourceSha256,
            I(value.LifecycleRevision),
            value.PeriodInstanceKey,
            value.CanonicalFilterJson,
            value.FilterSha256,
            columnsSha,
            rowsSha,
            I(rows.Length),
            totalsSha);
        return new(
            value.Columns, rows, totals, rowsSha, totalsSha, projectionSha);
    }

    private static ValidatedApi ValidateApi(
        NormalizedPlan plan,
        StatisticReconciliationActualCrossViewParityV2Actual? actual)
    {
        if (actual is null || !Eq(actual.SchemaVersion, ActualSchema) ||
            actual.Api is null)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PreimageMissing);
        var capture = actual.Api;
        var route = StatisticReconciliationActualApiProtocol.RouteId(
            plan.ApiSurface!);
        if (!Eq(capture.Surface, plan.ApiSurface) ||
            !Eq(capture.RouteId, route) ||
            !Eq(capture.WorkId, plan.WorkId) ||
            !Eq(capture.ScopeAssignmentId, plan.ScopeId) ||
            !Eq(capture.DynamicFormTemplateId,
                plan.DynamicFormTemplateId) ||
            !Eq(capture.OwnerResultId, plan.ApiOwnerResultId) ||
            !Eq(capture.CanonicalFilterJson, plan.CanonicalApiFilterJson) ||
            !Eq(capture.FilterSha256, plan.ApiFilterSha256) ||
            !capture.FullFilterTotalsStable || !capture.GenerationStable ||
            !capture.OverlapStable || !capture.PagingContractsValid ||
            !Eq(capture.CaptureState,
                StatisticReconciliationActualApiCaptureStates.Ready) ||
            capture.CaptureReason is not null ||
            capture.Pages.IsDefault ||
            capture.Pages.Length != plan.ApiPageCount)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiInvalid);
        }
        ValidateAuthorization(plan, capture.Authorization);
        var rows = ImmutableArray.CreateBuilder<ValidatedApiRow>(
            checked((int)plan.ApiExpectedTotalRows));
        ImmutableArray<ValidatedTotal>? stableTotals = null;
        var pageShas = ImmutableArray.CreateBuilder<string>(
            capture.Pages.Length);
        var expectedPages = plan.ApiExpectedTotalRows == 0
            ? 0
            : checked((int)((plan.ApiExpectedTotalRows + plan.ApiPageSize - 1) /
                plan.ApiPageSize));
        for (var pageIndex = 0; pageIndex < capture.Pages.Length; pageIndex++)
        {
            var page = capture.Pages[pageIndex] ?? throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiInvalid);
            var returned = plan.ApiExpectedTotalRows <=
                (long)pageIndex * plan.ApiPageSize
                ? 0
                : (int)Math.Min(
                    plan.ApiPageSize,
                    plan.ApiExpectedTotalRows -
                    (long)pageIndex * plan.ApiPageSize);
            if (page.Page != pageIndex || page.PageSize != plan.ApiPageSize ||
                page.TotalPages != expectedPages || page.ReturnedRows != returned ||
                page.TotalRows != plan.ApiExpectedTotalRows ||
                !Eq(page.GenerationId, plan.ApiGenerationId) ||
                !Eq(page.GenerationSha256, plan.ApiGenerationSha256) ||
                !Eq(page.ETag,
                    $"\"sha256-{plan.ApiGenerationSha256}\"") ||
                !page.TargetBindingMatches || !page.RequestBindingMatches ||
                !page.PagingContractValid || !page.RowIdentitiesUnique ||
                !page.RowSemanticsValid || page.Rows.IsDefault ||
                page.Rows.Length != returned)
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ApiPartitionIncomplete);
            }
            var totals = NormalizeApiTotals(
                page.FullFilterTotals,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiInvalid);
            stableTotals ??= totals;
            if (!stableTotals.Value.SequenceEqual(totals))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .TotalsMismatch);
            var rowShas = ImmutableArray.CreateBuilder<string>(page.Rows.Length);
            for (var rowIndex = 0; rowIndex < page.Rows.Length; rowIndex++)
            {
                var item = page.Rows[rowIndex] ?? throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ApiInvalid);
                var ordinal = checked(pageIndex * plan.ApiPageSize + rowIndex);
                var row = BaseApiRow(
                    ordinal,
                    item.Identity,
                    item.CanonicalRowJson,
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ApiInvalid);
                var rowSha = StatisticReconciliationActualApiObservationAdapter
                    .RowSha(row.Identity, row.CanonicalJson);
                if (item.AbsoluteOrdinal != ordinal ||
                    !item.StoredSemanticMatches ||
                    !Eq(item.RowSemanticSha256, rowSha))
                {
                    throw Fail(
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .ApiInvalid);
                }
                rows.Add(row);
                rowShas.Add(rowSha);
            }
            var requestSha = StatisticReconciliationActualApiProtocol.RequestSha(
                plan.ApiSurface!, route, plan.WorkId, plan.ScopeId,
                plan.DynamicFormTemplateId, plan.ApiOwnerResultId,
                plan.ApiFilterSha256!, plan.AuthorizationSnapshotSha256,
                pageIndex, plan.ApiPageSize);
            var pageSha = H(
                "P10_ACTUAL_API_PAGE_V1",
                plan.ApiSurface, route, plan.WorkId, plan.ScopeId,
                plan.DynamicFormTemplateId, plan.ApiOwnerResultId,
                plan.ApiFilterSha256, plan.AuthorizationSnapshotSha256,
                requestSha, page.ETag, plan.ApiGenerationId,
                plan.ApiGenerationSha256, I(pageIndex), I(plan.ApiPageSize),
                I(returned), "true", "true", "true", "true", "true",
                ApiTotalsSha(totals),
                HS("P10_ACTUAL_API_PAGE_ROWS_V1", rowShas));
            if (!Eq(page.PageSemanticSha256, pageSha))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ApiInvalid);
            pageShas.Add(pageSha);
        }
        var immutableRows = rows.MoveToImmutable();
        if (immutableRows.Length != plan.ApiExpectedTotalRows ||
            immutableRows.Select(row => row.Identity)
                .Distinct(StringComparer.Ordinal).Count() != immutableRows.Length)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiPartitionIncomplete);
        var totalsValue = stableTotals ?? throw Fail(
            StatisticReconciliationActualCrossViewParityV2Failures.ApiInvalid);
        RequireApiSemantics(plan, immutableRows, totalsValue,
            StatisticReconciliationActualCrossViewParityV2Failures.ApiInvalid);
        var captureSha = H(
            "P10_ACTUAL_API_CAPTURE_V1",
            plan.ApiSurface, route, plan.WorkId, plan.ScopeId,
            plan.DynamicFormTemplateId, plan.ApiOwnerResultId,
            plan.ApiFilterSha256, plan.AuthorizationSnapshotSha256,
            I(plan.ApiExpectedTotalRows),
            StatisticReconciliationActualApiCaptureStates.Ready, null,
            "true", "true", "true", "true", "true",
            HS("P10_ACTUAL_API_PAGES_V1", pageShas));
        if (!Eq(capture.CaptureSemanticSha256, captureSha))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiInvalid);
        return new(
            immutableRows,
            totalsValue,
            HS("P10_ACTUAL_CROSS_VIEW_API_ROWS_V2",
                immutableRows.Select(row => row.SemanticSha256)),
            ApiTotalsSha(totalsValue),
            H("P10_ACTUAL_CROSS_VIEW_API_FILTER_V2",
                capture.CanonicalFilterJson, capture.FilterSha256),
            captureSha);
    }

    private static ValidatedExport ValidateExport(
        NormalizedPlan plan,
        StatisticReconciliationActualCrossViewParityV2Actual? actual)
    {
        if (actual is null || !Eq(actual.SchemaVersion, ActualSchema) ||
            actual.ExportManifest is null || actual.ExportCapture is null)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PreimageMissing);
        var manifest = actual.ExportManifest;
        var capture = actual.ExportCapture;
        if (!Eq(manifest.SchemaVersion,
                StatisticReconciliationActualExportParser.RequiredSchemaVersion) ||
            !Eq(manifest.ExportId, plan.ExportId) ||
            !Eq(manifest.RequestSha256, plan.ExportRequestSha256) ||
            !Eq(manifest.AuthorizationSnapshotSha256,
                plan.AuthorizationSnapshotSha256) ||
            !Eq(manifest.ResultKind, plan.ExportResultKind) ||
            !Eq(manifest.WorkId, plan.WorkId) ||
            !Eq(manifest.ScopeType, plan.ScopeType) ||
            !Eq(manifest.ScopeId, plan.ScopeId) ||
            !Eq(manifest.ResultId, plan.ExportResultId) ||
            !Eq(manifest.ResultSha256, plan.ExportResultSha256) ||
            !Eq(manifest.ConfigSha256, plan.ExportConfigSha256) ||
            !Eq(manifest.SourceSha256, plan.ExportSourceSha256) ||
            manifest.LifecycleRevision != plan.ExportLifecycleRevision ||
            !Eq(manifest.ContentSha256, plan.ExportContentSha256) ||
            !Eq(manifest.OwnerSemanticSha256,
                plan.ExportOwnerSemanticSha256) ||
            !Eq(manifest.FilterSha256, plan.ExportFilterSha256) ||
            !Eq(manifest.PeriodInstanceKey, plan.PeriodInstanceKey) ||
            !Eq(manifest.CanonicalFilterJson,
                plan.CanonicalExportFilterJson) ||
            manifest.Columns.IsDefaultOrEmpty || manifest.RowsImpossible() ||
            manifest.RowCount > MaximumRows)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ExportInvalid);
        }
        ValidateColumns(manifest.Columns,
            StatisticReconciliationActualCrossViewParityV2Failures.ExportInvalid);
        var manifestSha = new StatisticReconciliationActualExportParser()
            .ComputeManifestSha256(manifest);
        if (!Eq(manifestSha, capture.ManifestSha256) ||
            !Eq(capture.ExportId, manifest.ExportId) ||
            !Eq(capture.Format, manifest.Format) ||
            !Eq(capture.ResultKind, manifest.ResultKind) ||
            !Eq(capture.ResultId, manifest.ResultId) ||
            !Eq(capture.ResultSha256, manifest.ResultSha256) ||
            !Eq(capture.ConfigSha256, manifest.ConfigSha256) ||
            !Eq(capture.SourceSha256, manifest.SourceSha256) ||
            !Eq(capture.FilterSha256, manifest.FilterSha256) ||
            capture.LifecycleRevision != manifest.LifecycleRevision ||
            !Eq(capture.ContentSha256, manifest.ContentSha256) ||
            !Eq(capture.OwnerSemanticSha256, manifest.OwnerSemanticSha256) ||
            !Eq(capture.PeriodInstanceKey, manifest.PeriodInstanceKey) ||
            !Eq(capture.CanonicalFilterJson,
                manifest.CanonicalFilterJson) ||
            capture.Headers.IsDefault || !capture.Headers.SequenceEqual(
                manifest.Columns.Select(column => column.Name),
                StringComparer.Ordinal) ||
            capture.Rows.IsDefault || capture.Rows.Length != manifest.RowCount ||
            capture.FullFilterTotals.IsDefault)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ExportInvalid);
        }
        var rows = capture.Rows.Select((row, index) =>
        {
            if (row is null || row.Ordinal != index + 1 ||
                row.Cells.IsDefault || row.Cells.Length != manifest.Columns.Length)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ExportInvalid);
            var cells = row.Cells.Select((cell, cellIndex) => ValidateCell(
                manifest.Columns[cellIndex], cell,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ExportInvalid)).ToImmutableArray();
            var rowSha = HS("P10_ACTUAL_EXPORT_ROW_V1",
                cells.Select(cell => cell.SemanticSha256));
            if (!Eq(row.RowSemanticSha256, rowSha))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ExportInvalid);
            return new ValidatedExportRow(row.Ordinal, null, cells, rowSha);
        }).ToImmutableArray();
        var totals = ValidateTotals(
            capture.FullFilterTotals,
            StatisticReconciliationActualCrossViewParityV2Failures
                .ExportInvalid);
        RequireExportCaptureTotals(manifest.Columns, rows, totals,
            StatisticReconciliationActualCrossViewParityV2Failures.ExportInvalid);
        RequireOwnerSemantic(plan, manifest,
            StatisticReconciliationActualCrossViewParityV2Failures.ExportInvalid);
        var rowsSha = HS("P10_ACTUAL_EXPORT_ROWS_V1",
            rows.Select(row => row.SemanticSha256));
        var totalsSha = ExportTotalsSha(totals);
        if (!Eq(capture.RowsSemanticSha256, rowsSha) ||
            !Eq(capture.TotalsSemanticSha256, totalsSha))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ExportInvalid);
        var captureSha = H(
            "P10_ACTUAL_EXPORT_CAPTURE_V2",
            manifest.ExportId, manifest.Format, manifest.ResultKind,
            manifest.ResultId, manifest.ResultSha256, manifest.ConfigSha256,
            manifest.SourceSha256, manifest.FilterSha256,
            manifest.PeriodInstanceKey ?? "~",
            manifest.CanonicalFilterJson ?? "~",
            I(manifest.LifecycleRevision), manifest.ContentSha256,
            manifestSha, manifest.OwnerSemanticSha256, rowsSha, totalsSha);
        if (!Eq(capture.CaptureSemanticSha256, captureSha))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ExportInvalid);
        return new(manifest.Columns, rows, totals, rowsSha, totalsSha,
            H("P10_ACTUAL_CROSS_VIEW_EXPORT_FILTER_V2",
                manifest.CanonicalFilterJson, manifest.FilterSha256),
            manifestSha, captureSha);
    }

    private static void ValidateAuthorization(
        NormalizedPlan plan,
        StatisticReconciliationActualApiAuthorizationContext? authorization)
    {
        if (authorization is null || !authorization.IsAuthorized ||
            !Eq(authorization.WorkId, plan.WorkId) ||
            !Eq(authorization.ScopeAssignmentId, plan.ScopeId) ||
            !Eq(authorization.AuthorizationSnapshotSha256,
                plan.AuthorizationSnapshotSha256) ||
            authorization.PermissionCodes.IsDefaultOrEmpty ||
            authorization.RowCountBeforeRedaction < 0 ||
            authorization.RowCountAfterRedaction < 0 ||
            authorization.RowCountAfterRedaction >
                authorization.RowCountBeforeRedaction)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiInvalid);
        var prior = string.Empty;
        foreach (var permission in authorization.PermissionCodes)
        {
            var current = Upper(permission);
            if (prior.Length > 0 && StringComparer.Ordinal.Compare(
                    prior, current) >= 0)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .ApiInvalid);
            prior = current;
        }
        var sha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                Required(authorization.ActorUserId), plan.WorkId, plan.ScopeId,
                authorization.PermissionCodes,
                authorization.RowCountBeforeRedaction,
                authorization.RowCountAfterRedaction);
        if (!Eq(sha, plan.AuthorizationSnapshotSha256))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ApiInvalid);
    }

    private static ValidatedApiRow BaseApiRow(
        int ordinal, string? identity, string? json, string failure)
    {
        if (ordinal < 0 || string.IsNullOrWhiteSpace(identity) ||
            string.IsNullOrWhiteSpace(json))
            throw Fail(failure);
        var id = Required(identity);
        var canonical = CanonicalAny(json!, failure);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            canonical, "CROSS_VIEW_API_ROW");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw Fail(failure);
        return new(
            ordinal, id, canonical, document.RootElement.Clone(),
            H("P10_ACTUAL_CROSS_VIEW_API_ROW_V2",
                I(ordinal), id, canonical));
    }

    private static string CanonicalAny(string value, string failure)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            value, "CROSS_VIEW_CANONICAL_JSON");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        if (!Eq(canonical, value))
            throw Fail(failure);
        return canonical;
    }

    private static ImmutableArray<ValidatedTotal> NormalizeApiTotals(
        ImmutableArray<StatisticReconciliationActualApiTotalValue> input,
        string failure)
    {
        if (input.IsDefaultOrEmpty || input.Length > 32)
            throw Fail(failure);
        var result = input.Select(value =>
        {
            if (value is null)
                throw Fail(failure);
            var name = Required(value.Name);
            var type = Upper(value.ValueType);
            string canonical;
            if (type == "INTEGER" && long.TryParse(
                    value.CanonicalValue, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var integer) && integer >= 0)
                canonical = I(integer);
            else if (type == "DECIMAL" && decimal.TryParse(
                         value.CanonicalValue,
                         NumberStyles.AllowLeadingSign |
                         NumberStyles.AllowDecimalPoint,
                         CultureInfo.InvariantCulture, out var number))
                canonical = StatisticReconciliationActualCanonical.Number(number);
            else
                throw Fail(failure);
            if (!Eq(canonical, value.CanonicalValue))
                throw Fail(failure);
            return new ValidatedTotal(name, type, "VALUE", canonical, 0,
                H("P10_ACTUAL_API_TOTAL_V1", name, type, canonical));
        }).OrderBy(value => value.Name, StringComparer.Ordinal)
          .ToImmutableArray();
        if (result.Select(value => value.Name)
                .Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw Fail(failure);
        return result;
    }

    private static string ApiTotalsSha(IEnumerable<ValidatedTotal> totals)
        => HS("P10_ACTUAL_API_FULL_FILTER_TOTALS_V1",
            totals.Select(total => total.SemanticSha256));

    private static void ValidateColumns(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        string failure)
    {
        if (columns.IsDefaultOrEmpty || columns.Length > 512)
            throw Fail(failure);
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index] ?? throw Fail(failure);
            if (column.Ordinal != index || string.IsNullOrWhiteSpace(column.Name) ||
                !StatisticReconciliationActualExportValueTypes.IsSupported(
                    column.ValueType) ||
                !StatisticReconciliationActualExportBlankPolicies.IsSupported(
                    column.BlankPolicy))
                throw Fail(failure);
        }
        if (!Eq(columns[0].Name, "ordinal") ||
            !Eq(columns[0].ValueType,
                StatisticReconciliationActualExportValueTypes.Integer) ||
            !Eq(columns[0].BlankPolicy,
                StatisticReconciliationActualExportBlankPolicies.Forbidden) ||
            columns[0].IsFullFilterTotal ||
            columns.Select(column => column.Name)
                .Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw Fail(failure);
    }

    private static ValidatedCell ValidateCell(
        StatisticReconciliationActualExportColumnContract column,
        StatisticReconciliationActualExportCellObservation? cell,
        string failure)
    {
        if (cell is null || cell.ColumnOrdinal != column.Ordinal ||
            !Eq(cell.ColumnName, column.Name) ||
            !Eq(cell.ValueType, column.ValueType) ||
            cell.DecimalScale is < 0 or > 28)
            throw Fail(failure);
        var state = Upper(cell.ValueState);
        if (state is not ("VALUE" or "NULL" or "EMPTY"))
            throw Fail(failure);
        if (state == "NULL" &&
            !Eq(column.BlankPolicy,
                StatisticReconciliationActualExportBlankPolicies.Null) ||
            state == "EMPTY" &&
            !Eq(column.BlankPolicy,
                StatisticReconciliationActualExportBlankPolicies.Empty) ||
            state != "VALUE" &&
            (cell.CanonicalValue.Length != 0 || cell.DecimalScale != 0 ||
             cell.FormulaNeutralized))
            throw Fail(failure);
        var sha = H("P10_ACTUAL_EXPORT_CELL_V1", I(column.Ordinal),
            column.Name, column.ValueType, state, cell.CanonicalValue,
            I(cell.DecimalScale), cell.FormulaNeutralized ? "true" : "false");
        if (!Eq(sha, cell.CellSemanticSha256))
            throw Fail(failure);
        return new(column.Ordinal, column.Name, column.ValueType, state,
            cell.CanonicalValue, cell.DecimalScale, cell.FormulaNeutralized, sha);
    }

    private static ImmutableArray<ValidatedTotal> ValidateTotals(
        ImmutableArray<StatisticReconciliationActualExportTotalObservation> input,
        string failure)
    {
        if (input.IsDefaultOrEmpty || input.Length > 512)
            throw Fail(failure);
        var result = input.Select(value =>
        {
            if (value is null)
                throw Fail(failure);
            var sha = H("P10_ACTUAL_EXPORT_TOTAL_V1", value.Name,
                value.ValueType, value.ValueState, value.CanonicalValue,
                I(value.DecimalScale));
            if (!Eq(sha, value.TotalSemanticSha256))
                throw Fail(failure);
            return new ValidatedTotal(value.Name, value.ValueType,
                value.ValueState, value.CanonicalValue, value.DecimalScale, sha);
        }).OrderBy(value => value.Name, StringComparer.Ordinal)
          .ToImmutableArray();
        if (result.Select(value => value.Name)
                .Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw Fail(failure);
        return result;
    }

    private static string ExportTotalsSha(IEnumerable<ValidatedTotal> totals)
        => HS("P10_ACTUAL_EXPORT_TOTALS_V1",
            totals.Select(total => total.SemanticSha256));

    private static string ColumnSha(
        StatisticReconciliationActualExportColumnContract column)
        => H("P10_ACTUAL_EXPORT_COLUMN_V1", I(column.Ordinal), column.Name,
            column.ValueType, column.BlankPolicy,
            column.IsFullFilterTotal ? "true" : "false");

    private sealed record ValidatedApiRow(
        int Ordinal,
        string Identity,
        string CanonicalJson,
        JsonElement Root,
        string SemanticSha256);

    private sealed record ValidatedCell(
        int Ordinal,
        string Name,
        string ValueType,
        string ValueState,
        string CanonicalValue,
        int DecimalScale,
        bool FormulaNeutralized,
        string SemanticSha256);

    private sealed record ValidatedTotal(
        string Name,
        string ValueType,
        string ValueState,
        string CanonicalValue,
        int DecimalScale,
        string SemanticSha256);

    private sealed record ValidatedExportRow(
        int Ordinal,
        string? CanonicalSourceRowJson,
        ImmutableArray<ValidatedCell> Cells,
        string SemanticSha256);

    private sealed record ValidatedApiBase(
        ImmutableArray<ValidatedApiRow> Rows,
        ImmutableArray<ValidatedTotal> Totals,
        string RowsSha256,
        string TotalsSha256,
        string FilterRelationSha256,
        string ProjectionSha256);

    private sealed record ValidatedExportBase(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> Columns,
        ImmutableArray<ValidatedExportRow> Rows,
        ImmutableArray<ValidatedTotal> Totals,
        string RowsSha256,
        string TotalsSha256,
        string ProjectionSha256);

    private sealed record ValidatedApi(
        ImmutableArray<ValidatedApiRow> Rows,
        ImmutableArray<ValidatedTotal> Totals,
        string RowsSha256,
        string TotalsSha256,
        string FilterRelationSha256,
        string CaptureSha256);

    private sealed record ValidatedExport(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> Columns,
        ImmutableArray<ValidatedExportRow> Rows,
        ImmutableArray<ValidatedTotal> Totals,
        string RowsSha256,
        string TotalsSha256,
        string FilterRelationSha256,
        string ManifestSha256,
        string CaptureSha256);
}

internal static class StatisticReconciliationActualExportManifestParityExtensions
{
    internal static bool RowsImpossible(
        this StatisticReconciliationActualExportManifest value)
        => value.RowCount < 0 || value.ColumnCount <= 0 ||
           value.Columns.Length != value.ColumnCount;
}
