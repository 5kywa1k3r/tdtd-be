using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var tests = new (string Name, Func<Task> Run)[]
{
    ("P10-ASOURCE-01", ActualAdapterTests.SourceOwnerClaimsAreObserved),
    ("P10-ASOURCE-02", ActualAdapterTests.SourceLifecycleStateIsHashBound),
    ("P10-ASOURCE-03", ActualAdapterTests.FlowCurrentEpochAndEffectivenessAreHashBound),
    ("P10-ASOURCE-04", ActualAdapterTests.SourceOrderingAndOwnerPinsFailClosed),
    ("P10-PROJECTION-01", ActualAdapterTests.FieldRowsPreserveTypedIdentityAndProvenance),
    ("P10-PROJECTION-02", ActualAdapterTests.TableMetricRowsPreserveTypedIdentityAndProvenance),
    ("P10-PROJECTION-03", ActualAdapterTests.RowLabelsPreserveTypedIdentityAndProvenance),
    ("P10-PROJECTION-04", ActualAdapterTests.DirectTypedStatesRemainDistinct),
    ("P10-PROJECTION-05", ActualAdapterTests.DirectRowsRevealStateAndMixedPinsFailClosed),
    ("P10-PROJECTION-06", ActualAdapterTests.DirectMultiplicityOrderingAndHashesAreDeterministic)
};

const int expectedCaseCount = 10;
var expectedIds = Enumerable.Range(1, 4)
    .Select(index => $"P10-ASOURCE-{index:00}")
    .Concat(Enumerable.Range(1, 6).Select(index => $"P10-PROJECTION-{index:00}"))
    .ToArray();
if (tests.Length != expectedCaseCount
    || tests.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != expectedCaseCount
    || !tests.Select(item => item.Name).SequenceEqual(expectedIds, StringComparer.Ordinal))
{
    throw new InvalidOperationException("P10-ACTUAL T17/T18 exact registry drifted.");
}

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

Console.WriteLine($"P10_T18_ACTUAL_ADAPTERS_OK cases={passed} stopBefore=P10-T19");

internal static class ActualAdapterTests
{
    internal static async Task SourceOwnerClaimsAreObserved()
    {
        var draftIncluded = ActualFixture.Source(
            ActualFixture.Report1,
            ordinal: 7,
            ownerIncluded: true) with
        {
            LifecycleStatus = "DRAFT",
            IsActive = false,
            AssignmentIsActive = false,
            InvalidatedByFlowEventId = ActualFixture.Event1,
            OwnerDecisionCode = "OWNER_INCLUDED_DRAFT"
        };
        var approvedExcluded = ActualFixture.Source(
            ActualFixture.Report2,
            ordinal: 2,
            ownerIncluded: false) with
        {
            LifecycleStatus = "APPROVED",
            IsActive = true,
            AssignmentIsActive = true,
            ContributionDecision = "EXCLUDE",
            OwnerDecisionCode = "OWNER_EXCLUDED_APPROVED"
        };

        var reader = new SourceReader([draftIncluded, approvedExcluded]);
        var capture = await new StatisticReconciliationActualSourceMembershipAdapter()
            .CaptureAsync(ActualFixture.Scope(), reader);

        Check.Equal(1, reader.CallCount, "one authoritative owner read");
        Check.SequenceEqual(
            new[] { ActualFixture.Report1, ActualFixture.Report2 },
            capture.Decisions.Select(item => item.ReportId),
            "canonical report order");
        Check.Equal(2, capture.Decisions.Length, "all owner claims observed");
        Check.Equal(1, capture.IncludedSources.Length, "raw owner include count");
        var included = capture.IncludedSources.Single();
        Check.Equal(ActualFixture.Report1, included.ReportId, "draft owner claim retained");
        Check.Equal("DRAFT", included.ObservedOwner.LifecycleStatus, "draft state retained");
        Check.False(included.ObservedOwner.IsActive, "inactive state retained");
        Check.Equal("OWNER_INCLUDED_DRAFT", included.OwnerDecisionCode, "owner decision retained");
        Check.False(capture.Decisions.Single(item => item.ReportId == ActualFixture.Report2).Included, "approved exclusion retained");
    }

