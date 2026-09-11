using tdtd_be.Models;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualCrossViewV3AdvancedProjector(
    IStatisticReconciliationActualAdvancedOwnerReader advancedOwner)
{
    internal async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> ProjectAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            CancellationToken cancellationToken)
    {
        var final = command.Advanced ?? throw Invalid(
            "CROSS_VIEW_OWNER_ADVANCED_CAPTURE_REQUIRED");
        var boundary = command.Material.Advanced;
        var days = await advancedOwner.ReadDayNodesAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_ADVANCED_DAY_ROWS_NULL");
        var months = await advancedOwner.ReadMonthNodesAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_ADVANCED_MONTH_ROWS_NULL");
        var years = await advancedOwner.ReadYearNodesAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_ADVANCED_YEAR_ROWS_NULL");
        var resultId = command.Material.Export.ResultId;
        var matches = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .Where(value => value.Id == resultId)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
            throw Invalid("CROSS_VIEW_OWNER_ADVANCED_OWNER_AMBIGUOUS");
        var node = matches[0];
        var rawSha = StatisticReconciliationActualJson.RawSha256(
            node.ValueJson ?? string.Empty);
        var observations = final.Nodes.Where(value =>
                value.OwnerNodeId == node.Id)
            .Take(2)
            .ToArray();
        if (observations.Length != 1 || final.Boundary != boundary ||
            node.WorkId != boundary.WorkId ||
            node.AssignmentId != boundary.AssignmentId ||
            node.DynamicFormTemplateId != boundary.DynamicFormTemplateId ||
            node.ConfigHash != boundary.ConfigSha256 || node.IsDeleted ||
            node.IsDirty ||
            node.Status !=
                WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean ||
            node.BuiltAtUtc?.Kind != DateTimeKind.Utc ||
            node.ValueHash != rawSha || node.SourceSignatureHash is null ||
            observations[0].ValueJson != node.ValueJson ||
            observations[0].StoredValueSha256 != node.ValueHash ||
            observations[0].ObservedValueSha256 != rawSha ||
            observations[0].SourceSignatureSha256 !=
                node.SourceSignatureHash ||
            !observations[0].OwnerState.IsCleanResult)
            throw Invalid("CROSS_VIEW_OWNER_ADVANCED_OWNER_DRIFT");
        return new(
            StatisticReconciliationActualCrossViewFamilies.Advanced,
            null,
            node.ValueJson,
            null);
    }

    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
