using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed class WorkReportLabelStatisticsService : IWorkReportLabelStatisticsService
{
    private static readonly Regex LabelCodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IWorkReportPayloadReader _payloadReader;

    public WorkReportLabelStatisticsService(
        MongoDbContext ctx,
        MeAccessor me,
        IWorkReportPayloadReader payloadReader)
    {
        _ctx = ctx;
        _me = me;
        _payloadReader = payloadReader;
    }

    public async Task RebuildForReportAsync(
        string reportId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Projection);
        var aggregateKey = await RebuildValuesForReportAsync(reportId, actorUserId, ct);
        if (aggregateKey is null)
            return;

        await RebuildAggregatesForWorkPeriodAsync(
            aggregateKey.WorkId,
            aggregateKey.PeriodInstanceKey,
            aggregateKey.DynamicFormTemplateId,
            actorUserId,
            ct);
    }

    public async Task<ReportStatisticAggregateKey?> RebuildValuesForReportAsync(
        string reportId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Projection);
        if (string.IsNullOrWhiteSpace(reportId))
            return null;

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (report is null)
        {
            await _ctx.WorkReportLabelStatValues
                .DeleteManyAsync(
                    x => x.WorkAssignmentReportId == reportId &&
                         x.DirectProjection == null,
                    ct);
            return null;
        }

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _ctx.WorkReportLabelStatValues.DeleteManyAsync(
            x => x.WorkAssignmentReportId == report.Id &&
                 x.DirectProjection == null,
            ct);

        if (report.IsActive == false || report.Status != WorkAssignmentReportStatus.Approved)
        {
            return new ReportStatisticAggregateKey(
                report.WorkId,
                report.PeriodInstanceKey,
                report.DynamicFormTemplateId);
        }

        var contributionPolicy = WorkReportCumulativeContributionPolicy.FromReport(report);
        if (!contributionPolicy.IncludesReport)
        {
            return new ReportStatisticAggregateKey(
                report.WorkId,
                report.PeriodInstanceKey,
                report.DynamicFormTemplateId);
        }

        var now = DateTime.UtcNow;
        WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);
        var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        var rowLabels = ExtractRowLabels(payload.TableValuesJson)
            .Select(row => row with
            {
                LabelCodes = row.LabelCodes
                    .Where(labelCode => contributionPolicy.ShouldIncludeLabel(
                        row.BlockId,
                        row.RowKey,
                        row.Source,
                        labelCode))
                    .ToList()
            })
            .Where(row => row.LabelCodes.Count > 0)
            .ToList();

        if (rowLabels.Count > 0)
        {
            var activeLabelCodes = await LoadActiveLabelCodesAsync(
                rowLabels.SelectMany(x => x.LabelCodes),
                ct);
            rowLabels = rowLabels
                .Select(row => row with
                {
                    LabelCodes = row.LabelCodes
                        .Where(activeLabelCodes.Contains)
                        .ToList()
                })
                .Where(row => row.LabelCodes.Count > 0)
                .ToList();
        }

        if (rowLabels.Count > 0)
        {
            var ancestorAssignmentIds = ExtractAncestorAssignmentIds(assignment, report.WorkAssignmentId);
            var sourceWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(report);
            var projectionContext = WorkReportStatisticProjectionContextBuilder.From(report, assignment, period, payload);
            var values = rowLabels.SelectMany(row =>
                row.LabelCodes.Select(labelCode => new WorkReportLabelStatValue
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    AssigneeUserId = projectionContext.AssigneeUserId,
                    AssigneeUnitId = projectionContext.AssigneeUnitId,
                    AssignmentIsActive = projectionContext.AssignmentIsActive,
                    ReportIsActive = projectionContext.ReportIsActive,
                    RootAssignmentId = assignment?.RootAssignmentId,
                    AncestorAssignmentIds = ancestorAssignmentIds,
                    FlowTemplateId = projectionContext.FlowTemplateId,
                    FlowTemplateVersionNo = projectionContext.FlowTemplateVersionNo,
                    FlowInstanceId = projectionContext.FlowInstanceId,
                    FlowStepId = projectionContext.FlowStepId,
                    FlowStepCode = projectionContext.FlowStepCode,
                    FlowStepOrder = projectionContext.FlowStepOrder,
                    FlowBranchId = projectionContext.FlowBranchId,
                    ParentFlowBranchId = projectionContext.ParentFlowBranchId,
                    FlowAttemptNo = projectionContext.FlowAttemptNo,
                    FlowRole = projectionContext.FlowRole,
                    IsFlowFinalNode = projectionContext.IsFlowFinalNode,
                    FlowEffectiveStatus = projectionContext.FlowEffectiveStatus,
                    InvalidatedByFlowEventId = projectionContext.InvalidatedByFlowEventId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    DynamicFormTemplateId = NormalizeObjectIdOrNull(report.DynamicFormTemplateId),
                    DynamicFormTemplateCode = report.DynamicFormTemplateCode,
                    DynamicFormTemplateName = report.DynamicFormTemplateName,
                    DynamicExcelTemplateId = NormalizeObjectIdOrNull(row.DynamicExcelTemplateId ?? report.DynamicExcelTemplateId),
                    BlockId = row.BlockId,
                    PeriodKey = report.PeriodKey,
                    PeriodInstanceKey = report.PeriodInstanceKey,
                    PeriodKind = report.PeriodKind,
                    PeriodAnchorDate = sourceWindow.PeriodAnchorDate,
                    PeriodStartDate = sourceWindow.PeriodStartDate,
                    PeriodEndDate = sourceWindow.PeriodEndDate,
                    CompletedDate = sourceWindow.CompletedDate,
                    IsHistoricalData = sourceWindow.IsHistoricalData,
                    ReportStatus = (int)report.Status,
                    SheetId = row.SheetId,
                    RowKey = row.RowKey,
                    RowIndex = row.RowIndex,
                    LabelCode = labelCode,
                    Source = row.Source,
                    SourcePayloadRevision = projectionContext.SourcePayloadRevision,
                    SourcePayloadHash = projectionContext.SourcePayloadHash,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = NormalizeObjectIdOrNull(actorUserId),
                    UpdatedByUserId = NormalizeObjectIdOrNull(actorUserId),
                    IsDeleted = false
                }))
                .ToList();

            if (values.Count > 0)
                await _ctx.WorkReportLabelStatValues.InsertManyAsync(values, cancellationToken: ct);
        }

        return new ReportStatisticAggregateKey(
            report.WorkId,
            report.PeriodInstanceKey,
            report.DynamicFormTemplateId);
    }

    public Task<ReportStatisticAggregateKey?> StageGenerationValuesForReportAsync(
        string reportId,
        WorkReportDirectGenerationContext generation,
        string? actorUserId,
        CancellationToken ct = default)
        => StageGenerationValuesForReportCoreAsync(
            reportId,
            generation,
            actorUserId,
            generationRows: null,
            ct);

    public async Task StageGenerationValuesForReportsAsync(
        IReadOnlyCollection<string> reportIds,
        WorkReportDirectGenerationContext generation,
        string? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reportIds);
        ArgumentNullException.ThrowIfNull(generation);
        ValidateGenerationContext(generation);
        var normalizedReportIds = new List<string>(reportIds.Count);
        foreach (var reportId in reportIds)
        {
            if (string.IsNullOrWhiteSpace(reportId))
                throw new ArgumentException("P9 Direct LABEL report ids must be non-empty.", nameof(reportIds));
            normalizedReportIds.Add(reportId.Trim());
        }

        var generationRows = new List<WorkReportLabelStatValue>();
        foreach (var reportId in normalizedReportIds
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            await StageGenerationValuesForReportCoreAsync(
                reportId,
                generation,
                actorUserId,
                generationRows,
                ct);
        }

        await WorkReportDirectGenerationInsertOnly.InsertOrValidateAsync(
            _ctx.WorkReportLabelStatValues,
            generationRows,
            "work_report_label_stat_values",
            generation.GenerationId,
            ct);
    }

    private async Task<ReportStatisticAggregateKey?> StageGenerationValuesForReportCoreAsync(
        string reportId,
        WorkReportDirectGenerationContext generation,
        string? actorUserId,
        List<WorkReportLabelStatValue>? generationRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ValidateGenerationContext(generation);

        if (string.IsNullOrWhiteSpace(reportId))
            throw new ArgumentException("A report id is required for P9 Direct LABEL staging.", nameof(reportId));

        reportId = reportId.Trim();
        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw WorkReportDirectGenerationValidationException.SourceDrift();

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        EnsureApprovedEffectiveGenerationSource(report, assignment, period, generation);

        var aggregateKey = new ReportStatisticAggregateKey(
            report.WorkId,
            report.PeriodInstanceKey,
            report.DynamicFormTemplateId);
        var contributionPolicy = generation.ResolveContributionPolicy(report);
        if (!contributionPolicy.IncludesReport)
            return aggregateKey;

        WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);
        var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        var rowLabels = ExtractRowLabels(payload.TableValuesJson)
            .Select(row => row with
            {
                LabelCodes = row.LabelCodes
                    .Where(labelCode => contributionPolicy.ShouldIncludeLabel(
                        row.BlockId,
                        row.RowKey,
                        row.Source,
                        labelCode))
                    .ToList()
            })
            .Where(row => row.LabelCodes.Count > 0)
            .ToList();

        if (rowLabels.Count == 0)
            return aggregateKey;

        var actorId = NormalizeObjectIdOrNull(actorUserId);
        var ancestorAssignmentIds = ExtractAncestorAssignmentIds(assignment, report.WorkAssignmentId);
        var sourceWindow = WorkAssignmentReportTemporalPolicy.ResolveSourceWindow(report);
        var projectionContext = WorkReportStatisticProjectionContextBuilder.From(report, assignment, period, payload);
        var rows = rowLabels
            .OrderBy(row => row.DynamicExcelTemplateId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(row => row.BlockId, StringComparer.Ordinal)
            .ThenBy(row => row.SheetId, StringComparer.Ordinal)
            .ThenBy(row => row.RowIndex)
            .ThenBy(row => row.RowKey, StringComparer.Ordinal)
            .ThenBy(row => row.Source, StringComparer.Ordinal)
            .SelectMany(row => row.LabelCodes
                .OrderBy(labelCode => labelCode, StringComparer.Ordinal)
                .Select(labelCode => new WorkReportLabelStatValue
                {
                    Id = generation.StableObjectId(
                        "LABEL_VALUE",
                        report.Id,
                        report.PayloadRevision.ToString(CultureInfo.InvariantCulture),
                        report.LifecycleRevision.ToString(CultureInfo.InvariantCulture),
                        report.WorkReportPeriodId,
                        report.PeriodInstanceKey,
                        NormalizeObjectIdOrNull(report.DynamicFormTemplateId),
                        NormalizeObjectIdOrNull(row.DynamicExcelTemplateId ?? report.DynamicExcelTemplateId),
                        row.BlockId,
                        row.SheetId,
                        row.RowKey,
                        row.RowIndex.ToString(CultureInfo.InvariantCulture),
                        labelCode,
                        row.Source),
                    DirectProjection = generation.CreatePin(report),
                    WorkId = report.WorkId,
                    WorkAssignmentId = report.WorkAssignmentId,
                    AssigneeUserId = projectionContext.AssigneeUserId,
                    AssigneeUnitId = projectionContext.AssigneeUnitId,
                    AssignmentIsActive = projectionContext.AssignmentIsActive,
                    ReportIsActive = projectionContext.ReportIsActive,
                    RootAssignmentId = assignment?.RootAssignmentId,
                    AncestorAssignmentIds = ancestorAssignmentIds,
                    FlowTemplateId = projectionContext.FlowTemplateId,
                    FlowTemplateVersionNo = projectionContext.FlowTemplateVersionNo,
                    FlowInstanceId = projectionContext.FlowInstanceId,
                    FlowStepId = projectionContext.FlowStepId,
                    FlowStepCode = projectionContext.FlowStepCode,
                    FlowStepOrder = projectionContext.FlowStepOrder,
                    FlowBranchId = projectionContext.FlowBranchId,
                    ParentFlowBranchId = projectionContext.ParentFlowBranchId,
                    FlowAttemptNo = projectionContext.FlowAttemptNo,
                    FlowRole = projectionContext.FlowRole,
                    IsFlowFinalNode = projectionContext.IsFlowFinalNode,
                    FlowEffectiveStatus = projectionContext.FlowEffectiveStatus,
                    InvalidatedByFlowEventId = projectionContext.InvalidatedByFlowEventId,
                    WorkReportPeriodId = report.WorkReportPeriodId,
                    WorkAssignmentReportId = report.Id,
                    DynamicFormTemplateId = NormalizeObjectIdOrNull(report.DynamicFormTemplateId),
                    DynamicFormTemplateCode = report.DynamicFormTemplateCode,
                    DynamicFormTemplateName = report.DynamicFormTemplateName,
                    DynamicExcelTemplateId = NormalizeObjectIdOrNull(
                        row.DynamicExcelTemplateId ?? report.DynamicExcelTemplateId),
                    BlockId = row.BlockId,
                    PeriodKey = report.PeriodKey,
                    PeriodInstanceKey = report.PeriodInstanceKey,
                    PeriodKind = report.PeriodKind,
                    PeriodAnchorDate = sourceWindow.PeriodAnchorDate,
                    PeriodStartDate = sourceWindow.PeriodStartDate,
                    PeriodEndDate = sourceWindow.PeriodEndDate,
                    CompletedDate = sourceWindow.CompletedDate,
                    IsHistoricalData = sourceWindow.IsHistoricalData,
                    ReportStatus = (int)report.Status,
                    SheetId = row.SheetId,
                    RowKey = row.RowKey,
                    RowIndex = row.RowIndex,
                    LabelCode = labelCode,
                    Source = row.Source,
                    SourcePayloadRevision = projectionContext.SourcePayloadRevision,
                    SourcePayloadHash = projectionContext.SourcePayloadHash,
                    CreatedAtUtc = generation.ComputedAtUtc,
                    UpdatedAtUtc = generation.ComputedAtUtc,
                    CreatedByUserId = actorId,
                    UpdatedByUserId = actorId,
                    IsDeleted = false
                }))
            .GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.Id, StringComparer.Ordinal)
            .ToList();

        if (rows.Count > 0)
        {
            if (generationRows is not null)
            {
                generationRows.AddRange(rows);
            }
            else
            {
                await WorkReportDirectGenerationInsertOnly.InsertOrValidateAsync(
                    _ctx.WorkReportLabelStatValues,
                    rows,
                    "work_report_label_stat_values",
                    generation.GenerationId,
                    ct);
            }
        }

        return aggregateKey;
    }

    private async Task<HashSet<string>> LoadActiveLabelCodesAsync(
        IEnumerable<string> labelCodes,
        CancellationToken ct)
    {
        var codes = labelCodes
            .Select(NormalizeLabelCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var labels = await _ctx.Labels
            .Find(x => codes.Contains(x.Code) && x.IsActive && !x.IsDeleted)
            .Project(x => x.Code)
            .ToListAsync(ct);

        return labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task RebuildAggregatesForWorkPeriodAsync(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        string? actorUserId,
        CancellationToken ct = default)
    {
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Projection);
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var valueFilter = BuildValueFilter(
            workId,
            periodInstanceKey,
            dynamicFormTemplateId,
            generationId: null);
        var aggregateFilter = BuildAggregateFilter(workId, periodInstanceKey, dynamicFormTemplateId);

        var values = await _ctx.WorkReportLabelStatValues
            .Find(valueFilter)
            .ToListAsync(ct);

        await _ctx.WorkReportLabelStatAggregates.DeleteManyAsync(aggregateFilter, ct);

        if (values.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var actorId = NormalizeObjectIdOrNull(actorUserId);
        var buckets = new Dictionary<AggregateKey, AggregateBucket>();

        foreach (var value in values)
        {
            foreach (var scope in ResolveScopes(value))
            {
                var key = new AggregateKey(
                    value.WorkId,
                    scope.ScopeType,
                    scope.ScopeId,
                    value.DynamicFormTemplateId,
                    value.DynamicExcelTemplateId,
                    value.BlockId,
                    value.LabelCode,
                    value.PeriodInstanceKey,
                    value.ReportStatus);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new AggregateBucket
                    {
                        Row = new WorkReportLabelStatAggregate
                        {
                            Id = ObjectId.GenerateNewId().ToString(),
                            WorkId = value.WorkId,
                            ScopeType = scope.ScopeType,
                            ScopeId = scope.ScopeId,
                            RootAssignmentId = value.RootAssignmentId,
                            DynamicFormTemplateId = value.DynamicFormTemplateId,
                            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
                            DynamicFormTemplateName = value.DynamicFormTemplateName,
                            DynamicExcelTemplateId = value.DynamicExcelTemplateId,
                            BlockId = value.BlockId,
                            LabelCode = value.LabelCode,
                            PeriodKey = value.PeriodKey,
                            PeriodInstanceKey = value.PeriodInstanceKey,
                            PeriodKind = value.PeriodKind,
                            PeriodAnchorDate = value.PeriodAnchorDate,
                            PeriodStartDate = value.PeriodStartDate,
                            PeriodEndDate = value.PeriodEndDate,
                            CompletedDate = value.CompletedDate,
                            IsHistoricalData = value.IsHistoricalData,
                            ReportStatus = value.ReportStatus,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            CreatedByUserId = actorId,
                            UpdatedByUserId = actorId,
                            IsDeleted = false
                        }
                    };
                    buckets.Add(key, bucket);
                }

                bucket.Row.RowCount += 1;
                bucket.ReportIds.Add(value.WorkAssignmentReportId);
            }
        }

        var aggregates = buckets.Values.Select(x =>
        {
            x.Row.ReportCount = x.ReportIds.Count;
            return x.Row;
        }).ToList();

        if (aggregates.Count > 0)
            await _ctx.WorkReportLabelStatAggregates.InsertManyAsync(aggregates, cancellationToken: ct);
    }

    public async Task StageGenerationAggregatesForWorkPeriodAsync(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        WorkReportDirectGenerationContext generation,
        string? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ValidateGenerationContext(generation);

        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("A work id is required for P9 Direct LABEL staging.", nameof(workId));

        var values = await _ctx.WorkReportLabelStatValues
            .Find(BuildGenerationValueFilter(workId, periodInstanceKey, dynamicFormTemplateId, generation))
            .SortBy(x => x.WorkAssignmentReportId)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        if (values.Count == 0)
        {
            await WorkReportDirectGenerationInsertOnly.InsertOrValidateAsync(
                _ctx.WorkReportLabelStatAggregates,
                Array.Empty<WorkReportLabelStatAggregate>(),
                "work_report_label_stat_aggregates",
                generation.GenerationId,
                ct);
            return;
        }

        foreach (var value in values)
            EnsureGenerationPinMatches(value, generation);

        var reportIds = values
            .Select(value => value.WorkAssignmentReportId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var reportFb = Builders<WorkAssignmentReport>.Filter;
        var reports = await _ctx.WorkAssignmentReports
            .Find(reportFb.In(x => x.Id, reportIds) & reportFb.Eq(x => x.IsDeleted, false))
            .ToListAsync(ct);
        var reportsById = reports.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (reportsById.Count != reportIds.Count)
        {
            throw WorkReportDirectGenerationValidationException.SourceDrift();
        }

        var assignmentIds = reports
            .Select(report => report.WorkAssignmentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignmentFb = Builders<WorkAssignment>.Filter;
        var assignments = await _ctx.WorkAssignments
            .Find(assignmentFb.In(x => x.Id, assignmentIds) & assignmentFb.Eq(x => x.IsDeleted, false))
            .ToListAsync(ct);
        var assignmentsById = assignments.ToDictionary(x => x.Id, StringComparer.Ordinal);

        var periodIds = reports
            .Select(report => report.WorkReportPeriodId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var periodFb = Builders<WorkReportPeriod>.Filter;
        var periods = await _ctx.WorkReportPeriods
            .Find(periodFb.In(x => x.Id, periodIds) & periodFb.Eq(x => x.IsDeleted, false))
            .ToListAsync(ct);
        var periodsById = periods.ToDictionary(x => x.Id, StringComparer.Ordinal);

        foreach (var report in reports.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            assignmentsById.TryGetValue(report.WorkAssignmentId, out var assignment);
            periodsById.TryGetValue(report.WorkReportPeriodId, out var period);
            EnsureApprovedEffectiveGenerationSource(report, assignment, period, generation);
        }

        foreach (var value in values)
        {
            var report = reportsById[value.WorkAssignmentReportId];
            EnsureGenerationValueMatchesSource(value, report);

            var contributionPolicy = generation.ResolveContributionPolicy(report);
            if (!contributionPolicy.IncludesReport ||
                !contributionPolicy.ShouldIncludeLabel(
                    value.BlockId,
                    value.RowKey,
                    value.Source,
                    value.LabelCode))
            {
                throw WorkReportDirectGenerationValidationException.PolicyDrift();
            }
        }

        var actorId = NormalizeObjectIdOrNull(actorUserId);
        var buckets = new Dictionary<AggregateKey, AggregateBucket>();

        foreach (var value in values)
        {
            foreach (var scope in ResolveScopes(value))
            {
                if (string.IsNullOrWhiteSpace(scope.ScopeId))
                    continue;

                var key = new AggregateKey(
                    value.WorkId,
                    scope.ScopeType,
                    scope.ScopeId,
                    value.DynamicFormTemplateId,
                    value.DynamicExcelTemplateId,
                    value.BlockId,
                    value.LabelCode,
                    value.PeriodInstanceKey,
                    value.ReportStatus);

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new AggregateBucket
                    {
                        Row = new WorkReportLabelStatAggregate
                        {
                            Id = generation.StableObjectId(
                                "LABEL_AGGREGATE",
                                key.WorkId,
                                key.ScopeType,
                                key.ScopeId,
                                key.DynamicFormTemplateId,
                                key.DynamicExcelTemplateId,
                                key.BlockId,
                                key.LabelCode,
                                key.PeriodInstanceKey,
                                key.ReportStatus.ToString(CultureInfo.InvariantCulture)),
                            DirectProjection = CloneDirectProjectionPin(value.DirectProjection!),
                            WorkId = value.WorkId,
                            ScopeType = scope.ScopeType,
                            ScopeId = scope.ScopeId,
                            RootAssignmentId = value.RootAssignmentId,
                            DynamicFormTemplateId = value.DynamicFormTemplateId,
                            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
                            DynamicFormTemplateName = value.DynamicFormTemplateName,
                            DynamicExcelTemplateId = value.DynamicExcelTemplateId,
                            BlockId = value.BlockId,
                            LabelCode = value.LabelCode,
                            PeriodKey = value.PeriodKey,
                            PeriodInstanceKey = value.PeriodInstanceKey,
                            PeriodKind = value.PeriodKind,
                            PeriodAnchorDate = value.PeriodAnchorDate,
                            PeriodStartDate = value.PeriodStartDate,
                            PeriodEndDate = value.PeriodEndDate,
                            CompletedDate = value.CompletedDate,
                            IsHistoricalData = value.IsHistoricalData,
                            ReportStatus = value.ReportStatus,
                            CreatedAtUtc = generation.ComputedAtUtc,
                            UpdatedAtUtc = generation.ComputedAtUtc,
                            CreatedByUserId = actorId,
                            UpdatedByUserId = actorId,
                            IsDeleted = false
                        }
                    };
                    buckets.Add(key, bucket);
                }

                bucket.Row.RowCount += 1;
                bucket.ReportIds.Add(value.WorkAssignmentReportId);
            }
        }

        var aggregates = buckets.Values
            .Select(bucket =>
            {
                bucket.Row.ReportCount = bucket.ReportIds.Count;
                return bucket.Row;
            })
            .OrderBy(row => row.Id, StringComparer.Ordinal)
            .ToList();

        await WorkReportDirectGenerationInsertOnly.InsertOrValidateAsync(
            _ctx.WorkReportLabelStatAggregates,
            aggregates,
            "work_report_label_stat_aggregates",
            generation.GenerationId,
            ct);
    }

    public async Task<LabelStatisticSummaryResponse> SearchSummaryAsync(
        LabelStatisticSummaryRequest req,
        CancellationToken ct = default)
    {
        StatConfigPhaseBarrier.Reject(StatConfigPhaseBarrierEntries.P9Result);
        var me = _me.RequireMe();
        var normalized = NormalizeRequest(req);
        await EnsureCanReadScopeAsync(normalized, me.Id, ct);

        var filter = BuildSummaryFilter(normalized);
        var page = Math.Max(0, normalized.Page);
        var pageSize = Math.Clamp(normalized.PageSize <= 0 ? 50 : normalized.PageSize, 1, 200);

        var total = await _ctx.WorkReportLabelStatAggregates.CountDocumentsAsync(filter, cancellationToken: ct);
        var totals = await _ctx.WorkReportLabelStatAggregates
            .Aggregate()
            .Match(filter)
            .Group(
                _ => 1,
                group => new LabelStatisticSummaryTotals
                {
                    TotalRowCount = group.Sum(x => x.RowCount),
                    TotalReportCount = group.Sum(x => x.ReportCount)
                })
            .FirstOrDefaultAsync(ct);
        var rows = await _ctx.WorkReportLabelStatAggregates
            .Find(filter)
            .SortByDescending(x => x.RowCount)
            .ThenBy(x => x.LabelCode)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var labelCodes = rows
            .Select(x => x.LabelCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var labels = labelCodes.Count == 0
            ? new Dictionary<string, LabelCatalogItem>(StringComparer.OrdinalIgnoreCase)
            : (await _ctx.Labels
                .Find(x => labelCodes.Contains(x.Code) && x.IsActive && !x.IsDeleted)
                .ToListAsync(ct))
                .ToDictionary(x => x.Code, x => x, StringComparer.OrdinalIgnoreCase);

        var resultRows = rows.Select(x =>
        {
            labels.TryGetValue(x.LabelCode, out var label);
            return new LabelStatisticSummaryRow
            {
                WorkId = x.WorkId,
                ScopeType = x.ScopeType,
                ScopeId = x.ScopeId,
                RootAssignmentId = x.RootAssignmentId,
                DynamicFormTemplateId = x.DynamicFormTemplateId,
                DynamicFormTemplateCode = x.DynamicFormTemplateCode,
                DynamicFormTemplateName = x.DynamicFormTemplateName,
                DynamicExcelTemplateId = x.DynamicExcelTemplateId,
                BlockId = x.BlockId,
                LabelCode = x.LabelCode,
                LabelName = label?.Name,
                LabelColor = label?.Color,
                LabelDataType = LabelDataTypes.Normalize(label?.DataType),
                PeriodKey = x.PeriodKey,
                PeriodInstanceKey = x.PeriodInstanceKey,
                PeriodKind = x.PeriodKind,
                ReportStatus = x.ReportStatus,
                RowCount = x.RowCount,
                ReportCount = x.ReportCount,
                UpdatedAtUtc = x.UpdatedAtUtc
            };
        }).ToList();

        return new LabelStatisticSummaryResponse
        {
            Rows = resultRows,
            TotalRows = total,
            TotalRowCount = totals?.TotalRowCount ?? 0,
            TotalReportCount = totals?.TotalReportCount ?? 0
        };
    }

    private static void ValidateGenerationContext(WorkReportDirectGenerationContext generation)
    {
        var required = new (string Name, string? Value)[]
        {
            (nameof(generation.RunId), generation.RunId),
            (nameof(generation.GenerationId), generation.GenerationId),
            (nameof(generation.LifecycleEventKey), generation.LifecycleEventKey),
            (nameof(generation.ConfigId), generation.ConfigId),
            (nameof(generation.ConfigVersionId), generation.ConfigVersionId),
            (nameof(generation.ConfigHash), generation.ConfigHash),
            (nameof(generation.CandidateChainId), generation.CandidateChainId),
            (nameof(generation.CatalogVersion), generation.CatalogVersion),
            (nameof(generation.CatalogRawSha256), generation.CatalogRawSha256),
            (nameof(generation.CatalogSemanticSha256), generation.CatalogSemanticSha256),
            (nameof(generation.SchemaRawSha256), generation.SchemaRawSha256),
            (nameof(generation.SchemaSemanticSha256), generation.SchemaSemanticSha256),
            (nameof(generation.StageLockSha256), generation.StageLockSha256),
            (nameof(generation.SourceMembershipSignature), generation.SourceMembershipSignature)
        };

        foreach (var (name, value) in required)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"P9 Direct LABEL generation pin '{name}' is required.", nameof(generation));
        }

        if (!ObjectId.TryParse(generation.RunId, out _))
            throw new ArgumentException("P9 Direct LABEL RunId must be an ObjectId.", nameof(generation));
        if (!ObjectId.TryParse(generation.ConfigId, out _))
            throw new ArgumentException("P9 Direct LABEL ConfigId must be an ObjectId.", nameof(generation));
        if (!ObjectId.TryParse(generation.ConfigVersionId, out _))
            throw new ArgumentException("P9 Direct LABEL ConfigVersionId must be an ObjectId.", nameof(generation));
        if (generation.DirectSourceRevision < 1 ||
            generation.ConfigVersionNo < 0 || generation.ConfigRevision < 0)
        {
            throw new ArgumentException(
                "P9 Direct LABEL config version and revision pins cannot be negative.",
                nameof(generation));
        }

        if (generation.ComputedAtUtc == default || generation.ComputedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "P9 Direct LABEL ComputedAtUtc must be a non-default UTC value.",
                nameof(generation));
        }
    }

    private static void EnsureApprovedEffectiveGenerationSource(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        WorkReportPeriod? period,
        WorkReportDirectGenerationContext generation)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved)
            throw InvalidGenerationSource(report.Id, "report is not approved");
        if (!report.IsCurrent)
            throw InvalidGenerationSource(report.Id, "report is not current");
        if (!report.IsActive)
            throw InvalidGenerationSource(report.Id, "report is inactive");
        if (!string.IsNullOrWhiteSpace(report.InvalidatedByFlowEventId))
            throw InvalidGenerationSource(report.Id, "report was invalidated by a Flow event");

        if (assignment is null)
            throw InvalidGenerationSource(report.Id, "assignment is missing or deleted");
        if (!string.Equals(assignment.WorkId, report.WorkId, StringComparison.Ordinal))
            throw InvalidGenerationSource(report.Id, "assignment belongs to another work");
        if (!assignment.IsActive)
            throw InvalidGenerationSource(report.Id, "assignment is inactive");
        if (!string.IsNullOrWhiteSpace(assignment.InvalidatedByFlowEventId))
            throw InvalidGenerationSource(report.Id, "assignment was invalidated by a Flow event");
        if (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId) &&
            (!string.Equals(
                 assignment.FlowEffectiveStatus,
                 DynamicFlowEffectiveStatuses.Effective,
                 StringComparison.OrdinalIgnoreCase) ||
             !assignment.FlowExecutionEpoch.HasValue ||
             assignment.FlowExecutionEpoch.Value < 1))
        {
            throw InvalidGenerationSource(report.Id, "Flow-bound assignment is not in an effective epoch");
        }

        if (period is null)
            throw InvalidGenerationSource(report.Id, "report period is missing or deleted");
        if (!period.IsActive)
            throw InvalidGenerationSource(report.Id, "report period is inactive");
        if (!string.Equals(period.WorkId, report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(period.WorkAssignmentId, report.WorkAssignmentId, StringComparison.Ordinal))
        {
            throw InvalidGenerationSource(report.Id, "report period belongs to another work or assignment");
        }

        if (!string.Equals(period.PeriodKey, report.PeriodKey, StringComparison.Ordinal) ||
            !string.Equals(period.PeriodInstanceKey, report.PeriodInstanceKey, StringComparison.Ordinal))
        {
            throw InvalidGenerationSource(report.Id, "report period identity does not match the source report");
        }

        if (!string.Equals(period.CurrentReportId, report.Id, StringComparison.Ordinal))
            throw InvalidGenerationSource(report.Id, "report is not the period's current report");
        if (!string.Equals(period.SourceLifecycleReportId, report.Id, StringComparison.Ordinal) ||
            period.SourceLifecycleRevision != report.LifecycleRevision ||
            !period.SourceLifecycleAppliedAtUtc.HasValue)
        {
            throw InvalidGenerationSource(report.Id, "period lifecycle projection is stale or missing");
        }

        if (period.Status is not (WorkReportPeriodStatus.Approved or WorkReportPeriodStatus.OverdueApproved))
            throw InvalidGenerationSource(report.Id, "period is not in an approved state");

        var transitions = report.LifecycleProjectionOutbox ?? new List<WorkReportLifecycleProjectionOutboxEntry>();
        var exactTransition = transitions.FirstOrDefault(entry => string.Equals(
            entry.EntryKey,
            generation.LifecycleEventKey,
            StringComparison.Ordinal));
        var transition = exactTransition ?? transitions
            .Where(entry =>
                entry.LifecycleRevision == report.LifecycleRevision &&
                entry.PayloadRevision == report.PayloadRevision &&
                string.Equals(entry.PayloadHash, report.PayloadHash, StringComparison.Ordinal) &&
                entry.State is (WorkReportLifecycleProjectionOutboxStates.Pending or
                    WorkReportLifecycleProjectionOutboxStates.Completed) &&
                IsActiveApprovedGenerationTransition(entry) &&
                string.Equals(entry.CommandId, report.LastLifecycleCommandId, StringComparison.Ordinal) &&
                entry.ToIsActive)
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ThenByDescending(entry => entry.EntryKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (transition is null)
            throw InvalidGenerationSource(report.Id, "current approved lifecycle intent is missing from the durable outbox");
        if (!string.Equals(
                transition.EntryKey,
                WorkReportLifecycleOutboxContract.ComputeEntryKey(
                    transition.CommandId,
                    transition.LifecycleRevision,
                    transition.Operation),
                StringComparison.Ordinal))
        {
            throw InvalidGenerationSource(report.Id, "lifecycle event key fails its deterministic integrity check");
        }
        if (transition.LifecycleRevision != report.LifecycleRevision)
            throw InvalidGenerationSource(report.Id, "lifecycle event revision is stale");
        if (transition.State is not (WorkReportLifecycleProjectionOutboxStates.Pending or
                WorkReportLifecycleProjectionOutboxStates.Completed) ||
            !IsActiveApprovedGenerationTransition(transition))
        {
            throw InvalidGenerationSource(report.Id, "lifecycle event does not admit an active approved report");
        }

        if (!string.Equals(report.LastLifecycleCommandId, transition.CommandId, StringComparison.Ordinal) ||
            !string.Equals(report.LastLifecycleCommandOperation, transition.Operation, StringComparison.Ordinal) ||
            report.LastLifecycleCommandRevision != transition.LifecycleRevision ||
            report.LastLifecycleCommandPayloadRevision != transition.PayloadRevision ||
            report.LastLifecycleCommandStatus != WorkAssignmentReportStatus.Approved ||
            report.LastLifecycleCommandIsActive != true ||
            report.LastLifecycleCommandHash is null ||
            report.LastLifecycleCommandHash.Length != 64 ||
            report.LastLifecycleCommandHash.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw InvalidGenerationSource(report.Id, "lifecycle event is not the report's canonical approval command");
        }

        if (transition.PayloadRevision != report.PayloadRevision ||
            !string.Equals(
                transition.PayloadHash ?? string.Empty,
                report.PayloadHash ?? string.Empty,
                StringComparison.Ordinal))
        {
            throw InvalidGenerationSource(report.Id, "lifecycle event payload pin is stale");
        }
    }

    private static bool IsActiveApprovedGenerationTransition(
        WorkReportLifecycleProjectionOutboxEntry transition)
    {
        var initialApproval =
            transition.Operation is ("REVIEW_APPROVE" or "REVIEW_CONFIRM_AUTO_APPROVE") &&
            string.Equals(transition.FromStatus, "SUBMITTED", StringComparison.Ordinal);
        var reactivation =
            string.Equals(transition.Operation, "REVIEW_REACTIVATE_REPORT", StringComparison.Ordinal) &&
            string.Equals(
                transition.FromStatus,
                WorkAssignmentReportStatus.Approved.ToString(),
                StringComparison.OrdinalIgnoreCase) &&
            !transition.FromIsActive;

        return (initialApproval || reactivation) &&
               transition.ToIsActive &&
               string.Equals(
                   transition.ToStatus,
                   WorkAssignmentReportStatus.Approved.ToString(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureGenerationPinMatches(
        WorkReportLabelStatValue value,
        WorkReportDirectGenerationContext generation)
    {
        var pin = value.DirectProjection
                  ?? throw WorkReportDirectGenerationValidationException
                      .PinConflict();

        if (!string.Equals(pin.RunId, generation.RunId, StringComparison.Ordinal) ||
            !string.Equals(pin.GenerationId, generation.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(pin.LifecycleEventKey, generation.LifecycleEventKey, StringComparison.Ordinal) ||
            pin.DirectSourceRevision != generation.DirectSourceRevision ||
            !string.Equals(pin.DynamicFormFamilyId, generation.DynamicFormFamilyId, StringComparison.Ordinal) ||
            !string.Equals(pin.DynamicFormTemplateId, generation.DynamicFormTemplateId, StringComparison.Ordinal) ||
            pin.DynamicFormVersionNo != generation.DynamicFormVersionNo ||
            !string.Equals(pin.DynamicFormSchemaHash, generation.DynamicFormSchemaHash, StringComparison.Ordinal) ||
            !string.Equals(pin.ConfigId, generation.ConfigId, StringComparison.Ordinal) ||
            !string.Equals(pin.ConfigVersionId, generation.ConfigVersionId, StringComparison.Ordinal) ||
            pin.ConfigVersionNo != generation.ConfigVersionNo ||
            pin.ConfigRevision != generation.ConfigRevision ||
            !string.Equals(pin.ConfigHash, generation.ConfigHash, StringComparison.Ordinal) ||
            !string.Equals(pin.CandidateChainId, generation.CandidateChainId, StringComparison.Ordinal) ||
            !string.Equals(pin.CatalogVersion, generation.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(pin.CatalogRawSha256, generation.CatalogRawSha256, StringComparison.Ordinal) ||
            !string.Equals(pin.CatalogSemanticSha256, generation.CatalogSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(pin.SchemaRawSha256, generation.SchemaRawSha256, StringComparison.Ordinal) ||
            !string.Equals(pin.SchemaSemanticSha256, generation.SchemaSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(pin.StageLockSha256, generation.StageLockSha256, StringComparison.Ordinal) ||
            !string.Equals(
                pin.SourceMembershipSignature,
                generation.SourceMembershipSignature,
                StringComparison.Ordinal) ||
            !SameUtcMillisecond(pin.ComputedAtUtc, generation.ComputedAtUtc))
        {
            throw WorkReportDirectGenerationValidationException.PinConflict();
        }
    }

    private static void EnsureGenerationValueMatchesSource(
        WorkReportLabelStatValue value,
        WorkAssignmentReport report)
    {
        var pin = value.DirectProjection!;
        if (!value.AssignmentIsActive ||
            !value.ReportIsActive ||
            value.ReportStatus != (int)WorkAssignmentReportStatus.Approved ||
            !string.IsNullOrWhiteSpace(value.InvalidatedByFlowEventId) ||
            !string.Equals(value.WorkId, report.WorkId, StringComparison.Ordinal) ||
            !string.Equals(value.WorkAssignmentId, report.WorkAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(value.WorkReportPeriodId, report.WorkReportPeriodId, StringComparison.Ordinal) ||
            !string.Equals(value.PeriodInstanceKey, report.PeriodInstanceKey, StringComparison.Ordinal) ||
            !string.Equals(
                value.DynamicFormTemplateId,
                NormalizeObjectIdOrNull(report.DynamicFormTemplateId),
                StringComparison.Ordinal) ||
            !string.Equals(pin.SourceReportId, report.Id, StringComparison.Ordinal) ||
            pin.SourcePayloadRevision != report.PayloadRevision ||
            !string.Equals(
                pin.SourcePayloadHash,
                report.PayloadHash ?? string.Empty,
                StringComparison.Ordinal) ||
            pin.SourceLifecycleRevision != report.LifecycleRevision ||
            pin.DirectSourceRevision < 1 ||
            !string.Equals(pin.DynamicFormFamilyId, report.DynamicFormFamilyId, StringComparison.Ordinal) ||
            !string.Equals(pin.DynamicFormTemplateId, report.DynamicFormTemplateId, StringComparison.Ordinal) ||
            pin.DynamicFormVersionNo != report.DynamicFormVersionNo ||
            !string.Equals(pin.DynamicFormSchemaHash, report.DynamicFormSchemaHash, StringComparison.Ordinal) ||
            value.SourcePayloadRevision != report.PayloadRevision ||
            !string.Equals(
                value.SourcePayloadHash ?? string.Empty,
                report.PayloadHash ?? string.Empty,
                StringComparison.Ordinal))
        {
            throw WorkReportDirectGenerationValidationException.SourceDrift();
        }
    }

    private static WorkReportDirectProjectionPin CloneDirectProjectionPin(WorkReportDirectProjectionPin source)
        => new()
        {
            RunId = source.RunId,
            GenerationId = source.GenerationId,
            LifecycleEventKey = source.LifecycleEventKey,
            SourceReportId = source.SourceReportId,
            SourcePayloadRevision = source.SourcePayloadRevision,
            SourcePayloadHash = source.SourcePayloadHash,
            SourceLifecycleRevision = source.SourceLifecycleRevision,
            DirectSourceRevision = source.DirectSourceRevision,
            DynamicFormFamilyId = source.DynamicFormFamilyId,
            DynamicFormTemplateId = source.DynamicFormTemplateId,
            DynamicFormVersionNo = source.DynamicFormVersionNo,
            DynamicFormSchemaHash = source.DynamicFormSchemaHash,
            ConfigId = source.ConfigId,
            ConfigVersionId = source.ConfigVersionId,
            ConfigVersionNo = source.ConfigVersionNo,
            ConfigRevision = source.ConfigRevision,
            ConfigHash = source.ConfigHash,
            CandidateChainId = source.CandidateChainId,
            CatalogVersion = source.CatalogVersion,
            CatalogRawSha256 = source.CatalogRawSha256,
            CatalogSemanticSha256 = source.CatalogSemanticSha256,
            SchemaRawSha256 = source.SchemaRawSha256,
            SchemaSemanticSha256 = source.SchemaSemanticSha256,
            StageLockSha256 = source.StageLockSha256,
            SourceMembershipSignature = source.SourceMembershipSignature,
            ComputedAtUtc = source.ComputedAtUtc
        };

    private static bool SameUtcMillisecond(DateTime left, DateTime right)
    {
        if (left.Kind == DateTimeKind.Unspecified || right.Kind == DateTimeKind.Unspecified)
            return false;

        return left.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond ==
               right.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static WorkReportDirectGenerationValidationException
        InvalidGenerationSource(string reportId, string reason)
    {
        _ = reportId;
        _ = reason;
        return WorkReportDirectGenerationValidationException.SourceDrift();
    }

    private static FilterDefinition<WorkReportLabelStatValue> BuildValueFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        string? generationId)
    {
        var fb = Builders<WorkReportLabelStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim()) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        filter &= generationId is null
            ? fb.Eq(x => x.DirectProjection, null)
            : fb.Eq("directProjection.generationId", generationId);

        return filter;
    }

    private static FilterDefinition<WorkReportLabelStatValue> BuildGenerationValueFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        WorkReportDirectGenerationContext generation)
    {
        return BuildValueFilter(
            workId,
            periodInstanceKey,
            dynamicFormTemplateId,
            generation.GenerationId);
    }

    private static FilterDefinition<WorkReportLabelStatAggregate> BuildAggregateFilter(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId)
    {
        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, workId.Trim())
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.DirectProjection, null);

        if (!string.IsNullOrWhiteSpace(periodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, periodInstanceKey.Trim());

        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());

        return filter;
    }

    private static FilterDefinition<WorkReportLabelStatAggregate> BuildSummaryFilter(
        LabelStatisticSummaryRequest req)
    {
        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, req.WorkId!.Trim())
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.DirectProjection, null);

        if (!string.IsNullOrWhiteSpace(req.ScopeType))
            filter &= fb.Eq(x => x.ScopeType, req.ScopeType!.Trim().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(req.ScopeId))
            filter &= fb.Eq(x => x.ScopeId, req.ScopeId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, req.DynamicFormTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, req.DynamicExcelTemplateId!.Trim());

        if (!string.IsNullOrWhiteSpace(req.LabelCode))
            filter &= fb.Eq(x => x.LabelCode, NormalizeLabelCode(req.LabelCode));

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey!.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodInstanceKey))
            filter &= fb.Eq(x => x.PeriodInstanceKey, req.PeriodInstanceKey!.Trim());

        if (req.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, req.ReportStatus.Value);

        return filter;
    }

    private async Task EnsureCanReadScopeAsync(
        LabelStatisticSummaryRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw ReportStatisticExceptions.WorkIdRequired("LABEL", req.WorkId);

        var scopeType = req.ScopeType?.Trim().ToUpperInvariant();
        if (scopeType is "ASSIGNMENT" or "ROOT")
        {
            if (string.IsNullOrWhiteSpace(req.ScopeId))
                throw ReportStatisticExceptions.ScopeIdRequired("LABEL", req.WorkId, scopeType, req.ScopeId);

            var assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == req.ScopeId && x.WorkId == req.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw ReportStatisticExceptions.AssignmentNotFound("LABEL", req.WorkId, scopeType, req.ScopeId);

            if (CanReadAssignment(assignment, actorUserId))
                return;
        }
        else
        {
            var anyVisibleAssignment = await _ctx.WorkAssignments
                .Find(x =>
                    x.WorkId == req.WorkId &&
                    !x.IsDeleted &&
                    (x.CreatedByUserId == actorUserId || x.LeaderWatcherUserIds.Contains(actorUserId)))
                .Limit(1)
                .AnyAsync(ct);

            if (anyVisibleAssignment)
                return;
        }

        throw ReportStatisticExceptions.ReadForbidden("LABEL", req.WorkId, scopeType, req.ScopeId, actorUserId);
    }

    private static bool CanReadAssignment(WorkAssignment assignment, string actorUserId)
        => string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal)
           || (assignment.LeaderWatcherUserIds?.Contains(actorUserId) ?? false);

    private static LabelStatisticSummaryRequest NormalizeRequest(LabelStatisticSummaryRequest req)
    {
        req ??= new LabelStatisticSummaryRequest();
        var workId = req.WorkId?.Trim();
        if (string.IsNullOrWhiteSpace(workId))
            throw ReportStatisticExceptions.WorkIdRequired("LABEL", req.WorkId);

        var scopeType = string.IsNullOrWhiteSpace(req.ScopeType)
            ? "WORK"
            : req.ScopeType.Trim().ToUpperInvariant();

        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw ReportStatisticExceptions.ScopeTypeInvalid("LABEL", workId, req.ScopeType);

        var scopeId = string.IsNullOrWhiteSpace(req.ScopeId)
            ? (scopeType == "WORK" ? workId : null)
            : req.ScopeId.Trim();

        return new LabelStatisticSummaryRequest
        {
            WorkId = workId,
            ScopeType = scopeType,
            ScopeId = scopeId,
            DynamicFormTemplateId = NormalizeOptionalId(req.DynamicFormTemplateId),
            DynamicExcelTemplateId = NormalizeOptionalId(req.DynamicExcelTemplateId),
            LabelCode = string.IsNullOrWhiteSpace(req.LabelCode) ? null : NormalizeLabelCode(req.LabelCode),
            PeriodKey = string.IsNullOrWhiteSpace(req.PeriodKey) ? null : req.PeriodKey.Trim(),
            PeriodInstanceKey = string.IsNullOrWhiteSpace(req.PeriodInstanceKey) ? null : req.PeriodInstanceKey.Trim(),
            ReportStatus = req.ReportStatus,
            Page = Math.Max(0, req.Page),
            PageSize = Math.Clamp(req.PageSize <= 0 ? 50 : req.PageSize, 1, 200)
        };
    }

    private static List<ParsedRowLabel> ExtractRowLabels(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new List<ParsedRowLabel>();

        try
        {
            var expandedTableValuesJson = Values1DCompression.ExpandTableValuesJson(tableValuesJson, JsonOptions) ?? tableValuesJson;
            var root = JsonSerializer.Deserialize<TableValuesRoot>(expandedTableValuesJson, JsonOptions);
            if (root?.Blocks is null || root.Blocks.Count == 0)
                return new List<ParsedRowLabel>();

            var result = new List<ParsedRowLabel>();
            foreach (var block in root.Blocks)
            {
                if (ShouldDisableRuntimeBlockTableStatistics(block))
                    continue;

                var blockId = string.IsNullOrWhiteSpace(block.BlockId)
                    ? "excel_block"
                    : block.BlockId.Trim();

                foreach (var row in block.RowLabels ?? new List<TableValuesRowLabel>())
                {
                    var rowIndex = NormalizeRowIndex(row.RowIndex, row.RowKey);
                    if (rowIndex < 0)
                        continue;

                    var labelCodes = NormalizeLabelCodes(row.RowLabelCodes);
                    if (labelCodes.Count == 0)
                        continue;

                    result.Add(new ParsedRowLabel(
                        blockId,
                        NormalizeOptionalId(block.DynamicExcelTemplateId),
                        string.IsNullOrWhiteSpace(row.SheetId) ? "sheet_1" : row.SheetId.Trim(),
                        string.IsNullOrWhiteSpace(row.RowKey) ? $"sheet_1:R{rowIndex + 1}" : row.RowKey.Trim(),
                        rowIndex,
                        labelCodes,
                        string.IsNullOrWhiteSpace(row.Source) ? "ROW_LABEL" : row.Source.Trim()));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new List<ParsedRowLabel>();
        }
    }

    private static List<string> ExtractAncestorAssignmentIds(WorkAssignment? assignment, string currentAssignmentId)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.Path))
            return new List<string>();

        return assignment.Path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, currentAssignmentId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<AggregateScope> ResolveScopes(WorkReportLabelStatValue value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        yield return Add("WORK", value.WorkId);

        if (!string.IsNullOrWhiteSpace(value.RootAssignmentId))
            yield return Add("ROOT", value.RootAssignmentId);

        foreach (var assignmentId in value.AncestorAssignmentIds.Append(value.WorkAssignmentId))
        {
            if (string.IsNullOrWhiteSpace(assignmentId))
                continue;

            var scope = Add("ASSIGNMENT", assignmentId);
            if (!string.IsNullOrWhiteSpace(scope.ScopeId))
                yield return scope;
        }

        AggregateScope Add(string type, string? id)
        {
            var scopeId = id?.Trim() ?? string.Empty;
            var key = $"{type}:{scopeId}";
            return !string.IsNullOrWhiteSpace(scopeId) && seen.Add(key)
                ? new AggregateScope(type, scopeId)
                : new AggregateScope(string.Empty, string.Empty);
        }
    }

    private static int NormalizeRowIndex(int? rowIndex, string? rowKey)
    {
        if (rowIndex.HasValue && rowIndex.Value >= 0)
            return rowIndex.Value;

        if (string.IsNullOrWhiteSpace(rowKey))
            return -1;

        var match = Regex.Match(rowKey.Trim(), "R(\\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return -1;

        return int.TryParse(match.Groups[1].Value, out var oneBased) && oneBased > 0
            ? oneBased - 1
            : -1;
    }

    private static List<string> NormalizeLabelCodes(IEnumerable<string>? values)
        => values?
            .Select(NormalizeLabelCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList()
           ?? new List<string>();

    private static string NormalizeLabelCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return LabelCodeRegex.IsMatch(code) ? code : string.Empty;
    }

    private static string? NormalizeOptionalId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeObjectIdOrNull(string? value)
        => ObjectId.TryParse(value, out _) ? value : null;

    private static bool ShouldDisableRuntimeBlockTableStatistics(TableValuesBlock block)
    {
        if (block.StatisticsDisabled == true)
            return true;

        var metadataInputCellCount = block.StatisticsInputCellCount.GetValueOrDefault();
        var valuesInputCellCount = block.Values1D?.Count ?? 0;
        var inputCellCount = Math.Max(metadataInputCellCount, valuesInputCellCount);
        if (inputCellCount <= 0)
        {
            var w = block.W.GetValueOrDefault();
            var h = block.H.GetValueOrDefault();
            inputCellCount = w > 0 && h > 0 ? w * h : 0;
        }

        return DynamicExcelRuntimePolicy.ShouldDisableBackgroundTableStatistics(inputCellCount);
    }

    private sealed class TableValuesRoot
    {
        public List<TableValuesBlock>? Blocks { get; set; }
    }

    private sealed class TableValuesBlock
    {
        public string? BlockId { get; set; }
        public string? DynamicExcelTemplateId { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
        public bool? StatisticsDisabled { get; set; }
        public int? StatisticsInputCellCount { get; set; }
        public int? StatisticsInputCellLimit { get; set; }
        public string? StatisticsDisabledReason { get; set; }
        public List<JsonElement>? Values1D { get; set; }
        public List<TableValuesRowLabel>? RowLabels { get; set; }
    }

    private sealed class TableValuesRowLabel
    {
        public string? SheetId { get; set; }
        public string? RowKey { get; set; }
        public int? RowIndex { get; set; }
        public List<string>? RowLabelCodes { get; set; }
        public string? Source { get; set; }
    }

    private sealed record ParsedRowLabel(
        string BlockId,
        string? DynamicExcelTemplateId,
        string SheetId,
        string RowKey,
        int RowIndex,
        List<string> LabelCodes,
        string Source);

    private sealed record AggregateScope(string ScopeType, string ScopeId);

    private sealed record AggregateKey(
        string WorkId,
        string ScopeType,
        string ScopeId,
        string? DynamicFormTemplateId,
        string? DynamicExcelTemplateId,
        string BlockId,
        string LabelCode,
        string PeriodInstanceKey,
        int ReportStatus);

    private sealed class AggregateBucket
    {
        public WorkReportLabelStatAggregate Row { get; set; } = default!;
        public HashSet<string> ReportIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class LabelStatisticSummaryTotals
    {
        public long TotalRowCount { get; set; }
        public long TotalReportCount { get; set; }
    }
}