    internal static async Task SourceLifecycleStateIsHashBound()
    {
        var baseline = ActualFixture.Source(ActualFixture.Report1);
        var changed = baseline with
        {
            LifecycleRevision = baseline.LifecycleRevision + 1,
            LifecycleSha256 = ActualFixture.Hash('d'),
            LifecycleStatus = "RECALLED",
            IsCurrent = false,
            IsActive = false,
            InvalidatedByFlowEventId = ActualFixture.Event1
        };

        var first = await ActualFixture.CaptureSources([baseline]);
        var second = await ActualFixture.CaptureSources([changed]);
        var firstDecision = first.Decisions.Single();
        var secondDecision = second.Decisions.Single();

        Check.Equal(
            firstDecision.SourceStableIdentitySha256,
            secondDecision.SourceStableIdentitySha256,
            "stable source identity");
        Check.NotEqual(
            firstDecision.OwnerStateSemanticSha256,
            secondDecision.OwnerStateSemanticSha256,
            "lifecycle state semantic hash");
        Check.NotEqual(
            firstDecision.DecisionSemanticSha256,
            secondDecision.DecisionSemanticSha256,
            "decision semantic hash");
        Check.NotEqual(first.CaptureSemanticSha256, second.CaptureSemanticSha256, "capture hash");
        Check.True(second.IncludedSources.Single().Included, "owner include is not lifecycle-recomputed");
    }

    internal static async Task FlowCurrentEpochAndEffectivenessAreHashBound()
    {
        var baseline = ActualFixture.Source(ActualFixture.Report1) with
        {
            MappingRevision = 4,
            MappingSemanticSha256 = ActualFixture.Hash('b'),
            FlowEffectiveStatus = "EFFECTIVE",
            FlowRuntime = ActualFixture.Flow(ActualFixture.Report1)
        };
        var changedFlow = baseline.FlowRuntime! with
        {
            CurrentExecutionEpoch = baseline.FlowRuntime!.CurrentExecutionEpoch + 1,
            ExecutionEpoch = baseline.FlowRuntime.ExecutionEpoch + 1,
            ExecutionEpochRevision = baseline.FlowRuntime.ExecutionEpochRevision + 1,
            ExecutionEpochState = "ROLLED_BACK",
            IsCanonicalEpoch = false,
            StepExecutionEpoch = baseline.FlowRuntime.StepExecutionEpoch + 1,
            StepIsCanonicalEpoch = false,
            ContributionWarning = "owner runtime changed"
        };
        var changed = baseline with
        {
            MappingRevision = 5,
            MappingSemanticSha256 = ActualFixture.Hash('c'),
            FlowEffectiveStatus = "STALE",
            FlowRuntime = changedFlow
        };

        var first = await ActualFixture.CaptureSources([baseline]);
        var second = await ActualFixture.CaptureSources([changed]);
        Check.NotEqual(
            first.Decisions.Single().OwnerStateSemanticSha256,
            second.Decisions.Single().OwnerStateSemanticSha256,
            "flow runtime semantic hash");
        Check.NotEqual(first.CaptureSemanticSha256, second.CaptureSemanticSha256, "flow capture hash");
        Check.Equal("STALE", second.Decisions.Single().ObservedOwner.FlowEffectiveStatus, "flow effectiveness retained");

        var badFlow = baseline.FlowRuntime! with
        {
            StepReportLifecycleRevision = baseline.LifecycleRevision + 1
        };
        await Check.Reason(
            "SOURCE_FLOW_REPORT_PIN_DRIFT",
            () => ActualFixture.CaptureSources([baseline with { FlowRuntime = badFlow }]),
            "flow/report lifecycle drift");
    }

