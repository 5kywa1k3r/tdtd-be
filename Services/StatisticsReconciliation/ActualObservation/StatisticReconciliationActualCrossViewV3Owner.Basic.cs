using System.Collections.Immutable;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualCrossViewV3BasicProjector(
    IStatisticReconciliationActualBasicOwnerReader basicOwner,
    IStatisticReconciliationActualBasicApiReadOwner basicApi)
{
    internal async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> ProjectAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            CancellationToken cancellationToken)
    {
        var material = command.Material;
        var api = command.Api ?? throw Invalid(
            "CROSS_VIEW_OWNER_API_CAPTURE_REQUIRED");
        var final = command.Basic ?? throw Invalid(
            "CROSS_VIEW_OWNER_BASIC_CAPTURE_REQUIRED");
        var boundary = material.Basic.Boundary;
        var snapshot = await basicOwner.ReadSnapshotAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_BASIC_OWNER_UNAVAILABLE");
        RequireSnapshot(boundary, final, snapshot);
        var filter = new
            StatisticReconciliationActualCrossViewV3CanonicalFilter(
            api.CanonicalFilterJson);
        filter.RequireAllowed("q", "periodKey", "unitId",
            "assigneeUserId");
        var sourceFilter = new StatisticReconciliationActualBasicApiSourceFilter(
            filter.String("q"),
            filter.String("periodKey"),
            filter.String("unitId"),
            filter.String("assigneeUserId"));
        var rows = new List<tdtd_be.DTOs.WorkAssignments.BasicSummary
            .WorkAssignmentBasicSummarySourceDto>();
        string? generationSha = null;
        int? totalRows = null;
        foreach (var selector in material.Api.Pages.OrderBy(value => value.Page))
        {
            var page = await basicApi.ReadSourcesPageAsync(
                    snapshot.Id,
                    snapshot.WorkId,
                    snapshot.ScopeAssignmentId,
                    snapshot.DynamicFormTemplateId,
                    sourceFilter,
                    selector.Page,
                    selector.PageSize,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw Invalid(
                    "CROSS_VIEW_OWNER_BASIC_API_PAGE_NULL");
            if (page.SnapshotId != snapshot.Id ||
                generationSha is not null &&
                generationSha != page.GenerationSha256 ||
                totalRows.HasValue && totalRows.Value != page.TotalRows)
                throw Invalid("CROSS_VIEW_OWNER_BASIC_API_PAGE_DRIFT");
            generationSha ??= page.GenerationSha256;
            totalRows ??= page.TotalRows;
            rows.AddRange(page.Rows);
        }
        if (totalRows is null || rows.Count != totalRows.Value)
            throw Invalid("CROSS_VIEW_OWNER_BASIC_API_PARTITION_INCOMPLETE");
        var captureGeneration =
            StatisticReconciliationActualCrossViewV3OwnerCommon.Generation(api);
        if (captureGeneration.GenerationId != snapshot.Id ||
            captureGeneration.GenerationSha256 != generationSha)
            throw Invalid("CROSS_VIEW_OWNER_BASIC_GENERATION_DRIFT");
        var apiRows = rows.Select(value => (
            StatisticReconciliationActualCanonical.Required(
                value.WorkAssignmentReportId,
                "CROSS_VIEW_OWNER_BASIC_ROW_ID"),
            StatisticReconciliationActualCrossViewV3OwnerCommon.CanonicalRow(
                value)));
        var apiBase =
            StatisticReconciliationActualCrossViewV3OwnerCommon.ApiBase(
                snapshot.WorkId,
                snapshot.ScopeAssignmentId,
                snapshot.DynamicFormTemplateId,
                Required(material.Run.PeriodInstanceKey,
                    "CROSS_VIEW_OWNER_PERIOD_INSTANCE_KEY"),
                api.Authorization.AuthorizationSnapshotSha256,
                StatisticReconciliationActualApiSurfaces.BasicSource,
                snapshot.Id,
                snapshot.Id,
                generationSha!,
                api.CanonicalFilterJson,
                api.FilterSha256,
                apiRows,
                StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
                    StatisticReconciliationActualCrossViewV3OwnerCommon
                        .IntegerTotal("totalRows", rows.Count)));
        var assignments = snapshot.SourceAssignmentIds
            .OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        var reports = snapshot.SourceReportIds
            .OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        var preimage = new
            StatisticReconciliationActualCrossViewBasicGenerationPreimage(
                snapshot.RequestHash,
                StatisticReconciliationActualJson.RawSha256(
                    snapshot.RequestJson ?? string.Empty),
                snapshot.SourceSignatureHash,
                snapshot.ConfigId,
                snapshot.ConfigVersionId,
                snapshot.ConfigVersionNo,
                checked((int)snapshot.ConfigRevision),
                snapshot.ConfigHash,
                snapshot.ConfigDependencyPins.ToImmutableArray(),
                snapshot.CandidateChainId,
                snapshot.CandidatePromptId,
                snapshot.CandidateStage,
                snapshot.CandidateCatalogRawSha256,
                snapshot.CandidateCatalogSemanticSha256,
                snapshot.CandidateStageLockSha256,
                assignments,
                reports);
        var family = StatisticReconciliationActualBasicModes.IsFlow(
            snapshot.SourceScopeMode)
            ? StatisticReconciliationActualCrossViewFamilies.Flow
            : StatisticReconciliationActualCrossViewFamilies.Basic;
        return new(family, apiBase, snapshot.SnapshotJson, preimage);
    }

    private static void RequireSnapshot(
        ActualBasicOwnerBoundary boundary,
        ActualBasicResultObservation final,
        WorkAssignmentBasicSummarySnapshot snapshot)
    {
        var rawSha = StatisticReconciliationActualJson.RawSha256(
            snapshot.SnapshotJson ?? string.Empty);
        if (snapshot.Id != boundary.OwnerSnapshotId ||
            snapshot.WorkId != boundary.WorkId ||
            snapshot.ScopeAssignmentId != boundary.ScopeAssignmentId ||
            snapshot.DynamicFormTemplateId != boundary.DynamicFormTemplateId ||
            snapshot.SourceScopeMode != boundary.SourceScopeMode ||
            snapshot.ConfigHash != boundary.ConfigSha256 ||
            snapshot.RequestHash != boundary.RequestHash ||
            snapshot.IsDeleted || snapshot.SnapshotDirty ||
            snapshot.RefreshStatus !=
                WorkAssignmentBasicSummaryRefreshStatuses.Done ||
            snapshot.SnapshotRefreshedAtUtc?.Kind != DateTimeKind.Utc ||
            final.Boundary != boundary ||
            final.SnapshotJson != snapshot.SnapshotJson ||
            final.SnapshotRawSha256 != rawSha ||
            final.SourceSignatureSha256 != snapshot.SourceSignatureHash ||
            !final.OwnerState.OwnerCompleteAndClean)
            throw Invalid("CROSS_VIEW_OWNER_BASIC_OWNER_DRIFT");
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
