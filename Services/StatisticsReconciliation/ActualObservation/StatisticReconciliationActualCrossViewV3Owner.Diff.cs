using tdtd_be.DTOs.Statistics;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualCrossViewV3DiffProjector(
    IStatisticReconciliationActualP9DiffOwnerReader diffOwner)
{
    internal async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> ProjectAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            CancellationToken cancellationToken)
    {
        var api = command.Api ?? throw Invalid(
            "CROSS_VIEW_OWNER_API_CAPTURE_REQUIRED");
        var final = command.Diff ?? throw Invalid(
            "CROSS_VIEW_OWNER_DIFF_CAPTURE_REQUIRED");
        var boundary = command.Material.Diff;
        var result = await diffOwner.ReadResultAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_DIFF_OWNER_UNAVAILABLE");
        RequireResult(boundary, final, result);
        var filter = new StatisticReconciliationActualCrossViewV3Filter(
            api.CanonicalFilterJson);
        filter.RequireExact();
        var generation =
            StatisticReconciliationActualCrossViewV3OwnerCommon.Generation(api);
        if (generation.GenerationId != result.Id ||
            generation.GenerationSha256 != result.ResultHash)
            throw Invalid("CROSS_VIEW_OWNER_DIFF_GENERATION_DRIFT");
        var responseRows = result.Rows
            .OrderBy(value => value.Ordinal)
            .Select(ToResponse)
            .ToArray();
        var apiRows = responseRows.Select(value => (
            StatisticReconciliationActualCanonical.Required(
                value.RowId,
                "CROSS_VIEW_OWNER_DIFF_ROW_ID"),
            StatisticReconciliationActualCrossViewV3OwnerCommon.CanonicalRow(
                value)));
        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", result.TotalRowCount),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRowCount", result.TotalRowCount),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalEqualRowCount", result.EqualRowCount),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalChangedRowCount", result.ChangedRowCount));
        var apiBase =
            StatisticReconciliationActualCrossViewV3OwnerCommon.ApiBase(
                result.WorkId,
                result.AssignmentId,
                result.DynamicFormTemplateId,
                Required(command.Material.Run.PeriodInstanceKey,
                    "CROSS_VIEW_OWNER_PERIOD_INSTANCE_KEY"),
                api.Authorization.AuthorizationSnapshotSha256,
                StatisticReconciliationActualApiSurfaces.P9Diff,
                result.Id,
                result.Id,
                result.ResultHash!,
                api.CanonicalFilterJson,
                api.FilterSha256,
                apiRows,
                totals);
        return new(
            StatisticReconciliationActualCrossViewFamilies.Diff,
            apiBase,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                result.Rows.OrderBy(value => value.Ordinal).ToArray()),
            null);
    }

    private static void RequireResult(
        ActualP9DiffOwnerBoundary boundary,
        ActualP9DiffCapture final,
        WorkReportStatisticDiffResult result)
    {
        var ids = result.Rows.OrderBy(value => value.Ordinal)
            .Select(value => value.RowId).ToArray();
        if (result.Id != boundary.ResultId || result.RunId != boundary.RunId ||
            result.WorkId != boundary.WorkId ||
            result.AssignmentId != boundary.AssignmentId ||
            result.DynamicFormTemplateId != boundary.DynamicFormTemplateId ||
            result.ConfigHash != boundary.ConfigSha256 || result.IsDeleted ||
            result.Status != P9StatisticDiffResultStatuses.Completed ||
            !result.IsCurrent || !result.IsFresh || result.IsDirty ||
            result.CompletedAtUtc?.Kind != DateTimeKind.Utc ||
            result.ResultHash is null ||
            result.TotalRowCount != result.Rows.Count ||
            result.EqualRowCount != result.Rows.Count(value => value.Equal) ||
            result.ChangedRowCount != result.Rows.Count(value => !value.Equal) ||
            final.Boundary != boundary || !final.OwnerState.IsUsableResult ||
            final.StoredResultSha256 != result.ResultHash ||
            !final.Rows.OrderBy(value => value.OwnerOrdinal)
                .Select(value => value.OwnerRowId)
                .SequenceEqual(ids, StringComparer.Ordinal))
            throw Invalid("CROSS_VIEW_OWNER_DIFF_OWNER_DRIFT");
    }

    private static P9StatisticDiffRowResponse ToResponse(
        P9StatisticDiffResultRow value)
        => new(
            value.RowId,
            value.Ordinal,
            value.Key,
            value.ConceptKind,
            value.ConceptKey,
            Typed(value.Left),
            Typed(value.Right),
            value.Equal,
            value.DifferenceKind,
            value.NumericDelta);

    private static P9StatisticDiffTypedValueResponse Typed(
        P9StatisticDiffTypedValue value)
    {
        var redacted = value.State == P9StatisticDiffValueStates.Redacted;
        return new(
            value.State,
            value.DataType,
            redacted ? null : value.CanonicalValue,
            redacted ? null : value.NumericValue,
            redacted ? null : value.BooleanValue,
            redacted ? null : value.DateValueUtc,
            redacted ? [] : value.ChoiceIds);
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