    internal static async Task SourceOrderingAndOwnerPinsFailClosed()
    {
        var firstOwner = ActualFixture.Source(ActualFixture.Report1, ordinal: 1);
        var secondOwner = ActualFixture.Source(ActualFixture.Report2, ordinal: 0);
        var first = await ActualFixture.CaptureSources([firstOwner, secondOwner]);
        var replay = await ActualFixture.CaptureSources([secondOwner, firstOwner]);

        Check.SequenceEqual(
            new[] { ActualFixture.Report1, ActualFixture.Report2 },
            first.Decisions.Select(item => item.ReportId),
            "deterministic canonical source order");
        Check.Equal(first.SourceSetSha256, replay.SourceSetSha256, "source-set replay hash");
        Check.Equal(first.CaptureSemanticSha256, replay.CaptureSemanticSha256, "capture replay hash");

        await Check.Reason(
            "SOURCE_REPORT_AMBIGUOUS",
            () => ActualFixture.CaptureSources(
            [
                firstOwner,
                firstOwner with { OwnerOrdinal = 9, PayloadRevision = 2, PayloadSha256 = ActualFixture.Hash('e') }
            ]),
            "conflicting owner report");
        await Check.Reason(
            "SOURCE_OWNER_GENERATION_MISMATCH",
            () => ActualFixture.CaptureSources(
                [firstOwner with { OwnerGenerationId = ActualFixture.Hash('f') }]),
            "mixed owner generation");
        await Check.Reason(
            "OWNER_GENERATION_ID_INVALID",
            () => new StatisticReconciliationActualSourceMembershipAdapter().CaptureAsync(
                ActualFixture.Scope() with { OwnerGenerationId = "not-a-sha" },
                new SourceReader([firstOwner])),
            "non-SHA source generation");
    }

    internal static async Task FieldRowsPreserveTypedIdentityAndProvenance()
    {
        var context = await ActualFixture.IncludedContext();
        var number = ActualFixture.Field(
            context.Boundary,
            context.Source,
            fieldId: "field-a",
            fieldKey: "alpha",
            valueKind: "NUMBER",
            numeric: 12.50m);
        var knownOwnerId = ActualFixture.P9OwnerStableObjectId(
            ActualFixture.GenerationId,
            "FIELD_VALUE",
            ActualFixture.Report1,
            ActualFixture.PeriodInstanceKey,
            ActualFixture.TemplateId,
            "field-a",
            "FORM_FIELD",
            null,
            "NUMBER");
        Check.Equal(
            "79b4f4cb7bfb6735f5eb07de",
            knownOwnerId,
            "independent P9 field ObjectId known vector");
        Check.Equal(knownOwnerId, number.Id, "fixture field ObjectId inputs");
        var numberCapture = await ActualFixture.CaptureDirect(
            context.Boundary,
            context.Membership,
            fields: [number]);
        var observed = numberCapture.FieldRows.Single();

        Check.Equal(number.Id, observed.OwnerRowId, "field owner id");
        Check.Equal("NUMBER", observed.TypedValue.ValueKind, "field kind");
        Check.Equal("12.5", observed.TypedValue.CanonicalValue, "canonical decimal");
        Check.Equal(context.Source.ReportId, observed.Provenance.SourceReportId, "field source report");
        Check.Equal(context.Source.PayloadSha256, observed.Provenance.SourcePayloadSha256, "field source payload");
        Check.True(observed.RowState.SourceMembershipMatched, "field source membership");

        var nullRow = ActualFixture.Field(
            context.Boundary,
            context.Source,
            fieldId: "field-a",
            fieldKey: "alpha",
            valueKind: "NULL");
        var nullCapture = await ActualFixture.CaptureDirect(
            context.Boundary,
            context.Membership,
            fields: [nullRow]);
        var observedNull = nullCapture.FieldRows.Single();
        Check.Equal(
            observed.StableTypedIdentitySha256,
            observedNull.StableTypedIdentitySha256,
            "value state excluded from field metric identity");
        Check.NotEqual(observed.OwnerRowId, observedNull.OwnerRowId, "P9 owner occurrence changes with kind");
        Check.NotEqual(
            observed.TypedValue.ValueIdentitySha256,
            observedNull.TypedValue.ValueIdentitySha256,
            "number/null value state distinct");
    }

