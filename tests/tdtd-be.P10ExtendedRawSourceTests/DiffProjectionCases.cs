using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class DiffProjectionCases
{
    private const string LeftPeriod =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-07\"}";
    private const string RightPeriod =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\"}";

    internal static int Run()
    {
        var cases = 0;
        cases += StringListMultiReport();
        cases += SideOnlyAdded();
        cases += SideOnlyRemoved();
        cases += TextChanged();
        cases += ReverseDirection();
        cases += PeriodMutation();
        return cases;
    }

    private static int StringListMultiReport()
    {
        var descriptor = TestSupport.DiffDescriptor(
            "FIELD", "CHOICE", "CHANGED");
        var left = TestSupport.DiffSide(
            "LEFT", "[\"A\",\"B\"]", "[\"B\",\"C\"]");
        var right = TestSupport.DiffSide(
            "RIGHT", "[\"A\",\"D\"]", "[\"D\"]");
        var typed = Compile(descriptor, "LEFT_TO_RIGHT", "FIELD", "CHOICE",
            left, right);
        var before = typed.LeftAtoms.Where(value =>
                value.IdentitySha256 == descriptor.IdentitySha256)
            .ToArray();
        TestSupport.Equal("2",
            before.Single(value => value.AtomKind == "COUNT")
                .CanonicalValue,
            "P10-EXT-DIFF-01 string-list-raw-sample-count");
        var raw = TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualExtendedRawSourceOwnerParity),
            "ChoiceValue",
            descriptor,
            before,
            "BEFORE",
            2L,
            "CHOICE");
        var choices = (ImmutableArray<string>)(raw.GetType()
            .GetProperty("Choices")?.GetValue(raw)
            ?? throw new InvalidOperationException("choices"));
        TestSupport.Equal("A,B,C", string.Join(',', choices),
            "P10-EXT-DIFF-02 string-list-expanded-overlap");
        TestSupport.Equal("CHANGED", Transition(typed),
            "P10-EXT-DIFF-03 field-choice-changed");
        return 3;
    }

    private static int SideOnlyAdded()
    {
        var descriptor = TestSupport.DiffDescriptor(
            "TABLE_METRIC", "BOOLEAN", "ADDED");
        var left = TestSupport.DiffSide("LEFT");
        var right = TestSupport.DiffSide("RIGHT", "true");
        var typed = Compile(
            descriptor,
            "LEFT_TO_RIGHT",
            "TABLE_METRIC",
            "BOOLEAN",
            left,
            right);
        TestSupport.Equal("ADDED", Transition(typed),
            "P10-EXT-DIFF-04 table-metric-boolean-added-side-only");
        TestSupport.Equal("RIGHT_ONLY", typed.SourcePairs.Single().RelationKind,
            "P10-EXT-DIFF-05 added-pair-relation");
        return 2;
    }

    private static int SideOnlyRemoved()
    {
        var descriptor = TestSupport.DiffDescriptor(
            "ROW_LABEL", "DATE", "REMOVED");
        var left = TestSupport.DiffSide("LEFT", "\"01/08/2026\"");
        var right = TestSupport.DiffSide("RIGHT");
        var typed = Compile(
            descriptor,
            "LEFT_TO_RIGHT",
            "ROW_LABEL",
            "DATE",
            left,
            right);
        TestSupport.Equal("REMOVED", Transition(typed),
            "P10-EXT-DIFF-06 row-label-date-removed-side-only");
        TestSupport.Equal("LEFT_ONLY", typed.SourcePairs.Single().RelationKind,
            "P10-EXT-DIFF-07 removed-pair-relation");
        return 2;
    }

    private static int TextChanged()
    {
        var descriptor = TestSupport.DiffDescriptor(
            "FIELD", "TEXT", "CHANGED");
        var left = TestSupport.DiffSide("LEFT", "\"before\"");
        var right = TestSupport.DiffSide("RIGHT", "\"after\"");
        var typed = Compile(
            descriptor,
            "LEFT_TO_RIGHT",
            "FIELD",
            "TEXT",
            left,
            right);
        TestSupport.Equal("CHANGED", Transition(typed),
            "P10-EXT-DIFF-08 field-text-changed");
        return 1;
    }

    private static int ReverseDirection()
    {
        var descriptor = TestSupport.DiffDescriptor(
            "FIELD", "NUMBER", "CHANGED", "RIGHT_TO_LEFT");
        var left = TestSupport.DiffSide("LEFT", "9");
        var right = TestSupport.DiffSide("RIGHT", "4");
        var typed = Compile(
            descriptor,
            "RIGHT_TO_LEFT",
            "FIELD",
            "NUMBER",
            left,
            right);
        TestSupport.Equal("AFTER", typed.LeftTransitionLeg,
            "P10-EXT-DIFF-09 reverse-left-leg");
        TestSupport.Equal("BEFORE", typed.RightTransitionLeg,
            "P10-EXT-DIFF-10 reverse-right-leg");
        TestSupport.Equal("CHANGED", Transition(typed),
            "P10-EXT-DIFF-11 reverse-direction-transition");
        return 3;
    }

    private static int PeriodMutation()
    {
        var descriptor = TestSupport.DiffDescriptor(
            "FIELD", "BOOLEAN", "CHANGED");
        var left = TestSupport.DiffSide("LEFT", "false");
        var right = TestSupport.DiffSide("RIGHT", "true");
        TestSupport.Throws(
            "P10-EXT-DIFF-12 period-one-bit-mutation",
            () => TestSupport.Compiler().CompileDiff(
                TestSupport.Plan(descriptor),
                "LEFT_TO_RIGHT",
                "FIELD",
                "BOOLEAN",
                LeftPeriod,
                "FIELD",
                "BOOLEAN",
                "{\"mode\":\"EXACT\",\"periodKey\":\"2026-09\"}",
                left.Pins,
                left.Envelopes,
                right.Pins,
                right.Envelopes));
        return 1;
    }

    private static StatisticReconciliationActualExtendedDiffTypedCompilation
        Compile(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            string direction,
            string kind,
            string ownerType,
            (ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                Pins,
                ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                Envelopes) left,
            (ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                Pins,
                ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                Envelopes) right)
        => TestSupport.Compiler().CompileDiff(
            TestSupport.Plan(descriptor),
            direction,
            kind,
            ownerType,
            LeftPeriod,
            kind,
            ownerType,
            RightPeriod,
            left.Pins,
            left.Envelopes,
            right.Pins,
            right.Envelopes);

    private static string Transition(
        StatisticReconciliationActualExtendedDiffTypedCompilation value)
        => value.TransitionAtoms.Single(atom =>
                atom.AtomKind == "TRANSITION_KIND")
            .CanonicalValue;
}
