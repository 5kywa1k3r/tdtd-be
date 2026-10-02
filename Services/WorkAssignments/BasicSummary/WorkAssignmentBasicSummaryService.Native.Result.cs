using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    private sealed record NativeBasicCapture(string Key, string SourceHash, WorkAssignment Scope, DynamicFormTemplate Template,
        P804ConfigState Config, NormalizedRequest Request, List<WorkAssignment> Assignments, List<WorkAssignmentReport> Reports,
        List<WorkReportPeriod> Periods, IReadOnlyDictionary<string, WorkReportPayloadSnapshot> Payloads, string PlanJson, string ActorId);

    public Task<BasicNativeSummaryResponse> GetNativeSummaryAsync(BasicNativeSummaryRequest request, CancellationToken ct)
    {
        LegacyAggregateRetirement.Reject();
        return request.Historical ? ReadNativeBasicHistoryAsync(request, ct)
            : GetNativeBasicCurrentAsync(request, _ => Task.FromResult(_me.RequireMe()), ct);
    }

    private async Task<BasicNativeSummaryResponse> GetNativeBasicCurrentAsync(BasicNativeSummaryRequest request,
        Func<CancellationToken, Task<MeResponse>> actor, CancellationToken ct)
    {
        var capture = await CaptureNativeBasicAsync(request, await actor(ct), ct);
        if (request.SnapshotId is not null && request.SnapshotId != capture.Key)
            throw NativeBasicError("BASIC_NATIVE_SNAPSHOT_STALE");
        var store = new BasicNativeSnapshotStore(_ctx.Db);
        var stored = await store.ReadAsync(capture.Key, ct);
        if (stored is null)
        {
            var sources = capture.Reports.Select(report => NativeStatisticCalculationSource.Capture(report, capture.Payloads[report.Id])).ToArray();
            var pins = sources.Select(s => s.Pin).ToArray();
            var result = NativeTableStatisticCalculator.Calculate(capture.PlanJson,
                DynamicFormNativeTableDefinition.ReadStored(capture.Template.NativeTablesVersion, capture.Template.TablesJson)!,
                capture.Template.SectionsJson, capture.Template.FieldsJson, capture.Template.BlocksJson,
                capture.Template.PublishedSchemaHash!, pins, WorkReportNativeSourcePin.Digest(pins), sources,
                NativeStatisticPublicationContract.CalculationLimits with { MaxOutputBytes = 10 * 1024 * 1024 }, ct);
            // Legacy fields/Excel use the SAME captured payloads and membership.
            // Native ranges bypass the legacy merge-of-period-summaries path.
            var legacy = await BuildSummaryAsync(capture.Key, capture.Scope, capture.Template, capture.Request,
                capture.Assignments, capture.Reports, capture.SourceHash, ct, capture.Payloads);
            legacy.Meta.SnapshotRefreshedAtUtc = null; // No wall clock in deterministic immutable content.
            P9ApplyRuntimeMeta(legacy, capture.Request.RuntimePin);
            var config = capture.Config;
            var artifact = new BasicNativeSnapshot(1, capture.Key, capture.Scope.Id, capture.ActorId,
                capture.Assignments.Select(a => a.Id).ToArray(),
                capture.Template.PublishedSchemaSnapshotJson!, StatConfigCanonicalJson.Canonicalize(capture.Template.StatisticConfigSections),
                P804CanonicalPayload(config.Payload), capture.PlanJson, capture.SourceHash,
                new(StatConfigOwnerKinds.BasicSummary, P804OwnerId(capture.Scope.Id, capture.Template.Id),
                    config.ConfigId, config.VersionId, config.VersionNo, config.Revision, config.Status, config.ConfigHash, config.DependencyPins),
                legacy, JsonSerializer.Deserialize<NativeStatisticResultDocument>(StatConfigCanonicalJson.Canonicalize(result),
                    StatConfigCanonicalJson.StrictJsonOptions)!);
            RequireSameNativeCapture(capture, await CaptureNativeBasicAsync(request, await actor(ct), ct));
            await _statConfigTransactions.ExecuteAsync(async (session, token) =>
            {
                var binding = _candidateActivation.RequireCapability(StatRunCapabilities.BasicSummary, StatRunRouteRegistry.BasicResult);
                if (StatConfigCanonicalJson.Canonicalize(binding) != StatConfigCanonicalJson.Canonicalize(capture.Request.RuntimePin!.Candidate))
                    throw NativeBasicError("BASIC_NATIVE_CANDIDATE_CHANGED");
                await FenceNativeBasicAsync(session, capture, token);
                await store.StoreAsync(session, artifact, token);
                return true;
            }, ct);
            stored = await store.ReadAsync(capture.Key, ct) ?? throw NativeBasicError("BASIC_NATIVE_SNAPSHOT_INTEGRITY");
        }
        // Captures are retained as evidence if a concurrent change makes them stale.
        // A cache hit still checks full current rights, source set and payload pins.
        RequireSameNativeCapture(capture, await CaptureNativeBasicAsync(request, await actor(ct), ct));
        var snapshot = stored.Value.Snapshot;
        if (snapshot.ActorId != capture.ActorId || snapshot.ScopeAssignmentId != capture.Scope.Id
            || snapshot.SourceSetHash != capture.SourceHash || snapshot.Configuration.ConfigHash != capture.Config.ConfigHash
            || snapshot.PlanJson != capture.PlanJson || snapshot.DefinitionJson != capture.Template.PublishedSchemaSnapshotJson)
            throw NativeBasicError("BASIC_NATIVE_SNAPSHOT_INTEGRITY");
        return NativeBasicResponse(snapshot, stored.Value.Hash, "REVALIDATED", capture.Request.IncludeSourceRows);
    }

    private async Task<BasicNativeSummaryResponse> ReadNativeBasicHistoryAsync(BasicNativeSummaryRequest request, CancellationToken ct)
    {
        _candidateActivation.RequireCapability(StatRunCapabilities.BasicSummary, StatRunRouteRegistry.BasicResult);
        var me = _me.RequireMe();
        var scopeId = P804NormalizeAssignmentId(request.ScopeAssignmentId);
        _ = await P804LoadAuthorizedAssignmentAsync(null, scopeId, me, false, ct);
        if (!StatRunCanonicalJson.IsCanonicalSha256(request.SnapshotId) || request.SelectedUnitIds is not null)
            throw NativeBasicError("BASIC_NATIVE_HISTORY_EXACT_SNAPSHOT_REQUIRED");
        var stored = await new BasicNativeSnapshotStore(_ctx.Db).ReadAsync(request.SnapshotId!, ct)
            ?? throw NativeBasicError("BASIC_NATIVE_HISTORY_NOT_FOUND");
        var snapshot = stored.Snapshot;
        if (snapshot.ScopeAssignmentId != scopeId || snapshot.ActorId != me.Id
            || request.DynamicFormTemplateId is not null && request.DynamicFormTemplateId != snapshot.Legacy.Meta.DynamicFormTemplateId)
            throw NativeBasicError("BASIC_NATIVE_HISTORY_SCOPE_MISMATCH");
        // Historical values keep their exact old config and sources, but access
        // remains current, including assignments that contributed zero reports.
        async Task CheckAccess()
        {
            var ids = snapshot.AssignmentIds.Append(scopeId).Distinct(StringComparer.Ordinal).ToArray();
            var leadershipWorkIds = await LoadLeadershipWorkIdsAsync(me.Id, ct);
            var allowed = await _ctx.WorkAssignments.CountDocumentsAsync(Builders<WorkAssignment>.Filter.Or(
                ids.Select(id => P804AssignmentAuthorizationFilter(id, me, false, leadershipWorkIds))), cancellationToken: ct);
            if (allowed != ids.Length) throw tdtd_be.Common.Errors.AppExceptionFactory.Forbidden(
                tdtd_be.Common.Errors.AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN,
                new { reason = "BASIC_SUMMARY_CONFIG_ACCESS_DENIED" });
        }
        await CheckAccess();
        var payload = P804DeserializeStoredPayload(snapshot.BasicConfigurationJson);
        var response = NativeBasicResponse(snapshot, stored.Hash, "HISTORICAL", payload.DetailHints!.IncludeSourceRows == true);
        _candidateActivation.RequireCapability(StatRunCapabilities.BasicSummary, StatRunRouteRegistry.BasicResult);
        await CheckAccess();
        return response;
    }

    private static BasicNativeSummaryResponse NativeBasicResponse(BasicNativeSnapshot snapshot, string hash,
        string freshness, bool includeSourceRows)
    {
        var projection = NativeStatisticResultProjection.Project(snapshot.DefinitionJson, snapshot.FormConfigurationJson, snapshot.PlanJson, snapshot.Native);
        ApplySourceView(snapshot.Legacy, NormalizeSourceView(null), includeSourceRows);
        snapshot.Legacy.Meta.FromSnapshot = true;
        return new(1, snapshot.SnapshotId, hash, snapshot.SourceSetHash, freshness, DateTime.UtcNow,
            snapshot.Configuration, snapshot.Legacy, projection.Result) { NativeMetadata = projection.Metadata };
    }

    private async Task<NativeBasicCapture> CaptureNativeBasicAsync(BasicNativeSummaryRequest request, MeResponse me, CancellationToken ct)
    {
        var candidate = _candidateActivation.RequireCapability(StatRunCapabilities.BasicSummary, StatRunRouteRegistry.BasicResult);
        var scopeId = P804NormalizeAssignmentId(request.ScopeAssignmentId);
        var scope = await P804LoadAuthorizedAssignmentAsync(null, scopeId, me, false, ct);
        var templateId = P804NormalizeTemplateId(request.DynamicFormTemplateId ?? scope.DynamicFormTemplateId!);
        var template = await P804LoadTemplateAsync(null, templateId, ct);
        if (template.NativeTablesVersion is null || !template.IsActive || !scope.IsActive || scope.InvalidatedByFlowEventId is not null
            || !string.IsNullOrWhiteSpace(scope.FlowInstanceId))
            throw NativeBasicError("BASIC_NATIVE_ACTIVE_NON_FLOW_SCOPE_REQUIRED");
        var normalized = await NormalizeRequestAsync(new() { ScopeAssignmentId = scopeId, DynamicFormTemplateId = templateId,
            SelectedUnitIds = request.SelectedUnitIds?.ToList() }, ct);
        normalized = await P9ApplyLockedRuntimeConfigAsync(scope, template, normalized, candidate, ct, nativeConsumer: true);
        ValidateSummaryScope(scope, normalized);
        var config = await P804LoadStateAsync(null, scope, template, ct);
        if (config.IsVirtual || config.Status != StatConfigStatuses.Locked || config.Payload.NativeTargets is null
            || config.ConfigHash != normalized.RuntimePin!.ConfigHash || config.Revision != normalized.RuntimePin.Revision)
            throw NativeBasicError("BASIC_NATIVE_LOCKED_CONFIG_REQUIRED");
        using (var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: ct))
            await P804EnsureDependenciesCurrentAsync(session, template, config, me, ct);
        var plan = ResolveNativeBasicPlan(template, config.Payload.NativeTargets);
        var assignments = (await LoadSourceAssignmentsAsync(scope, templateId, normalized, ct)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (assignments.Count > 1000) throw NativeBasicError("BASIC_NATIVE_ASSIGNMENT_QUOTA");
        var leadershipWorkIds = await LoadLeadershipWorkIdsAsync(me.Id, ct);
        foreach (var assignment in assignments)
        {
            if (!RoleGuard.IsSystemAdmin(me) && !CanReadAssignment(assignment, me.Id) &&
                !leadershipWorkIds.Contains(assignment.WorkId, StringComparer.Ordinal))
                throw NativeBasicError("BASIC_NATIVE_SOURCE_ACCESS_DENIED");
            if (assignment.WorkId != scope.WorkId || !assignment.IsActive || assignment.IsDeleted
                || assignment.InvalidatedByFlowEventId is not null || !string.IsNullOrWhiteSpace(assignment.FlowInstanceId)
                || assignment.DynamicFormTemplateId != template.Id || assignment.DynamicFormFamilyId != template.FamilyId
                || assignment.DynamicFormVersionNo != template.VersionNo || assignment.DynamicFormSchemaHash != template.PublishedSchemaHash)
                throw NativeBasicError("BASIC_NATIVE_ASSIGNMENT_PIN_INVALID");
        }
        var reports = (await LoadSourceReportsAsync(assignments.Select(a => a.Id).ToList(), templateId, normalized, ct))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var periods = reports.Count == 0 ? new List<WorkReportPeriod>() : await _ctx.WorkReportPeriods
            .Find(Builders<WorkReportPeriod>.Filter.In(p => p.Id, reports.Select(r => r.WorkReportPeriodId))).ToListAsync(ct);
        var byAssignment = assignments.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var byPeriod = periods.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var payloads = new Dictionary<string, WorkReportPayloadSnapshot>(StringComparer.Ordinal);
        long bytes = 0, actualBytes = 0;
        foreach (var report in reports)
        {
            if (report.WorkId != scope.WorkId || report.IsActive != true || report.InvalidatedByFlowEventId is not null
                || report.DynamicFormFamilyId != template.FamilyId || report.DynamicFormVersionNo != template.VersionNo
                || report.DynamicFormSchemaHash != template.PublishedSchemaHash || report.LifecycleRevision < 1
                || string.IsNullOrWhiteSpace(report.ApprovedByUserId)
                || !byAssignment[report.WorkAssignmentId].Assignees.Any(a => a.UserId == report.AssigneeUserId)
                || !byPeriod.TryGetValue(report.WorkReportPeriodId, out var period)
                || period.IsDeleted || !period.IsActive || period.CurrentReportId != report.Id
                || period.SourceLifecycleReportId != report.Id || period.SourceLifecycleRevision != report.LifecycleRevision
                || !period.SourceLifecycleAppliedAtUtc.HasValue || period.WorkId != report.WorkId
                || period.WorkAssignmentId != report.WorkAssignmentId || period.AssigneeUserId != report.AssigneeUserId
                || period.PeriodInstanceKey != report.PeriodInstanceKey || string.IsNullOrWhiteSpace(report.PeriodInstanceKey)
                || period.PeriodKey != report.PeriodKey || period.PeriodKind != report.PeriodKind
                || period.PeriodStart != report.PeriodStart || period.PeriodEnd != report.PeriodEnd
                || !byAssignment[report.WorkAssignmentId].Assignees.Any(a => a.UserId == report.AssigneeUserId && a.UnitId == period.AssigneeUnitId)
                || period.DynamicFormTemplateId is not null && period.DynamicFormTemplateId != template.Id
                || period.DynamicFormFamilyId is not null && period.DynamicFormFamilyId != template.FamilyId
                || period.DynamicFormVersionNo is not null && period.DynamicFormVersionNo != template.VersionNo
                || period.DynamicFormSchemaHash is not null && period.DynamicFormSchemaHash != template.PublishedSchemaHash
                || period.Status is not (WorkReportPeriodStatus.Approved or WorkReportPeriodStatus.OverdueApproved)
                || report.PeriodStart.HasValue && report.PeriodEnd.HasValue && report.PeriodStart > report.PeriodEnd)
                throw NativeBasicError("BASIC_NATIVE_REPORT_PERIOD_PIN_INVALID");
            NativeStatisticGenerationStage.RequireContribution(new(report.CumulativeContributionMode!, false), report.CumulativeContributionPolicyJson);
            if (report.PayloadSizeBytes < 0) throw NativeBasicError("BASIC_NATIVE_PAYLOAD_QUOTA");
            bytes = checked(bytes + report.PayloadSizeBytes);
            if (bytes > NativeStatisticPublicationContract.MaxSourcePayloadBytes) throw NativeBasicError("BASIC_NATIVE_PAYLOAD_QUOTA");
            var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
            actualBytes = checked(actualBytes + System.Text.Encoding.UTF8.GetByteCount(payload.Values1DJson)
                + System.Text.Encoding.UTF8.GetByteCount(payload.FieldValuesJson ?? "")
                + System.Text.Encoding.UTF8.GetByteCount(payload.TableValuesJson ?? "")
                + System.Text.Encoding.UTF8.GetByteCount(payload.SummarySourceJson ?? ""));
            if (actualBytes > NativeStatisticPublicationContract.MaxSourcePayloadBytes) throw NativeBasicError("BASIC_NATIVE_PAYLOAD_QUOTA");
            _ = WorkReportNativeSourcePin.Capture(report, payload);
            DynamicFormNativeTableValues.Validate(template, report.DynamicFormSchemaHash, payload.TableValuesJson, submitting: true);
            payloads.Add(report.Id, payload);
        }
        // Whole owner hashes include ACL, bindings, status and source ordering.
        // Technical publication counters are deliberately excluded from identity.
        var sourceHash = StatConfigCanonicalJson.HashObject(new {
            assignments = assignments.Select(NativeBasicOwnerHash), reports = reports.Select(NativeBasicOwnerHash),
            periods = periods.OrderBy(p => p.Id, StringComparer.Ordinal).Select(NativeBasicOwnerHash),
            sourceOrder = WorkReportNativeSourcePin.Digest(reports.Select(r => WorkReportNativeSourcePin.Capture(r, payloads[r.Id]))) });
        var key = StatConfigCanonicalJson.HashObject(new { version = "BASIC_NATIVE_CAPTURE_V1", actorId = me.Id,
            scope = NativeBasicOwnerHash(scope), template = NativeBasicOwnerHash(template), config.ConfigId, config.VersionId,
            config.VersionNo, config.Revision, config.ConfigHash, config.DependencyPins, sourceHash,
            units = normalized.SelectedUnitIds.OrderBy(id => id, StringComparer.Ordinal), candidate });
        return new(key, sourceHash, scope, template, config, normalized, assignments, reports, periods, payloads,
            StatConfigCanonicalJson.Canonicalize(plan), me.Id);
    }

    private async Task FenceNativeBasicAsync(IClientSessionHandle session, NativeBasicCapture capture, CancellationToken ct)
    {
        try { await FenceNativeBasicOwnersAsync(session, capture, ct); }
        catch (InvalidOperationException ex) when (ex.Message == "P9_DIRECT_NATIVE_PUBLICATION_SOURCE_FENCE_STALE")
        {
            throw NativeBasicError("BASIC_NATIVE_INPUT_CHANGED_RETRY");
        }
    }

    private async Task FenceNativeBasicOwnersAsync(IClientSessionHandle session, NativeBasicCapture capture, CancellationToken ct)
    {
        // Reuse the publication write-conflict fence. This transaction records an
        // immutable capture, not a current Direct publication or background job.
        static string[] Fields<T>(T owner) => owner.ToBsonDocument().Names.Where(n => n != "nativeStatisticPublicationFence").ToArray();
        await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.DynamicFormTemplates, capture.Template, Fields(capture.Template), ct);
        await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignmentBasicSummaryConfigs,
            capture.Config.Entity!, Fields(capture.Config.Entity!), ct);
        foreach (var assignment in capture.Assignments.Append(capture.Scope).DistinctBy(a => a.Id))
            await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignments, assignment, Fields(assignment), ct);
        foreach (var report in capture.Reports)
            await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignmentReports, report, Fields(report), ct);
        foreach (var period in capture.Periods)
            await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkReportPeriods, period, Fields(period), ct);
    }

    private static string NativeBasicOwnerHash<T>(T owner)
    {
        var document = owner.ToBsonDocument();
        document.Remove("nativeStatisticPublicationFence");
        return StatRunCanonicalJson.HashText(document.ToJson());
    }
    private static void RequireSameNativeCapture(NativeBasicCapture expected, NativeBasicCapture current)
    {
        if (expected.Key != current.Key) throw NativeBasicError("BASIC_NATIVE_INPUT_CHANGED_RETRY");
    }
    private static Exception NativeBasicError(string reason) => tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
        tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED, new { reason });
}