    internal static async Task TableMetricRowsPreserveTypedIdentityAndProvenance()
    {
        var context = await ActualFixture.IncludedContext();
        var textRow = ActualFixture.Table(
            context.Boundary,
            context.Source,
            blockId: "block-a",
            metricKey: "metric-a",
            rowKey: "row-a",
            columnKey: "column-a",
            valueKind: "TEXT",
            text: "Alpha",
            bucketKey: "TEXT:alpha");
        var capture = await ActualFixture.CaptureDirect(
            context.Boundary,
            context.Membership,
            tables: [textRow]);
        var observed = capture.TableMetricRows.Single();

        Check.Equal(textRow.Id, observed.OwnerRowId, "table owner id");
        Check.Equal("metric-a", observed.MetricKey, "metric identity");
        Check.Equal("row-a", observed.RowKey, "row identity");
        Check.Equal("column-a", observed.ColumnKey, "column identity");
        Check.Equal("TEXT", observed.TypedValue.ValueKind, "table text kind");
        Check.Equal("TEXT:alpha", observed.TypedValue.BucketKey, "table text bucket");
        Check.Equal("TEXT:alpha", observed.TypedValue.CanonicalValue, "table text canonical value");
        Check.Equal("Alpha", observed.TypedValue.TextValue, "table raw text");
        Check.Equal(context.Boundary.OwnerGenerationSha256, observed.Provenance.OwnerGenerationSha256, "owner generation hash");
        Check.Equal(context.Boundary.OwnerMembershipSignature, observed.Provenance.OwnerMembershipSignature, "membership signature");
    }

    internal static async Task RowLabelsPreserveTypedIdentityAndProvenance()
    {
        var context = await ActualFixture.IncludedContext();
        var label = ActualFixture.Label(
            context.Boundary,
            context.Source,
            blockId: "block-label",
            rowKey: "row-label",
            rowIndex: 4,
            labelCode: "TOTAL");
        var capture = await ActualFixture.CaptureDirect(
            context.Boundary,
            context.Membership,
            labels: [label]);
        var observed = capture.RowLabelRows.Single();

        Check.Equal(label.Id, observed.OwnerRowId, "label owner id");
        Check.Equal("ROW_LABEL", observed.Source, "label source");
        Check.Equal("ROW_LABEL", observed.TypedValue.ValueKind, "label value kind");
        Check.Equal("TOTAL", observed.TypedValue.CanonicalValue, "label canonical value");
        Check.Equal(4, observed.RowIndex, "label row index");
        Check.Equal(context.Source.LifecycleRevision, observed.Provenance.SourceLifecycleRevision, "label lifecycle revision");
        Check.True(observed.RowState.SourceMembershipMatched, "label source membership");
    }

    internal static async Task DirectTypedStatesRemainDistinct()
    {
        var context = await ActualFixture.IncludedContext();
        var date = new DateTime(2026, 8, 10, 1, 2, 3, DateTimeKind.Utc);
        var fields = new[]
        {
            ActualFixture.Field(context.Boundary, context.Source, "missing", "01", "MISSING"),
            ActualFixture.Field(context.Boundary, context.Source, "null", "02", "NULL"),
            ActualFixture.Field(context.Boundary, context.Source, "empty", "03", "EMPTY"),
            ActualFixture.Field(context.Boundary, context.Source, "number", "04", "NUMBER", numeric: 12.50m),
            ActualFixture.Field(context.Boundary, context.Source, "boolean", "05", "BOOLEAN", boolean: false),
            ActualFixture.Field(context.Boundary, context.Source, "date", "06", "DATE", date: date),
            ActualFixture.Field(context.Boundary, context.Source, "option", "07", "OPTION", bucketKey: "OPEN"),
            ActualFixture.Field(context.Boundary, context.Source, "text-bucket", "08", "TEXT_BUCKET", bucketKey: "TEXT:alpha", text: "Alpha"),
            ActualFixture.Field(context.Boundary, context.Source, "present", "09", "PRESENT", text: "present")
        };
        var capture = await ActualFixture.CaptureDirect(
            context.Boundary,
            context.Membership,
            fields: fields);
        var typed = capture.FieldRows.ToDictionary(item => item.TypedValue.ValueKind, item => item.TypedValue);

        Check.Equal("<missing>", typed["MISSING"].CanonicalValue, "missing");
        Check.Equal("<null>", typed["NULL"].CanonicalValue, "null");
        Check.Equal(string.Empty, typed["EMPTY"].CanonicalValue, "empty");
        Check.Equal("12.5", typed["NUMBER"].CanonicalValue, "number");
        Check.Equal("false", typed["BOOLEAN"].CanonicalValue, "false");
        Check.Equal(date.ToString("O"), typed["DATE"].CanonicalValue, "date");
        Check.Equal("OPEN", typed["OPTION"].CanonicalValue, "option");
        Check.Equal("TEXT:alpha", typed["TEXT_BUCKET"].CanonicalValue, "text bucket");
        Check.Equal("Alpha", typed["TEXT_BUCKET"].TextValue, "text bucket raw text");
        Check.Equal("present", typed["PRESENT"].CanonicalValue, "present");
        Check.Equal(9, typed.Values.Select(item => item.ValueIdentitySha256).Distinct(StringComparer.Ordinal).Count(), "typed identity count");

        var invalidOption = ActualFixture.Field(
            context.Boundary,
            context.Source,
            "bad-option",
            "10",
            "OPTION");
        await Check.Reason(
            "DIRECT_OPTION_BUCKET_REQUIRED",
            () => ActualFixture.CaptureDirect(
                context.Boundary,
                context.Membership,
                fields: [invalidOption]),
            "option bucket required");
    }

