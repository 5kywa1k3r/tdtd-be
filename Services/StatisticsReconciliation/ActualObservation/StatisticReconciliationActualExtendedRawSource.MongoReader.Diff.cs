using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualMongoExtendedRawSourceCollectReader
{
    private const int InvariantDiffScanLimit = 1_000;

    private async Task<StatisticReconciliationActualExtendedDiffSourceProof>
        CollectDiffAsync(
            StatisticReconciliationActualExtendedRawSourceCommand command,
            CancellationToken cancellationToken)
    {
        var capture = command.Diff ?? throw Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .CaptureRequired);
        if (!capture.OwnerState.IsUsableResult)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
        var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            command.SummaryPlan);
        var boundary = capture.Boundary;
        var configs = await context.WorkReportStatisticDiffConfigs
            .Find(value =>
                value.Id == boundary.ConfigVersionId &&
                value.ConfigId == boundary.ConfigId &&
                value.WorkId == boundary.WorkId &&
                value.AssignmentId == boundary.AssignmentId &&
                value.DynamicFormTemplateId ==
                    boundary.DynamicFormTemplateId &&
                value.Status == StatConfigStatuses.Locked &&
                !value.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (configs.Count != 1)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSelectorUnavailable,
                StatisticReconciliationActualExtendedRawSourceFields
                    .DiffLockedConfig);
        var config = configs[0];
        WorkReportStatisticDiffConfigPayload payload;
        try
        {
            using var document =
                StatisticReconciliationActualJson.ParseStrict(
                    config.ConfigJson, "EXTENDED_DIFF_CONFIG_JSON");
            payload = StatConfigCanonicalJson.DeserializeStrict<
                WorkReportStatisticDiffConfigPayload>(document.RootElement);
        }
        catch (JsonException)
        {
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
        }
        var configHash = WorkReportStatisticDiffService.P806ComputeConfigHash(
            WorkReportStatisticDiffService.P806OwnerId(
                boundary.AssignmentId,
                boundary.DynamicFormTemplateId),
            payload,
            config.DependencyPins);
        if (config.VersionNo != boundary.ConfigVersionNo ||
            config.Revision != boundary.ConfigRevision ||
            config.ConfigHash != boundary.ConfigSha256 ||
            configHash != boundary.ConfigSha256 ||
            !config.DependencyPins.SequenceEqual(
                boundary.DependencyPins, StringComparer.Ordinal))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
        RequireDiffCaptureMatchesConfig(capture, payload);
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
                    .DiffSelectorUnavailable);
        var scope = scopes[0];
        var left = await CollectDiffSideAsync(
                "LEFT",
                scope,
                payload.Left!,
                capture.SourcePins.Where(value => value.Side == "LEFT")
                    .ToImmutableArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var right = await CollectDiffSideAsync(
                "RIGHT",
                scope,
                payload.Right!,
                capture.SourcePins.Where(value => value.Side == "RIGHT")
                    .ToImmutableArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var allEnvelopes = left.Envelopes.Concat(right.Envelopes)
            .Select((value, index) => value with { SourceOrdinal = index })
            .Select(RehashEnvelope)
            .ToImmutableArray();
        var leftEnvelopes = allEnvelopes
            .Where(value => value.Side == "LEFT")
            .ToImmutableArray();
        var rightEnvelopes = allEnvelopes
            .Where(value => value.Side == "RIGHT")
            .ToImmutableArray();
        var typed = typedCompiler.CompileDiff(
            plan,
            capture.Direction,
            capture.LeftConceptKind,
            capture.LeftDataType,
            capture.LeftPeriodCanonicalJson,
            capture.RightConceptKind,
            capture.RightDataType,
            capture.RightPeriodCanonicalJson,
            left.ProjectionPins,
            leftEnvelopes,
            right.ProjectionPins,
            rightEnvelopes);
        var sides = ImmutableArray.Create(
            StatisticReconciliationActualExtendedRawSourceIntegrity.DiffSide(
                "LEFT",
                typed.LeftTransitionLeg,
                left.SelectorSemanticSha256,
                left.SourceScopeSemanticSha256,
                left.PeriodSemanticSha256,
                left.SourceAssignmentManifestSha256,
                left.SourceAssignmentCount,
                left.ProjectionPins,
                left.CapturedPins,
                leftEnvelopes,
                typed.DescriptorManifestSha256,
                typed.DescriptorCount,
                typed.LeftAtoms),
            StatisticReconciliationActualExtendedRawSourceIntegrity.DiffSide(
                "RIGHT",
                typed.RightTransitionLeg,
                right.SelectorSemanticSha256,
                right.SourceScopeSemanticSha256,
                right.PeriodSemanticSha256,
                right.SourceAssignmentManifestSha256,
                right.SourceAssignmentCount,
                right.ProjectionPins,
                right.CapturedPins,
                rightEnvelopes,
                typed.DescriptorManifestSha256,
                typed.DescriptorCount,
                typed.RightAtoms));
        return StatisticReconciliationActualExtendedRawSourceIntegrity.Diff(
            config.ConfigId!,
            config.Id,
            config.VersionNo,
            config.Revision,
            config.ConfigHash!,
            plan.SemanticSha256,
            capture.Direction,
            typed.PeriodBindingSha256,
            sides,
            allEnvelopes,
            typed.SourcePairs,
            typed.TransitionAtoms);
    }

    private async Task<DiffSideCollect> CollectDiffSideAsync(
        string side,
        WorkAssignment scope,
        WorkReportStatisticDiffSidePayload configured,
        ImmutableArray<ActualP9DiffSourcePinObservation> captured,
        CancellationToken cancellationToken)
    {
        var sourceScope = WorkAssignmentSummarySourceScope.Normalize(
            scope,
            configured.SourceScope!.Mode,
            configured.SourceScope.FlowInstanceId,
            configured.SourceScope.FlowStepId,
            configured.SourceScope.FlowBranchId,
            configured.SourceScope.FlowEffectiveStatus);
        var assignments = await WorkAssignmentSummarySourceScope
            .LoadAssignmentsAsync(
                context.WorkAssignments,
                scope,
                scope.DynamicFormTemplateId!,
                Array.Empty<string>(),
                sourceScope,
                cancellationToken)
            .ConfigureAwait(false);
        if (assignments.Count > MaxSourceAssignments)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
        var assignmentManifest = AssignmentManifest(
            assignments,
            "P10_ACTUAL_EXTENDED_DIFF_ASSIGNMENT_MANIFEST_V2");
        var selector = configured.Selector!;
        var documents = await ReadDiffProjectionDocumentsAsync(
                scope.WorkId,
                assignments.Select(value => value.Id).ToArray(),
                configured,
                cancellationToken)
            .ConfigureAwait(false);
        if (documents.Count > InvariantDiffScanLimit)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffScanLimitPreimageRequired,
                StatisticReconciliationActualExtendedRawSourceFields
                    .DiffNormalizedLimit);
        var projectionSeeds = documents.Select(value =>
                ProjectionSeed(side, value))
            .Distinct()
            .OrderBy(value => value.ReportId, StringComparer.Ordinal)
            .ThenBy(value => value.DirectRunId, StringComparer.Ordinal)
            .ThenBy(value => value.DirectGenerationId, StringComparer.Ordinal)
            .ToImmutableArray();
        RequireNoMixedLogicalPins(projectionSeeds);
        var capturedSeeds = captured.Select(value => new DiffPinSeed(
                side,
                value.SourceReportId,
                value.SourcePayloadRevision,
                value.SourcePayloadSha256,
                value.SourceLifecycleRevision,
                value.DirectRunId,
                value.DirectGenerationId))
            .Distinct()
            .OrderBy(value => value.ReportId, StringComparer.Ordinal)
            .ThenBy(value => value.DirectRunId, StringComparer.Ordinal)
            .ThenBy(value => value.DirectGenerationId, StringComparer.Ordinal)
            .ToImmutableArray();
        RequireNoMixedLogicalPins(capturedSeeds);
        var allSeeds = projectionSeeds.Concat(capturedSeeds)
            .Distinct()
            .ToImmutableArray();
        var jobs = await ReadDirectJobsAsync(allSeeds, cancellationToken)
            .ConfigureAwait(false);
        var projectionPins = BuildPins(projectionSeeds, jobs);
        var capturedPins = BuildPins(capturedSeeds, jobs);
        if (!projectionPins.SequenceEqual(capturedPins))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourcePinMismatch,
                StatisticReconciliationActualExtendedRawSourceFields
                    .DiffProjectionPin);
        var reports = await ReadPinnedReportsAsync(
                projectionPins,
                scope.WorkId,
                scope.DynamicFormTemplateId!,
                assignments.Select(value => value.Id)
                    .ToImmutableHashSet(StringComparer.Ordinal),
                cancellationToken)
            .ConfigureAwait(false);
        var envelopes = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExtendedRawSourceEnvelope>();
        foreach (var pin in projectionPins)
        {
            var report = reports[pin.SourceReportId];
            var envelope = await ReadEnvelopeAsync(
                    "DIFF",
                    side,
                    null,
                    null,
                    envelopes.Count,
                    report,
                    pin.DirectRunId,
                    pin.DirectGenerationId,
                    pin.DirectGenerationSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            envelopes.Add(envelope);
        }
        return new(
            SelectorSha(selector),
            ScopeSha(sourceScope),
            PeriodSha(configured.Period!),
            assignmentManifest,
            assignments.Count,
            projectionPins,
            capturedPins,
            envelopes.ToImmutable());
    }

    private async Task<IReadOnlyList<BsonDocument>>
        ReadDiffProjectionDocumentsAsync(
            string workId,
            IReadOnlyList<string> assignmentIds,
            WorkReportStatisticDiffSidePayload side,
            CancellationToken cancellationToken)
    {
        if (assignmentIds.Count == 0)
            return Array.Empty<BsonDocument>();
        var selector = side.Selector!;
        var collectionName = selector.ConceptKind switch
        {
            "FIELD" => "work_report_field_stat_values",
            "TABLE_METRIC" => "work_report_table_stat_values",
            "ROW_LABEL" => "work_report_label_stat_values",
            _ => throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSelectorUnavailable)
        };
        var filter = new BsonDocument
        {
            ["workId"] = ObjectId.Parse(workId),
            ["workAssignmentId"] = new BsonDocument(
                "$in",
                new BsonArray(assignmentIds.Select(ObjectId.Parse))),
            ["assignmentIsActive"] = true,
            ["reportIsActive"] = true,
            ["reportStatus"] = (int)WorkAssignmentReportStatus.Approved,
            ["isDeleted"] = false,
            ["directProjection"] = new BsonDocument("$type", "object")
        };
        ApplyDiffPeriodFilter(filter, side.Period!);
        ApplyDiffSelectorFilter(filter, selector);
        return await context.Db.GetCollection<BsonDocument>(collectionName)
            .Find(filter)
            .Sort(new BsonDocument
            {
                ["rowKey"] = 1,
                ["sourceKey"] = 1,
                ["workAssignmentReportId"] = 1,
                ["_id"] = 1
            })
            .Limit(InvariantDiffScanLimit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<string, WorkReportStatisticRebuildJob>>
        ReadDirectJobsAsync(
            ImmutableArray<DiffPinSeed> seeds,
            CancellationToken cancellationToken)
    {
        var ids = seeds.Select(value => value.DirectRunId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, WorkReportStatisticRebuildJob>(
                StringComparer.Ordinal);
        var rows = await context.WorkReportStatisticRebuildJobs
            .Find(value => ids.Contains(value.Id) && !value.IsDeleted)
            .Limit(ids.Length + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count != ids.Length)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
        var result = rows.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            var job = result[seed.DirectRunId];
            if (job.Status != WorkReportStatisticRebuildJobStatuses.Completed ||
                !job.IsCurrentPublication ||
                job.GenerationId != seed.DirectGenerationId ||
                string.IsNullOrWhiteSpace(job.GenerationHash) ||
                job.SourceReportId != seed.ReportId ||
                job.SourcePayloadRevision != seed.PayloadRevision ||
                job.SourcePayloadHash != seed.PayloadSha256 ||
                job.SourceLifecycleRevision != seed.LifecycleRevision)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .DiffSourceInvalid);
        }
        return result;
    }

    private async Task<Dictionary<string, WorkAssignmentReport>>
        ReadPinnedReportsAsync(
            ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                pins,
            string workId,
            string templateId,
            ImmutableHashSet<string> assignmentIds,
            CancellationToken cancellationToken)
    {
        var ids = pins.Select(value => value.SourceReportId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, WorkAssignmentReport>(
                StringComparer.Ordinal);
        var reports = await context.WorkAssignmentReports
            .Find(value => ids.Contains(value.Id))
            .Limit(ids.Length + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reports.Count != ids.Length)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
        var result = reports.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            var report = result[pin.SourceReportId];
            if (report.WorkId != workId ||
                !assignmentIds.Contains(report.WorkAssignmentId) ||
                report.DynamicFormTemplateId != templateId ||
                report.Status != WorkAssignmentReportStatus.Approved ||
                !report.IsCurrent || !report.IsActive || report.IsDeleted ||
                report.CumulativeContributionMode ==
                    WorkReportCumulativeContributionMode.Exclude ||
                report.PayloadRevision <= 0 ||
                string.IsNullOrWhiteSpace(report.PayloadHash) ||
                report.PayloadRevision != pin.SourcePayloadRevision ||
                report.PayloadHash != pin.SourcePayloadSha256 ||
                report.LifecycleRevision != pin.SourceLifecycleRevision)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .DiffSourceInvalid);
        }
        return result;
    }

    private static ImmutableArray<
        StatisticReconciliationActualExtendedDiffSourcePin> BuildPins(
            ImmutableArray<DiffPinSeed> seeds,
            IReadOnlyDictionary<string, WorkReportStatisticRebuildJob> jobs)
        => seeds.Select(value =>
                StatisticReconciliationActualExtendedRawSourceIntegrity.Pin(
                    value.Side,
                    value.ReportId,
                    value.PayloadRevision,
                    value.PayloadSha256,
                    value.LifecycleRevision,
                    value.DirectRunId,
                    value.DirectGenerationId,
                    jobs[value.DirectRunId].GenerationHash!))
            .ToImmutableArray();

    private static DiffPinSeed ProjectionSeed(string side, BsonDocument row)
    {
        var pin = row.GetValue("directProjection", BsonNull.Value);
        if (!pin.IsBsonDocument)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffProjectionPin);
        var value = pin.AsBsonDocument;
        return new(
            side,
            value["sourceReportId"].AsObjectId.ToString(),
            value["sourcePayloadRevision"].ToInt32(),
            value["sourcePayloadHash"].AsString,
            value["sourceLifecycleRevision"].ToInt32(),
            value["runId"].AsObjectId.ToString(),
            value["generationId"].AsString);
    }

    private static void RequireNoMixedLogicalPins(
        ImmutableArray<DiffPinSeed> pins)
    {
        if (pins.GroupBy(
                value => $"{value.Side}\0{value.ReportId}\0{value.DirectRunId}\0{value.DirectGenerationId}",
                StringComparer.Ordinal)
            .Any(value => value.Count() != 1))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
    }

    private static void RequireDiffCaptureMatchesConfig(
        ActualP9DiffCapture capture,
        WorkReportStatisticDiffConfigPayload payload)
    {
        if (payload.Left?.Selector is not { } left ||
            payload.Right?.Selector is not { } right ||
            payload.Left.Period is null || payload.Right.Period is null ||
            payload.Left.SourceScope is null || payload.Right.SourceScope is null ||
            capture.LeftConceptKind != left.ConceptKind ||
            capture.LeftConceptKey != left.ConceptKey ||
            capture.LeftConceptCode != left.ConceptCode ||
            capture.LeftDataType != left.DataType ||
            capture.RightConceptKind != right.ConceptKind ||
            capture.RightConceptKey != right.ConceptKey ||
            capture.RightConceptCode != right.ConceptCode ||
            capture.RightDataType != right.DataType ||
            capture.Direction != payload.Direction ||
            capture.MissingPolicy != payload.MissingPolicy ||
            capture.EmptyPolicy != payload.EmptyPolicy ||
            !PeriodEquals(capture.LeftPeriodCanonicalJson, payload.Left.Period) ||
            !PeriodEquals(capture.RightPeriodCanonicalJson,
                payload.Right.Period))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSourceInvalid);
    }

    private static bool PeriodEquals(
        string canonical,
        WorkReportStatisticDiffPeriodPayload configured)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            canonical, "EXTENDED_DIFF_CAPTURE_PERIOD_JSON");
        var observed = StatConfigCanonicalJson.DeserializeStrict<
            WorkReportStatisticDiffPeriodPayload>(document.RootElement);
        return observed == configured;
    }

    private static void ApplyDiffPeriodFilter(
        BsonDocument filter,
        WorkReportStatisticDiffPeriodPayload period)
    {
        if (period.Mode == "EXACT")
        {
            filter["periodKey"] = period.PeriodKey!;
            return;
        }
        if (period.Mode != "RANGE")
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSelectorUnavailable);
        filter["periodKey"] = new BsonDocument
        {
            ["$gte"] = period.PeriodKeyFrom!,
            ["$lte"] = period.PeriodKeyTo!
        };
    }

    private static void ApplyDiffSelectorFilter(
        BsonDocument filter,
        WorkReportStatisticDiffSelectorPayload selector)
    {
        if (selector.ConceptKind == "FIELD")
        {
            filter["conceptCode"] = selector.ConceptCode!;
            filter["fieldId"] = selector.ConceptKey!;
            return;
        }
        var separator = selector.ConceptKey!.IndexOf(':');
        if (separator <= 0 || separator == selector.ConceptKey.Length - 1)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSelectorUnavailable);
        filter["blockId"] = selector.ConceptKey[..separator];
        var tail = selector.ConceptKey[(separator + 1)..];
        if (selector.ConceptKind == "TABLE_METRIC")
        {
            filter["conceptCode"] = selector.ConceptCode!;
            filter["metricKey"] = tail;
        }
        else if (selector.ConceptKind == "ROW_LABEL")
            filter["labelCode"] = tail;
        else
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .DiffSelectorUnavailable);
    }

    private static string SelectorSha(
        WorkReportStatisticDiffSelectorPayload value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_SELECTOR_V2",
            value.ConceptKind,
            value.ConceptKey,
            value.ConceptCode,
            value.DataType);

    private static string PeriodSha(WorkReportStatisticDiffPeriodPayload value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_PERIOD_V2",
            value.Mode,
            value.PeriodKey,
            value.PeriodKeyFrom,
            value.PeriodKeyTo);

    private static StatisticReconciliationActualExtendedRawSourceEnvelope
        RehashEnvelope(
            StatisticReconciliationActualExtendedRawSourceEnvelope value)
        => StatisticReconciliationActualExtendedRawSourceIntegrity.Envelope(
            value.Family,
            value.Side,
            value.Grain,
            value.GrainKey,
            value.SourceOrdinal,
            value.WorkId,
            value.WorkAssignmentId,
            value.ReportId,
            value.PayloadDocumentId,
            value.PayloadRevision,
            value.PayloadOwnerSha256,
            value.CanonicalPayloadJson,
            value.LifecycleRevision,
            value.LifecycleSha256,
            value.DirectRunId,
            value.DirectGenerationId,
            value.DirectGenerationSha256);

    private sealed record DiffPinSeed(
        string Side,
        string ReportId,
        int PayloadRevision,
        string PayloadSha256,
        int LifecycleRevision,
        string DirectRunId,
        string DirectGenerationId);

    private sealed record DiffSideCollect(
        string SelectorSemanticSha256,
        string SourceScopeSemanticSha256,
        string PeriodSemanticSha256,
        string SourceAssignmentManifestSha256,
        int SourceAssignmentCount,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
            ProjectionPins,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
            CapturedPins,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            Envelopes);
}
