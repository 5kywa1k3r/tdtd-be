using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Enum;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    private const int NativeSourceScanLimit = 12000;
    private sealed record NativeAdvancedCapture(string Key, string SourceHash, P805OwnerContext Owner,
        P805ConfigState Config, AdvancedNativeSummaryRequest Request, DateTime StartUtc, DateTime EndExclusiveUtc,
        List<WorkAssignment> Assignments, List<WorkAssignmentReport> Reports, List<WorkReportPeriod> Periods,
        Dictionary<string, WorkReportPayloadSnapshot> Payloads, string PlanJson, StatRunCandidateBinding Candidate);

    public Task<AdvancedNativeSummaryResponse> GetNativeSummaryAsync(AdvancedNativeSummaryRequest request, CancellationToken ct)
    {
        LegacyAggregateRetirement.Reject();
        return request.Historical ? ReadNativeAdvancedHistoryAsync(request, ct)
            : GetNativeAdvancedCurrentAsync(request, _ => Task.FromResult(_me.RequireMe()), ct);
    }

    private async Task<AdvancedNativeSummaryResponse> GetNativeAdvancedCurrentAsync(AdvancedNativeSummaryRequest request,
        Func<CancellationToken, Task<MeResponse>> actor, CancellationToken ct)
    {
        var capture = await CaptureNativeAdvancedAsync(request, await actor(ct), ct);
        if (request.SnapshotId is not null && request.SnapshotId != capture.Key) throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_STALE");
        var store = new AdvancedNativeSnapshotStore(_ctx.Db);
        var stored = await store.ReadAsync(capture.Key, ct);
        if (stored is null)
        {
            _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.AdvancedBuild);
            // Broad historical builds retain the existing quota policy. The
            // exact capture is the command identity, including across retries.
            string? ledger = null;
            if (request.Grain is "MONTH" or "YEAR" or "RANGE")
            {
                ledger = (await _summaryTokens.ConsumeAdvancedBroadHistoricalBuildP9Async(capture.Config.Entity!,
                    capture.Owner.Assignment.IssuedByUnitId!, "native-" + capture.Key, request.Grain, request.GrainKey,
                    capture.Owner.Actor.Id, ct)).LedgerId;
            }
            var snapshot = CalculateNativeAdvancedSnapshot(capture, ledger, ct);
            RequireSameNativeAdvanced(capture, await CaptureNativeAdvancedAsync(request, await actor(ct), ct));
            await _statConfigTransactions.ExecuteAsync(async (session, token) => {
                RequireNativeAdvancedCandidate(capture);
                await FenceNativeAdvancedAsync(session, capture, token);
                await store.StoreAsync(session, snapshot, token);
                return true;
            }, ct);
            stored = await store.ReadAsync(capture.Key, ct) ?? throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
        }
        RequireSameNativeAdvanced(capture, await CaptureNativeAdvancedAsync(request, await actor(ct), ct));
        var value = stored.Value.Snapshot;
        if (value.ActorId != capture.Owner.Actor.Id || value.SourceSetHash != capture.SourceHash
            || value.ScopeAssignmentId != capture.Owner.Assignment.Id || value.PlanJson != capture.PlanJson
            || value.DefinitionJson != capture.Owner.Template.PublishedSchemaSnapshotJson
            || value.Configuration.ConfigHash != capture.Config.ConfigHash || value.SectionId != request.SectionId
            || value.Grain != request.Grain || value.GrainKey != request.GrainKey)
            throw NativeAdvancedError("ADVANCED_NATIVE_SNAPSHOT_INTEGRITY");
        return NativeAdvancedResponse(value, stored.Value.Hash, "REVALIDATED");
    }

    private static AdvancedNativeSnapshot CalculateNativeAdvancedSnapshot(NativeAdvancedCapture capture, string? ledger, CancellationToken ct)
    {
        var request = capture.Request;
        var template = capture.Owner.Template;
        var sources = capture.Reports.Select(r => NativeStatisticCalculationSource.Capture(r, capture.Payloads[r.Id])).ToArray();
        var pins = sources.Select(s => s.Pin).ToArray();
        var result = NativeTableStatisticCalculator.Calculate(capture.PlanJson,
            DynamicFormNativeTableDefinition.ReadStored(template.NativeTablesVersion, template.TablesJson)!,
            template.SectionsJson, template.FieldsJson, template.BlocksJson, template.PublishedSchemaHash!,
            pins, WorkReportNativeSourcePin.Digest(pins), sources,
            NativeStatisticPublicationContract.CalculationLimits with { MaxOutputBytes = 10 * 1024 * 1024 }, ct);
        var fields = WorkAssignmentAdvancedSummaryHierarchyService.CalculateCapturedFields(template, request.SectionId,
            capture.Config.Payload.Sections![0].Targets!, capture.Reports, capture.Payloads);
        var config = capture.Config;
        var snapshot = new AdvancedNativeSnapshot(1, capture.Key, capture.Owner.Assignment.Id, capture.Owner.Actor.Id,
            capture.Assignments.Select(a => a.Id).ToArray(), template.Id, request.SectionId, request.Grain, request.GrainKey,
            capture.StartUtc, capture.EndExclusiveUtc, template.PublishedSchemaSnapshotJson!,
            StatConfigCanonicalJson.Canonicalize(template.StatisticConfigSections),
            StatConfigCanonicalJson.Canonicalize(config.Payload), capture.PlanJson, capture.SourceHash,
            new(StatConfigOwnerKinds.AdvancedSummary, P805OwnerId(capture.Owner), config.ConfigId, config.VersionId,
                config.VersionNo, config.Revision, config.Status, config.ConfigHash, config.DependencyPins), fields,
            JsonSerializer.Deserialize<NativeStatisticResultDocument>(StatConfigCanonicalJson.Canonicalize(result),
                StatConfigCanonicalJson.StrictJsonOptions)!, ledger);
        return snapshot;
    }

    private async Task<AdvancedNativeSummaryResponse> ReadNativeAdvancedHistoryAsync(AdvancedNativeSummaryRequest request, CancellationToken ct)
    {
        RequireNativeAdvancedResultGate();
        var me = _me.RequireMe();
        _ = NativeAdvancedBounds(request.Grain, request.GrainKey);
        var scope = P805NormalizeAssignmentId(request.ScopeAssignmentId);
        _ = await P805LoadAuthorizedAssignmentAsync(null, scope, me, false, ct);
        if (!StatRunCanonicalJson.IsCanonicalSha256(request.SnapshotId)) throw NativeAdvancedError("ADVANCED_NATIVE_HISTORY_EXACT_SNAPSHOT_REQUIRED");
        var stored = await new AdvancedNativeSnapshotStore(_ctx.Db).ReadAsync(request.SnapshotId!, ct)
            ?? throw NativeAdvancedError("ADVANCED_NATIVE_HISTORY_NOT_FOUND");
        var snapshot = stored.Snapshot;
        if (snapshot.ActorId != me.Id || snapshot.ScopeAssignmentId != scope || snapshot.TemplateId != request.DynamicFormTemplateId
            || snapshot.SectionId != request.SectionId || snapshot.Grain != request.Grain || snapshot.GrainKey != request.GrainKey)
            throw NativeAdvancedError("ADVANCED_NATIVE_HISTORY_SCOPE_MISMATCH");
        async Task CheckAccess()
        {
            foreach (var id in snapshot.AssignmentIds.Append(scope).Distinct(StringComparer.Ordinal))
                _ = await P805LoadAuthorizedAssignmentAsync(null, id, me, false, ct);
        }
        await CheckAccess();
        var response = NativeAdvancedResponse(snapshot, stored.Hash, "HISTORICAL");
        RequireNativeAdvancedResultGate();
        await CheckAccess();
        return response;
    }

    private static AdvancedNativeSummaryResponse NativeAdvancedResponse(AdvancedNativeSnapshot snapshot, string hash, string freshness)
    {
        var projection = NativeStatisticResultProjection.Project(snapshot.DefinitionJson, snapshot.FormConfigurationJson, snapshot.PlanJson, snapshot.Native);
        return new(1, snapshot.SnapshotId, hash, snapshot.SourceSetHash, freshness, DateTime.UtcNow, snapshot.Configuration,
            snapshot.SectionId, snapshot.Grain, snapshot.GrainKey, "UTC_GREGORIAN", snapshot.StartUtc, snapshot.EndExclusiveUtc,
            snapshot.AssignmentIds.Count, snapshot.LegacyFields, projection.Result, snapshot.QuotaLedgerId) { NativeMetadata = projection.Metadata };
    }

    private async Task<NativeAdvancedCapture> CaptureNativeAdvancedAsync(AdvancedNativeSummaryRequest request, MeResponse me, CancellationToken ct)
    {
        var candidate = RequireNativeAdvancedResultGate();
        if (new[] { request.ScopeAssignmentId, request.DynamicFormTemplateId, request.SectionId }
            .Any(value => string.IsNullOrWhiteSpace(value) || value != value.Trim()))
            throw NativeAdvancedError("ADVANCED_NATIVE_OWNER_ID_INVALID");
        var (start, end) = NativeAdvancedBounds(request.Grain, request.GrainKey);
        var owner = await P805LoadOwnerContextAsync(null, request.ScopeAssignmentId, request.DynamicFormTemplateId, request.SectionId, me, false, ct);
        var scope = owner.Assignment; var template = owner.Template;
        if (template.NativeTablesVersion is null || !scope.IsActive || scope.InvalidatedByFlowEventId is not null || !string.IsNullOrWhiteSpace(scope.FlowInstanceId))
            throw NativeAdvancedError("ADVANCED_NATIVE_ACTIVE_NON_FLOW_SCOPE_REQUIRED");
        var config = await P805LoadStateAsync(null, owner, ct);
        if (config.IsVirtual || config.Status != StatConfigStatuses.Locked || config.Payload.Sections![0].NativeTargets is null)
            throw NativeAdvancedError("ADVANCED_NATIVE_LOCKED_CONFIG_REQUIRED");
        if (!config.Payload.HierarchyGrains!.Contains(request.Grain == "RANGE" ? "DAY" : request.Grain, StringComparer.Ordinal)) throw NativeAdvancedError("ADVANCED_NATIVE_GRAIN_NOT_CONFIGURED");
        if (config.Payload.Grouping!.Count != 0 || config.Payload.Ordering!.Count != 0)
            throw NativeAdvancedError("ADVANCED_NATIVE_GROUPING_ORDERING_UNSUPPORTED");
        if (P805FlowModes.Contains(config.Payload.SourceScope!.Mode!)) throw NativeAdvancedError("ADVANCED_NATIVE_FLOW_CONSUMER_REQUIRED");
        using (var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: ct))
            await P805EnsureDependenciesCurrentAsync(session, owner, config.Payload, config.DependencyPins, ct);
        var plan = ResolveNativeAdvancedPlan(template, request.SectionId, config.Payload.Sections[0].NativeTargets!);
        var source = config.Payload.SourceScope;
        var normalized = WorkAssignmentSummarySourceScope.Normalize(scope, source.Mode, source.FlowInstanceId, source.FlowStepId, source.FlowBranchId, source.FlowEffectiveStatus);
        var assignments = (await WorkAssignmentSummarySourceScope.LoadAssignmentsAsync(_ctx.WorkAssignments, scope, template.Id, [], normalized, ct))
            .DistinctBy(a => a.Id).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (assignments.Count > 1000) throw NativeAdvancedError("ADVANCED_NATIVE_ASSIGNMENT_QUOTA");
        foreach (var assignment in assignments)
        {
            _ = await P805LoadAuthorizedAssignmentAsync(null, assignment.Id, me, false, ct);
            if (assignment.WorkId != scope.WorkId || !assignment.IsActive || assignment.IsDeleted
                || assignment.InvalidatedByFlowEventId is not null || !string.IsNullOrWhiteSpace(assignment.FlowInstanceId)
                || assignment.DynamicFormTemplateId != template.Id || assignment.DynamicFormFamilyId != template.FamilyId
                || assignment.DynamicFormVersionNo != template.VersionNo || assignment.DynamicFormSchemaHash != template.PublishedSchemaHash)
                throw NativeAdvancedError("ADVANCED_NATIVE_ASSIGNMENT_PIN_INVALID");
        }
        var reports = await LoadNativeAdvancedReportsAsync(assignments.Select(a => a.Id).ToArray(), template.Id, start, end, ct);
        if (reports.Count > NativeStatisticPublicationContract.CalculationLimits.MaxReports)
            throw NativeAdvancedError("ADVANCED_NATIVE_SOURCE_REPORT_QUOTA");
        var periods = reports.Count == 0 ? new List<WorkReportPeriod>() : await _ctx.WorkReportPeriods.Find(
            Builders<WorkReportPeriod>.Filter.In(p => p.Id, reports.Select(r => r.WorkReportPeriodId))).ToListAsync(ct);
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
                throw NativeAdvancedError("ADVANCED_NATIVE_REPORT_PERIOD_PIN_INVALID");
            NativeStatisticGenerationStage.RequireContribution(new(report.CumulativeContributionMode!, false), report.CumulativeContributionPolicyJson);
            if (report.PayloadSizeBytes < 0) throw NativeAdvancedError("ADVANCED_NATIVE_PAYLOAD_QUOTA");
            bytes = checked(bytes + report.PayloadSizeBytes);
            if (bytes > NativeStatisticPublicationContract.MaxSourcePayloadBytes) throw NativeAdvancedError("ADVANCED_NATIVE_PAYLOAD_QUOTA");
            var payload = await _payloadReader.LoadReportPayloadAsync(report, ct);
            actualBytes = checked(actualBytes + System.Text.Encoding.UTF8.GetByteCount(payload.Values1DJson)
                + System.Text.Encoding.UTF8.GetByteCount(payload.FieldValuesJson ?? "") + System.Text.Encoding.UTF8.GetByteCount(payload.TableValuesJson ?? "")
                + System.Text.Encoding.UTF8.GetByteCount(payload.SummarySourceJson ?? ""));
            if (actualBytes > NativeStatisticPublicationContract.MaxSourcePayloadBytes) throw NativeAdvancedError("ADVANCED_NATIVE_PAYLOAD_QUOTA");
            _ = WorkReportNativeSourcePin.Capture(report, payload);
            DynamicFormNativeTableValues.Validate(template, report.DynamicFormSchemaHash, payload.TableValuesJson, submitting: true);
            payloads.Add(report.Id, payload);
        }
        var sourceHash = StatConfigCanonicalJson.HashObject(new { assignments = assignments.Select(NativeAdvancedOwnerHash),
            reports = reports.Select(NativeAdvancedOwnerHash), periods = periods.OrderBy(p => p.Id, StringComparer.Ordinal).Select(NativeAdvancedOwnerHash),
            sourceOrder = WorkReportNativeSourcePin.Digest(reports.Select(r => WorkReportNativeSourcePin.Capture(r, payloads[r.Id]))) });
        var key = StatConfigCanonicalJson.HashObject(new { version = "ADVANCED_NATIVE_CAPTURE_V1", actorId = me.Id,
            scope = NativeAdvancedOwnerHash(scope), template = NativeAdvancedOwnerHash(template), config.ConfigId, config.VersionId,
            config.VersionNo, config.Revision, config.ConfigHash, config.DependencyPins, sourceHash, request.SectionId,
            request.Grain, request.GrainKey, timeAxis = "UTC_GREGORIAN", candidate });
        return new(key, sourceHash, owner, config, request, start, end, assignments, reports, periods, payloads,
            StatConfigCanonicalJson.Canonicalize(plan), candidate);
    }

    private async Task<List<WorkAssignmentReport>> LoadNativeAdvancedReportsAsync(string[] assignments, string templateId,
        DateTime start, DateTime end, CancellationToken ct)
    {
        if (assignments.Length == 0) return [];
        var f = Builders<WorkAssignmentReport>.Filter;
        // Superset of ALL source-day resolver branches. Enforce scan limit
        // BEFORE filtering; never publish a silently truncated candidate set.
        var coarse = f.Or(f.Gte(r => r.CompletedDate, start) & f.Lt(r => r.CompletedDate, end),
            f.Regex(r => r.PeriodKey, new BsonRegularExpression(@"^\s*\d{4}-\d{2}-\d{2}\s*$")),
            f.Gte(r => r.PeriodStart, start) & f.Lt(r => r.PeriodStart, end),
            f.Gte(r => r.ReportDate, start) & f.Lt(r => r.ReportDate, end),
            f.Gte(r => r.ApprovedAtUtc, start) & f.Lt(r => r.ApprovedAtUtc, end),
            f.Eq(r => r.CompletedDate, null) & f.Eq(r => r.PeriodStart, null)
                & f.Eq(r => r.ReportDate, null) & f.Eq(r => r.ApprovedAtUtc, null));
        var rows = await _ctx.WorkAssignmentReports.Find(f.In(r => r.WorkAssignmentId, assignments) & f.Eq(r => r.DynamicFormTemplateId, templateId)
            & f.Or(f.Eq(r => r.PeriodKind, null), f.Eq(r => r.PeriodKind, "SCHEDULED"))
            & f.Eq(r => r.Status, WorkAssignmentReportStatus.Approved) & f.Eq(r => r.IsDeleted, false)
            & f.Eq(r => r.IsCurrent, true) & f.Ne(r => r.IsActive, false)
            & f.Ne(r => r.CumulativeContributionMode, "EXCLUDE") & coarse)
            .SortBy(r => r.Id).Limit(NativeSourceScanLimit + 1).ToListAsync(ct);
        if (rows.Count > NativeSourceScanLimit) throw NativeAdvancedError("ADVANCED_NATIVE_SOURCE_SCAN_QUOTA");
        return rows.Where(r => { var day = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(AdvancedSummaryReportSourceDayResolver.Resolve(r));
            return day >= start && day < end; }).DistinctBy(r => r.Id).ToList();
    }

    private static (DateTime Start, DateTime End) NativeAdvancedBounds(string grain, string key)
    {
        try
        {
            if (key is null || key != key.Trim()) throw new ArgumentException();
            return grain switch { "DAY" => AdvancedSummaryHierarchyKeyHelper.GetDayBoundsUtc(key),
                "MONTH" => AdvancedSummaryHierarchyKeyHelper.GetMonthBoundsUtc(key), "YEAR" => AdvancedSummaryHierarchyKeyHelper.GetYearBoundsUtc(key),
                "RANGE" => AdvancedNativeRange.Bounds(key),
                _ => throw new ArgumentException() };
        }
        catch (ArgumentException) { throw NativeAdvancedError("ADVANCED_NATIVE_WINDOW_INVALID"); }
    }

    private async Task FenceNativeAdvancedAsync(IClientSessionHandle session, NativeAdvancedCapture capture, CancellationToken ct)
    {
        static string[] Fields<T>(T owner) => owner.ToBsonDocument().Names.Where(n => n != "nativeStatisticPublicationFence").ToArray();
        try
        {
            await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.DynamicFormTemplates, capture.Owner.Template, Fields(capture.Owner.Template), ct);
            await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignmentAdvancedSummaryConfigs, capture.Config.Entity!, Fields(capture.Config.Entity!), ct);
            foreach (var a in capture.Assignments.Append(capture.Owner.Assignment).DistinctBy(a => a.Id))
                await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignments, a, Fields(a), ct);
            foreach (var r in capture.Reports) await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkAssignmentReports, r, Fields(r), ct);
            foreach (var p in capture.Periods) await StatRunDirectProjectionService.FenceDocumentAsync(session, _ctx.WorkReportPeriods, p, Fields(p), ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "P9_DIRECT_NATIVE_PUBLICATION_SOURCE_FENCE_STALE")
        { throw NativeAdvancedError("ADVANCED_NATIVE_INPUT_CHANGED_RETRY"); }
    }

    private StatRunCandidateBinding RequireNativeAdvancedResultGate()
        => _candidateActivation.RequireCapability(StatRunCapabilities.AdvancedSummary, StatRunRouteRegistry.AdvancedResult);
    private void RequireNativeAdvancedCandidate(NativeAdvancedCapture capture)
    {
        if (StatConfigCanonicalJson.Canonicalize(RequireNativeAdvancedResultGate()) != StatConfigCanonicalJson.Canonicalize(capture.Candidate))
            throw NativeAdvancedError("ADVANCED_NATIVE_CANDIDATE_CHANGED");
    }
    private static string NativeAdvancedOwnerHash<T>(T owner)
    { var doc = owner.ToBsonDocument(); doc.Remove("nativeStatisticPublicationFence"); return StatRunCanonicalJson.HashText(doc.ToJson()); }
    private static void RequireSameNativeAdvanced(NativeAdvancedCapture expected, NativeAdvancedCapture actual)
    { if (expected.Key != actual.Key) throw NativeAdvancedError("ADVANCED_NATIVE_INPUT_CHANGED_RETRY"); }
    private static Exception NativeAdvancedError(string reason) => tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
        tdtd_be.Common.Errors.AppErrorCode.COMMON_VALIDATION_FAILED, new { reason });
}
