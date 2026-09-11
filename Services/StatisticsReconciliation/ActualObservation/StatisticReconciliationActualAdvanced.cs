using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using tdtd_be.Models;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualAdvancedStores
{
    internal const string Day = "work_assignment_advanced_summary_day_nodes";
    internal const string Month = "work_assignment_advanced_summary_month_nodes";
    internal const string Year = "work_assignment_advanced_summary_year_nodes";
}

internal sealed class StatisticReconciliationActualAdvancedAdapter
{
    internal const int MaxNodes = 100_000;
    internal const int MaxFieldsPerNode = 10_000;
    private const string TimeAxis = "UTC_GREGORIAN";
    private const string CandidatePrompt = "P9-05";
    private const int CandidateStage = 3;
    private const string CandidateCatalogRaw =
        "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3";
    private const string CandidateCatalogSemantic =
        "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f";
    private const string CandidateStageLock =
        "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc";

    internal async Task<ActualAdvancedCapture> CaptureAsync(
        ActualAdvancedOwnerBoundary boundary,
        IStatisticReconciliationActualAdvancedOwnerReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(reader);
        var normalized = NormalizeBoundary(boundary);
        var days = await reader.ReadDayNodesAsync(normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw Fail("ADVANCED_DAY_ROWS_NULL");
        var months = await reader.ReadMonthNodesAsync(normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw Fail("ADVANCED_MONTH_ROWS_NULL");
        var years = await reader.ReadYearNodesAsync(normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw Fail("ADVANCED_YEAR_ROWS_NULL");
        var total = checked(days.Count + months.Count + years.Count);
        if (total > MaxNodes)
            throw Fail("ADVANCED_NODES_LIMIT");
        RequireExactIds(days.Select(x => x.Id), normalized.DayNodeIds, "ADVANCED_DAY_OWNER_SET");
        RequireExactIds(months.Select(x => x.Id), normalized.MonthNodeIds, "ADVANCED_MONTH_OWNER_SET");
        RequireExactIds(years.Select(x => x.Id), normalized.YearNodeIds, "ADVANCED_YEAR_OWNER_SET");

        var observations = days.Select(node => Adapt(
                normalized, StatisticReconciliationActualAdvancedStores.Day,
                WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
                node.DayKey, node.DayKey,
                MonthFromDay(node.DayKey), YearFromDay(node.DayKey), node))
            .Concat(months.Select(node => Adapt(
                normalized, StatisticReconciliationActualAdvancedStores.Month,
                WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
                node.MonthKey, null, node.MonthKey, node.YearKey, node)))
            .Concat(years.Select(node => Adapt(
                normalized, StatisticReconciliationActualAdvancedStores.Year,
                WorkAssignmentAdvancedSummaryHierarchyGrains.Year,
                node.YearKey, null, null, node.YearKey, node)))
            .OrderBy(x => x.WindowStartUtc)
            .ThenBy(x => GrainRank(x.Grain))
            .ThenBy(x => x.GrainKey, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerNodeId, StringComparer.Ordinal)
            .ToImmutableArray();

        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_ADVANCED_CAPTURE_V1",
            BoundarySemantic(normalized),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_NODES_V1",
                observations.Select(x => x.SemanticSha256)));
        return new ActualAdvancedCapture(
            normalized, observations, total, captureSha);
    }

    private static ActualAdvancedOwnerBoundary NormalizeBoundary(
        ActualAdvancedOwnerBoundary value)
    {
        if (value.ConfigVersionNo < 1 || value.ConfigRevision < 1)
            throw Fail("ADVANCED_BOUNDARY_REVISION_INVALID");
        if (value.CandidateStage != CandidateStage
            || !StringComparer.Ordinal.Equals(value.CandidatePromptId, CandidatePrompt)
            || !StringComparer.Ordinal.Equals(value.CandidateCatalogRawSha256, CandidateCatalogRaw)
            || !StringComparer.Ordinal.Equals(value.CandidateCatalogSemanticSha256, CandidateCatalogSemantic)
            || !StringComparer.Ordinal.Equals(value.CandidateStageLockSha256, CandidateStageLock))
            throw Fail("ADVANCED_FROZEN_CANDIDATE_MISMATCH");
        var days = SelectorIds(value.DayNodeIds, "ADVANCED_DAY_NODE_ID");
        var months = SelectorIds(value.MonthNodeIds, "ADVANCED_MONTH_NODE_ID");
        var years = SelectorIds(value.YearNodeIds, "ADVANCED_YEAR_NODE_ID");
        if (checked(days.Length + months.Length + years.Length) > MaxNodes)
            throw Fail("ADVANCED_PINNED_NODES_LIMIT");
        return value with
        {
            WorkId = Required(value.WorkId, "ADVANCED_WORK_ID"),
            AssignmentId = Required(value.AssignmentId, "ADVANCED_ASSIGNMENT_ID"),
            DynamicFormTemplateId = Required(value.DynamicFormTemplateId, "ADVANCED_FORM_TEMPLATE_ID"),
            SectionId = Required(value.SectionId, "ADVANCED_SECTION_ID"),
            ConfigId = Required(value.ConfigId, "ADVANCED_CONFIG_ID"),
            ConfigVersionId = Required(value.ConfigVersionId, "ADVANCED_CONFIG_VERSION_ID"),
            ConfigSha256 = Sha(value.ConfigSha256, "ADVANCED_CONFIG_SHA256"),
            DependencyPins = Pins(value.DependencyPins, "ADVANCED_DEPENDENCY_PIN"),
            TimeAxis = RequireExact(value.TimeAxis, TimeAxis, "ADVANCED_TIME_AXIS"),
            CandidateChainId = Required(value.CandidateChainId, "ADVANCED_CANDIDATE_CHAIN_ID"),
            CandidatePromptId = CandidatePrompt,
            CandidateCatalogRawSha256 = CandidateCatalogRaw,
            CandidateCatalogSemanticSha256 = CandidateCatalogSemantic,
            CandidateStageLockSha256 = CandidateStageLock,
            DayNodeIds = days,
            MonthNodeIds = months,
            YearNodeIds = years
        };
    }

    private static ActualAdvancedNodeObservation Adapt(
        ActualAdvancedOwnerBoundary boundary,
        string store,
        string grain,
        string grainKey,
        string? dayKey,
        string? monthKey,
        string? yearKey,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node)
    {
        ArgumentNullException.ThrowIfNull(node);
        RequireNodeBoundary(boundary, node, grain, grainKey);
        var sourceIds = Snapshot(node.SourceReportIds, "ADVANCED_SOURCE_REPORT_ID");
        var inputKeys = Snapshot(node.InputNodeKeys, "ADVANCED_INPUT_NODE_KEY");
        var observedValueSha = StatisticReconciliationActualJson.RawSha256(node.ValueJson ?? string.Empty);
        var parsed = TryReadValue(node.ValueJson, grain);
        var canonicalValueSha = parsed.Document is null
            ? StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_ADVANCED_INVALID_VALUE_JSON_V1", node.ValueJson)
            : StatisticReconciliationActualJson.CanonicalSha256(parsed.Document.RootElement);
        var innerMatches = parsed.Value is not null
            && InnerMatches(parsed.Value, boundary, node, grainKey, dayKey, monthKey, yearKey);
        var status = ExactUpper(
            node.Status, "ADVANCED_NODE_STATUS");
        var lifecycle = BuildLifecycleSemantic(node, status);
        var coverage = GrainCoverage(
            grain, grainKey, yearKey, sourceIds, inputKeys, node.SourceReportCount);
        var sourceOrderCanonical = Canonical(sourceIds);
        var inputOrderCanonical = Canonical(inputKeys);
        var sourceSignatureValid = IsSha256(node.SourceSignatureHash);
        var typedValueShapeValid = parsed.Value is not null
                                   && TypedValueShape(parsed.Value, node.SourceReportCount);
        var cleanLifecycle = node.BuiltAtUtc?.Kind == DateTimeKind.Utc
                             && node.DirtyReason is null && node.BuildError is null
                             && node.LeaseOwner is null
                             && !node.LeaseExpiresAtUtc.HasValue;
        var state = new ActualAdvancedNodeState(
            status,
            node.IsDirty,
            node.DirtyReason,
            node.IsDeleted,
            !node.IsDeleted && !node.IsDirty
                && status == WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean
                && cleanLifecycle && parsed.Value is not null
                && StringComparer.Ordinal.Equals(node.ValueHash, observedValueSha)
                && innerMatches && coverage && sourceOrderCanonical
                && inputOrderCanonical && sourceSignatureValid && typedValueShapeValid,
            parsed.Value is not null,
            !string.IsNullOrWhiteSpace(node.ValueHash)
                && StringComparer.Ordinal.Equals(node.ValueHash, observedValueSha),
            sourceSignatureValid,
            typedValueShapeValid,
            innerMatches,
            sourceOrderCanonical,
            inputOrderCanonical,
            coverage,
            cleanLifecycle,
            lifecycle);

        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_ADVANCED_NODE_OBSERVATION_V1",
            store,
            node.Id,
            grain,
            grainKey,
            dayKey,
            monthKey,
            yearKey,
            StatisticReconciliationActualCanonical.Instant(node.WindowStartUtc),
            StatisticReconciliationActualCanonical.Instant(node.WindowEndExclusiveUtc),
            node.SourceSignatureHash,
            StatisticReconciliationActualCanonical.Integer(node.SourceReportCount),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_SOURCE_REPORT_ORDER_V1", sourceIds),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_INPUT_NODE_ORDER_V1", inputKeys),
            node.ValueHash,
            observedValueSha,
            canonicalValueSha,
            parsed.Value?.SemanticSha256,
            lifecycle,
            StatisticReconciliationActualCanonical.Boolean(state.IsCleanResult),
            StatisticReconciliationActualCanonical.Boolean(state.ValueHashMatches),
            StatisticReconciliationActualCanonical.Boolean(state.SourceSignatureValid),
            StatisticReconciliationActualCanonical.Boolean(state.TypedValueShapeValid),
            StatisticReconciliationActualCanonical.Boolean(state.InnerValueMatchesOuter),
            StatisticReconciliationActualCanonical.Boolean(state.CleanLifecycleValid));
        parsed.Document?.Dispose();
        return new ActualAdvancedNodeObservation(
            store,
            node.Id,
            grain,
            grainKey,
            dayKey,
            monthKey,
            yearKey,
            node.WindowStartUtc,
            node.WindowEndExclusiveUtc,
            node.SourceSignatureHash,
            node.SourceReportCount,
            sourceIds,
            inputKeys,
            node.ValueJson ?? string.Empty,
            node.ValueHash,
            observedValueSha,
            canonicalValueSha,
            node.BuiltAtUtc,
            parsed.Value,
            state,
            semantic);
    }

    private static ParsedAdvancedValue TryReadValue(string? json, string grain)
    {
        JsonDocument? document = null;
        try
        {
            document = StatisticReconciliationActualJson.ParseStrict(
                json, "ADVANCED_VALUE_JSON");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ParsedAdvancedValue(document, null);
            var expectedKind = grain switch
            {
                WorkAssignmentAdvancedSummaryHierarchyGrains.Day => "ADVANCED_SUMMARY_DAY_NODE_V1",
                WorkAssignmentAdvancedSummaryHierarchyGrains.Month => "ADVANCED_SUMMARY_MONTH_NODE_V1",
                WorkAssignmentAdvancedSummaryHierarchyGrains.Year => "ADVANCED_SUMMARY_YEAR_NODE_V1",
                _ => throw Fail("ADVANCED_GRAIN_INVALID")
            };
            if (Text(root, "kind") != expectedKind)
                return new ParsedAdvancedValue(document, null);
            var fieldsElement = Property(root, "fields", JsonValueKind.Array);
            var fieldBuilder = ImmutableArray.CreateBuilder<ActualAdvancedFieldObservation>();
            var ordinal = 0;
            foreach (var field in fieldsElement.EnumerateArray())
            {
                if (fieldBuilder.Count >= MaxFieldsPerNode || field.ValueKind != JsonValueKind.Object)
                    return new ParsedAdvancedValue(document, null);
                var result = field.TryGetProperty("result", out var resultElement)
                    ? StatisticReconciliationActualJson.Canonicalize(resultElement)
                    : "null";
                var samples = Strings(Property(field, "sampleValues", JsonValueKind.Array),
                    "ADVANCED_SAMPLE_VALUE");
                var fieldId = Required(Text(field, "fieldId"), "ADVANCED_VALUE_FIELD_ID");
                var fieldKey = Required(Text(field, "fieldKey"), "ADVANCED_VALUE_FIELD_KEY");
                var label = Required(Text(field, "label"), "ADVANCED_VALUE_FIELD_LABEL");
                var dataType = Required(Text(field, "dataType"), "ADVANCED_VALUE_FIELD_DATA_TYPE");
                var method = Required(Text(field, "method"), "ADVANCED_VALUE_FIELD_METHOD");
                var valueCount = Integer(field, "valueCount");
                var sourceReportCount = Integer(field, "sourceReportCount");
                var fieldSha = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_ADVANCED_FIELD_V1",
                    StatisticReconciliationActualCanonical.Integer(ordinal),
                    fieldId, fieldKey, label, dataType, method,
                    StatisticReconciliationActualCanonical.Integer(valueCount),
                    StatisticReconciliationActualCanonical.Integer(sourceReportCount),
                    StatisticReconciliationActualJson.RawSha256(result),
                    StatisticReconciliationActualCanonical.HashSequence(
                        "P10_ACTUAL_ADVANCED_FIELD_SAMPLES_V1", samples));
                fieldBuilder.Add(new ActualAdvancedFieldObservation(
                    ordinal++, fieldId, fieldKey, label, dataType, method,
                    valueCount, sourceReportCount, result, samples, fieldSha));
            }
            var fields = fieldBuilder.ToImmutable();
            var warnings = Strings(Property(root, "warnings", JsonValueKind.Array),
                "ADVANCED_WARNING");
            var generated = Utc(root, "generatedAtUtc");
            var start = Utc(root, "windowStartUtc");
            var end = Utc(root, "windowEndExclusiveUtc");
            var value = new ActualAdvancedValueObservation(
                checked((int)Integer(root, "schemaVersion")),
                expectedKind,
                generated,
                Required(Text(root, "configId"), "ADVANCED_VALUE_CONFIG_ID"),
                OwnerSha(Text(root, "configHash"), "ADVANCED_VALUE_CONFIG_SHA256"),
                Required(Text(root, "grain"), "ADVANCED_VALUE_GRAIN"),
                Required(Text(root, "grainKey"), "ADVANCED_VALUE_GRAIN_KEY"),
                Optional(Text(root, "dayKey"), "ADVANCED_VALUE_DAY_KEY"),
                Optional(Text(root, "monthKey"), "ADVANCED_VALUE_MONTH_KEY"),
                Optional(Text(root, "yearKey"), "ADVANCED_VALUE_YEAR_KEY"),
                start,
                end,
                Required(Text(root, "sourceScopeMode"), "ADVANCED_VALUE_SOURCE_SCOPE_MODE"),
                Optional(Text(root, "sourceFlowInstanceId"), "ADVANCED_VALUE_FLOW_INSTANCE_ID"),
                Optional(Text(root, "sourceFlowStepId"), "ADVANCED_VALUE_FLOW_STEP_ID"),
                Optional(Text(root, "sourceFlowBranchId"), "ADVANCED_VALUE_FLOW_BRANCH_ID"),
                Optional(Text(root, "sourceFlowEffectiveStatus"), "ADVANCED_VALUE_FLOW_EFFECTIVE_STATUS"),
                Integer(root, "sourceAssignmentCount"),
                Integer(root, "sourceReportCount"),
                Integer(root, "sectionReportCount"),
                Integer(root, "sectionFieldCount"),
                Integer(root, "targetFieldCount"),
                Integer(root, "inputNodeCount"),
                warnings,
                fields,
                string.Empty);
            value = value with
            {
                SemanticSha256 = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_ADVANCED_TYPED_VALUE_V1",
                    StatisticReconciliationActualJson.CanonicalSha256(root),
                    StatisticReconciliationActualCanonical.HashSequence(
                        "P10_ACTUAL_ADVANCED_TYPED_FIELDS_V1",
                        fields.Select(x => x.SemanticSha256)))
            };
            return new ParsedAdvancedValue(document, value);
        }
        catch (Exception exception) when (exception is
                   JsonException or StatisticReconciliationActualObservationException
                   or OverflowException)
        {
            document?.Dispose();
            return new ParsedAdvancedValue(null, null);
        }
    }

    private static bool InnerMatches(
        ActualAdvancedValueObservation value,
        ActualAdvancedOwnerBoundary boundary,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        string grainKey,
        string? dayKey,
        string? monthKey,
        string? yearKey)
        => value.SchemaVersion == 1
           && value.InnerConfigId == (node.Grain ==
               WorkAssignmentAdvancedSummaryHierarchyGrains.Day
                   ? boundary.ConfigVersionId
                   : boundary.ConfigId)
           && value.ConfigSha256 == boundary.ConfigSha256
           && value.Grain == node.Grain
           && value.GrainKey == grainKey
           && value.DayKey == dayKey
           && value.MonthKey == monthKey
           && value.YearKey == yearKey
           && value.WindowStartUtc == node.WindowStartUtc
           && value.WindowEndExclusiveUtc == node.WindowEndExclusiveUtc
           && value.SourceReportCount == node.SourceReportCount
           && value.InputNodeCount == node.InputNodeKeys.Count;

    private static void RequireNodeBoundary(
        ActualAdvancedOwnerBoundary boundary,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        string grain,
        string grainKey)
    {
        if (!Eq(node.WorkId, boundary.WorkId)
            || !Eq(node.AssignmentId, boundary.AssignmentId)
            || !Eq(node.DynamicFormTemplateId, boundary.DynamicFormTemplateId)
            || !Eq(node.SectionId, boundary.SectionId)
            || !Eq(node.ConfigId, boundary.ConfigId)
            || !Eq(node.ConfigVersionId, boundary.ConfigVersionId)
            || node.ConfigVersionNo != boundary.ConfigVersionNo
            || node.ConfigRevision != boundary.ConfigRevision
            || !Eq(node.ConfigHash, boundary.ConfigSha256)
            || !node.DependencyPins.SequenceEqual(boundary.DependencyPins, StringComparer.Ordinal)
            || !Eq(node.TimeAxis, boundary.TimeAxis)
            || !Eq(node.CandidateChainId, boundary.CandidateChainId)
            || !Eq(node.CandidatePromptId, boundary.CandidatePromptId)
            || node.CandidateStage != boundary.CandidateStage
            || !Eq(node.CandidateCatalogRawSha256, boundary.CandidateCatalogRawSha256)
            || !Eq(node.CandidateCatalogSemanticSha256, boundary.CandidateCatalogSemanticSha256)
            || !Eq(node.CandidateStageLockSha256, boundary.CandidateStageLockSha256)
            || !Eq(node.Grain, grain) || !Eq(node.GrainKey, grainKey)
            || node.WindowStartUtc.Kind != DateTimeKind.Utc
            || node.WindowEndExclusiveUtc.Kind != DateTimeKind.Utc
            || !ExactWindow(grain, grainKey, node.WindowStartUtc, node.WindowEndExclusiveUtc))
            throw Fail("ADVANCED_OWNER_BOUNDARY_MISMATCH");
    }

    private static string BuildLifecycleSemantic(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        string status)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_ADVANCED_BUILD_LIFECYCLE_V1",
            status,
            StatisticReconciliationActualCanonical.Boolean(node.IsDirty),
            node.DirtyReason,
            StatisticReconciliationActualCanonical.Boolean(node.IsDeleted),
            Instant(node.BuiltAtUtc),
            node.BuildJobId,
            node.BuildCorrelationId,
            node.BuildCommandId,
            node.BuildRequestHash,
            node.BuildReceiptId,
            node.QuotaLedgerId,
            StatisticReconciliationActualCanonical.Integer(node.BuildAttemptNo),
            node.LeaseOwner,
            Instant(node.LeaseExpiresAtUtc),
            StatisticReconciliationActualCanonical.Integer(node.FenceToken),
            node.BuildError,
            StatisticReconciliationActualCanonical.Instant(node.CreatedAtUtc),
            StatisticReconciliationActualCanonical.Instant(node.UpdatedAtUtc),
            node.CreatedByUserId,
            node.UpdatedByUserId);

    private static bool GrainCoverage(
        string grain,
        string grainKey,
        string? yearKey,
        ImmutableArray<string> sources,
        ImmutableArray<string> inputs,
        long sourceCount)
    {
        if (grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Day)
            return inputs.Length == 0 && sourceCount == sources.Length
                   && TryDay(grainKey, out _);
        if (sources.Length != 0)
            return false;
        if (grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Month)
        {
            if (!TryMonth(grainKey, out var month))
                return false;
            if (!StringComparer.Ordinal.Equals(
                    yearKey, month.Year.ToString("0000", CultureInfo.InvariantCulture)))
                return false;
            var expected = Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month))
                .Select(offset => month.AddDays(offset)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return inputs.SequenceEqual(expected, StringComparer.Ordinal);
        }
        if (grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Year)
            return inputs.SequenceEqual(Enumerable.Range(1, 12)
                .Select(month => $"{grainKey}-{month:00}"), StringComparer.Ordinal);
        return false;
    }

    private static bool ExactWindow(
        string grain,
        string grainKey,
        DateTime start,
        DateTime end)
    {
        DateTime expectedStart;
        DateTime expectedEnd;
        if (grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Day
            && TryDay(grainKey, out var day))
        {
            expectedStart = day;
            expectedEnd = day.AddDays(1);
        }
        else if (grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Month
                 && TryMonth(grainKey, out var month))
        {
            expectedStart = month;
            expectedEnd = month.AddMonths(1);
        }
        else if (grain == WorkAssignmentAdvancedSummaryHierarchyGrains.Year
                 && DateTime.TryParseExact(grainKey, "yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var year))
        {
            expectedStart = DateTime.SpecifyKind(new DateTime(year.Year, 1, 1), DateTimeKind.Utc);
            expectedEnd = expectedStart.AddYears(1);
        }
        else return false;
        return start == expectedStart && end == expectedEnd;
    }

    private static bool TryDay(string value, out DateTime day)
    {
        var valid = DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);
        day = valid ? DateTime.SpecifyKind(day, DateTimeKind.Utc) : default;
        return valid;
    }

    private static bool TryMonth(string value, out DateTime month)
    {
        var valid = DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out month);
        month = valid ? DateTime.SpecifyKind(month, DateTimeKind.Utc) : default;
        return valid;
    }

    private static bool TypedValueShape(
        ActualAdvancedValueObservation value,
        long nodeSourceReportCount)
        => value.SourceAssignmentCount >= 0 && value.SourceReportCount >= 0
           && value.SectionReportCount >= 0 && value.SectionFieldCount >= 0
           && value.TargetFieldCount == value.Fields.Length && value.InputNodeCount >= 0
           && value.SourceReportCount == nodeSourceReportCount
           && value.Fields.All(field => field.ValueCount >= 0
                                       && field.SourceReportCount >= 0
                                       && field.SourceReportCount <= nodeSourceReportCount)
           && value.Fields.Select(x => x.FieldId).Distinct(StringComparer.Ordinal).Count()
              == value.Fields.Length
           && value.Fields.Select(x => x.FieldKey).Distinct(StringComparer.Ordinal).Count()
              == value.Fields.Length
           && value.Fields.SequenceEqual(value.Fields
                   .OrderBy(x => x.FieldKey, StringComparer.Ordinal)
                   .ThenBy(x => x.FieldId, StringComparer.Ordinal));

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(character => character is >= '0' and <= '9'
                                     or >= 'a' and <= 'f');

    private static string BoundarySemantic(ActualAdvancedOwnerBoundary value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_ADVANCED_BOUNDARY_V1",
            value.WorkId, value.AssignmentId, value.DynamicFormTemplateId, value.SectionId,
            value.ConfigId, value.ConfigVersionId,
            StatisticReconciliationActualCanonical.Integer(value.ConfigVersionNo),
            StatisticReconciliationActualCanonical.Integer(value.ConfigRevision),
            value.ConfigSha256,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_DEPENDENCY_PINS_V1", value.DependencyPins),
            value.TimeAxis, value.CandidateChainId, value.CandidatePromptId,
            StatisticReconciliationActualCanonical.Integer(value.CandidateStage),
            value.CandidateCatalogRawSha256, value.CandidateCatalogSemanticSha256,
            value.CandidateStageLockSha256,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_DAY_SELECTORS_V1", value.DayNodeIds),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_MONTH_SELECTORS_V1", value.MonthNodeIds),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_ADVANCED_YEAR_SELECTORS_V1", value.YearNodeIds));

    private static ImmutableArray<string> SelectorIds(ImmutableArray<string> values, string name)
    {
        if (values.IsDefault)
            throw Fail("ADVANCED_SELECTOR_IDS_REQUIRED");
        var normalized = values.Select(x => Required(x, name))
            .OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Fail("ADVANCED_SELECTOR_IDS_DUPLICATE");
        return normalized;
    }

    private static ImmutableArray<string> Pins(ImmutableArray<string> values, string name)
    {
        if (values.IsDefault || values.Length > 10_000)
            throw Fail("ADVANCED_DEPENDENCY_PINS_INVALID");
        var normalized = values.Select(x => Required(x, name)).ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Fail("ADVANCED_DEPENDENCY_PINS_DUPLICATE");
        if (!normalized.SequenceEqual(normalized.OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw Fail("ADVANCED_DEPENDENCY_PINS_ORDER_INVALID");
        return normalized;
    }

    private static ImmutableArray<string> Snapshot(IEnumerable<string>? values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        var output = values.Select(x => Required(x, name)).ToImmutableArray();
        if (output.Length > MaxNodes)
            throw Fail("ADVANCED_NODE_LINEAGE_LIMIT");
        return output;
    }

    private static void RequireExactIds(
        IEnumerable<string> observed,
        ImmutableArray<string> expected,
        string reason)
    {
        var values = observed.Select(x => Required(x, reason))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!values.SequenceEqual(expected, StringComparer.Ordinal))
            throw Fail($"{reason}_MISMATCH");
    }

    private static JsonElement Property(JsonElement root, string name, JsonValueKind kind)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw Fail($"ADVANCED_VALUE_{name.ToUpperInvariant()}_INVALID");
        return value;
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Integer(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.TryGetInt64(out var integer)
            ? integer
            : throw Fail($"ADVANCED_VALUE_{name.ToUpperInvariant()}_INVALID");

    private static DateTime Utc(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTime(out var parsed)
            || parsed.Kind != DateTimeKind.Utc)
            throw Fail($"ADVANCED_VALUE_{name.ToUpperInvariant()}_INVALID");
        return parsed;
    }

    private static ImmutableArray<string> Strings(JsonElement array, string name)
    {
        var output = ImmutableArray.CreateBuilder<string>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                throw Fail($"{name}_INVALID");
            output.Add(Required(value.GetString(), name));
        }
        return output.ToImmutable();
    }

    private static int GrainRank(string grain) => grain switch
    {
        WorkAssignmentAdvancedSummaryHierarchyGrains.Day => 0,
        WorkAssignmentAdvancedSummaryHierarchyGrains.Month => 1,
        WorkAssignmentAdvancedSummaryHierarchyGrains.Year => 2,
        _ => int.MaxValue
    };

    private static string MonthFromDay(string dayKey)
        => dayKey.Length == 10 && dayKey[4] == '-' && dayKey[7] == '-'
            ? dayKey[..7]
            : throw Fail("ADVANCED_DAY_KEY_INVALID");

    private static string YearFromDay(string dayKey)
        => dayKey.Length == 10 && dayKey[4] == '-' && dayKey[7] == '-'
            ? dayKey[..4]
            : throw Fail("ADVANCED_DAY_KEY_INVALID");

    private static bool Canonical(ImmutableArray<string> values)
        => values.SequenceEqual(values.OrderBy(x => x, StringComparer.Ordinal),
            StringComparer.Ordinal)
           && values.Distinct(StringComparer.Ordinal).Count() == values.Length;

    private static string RequireExact(string? value, string expected, string name)
    {
        var actual = Required(value, name);
        if (!StringComparer.Ordinal.Equals(actual, expected))
            throw Fail($"{name}_MISMATCH");
        return actual;
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string? Optional(string? value, string name)
        => StatisticReconciliationActualCanonical.Optional(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static string OwnerSha(string? value, string name)
    {
        var required = Required(value, name);
        if (!Eq(required, required.ToLowerInvariant()))
            throw Fail($"{name}_NON_CANONICAL_CASE" );
        return StatisticReconciliationActualCanonical.Sha256(required, name);
    }
    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);
    private static string? Instant(DateTime? value)
        => value.HasValue
            ? StatisticReconciliationActualCanonical.Instant(
                value.Value.Kind == DateTimeKind.Utc
                    ? value.Value
                    : throw Fail("ADVANCED_OWNER_TIME_NOT_UTC"))
            : null;
    private static string ExactUpper(string? value, string name)
    {
        var required = StatisticReconciliationActualCanonical.Required(value, name);
        if (!StringComparer.Ordinal.Equals(required, required.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL_CASE" );
        return required;
    }

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);

    private sealed record ParsedAdvancedValue(
        JsonDocument? Document,
        ActualAdvancedValueObservation? Value);
}
