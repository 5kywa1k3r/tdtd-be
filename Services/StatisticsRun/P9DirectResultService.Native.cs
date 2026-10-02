using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class P9DirectResultService
{
    private readonly StatRunDirectProjectionService? _nativePublicationOwner;

    public async Task<NativeStatisticResultResponse> ReadNativeAsync(NativeStatisticResultRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = NormalizeQuery(request.WorkId, "WORK", request.WorkId, request.PeriodInstanceKey, 0, 1);
        var access = await AuthorizeBeforeExistenceAsync(query, ct);
        if (!ObjectId.TryParse(request.DynamicFormTemplateId, out _)
            || request.RunId is not null && !ObjectId.TryParse(request.RunId, out _)
            || request.Historical && (request.RunId is null || request.GenerationId is null))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason = "NATIVE_RESULT_EXACT_PIN_REQUIRED" });
        _ = NormalizeOptionalGenerationId(request.GenerationId);
        P9DirectResultMetadata metadata;
        WorkReportStatisticRebuildJob job;
        if (!request.Historical)
        {
            var resolved = await ResolvePublicationAsync(query, request.DynamicFormTemplateId, request.RunId, request.GenerationId, ct);
            if (!resolved.CanReadRows) return new(resolved.Metadata, null, null);
            if (resolved.Metadata.Publications.Count != 1) throw DirectGenerationConflict("NATIVE_PUBLICATION_AMBIGUOUS");
            var pin = resolved.Metadata.Publications.Single();
            job = await _ctx.WorkReportStatisticRebuildJobs.Find(x => x.Id == pin.RunId && x.GenerationHash == pin.GenerationHash && !x.IsDeleted)
                .SingleOrDefaultAsync(ct) ?? throw DirectGenerationConflict("NATIVE_PUBLICATION_CHANGED");
            metadata = resolved.Metadata;
        }
        else
        {
            job = await _ctx.WorkReportStatisticRebuildJobs.Find(x => x.Id == request.RunId && x.GenerationId == request.GenerationId
                && x.WorkId == query.WorkId && x.PeriodInstanceKey == query.PeriodInstanceKey
                && x.DynamicFormTemplateId == request.DynamicFormTemplateId && !x.IsDeleted
                && x.RunKind == WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection && x.Status == WorkReportStatisticRebuildJobStatuses.Completed)
                .SingleOrDefaultAsync(ct) ?? throw DirectGenerationConflict("NATIVE_HISTORY_PIN_NOT_FOUND");
            metadata = new() { State = P9ResultStates.Ready, Freshness = "HISTORICAL", WorkId = query.WorkId,
                PeriodInstanceKey = query.PeriodInstanceKey, ComputedAtUtc = job.ComputedAtUtc, PublishedAtUtc = job.PublishedAtUtc,
                Publications = [ToPublicationPin(job)] };
        }
        var owner = _nativePublicationOwner ?? throw DirectGenerationConflict("NATIVE_PUBLICATION_READER_REQUIRED");
        await owner.ValidateNativePublicationAsync(job, ct);
        var artifact = await NativeStatisticPublicationContract.ReadAsync(_ctx.Db, job, ct);
        // The published result spans the complete work-period membership. A user
        // who sees one assignment must not receive other assignments' aggregation,
        // text, source references or configuration. Never filter a precomputed AVG.
        RequireCompleteNativeAccess(artifact, access.VisibleAssignmentIds);
        if (!request.Historical && !await owner.NativeCurrentPinsMatchAsync(job, ct))
            return NativeStale(metadata);

        var result = JsonSerializer.Deserialize<NativeStatisticResultDocument>(
            JsonSerializer.Serialize(artifact.Result, StatConfigCanonicalJson.StrictJsonOptions), StatConfigCanonicalJson.StrictJsonOptions)
            ?? throw DirectGenerationConflict("NATIVE_RESULT_CONTRACT_INVALID");
        var projection = NativeStatisticResultProjection.Project(artifact.DefinitionJson, artifact.ConfigurationJson, null, result);
        // Resolve current publication/freshness and ACL again after the full read.
        // Historical reads keep the exact old artifact, but still use current ACL.
        var latestAccess = await AuthorizeBeforeExistenceAsync(query, ct);
        RequireCompleteNativeAccess(artifact, latestAccess.VisibleAssignmentIds);
        if (!request.Historical)
        {
            var latest = await ResolvePublicationAsync(query, request.DynamicFormTemplateId, job.Id, job.GenerationId, ct);
            if (!latest.CanReadRows || latest.Metadata.Publications.Single().GenerationHash != job.GenerationHash)
                return new(latest.Metadata, null, null);
            metadata = latest.Metadata;
            if (!await owner.NativeCurrentPinsMatchAsync(job, ct)) return NativeStale(metadata);
        }
        return new(metadata, projection.Result, job.NativeStatisticPublication!.ArtifactHash) { NativeMetadata = projection.Metadata };
    }

    private static NativeStatisticResultResponse NativeStale(P9DirectResultMetadata metadata)
    {
        metadata.State = P9ResultStates.Stale; metadata.Freshness = "STALE";
        metadata.StaleReason = "NATIVE_CONFIG_CATALOG_OR_MEMBERSHIP_CHANGED";
        return new(metadata, null, null);
    }

    private static void RequireCompleteNativeAccess(NativeStatisticGenerationArtifact artifact, IReadOnlyList<string> assignments)
    {
        var visible = assignments.ToHashSet(StringComparer.Ordinal);
        if (artifact.Sources.Any(source => !visible.Contains(source.AssignmentId)))
            throw AppExceptionFactory.Forbidden(AppErrorCode.STAT_RUN_FORBIDDEN,
                new { reason = "NATIVE_COMPLETE_MEMBERSHIP_ACCESS_REQUIRED", writes = 0 });
    }

}
