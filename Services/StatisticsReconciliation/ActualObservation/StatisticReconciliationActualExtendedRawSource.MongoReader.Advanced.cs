using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualMongoExtendedRawSourceCollectReader
{
    private const int MaxAdvancedDayReports = 3_000;
    private const int MaxAdvancedGrainReports = 49_999;

    private async Task<
        StatisticReconciliationActualExtendedAdvancedSourceProof>
        CollectAdvancedAsync(
            StatisticReconciliationActualExtendedRawSourceCommand command,
            CancellationToken cancellationToken)
    {
        var capture = command.Advanced ?? throw Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .CaptureRequired);
        var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            command.SummaryPlan);
        var boundary = capture.Boundary;
        var configs = await context.WorkAssignmentAdvancedSummaryConfigs
            .Find(value =>
                value.Id == boundary.ConfigVersionId &&
                value.ConfigId == boundary.ConfigId &&
                value.WorkId == boundary.WorkId &&
                value.AssignmentId == boundary.AssignmentId &&
                value.DynamicFormTemplateId ==
                    boundary.DynamicFormTemplateId &&
                value.SectionId == boundary.SectionId &&
                value.Status ==
                    WorkAssignmentAdvancedSummaryConfigStatuses.Locked &&
                !value.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (configs.Count != 1)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSelectorUnavailable,
                StatisticReconciliationActualExtendedRawSourceFields
                    .AdvancedLockedConfig);
        var config = configs[0];
        WorkAssignmentAdvancedSummaryConfigPayload payload;
        try
        {
            using var configDocument =
                StatisticReconciliationActualJson.ParseStrict(
                    config.ConfigJson, "EXTENDED_ADVANCED_CONFIG_JSON");
            payload = StatConfigCanonicalJson.DeserializeStrict<
                WorkAssignmentAdvancedSummaryConfigPayload>(
                configDocument.RootElement);
        }
        catch (JsonException)
        {
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        }
        var configHash = StatConfigCanonicalJson.HashObject(new
        {
            payload,
            dependencyPins = config.DependencyPins
        });
        if (config.VersionNo != boundary.ConfigVersionNo ||
            config.Revision != boundary.ConfigRevision ||
            config.ConfigHash != boundary.ConfigSha256 ||
            configHash != boundary.ConfigSha256 ||
            !config.DependencyPins.SequenceEqual(
                boundary.DependencyPins, StringComparer.Ordinal))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        var schemaOptionBinding =
            await ReadAdvancedSchemaOptionBindingAsync(
                    boundary,
                    payload,
                    config.DependencyPins,
                    plan,
                    cancellationToken)
                .ConfigureAwait(false);

        var scopes = await context.WorkAssignments
            .Find(value =>
                value.Id == boundary.AssignmentId &&
                value.WorkId == boundary.WorkId &&
                value.DynamicFormTemplateId ==
                    boundary.DynamicFormTemplateId &&
                value.IsActive &&
                !value.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (scopes.Count != 1)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSelectorUnavailable);
        var scope = scopes[0];
        var normalizedScope = WorkAssignmentSummarySourceScope.Normalize(
            scope,
            config.SourceScopeMode,
            config.SourceFlowInstanceId,
            config.SourceFlowStepId,
            config.SourceFlowBranchId,
            config.SourceFlowEffectiveStatus);
        RequireAdvancedPayloadScope(payload, normalizedScope);
        var assignments = await WorkAssignmentSummarySourceScope
            .LoadAssignmentsAsync(
                context.WorkAssignments,
                scope,
                boundary.DynamicFormTemplateId,
                Array.Empty<string>(),
                normalizedScope,
                cancellationToken)
            .ConfigureAwait(false);
        if (assignments.Count > MaxSourceAssignments)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        var assignmentIds = assignments
            .Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        var assignmentManifest = AssignmentManifest(
            assignments,
            "P10_ACTUAL_EXTENDED_ADVANCED_ASSIGNMENT_MANIFEST_V2");
        var nodes = await ReadAdvancedNodesAsync(
                boundary, capture, cancellationToken)
            .ConfigureAwait(false);
        var allowedGrains = (payload.HierarchyGrains ??
            Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        if (nodes.Any(value => !allowedGrains.Contains(value.Grain)))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        typedCompiler.RequireAdvancedCoverage(
            plan, nodes.Select(value => (value.Grain, value.GrainKey)));

        var envelopes = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExtendedRawSourceEnvelope>();
        var grains = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExtendedAdvancedGrainProof>();
        foreach (var node in nodes)
        {
            var reports = await ReadAdvancedReportsAsync(
                    assignmentIds,
                    boundary.WorkId,
                    boundary.DynamicFormTemplateId,
                    node,
                    cancellationToken)
                .ConfigureAwait(false);
            var grainEnvelopes = ImmutableArray.CreateBuilder<
                StatisticReconciliationActualExtendedRawSourceEnvelope>(
                reports.Count);
            foreach (var report in reports)
            {
                var envelope = await ReadEnvelopeAsync(
                        "ADVANCED",
                        null,
                        node.Grain,
                        node.GrainKey,
                        envelopes.Count,
                        report,
                        null,
                        null,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
                envelopes.Add(envelope);
                grainEnvelopes.Add(envelope);
            }
            var exactEnvelopes = grainEnvelopes.MoveToImmutable();
            var envelopeManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_ENVELOPE_MANIFEST_V2",
                    exactEnvelopes.Select(value =>
                        value.EnvelopeSemanticSha256));
            var typed = typedCompiler.CompileAdvanced(
                plan,
                node.Grain,
                node.GrainKey,
                exactEnvelopes,
                schemaOptionBinding.DescriptorProjections,
                schemaOptionBinding.SchemaOptionBindingSha256);
            grains.Add(
                StatisticReconciliationActualExtendedRawSourceIntegrity
                    .AdvancedGrain(
                        node.Id,
                        node.Grain,
                        node.GrainKey,
                        node.WindowStartUtc,
                        node.WindowEndExclusiveUtc,
                        assignmentManifest,
                        assignments.Count,
                        envelopeManifest,
                        exactEnvelopes.Length,
                        typed));
        }
        return StatisticReconciliationActualExtendedRawSourceIntegrity.Advanced(
            config.ConfigId,
            config.Id,
            config.VersionNo,
            config.Revision,
            config.ConfigHash,
            plan.SemanticSha256,
            ScopeSha(normalizedScope),
            schemaOptionBinding.SchemaOptionBindingSha256,
            grains.MoveToImmutable(),
            envelopes.MoveToImmutable());
    }

    private async Task<ImmutableArray<AdvancedNodeIdentity>>
        ReadAdvancedNodesAsync(
            ActualAdvancedOwnerBoundary boundary,
            ActualAdvancedCapture capture,
            CancellationToken cancellationToken)
    {
        var days = await context.WorkAssignmentAdvancedSummaryDayNodes
            .Find(value => boundary.DayNodeIds.Contains(value.Id))
            .Limit(boundary.DayNodeIds.Length + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var months = await context.WorkAssignmentAdvancedSummaryMonthNodes
            .Find(value => boundary.MonthNodeIds.Contains(value.Id))
            .Limit(boundary.MonthNodeIds.Length + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var years = await context.WorkAssignmentAdvancedSummaryYearNodes
            .Find(value => boundary.YearNodeIds.Contains(value.Id))
            .Limit(boundary.YearNodeIds.Length + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (days.Count != boundary.DayNodeIds.Length ||
            months.Count != boundary.MonthNodeIds.Length ||
            years.Count != boundary.YearNodeIds.Length)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSelectorUnavailable,
                StatisticReconciliationActualExtendedRawSourceFields
                    .AdvancedGrainIdentity);
        var rows = days.Cast<
                WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .ToArray();
        if (rows.Any(value => !AdvancedBoundaryMatches(value, boundary)))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        var observed = capture.Nodes.ToDictionary(
            value => value.OwnerNodeId, StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<AdvancedNodeIdentity>(
            rows.Length);
        foreach (var value in rows.OrderBy(value =>
                     GrainOrdinal(value.Grain))
                 .ThenBy(value => value.GrainKey, StringComparer.Ordinal)
                 .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            if (!observed.TryGetValue(value.Id, out var item) ||
                !item.OwnerState.IsCleanResult ||
                item.Grain != value.Grain ||
                item.GrainKey != value.GrainKey ||
                item.WindowStartUtc != value.WindowStartUtc ||
                item.WindowEndExclusiveUtc != value.WindowEndExclusiveUtc ||
                item.SourceSignatureSha256 != value.SourceSignatureHash ||
                item.StoredValueSha256 != value.ValueHash ||
                item.SourceReportCount != value.SourceReportCount)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedSourceInvalid);
            var reportIds = (value.SourceReportIds ??
                    new List<string>())
                .ToImmutableArray();
            if (value.SourceReportCount < 0 ||
                value.SourceReportCount > MaxAdvancedGrainReports ||
                value.Grain == "DAY" &&
                    value.SourceReportCount != reportIds.Length ||
                value.Grain != "DAY" && reportIds.Length != 0 ||
                reportIds.Any(value => string.IsNullOrWhiteSpace(value) ||
                    value != value.Trim()) ||
                reportIds.Distinct(StringComparer.Ordinal).Count() !=
                    reportIds.Length ||
                !reportIds.SequenceEqual(
                    item.SourceReportIds, StringComparer.Ordinal))
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedSourceInvalid);
            result.Add(new(
                value.Id,
                value.Grain,
                value.GrainKey,
                value.WindowStartUtc,
                value.WindowEndExclusiveUtc,
                value.SourceReportCount,
                reportIds));
        }
        if (result.Count != capture.Nodes.Length)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        return result.MoveToImmutable();
    }

    private async Task<IReadOnlyList<WorkAssignmentReport>>
        ReadAdvancedReportsAsync(
            IReadOnlySet<string> assignmentIds,
            string workId,
            string templateId,
            AdvancedNodeIdentity node,
            CancellationToken cancellationToken)
    {
        if (assignmentIds.Count == 0)
        {
            if (node.SourceReportCount != 0)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedSourceInvalid);
            return Array.Empty<WorkAssignmentReport>();
        }
        var selected = new Dictionary<string, WorkAssignmentReport>(
            StringComparer.Ordinal);
        for (var start = node.WindowStartUtc.Date;
             start < node.WindowEndExclusiveUtc.Date;
             start = start.AddDays(1))
        {
            var end = start.AddDays(1);
            var dayKey = DayKey(start);
            var fb = Builders<WorkAssignmentReport>.Filter;
            var scheduled = fb.Or(
                fb.Eq(value => value.PeriodKind, null),
                fb.Eq(value => value.PeriodKind,
                    WorkReportPeriodKind.Scheduled));
            var coarse = fb.Or(
                fb.And(
                    fb.Gte(value => value.CompletedDate, start),
                    fb.Lt(value => value.CompletedDate, end)),
                fb.Eq(value => value.PeriodKey, dayKey),
                fb.And(
                    fb.Lt(value => value.PeriodStart, end),
                    fb.Gte(value => value.PeriodEnd, start)));
            var filter = fb.In(
                             value => value.WorkAssignmentId, assignmentIds)
                         & fb.Eq(value => value.DynamicFormTemplateId,
                             templateId)
                         & scheduled
                         & fb.Eq(value => value.Status,
                             WorkAssignmentReportStatus.Approved)
                         & fb.Eq(value => value.IsDeleted, false)
                         & fb.Eq(value => value.IsCurrent, true)
                         & fb.Ne(value => value.IsActive, false)
                         & fb.Ne(value => value.CumulativeContributionMode,
                             WorkReportCumulativeContributionMode.Exclude)
                         & coarse;
            var candidates = await context.WorkAssignmentReports
                .Find(filter)
                .Sort(Builders<WorkAssignmentReport>.Sort
                    .Ascending(value => value.WorkAssignmentId)
                    .Ascending(value => value.AssigneeUserId)
                    .Ascending(value => value.Id))
                .Limit(MaxAdvancedDayReports + 1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count > MaxAdvancedDayReports)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedSourceInvalid);
            foreach (var report in candidates)
            {
                if (!AdvancedSummaryReportSourceDayResolver.TryResolve(
                        report, out var resolved) || resolved != dayKey)
                    continue;
                if (!selected.TryAdd(report.Id, report))
                    throw Incomplete(
                        StatisticReconciliationActualExtendedRawSourceFailures
                            .AdvancedSourceInvalid);
            }
            if (selected.Count > MaxAdvancedGrainReports)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedSourceInvalid);
        }
        var ordered = ImmutableArray.CreateBuilder<
            (WorkAssignmentReport Report, string DayKey)>(selected.Count);
        foreach (var report in selected.Values)
        {
            if (!AdvancedSummaryReportSourceDayResolver.TryResolve(
                    report, out var sourceDayKey))
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedSourceInvalid);
            ordered.Add((report, sourceDayKey));
        }
        var reports = ordered
            .OrderBy(value => value.DayKey, StringComparer.Ordinal)
            .ThenBy(value => value.Report.WorkAssignmentId,
                StringComparer.Ordinal)
            .ThenBy(value => value.Report.AssigneeUserId,
                StringComparer.Ordinal)
            .ThenBy(value => value.Report.Id, StringComparer.Ordinal)
            .Select(value => value.Report)
            .ToArray();
        if (reports.LongLength != node.SourceReportCount ||
            reports.Any(value =>
                value.WorkId != workId ||
                !assignmentIds.Contains(value.WorkAssignmentId) ||
                value.DynamicFormTemplateId != templateId ||
                value.Status != WorkAssignmentReportStatus.Approved ||
                value.IsDeleted || !value.IsCurrent || !value.IsActive ||
                value.PayloadRevision <= 0 ||
                string.IsNullOrWhiteSpace(value.PayloadHash) ||
                value.CumulativeContributionMode ==
                    WorkReportCumulativeContributionMode.Exclude ||
                value.PeriodKind is not (
                    null or WorkReportPeriodKind.Scheduled) ||
                !InAdvancedWindow(value, node)) ||
            node.Grain == "DAY" &&
            !AdvancedDayReportIdsMatch(reports, node.SourceReportIds))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
        return reports;
    }

    private static bool AdvancedDayReportIdsMatch(
        IReadOnlyList<WorkAssignmentReport> reports,
        ImmutableArray<string> persistedIds)
        => !persistedIds.IsDefault &&
           persistedIds.SequenceEqual(
               persistedIds.OrderBy(value => value, StringComparer.Ordinal),
               StringComparer.Ordinal) &&
           reports.Select(value => value.Id)
               .OrderBy(value => value, StringComparer.Ordinal)
               .SequenceEqual(persistedIds, StringComparer.Ordinal);

    private static bool InAdvancedWindow(
        WorkAssignmentReport report,
        AdvancedNodeIdentity node)
        => AdvancedSummaryReportSourceDayResolver.TryResolve(
               report, out var dayKey) &&
           StringComparer.Ordinal.Compare(
               dayKey, DayKey(node.WindowStartUtc)) >= 0 &&
           StringComparer.Ordinal.Compare(
               dayKey, DayKey(node.WindowEndExclusiveUtc)) < 0;

    private static void RequireAdvancedPayloadScope(
        WorkAssignmentAdvancedSummaryConfigPayload payload,
        NormalizedSummarySourceScope scope)
    {
        var source = payload.SourceScope;
        if (source is null || source.Mode != scope.Mode ||
            source.FlowInstanceId != scope.FlowInstanceId ||
            source.FlowStepId != scope.FlowStepId ||
            source.FlowBranchId != scope.FlowBranchId ||
            source.FlowEffectiveStatus != scope.FlowEffectiveStatus)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSourceInvalid);
    }

    private static bool AdvancedBoundaryMatches(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase value,
        ActualAdvancedOwnerBoundary boundary)
        => !value.IsDeleted && !value.IsDirty &&
           value.Status ==
               WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean &&
           value.BuiltAtUtc?.Kind == DateTimeKind.Utc &&
           value.WorkId == boundary.WorkId &&
           value.AssignmentId == boundary.AssignmentId &&
           value.DynamicFormTemplateId ==
               boundary.DynamicFormTemplateId &&
           value.SectionId == boundary.SectionId &&
           value.ConfigId == boundary.ConfigId &&
           value.ConfigVersionId == boundary.ConfigVersionId &&
           value.ConfigVersionNo == boundary.ConfigVersionNo &&
           value.ConfigRevision == boundary.ConfigRevision &&
           value.ConfigHash == boundary.ConfigSha256 &&
           value.DependencyPins.SequenceEqual(
               boundary.DependencyPins, StringComparer.Ordinal) &&
           value.TimeAxis == boundary.TimeAxis &&
           value.CandidateChainId == boundary.CandidateChainId &&
           value.CandidatePromptId == boundary.CandidatePromptId &&
           value.CandidateStage == boundary.CandidateStage &&
           value.CandidateCatalogRawSha256 ==
               boundary.CandidateCatalogRawSha256 &&
           value.CandidateCatalogSemanticSha256 ==
               boundary.CandidateCatalogSemanticSha256 &&
           value.CandidateStageLockSha256 ==
               boundary.CandidateStageLockSha256 &&
           !string.IsNullOrWhiteSpace(value.SourceSignatureHash) &&
           !string.IsNullOrWhiteSpace(value.ValueHash) &&
           value.ValueHash ==
               StatisticReconciliationActualJson.RawSha256(
                   value.ValueJson ?? string.Empty);

    private static string DayKey(DateTime value)
        => value.ToString(
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);

    private static int GrainOrdinal(string grain) => grain switch
    {
        "DAY" => 0,
        "MONTH" => 1,
        "YEAR" => 2,
        _ => 3
    };

    private sealed record AdvancedNodeIdentity(
        string Id,
        string Grain,
        string GrainKey,
        DateTime WindowStartUtc,
        DateTime WindowEndExclusiveUtc,
        long SourceReportCount,
        ImmutableArray<string> SourceReportIds);
}
