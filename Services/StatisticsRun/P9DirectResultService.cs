using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public interface IP9DirectResultService
{
    Task<FieldStatisticSummaryResponse> ReadFieldAsync(
        FieldStatisticSummaryRequest request,
        CancellationToken ct);
    Task<TableStatisticSummaryResponse> ReadTableAsync(
        TableStatisticSummaryRequest request,
        CancellationToken ct);
    Task<LabelStatisticSummaryResponse> ReadLabelAsync(
        LabelStatisticSummaryRequest request,
        CancellationToken ct);
    Task<FieldTextConcatResponse> ReadTextAsync(
        FieldTextConcatRequest request,
        CancellationToken ct);
}

public interface IStatisticReconciliationActualPinnedDirectResultService
{
    Task<FieldStatisticSummaryResponse> ReadFieldPinnedAsync(
        FieldStatisticSummaryRequest request,
        string runId,
        CancellationToken ct);
    Task<TableStatisticSummaryResponse> ReadTablePinnedAsync(
        TableStatisticSummaryRequest request,
        string runId,
        CancellationToken ct);
    Task<LabelStatisticSummaryResponse> ReadLabelPinnedAsync(
        LabelStatisticSummaryRequest request,
        string runId,
        CancellationToken ct);
}

/// <summary>
/// Canonical P9-03 reader. Result values come only from a completed, currently
/// published P9-02 generation. Authorization is resolved from server-owned
/// assignments before work, job, generation, or result existence is inspected.
/// </summary>
public sealed class P9DirectResultService :
    IP9DirectResultService,
    IStatisticReconciliationActualPinnedDirectResultService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public P9DirectResultService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public Task<FieldStatisticSummaryResponse> ReadFieldAsync(
        FieldStatisticSummaryRequest request,
        CancellationToken ct)
        => ReadFieldCoreAsync(request, null, ct);

    public Task<FieldStatisticSummaryResponse> ReadFieldPinnedAsync(
        FieldStatisticSummaryRequest request,
        string runId,
        CancellationToken ct)
        => ReadFieldCoreAsync(request, runId, ct);

    private async Task<FieldStatisticSummaryResponse> ReadFieldCoreAsync(
        FieldStatisticSummaryRequest request,
        string? pinnedRunId,
        CancellationToken ct)
    {
        request ??= new FieldStatisticSummaryRequest();
        var query = NormalizeQuery(
            request.WorkId,
            request.ScopeType,
            request.ScopeId,
            request.PeriodInstanceKey,
            request.Page,
            request.PageSize);
        var access = await AuthorizeBeforeExistenceAsync(query, ct);
        var pinnedGenerationId = NormalizeOptionalGenerationId(request.GenerationId);
        var publication = await ResolvePublicationAsync(
            query,
            request.DynamicFormTemplateId,
            pinnedRunId,
            pinnedGenerationId,
            ct);
        var response = new FieldStatisticSummaryResponse
        {
            Metadata = publication.Metadata,
            Page = query.Page,
            PageSize = query.PageSize
        };
        if (!publication.CanReadRows)
            return response;

        var fb = Builders<WorkReportFieldStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.ScopeType, query.ScopeType)
                     & fb.Eq(x => x.ScopeId, query.ScopeId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In("directProjection.generationId", publication.GenerationIds);
        if (!string.IsNullOrWhiteSpace(request.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.FieldId))
            filter &= fb.Eq(x => x.FieldId, request.FieldId.Trim());
        if (!string.IsNullOrWhiteSpace(request.FieldKey))
            filter &= fb.Eq(x => x.FieldKey, request.FieldKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.StatisticLabelCode))
            filter &= fb.AnyEq(
                x => x.StatisticLabelCodes,
                request.StatisticLabelCode.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(request.FieldType))
            filter &= fb.Eq(x => x.FieldType, request.FieldType.Trim());
        if (!string.IsNullOrWhiteSpace(request.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, request.BucketKey.Trim());
        if (request.ShowInTree.HasValue)
            filter &= fb.Eq(x => x.ShowInTree, request.ShowInTree.Value);
        if (request.ShowInDetail.HasValue)
            filter &= fb.Eq(x => x.ShowInDetail, request.ShowInDetail.Value);
        if (!string.IsNullOrWhiteSpace(request.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, request.PeriodKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.PeriodKeyFrom))
            filter &= fb.Gte(x => x.PeriodKey, request.PeriodKeyFrom.Trim());
        if (!string.IsNullOrWhiteSpace(request.PeriodKeyTo))
            filter &= fb.Lte(x => x.PeriodKey, request.PeriodKeyTo.Trim());
        if (request.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, request.ReportStatus.Value);

        // A field hidden from both result surfaces is not observable through
        // rows or totals.
        filter &= fb.Or(
            fb.Eq(x => x.ShowInTree, true),
            fb.Eq(x => x.ShowInDetail, true));
        var all = await _ctx.WorkReportFieldStatAggregates
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.FieldKey)
            .ThenBy(x => x.BucketKey)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        response.TotalRows = all.Count;
        response.TotalValueCount = all.Sum(x => x.ValueCount);
        response.TotalSum = all.Sum(x => x.Sum);
        response.TotalReportCount = all.Sum(x => x.ReportCount);
        response.Rows = all
            .Skip(query.Page * query.PageSize)
            .Take(query.PageSize)
            .Select(ToFieldRow)
            .ToList();
        response.ReturnedRows = response.Rows.Count;
        AttachReconciliationIdentity(
            response.Metadata,
            all.Select(item => item.FieldKey),
            all.Select(item => item.PeriodKey),
            query.PeriodInstanceKey);
        if (request.IncludeDrilldown)
        {
            response.DrilldownRows = await ReadFieldDrilldownAsync(
                request,
                query,
                publication.GenerationIds,
                access,
                ct);
        }
        return response;
    }

    public Task<TableStatisticSummaryResponse> ReadTableAsync(
        TableStatisticSummaryRequest request,
        CancellationToken ct)
        => ReadTableCoreAsync(request, null, ct);

    public Task<TableStatisticSummaryResponse> ReadTablePinnedAsync(
        TableStatisticSummaryRequest request,
        string runId,
        CancellationToken ct)
        => ReadTableCoreAsync(request, runId, ct);

    private async Task<TableStatisticSummaryResponse> ReadTableCoreAsync(
        TableStatisticSummaryRequest request,
        string? pinnedRunId,
        CancellationToken ct)
    {
        request ??= new TableStatisticSummaryRequest();
        var query = NormalizeQuery(
            request.WorkId,
            request.ScopeType,
            request.ScopeId,
            request.PeriodInstanceKey,
            request.Page,
            request.PageSize);
        var access = await AuthorizeBeforeExistenceAsync(query, ct);
        var pinnedGenerationId = NormalizeOptionalGenerationId(request.GenerationId);
        var publication = await ResolvePublicationAsync(
            query,
            request.DynamicFormTemplateId,
            pinnedRunId,
            pinnedGenerationId,
            ct);
        var response = new TableStatisticSummaryResponse
        {
            Metadata = publication.Metadata,
            Page = query.Page,
            PageSize = query.PageSize
        };
        if (!publication.CanReadRows)
            return response;

        var fb = Builders<WorkReportTableStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.ScopeType, query.ScopeType)
                     & fb.Eq(x => x.ScopeId, query.ScopeId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In("directProjection.generationId", publication.GenerationIds);
        if (!string.IsNullOrWhiteSpace(request.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, request.DynamicExcelTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.BlockId))
            filter &= fb.Eq(x => x.BlockId, request.BlockId.Trim());
        if (!string.IsNullOrWhiteSpace(request.TableMode))
            filter &= fb.Eq(x => x.TableMode, request.TableMode.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.MetricKey))
            filter &= fb.Eq(x => x.MetricKey, request.MetricKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.MetricLabelCode))
            filter &= fb.Eq(x => x.MetricLabelCode, request.MetricLabelCode.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(request.DataType))
            filter &= fb.Eq(x => x.DataType, request.DataType.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, request.BucketKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, request.PeriodKey.Trim());
        if (request.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, request.ReportStatus.Value);

        var all = await _ctx.WorkReportTableStatAggregates
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.BlockId)
            .ThenBy(x => x.MetricKey)
            .ThenBy(x => x.RowKey)
            .ThenBy(x => x.ColumnKey)
            .ThenBy(x => x.BucketKey)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        response.TotalRows = all.Count;
        response.TotalValueCount = all.Sum(x => x.ValueCount);
        response.TotalSum = all.Sum(x => x.Sum);
        response.TotalReportCount = all.Sum(x => x.ReportCount);
        response.Rows = all
            .Skip(query.Page * query.PageSize)
            .Take(query.PageSize)
            .Select(ToTableRow)
            .ToList();
        response.ReturnedRows = response.Rows.Count;
        AttachReconciliationIdentity(
            response.Metadata,
            all.Select(item => item.MetricKey),
            all.Select(item => item.PeriodKey),
            query.PeriodInstanceKey);
        if (request.IncludeDrilldown)
        {
            response.DrilldownRows = await ReadTableDrilldownAsync(
                request,
                query,
                publication.GenerationIds,
                access,
                ct);
        }
        return response;
    }

    public Task<LabelStatisticSummaryResponse> ReadLabelAsync(
        LabelStatisticSummaryRequest request,
        CancellationToken ct)
        => ReadLabelCoreAsync(request, null, ct);

    public Task<LabelStatisticSummaryResponse> ReadLabelPinnedAsync(
        LabelStatisticSummaryRequest request,
        string runId,
        CancellationToken ct)
        => ReadLabelCoreAsync(request, runId, ct);

    private async Task<LabelStatisticSummaryResponse> ReadLabelCoreAsync(
        LabelStatisticSummaryRequest request,
        string? pinnedRunId,
        CancellationToken ct)
    {
        request ??= new LabelStatisticSummaryRequest();
        var query = NormalizeQuery(
            request.WorkId,
            request.ScopeType,
            request.ScopeId,
            request.PeriodInstanceKey,
            request.Page,
            request.PageSize);
        var access = await AuthorizeBeforeExistenceAsync(query, ct);
        var pinnedGenerationId = NormalizeOptionalGenerationId(request.GenerationId);
        var publication = await ResolvePublicationAsync(
            query,
            request.DynamicFormTemplateId,
            pinnedRunId,
            pinnedGenerationId,
            ct);
        var response = new LabelStatisticSummaryResponse
        {
            Metadata = publication.Metadata,
            Page = query.Page,
            PageSize = query.PageSize
        };
        if (!publication.CanReadRows)
            return response;

        var fb = Builders<WorkReportLabelStatAggregate>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.ScopeType, query.ScopeType)
                     & fb.Eq(x => x.ScopeId, query.ScopeId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.In("directProjection.generationId", publication.GenerationIds);
        if (!string.IsNullOrWhiteSpace(request.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.DynamicExcelTemplateId))
            filter &= fb.Eq(x => x.DynamicExcelTemplateId, request.DynamicExcelTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.LabelCode))
            filter &= fb.Eq(x => x.LabelCode, request.LabelCode.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(request.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, request.PeriodKey.Trim());
        if (request.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, request.ReportStatus.Value);

        var all = await _ctx.WorkReportLabelStatAggregates
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.BlockId)
            .ThenBy(x => x.LabelCode)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        var labelCodes = all.Select(x => x.LabelCode).Distinct(StringComparer.Ordinal).ToList();
        var catalog = labelCodes.Count == 0
            ? new Dictionary<string, LabelCatalogItem>(StringComparer.Ordinal)
            : (await _ctx.Labels
                    .Find(x => labelCodes.Contains(x.Code) && !x.IsDeleted && x.IsActive)
                    .ToListAsync(ct))
                .GroupBy(x => x.Code, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        response.TotalRows = all.Count;
        response.TotalRowCount = all.Sum(x => x.RowCount);
        response.TotalReportCount = all.Sum(x => x.ReportCount);
        response.Rows = all
            .Skip(query.Page * query.PageSize)
            .Take(query.PageSize)
            .Select(x => ToLabelRow(
                x,
                catalog.TryGetValue(x.LabelCode, out var label) ? label : null))
            .ToList();
        response.ReturnedRows = response.Rows.Count;
        AttachReconciliationIdentity(
            response.Metadata,
            all.Select(item => item.LabelCode),
            all.Select(item => item.PeriodKey),
            query.PeriodInstanceKey);
        if (request.IncludeDrilldown)
        {
            response.DrilldownRows = await ReadLabelDrilldownAsync(
                request,
                query,
                publication.GenerationIds,
                access,
                ct);
        }
        return response;
    }

    public async Task<FieldTextConcatResponse> ReadTextAsync(
        FieldTextConcatRequest request,
        CancellationToken ct)
    {
        request ??= new FieldTextConcatRequest();
        if (string.IsNullOrWhiteSpace(request.DynamicFormTemplateId) ||
            (string.IsNullOrWhiteSpace(request.FieldId) &&
             string.IsNullOrWhiteSpace(request.FieldKey)))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_ARGUMENT_REQUIRED,
                new { reason = "DIRECT_TEXT_SELECTOR_REQUIRED" });
        }
        var query = NormalizeQuery(
            request.WorkId,
            request.ScopeType,
            request.ScopeId,
            request.PeriodInstanceKey,
            request.Page,
            request.PageSize);
        var access = await AuthorizeBeforeExistenceAsync(query, ct);
        var publication = await ResolvePublicationAsync(
            query,
            request.DynamicFormTemplateId,
            ct);
        var response = new FieldTextConcatResponse
        {
            Metadata = publication.Metadata,
            WorkId = query.WorkId,
            ScopeType = query.ScopeType,
            ScopeId = query.ScopeId,
            DynamicFormTemplateId = request.DynamicFormTemplateId.Trim(),
            FieldId = request.FieldId?.Trim() ?? string.Empty,
            FieldKey = request.FieldKey?.Trim() ?? string.Empty,
            Page = query.Page,
            PageSize = query.PageSize,
            MaxChars = Math.Clamp(
                request.MaxChars <= 0 ? 10000 : request.MaxChars,
                1000,
                50000),
            ScanLimit = Math.Clamp(
                request.ScanLimit <= 0 ? 1000 : request.ScanLimit,
                100,
                5000)
        };
        if (!publication.CanReadRows)
            return response;

        var fb = Builders<WorkReportFieldStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim())
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.AssignmentIsActive, true)
                     & fb.Eq(x => x.ReportIsActive, true)
                     & fb.Eq(x => x.ShowInDetail, true)
                     & fb.Ne(x => x.TextValue, null)
                     & fb.In("directProjection.generationId", publication.GenerationIds)
                     & fb.In(x => x.WorkAssignmentId, access.VisibleAssignmentIds);
        if (!string.IsNullOrWhiteSpace(request.FieldId))
            filter &= fb.Eq(x => x.FieldId, request.FieldId.Trim());
        if (!string.IsNullOrWhiteSpace(request.FieldKey))
            filter &= fb.Eq(x => x.FieldKey, request.FieldKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, request.BucketKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, request.PeriodKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.PeriodKeyFrom))
            filter &= fb.Gte(x => x.PeriodKey, request.PeriodKeyFrom.Trim());
        if (!string.IsNullOrWhiteSpace(request.PeriodKeyTo))
            filter &= fb.Lte(x => x.PeriodKey, request.PeriodKeyTo.Trim());
        if (request.ReportStatus.HasValue)
            filter &= fb.Eq(x => x.ReportStatus, request.ReportStatus.Value);

        var values = await _ctx.WorkReportFieldStatValues
            .Find(filter)
            .SortBy(x => x.PeriodKey)
            .ThenBy(x => x.WorkAssignmentReportId)
            .ThenBy(x => x.SourceKey)
            .ThenBy(x => x.Id)
            .Limit(response.ScanLimit)
            .ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            values = values
                .Where(x => (x.TextValue ?? string.Empty).Contains(
                    request.Q.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        var grouped = values
            .GroupBy(x => x.WorkAssignmentReportId, StringComparer.Ordinal)
            .OrderBy(x => x.Min(v => v.PeriodKey), StringComparer.Ordinal)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
        response.TotalRows = grouped.Count;
        response.MatchingReportCount = grouped.Count;
        response.ScannedReportCount = values.Count;
        response.HasMoreReportsThanScanLimit = values.Count >= response.ScanLimit;
        var pageGroups = grouped
            .Skip(query.Page * query.PageSize)
            .Take(query.PageSize)
            .ToList();
        var assignmentIds = pageGroups
            .SelectMany(x => x.Select(v => v.WorkAssignmentId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignments = assignmentIds.Count == 0
            ? new Dictionary<string, WorkAssignment>(StringComparer.Ordinal)
            : (await _ctx.WorkAssignments
                    .Find(x => assignmentIds.Contains(x.Id) && !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var maxRowChars = Math.Clamp(
            request.MaxRowChars <= 0 ? 2000 : request.MaxRowChars,
            200,
            10000);
        foreach (var group in pageGroups)
        {
            var first = group.First();
            assignments.TryGetValue(first.WorkAssignmentId, out var assignment);
            var fullText = string.Join(
                "\n",
                group.Select(x => x.TextValue).Where(x => x is not null));
            var rowTruncated = fullText.Length > maxRowChars;
            var text = rowTruncated ? fullText[..maxRowChars] : fullText;
            response.Rows.Add(new FieldTextConcatRow
            {
                WorkAssignmentReportId = first.WorkAssignmentReportId,
                WorkReportPeriodId = first.WorkReportPeriodId,
                AssignmentId = first.WorkAssignmentId,
                AssignmentCode = assignment?.Code,
                AssignmentName = assignment?.Name ?? string.Empty,
                AssigneeUserId = first.AssigneeUserId,
                PeriodKey = first.PeriodKey,
                PeriodInstanceKey = first.PeriodInstanceKey,
                PeriodKind = first.PeriodKind,
                ReportStatus = first.ReportStatus,
                Text = text,
                CharCount = fullText.Length,
                RowTruncated = rowTruncated,
                Items = group.Select(x => new FieldTextConcatItem
                {
                    Value = x.TextValue ?? string.Empty,
                    Label = x.BucketLabel ?? x.FieldLabel
                }).ToList()
            });
        }
        response.ReturnedRows = response.Rows.Count;
        response.FieldId = values.FirstOrDefault()?.FieldId ?? response.FieldId;
        response.FieldKey = values.FirstOrDefault()?.FieldKey ?? response.FieldKey;
        response.FieldLabel = values.FirstOrDefault()?.FieldLabel ?? string.Empty;
        response.FieldType = values.FirstOrDefault()?.FieldType ?? string.Empty;
        var concatenated = string.Join("\n", response.Rows.Select(x => x.Text));
        response.Truncated = concatenated.Length > response.MaxChars ||
                             response.Rows.Any(x => x.RowTruncated);
        response.ConcatenatedText = concatenated.Length > response.MaxChars
            ? concatenated[..response.MaxChars]
            : concatenated;
        response.TotalChars = response.ConcatenatedText.Length;
        return response;
    }

    private async Task<List<P9DirectDrilldownRow>> ReadFieldDrilldownAsync(
        FieldStatisticSummaryRequest request,
        DirectQuery query,
        IReadOnlyCollection<string> generationIds,
        DirectAccess access,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportFieldStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.AssignmentIsActive, true)
                     & fb.Eq(x => x.ReportIsActive, true)
                     & fb.Eq(x => x.ShowInDetail, true)
                     & fb.In("directProjection.generationId", generationIds)
                     & fb.In(x => x.WorkAssignmentId, access.VisibleAssignmentIds);
        if (!string.IsNullOrWhiteSpace(request.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.FieldId))
            filter &= fb.Eq(x => x.FieldId, request.FieldId.Trim());
        if (!string.IsNullOrWhiteSpace(request.FieldKey))
            filter &= fb.Eq(x => x.FieldKey, request.FieldKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, request.BucketKey.Trim());
        return (await _ctx.WorkReportFieldStatValues
                .Find(filter)
                .SortBy(x => x.PeriodKey)
                .ThenBy(x => x.FieldKey)
                .ThenBy(x => x.BucketKey)
                .ThenBy(x => x.WorkAssignmentReportId)
                .ThenBy(x => x.SourceKey)
                .ThenBy(x => x.Id)
                .Skip(query.Page * query.PageSize)
                .Limit(query.PageSize)
                .ToListAsync(ct))
            .Select(x => new P9DirectDrilldownRow
            {
                SourceKind = "FIELD",
                WorkAssignmentReportId = x.WorkAssignmentReportId,
                WorkReportPeriodId = x.WorkReportPeriodId,
                WorkAssignmentId = x.WorkAssignmentId,
                PeriodKey = x.PeriodKey,
                PeriodInstanceKey = x.PeriodInstanceKey,
                ValueState = x.ValueKind,
                StableIdentity = $"{x.FieldId}:{x.SourceKey}:{x.BucketKey}",
                FieldId = x.FieldId,
                FieldKey = x.FieldKey,
                BucketKey = x.BucketKey,
                NumericValue = x.NumericValue,
                BooleanValue = x.BooleanValue,
                DateValueUtc = x.DateValueUtc,
                TextValue = x.TextValue
            }).ToList();
    }

    private async Task<List<P9DirectDrilldownRow>> ReadTableDrilldownAsync(
        TableStatisticSummaryRequest request,
        DirectQuery query,
        IReadOnlyCollection<string> generationIds,
        DirectAccess access,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportTableStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.AssignmentIsActive, true)
                     & fb.Eq(x => x.ReportIsActive, true)
                     & fb.In("directProjection.generationId", generationIds)
                     & fb.In(x => x.WorkAssignmentId, access.VisibleAssignmentIds);
        if (!string.IsNullOrWhiteSpace(request.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.BlockId))
            filter &= fb.Eq(x => x.BlockId, request.BlockId.Trim());
        if (!string.IsNullOrWhiteSpace(request.MetricKey))
            filter &= fb.Eq(x => x.MetricKey, request.MetricKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.BucketKey))
            filter &= fb.Eq(x => x.BucketKey, request.BucketKey.Trim());
        return (await _ctx.WorkReportTableStatValues
                .Find(filter)
                .SortBy(x => x.PeriodKey)
                .ThenBy(x => x.BlockId)
                .ThenBy(x => x.MetricKey)
                .ThenBy(x => x.RowKey)
                .ThenBy(x => x.ColumnKey)
                .ThenBy(x => x.WorkAssignmentReportId)
                .ThenBy(x => x.SourceKey)
                .ThenBy(x => x.Id)
                .Skip(query.Page * query.PageSize)
                .Limit(query.PageSize)
                .ToListAsync(ct))
            .Select(x => new P9DirectDrilldownRow
            {
                SourceKind = "TABLE_METRIC",
                WorkAssignmentReportId = x.WorkAssignmentReportId,
                WorkReportPeriodId = x.WorkReportPeriodId,
                WorkAssignmentId = x.WorkAssignmentId,
                PeriodKey = x.PeriodKey,
                PeriodInstanceKey = x.PeriodInstanceKey,
                ValueState = x.ValueKind,
                StableIdentity = $"{x.BlockId}:{x.MetricKey}:{x.RowKey}:{x.ColumnKey}:{x.SourceKey}",
                MetricKey = x.MetricKey,
                RowKey = x.RowKey,
                ColumnKey = x.ColumnKey,
                BucketKey = x.BucketKey,
                NumericValue = x.NumericValue,
                BooleanValue = x.BooleanValue,
                DateValueUtc = x.DateValue,
                TextValue = x.TextValue
            }).ToList();
    }

    private async Task<List<P9DirectDrilldownRow>> ReadLabelDrilldownAsync(
        LabelStatisticSummaryRequest request,
        DirectQuery query,
        IReadOnlyCollection<string> generationIds,
        DirectAccess access,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportLabelStatValue>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.AssignmentIsActive, true)
                     & fb.Eq(x => x.ReportIsActive, true)
                     & fb.In("directProjection.generationId", generationIds)
                     & fb.In(x => x.WorkAssignmentId, access.VisibleAssignmentIds);
        if (!string.IsNullOrWhiteSpace(request.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, request.DynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(request.LabelCode))
            filter &= fb.Eq(x => x.LabelCode, request.LabelCode.Trim().ToLowerInvariant());
        return (await _ctx.WorkReportLabelStatValues
                .Find(filter)
                .SortBy(x => x.PeriodKey)
                .ThenBy(x => x.BlockId)
                .ThenBy(x => x.LabelCode)
                .ThenBy(x => x.RowKey)
                .ThenBy(x => x.RowIndex)
                .ThenBy(x => x.WorkAssignmentReportId)
                .ThenBy(x => x.Id)
                .Skip(query.Page * query.PageSize)
                .Limit(query.PageSize)
                .ToListAsync(ct))
            .Select(x => new P9DirectDrilldownRow
            {
                SourceKind = "ROW_LABEL",
                WorkAssignmentReportId = x.WorkAssignmentReportId,
                WorkReportPeriodId = x.WorkReportPeriodId,
                WorkAssignmentId = x.WorkAssignmentId,
                PeriodKey = x.PeriodKey,
                PeriodInstanceKey = x.PeriodInstanceKey,
                ValueState = "LABEL_IDENTITY",
                StableIdentity = $"{x.BlockId}:{x.RowKey}:{x.LabelCode}",
                RowKey = x.RowKey,
                LabelCode = x.LabelCode
            }).ToList();
    }

    private async Task<DirectAccess> AuthorizeBeforeExistenceAsync(
        DirectQuery query,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        var isAdmin = RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor);
        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsActive, true);
        if (!isAdmin)
        {
            filter &= fb.Or(
                fb.Eq(x => x.CreatedByUserId, actor.Id),
                fb.AnyEq(x => x.LeaderWatcherUserIds, actor.Id),
                fb.ElemMatch(x => x.Assignees, assignee => assignee.UserId == actor.Id));
        }
        if (query.ScopeType == "ASSIGNMENT")
            filter &= fb.Eq(x => x.Id, query.ScopeId);
        else if (query.ScopeType == "ROOT")
            filter &= fb.Or(
                fb.Eq(x => x.Id, query.ScopeId),
                fb.Eq(x => x.RootAssignmentId, query.ScopeId));
        var visible = await _ctx.WorkAssignments
            .Find(filter)
            .Project(x => x.Id)
            .ToListAsync(ct);
        if (visible.Count == 0 && !isAdmin)
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.STAT_RUN_FORBIDDEN,
                new
                {
                    reason = "SCOPE_FORBIDDEN",
                    writes = 0
                });
        }
        if (isAdmin)
        {
            visible = await _ctx.WorkAssignments
                .Find(x => x.WorkId == query.WorkId && !x.IsDeleted && x.IsActive)
                .Project(x => x.Id)
                .ToListAsync(ct);
        }
        return new DirectAccess(actor.Id, visible);
    }

    private Task<PublicationResolution> ResolvePublicationAsync(
        DirectQuery query,
        string? dynamicFormTemplateId,
        CancellationToken ct)
        => ResolvePublicationAsync(query, dynamicFormTemplateId, null, null, ct);

    private async Task<PublicationResolution> ResolvePublicationAsync(
        DirectQuery query,
        string? dynamicFormTemplateId,
        string? pinnedRunId,
        string? pinnedGenerationId,
        CancellationToken ct)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == query.WorkId && !x.IsDeleted)
            .Project(x => new { x.Id, x.DirectSourceRevision })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return PublicationResolution.Unavailable(new P9DirectResultMetadata
            {
                State = P9ResultStates.Stale,
                Freshness = "SOURCE_GONE",
                WorkId = query.WorkId,
                PeriodInstanceKey = query.PeriodInstanceKey,
                StaleReason = "SOURCE_GONE"
            });
        }

        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.WorkId, query.WorkId)
                     & fb.Eq(x => x.PeriodInstanceKey, query.PeriodInstanceKey)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(
                         x => x.RunKind,
                         WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection);
        if (!string.IsNullOrWhiteSpace(dynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId.Trim());
        if (!string.IsNullOrWhiteSpace(pinnedRunId))
            filter &= fb.Eq(x => x.Id, pinnedRunId.Trim());
        if (pinnedGenerationId is not null)
            filter &= fb.Eq(x => x.GenerationId, pinnedGenerationId);
        var jobs = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .SortByDescending(x => x.PublishedAtUtc)
            .ThenByDescending(x => x.CompletedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        var published = jobs
            .Where(x =>
                x.IsCurrentPublication &&
                x.Status == WorkReportStatisticRebuildJobStatuses.Completed &&
                !string.IsNullOrWhiteSpace(x.GenerationId) &&
                !string.IsNullOrWhiteSpace(x.GenerationHash))
            .OrderBy(x => x.DynamicFormTemplateId, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        RequireExactGenerationPublication(published, pinnedGenerationId);
        if (published.Count == 0)
        {
            var latest = jobs.FirstOrDefault();
            var state = latest?.Status switch
            {
                WorkReportStatisticRebuildJobStatuses.Pending => P9ResultStates.Pending,
                WorkReportStatisticRebuildJobStatuses.Running or
                    WorkReportStatisticRebuildJobStatuses.RetryWaiting => P9ResultStates.Building,
                WorkReportStatisticRebuildJobStatuses.DeadLetter => P9ResultStates.Failed,
                _ => P9ResultStates.Empty
            };
            return PublicationResolution.Unavailable(new P9DirectResultMetadata
            {
                State = state,
                Freshness = state,
                WorkId = query.WorkId,
                PeriodInstanceKey = query.PeriodInstanceKey,
                AuthoritativeSourceRevision = work.DirectSourceRevision,
                FailureCode = latest?.DiagnosticCode,
                StaleReason = latest?.StaleReason,
                ComputedAtUtc = latest?.ComputedAtUtc,
                PublishedAtUtc = latest?.PublishedAtUtc
            });
        }

        var stale = published.Any(x =>
            x.DirectSourceRevision.GetValueOrDefault() != work.DirectSourceRevision ||
            !string.Equals(
                x.FreshnessState,
                WorkReportStatisticRebuildJobFreshnessStates.Fresh,
                StringComparison.Ordinal));
        var staleReason = published
            .Where(x => !string.IsNullOrWhiteSpace(x.StaleReason))
            .Select(x => x.StaleReason)
            .FirstOrDefault();
        var metadata = new P9DirectResultMetadata
        {
            State = stale ? P9ResultStates.Stale : P9ResultStates.Ready,
            Freshness = stale ? "STALE" : "FRESH",
            WorkId = query.WorkId,
            PeriodInstanceKey = query.PeriodInstanceKey,
            AuthoritativeSourceRevision = work.DirectSourceRevision,
            StaleReason = stale
                ? staleReason ?? "DIRECT_SOURCE_REVISION_CHANGED"
                : null,
            ComputedAtUtc = published.Max(x => x.ComputedAtUtc),
            PublishedAtUtc = published.Max(x => x.PublishedAtUtc),
            Publications = published.Select(ToPublicationPin).ToList()
        };
        return stale
            ? PublicationResolution.Unavailable(metadata)
            : new PublicationResolution(
                metadata,
                published
                    .Select(x => x.GenerationId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
    }

    private static P9DirectPublicationPin ToPublicationPin(
        WorkReportStatisticRebuildJob job)
        => new()
        {
            RunId = job.Id,
            GenerationId = job.GenerationId ?? string.Empty,
            GenerationHash = job.GenerationHash ?? string.Empty,
            DirectSourceRevision = job.DirectSourceRevision.GetValueOrDefault(),
            DirectPublicationRevision = job.DirectPublicationRevision.GetValueOrDefault(),
            SourceLifecycleEventKey = job.SourceLifecycleEventKey ?? string.Empty,
            SourcePayloadRevision = job.SourcePayloadRevision.GetValueOrDefault(),
            SourcePayloadHash = job.SourcePayloadHash ?? string.Empty,
            SourceLifecycleRevision = job.SourceLifecycleRevision.GetValueOrDefault(),
            SourceMembershipSignature = job.SourceMembershipSignature ?? string.Empty,
            ConfigId = job.ConfigId ?? string.Empty,
            ConfigVersionId = job.ConfigVersionId ?? string.Empty,
            ConfigVersionNo = job.ConfigVersionNo.GetValueOrDefault(),
            ConfigRevision = job.ConfigRevision.GetValueOrDefault(),
            ConfigHash = job.ConfigHash ?? string.Empty,
            CatalogVersion = job.CatalogVersion ?? string.Empty,
            CatalogRawSha256 = job.CatalogRawSha256 ?? string.Empty,
            CatalogSemanticSha256 = job.CatalogSemanticSha256 ?? string.Empty,
            SchemaRawSha256 = job.SchemaRawSha256 ?? string.Empty,
            SchemaSemanticSha256 = job.SchemaSemanticSha256 ?? string.Empty,
            StageLockSha256 = job.StageLockSha256 ?? string.Empty,
            CandidateChainId = job.CandidateChainId ?? string.Empty
        };

    private static string? NormalizeOptionalGenerationId(string? value)
    {
        if (value is null)
            return null;
        if (!StatRunCanonicalJson.IsCanonicalSha256(value))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "GENERATION_ID_SHA256_CANONICAL_INVALID" });
        }
        return value;
    }

    private static void RequireExactGenerationPublication(
        IReadOnlyList<WorkReportStatisticRebuildJob> publications,
        string? pinnedGenerationId)
    {
        if (pinnedGenerationId is null)
            return;
        if (publications.Any(publication => !string.Equals(
                publication.GenerationId,
                pinnedGenerationId,
                StringComparison.Ordinal)))
        {
            throw DirectGenerationConflict(
                "DIRECT_GENERATION_PUBLICATION_MISMATCH");
        }
        if (publications.Count > 1)
        {
            throw DirectGenerationConflict(
                "DIRECT_GENERATION_PUBLICATION_AMBIGUOUS");
        }
    }

    private static AppException DirectGenerationConflict(string reason)
        => new(
            AppErrorCode.STAT_RUN_RESULT_STALE,
            new { reason, writes = 0 });

    private static DirectQuery NormalizeQuery(
        string? workId,
        string? scopeType,
        string? scopeId,
        string? periodInstanceKey,
        int page,
        int pageSize)
    {
        workId = workId?.Trim();
        periodInstanceKey = periodInstanceKey?.Trim();
        if (string.IsNullOrWhiteSpace(workId) ||
            string.IsNullOrWhiteSpace(periodInstanceKey))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_ARGUMENT_REQUIRED,
                new
                {
                    reason = string.IsNullOrWhiteSpace(workId)
                        ? "WORK_ID_REQUIRED"
                        : "PERIOD_INSTANCE_KEY_REQUIRED",
                    implicitPeriod = false
                });
        }
        scopeType = string.IsNullOrWhiteSpace(scopeType)
            ? "WORK"
            : scopeType.Trim().ToUpperInvariant();
        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "SCOPE_TYPE_INVALID" });
        }
        scopeId = string.IsNullOrWhiteSpace(scopeId)
            ? (scopeType == "WORK" ? workId : null)
            : scopeId.Trim();
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_ARGUMENT_REQUIRED,
                new { reason = "SCOPE_ID_REQUIRED" });
        }
        if (page < 0 || pageSize is < 1 or > 200)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.STAT_RUN_PAGE_INVALID,
                new
                {
                    page,
                    pageSize,
                    pageBase = 0,
                    minimumPageSize = 1,
                    maximumPageSize = 200
                });
        }
        return new DirectQuery(
            workId,
            scopeType,
            scopeId,
            periodInstanceKey,
            page,
            pageSize);
    }

    private static FieldStatisticSummaryRow ToFieldRow(
        WorkReportFieldStatAggregate x)
        => new()
        {
            WorkId = x.WorkId,
            ScopeType = x.ScopeType,
            ScopeId = x.ScopeId,
            RootAssignmentId = x.RootAssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName,
            FieldId = x.FieldId,
            FieldKey = x.FieldKey,
            FieldLabel = x.FieldLabel,
            FieldType = x.FieldType,
            StatisticLabelCodes = x.StatisticLabelCodes,
            ShowInTree = x.ShowInTree,
            ShowInDetail = x.ShowInDetail,
            BucketKey = x.BucketKey,
            BucketLabel = x.BucketLabel,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportStatus = x.ReportStatus,
            ValueCount = x.ValueCount,
            NumericValueCount = x.NumericValueCount,
            Sum = x.Sum,
            Min = x.Min,
            Max = x.Max,
            Average = x.NumericValueCount == 0 ? null : x.Sum / x.NumericValueCount,
            TrueCount = x.TrueCount,
            FalseCount = x.FalseCount,
            EarliestDateUtc = x.EarliestDateUtc,
            LatestDateUtc = x.LatestDateUtc,
            ReportCount = x.ReportCount,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static TableStatisticSummaryRow ToTableRow(
        WorkReportTableStatAggregate x)
        => new()
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
            TableMode = x.TableMode,
            MetricKey = x.MetricKey,
            MetricLabelCode = x.MetricLabelCode,
            RowKey = x.RowKey,
            ColumnKey = x.ColumnKey,
            DataType = x.DataType,
            BucketKey = x.BucketKey,
            BucketLabel = x.BucketLabel,
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportStatus = x.ReportStatus,
            ValueCount = x.ValueCount,
            NumericValueCount = x.NumericValueCount,
            Sum = x.Sum,
            Min = x.Min,
            Max = x.Max,
            Average = x.NumericValueCount == 0 ? null : x.Sum / x.NumericValueCount,
            TrueCount = x.TrueCount,
            FalseCount = x.FalseCount,
            EarliestDateUtc = x.EarliestDateUtc,
            LatestDateUtc = x.LatestDateUtc,
            ReportCount = x.ReportCount,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static LabelStatisticSummaryRow ToLabelRow(
        WorkReportLabelStatAggregate x,
        LabelCatalogItem? catalog)
        => new()
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
            LabelName = catalog?.Name,
            LabelColor = catalog?.Color,
            LabelDataType = catalog?.DataType ?? "NUMBER",
            PeriodKey = x.PeriodKey,
            PeriodInstanceKey = x.PeriodInstanceKey,
            PeriodKind = x.PeriodKind,
            ReportStatus = x.ReportStatus,
            RowCount = x.RowCount,
            ReportCount = x.ReportCount,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static void AttachReconciliationIdentity(
        P9DirectResultMetadata metadata,
        IEnumerable<string> conceptKeys,
        IEnumerable<string> periodKeys,
        string periodInstanceKey)
    {
        if (metadata.State != P9ResultStates.Ready ||
            metadata.Freshness != "FRESH" ||
            metadata.Publications.Count != 1)
            return;
        var concepts = conceptKeys
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (concepts.Length != 1)
            return;
        var periods = periodKeys
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (periods.Length != 1)
            return;
        var grains = periods
            .Select(value => ReconciliationGrain(value,
                periodInstanceKey))
            .Where(value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (grains.Length != 1)
            return;
        var publication = metadata.Publications[0];
        if (!CanonicalHex(publication.RunId, 24) ||
            !CanonicalHex(publication.GenerationId, 64) ||
            !CanonicalHex(publication.GenerationHash, 64))
            return;
        metadata.ReconciliationIdentity = new P9DirectReconciliationIdentity
        {
            P9RunId = publication.RunId,
            P9ResultId = publication.RunId,
            GenerationId = publication.GenerationId,
            GenerationHash = publication.GenerationHash,
            ConceptKey = concepts[0],
            PeriodKey = periods[0],
            Grain = grains[0]!
        };
    }

    private static string? ReconciliationGrain(
        string? periodKey,
        string periodInstanceKey)
    {
        var canonical = periodInstanceKey.Trim().ToUpperInvariant();
        if (canonical.StartsWith("DAY:", StringComparison.Ordinal)) return "DAY";
        if (canonical.StartsWith("MONTH:", StringComparison.Ordinal)) return "MONTH";
        if (canonical.StartsWith("YEAR:", StringComparison.Ordinal)) return "YEAR";
        var key = periodKey?.Trim() ?? string.Empty;
        if (key.Length >= 10 &&
            DateTime.TryParseExact(key[..10], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            return "DAY";
        if (key.Length >= 7 &&
            DateTime.TryParseExact(key[..7], "yyyy-MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            return "MONTH";
        if (key.Length == 4 &&
            DateTime.TryParseExact(key, "yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            return "YEAR";
        return null;
    }

    private static bool CanonicalHex(string? value, int length)
        => value?.Length == length &&
           value.All(character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record DirectQuery(
        string WorkId,
        string ScopeType,
        string ScopeId,
        string PeriodInstanceKey,
        int Page,
        int PageSize);

    private sealed record DirectAccess(
        string ActorUserId,
        IReadOnlyList<string> VisibleAssignmentIds);

    private sealed record PublicationResolution(
        P9DirectResultMetadata Metadata,
        IReadOnlyList<string> GenerationIds)
    {
        public bool CanReadRows =>
            Metadata.State == P9ResultStates.Ready && GenerationIds.Count > 0;

        public static PublicationResolution Unavailable(
            P9DirectResultMetadata metadata)
            => new(metadata, Array.Empty<string>());
    }
}
