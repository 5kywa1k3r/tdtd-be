using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed record StatisticReconciliationActualBasicApiSourceFilter(
    string? Q,
    string? PeriodKey,
    string? UnitId,
    string? AssigneeUserId);

public sealed record StatisticReconciliationActualBasicApiSourcePage(
    IReadOnlyList<WorkAssignmentBasicSummarySourceDto> Rows,
    int TotalRows,
    string SnapshotId,
    string GenerationSha256);

public interface IStatisticReconciliationActualBasicApiReadOwner
{
    Task<StatisticReconciliationActualBasicApiSourcePage> ReadSourcesPageAsync(
        string snapshotId,
        string workId,
        string scopeAssignmentId,
        string dynamicFormTemplateId,
        StatisticReconciliationActualBasicApiSourceFilter filter,
        int page,
        int pageSize,
        CancellationToken ct);
}

/// <summary>
/// P10 API-parity reader for Basic source rows. This partial intentionally has
/// no build, refresh, dirty-marking, queue, or write path. It can only project
/// an exact clean snapshot and fails closed when any frozen source pin drifts.
/// </summary>
public sealed partial class WorkAssignmentBasicSummaryService :
    IStatisticReconciliationActualBasicApiReadOwner
{
    public async Task<StatisticReconciliationActualBasicApiSourcePage>
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
        ArgumentNullException.ThrowIfNull(filter);
        if (!ObjectId.TryParse(snapshotId, out _) ||
            !ObjectId.TryParse(workId, out _) ||
            !ObjectId.TryParse(scopeAssignmentId, out _) ||
            !ObjectId.TryParse(dynamicFormTemplateId, out _))
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status400BadRequest,
                "API_BASIC_OWNER_TARGET_INVALID");
        }

        var snapshot = await _ctx.WorkAssignmentBasicSummarySnapshots
            .Find(item =>
                item.Id == snapshotId &&
                item.WorkId == workId &&
                item.ScopeAssignmentId == scopeAssignmentId &&
                item.DynamicFormTemplateId == dynamicFormTemplateId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (snapshot is null)
        {
            throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status404NotFound,
                "API_BASIC_OWNER_NOT_FOUND");
        }

        if (snapshot.SnapshotDirty ||
            !string.Equals(
                snapshot.RefreshStatus,
                WorkAssignmentBasicSummaryRefreshStatuses.Done,
                StringComparison.Ordinal) ||
            snapshot.SnapshotRefreshedAtUtc is null ||
            string.IsNullOrWhiteSpace(snapshot.SourceSignatureHash))
        {
            throw BasicApiSnapshotStale("API_BASIC_SNAPSHOT_NOT_CLEAN");
        }

        var sourceOwner = await P10ApiResolveExactSourcesAsync(
                snapshot,
                workId,
                scopeAssignmentId,
                dynamicFormTemplateId,
                ct)
            .ConfigureAwait(false);
        var assignmentIds = sourceOwner.AssignmentIds;
        var reportIds = sourceOwner.ReportIds;
        var assignments = sourceOwner.Assignments;
        var reports = sourceOwner.Reports;
        var currentSourceSignature = BuildSourceSignatureHash(assignments, reports);
        if (!string.Equals(
                currentSourceSignature,
                snapshot.SourceSignatureHash,
                StringComparison.Ordinal))
        {
            throw BasicApiSnapshotStale("API_BASIC_SOURCE_SIGNATURE_DRIFT");
        }

        var assignmentById = assignments.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var sourceView = new NormalizedSourceView(
            filter.Q,
            filter.PeriodKey,
            filter.UnitId,
            filter.AssigneeUserId,
            page,
            pageSize);
        var filtered = FilterSources(
                reports.Select(report => MapSource(report, assignmentById)),
                sourceView)
            .ToList();
        var rows = filtered
            .Skip(checked(page * pageSize))
            .Take(pageSize)
            .ToList();

        var generationSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_BASIC_GENERATION_V1",
            snapshot.Id,
            snapshot.RequestHash,
            StatisticReconciliationActualJson.RawSha256(snapshot.RequestJson ?? ""),
            snapshot.SourceSignatureHash,
            snapshot.ConfigId,
            snapshot.ConfigVersionId,
            StatisticReconciliationActualCanonical.Integer(snapshot.ConfigVersionNo),
            StatisticReconciliationActualCanonical.Integer(snapshot.ConfigRevision),
            snapshot.ConfigHash,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_BASIC_CONFIG_DEPENDENCIES_V1",
                snapshot.ConfigDependencyPins ?? []),
            snapshot.CandidateChainId,
            snapshot.CandidatePromptId,
            StatisticReconciliationActualCanonical.Integer(snapshot.CandidateStage),
            snapshot.CandidateCatalogRawSha256,
            snapshot.CandidateCatalogSemanticSha256,
            snapshot.CandidateStageLockSha256,
            StatisticReconciliationActualJson.RawSha256(snapshot.SnapshotJson ?? ""),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_BASIC_ASSIGNMENT_IDS_V1",
                assignmentIds),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_API_BASIC_REPORT_IDS_V1",
                reportIds));

        return new StatisticReconciliationActualBasicApiSourcePage(
            rows,
            filtered.Count,
            snapshot.Id,
            generationSha);
    }

    private static List<string> ExactPinnedObjectIds(
        IEnumerable<string>? values,
        string name)
    {
        var result = values?.ToList() ?? [];
        if (result.Count > MaxSourceReportsPerSummary ||
            result.Any(value =>
                !ObjectId.TryParse(value, out var parsed) ||
                !string.Equals(
                    parsed.ToString(),
                    value,
                    StringComparison.Ordinal)) ||
            result.Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw BasicApiSnapshotStale($"{name}_INVALID");
        }

        return result.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private static StatisticReconciliationActualApiEndpointException
        BasicApiSnapshotStale(string reason)
        => new(StatusCodes.Status409Conflict, reason);
}
