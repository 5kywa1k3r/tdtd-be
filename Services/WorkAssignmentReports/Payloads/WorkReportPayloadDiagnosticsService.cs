using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public interface IWorkReportPayloadDiagnosticsService
{
    Task<WorkReportPayloadDiagnosticsResult> CheckAsync(
        WorkReportPayloadDiagnosticsOptions options,
        CancellationToken ct = default);

    Task<WorkReportPayloadDiagnosticsRepairResult> RepairAsync(
        WorkReportPayloadDiagnosticsRepairOptions options,
        CancellationToken ct = default);
}

public sealed class WorkReportPayloadDiagnosticsOptions
{
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class WorkReportPayloadDiagnosticsRepairOptions
{
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public bool DryRun { get; init; } = true;
    public bool SoftDeleteOrphanPayloadRows { get; init; } = true;
    public bool SoftDeleteOrphanTableValueRows { get; init; } = true;
    public bool EnqueueStatisticRebuilds { get; init; } = true;
    public bool HighPriorityStatisticRebuilds { get; init; } = true;
    public string ByUserId { get; init; } = "system";
    public int Limit { get; init; } = 100;
}

public sealed class WorkReportPayloadDiagnosticsResult
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public int Limit { get; init; }
    public int ScannedReportCount { get; set; }
    public int ScannedPayloadRowCount { get; set; }
    public int ScannedTableValueRowCount { get; set; }
    public int ScannedStatValueRowCount { get; set; }
    public List<WorkReportPayloadDiagnosticIssue> Issues { get; init; } = new();
    public int IssueCount => Issues.Count;
    public bool HasIssues => Issues.Count > 0;
    public Dictionary<string, int> IssueCountsByType => Issues
        .GroupBy(x => x.Type)
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
}