    internal static async Task DirectRowsRevealStateAndMixedPinsFailClosed()
    {
        var source = ActualFixture.Source(
            ActualFixture.Report1,
            ownerIncluded: false) with
        {
            ContributionDecision = "EXCLUDE",
            OwnerDecisionCode = "OWNER_EXCLUDED"
        };
        var membership = await ActualFixture.CaptureSources([source]);
        var boundary = ActualFixture.Boundary();
        var row = ActualFixture.Field(
            boundary,
            source,
            "inactive",
            "inactive",
            "NULL",
            assignmentIsActive: false,
            reportIsActive: false,
            invalidatedByFlowEventId: ActualFixture.Event1);
        var capture = await ActualFixture.CaptureDirect(boundary, membership, fields: [row]);
        var observed = capture.FieldRows.Single();

        Check.False(observed.RowState.AssignmentIsActive, "inactive assignment retained");
        Check.False(observed.RowState.ReportIsActive, "inactive report retained");
        Check.Equal(ActualFixture.Event1, observed.RowState.InvalidatedByFlowEventId, "invalidation retained");
        Check.False(observed.RowState.SourceMembershipMatched, "excluded source row retained");

        var mixedPin = ActualFixture.Field(boundary, source, "mixed", "mixed", "NULL");
        mixedPin.DirectProjection!.GenerationId = ActualFixture.Hash('f');
        await Check.Reason(
            "DIRECT_PROJECTION_PIN_MISMATCH",
            () => ActualFixture.CaptureDirect(boundary, membership, fields: [mixedPin]),
            "mixed row generation");
        var mixedLifecycle = ActualFixture.Field(boundary, source, "mixed-event", "mixed-event", "NULL");
        mixedLifecycle.DirectProjection!.LifecycleEventKey = "event:approved:4";
        await Check.Reason(
            "DIRECT_PROJECTION_PIN_MISMATCH",
            () => ActualFixture.CaptureDirect(boundary, membership, fields: [mixedLifecycle]),
            "mixed lifecycle event key");
        var mixedComputedAt = ActualFixture.Field(boundary, source, "mixed-time", "mixed-time", "NULL");
        mixedComputedAt.DirectProjection!.ComputedAtUtc =
            mixedComputedAt.DirectProjection.ComputedAtUtc.AddSeconds(1);
        await Check.Reason(
            "DIRECT_PROJECTION_PIN_MISMATCH",
            () => ActualFixture.CaptureDirect(boundary, membership, fields: [mixedComputedAt]),
            "mixed computed-at time");
        await Check.Reason(
            "DIRECT_GENERATION_ID_INVALID",
            () => ActualFixture.CaptureDirect(
                boundary with { GenerationId = "not-a-sha" },
                membership,
                fields: [row]),
            "non-SHA direct generation");
        await Check.Reason(
            "DIRECT_SOURCE_MEMBERSHIP_MISMATCH",
            () => ActualFixture.CaptureDirect(
                boundary,
                membership with { OwnerGenerationSha256 = ActualFixture.Hash('e') },
                fields: [row]),
            "mixed membership generation");
    }

