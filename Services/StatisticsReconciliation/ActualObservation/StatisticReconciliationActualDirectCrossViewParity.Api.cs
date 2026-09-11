using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualDirectCrossViewParity
{
    private static ValidatedApi ValidateApi(
        NormalizedPlan plan,
        StatisticReconciliationActualApiCapture? capture)
    {
        if (capture is null)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiCaptureInvalid);

        var route = StatisticReconciliationActualApiProtocol.RouteId(plan.Surface);
        var expectedGenerationSha = HS(
            "P10_ACTUAL_API_DIRECT_GENERATION_V1",
            [H(
                "P10_ACTUAL_API_DIRECT_PUBLICATION_PIN_V1",
                plan.P9RunId,
                plan.P9GenerationId,
                plan.P9GenerationSha256,
                I(plan.P9DirectSourceRevision),
                I(plan.P9DirectPublicationRevision))]);
        var expectedEtag = $"\"sha256-{expectedGenerationSha}\"";

        if (!Eq(capture.Surface, plan.Surface) ||
            !Eq(capture.RouteId, route) ||
            !Eq(capture.WorkId, plan.WorkId) ||
            !Eq(capture.ScopeAssignmentId, plan.ScopeAssignmentId) ||
            !Eq(capture.DynamicFormTemplateId, plan.DynamicFormTemplateId) ||
            !Eq(capture.OwnerResultId, plan.P9RunId) ||
            !Eq(capture.CanonicalFilterJson, plan.CanonicalApiFilterJson) ||
            !Eq(capture.FilterSha256, plan.ApiFilterSha256) ||
            !capture.FullFilterTotalsStable ||
            !capture.GenerationStable ||
            !capture.OverlapStable ||
            !capture.PagingContractsValid ||
            !Eq(capture.CaptureState,
                StatisticReconciliationActualApiCaptureStates.Ready) ||
            capture.CaptureReason is not null)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiCaptureInvalid);
        }

        var authorization = capture.Authorization;
        if (authorization is null || !authorization.IsAuthorized ||
            !Eq(authorization.WorkId, plan.WorkId) ||
            !Eq(authorization.ScopeAssignmentId, plan.ScopeAssignmentId) ||
            !Eq(authorization.AuthorizationSnapshotSha256,
                plan.AuthorizationSnapshotSha256) ||
            authorization.RowCountBeforeRedaction < 0 ||
            authorization.RowCountAfterRedaction < 0 ||
            authorization.RowCountAfterRedaction >
                authorization.RowCountBeforeRedaction ||
            authorization.PermissionCodes.IsDefaultOrEmpty)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiCaptureInvalid);
        }
        var permissionCodes = authorization.PermissionCodes;
        for (var index = 0; index < permissionCodes.Length; index++)
        {
            var permission = ExactUpper(permissionCodes[index]);
            if (index > 0 && StringComparer.Ordinal.Compare(
                    permissionCodes[index - 1], permission) >= 0)
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ApiCaptureInvalid);
            }
        }
        var authorizationSha =
            StatisticReconciliationActualApiObservationAdapter.AuthorizationSha(
                Required(authorization.ActorUserId),
                plan.WorkId,
                plan.ScopeAssignmentId,
                permissionCodes,
                authorization.RowCountBeforeRedaction,
                authorization.RowCountAfterRedaction);
        if (!Eq(authorizationSha, plan.AuthorizationSnapshotSha256))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiCaptureInvalid);

        if (capture.Pages.IsDefault ||
            capture.Pages.Length != plan.PageCount)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiPartitionIncomplete);
        }

        var apiRows = ImmutableArray.CreateBuilder<SemanticRow>(
            checked((int)plan.ExpectedTotalRows));
        ImmutableArray<SemanticTotal>? baselineTotals = null;
        var pageSemanticShas = ImmutableArray.CreateBuilder<string>(
            capture.Pages.Length);
        var filter = ReadApiFilter(plan.Surface, plan.CanonicalApiFilterJson);
        var expectedApiTotalPages = plan.ExpectedTotalRows == 0
            ? 0
            : checked((int)((plan.ExpectedTotalRows + plan.PageSize - 1) /
                plan.PageSize));

        for (var pageIndex = 0; pageIndex < capture.Pages.Length; pageIndex++)
        {
            var page = capture.Pages[pageIndex] ?? throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .ApiCaptureInvalid);
            var expectedReturned = plan.ExpectedTotalRows <=
                (long)pageIndex * plan.PageSize
                ? 0
                : (int)Math.Min(
                    plan.PageSize,
                    plan.ExpectedTotalRows - (long)pageIndex * plan.PageSize);
            if (page.Page != pageIndex || page.PageSize != plan.PageSize ||
                page.TotalPages != expectedApiTotalPages ||
                page.ReturnedRows != expectedReturned ||
                page.TotalRows != plan.ExpectedTotalRows ||
                page.Rows.IsDefault || page.Rows.Length != expectedReturned ||
                !Eq(page.ETag, expectedEtag) ||
                !Eq(page.GenerationId, plan.P9GenerationId) ||
                !Eq(page.GenerationSha256, expectedGenerationSha) ||
                !page.TargetBindingMatches || !page.RequestBindingMatches ||
                !page.PagingContractValid || !page.RowIdentitiesUnique ||
                !page.RowSemanticsValid)
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ApiPartitionIncomplete);
            }

            var totals = NormalizeTotals(
                page.FullFilterTotals,
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .ApiCaptureInvalid);
            baselineTotals ??= totals;
            if (!baselineTotals.Value.SequenceEqual(totals))
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .TotalsMismatch);

            var requestSha = StatisticReconciliationActualApiProtocol.RequestSha(
                plan.Surface,
                route,
                plan.WorkId,
                plan.ScopeAssignmentId,
                plan.DynamicFormTemplateId,
                plan.P9RunId,
                plan.ApiFilterSha256,
                plan.AuthorizationSnapshotSha256,
                pageIndex,
                plan.PageSize);
            var rowShas = ImmutableArray.CreateBuilder<string>(page.Rows.Length);
            for (var rowIndex = 0; rowIndex < page.Rows.Length; rowIndex++)
            {
                var value = page.Rows[rowIndex] ?? throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ApiRowInvalid);
                var absolute = checked(pageIndex * plan.PageSize + rowIndex);
                if (value.AbsoluteOrdinal != absolute ||
                    !value.StoredSemanticMatches)
                {
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ApiRowInvalid);
                }
                var row = ValidateSemanticRow(
                    plan,
                    filter,
                    absolute,
                    value.Identity,
                    value.CanonicalRowJson,
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ApiRowInvalid);
                var apiRowSha =
                    StatisticReconciliationActualApiObservationAdapter.RowSha(
                        row.Identity,
                        row.CanonicalJson);
                if (!Eq(value.RowSemanticSha256, apiRowSha))
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ApiRowInvalid);
                apiRows.Add(row);
                rowShas.Add(apiRowSha);
            }

            var pageSha = H(
                "P10_ACTUAL_API_PAGE_V1",
                plan.Surface,
                route,
                plan.WorkId,
                plan.ScopeAssignmentId,
                plan.DynamicFormTemplateId,
                plan.P9RunId,
                plan.ApiFilterSha256,
                plan.AuthorizationSnapshotSha256,
                requestSha,
                expectedEtag,
                plan.P9GenerationId,
                expectedGenerationSha,
                I(pageIndex),
                I(plan.PageSize),
                I(expectedReturned),
                "true", "true", "true", "true", "true",
                ApiTotalsSha(totals),
                HS("P10_ACTUAL_API_PAGE_ROWS_V1", rowShas));
            if (!Eq(page.PageSemanticSha256, pageSha))
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ApiCaptureInvalid);
            pageSemanticShas.Add(pageSha);
        }

        var rows = apiRows.MoveToImmutable();
        if (rows.Length != plan.ExpectedTotalRows)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiPartitionIncomplete);
        RequireUniqueRows(rows,
            StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiPartitionIncomplete);
        var allTotals = baselineTotals ?? throw F(
            StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiCaptureInvalid);
        var recomputedTotals = TotalsForRows(plan.Surface, rows);
        if (!allTotals.SequenceEqual(recomputedTotals))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .TotalsMismatch);

        var captureSha = H(
            "P10_ACTUAL_API_CAPTURE_V1",
            plan.Surface,
            route,
            plan.WorkId,
            plan.ScopeAssignmentId,
            plan.DynamicFormTemplateId,
            plan.P9RunId,
            plan.ApiFilterSha256,
            plan.AuthorizationSnapshotSha256,
            I(plan.ExpectedTotalRows),
            StatisticReconciliationActualApiCaptureStates.Ready,
            null,
            "true", "true", "true", "true", "true",
            HS("P10_ACTUAL_API_PAGES_V1", pageSemanticShas));
        if (!Eq(capture.CaptureSemanticSha256, captureSha))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ApiCaptureInvalid);

        var partitionSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_API_PARTITION_V1",
            captureSha,
            HS("P10_ACTUAL_DIRECT_CROSS_VIEW_API_ROWS_V1",
                rows.Select(row => row.SemanticSha256)),
            I(rows.Length),
            TotalsSha("P10_ACTUAL_DIRECT_CROSS_VIEW_API_TOTALS_V1", allTotals));
        return new(rows, allTotals, partitionSha, captureSha);
    }

    private static string ApiTotalsSha(
        IEnumerable<SemanticTotal> totals)
        => HS(
            "P10_ACTUAL_API_FULL_FILTER_TOTALS_V1",
            totals.Select(total => H(
                "P10_ACTUAL_API_TOTAL_V1",
                total.Name,
                total.ValueType,
                total.CanonicalValue)));

    private sealed record ValidatedApi(
        ImmutableArray<SemanticRow> Rows,
        ImmutableArray<SemanticTotal> Totals,
        string PartitionSha256,
        string CaptureSha256);
}
