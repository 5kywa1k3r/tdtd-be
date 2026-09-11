using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunExportService
{
    private static NormalizedExportRequest NormalizeRequest(
        StatRunExportCreateRequest request,
        MeResponse actor)
    {
        request ??= new StatRunExportCreateRequest();
        if (actor is null || string.IsNullOrWhiteSpace(actor.Id))
            throw AppExceptionFactory.Unauthorized();
        var commandId = request.CommandId?.Trim() ?? string.Empty;
        var commandBytes = Encoding.UTF8.GetByteCount(commandId);
        if (commandBytes is < 1 or > 128)
            throw Invalid("EXPORT_COMMAND_ID_INVALID");
        var workId = Required(request.WorkId, "EXPORT_WORK_ID_REQUIRED");
        var scopeType = string.IsNullOrWhiteSpace(request.ScopeType)
            ? "WORK"
            : request.ScopeType.Trim().ToUpperInvariant();
        if (scopeType is not ("WORK" or "ROOT" or "ASSIGNMENT"))
            throw Invalid("EXPORT_SCOPE_TYPE_INVALID");
        var scopeId = string.IsNullOrWhiteSpace(request.ScopeId)
            ? (scopeType == "WORK" ? workId : string.Empty)
            : request.ScopeId.Trim();
        if (scopeId.Length == 0)
            throw Invalid("EXPORT_SCOPE_ID_REQUIRED");
        var resultKind = StatRunExportContract.NormalizeResultKind(request.ResultKind);
        var period = request.PeriodInstanceKey?.Trim();
        if (resultKind.StartsWith("DIRECT_", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(period))
        {
            throw Invalid("EXPORT_PERIOD_INSTANCE_REQUIRED");
        }
        return new NormalizedExportRequest(
            commandId,
            StatRunExportContract.NormalizeFormat(request.Format),
            resultKind,
            workId,
            scopeType,
            scopeId,
            period,
            Required(request.ResultId, "EXPORT_RESULT_ID_REQUIRED"),
            RequiredSha(request.ExpectedResultHash, "EXPORT_RESULT_HASH_INVALID"),
            RequiredSha(request.ExpectedConfigHash, "EXPORT_CONFIG_HASH_INVALID"),
            RequiredSha(request.ExpectedSourceHash, "EXPORT_SOURCE_HASH_INVALID"),
            request.ExpectedLifecycleRevision,
            NormalizeFilters(request.Filters));
    }

    private async Task<CanonicalExportResult> ResolveCanonicalResultAsync(
        NormalizedExportRequest request,
        string capabilityId,
        StatRunCandidateBinding binding,
        MeResponse actor,
        CancellationToken ct)
    {
        if (request.ResultKind.StartsWith("DIRECT_", StringComparison.Ordinal))
            return await ResolveDirectAsync(request, ct);

        await AuthorizeScopeAsync(
            request.WorkId,
            request.ScopeType,
            request.ScopeId,
            actor,
            ct);
        return request.ResultKind switch
        {
            StatRunExportResultKinds.Basic => await ResolveBasicAsync(request, false, binding, ct),
            StatRunExportResultKinds.Flow => await ResolveBasicAsync(request, true, binding, ct),
            StatRunExportResultKinds.Advanced => await ResolveAdvancedAsync(request, binding, ct),
            StatRunExportResultKinds.Diff => await ResolveDiffAsync(request, binding, ct),
            _ => throw Invalid("EXPORT_RESULT_KIND_INVALID")
        };
    }

    private async Task<CanonicalExportResult> ResolveDirectAsync(
        NormalizedExportRequest request,
        CancellationToken ct)
    {
        const int pageSize = 200;
        var rows = new List<object>();
        P9DirectResultMetadata? metadata = null;
        long totalRows;
        var page = 0;
        do
        {
            switch (request.ResultKind)
            {
                case StatRunExportResultKinds.DirectField:
                {
                    var response = await _directResults.ReadFieldAsync(
                        new FieldStatisticSummaryRequest
                        {
                            WorkId = request.WorkId,
                            ScopeType = request.ScopeType,
                            ScopeId = request.ScopeId,
                            PeriodInstanceKey = request.PeriodInstanceKey,
                            DynamicFormTemplateId = request.Filters.DynamicFormTemplateId,
                            FieldId = request.Filters.FieldId,
                            FieldKey = request.Filters.FieldKey,
                            PeriodKey = request.Filters.PeriodKey,
                            BucketKey = request.Filters.BucketKey,
                            Page = page,
                            PageSize = pageSize
                        },
                        ct);
                    metadata ??= response.Metadata;
                    EnsureDirectMetadataStable(metadata, response.Metadata);
                    rows.AddRange(response.Rows.Cast<object>());
                    totalRows = response.TotalRows;
                    break;
                }
                case StatRunExportResultKinds.DirectTable:
                {
                    var response = await _directResults.ReadTableAsync(
                        new TableStatisticSummaryRequest
                        {
                            WorkId = request.WorkId,
                            ScopeType = request.ScopeType,
                            ScopeId = request.ScopeId,
                            PeriodInstanceKey = request.PeriodInstanceKey,
                            DynamicFormTemplateId = request.Filters.DynamicFormTemplateId,
                            BlockId = request.Filters.BlockId,
                            MetricKey = request.Filters.MetricKey,
                            PeriodKey = request.Filters.PeriodKey,
                            BucketKey = request.Filters.BucketKey,
                            Page = page,
                            PageSize = pageSize
                        },
                        ct);
                    metadata ??= response.Metadata;
                    EnsureDirectMetadataStable(metadata, response.Metadata);
                    rows.AddRange(response.Rows.Cast<object>());
                    totalRows = response.TotalRows;
                    break;
                }
                default:
                {
                    var response = await _directResults.ReadLabelAsync(
                        new LabelStatisticSummaryRequest
                        {
                            WorkId = request.WorkId,
                            ScopeType = request.ScopeType,
                            ScopeId = request.ScopeId,
                            PeriodInstanceKey = request.PeriodInstanceKey,
                            DynamicFormTemplateId = request.Filters.DynamicFormTemplateId,
                            LabelCode = request.Filters.LabelCode,
                            PeriodKey = request.Filters.PeriodKey,
                            Page = page,
                            PageSize = pageSize
                        },
                        ct);
                    metadata ??= response.Metadata;
                    EnsureDirectMetadataStable(metadata, response.Metadata);
                    rows.AddRange(response.Rows.Cast<object>());
                    totalRows = response.TotalRows;
                    break;
                }
            }
            if (rows.Count > StatRunExportContract.MaxRows)
                throw ExportLimit("ROW_LIMIT", rows.Count, 0);
            page++;
        } while (rows.Count < totalRows);

        if (metadata is null || metadata.State != P9ResultStates.Ready)
            throw ResultStale(metadata?.State ?? P9ResultStates.Empty);
        var publication = metadata.Publications.SingleOrDefault(pin =>
            string.Equals(pin.RunId, request.ResultId, StringComparison.Ordinal) ||
            string.Equals(pin.GenerationId, request.ResultId, StringComparison.Ordinal));
        if (publication is null)
            throw ResultStale("DIRECT_PUBLICATION_NOT_CURRENT");
        return new CanonicalExportResult(
            request.ResultId,
            publication.GenerationHash,
            publication.ConfigHash,
            publication.SourcePayloadHash,
            publication.SourceLifecycleRevision,
            publication.CatalogVersion,
            publication.CatalogRawSha256,
            publication.CatalogSemanticSha256,
            rows);
    }

    private async Task<CanonicalExportResult> ResolveBasicAsync(
        NormalizedExportRequest request,
        bool requireFlow,
        StatRunCandidateBinding binding,
        CancellationToken ct)
    {
        var snapshot = await _ctx.WorkAssignmentBasicSummarySnapshots
            .Find(item => item.Id == request.ResultId &&
                          item.WorkId == request.WorkId &&
                          item.ScopeAssignmentId == request.ScopeId &&
                          !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (snapshot is null)
            throw NotFound("EXPORT_CANONICAL_RESULT_NOT_FOUND");
        if (snapshot.SnapshotDirty ||
            snapshot.RefreshStatus != WorkAssignmentBasicSummaryRefreshStatuses.Done ||
            (requireFlow && !snapshot.SourceScopeMode.StartsWith("FLOW", StringComparison.Ordinal)) ||
            (!requireFlow && snapshot.SourceScopeMode.StartsWith("FLOW", StringComparison.Ordinal)))
        {
            throw ResultStale("BASIC_SNAPSHOT_NOT_CURRENT");
        }
        return CanonicalFromJson(
            snapshot.Id,
            snapshot.SnapshotJson,
            snapshot.ConfigHash,
            snapshot.SourceSignatureHash,
            0,
            snapshot.CandidateCatalogRawSha256,
            snapshot.CandidateCatalogSemanticSha256,
            binding.CatalogVersion);
    }

    private async Task<CanonicalExportResult> ResolveAdvancedAsync(
        NormalizedExportRequest request,
        StatRunCandidateBinding binding,
        CancellationToken ct)
    {
        WorkAssignmentAdvancedSummaryHierarchyNodeBase? node =
            await _ctx.WorkAssignmentAdvancedSummaryDayNodes
                .Find(item => item.Id == request.ResultId && item.WorkId == request.WorkId && !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
        node ??= await _ctx.WorkAssignmentAdvancedSummaryMonthNodes
            .Find(item => item.Id == request.ResultId && item.WorkId == request.WorkId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        node ??= await _ctx.WorkAssignmentAdvancedSummaryYearNodes
            .Find(item => item.Id == request.ResultId && item.WorkId == request.WorkId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (node is null || node.AssignmentId != request.ScopeId)
            throw NotFound("EXPORT_CANONICAL_RESULT_NOT_FOUND");
        if (node.IsDirty ||
            node.Status != WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean ||
            string.IsNullOrWhiteSpace(node.ValueHash))
        {
            throw ResultStale("ADVANCED_NODE_NOT_CURRENT");
        }
        return CanonicalFromJson(
            node.Id,
            node.ValueJson,
            node.ConfigHash,
            node.SourceSignatureHash ?? StatRunCanonicalJson.HashObject(node.SourceReportIds),
            0,
            node.CandidateCatalogRawSha256,
            node.CandidateCatalogSemanticSha256,
            binding.CatalogVersion,
            node.ValueHash);
    }

    private async Task<CanonicalExportResult> ResolveDiffAsync(
        NormalizedExportRequest request,
        StatRunCandidateBinding binding,
        CancellationToken ct)
    {
        var collection = _ctx.Db.GetCollection<WorkReportStatisticDiffResult>(
            "work_report_statistic_diff_results");
        var result = await collection.Find(item =>
                (item.Id == request.ResultId || item.RunId == request.ResultId) &&
                item.WorkId == request.WorkId &&
                item.AssignmentId == request.ScopeId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (result is null)
            throw NotFound("EXPORT_CANONICAL_RESULT_NOT_FOUND");
        if (result.Status != P9StatisticDiffResultStatuses.Completed ||
            !result.IsCurrent || !result.IsFresh || result.IsDirty ||
            !StatRunCanonicalJson.IsCanonicalSha256(result.ResultHash))
        {
            throw ResultStale("DIFF_RESULT_NOT_CURRENT");
        }
        var sourceHash = StatRunCanonicalJson.HashObject(result.SourcePins);
        var lifecycleRevision = result.SourcePins.Count == 0
            ? 0
            : result.SourcePins.Max(pin => pin.SourceLifecycleRevision);
        return new CanonicalExportResult(
            result.Id,
            result.ResultHash!,
            result.ConfigHash,
            sourceHash,
            lifecycleRevision,
            binding.CatalogVersion,
            result.CandidateCatalogRawSha256,
            result.CandidateCatalogSemanticSha256,
            result.Rows.Cast<object>().ToList());
    }

    private async Task AuthorizeScopeAsync(
        string workId,
        string scopeType,
        string scopeId,
        MeResponse actor,
        CancellationToken ct)
    {
        var isAdmin = RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor);
        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(item => item.WorkId, workId) &
                     fb.Eq(item => item.IsDeleted, false) &
                     fb.Eq(item => item.IsActive, true);
        if (!isAdmin)
        {
            filter &= fb.Or(
                fb.Eq(item => item.CreatedByUserId, actor.Id),
                fb.AnyEq(item => item.LeaderWatcherUserIds, actor.Id),
                fb.ElemMatch(item => item.Assignees, assignee => assignee.UserId == actor.Id));
        }
        if (scopeType == "ASSIGNMENT")
            filter &= fb.Eq(item => item.Id, scopeId);
        else if (scopeType == "ROOT")
            filter &= fb.Or(fb.Eq(item => item.Id, scopeId), fb.Eq(item => item.RootAssignmentId, scopeId));
        if (!await _ctx.WorkAssignments.Find(filter).Limit(1).AnyAsync(ct) && !isAdmin)
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.STAT_RUN_FORBIDDEN,
                new { reason = "SCOPE_FORBIDDEN", writes = 0 });
        }
    }

    private static void ValidateExpectedPins(
        NormalizedExportRequest request,
        CanonicalExportResult result)
    {
        if (!string.Equals(request.ResultId, result.ResultId, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedResultHash, result.ResultHash, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedConfigHash, result.ConfigHash, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedSourceHash, result.SourceHash, StringComparison.Ordinal) ||
            request.ExpectedLifecycleRevision != result.LifecycleRevision)
        {
            throw ResultStale("EXPORT_PIN_MISMATCH");
        }
    }

    private static void EnsureDirectMetadataStable(
        P9DirectResultMetadata expected,
        P9DirectResultMetadata actual)
    {
        if (expected.State != actual.State ||
            expected.AuthoritativeSourceRevision != actual.AuthoritativeSourceRevision ||
            StatRunCanonicalJson.HashObject(expected.Publications) !=
            StatRunCanonicalJson.HashObject(actual.Publications))
        {
            throw ResultStale("DIRECT_PUBLICATION_CHANGED_DURING_EXPORT");
        }
    }

    private static CanonicalExportResult CanonicalFromJson(
        string resultId,
        string json,
        string configHash,
        string sourceHash,
        int lifecycleRevision,
        string catalogRawSha256,
        string catalogSemanticSha256,
        string catalogVersion,
        string? resultHash = null)
    {
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(ToJsonObject).ToList()
            : new List<object> { ToJsonObject(document.RootElement) };
        return new CanonicalExportResult(
            resultId,
            resultHash ?? StatRunCanonicalJson.HashText(json),
            configHash,
            sourceHash,
            lifecycleRevision,
            catalogVersion,
            catalogRawSha256,
            catalogSemanticSha256,
            rows);
    }

    private static object ToJsonObject(JsonElement value) => value.Clone();

    private static StatRunExportFilterRequest NormalizeFilters(StatRunExportFilterRequest? filters)
    {
        filters ??= new StatRunExportFilterRequest();
        return new StatRunExportFilterRequest
        {
            DynamicFormTemplateId = Trim(filters.DynamicFormTemplateId),
            FieldId = Trim(filters.FieldId),
            FieldKey = Trim(filters.FieldKey),
            BlockId = Trim(filters.BlockId),
            MetricKey = Trim(filters.MetricKey),
            LabelCode = Trim(filters.LabelCode)?.ToLowerInvariant(),
            PeriodKey = Trim(filters.PeriodKey),
            BucketKey = Trim(filters.BucketKey)
        };
    }

    private static string Required(string? value, string reason)
        => string.IsNullOrWhiteSpace(value) ? throw Invalid(reason) : value.Trim();

    private static string RequiredSha(string? value, string reason)
    {
        var normalized = value?.Trim();
        return StatRunCanonicalJson.IsCanonicalSha256(normalized)
            ? normalized!
            : throw Invalid(reason);
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AppException ResultStale(string reason)
        => new(AppErrorCode.STAT_RUN_RESULT_STALE, new { reason, writes = 0 });

    private sealed record NormalizedExportRequest(
        string CommandId,
        string Format,
        string ResultKind,
        string WorkId,
        string ScopeType,
        string ScopeId,
        string? PeriodInstanceKey,
        string ResultId,
        string ExpectedResultHash,
        string ExpectedConfigHash,
        string ExpectedSourceHash,
        int ExpectedLifecycleRevision,
        StatRunExportFilterRequest Filters);

    private sealed record CanonicalExportResult(
        string ResultId,
        string ResultHash,
        string ConfigHash,
        string SourceHash,
        int LifecycleRevision,
        string CatalogVersion,
        string CatalogRawSha256,
        string CatalogSemanticSha256,
        IReadOnlyList<object> Rows);
}