    internal static async Task DirectMultiplicityOrderingAndHashesAreDeterministic()
    {
        var source1 = ActualFixture.Source(ActualFixture.Report1, ordinal: 0);
        var source2 = ActualFixture.Source(ActualFixture.Report2, ordinal: 1);
        var membership = await ActualFixture.CaptureSources([source1, source2]);
        var boundary = ActualFixture.Boundary();

        var fieldZ = ActualFixture.Field(boundary, source1, "field-z", "zeta", "NULL", periodKey: "B");
        var fieldA = ActualFixture.Field(boundary, source2, "field-a", "alpha", "EMPTY", periodKey: "A");
        var tableZ = ActualFixture.Table(boundary, source1, "block-z", "metric-z", "row-z", "column-z", "NUMBER", numeric: 2m, periodKey: "B");
        var tableA = ActualFixture.Table(boundary, source2, "block-a", "metric-a", "row-a", "column-a", "NUMBER", numeric: 1m, periodKey: "A");
        var label2 = ActualFixture.Label(boundary, source1, "block-label", "same-row", 2, "DUP", periodKey: "A");
        var label1 = ActualFixture.Label(boundary, source2, "block-label", "same-row", 1, "DUP", periodKey: "A");

        var first = await ActualFixture.CaptureDirect(
            boundary,
            membership,
            fields: [fieldZ, fieldA],
            tables: [tableZ, tableA],
            labels: [label2, label1]);
        var replay = await ActualFixture.CaptureDirect(
            boundary,
            membership,
            fields: [fieldA, fieldZ],
            tables: [tableA, tableZ],
            labels: [label1, label2]);

        Check.Equal(6, first.TotalRowCount, "all occurrences retained");
        Check.SequenceEqual(new[] { "alpha", "zeta" }, first.FieldRows.Select(item => item.FieldKey), "field order");
        Check.SequenceEqual(new[] { "metric-a", "metric-z" }, first.TableMetricRows.Select(item => item.MetricKey), "table order");
        Check.SequenceEqual(new[] { 1, 2 }, first.RowLabelRows.Select(item => item.RowIndex), "label duplicate order");
        Check.Equal(2, first.RowLabelRows.Count(item => item.LabelCode == "DUP"), "label multiplicity");
        Check.Equal(first.CaptureSemanticSha256, replay.CaptureSemanticSha256, "physical-order invariant layer hash");
        Check.SequenceEqual(
            first.FieldRows.Select(item => item.SemanticSha256),
            replay.FieldRows.Select(item => item.SemanticSha256),
            "field semantic replay");
        Check.SequenceEqual(
            first.TableMetricRows.Select(item => item.SemanticSha256),
            replay.TableMetricRows.Select(item => item.SemanticSha256),
            "table semantic replay");
        Check.SequenceEqual(
            first.RowLabelRows.Select(item => item.SemanticSha256),
            replay.RowLabelRows.Select(item => item.SemanticSha256),
            "label semantic replay");
    }
}

internal static class Check
{
    internal static void True(bool value, string name)
    {
        if (!value)
            throw new InvalidOperationException($"{name}: expected true");
    }

    internal static void False(bool value, string name)
    {
        if (value)
            throw new InvalidOperationException($"{name}: expected false");
    }

    internal static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected={expected}; actual={actual}");
    }

    internal static void NotEqual<T>(T left, T right, string name)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException($"{name}: values unexpectedly equal ({left})");
    }

    internal static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string name)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                $"{name}: expected=[{string.Join(",", expectedArray)}]; actual=[{string.Join(",", actualArray)}]");
        }
    }

    internal static async Task Reason(string expected, Func<Task> action, string name)
    {
        try
        {
            await action();
        }
        catch (StatisticReconciliationActualObservationException error)
        {
            Equal(expected, error.Reason, name);
            return;
        }

        throw new InvalidOperationException($"{name}: expected reason {expected}");
    }
}