public sealed class WorkReportPayloadDiagnosticIssue
{
    public string Type { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? WorkAssignmentReportId { get; init; }
    public string? PayloadId { get; init; }
    public string? TableValueId { get; init; }
    public string? StatValueId { get; init; }
    public string? StatCollection { get; init; }
    public string Message { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public Dictionary<string, string?> Fields { get; init; } = new();
}

public sealed class WorkReportPayloadDiagnosticsRepairResult
{
    public bool DryRun { get; init; } = true;
    public int Limit { get; init; }
    public bool SoftDeleteOrphanPayloadRows { get; init; }
    public bool SoftDeleteOrphanTableValueRows { get; init; }
    public bool EnqueueStatisticRebuilds { get; init; }
    public bool HighPriorityStatisticRebuilds { get; init; }
    public WorkReportPayloadDiagnosticsResult Diagnostics { get; init; } = new();
    public int PlannedOrphanPayloadRows { get; set; }
    public int SoftDeletedPayloadRows { get; set; }
    public int PlannedOrphanTableValueRows { get; set; }
    public int SoftDeletedTableValueRows { get; set; }
    public int PlannedStatisticTemplateRebuilds { get; set; }
    public int EnqueuedStatisticTemplateRebuilds { get; set; }
    public List<StatisticRebuildJobEnqueueResult> StatisticRebuildJobs { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public int FailedCount => Errors.Count;
}

public sealed class WorkReportPayloadDiagnosticsService : IWorkReportPayloadDiagnosticsService
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly MongoDbContext _ctx;
    private readonly IWorkReportStatisticRebuildJobService _statisticRebuildJobs;

    public WorkReportPayloadDiagnosticsService(
        MongoDbContext ctx,
        IWorkReportStatisticRebuildJobService statisticRebuildJobs)
    {
        _ctx = ctx;
        _statisticRebuildJobs = statisticRebuildJobs;
    }

    public async Task<WorkReportPayloadDiagnosticsResult> CheckAsync(
        WorkReportPayloadDiagnosticsOptions options,
        CancellationToken ct = default)
    {
        options ??= new WorkReportPayloadDiagnosticsOptions();
        var scope = Normalize(options);

        var result = new WorkReportPayloadDiagnosticsResult
        {
            WorkId = scope.WorkId,
            WorkAssignmentId = scope.WorkAssignmentId,
            WorkReportPeriodId = scope.WorkReportPeriodId,
            WorkAssignmentReportId = scope.WorkAssignmentReportId,
            Limit = scope.Limit
        };

        var reports = await LoadScopedReportsAsync(scope, ct);
        result.ScannedReportCount = reports.Count;
        var reportsById = reports
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var scopedReportIds = reportsById.Keys.ToList();

        await CheckReportHeadersAsync(scope, result, reports, ct);
        await CheckPayloadRowsAsync(scope, result, scopedReportIds, ct);
        await CheckTableValueRowsAsync(scope, result, scopedReportIds, ct);
        await CheckStatProjectionAsync(scope, result, scopedReportIds, ct);

        return result;
    }

    public async Task<WorkReportPayloadDiagnosticsRepairResult> RepairAsync(
        WorkReportPayloadDiagnosticsRepairOptions options,
        CancellationToken ct = default)
    {
        options ??= new WorkReportPayloadDiagnosticsRepairOptions();
        var normalized = NormalizeRepair(options);
        var diagnostics = await CheckAsync(ToDiagnosticsOptions(normalized), ct);

        var result = new WorkReportPayloadDiagnosticsRepairResult
        {
            DryRun = normalized.DryRun,
            Limit = normalized.Limit,
            SoftDeleteOrphanPayloadRows = normalized.SoftDeleteOrphanPayloadRows,
            SoftDeleteOrphanTableValueRows = normalized.SoftDeleteOrphanTableValueRows,
            EnqueueStatisticRebuilds = normalized.EnqueueStatisticRebuilds,
            HighPriorityStatisticRebuilds = normalized.HighPriorityStatisticRebuilds,
            Diagnostics = diagnostics
        };

        await RepairOrphanPayloadRowsAsync(normalized, diagnostics, result, ct);
        await RepairOrphanTableRowsAsync(normalized, diagnostics, result, ct);
        await EnqueueStaleStatisticRebuildsAsync(normalized, diagnostics, result, ct);

        return result;
    }

    private async Task CheckReportHeadersAsync(
        DiagnosticScope scope,
        WorkReportPayloadDiagnosticsResult result,
        IReadOnlyList<WorkAssignmentReport> reports,
        CancellationToken ct)
    {
        var reportIds = reports
            .Where(HasReadyPayloadHeader)
            .Select(x => x.Id)
            .Where(NotBlank)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (reportIds.Count == 0)
            return;

        var payloads = await _ctx.WorkReportPayloads
            .Find(x => reportIds.Contains(x.ReportId) && !x.IsDeleted)
            .ToListAsync(ct);
        var payloadByReportId = payloads
            .GroupBy(x => x.ReportId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.UpdatedAtUtc).First(), StringComparer.Ordinal);
        var tableRows = await _ctx.WorkReportTableValues
            .Find(x =>
                reportIds.Contains(x.ReportId) &&
                x.Status == WorkReportPayloadStatus.Ready &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var tableRowsByReportId = tableRows
            .GroupBy(x => x.ReportId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        foreach (var report in reports.Where(HasReadyPayloadHeader))
        {
            if (!payloadByReportId.TryGetValue(report.Id, out var payload))
            {
                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "HEADER_PAYLOAD_MISSING",
                    Key = $"report:{report.Id}",
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    Message = "Report header points to a ready external payload, but no active payload row exists.",
                    RecommendedAction = "Rewrite the report payload from the report save path or rebuild the report from embedded compatibility fields if still available.",
                    Fields = HeaderFields(report)
                });
                continue;
            }

            var mismatch = payload.PayloadRevision != report.PayloadRevision ||
                           !string.Equals(payload.PayloadHash, report.PayloadHash, StringComparison.Ordinal) ||
                           !string.Equals(payload.Status, report.PayloadStatus, StringComparison.Ordinal);

            if (mismatch)
            {
                var fields = HeaderFields(report);
                fields["payloadId"] = payload.Id;
                fields["payloadRevision"] = payload.PayloadRevision.ToString();
                fields["payloadHash"] = payload.PayloadHash;
                fields["payloadStatus"] = payload.Status;

                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "HEADER_PAYLOAD_MISMATCH",
                    Key = $"report:{report.Id}:payload:{payload.Id}",
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    PayloadId = payload.Id,
                    Message = "Report header payload pointer does not match the active payload row.",
                    RecommendedAction = "Repair the report header pointer or rewrite the payload/header pair from the report save path before statistic rebuild.",
                    Fields = fields
                });
                continue;
            }

            tableRowsByReportId.TryGetValue(report.Id, out var reportTableRows);
            var payloadTableRows = (reportTableRows ?? new List<WorkReportTableValue>())
                .Where(x => x.PayloadRevision == payload.PayloadRevision)
                .ToList();
            var actualPayloadHash = WorkReportPayloadHash.Compute(
                payload.Values1DJson,
                payload.FieldValuesJson,
                payload.TableValuesRootJson,
                payload.SummarySourceJson,
                payloadTableRows.Select(x => new WorkReportPayloadBlockHash(x.BlockId, x.BlockOrder, x.PayloadHash)));

            if (string.Equals(actualPayloadHash, payload.PayloadHash, StringComparison.Ordinal))
                continue;

            var hashFields = HeaderFields(report);
            hashFields["payloadId"] = payload.Id;
            hashFields["payloadRevision"] = payload.PayloadRevision.ToString();
            hashFields["payloadHash"] = payload.PayloadHash;
            hashFields["actualPayloadHash"] = actualPayloadHash;
            hashFields["tableRowCount"] = payloadTableRows.Count.ToString();

            AddIssue(result, new WorkReportPayloadDiagnosticIssue
            {
                Type = "PAYLOAD_HASH_UNVERIFIED",
                Key = $"report:{report.Id}:payload:{payload.Id}:hash",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                PayloadId = payload.Id,
                Message = "External payload row hash does not verify against the currently active table payload rows.",
                RecommendedAction = "Rewrite the report payload so top-level and table payload rows form one complete ready payload before statistic rebuild.",
                Fields = hashFields
            });
        }
    }

    private async Task CheckPayloadRowsAsync(
        DiagnosticScope scope,
        WorkReportPayloadDiagnosticsResult result,
        IReadOnlyCollection<string> scopedReportIds,
        CancellationToken ct)
    {
        if (ShouldSkipPayloadOnlyCollectionForEmptySourceScope(scope, scopedReportIds))
            return;

        var fb = Builders<WorkReportPayload>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId))
            filter &= fb.Eq(x => x.ReportId, scope.WorkAssignmentReportId);
        else if (scopedReportIds.Count > 0 && HasSourceScope(scope))
            filter &= fb.In(x => x.ReportId, scopedReportIds);

        var payloads = await _ctx.WorkReportPayloads
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Limit(scope.Limit)
            .ToListAsync(ct);

        result.ScannedPayloadRowCount = payloads.Count;
        if (payloads.Count == 0)
            return;

        var reportIds = payloads.Select(x => x.ReportId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        var reportMap = await LoadReportsByIdAsync(reportIds, ct);

        foreach (var payload in payloads)
        {
            if (!reportMap.TryGetValue(payload.ReportId, out var report) || report.IsDeleted)
            {
                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "ORPHAN_PAYLOAD_ROW",
                    Key = $"payload:{payload.Id}",
                    WorkAssignmentReportId = payload.ReportId,
                    PayloadId = payload.Id,
                    Message = "Active payload row points to a missing or deleted report.",
                    RecommendedAction = "Soft-delete the orphan payload row after confirming no report header references it.",
                    Fields = PayloadFields(payload)
                });
                continue;
            }

            if (!HasReadyPayloadHeader(report))
            {
                var fields = PayloadFields(payload);
                fields["reportPayloadRevision"] = report.PayloadRevision.ToString();
                fields["reportPayloadHash"] = report.PayloadHash;
                fields["reportPayloadStatus"] = report.PayloadStatus;

                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "PAYLOAD_WITH_UNREADY_HEADER",
                    Key = $"report:{report.Id}:payload:{payload.Id}",
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    PayloadId = payload.Id,
                    Message = "Active payload row exists, but the report header is not ready for external payload reads.",
                    RecommendedAction = "Repair the report header pointer or mark the payload row deleted if it is from an abandoned write.",
                    Fields = fields
                });
            }
        }
    }

    private async Task CheckTableValueRowsAsync(
        DiagnosticScope scope,
        WorkReportPayloadDiagnosticsResult result,
        IReadOnlyCollection<string> scopedReportIds,
        CancellationToken ct)
    {
        if (ShouldSkipPayloadOnlyCollectionForEmptySourceScope(scope, scopedReportIds))
            return;

        var fb = Builders<WorkReportTableValue>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId))
            filter &= fb.Eq(x => x.ReportId, scope.WorkAssignmentReportId);
        else if (scopedReportIds.Count > 0 && HasSourceScope(scope))
            filter &= fb.In(x => x.ReportId, scopedReportIds);

        var rows = await _ctx.WorkReportTableValues
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Limit(scope.Limit)
            .ToListAsync(ct);

        result.ScannedTableValueRowCount = rows.Count;
        if (rows.Count == 0)
            return;

        var reportIds = rows.Select(x => x.ReportId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        var reportMap = await LoadReportsByIdAsync(reportIds, ct);
        var payloads = await _ctx.WorkReportPayloads
            .Find(x => reportIds.Contains(x.ReportId) && !x.IsDeleted)
            .ToListAsync(ct);
        var payloadByReportId = payloads
            .GroupBy(x => x.ReportId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(p => p.UpdatedAtUtc).First(), StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!reportMap.TryGetValue(row.ReportId, out var report) || report.IsDeleted)
            {
                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "ORPHAN_TABLE_VALUE_ROW",
                    Key = $"tableValue:{row.Id}",
                    WorkAssignmentReportId = row.ReportId,
                    TableValueId = row.Id,
                    Message = "Active table payload row points to a missing or deleted report.",
                    RecommendedAction = "Soft-delete the orphan table payload row after confirming no report header references it.",
                    Fields = TableFields(row)
                });
                continue;
            }

            if (!payloadByReportId.TryGetValue(row.ReportId, out var payload))
            {
                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "TABLE_VALUE_PAYLOAD_MISSING",
                    Key = $"report:{report.Id}:tableValue:{row.Id}",
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    TableValueId = row.Id,
                    Message = "Active table payload row exists, but the report has no active top-level payload row.",
                    RecommendedAction = "Rewrite the report payload so top-level and table payload rows share one ready revision.",
                    Fields = TableFields(row)
                });
                continue;
            }

            if (row.PayloadRevision == report.PayloadRevision &&
                row.PayloadRevision == payload.PayloadRevision &&
                string.Equals(row.Status, WorkReportPayloadStatus.Ready, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = TableFields(row);
            fields["reportPayloadRevision"] = report.PayloadRevision.ToString();
            fields["payloadRevision"] = payload.PayloadRevision.ToString();
            fields["reportPayloadStatus"] = report.PayloadStatus;
            fields["payloadStatus"] = payload.Status;

            AddIssue(result, new WorkReportPayloadDiagnosticIssue
            {
                Type = "TABLE_VALUE_REVISION_MISMATCH",
                Key = $"report:{report.Id}:tableValue:{row.Id}",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                PayloadId = payload.Id,
                TableValueId = row.Id,
                Message = "Table payload row revision/status does not match the report header and top-level payload row.",
                RecommendedAction = "Rewrite the report payload so every active table block belongs to the current ready revision.",
                Fields = fields
            });
        }
    }

    private async Task CheckStatProjectionAsync(
        DiagnosticScope scope,
        WorkReportPayloadDiagnosticsResult result,
        IReadOnlyCollection<string> scopedReportIds,
        CancellationToken ct)
    {
        var probes = new List<StatProjectionProbe>();
        probes.AddRange(await LoadFieldStatProbesAsync(scope, scopedReportIds, ct));
        probes.AddRange(await LoadTableStatProbesAsync(scope, scopedReportIds, ct));
        probes.AddRange(await LoadLabelStatProbesAsync(scope, scopedReportIds, ct));
        result.ScannedStatValueRowCount = probes.Count;

        if (probes.Count == 0)
            return;

        var reportIds = probes.Select(x => x.WorkAssignmentReportId).Where(NotBlank).Distinct(StringComparer.Ordinal).ToList();
        var reports = await LoadReportsByIdAsync(reportIds, ct);

        foreach (var group in probes.GroupBy(x => new { x.StatCollection, x.WorkAssignmentReportId }))
        {
            var probe = group.First();
            if (!reports.TryGetValue(probe.WorkAssignmentReportId, out var report) || report.IsDeleted)
            {
                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "STAT_SOURCE_REPORT_MISSING",
                    Key = $"{probe.StatCollection}:report:{probe.WorkAssignmentReportId}",
                    WorkId = probe.WorkId,
                    WorkAssignmentId = probe.WorkAssignmentId,
                    WorkReportPeriodId = probe.WorkReportPeriodId,
                    WorkAssignmentReportId = probe.WorkAssignmentReportId,
                    StatValueId = probe.StatValueId,
                    StatCollection = probe.StatCollection,
                    Message = "Active statistic value row points to a missing or deleted report.",
                    RecommendedAction = "Rebuild statistics for the affected template/report scope so orphan stat values are soft-deleted.",
                    Fields = StatFields(probe, report: null)
                });
                continue;
            }

            if (!HasReadyPayloadHeader(report))
            {
                AddIssue(result, new WorkReportPayloadDiagnosticIssue
                {
                    Type = "STAT_SOURCE_PAYLOAD_NOT_READY",
                    Key = $"{probe.StatCollection}:report:{report.Id}",
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    StatValueId = probe.StatValueId,
                    StatCollection = probe.StatCollection,
                    Message = "Statistic value row exists for a report whose payload header is not ready.",
                    RecommendedAction = "Repair the report payload/header pair before rebuilding statistics.",
                    Fields = StatFields(probe, report)
                });
                continue;
            }

            if (probe.SourcePayloadRevision == report.PayloadRevision &&
                string.Equals(probe.SourcePayloadHash, report.PayloadHash, StringComparison.Ordinal))
            {
                continue;
            }

            AddIssue(result, new WorkReportPayloadDiagnosticIssue
            {
                Type = "STALE_STAT_PROJECTION",
                Key = $"{probe.StatCollection}:report:{report.Id}",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                StatValueId = probe.StatValueId,
                StatCollection = probe.StatCollection,
                Message = "Statistic value row was projected from an older payload revision/hash than the current report header.",
                RecommendedAction = "Rebuild field/table/label statistics for this report or enqueue a template-level statistic rebuild.",
                Fields = StatFields(probe, report)
            });
        }
    }

    private async Task<List<StatProjectionProbe>> LoadFieldStatProbesAsync(
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportFieldStatValue>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        filter = ApplyStatScope(filter, fb, scope, scopedReportIds);

        return await _ctx.WorkReportFieldStatValues
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Limit(scope.Limit)
            .Project(x => new StatProjectionProbe
            {
                StatCollection = "work_report_field_stat_values",
                StatValueId = x.Id,
                WorkId = x.WorkId,
                WorkAssignmentId = x.WorkAssignmentId,
                WorkReportPeriodId = x.WorkReportPeriodId,
                WorkAssignmentReportId = x.WorkAssignmentReportId,
                SourcePayloadRevision = x.SourcePayloadRevision,
                SourcePayloadHash = x.SourcePayloadHash
            })
            .ToListAsync(ct);
    }

    private async Task<List<StatProjectionProbe>> LoadTableStatProbesAsync(
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportTableStatValue>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        filter = ApplyStatScope(filter, fb, scope, scopedReportIds);

        return await _ctx.WorkReportTableStatValues
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Limit(scope.Limit)
            .Project(x => new StatProjectionProbe
            {
                StatCollection = "work_report_table_stat_values",
                StatValueId = x.Id,
                WorkId = x.WorkId,
                WorkAssignmentId = x.WorkAssignmentId,
                WorkReportPeriodId = x.WorkReportPeriodId,
                WorkAssignmentReportId = x.WorkAssignmentReportId,
                SourcePayloadRevision = x.SourcePayloadRevision,
                SourcePayloadHash = x.SourcePayloadHash
            })
            .ToListAsync(ct);
    }

    private async Task<List<StatProjectionProbe>> LoadLabelStatProbesAsync(
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportLabelStatValue>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        filter = ApplyStatScope(filter, fb, scope, scopedReportIds);

        return await _ctx.WorkReportLabelStatValues
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Limit(scope.Limit)
            .Project(x => new StatProjectionProbe
            {
                StatCollection = "work_report_label_stat_values",
                StatValueId = x.Id,
                WorkId = x.WorkId,
                WorkAssignmentId = x.WorkAssignmentId,
                WorkReportPeriodId = x.WorkReportPeriodId,
                WorkAssignmentReportId = x.WorkAssignmentReportId,
                SourcePayloadRevision = x.SourcePayloadRevision,
                SourcePayloadHash = x.SourcePayloadHash
            })
            .ToListAsync(ct);
    }

    private async Task<List<WorkAssignmentReport>> LoadScopedReportsAsync(
        DiagnosticScope scope,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId))
            filter &= fb.Eq(x => x.Id, scope.WorkAssignmentReportId);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentId))
            filter &= fb.Eq(x => x.WorkAssignmentId, scope.WorkAssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.WorkReportPeriodId, scope.WorkReportPeriodId);

        return await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Limit(scope.Limit)
            .ToListAsync(ct);
    }

    private async Task<Dictionary<string, WorkAssignmentReport>> LoadReportsByIdAsync(
        IReadOnlyCollection<string> reportIds,
        CancellationToken ct)
    {
        if (reportIds.Count == 0)
            return new Dictionary<string, WorkAssignmentReport>(StringComparer.Ordinal);

        var reports = await _ctx.WorkAssignmentReports
            .Find(x => reportIds.Contains(x.Id))
            .ToListAsync(ct);

        return reports
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
    }

    private static FilterDefinition<WorkReportFieldStatValue> ApplyStatScope(
        FilterDefinition<WorkReportFieldStatValue> filter,
        FilterDefinitionBuilder<WorkReportFieldStatValue> fb,
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds)
    {
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId))
            return filter & fb.Eq(x => x.WorkAssignmentReportId, scope.WorkAssignmentReportId);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentId))
            filter &= fb.Eq(x => x.WorkAssignmentId, scope.WorkAssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.WorkReportPeriodId, scope.WorkReportPeriodId);
        else if (scopedReportIds.Count > 0 && HasSourceScope(scope))
            filter &= fb.In(x => x.WorkAssignmentReportId, scopedReportIds);
        return filter;
    }

    private static FilterDefinition<WorkReportTableStatValue> ApplyStatScope(
        FilterDefinition<WorkReportTableStatValue> filter,
        FilterDefinitionBuilder<WorkReportTableStatValue> fb,
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds)
    {
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId))
            return filter & fb.Eq(x => x.WorkAssignmentReportId, scope.WorkAssignmentReportId);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentId))
            filter &= fb.Eq(x => x.WorkAssignmentId, scope.WorkAssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.WorkReportPeriodId, scope.WorkReportPeriodId);
        else if (scopedReportIds.Count > 0 && HasSourceScope(scope))
            filter &= fb.In(x => x.WorkAssignmentReportId, scopedReportIds);
        return filter;
    }

    private static FilterDefinition<WorkReportLabelStatValue> ApplyStatScope(
        FilterDefinition<WorkReportLabelStatValue> filter,
        FilterDefinitionBuilder<WorkReportLabelStatValue> fb,
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds)
    {
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId))
            return filter & fb.Eq(x => x.WorkAssignmentReportId, scope.WorkAssignmentReportId);
        if (!string.IsNullOrWhiteSpace(scope.WorkId))
            filter &= fb.Eq(x => x.WorkId, scope.WorkId);
        if (!string.IsNullOrWhiteSpace(scope.WorkAssignmentId))
            filter &= fb.Eq(x => x.WorkAssignmentId, scope.WorkAssignmentId);
        if (!string.IsNullOrWhiteSpace(scope.WorkReportPeriodId))
            filter &= fb.Eq(x => x.WorkReportPeriodId, scope.WorkReportPeriodId);
        else if (scopedReportIds.Count > 0 && HasSourceScope(scope))
            filter &= fb.In(x => x.WorkAssignmentReportId, scopedReportIds);
        return filter;
    }

    private static DiagnosticScope Normalize(WorkReportPayloadDiagnosticsOptions options)
        => new(
            NullIfWhiteSpace(options.WorkId),
            NullIfWhiteSpace(options.WorkAssignmentId),
            NullIfWhiteSpace(options.WorkReportPeriodId),
            NullIfWhiteSpace(options.WorkAssignmentReportId),
            Math.Clamp(options.Limit <= 0 ? DefaultLimit : options.Limit, 1, MaxLimit));

    private static WorkReportPayloadDiagnosticsRepairOptions NormalizeRepair(
        WorkReportPayloadDiagnosticsRepairOptions options)
        => new()
        {
            WorkId = NullIfWhiteSpace(options.WorkId),
            WorkAssignmentId = NullIfWhiteSpace(options.WorkAssignmentId),
            WorkReportPeriodId = NullIfWhiteSpace(options.WorkReportPeriodId),
            WorkAssignmentReportId = NullIfWhiteSpace(options.WorkAssignmentReportId),
            DryRun = options.DryRun,
            SoftDeleteOrphanPayloadRows = options.SoftDeleteOrphanPayloadRows,
            SoftDeleteOrphanTableValueRows = options.SoftDeleteOrphanTableValueRows,
            EnqueueStatisticRebuilds = options.EnqueueStatisticRebuilds,
            HighPriorityStatisticRebuilds = options.HighPriorityStatisticRebuilds,
            ByUserId = string.IsNullOrWhiteSpace(options.ByUserId) ? "system" : options.ByUserId.Trim(),
            Limit = Math.Clamp(options.Limit <= 0 ? DefaultLimit : options.Limit, 1, MaxLimit)
        };

    private static WorkReportPayloadDiagnosticsOptions ToDiagnosticsOptions(
        WorkReportPayloadDiagnosticsRepairOptions options)
        => new()
        {
            WorkId = options.WorkId,
            WorkAssignmentId = options.WorkAssignmentId,
            WorkReportPeriodId = options.WorkReportPeriodId,
            WorkAssignmentReportId = options.WorkAssignmentReportId,
            Limit = options.Limit
        };

    private async Task RepairOrphanPayloadRowsAsync(
        WorkReportPayloadDiagnosticsRepairOptions options,
        WorkReportPayloadDiagnosticsResult diagnostics,
        WorkReportPayloadDiagnosticsRepairResult result,
        CancellationToken ct)
    {
        if (!options.SoftDeleteOrphanPayloadRows)
            return;

        var payloadIds = diagnostics.Issues
            .Where(x => x.Type == "ORPHAN_PAYLOAD_ROW")
            .Select(x => x.PayloadId)
            .Where(NotBlank)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        result.PlannedOrphanPayloadRows = payloadIds.Count;
        if (options.DryRun || payloadIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var payloadId in payloadIds)
        {
            try
            {
                var update = await _ctx.WorkReportPayloads.UpdateOneAsync(
                    x => x.Id == payloadId && !x.IsDeleted,
                    Builders<WorkReportPayload>.Update
                        .Set(x => x.IsDeleted, true)
                        .Set(x => x.DeletedAtUtc, now)
                        .Set(x => x.DeletedByUserId, options.ByUserId)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, options.ByUserId),
                    cancellationToken: ct);

                result.SoftDeletedPayloadRows += (int)update.ModifiedCount;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"payload:{payloadId}: {ex.Message}");
            }
        }
    }

    private async Task RepairOrphanTableRowsAsync(
        WorkReportPayloadDiagnosticsRepairOptions options,
        WorkReportPayloadDiagnosticsResult diagnostics,
        WorkReportPayloadDiagnosticsRepairResult result,
        CancellationToken ct)
    {
        if (!options.SoftDeleteOrphanTableValueRows)
            return;

        var tableValueIds = diagnostics.Issues
            .Where(x => x.Type == "ORPHAN_TABLE_VALUE_ROW")
            .Select(x => x.TableValueId)
            .Where(NotBlank)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        result.PlannedOrphanTableValueRows = tableValueIds.Count;
        if (options.DryRun || tableValueIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var tableValueId in tableValueIds)
        {
            try
            {
                var update = await _ctx.WorkReportTableValues.UpdateOneAsync(
                    x => x.Id == tableValueId && !x.IsDeleted,
                    Builders<WorkReportTableValue>.Update
                        .Set(x => x.IsDeleted, true)
                        .Set(x => x.DeletedAtUtc, now)
                        .Set(x => x.DeletedByUserId, options.ByUserId)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, options.ByUserId),
                    cancellationToken: ct);

                result.SoftDeletedTableValueRows += (int)update.ModifiedCount;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"tableValue:{tableValueId}: {ex.Message}");
            }
        }
    }

    private async Task EnqueueStaleStatisticRebuildsAsync(
        WorkReportPayloadDiagnosticsRepairOptions options,
        WorkReportPayloadDiagnosticsResult diagnostics,
        WorkReportPayloadDiagnosticsRepairResult result,
        CancellationToken ct)
    {
        if (!options.EnqueueStatisticRebuilds)
            return;

        var staleReportIds = diagnostics.Issues
            .Where(x => x.Type == "STALE_STAT_PROJECTION")
            .Select(x => x.WorkAssignmentReportId)
            .Where(NotBlank)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (staleReportIds.Count == 0)
            return;

        var reports = await _ctx.WorkAssignmentReports
            .Find(x => staleReportIds.Contains(x.Id) && !x.IsDeleted)
            .Project(x => new { x.DynamicFormTemplateId })
            .ToListAsync(ct);

        var templateIds = reports
            .Select(x => x.DynamicFormTemplateId)
            .Where(NotBlank)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        result.PlannedStatisticTemplateRebuilds = templateIds.Count;
        if (options.DryRun || templateIds.Count == 0)
            return;

        var templates = await _ctx.DynamicFormTemplates
            .Find(x => templateIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        foreach (var template in templates)
        {
            try
            {
                var enqueued = await _statisticRebuildJobs.EnqueueForTemplateStatisticConfigAsync(
                    template,
                    options.ByUserId,
                    options.HighPriorityStatisticRebuilds,
                    ct);

                result.StatisticRebuildJobs.Add(enqueued);
                result.EnqueuedStatisticTemplateRebuilds++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"template:{template.Id}: {ex.Message}");
            }
        }
    }

    private static bool HasSourceScope(DiagnosticScope scope)
        => !string.IsNullOrWhiteSpace(scope.WorkId)
           || !string.IsNullOrWhiteSpace(scope.WorkAssignmentId)
           || !string.IsNullOrWhiteSpace(scope.WorkReportPeriodId)
           || !string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId);

    private static bool ShouldSkipPayloadOnlyCollectionForEmptySourceScope(
        DiagnosticScope scope,
        IReadOnlyCollection<string> scopedReportIds)
        => HasSourceScope(scope)
           && string.IsNullOrWhiteSpace(scope.WorkAssignmentReportId)
           && scopedReportIds.Count == 0;

    private static bool HasReadyPayloadHeader(WorkAssignmentReport report)
        => report.PayloadRevision > 0
           && !string.IsNullOrWhiteSpace(report.PayloadHash)
           && string.Equals(report.PayloadStatus, WorkReportPayloadStatus.Ready, StringComparison.Ordinal);

    private static bool NotBlank(string? value)
        => !string.IsNullOrWhiteSpace(value);

    private static string? NullIfWhiteSpace(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void AddIssue(
        WorkReportPayloadDiagnosticsResult result,
        WorkReportPayloadDiagnosticIssue issue)
        => result.Issues.Add(issue);

    private static Dictionary<string, string?> HeaderFields(WorkAssignmentReport report)
        => new(StringComparer.Ordinal)
        {
            ["reportPayloadRevision"] = report.PayloadRevision.ToString(),
            ["reportPayloadHash"] = report.PayloadHash,
            ["reportPayloadStatus"] = report.PayloadStatus,
            ["payloadUpdatedAtUtc"] = Format(report.PayloadUpdatedAtUtc),
            ["reportUpdatedAtUtc"] = Format(report.UpdatedAtUtc)
        };

    private static Dictionary<string, string?> PayloadFields(WorkReportPayload payload)
        => new(StringComparer.Ordinal)
        {
            ["payloadRevision"] = payload.PayloadRevision.ToString(),
            ["payloadHash"] = payload.PayloadHash,
            ["payloadStatus"] = payload.Status,
            ["payloadSizeBytes"] = payload.PayloadSizeBytes.ToString(),
            ["payloadUpdatedAtUtc"] = Format(payload.UpdatedAtUtc)
        };

    private static Dictionary<string, string?> TableFields(WorkReportTableValue row)
        => new(StringComparer.Ordinal)
        {
            ["blockId"] = row.BlockId,
            ["payloadRevision"] = row.PayloadRevision.ToString(),
            ["payloadHash"] = row.PayloadHash,
            ["payloadStatus"] = row.Status,
            ["sizeBytes"] = row.SizeBytes.ToString(),
            ["rowUpdatedAtUtc"] = Format(row.UpdatedAtUtc)
        };

    private static Dictionary<string, string?> StatFields(
        StatProjectionProbe probe,
        WorkAssignmentReport? report)
        => new(StringComparer.Ordinal)
        {
            ["sourcePayloadRevision"] = probe.SourcePayloadRevision.ToString(),
            ["sourcePayloadHash"] = probe.SourcePayloadHash,
            ["reportPayloadRevision"] = report?.PayloadRevision.ToString(),
            ["reportPayloadHash"] = report?.PayloadHash,
            ["reportPayloadStatus"] = report?.PayloadStatus,
            ["statCollection"] = probe.StatCollection
        };

    private static string? Format(DateTime? value)
        => value?.ToUniversalTime().ToString("O");

    private sealed record DiagnosticScope(
        string? WorkId,
        string? WorkAssignmentId,
        string? WorkReportPeriodId,
        string? WorkAssignmentReportId,
        int Limit);

    private sealed class StatProjectionProbe
    {
        public string StatCollection { get; init; } = string.Empty;
        public string StatValueId { get; init; } = string.Empty;
        public string WorkId { get; init; } = string.Empty;
        public string WorkAssignmentId { get; init; } = string.Empty;
        public string WorkReportPeriodId { get; init; } = string.Empty;
        public string WorkAssignmentReportId { get; init; } = string.Empty;
        public int SourcePayloadRevision { get; init; }
        public string? SourcePayloadHash { get; init; }
    }
}
