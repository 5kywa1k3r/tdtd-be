using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class StatisticReconciliationActualSummaryOwnerParity
{
    internal static string BasicCaptureSemantic(
        ActualBasicResultObservation owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var boundary = owner.Boundary;
        var boundarySemantic = Hash("P10_ACTUAL_BASIC_BOUNDARY_V1",
            boundary.OwnerSnapshotId, boundary.WorkId,
            boundary.ScopeAssignmentId, boundary.DynamicFormTemplateId,
            boundary.SourceScopeMode, boundary.SourceFlowInstanceId,
            boundary.SourceFlowStepId, boundary.SourceFlowBranchId,
            boundary.SourceFlowEffectiveStatus, boundary.RequestHash,
            boundary.ConfigId, boundary.ConfigVersionId,
            I(boundary.ConfigVersionNo), I(boundary.ConfigRevision),
            boundary.ConfigSha256,
            HashSequence("P10_ACTUAL_BASIC_CONFIG_DEPENDENCIES_V1",
                boundary.ConfigDependencyPins), boundary.CandidateChainId,
            boundary.CandidatePromptId, I(boundary.CandidateStage),
            boundary.CandidateCatalogRawSha256,
            boundary.CandidateCatalogSemanticSha256,
            boundary.CandidateStageLockSha256);
        return Hash("P10_ACTUAL_BASIC_RESULT_CAPTURE_V1",
            boundarySemantic, owner.RequestRawSha256,
            owner.RequestCanonicalSha256, owner.SnapshotRawSha256,
            owner.SnapshotCanonicalSha256,
            owner.SourceAssignmentSetSha256, owner.SourceReportSetSha256,
            HashSequence("P10_ACTUAL_BASIC_ASSIGNMENT_ORDER_V1",
                owner.SourceAssignmentIds),
            HashSequence("P10_ACTUAL_BASIC_REPORT_ORDER_V1",
                owner.SourceReportIds), owner.SourceSignatureSha256,
            owner.OwnerState.OwnerLifecycleSemanticSha256,
            "FLOW_RUNTIME_REVISION_NOT_PERSISTED_BY_BASIC_OWNER",
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.InnerMetaMatchesOuter),
            HashSequence("P10_ACTUAL_BASIC_RESULT_ITEMS_V1",
                owner.ResultItems.Select(value => value.SemanticSha256)));
    }

    private static void ValidateBasicCaptureRoot(
        ActualBasicResultObservation owner)
    {
        if (owner.SnapshotPayloadKind != "COMPACT_VALUES1D_V9" ||
            owner.SnapshotPayloadVersion != 9 ||
            owner.SourceAssignmentIds.IsDefault ||
            owner.SourceReportIds.IsDefault || owner.ResultItems.IsDefault ||
            owner.Boundary.ConfigDependencyPins.IsDefault ||
            owner.SourceAssignmentIds.Distinct(StringComparer.Ordinal).Count() !=
                owner.SourceAssignmentIds.Length ||
            owner.SourceReportIds.Distinct(StringComparer.Ordinal).Count() !=
                owner.SourceReportIds.Length)
            throw Fail("BASIC_CAPTURE_ROOT_INVALID");
        var assignmentSet = HashSequence(
            "P10_ACTUAL_BASIC_ASSIGNMENT_SET_V1",
            owner.SourceAssignmentIds.Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
        var reportSet = HashSequence("P10_ACTUAL_BASIC_REPORT_SET_V1",
            owner.SourceReportIds.Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
        if (assignmentSet != owner.SourceAssignmentSetSha256 ||
            reportSet != owner.SourceReportSetSha256 ||
            BasicCaptureSemantic(owner) != owner.CaptureSemanticSha256)
            throw Fail("BASIC_CAPTURE_ROOT_SEMANTIC_MISMATCH");
    }

    internal static string AdvancedCaptureSemantic(ActualAdvancedCapture owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var boundary = owner.Boundary;
        var boundarySemantic = Hash("P10_ACTUAL_ADVANCED_BOUNDARY_V1",
            boundary.WorkId, boundary.AssignmentId,
            boundary.DynamicFormTemplateId, boundary.SectionId,
            boundary.ConfigId, boundary.ConfigVersionId,
            I(boundary.ConfigVersionNo), I(boundary.ConfigRevision),
            boundary.ConfigSha256,
            HashSequence("P10_ACTUAL_ADVANCED_DEPENDENCY_PINS_V1",
                boundary.DependencyPins), boundary.TimeAxis,
            boundary.CandidateChainId, boundary.CandidatePromptId,
            I(boundary.CandidateStage), boundary.CandidateCatalogRawSha256,
            boundary.CandidateCatalogSemanticSha256,
            boundary.CandidateStageLockSha256,
            HashSequence("P10_ACTUAL_ADVANCED_DAY_SELECTORS_V1",
                boundary.DayNodeIds),
            HashSequence("P10_ACTUAL_ADVANCED_MONTH_SELECTORS_V1",
                boundary.MonthNodeIds),
            HashSequence("P10_ACTUAL_ADVANCED_YEAR_SELECTORS_V1",
                boundary.YearNodeIds));
        return Hash("P10_ACTUAL_ADVANCED_CAPTURE_V1", boundarySemantic,
            HashSequence("P10_ACTUAL_ADVANCED_NODES_V1",
                owner.Nodes.Select(value => value.SemanticSha256)));
    }

    private static void ValidateAdvancedCaptureRoot(ActualAdvancedCapture owner)
    {
        if (owner.Nodes.IsDefault || owner.Boundary.DependencyPins.IsDefault ||
            owner.Boundary.DayNodeIds.IsDefault ||
            owner.Boundary.MonthNodeIds.IsDefault ||
            owner.Boundary.YearNodeIds.IsDefault ||
            owner.TotalNodeCount != owner.Nodes.Length ||
            AdvancedCaptureSemantic(owner) != owner.CaptureSemanticSha256)
            throw Fail("ADVANCED_CAPTURE_ROOT_SEMANTIC_MISMATCH");
        var observed = owner.Nodes.Select(value => value.OwnerNodeId)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var selected = owner.Boundary.DayNodeIds
            .Concat(owner.Boundary.MonthNodeIds)
            .Concat(owner.Boundary.YearNodeIds)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!observed.SequenceEqual(selected, StringComparer.Ordinal))
            throw Fail("ADVANCED_CAPTURE_SELECTOR_MISMATCH");
    }

    internal static string DiffCaptureSemantic(ActualP9DiffCapture owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var boundary = owner.Boundary;
        var boundarySemantic = Hash("P10_ACTUAL_P9_DIFF_BOUNDARY_V1",
            boundary.ResultId, boundary.RunId, boundary.WorkId,
            boundary.AssignmentId, boundary.DynamicFormTemplateId,
            boundary.ConfigId, boundary.ConfigVersionId,
            I(boundary.ConfigVersionNo), I(boundary.ConfigRevision),
            boundary.ConfigSha256,
            HashSequence("P10_ACTUAL_P9_DIFF_DEPENDENCIES_V1",
                boundary.DependencyPins), boundary.CandidateChainId,
            boundary.CandidatePromptId, I(boundary.CandidateStage),
            boundary.CandidateCatalogRawSha256,
            boundary.CandidateCatalogSemanticSha256,
            boundary.CandidateStageLockSha256);
        return Hash("P10_ACTUAL_P9_TYPED_DIFF_CAPTURE_V1", boundarySemantic,
            owner.LeftConceptKind, owner.LeftConceptKey,
            owner.LeftConceptCode, owner.LeftDataType,
            StatisticReconciliationActualJson.RawSha256(
                owner.LeftPeriodCanonicalJson), owner.RightConceptKind,
            owner.RightConceptKey, owner.RightConceptCode,
            owner.RightDataType,
            StatisticReconciliationActualJson.RawSha256(
                owner.RightPeriodCanonicalJson), owner.Direction,
            owner.MissingPolicy, owner.EmptyPolicy, owner.TimeAxis,
            HashSequence("P10_ACTUAL_P9_DIFF_SOURCE_PINS_V1",
                owner.SourcePins.Select(value => value.SemanticSha256)),
            HashSequence("P10_ACTUAL_P9_DIFF_ROWS_V1",
                owner.Rows.Select(value => value.SemanticSha256)),
            owner.StoredResultSha256, owner.ObservedResultSha256,
            owner.OwnerState.OwnerLifecycleSemanticSha256,
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.TotalsMatchRows),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.RowsCanonical),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.SourcePinsCanonical),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.OwnerMetadataValid),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.PeriodsValid),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.TypedRowsValid),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.ComparisonsConsistent),
            StatisticReconciliationActualCanonical.Boolean(
                owner.OwnerState.DeclaredTypesMatch),
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed);
    }

    private static void ValidateDiffCaptureRoot(ActualP9DiffCapture owner)
    {
        if (owner.SourcePins.IsDefault || owner.Rows.IsDefault ||
            owner.Boundary.DependencyPins.IsDefault ||
            (owner.Rows.Length == 0) != (owner.SourcePins.Length == 0) ||
            !owner.OwnerState.IsCompletedResult ||
            !owner.OwnerState.ResultHashMatches ||
            !owner.OwnerState.TotalsMatchRows ||
            !owner.OwnerState.RowsCanonical ||
            !owner.OwnerState.SourcePinsCanonical ||
            !owner.OwnerState.OwnerMetadataValid ||
            !owner.OwnerState.PeriodsValid ||
            !owner.OwnerState.TypedRowsValid ||
            !owner.OwnerState.ComparisonsConsistent ||
            !owner.OwnerState.DeclaredTypesMatch ||
            !owner.OwnerState.IsUsableResult ||
            owner.StoredResultSha256 != owner.ObservedResultSha256 ||
            DiffCaptureSemantic(owner) != owner.CaptureSemanticSha256)
            throw Fail("DIFF_CAPTURE_ROOT_SEMANTIC_MISMATCH");
        string? prior = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in owner.SourcePins)
        {
            var semantic = Hash("P10_ACTUAL_P9_DIFF_SOURCE_PIN_V1",
                I(pin.OwnerOrdinal), pin.Side, pin.SourceReportId,
                I(pin.SourcePayloadRevision), pin.SourcePayloadSha256,
                I(pin.SourceLifecycleRevision), pin.DirectRunId,
                pin.DirectGenerationId);
            var tuple = $"{pin.Side}\u001f{pin.SourceReportId}";
            if (semantic != pin.SemanticSha256 || pin.OwnerOrdinal < 0 ||
                pin.SourcePayloadRevision < 1 ||
                pin.SourceLifecycleRevision < 1 ||
                pin.Side is not ("LEFT" or "RIGHT") ||
                !keys.Add($"{tuple}\u001f{pin.DirectGenerationId}") ||
                prior is not null && StringComparer.Ordinal.Compare(
                    prior, tuple) > 0)
                throw Fail("DIFF_SOURCE_PIN_SEMANTIC_MISMATCH");
            prior = tuple;
        }
    }
}
