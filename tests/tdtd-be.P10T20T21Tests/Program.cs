using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-ABASIC-NONFLOW-01", BasicDirectChildrenOrSelf),
    ("P10-ABASIC-NONFLOW-02", BasicDirectChildren),
    ("P10-ABASIC-01", BasicFlowBranch),
    ("P10-ABASIC-02", BasicFlowStep),
    ("P10-ABASIC-03", BasicFlowEffectivePath),
    ("P10-ABASIC-04", BasicFlowFinal),
    ("P10-AADVDIFF-01", AdvancedDayMonthYear),
    ("P10-AADVDIFF-02", DiffField),
    ("P10-AADVDIFF-03", DiffTableMetric),
    ("P10-AADVDIFF-04", DiffRowLabel)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"FAIL {item.Id}: {exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

Console.WriteLine(
    $"P10_T20_T21_ACTUAL_BASIC_ADVANCED_DIFF_OK cases={passed} cumulative=26 stopBefore=P10-T22");
return passed == cases.Length ? 0 : 1;

static async Task BasicDirectChildrenOrSelf()
{
    var boundary = Fixture.BasicBoundary(
        StatisticReconciliationActualBasicModes.DirectChildrenOrSelf);
    var owner = Fixture.BasicOwner(boundary);
    var reader = new BasicReader(owner);
    var capture = await new StatisticReconciliationActualBasicAdapter()
        .CaptureDirectChildrenOrSelfAsync(boundary, reader);

    Equal(StatisticReconciliationActualBasicModes.DirectChildrenOrSelf,
        capture.Boundary.SourceScopeMode, "direct self mode");
    Null(capture.Boundary.SourceFlowInstanceId, "direct self instance");
    Null(capture.Boundary.SourceFlowStepId, "direct self step");
    Null(capture.Boundary.SourceFlowBranchId, "direct self branch");
    Null(capture.Boundary.SourceFlowEffectiveStatus, "direct self status");
    True(capture.OwnerState.InnerMetaMatchesOuter, "direct self inner meta");
    Equal(2, capture.SourceAssignmentIds.Length,
        "direct self source assignments bound");
    Equal(2, capture.SourceReportIds.Length,
        "direct self source reports bound");

    var forged = boundary with { SourceFlowInstanceId = "forged-flow" };
    await Throws("BASIC_NON_FLOW_SCOPE_INVALID", async () =>
        await new StatisticReconciliationActualBasicAdapter()
            .CaptureDirectChildrenOrSelfAsync(forged, reader));
    Equal(1, reader.ReadCount, "forged selector rejected before owner read");
}

static async Task BasicDirectChildren()
{
    var boundary = Fixture.BasicBoundary(
        StatisticReconciliationActualBasicModes.DirectChildren);
    var owner = Fixture.BasicOwner(boundary);
    var capture = await new StatisticReconciliationActualBasicAdapter()
        .CaptureDirectChildrenAsync(boundary, new BasicReader(owner));

    Equal(StatisticReconciliationActualBasicModes.DirectChildren,
        capture.Boundary.SourceScopeMode, "direct children mode");
    Null(capture.Boundary.SourceFlowInstanceId, "direct children instance");
    True(capture.OwnerState.SourceIdsUnique,
        "direct children source identities unique");
}

static async Task BasicFlowBranch()
{
    var boundary = Fixture.BasicBoundary(StatisticReconciliationActualBasicModes.FlowBranch);
    var owner = Fixture.BasicOwner(boundary);
    var reader = new BasicReader(owner);
    var capture = await new StatisticReconciliationActualBasicAdapter()
        .CaptureFlowBranchAsync(boundary, reader);

    Equal(1, reader.ReadCount, "branch owner reads");
    NotNull(reader.LastBoundary, "branch normalized boundary");
    Equal(boundary.SourceScopeMode, reader.LastBoundary!.SourceScopeMode,
        "branch normalized boundary mode");
    SequenceEqual(boundary.ConfigDependencyPins,
        reader.LastBoundary.ConfigDependencyPins,
        "branch normalized boundary dependencies");
    Equal(StatisticReconciliationActualBasicModes.FlowBranch,
        capture.Boundary.SourceScopeMode, "branch mode");
    Equal("branch-a", capture.Boundary.SourceFlowBranchId, "branch selector");
    Null(capture.Boundary.SourceFlowStepId, "branch step selector");
    Equal(Fixture.RawSha256(owner.SnapshotJson), capture.SnapshotRawSha256,
        "branch snapshot raw sha");
    Equal(Fixture.RawSha256(owner.RequestJson), capture.RequestRawSha256,
        "branch request raw sha");
    NotEqual(capture.SnapshotRawSha256, capture.SnapshotCanonicalSha256,
        "branch raw and canonical sha");
    True(capture.OwnerState.InnerMetaMatchesOuter, "branch inner meta");
    True(capture.OwnerState.OwnerCompleteAndClean, "branch clean owner");
    Equal(boundary.ConfigSha256, owner.ConfigHash, "branch config pin");
    Equal(2, capture.ResultItems.Length, "branch owner item multiplicity");

    await Throws("BASIC_SOURCE_SCOPE_MODE_MISMATCH", async () =>
        await new StatisticReconciliationActualBasicAdapter()
            .CaptureFlowStepAsync(boundary, reader));
    Equal(1, reader.ReadCount, "wrong mode rejected before owner read");
}

static async Task BasicFlowStep()
{
    var boundary = Fixture.BasicBoundary(StatisticReconciliationActualBasicModes.FlowStep);
    var owner = Fixture.BasicOwner(boundary, dirty: true);
    var reader = new BasicReader(owner);
    var capture = await new StatisticReconciliationActualBasicAdapter()
        .CaptureFlowStepAsync(boundary, reader);

    Equal("step-a", capture.Boundary.SourceFlowStepId, "step selector");
    Null(capture.Boundary.SourceFlowBranchId, "step branch selector");
    True(capture.OwnerState.SnapshotDirty, "dirty state remains observable");
    False(capture.OwnerState.OwnerCompleteAndClean, "dirty owner is not clean");
    True(capture.OwnerState.InnerMetaMatchesOuter, "dirty snapshot meta preserved");
    Equal(owner.SnapshotJson, capture.SnapshotJson, "dirty owner payload preserved");
    Equal(WorkAssignmentBasicSummaryRefreshStatuses.Done,
        capture.OwnerState.RefreshStatus, "dirty done status preserved");
}

static async Task BasicFlowEffectivePath()
{
    var boundary = Fixture.BasicBoundary(
        StatisticReconciliationActualBasicModes.FlowEffectivePath);
    var owner = Fixture.BasicOwner(boundary, innerMetaMatches: false);
    var reader = new BasicReader(owner);
    var capture = await new StatisticReconciliationActualBasicAdapter()
        .CaptureFlowEffectivePathAsync(boundary, reader);

    Null(capture.Boundary.SourceFlowStepId, "effective path step selector");
    Null(capture.Boundary.SourceFlowBranchId, "effective path branch selector");
    False(capture.OwnerState.InnerMetaMatchesOuter,
        "effective path stale inner meta revealed");
    True(capture.OwnerState.OwnerCompleteAndClean,
        "owner lifecycle stays separate from inner meta");
    Equal(Fixture.RawSha256(owner.SnapshotJson), capture.SnapshotRawSha256,
        "effective path raw sha");

    owner.ConfigHash = Fixture.Hash('f');
    await Throws("BASIC_OWNER_BOUNDARY_MISMATCH", async () =>
        await new StatisticReconciliationActualBasicAdapter()
            .CaptureFlowEffectivePathAsync(boundary, reader));
}

static async Task BasicFlowFinal()
{
    var boundary = Fixture.BasicBoundary(StatisticReconciliationActualBasicModes.FlowFinal);
    var owner = Fixture.BasicOwner(boundary);
    owner.SourceReportIds.Reverse();
    var reader = new BasicReader(owner);
    var capture = await new StatisticReconciliationActualBasicAdapter()
        .CaptureFlowFinalAsync(boundary, reader);

    Equal(StatisticReconciliationActualBasicModes.FlowFinal,
        capture.Boundary.SourceScopeMode, "final mode");
    SequenceEqual(owner.SourceReportIds, capture.SourceReportIds,
        "final owner source order preserved");
    False(capture.OwnerState.SourceReportOrderCanonical,
        "final noncanonical source order revealed");
    True(capture.OwnerState.SourceIdsUnique, "final source identity uniqueness");
    var methods = typeof(IStatisticReconciliationActualBasicOwnerReader).GetMethods();
    Equal(1, methods.Length, "basic reader method count");
    True(methods.All(method => method.Name.StartsWith("Read", StringComparison.Ordinal)),
        "basic reader is read-only");
}

static async Task AdvancedDayMonthYear()
{
    var boundary = Fixture.AdvancedBoundary();
    var (day, month, year) = Fixture.AdvancedNodes(boundary);
    var reader = new AdvancedReader([day], [month], [year]);
    var capture = await new StatisticReconciliationActualAdvancedAdapter()
        .CaptureAsync(boundary, reader);

    Equal(3, capture.TotalNodeCount, "advanced node multiplicity");
    Equal(1, reader.DayReads, "advanced day reads");
    Equal(1, reader.MonthReads, "advanced month reads");
    Equal(1, reader.YearReads, "advanced year reads");
    SequenceEqual(new[] { "YEAR", "DAY", "MONTH" },
        capture.Nodes.Select(node => node.Grain), "advanced deterministic order");

    var observedDay = capture.Nodes.Single(node => node.Grain == "DAY");
    NotNull(observedDay.Value, "advanced day typed value");
    Equal("FLOW_STEP", observedDay.Value!.SourceScopeMode,
        "advanced source scope mode");
    Equal("step-a", observedDay.Value.SourceFlowStepId,
        "advanced source step");
    Equal(2L, observedDay.Value.SourceReportCount,
        "advanced typed source count");
    Equal(2, observedDay.SourceReportIds.Length,
        "advanced source report identities");
    Equal("NUMBER", observedDay.Value.Fields[0].DataType,
        "advanced typed field data type");
    Contains("\"sum\":42", observedDay.Value.Fields[0].ResultCanonicalJson,
        "advanced typed field result");
    Equal(Fixture.RawSha256(day.ValueJson), observedDay.ObservedValueSha256,
        "advanced independent raw value sha");
    True(observedDay.OwnerState.ValueHashMatches,
        "advanced stored raw value hash");
    True(observedDay.OwnerState.GrainCoverageShapeValid,
        "advanced day coverage");

    var observedMonth = capture.Nodes.Single(node => node.Grain == "MONTH");
    True(observedMonth.OwnerState.IsDirty, "advanced dirty month observed");
    Equal("upstream-day-changed", observedMonth.OwnerState.DirtyReason,
        "advanced dirty reason observed");
    False(observedMonth.OwnerState.IsCleanResult,
        "advanced dirty owner not repaired");
    True(observedMonth.OwnerState.ValueJsonShapeValid,
        "advanced dirty value still typed");
    True(capture.Nodes.Single(node => node.Grain == "YEAR")
        .OwnerState.GrainCoverageShapeValid, "advanced year coverage");

    var (relationDay, relationMonth, relationYear) = Fixture.AdvancedNodes(boundary);
    relationMonth.Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean;
    relationMonth.IsDirty = false;
    relationMonth.DirtyReason = null;
    Fixture.SetMonthYearKey(relationMonth, "2025");
    var relationCapture = await new StatisticReconciliationActualAdvancedAdapter()
        .CaptureAsync(boundary,
            new AdvancedReader([relationDay], [relationMonth], [relationYear]));
    False(relationCapture.Nodes.Single(node => node.Grain == "MONTH")
        .OwnerState.IsCleanResult,
        "advanced month/year relational mismatch not clean");

    var (lowerDay, lowerMonth, lowerYear) = Fixture.AdvancedNodes(boundary);
    lowerDay.Status = "clean";
    await Throws("ADVANCED_NODE_STATUS_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualAdvancedAdapter().CaptureAsync(
            boundary, new AdvancedReader([lowerDay], [lowerMonth], [lowerYear])));

    var (errorDay, errorMonth, errorYear) = Fixture.AdvancedNodes(boundary);
    errorDay.BuildError = "stale-build-error";
    var errorCapture = await new StatisticReconciliationActualAdvancedAdapter()
        .CaptureAsync(boundary,
            new AdvancedReader([errorDay], [errorMonth], [errorYear]));
    var observedErrorDay = errorCapture.Nodes.Single(node => node.Grain == "DAY");
    False(observedErrorDay.OwnerState.CleanLifecycleValid,
        "CLEAN node with BuildError lifecycle invalid");
    False(observedErrorDay.OwnerState.IsCleanResult,
        "CLEAN node with BuildError not clean");

    var (leaseDay, leaseMonth, leaseYear) = Fixture.AdvancedNodes(boundary);
    leaseDay.LeaseOwner = "stale-worker";
    leaseDay.LeaseExpiresAtUtc =
        new DateTime(2026, 8, 10, 13, 0, 0, DateTimeKind.Utc);
    var leaseCapture = await new StatisticReconciliationActualAdvancedAdapter()
        .CaptureAsync(boundary,
            new AdvancedReader([leaseDay], [leaseMonth], [leaseYear]));
    var observedLeaseDay = leaseCapture.Nodes.Single(node => node.Grain == "DAY");
    False(observedLeaseDay.OwnerState.CleanLifecycleValid,
        "CLEAN node with lease lifecycle invalid");
    False(observedLeaseDay.OwnerState.IsCleanResult,
        "CLEAN node with lease not clean");

    var (upperDay, upperMonth, upperYear) = Fixture.AdvancedNodes(boundary);
    Fixture.SetAdvancedInnerConfigHash(upperDay, Fixture.Hash('A'));
    var upperHashCapture = await new StatisticReconciliationActualAdvancedAdapter()
        .CaptureAsync(boundary,
            new AdvancedReader([upperDay], [upperMonth], [upperYear]));
    var observedUpperDay = upperHashCapture.Nodes.Single(node => node.Grain == "DAY");
    False(observedUpperDay.OwnerState.ValueJsonShapeValid,
        "uppercase inner configHash rejected");
    False(observedUpperDay.OwnerState.IsCleanResult,
        "uppercase inner configHash not clean");

    var methods = typeof(IStatisticReconciliationActualAdvancedOwnerReader).GetMethods();
    Equal(3, methods.Length, "advanced reader method count");
    True(methods.All(method => method.Name.StartsWith("Read", StringComparison.Ordinal)),
        "advanced reader is read-only");

    day.ConfigHash = Fixture.Hash('f');
    await Throws("ADVANCED_OWNER_BOUNDARY_MISMATCH", async () =>
        await new StatisticReconciliationActualAdvancedAdapter()
            .CaptureAsync(boundary, reader));
}

static async Task DiffField()
{
    var boundary = Fixture.DiffBoundary("FIELD");
    Equal("cd351896fa7f9bace44b813b27c1c7297dc2e0609c4fe997202ae5d1eae223fb",
        Fixture.RawSha256("FIELD\nfield-a\nFIELD:field-a"),
        "P9 FIELD owner RowId known vector");
    var rows = Fixture.FieldRows("FIELD");
    var owner = Fixture.DiffOwner(boundary, "FIELD", rows);
    var reader = new DiffReader(owner);
    var capture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, reader);

    Equal(1, reader.ReadCount, "field diff reads");
    True(capture.OwnerState.ResultHashMatches, "field diff result hash");
    True(capture.OwnerState.RowsCanonical, "field diff row ordering");
    True(capture.OwnerState.SourcePinsCanonical, "field diff source pin ordering");
    True(capture.OwnerState.IsCompletedResult, "field diff completed lifecycle");
    True(capture.OwnerState.TypedRowsValid, "field typed rows valid");
    True(capture.OwnerState.ComparisonsConsistent, "field comparisons consistent");
    True(capture.OwnerState.DeclaredTypesMatch, "field declared types match");
    True(capture.OwnerState.IsUsableResult, "field diff usable owner result");
    Equal("NOT_COMPUTED", capture.P10DeltaState,
        "field capture P10 delta state");
    Null(capture.P10Verdict, "field capture P10 verdict");
    True(capture.Rows.All(row => row.P10DeltaState == "NOT_COMPUTED"
                                 && row.P10Verdict is null),
        "field rows keep P9 diff separate from P10 delta");
    True(capture.Rows.All(row => row.Left.TypedShapeValid
                                 && row.Right.TypedShapeValid),
        "field typed shapes");
    SequenceEqual(new[] { "FIELD:field:amount", "FIELD:field:balance" },
        capture.Rows.Select(row => row.Key), "field producer key grammar");
    var redacted = capture.Rows.Single(
        row => row.Key == "FIELD:field:balance");
    True(redacted.P9Equal, "redacted equality semantics");
    Equal("REDACTED", redacted.P9DifferenceKind,
        "redacted difference semantics");
    True(redacted.P9ComparisonConsistent, "redacted comparison consistency");
    var states = capture.Rows.SelectMany(row => new[] { row.Left.State, row.Right.State })
        .ToHashSet(StringComparer.Ordinal);
    True(new[] { "VALUE", "REDACTED", "MISSING", "NULL" }
        .All(states.Contains), "field diff typed states");
    Equal(2, capture.SourcePins
        .Where(pin => pin.Side == "LEFT" && pin.SourceReportId == "report-a")
        .Select(pin => pin.DirectGenerationId)
        .Distinct(StringComparer.Ordinal).Count(),
        "field diff multi-generation source pins");

    var tooManyField = Fixture.DiffOwner(
        boundary, "FIELD", Fixture.TooManyFieldRows());
    Equal(3, tooManyField.Rows.Count, "field producer max-key negative fixture");
    var tooManyFieldCapture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(tooManyField));
    False(tooManyFieldCapture.OwnerState.RowsCanonical,
        "FIELD more than two rows noncanonical");
    False(tooManyFieldCapture.OwnerState.IsUsableResult,
        "FIELD more than two rows unusable");

    var noSourcePins = Fixture.DiffOwner(
        boundary, "FIELD", Fixture.FieldRows("FIELD"));
    noSourcePins.SourcePins.Clear();
    var noSourcePinsCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(noSourcePins));
    False(noSourcePinsCapture.OwnerState.SourcePinsCanonical,
        "nonempty rows require source pins");
    False(noSourcePinsCapture.OwnerState.IsUsableResult,
        "nonempty rows without source pins unusable");

    var noCompletedAt = Fixture.DiffOwner(
        boundary, "FIELD", Fixture.FieldRows("FIELD"));
    noCompletedAt.CompletedAtUtc = null;
    var noCompletedAtCapture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(noCompletedAt));
    False(noCompletedAtCapture.OwnerState.IsCompletedResult,
        "completed owner without completion time is incomplete");
    False(noCompletedAtCapture.OwnerState.IsUsableResult,
        "completed owner without completion time is unusable");

    var lowerStatus = Fixture.DiffOwner(
        boundary, "FIELD", Fixture.FieldRows("FIELD"));
    lowerStatus.Status = "completed";
    await Throws("P9_DIFF_OWNER_ENUM_INVALID", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(lowerStatus)));

    var lowerState = Fixture.DiffOwner(
        boundary, "FIELD", Fixture.FieldRows("FIELD"));
    lowerState.Rows[0].Left.State = "value";
    lowerState.ResultHash = Fixture.DiffResultHash(lowerState);
    await Throws("P9_DIFF_LEFT_STATE_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(lowerState)));

    var methods = typeof(IStatisticReconciliationActualP9DiffOwnerReader).GetMethods();
    Equal(1, methods.Length, "diff reader method count");
    True(methods.All(method => method.Name.StartsWith("Read", StringComparison.Ordinal)),
        "diff reader is read-only");
}

static async Task DiffTableMetric()
{
    var boundary = Fixture.DiffBoundary("TABLE_METRIC");
    Equal("0ac91c0819d9bbef969bf08c84fa294f9a7e09894b2dd47e363556a1a0ac8721",
        Fixture.RawSha256("TABLE_METRIC\nblock-a:metric-a\nROW:row-a"),
        "P9 TABLE_METRIC owner RowId known vector");
    var owner = Fixture.DiffOwner(boundary, "TABLE_METRIC",
        Fixture.TableMetricRows("TABLE_METRIC"));
    owner.ResultHash = Fixture.Hash('f');
    var capture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(owner));

    False(capture.OwnerState.ResultHashMatches,
        "table metric corrupt result hash revealed");
    Equal(owner.ResultHash, capture.StoredResultSha256,
        "table metric stored result hash preserved");
    Equal(Fixture.DiffResultHash(owner), capture.ObservedResultSha256,
        "table metric independent observed result hash");
    True(capture.Rows.SelectMany(row => new[] { row.Left, row.Right })
        .All(value => value.State == "VALUE"),
        "table metric producer contains only value states");
    SequenceEqual(new[] { "ROW:row-a", "ROW:row-b" },
        capture.Rows.Select(row => row.Key), "table metric producer key grammar");
    True(capture.OwnerState.RowsCanonical,
        "table metric producer rows canonical");
    True(capture.OwnerState.TypedRowsValid,
        "table metric producer typed rows valid");
    True(capture.Rows.SelectMany(row => new[] { row.Left, row.Right })
        .Any(value => value.DataType == "DATE" && value.DateValueUtc.HasValue),
        "table metric typed date");
    True(capture.OwnerState.DeclaredTypesMatch,
        "table metric declared types match");
    True(capture.Rows.All(row => row.P10DeltaState == "NOT_COMPUTED"),
        "table metric P10 delta not computed");

    var policyOwner = Fixture.DiffOwner(boundary, "TABLE_METRIC",
        Fixture.MissingCanonicalRows("TABLE_METRIC"));
    policyOwner.LeftDataType = "TEXT";
    policyOwner.RightDataType = "TEXT";
    policyOwner.Rows[0].Left.DataType = "TEXT";
    policyOwner.Rows[0].Right.DataType = "TEXT";
    policyOwner.EmptyPolicy = "AS_MISSING";
    policyOwner.ResultHash = Fixture.DiffResultHash(policyOwner);
    var producerParity = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(policyOwner));
    True(producerParity.OwnerState.TypedRowsValid,
        "AS_MISSING producer canonical empty is valid");
    True(producerParity.OwnerState.IsUsableResult,
        "AS_MISSING producer parity remains usable");

    policyOwner.EmptyPolicy = "INCLUDE";
    policyOwner.ResultHash = Fixture.DiffResultHash(policyOwner);
    var wrongPolicy = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(policyOwner));
    False(wrongPolicy.OwnerState.TypedRowsValid,
        "MISSING canonical empty invalid outside AS_MISSING");
    False(wrongPolicy.OwnerState.IsUsableResult,
        "wrong empty policy owner unusable");

    var emptyNull = Fixture.DiffOwner(boundary, "TABLE_METRIC",
        Fixture.PolicyStateRows("TABLE_METRIC", "EMPTY", "TEXT", null));
    emptyNull.LeftDataType = "TEXT";
    emptyNull.RightDataType = "TEXT";
    emptyNull.ResultHash = Fixture.DiffResultHash(emptyNull);
    var emptyNullCapture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(emptyNull));
    False(emptyNullCapture.OwnerState.TypedRowsValid,
        "EMPTY with null canonical invalid");
    False(emptyNullCapture.OwnerState.IsUsableResult,
        "EMPTY with null canonical unusable");

    var numberBoundary = Fixture.DiffBoundary("FIELD");
    var asMissingNumber = Fixture.DiffOwner(
        numberBoundary, "FIELD",
        Fixture.PolicyStateRows("FIELD", "MISSING", "NUMBER", string.Empty));
    asMissingNumber.EmptyPolicy = "AS_MISSING";
    asMissingNumber.ResultHash = Fixture.DiffResultHash(asMissingNumber);
    var asMissingNumberCapture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(numberBoundary, new DiffReader(asMissingNumber));
    False(asMissingNumberCapture.OwnerState.TypedRowsValid,
        "AS_MISSING NUMBER with empty canonical invalid");
    False(asMissingNumberCapture.OwnerState.IsUsableResult,
        "AS_MISSING NUMBER with empty canonical unusable");

    var missingInclude = Fixture.DiffOwner(
        numberBoundary, "FIELD",
        Fixture.PolicyStateRows("FIELD", "MISSING", "NUMBER", null));
    missingInclude.MissingPolicy = "INCLUDE";
    missingInclude.ResultHash = Fixture.DiffResultHash(missingInclude);
    var missingIncludeCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            numberBoundary, new DiffReader(missingInclude));
    True(missingIncludeCapture.OwnerState.TypedRowsValid,
        "MISSING null valid under INCLUDE");
    True(missingIncludeCapture.OwnerState.IsUsableResult,
        "MISSING null usable under INCLUDE");

    foreach (var missingPolicy in new[] { "REJECT", "AS_ZERO" })
    {
        var forbiddenMissing = Fixture.DiffOwner(
            numberBoundary, "FIELD",
            Fixture.PolicyStateRows("FIELD", "MISSING", "NUMBER", null));
        forbiddenMissing.MissingPolicy = missingPolicy;
        forbiddenMissing.ResultHash = Fixture.DiffResultHash(forbiddenMissing);
        var forbiddenMissingCapture =
            await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
                numberBoundary, new DiffReader(forbiddenMissing));
        False(forbiddenMissingCapture.OwnerState.TypedRowsValid,
            $"MISSING invalid under {missingPolicy}");
        False(forbiddenMissingCapture.OwnerState.IsUsableResult,
            $"MISSING unusable under {missingPolicy}");
    }

    foreach (var emptyPolicy in new[] { "REJECT", "AS_MISSING" })
    {
        var forbiddenEmpty = Fixture.DiffOwner(
            boundary, "TABLE_METRIC",
            Fixture.PolicyStateRows(
                "TABLE_METRIC", "EMPTY", "TEXT", string.Empty));
        forbiddenEmpty.LeftDataType = "TEXT";
        forbiddenEmpty.RightDataType = "TEXT";
        forbiddenEmpty.EmptyPolicy = emptyPolicy;
        forbiddenEmpty.ResultHash = Fixture.DiffResultHash(forbiddenEmpty);
        var forbiddenEmptyCapture =
            await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
                boundary, new DiffReader(forbiddenEmpty));
        False(forbiddenEmptyCapture.OwnerState.TypedRowsValid,
            $"EMPTY invalid under {emptyPolicy}");
        False(forbiddenEmptyCapture.OwnerState.IsUsableResult,
            $"EMPTY unusable under {emptyPolicy}");
    }

    foreach (var emptyType in new[] { "NUMBER", "DATE", "BOOLEAN" })
    {
        var invalidTypedEmpty = Fixture.DiffOwner(
            boundary, "TABLE_METRIC",
            Fixture.PolicyStateRows(
                "TABLE_METRIC", "EMPTY", emptyType, string.Empty));
        invalidTypedEmpty.LeftDataType = emptyType;
        invalidTypedEmpty.RightDataType = emptyType;
        invalidTypedEmpty.EmptyPolicy = "INCLUDE";
        invalidTypedEmpty.ResultHash = Fixture.DiffResultHash(invalidTypedEmpty);
        var invalidTypedEmptyCapture =
            await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
                boundary, new DiffReader(invalidTypedEmpty));
        False(invalidTypedEmptyCapture.OwnerState.TypedRowsValid,
            $"EMPTY {emptyType} invalid");
        False(invalidTypedEmptyCapture.OwnerState.IsUsableResult,
            $"EMPTY {emptyType} unusable");
    }

    foreach (var emptyType in new[] { "TEXT", "CHOICE" })
    {
        var includedEmpty = Fixture.DiffOwner(
            boundary, "TABLE_METRIC",
            Fixture.PolicyStateRows(
                "TABLE_METRIC", "EMPTY", emptyType, string.Empty));
        includedEmpty.LeftDataType = emptyType;
        includedEmpty.RightDataType = emptyType;
        includedEmpty.EmptyPolicy = "INCLUDE";
        includedEmpty.ResultHash = Fixture.DiffResultHash(includedEmpty);
        var includedEmptyCapture =
            await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
                boundary, new DiffReader(includedEmpty));
        True(includedEmptyCapture.OwnerState.TypedRowsValid,
            $"EMPTY {emptyType} valid under INCLUDE");
        True(includedEmptyCapture.OwnerState.IsUsableResult,
            $"EMPTY {emptyType} usable under INCLUDE");
    }

    var choiceValueEmpty = Fixture.DiffOwner(boundary, "TABLE_METRIC",
        Fixture.RowLabelChoiceRows("TABLE_METRIC"));
    choiceValueEmpty.LeftDataType = "CHOICE";
    choiceValueEmpty.RightDataType = "CHOICE";
    foreach (var value in new[]
             {
                 choiceValueEmpty.Rows[0].Left, choiceValueEmpty.Rows[0].Right
             })
    {
        value.DataType = "CHOICE";
        value.CanonicalValue = string.Empty;
        value.ChoiceIds.Clear();
    }
    choiceValueEmpty.ResultHash = Fixture.DiffResultHash(choiceValueEmpty);
    var choiceValueEmptyCapture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(choiceValueEmpty));
    False(choiceValueEmptyCapture.OwnerState.TypedRowsValid,
        "CHOICE VALUE empty invalid");
    False(choiceValueEmptyCapture.OwnerState.IsUsableResult,
        "CHOICE VALUE empty unusable");

    var textValueEmptyBoundary = Fixture.DiffBoundary("FIELD");
    var textValueEmpty = Fixture.DiffOwner(textValueEmptyBoundary, "FIELD",
        Fixture.RowLabelChoiceRows("FIELD"));
    textValueEmpty.LeftDataType = "TEXT";
    textValueEmpty.RightDataType = "TEXT";
    foreach (var value in new[]
             {
                 textValueEmpty.Rows[0].Left, textValueEmpty.Rows[0].Right
             })
    {
        value.CanonicalValue = string.Empty;
        value.ChoiceIds.Clear();
    }
    textValueEmpty.ResultHash = Fixture.DiffResultHash(textValueEmpty);
    var textValueEmptyCapture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(textValueEmptyBoundary, new DiffReader(textValueEmpty));
    False(textValueEmptyCapture.OwnerState.TypedRowsValid,
        "TEXT VALUE empty invalid");
    False(textValueEmptyCapture.OwnerState.IsUsableResult,
        "TEXT VALUE empty unusable");

    var arbitraryKey = Fixture.DiffOwner(
        boundary, "TABLE_METRIC", Fixture.TableMetricRows("TABLE_METRIC"));
    arbitraryKey.Rows[0].Key = "arbitrary-key";
    arbitraryKey.Rows[0].RowId = Fixture.RawSha256(
        $"TABLE_METRIC\n{arbitraryKey.LeftConceptKey}\narbitrary-key");
    arbitraryKey.ResultHash = Fixture.DiffResultHash(arbitraryKey);
    var arbitraryKeyCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(arbitraryKey));
    False(arbitraryKeyCapture.OwnerState.RowsCanonical,
        "arbitrary TABLE_METRIC key noncanonical");
    False(arbitraryKeyCapture.OwnerState.IsUsableResult,
        "arbitrary TABLE_METRIC key unusable");

    var lowerType = Fixture.DiffOwner(boundary, "TABLE_METRIC",
        Fixture.TableMetricRows("TABLE_METRIC"));
    lowerType.Rows[0].Left.DataType = "date";
    lowerType.ResultHash = Fixture.DiffResultHash(lowerType);
    await Throws("P9_DIFF_LEFT_DATA_TYPE_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(lowerType)));
}

static async Task DiffRowLabel()
{
    var boundary = Fixture.DiffBoundary("ROW_LABEL");
    Equal("1437a644a87e5b5a57a62a453db2468b3198c3fb5c1af1d47c23e9f4640296ac",
        Fixture.RawSha256("ROW_LABEL\nblock-a:label-a\nROW:row-a"),
        "P9 ROW_LABEL owner RowId known vector");
    var rows = Fixture.RowLabelRows("ROW_LABEL").AsEnumerable().Reverse().ToList();
    var owner = Fixture.DiffOwner(boundary, "ROW_LABEL", rows);
    owner.SourcePins.Reverse();
    owner.ResultHash = Fixture.DiffResultHash(owner);
    var reader = new DiffReader(owner);
    var capture = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, reader);

    False(capture.OwnerState.RowsCanonical,
        "row label physical row ordering revealed");
    False(capture.OwnerState.SourcePinsCanonical,
        "row label physical source pin ordering revealed");
    True(capture.OwnerState.ResultHashMatches,
        "row label hash covers owner order exactly");
    SequenceEqual(owner.Rows.Select(row => row.Key),
        capture.Rows.Select(row => row.Key), "row label owner order preserved");
    Equal("NOT_COMPUTED", capture.P10DeltaState,
        "row label P10 delta state");
    Null(capture.P10Verdict, "row label P10 verdict");

    var rowLabelParityOwner = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Fixture.RowLabelChoiceRows("ROW_LABEL"));
    var rowLabelParity = await new StatisticReconciliationActualP9DiffAdapter()
        .CaptureAsync(boundary, new DiffReader(rowLabelParityOwner));
    True(rowLabelParity.OwnerState.TypedRowsValid,
        "ROW_LABEL ChoiceIds producer value typed valid");
    True(rowLabelParity.OwnerState.IsUsableResult,
        "ROW_LABEL ChoiceIds producer value usable");
    var labelValue = rowLabelParity.Rows[0].Left;
    Equal("TEXT", labelValue.DataType, "ROW_LABEL producer data type");
    Equal("label-a\u001flabel-b", labelValue.CanonicalValue,
        "ROW_LABEL producer canonical labels");
    SequenceEqual(new[] { "label-a", "label-b" }, labelValue.ChoiceIds,
        "ROW_LABEL producer sorted ChoiceIds");
    Null(labelValue.NumericValue, "ROW_LABEL producer numeric channel");
    Null(labelValue.BooleanValue, "ROW_LABEL producer boolean channel");
    Null(labelValue.DateValueUtc, "ROW_LABEL producer date channel");
    Equal("ROW:row-a", rowLabelParity.Rows[0].Key,
        "ROW_LABEL producer key grammar");

    var rowLabelNonValueOwner = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Fixture.RowLabelRows("ROW_LABEL"));
    var rowLabelNonValue =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(rowLabelNonValueOwner));
    True(rowLabelNonValue.OwnerState.RowsCanonical,
        "ROW_LABEL non-value producer keys canonical");
    True(rowLabelNonValue.OwnerState.TypedRowsValid,
        "ROW_LABEL REDACTED and absent MISSING typed valid");
    True(rowLabelNonValue.OwnerState.IsUsableResult,
        "ROW_LABEL REDACTED and absent MISSING usable");
    True(rowLabelNonValue.Rows
        .SelectMany(row => new[] { row.Left, row.Right })
        .All(value => value.State is "REDACTED" or "MISSING"
                      && value.CanonicalValue is null),
        "ROW_LABEL non-value producer states are absent or redacted");

    foreach (var invalid in
             new (string State, string? Canonical, string EmptyPolicy)[]
             {
                 ("NULL", null, "INCLUDE"),
                 ("EMPTY", string.Empty, "INCLUDE"),
                 ("MISSING", string.Empty, "AS_MISSING")
             })
    {
        var invalidRowLabel = Fixture.DiffOwner(
            boundary, "ROW_LABEL",
            Fixture.PolicyStateRows(
                "ROW_LABEL", invalid.State, "TEXT", invalid.Canonical));
        invalidRowLabel.EmptyPolicy = invalid.EmptyPolicy;
        invalidRowLabel.ResultHash = Fixture.DiffResultHash(invalidRowLabel);
        var invalidRowLabelCapture =
            await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
                boundary, new DiffReader(invalidRowLabel));
        False(invalidRowLabelCapture.OwnerState.TypedRowsValid,
            $"ROW_LABEL {invalid.State} producer state invalid");
        False(invalidRowLabelCapture.OwnerState.IsUsableResult,
            $"ROW_LABEL {invalid.State} producer state unusable");
    }

    var lowerSide = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Fixture.RowLabelRows("ROW_LABEL"));
    lowerSide.SourcePins[0].Side = "left";
    await Throws("P9_DIFF_SOURCE_SIDE_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(lowerSide)));

    var upperRowId = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Fixture.RowLabelChoiceRows("ROW_LABEL"));
    upperRowId.Rows[0].RowId = upperRowId.Rows[0].RowId.ToUpperInvariant();
    upperRowId.ResultHash = Fixture.DiffResultHash(upperRowId);
    await Throws("P9_DIFF_ROW_ID_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(upperRowId)));

    var upperPayloadHash = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Fixture.RowLabelChoiceRows("ROW_LABEL"));
    upperPayloadHash.SourcePins[0].SourcePayloadHash = Fixture.Hash('A');
    await Throws("P9_DIFF_SOURCE_PAYLOAD_SHA256_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(upperPayloadHash)));

    var upperGeneration = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Fixture.RowLabelChoiceRows("ROW_LABEL"));
    upperGeneration.SourcePins[0].DirectGenerationId =
        upperGeneration.SourcePins[0].DirectGenerationId.ToUpperInvariant();
    await Throws("P9_DIFF_DIRECT_GENERATION_ID_NON_CANONICAL_CASE", async () =>
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(upperGeneration)));

    var fieldBoundary = Fixture.DiffBoundary("FIELD");
    var genericTextOwner = Fixture.DiffOwner(
        fieldBoundary, "FIELD", Fixture.RowLabelRows("FIELD"));
    genericTextOwner.LeftDataType = "TEXT";
    genericTextOwner.RightDataType = "TEXT";
    genericTextOwner.ResultHash = Fixture.DiffResultHash(genericTextOwner);
    var genericTextCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            fieldBoundary, new DiffReader(genericTextOwner));
    True(genericTextCapture.OwnerState.TypedRowsValid,
        "generic FIELD TEXT without ChoiceIds remains valid");
    True(genericTextCapture.OwnerState.IsUsableResult,
        "generic FIELD TEXT without ChoiceIds remains usable");
    True(genericTextCapture.Rows.SelectMany(row => new[] { row.Left, row.Right })
        .All(value => value.ChoiceIds.Length == 0),
        "generic FIELD TEXT has no ChoiceIds");

    var zeroRowsAndPins = Fixture.DiffOwner(
        fieldBoundary, "FIELD", Array.Empty<P9StatisticDiffResultRow>());
    zeroRowsAndPins.SourcePins.Clear();
    zeroRowsAndPins.ResultHash = Fixture.DiffResultHash(zeroRowsAndPins);
    var zeroRowsAndPinsCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            fieldBoundary, new DiffReader(zeroRowsAndPins));
    True(zeroRowsAndPinsCapture.OwnerState.RowsCanonical,
        "zero rows remain canonical");
    True(zeroRowsAndPinsCapture.OwnerState.SourcePinsCanonical,
        "zero rows with zero source pins canonical");
    True(zeroRowsAndPinsCapture.OwnerState.IsUsableResult,
        "zero rows with zero source pins usable");

    var incompatibleConcept = Fixture.DiffOwner(
        fieldBoundary, "FIELD", Array.Empty<P9StatisticDiffResultRow>());
    incompatibleConcept.RightConceptKind = "TABLE_METRIC";
    incompatibleConcept.ResultHash = Fixture.DiffResultHash(incompatibleConcept);
    var incompatibleConceptCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            fieldBoundary, new DiffReader(incompatibleConcept));
    False(incompatibleConceptCapture.OwnerState.OwnerMetadataValid,
        "zero-row incompatible concept metadata invalid");
    False(incompatibleConceptCapture.OwnerState.IsUsableResult,
        "zero-row incompatible concept fail-closed");

    var incompatibleType = Fixture.DiffOwner(
        fieldBoundary, "FIELD", Array.Empty<P9StatisticDiffResultRow>());
    incompatibleType.RightDataType = "TEXT";
    incompatibleType.ResultHash = Fixture.DiffResultHash(incompatibleType);
    var incompatibleTypeCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            fieldBoundary, new DiffReader(incompatibleType));
    False(incompatibleTypeCapture.OwnerState.OwnerMetadataValid,
        "zero-row incompatible data type metadata invalid");
    False(incompatibleTypeCapture.OwnerState.IsUsableResult,
        "zero-row incompatible data type fail-closed");

    var asZeroNonNumber = Fixture.DiffOwner(
        boundary, "ROW_LABEL", Array.Empty<P9StatisticDiffResultRow>());
    asZeroNonNumber.MissingPolicy = "AS_ZERO";
    asZeroNonNumber.ResultHash = Fixture.DiffResultHash(asZeroNonNumber);
    var asZeroNonNumberCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            boundary, new DiffReader(asZeroNonNumber));
    False(asZeroNonNumberCapture.OwnerState.OwnerMetadataValid,
        "zero-row AS_ZERO non-number metadata invalid");
    False(asZeroNonNumberCapture.OwnerState.IsUsableResult,
        "zero-row AS_ZERO non-number fail-closed");

    var invalidPeriod = Fixture.DiffOwner(
        fieldBoundary, "FIELD", Array.Empty<P9StatisticDiffResultRow>());
    invalidPeriod.LeftPeriodJson =
        "{\"mode\":\"INVALID\",\"periodKey\":\"2026-08\"," +
        "\"periodKeyFrom\":null,\"periodKeyTo\":null}";
    invalidPeriod.ResultHash = Fixture.DiffResultHash(invalidPeriod);
    var invalidPeriodCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            fieldBoundary, new DiffReader(invalidPeriod));
    False(invalidPeriodCapture.OwnerState.PeriodsValid,
        "zero-row invalid period mode state invalid");
    False(invalidPeriodCapture.OwnerState.IsUsableResult,
        "zero-row invalid period mode fail-closed");

    var missingPeriodProperty = Fixture.DiffOwner(
        fieldBoundary, "FIELD", Array.Empty<P9StatisticDiffResultRow>());
    missingPeriodProperty.LeftPeriodJson =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\"," +
        "\"periodKeyFrom\":null}";
    missingPeriodProperty.ResultHash = Fixture.DiffResultHash(missingPeriodProperty);
    var missingPeriodPropertyCapture =
        await new StatisticReconciliationActualP9DiffAdapter().CaptureAsync(
            fieldBoundary, new DiffReader(missingPeriodProperty));
    False(missingPeriodPropertyCapture.OwnerState.PeriodsValid,
        "period missing required null property invalid");
    False(missingPeriodPropertyCapture.OwnerState.IsUsableResult,
        "period missing required null property fail-closed");

    owner.ConfigHash = Fixture.Hash('f');
    await Throws("P9_DIFF_OWNER_BOUNDARY_MISMATCH", async () =>
        await new StatisticReconciliationActualP9DiffAdapter()
            .CaptureAsync(boundary, reader));
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{name}: expected={expected} actual={actual}");
}

static void True(bool value, string name)
{
    if (!value)
        throw new InvalidOperationException($"{name}: expected true");
}

static void False(bool value, string name)
{
    if (value)
        throw new InvalidOperationException($"{name}: expected false");
}

static void Null(object? value, string name)
{
    if (value is not null)
        throw new InvalidOperationException($"{name}: expected null");
}

static void NotNull(object? value, string name)
{
    if (value is null)
        throw new InvalidOperationException($"{name}: expected value");
}

static void NotEqual<T>(T left, T right, string name)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
        throw new InvalidOperationException($"{name}: expected distinct values");
}

static void Contains(string expected, string actual, string name)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"{name}: expected fragment={expected} actual={actual}");
}

static void SequenceEqual<T>(
    IEnumerable<T> expected,
    IEnumerable<T> actual,
    string name)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{name}: sequence mismatch");
}

static async Task Throws(string reason, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationActualObservationException exception)
        when (exception.Reason == reason)
    {
        return;
    }
    throw new InvalidOperationException($"expected failure {reason}");
}

internal static class Fixture
{
    internal const string BasicCandidateRaw =
        "7955d4c0fb1aa03b5d28484b15f321752b16983996f3b5c5428d9192ff67a509";
    internal const string BasicCandidateSemantic =
        "c7120bd77d338006e8df117c83718ee694aa407167a7ca214e2f71dc2f2da9f1";
    internal const string BasicStageLock =
        "38d13a94dfe863625ae54396ceee6beccce9bf9de85cd4d26cb0e2e730fdf1ad";
    internal const string AdvancedCandidateRaw =
        "c3ebff7c0cfa4ce62003fb83e0cfc75ba9a9fd1419cc00b9df3836cf981085d3";
    internal const string AdvancedCandidateSemantic =
        "d0b33a7ed334f0412488618e1ed23375fc72657fa3509adeaceec76013460c0f";
    internal const string AdvancedStageLock =
        "505272c7c32544a7363d03c5e8b087bfcfac00aa3a7cef2e89ff7d45c7ecc5bc";
    internal const string DiffCandidateRaw =
        "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68";
    internal const string DiffCandidateSemantic =
        "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36";
    internal const string DiffStageLock =
        "e237f0e260f0ba2704ccb830a9b3aca1bceb7c88eaca52dc679f6b08b9126397";

    private static readonly JsonSerializerOptions Web =
        new(JsonSerializerDefaults.Web);

    internal static ActualBasicOwnerBoundary BasicBoundary(string mode)
    {
        var suffix = mode.ToLowerInvariant().Replace('_', '-');
        var flow = StatisticReconciliationActualBasicModes.IsFlow(mode);
        return new ActualBasicOwnerBoundary(
            $"snapshot-{suffix}",
            "work-a",
            "assignment-a",
            "form-a",
            mode,
            flow ? "flow-instance-a" : null,
            mode == StatisticReconciliationActualBasicModes.FlowStep ? "step-a" : null,
            mode == StatisticReconciliationActualBasicModes.FlowBranch ? "branch-a" : null,
            flow ? "COMPLETED" : null,
            Hash('1'),
            "basic-config-a",
            "basic-config-version-a",
            3,
            7,
            Hash('2'),
            ImmutableArray.Create("dynamic-form:3", "labels:7"),
            "p10-fixture-chain",
            "P9-04",
            2,
            BasicCandidateRaw,
            BasicCandidateSemantic,
            BasicStageLock);
    }

    internal static WorkAssignmentBasicSummarySnapshot BasicOwner(
        ActualBasicOwnerBoundary boundary,
        bool dirty = false,
        bool innerMetaMatches = true)
    {
        var owner = new WorkAssignmentBasicSummarySnapshot
        {
            Id = boundary.OwnerSnapshotId,
            WorkId = boundary.WorkId,
            ScopeAssignmentId = boundary.ScopeAssignmentId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            RequestHash = boundary.RequestHash,
            RequestJson =
                $"{{ \"mode\" : \"{boundary.SourceScopeMode}\", \"include\" : [ \"values\" ] }}",
            SourceScopeMode = boundary.SourceScopeMode,
            SourceFlowInstanceId = boundary.SourceFlowInstanceId,
            SourceFlowStepId = boundary.SourceFlowStepId,
            SourceFlowBranchId = boundary.SourceFlowBranchId,
            SourceFlowEffectiveStatus = boundary.SourceFlowEffectiveStatus,
            SourceAssignmentIds = ["assignment-a", "assignment-b"],
            SourceReportIds = ["report-a", "report-b"],
            SourceSignatureHash = Hash('6'),
            ConfigId = boundary.ConfigId,
            ConfigVersionId = boundary.ConfigVersionId,
            ConfigVersionNo = boundary.ConfigVersionNo,
            ConfigRevision = boundary.ConfigRevision,
            ConfigHash = boundary.ConfigSha256,
            ConfigDependencyPins = boundary.ConfigDependencyPins.ToList(),
            CandidateChainId = boundary.CandidateChainId,
            CandidatePromptId = boundary.CandidatePromptId,
            CandidateStage = boundary.CandidateStage,
            CandidateCatalogRawSha256 = boundary.CandidateCatalogRawSha256,
            CandidateCatalogSemanticSha256 = boundary.CandidateCatalogSemanticSha256,
            CandidateStageLockSha256 = boundary.CandidateStageLockSha256,
            SnapshotDirty = dirty,
            SnapshotDirtyAtUtc = dirty ? Utc(10, 10) : null,
            SnapshotRefreshedAtUtc = Utc(10, 9),
            RefreshStatus = WorkAssignmentBasicSummaryRefreshStatuses.Done,
            RefreshJobId = "basic-job-a",
            RefreshCorrelationId = "basic-correlation-a",
            RefreshQueuedAtUtc = Utc(10, 8),
            RefreshStartedAtUtc = Utc(10, 8).AddMinutes(1),
            RefreshFinishedAtUtc = Utc(10, 9),
            CreatedAtUtc = Utc(10, 7),
            UpdatedAtUtc = Utc(10, 10),
            CreatedByUserId = "user-a",
            UpdatedByUserId = "user-a"
        };
        var metaMode = innerMetaMatches ? owner.SourceScopeMode : "FLOW_STEP";
        var payload = new
        {
            v = 9,
            meta = new
            {
                snapshotId = owner.Id,
                scopeAssignmentId = owner.ScopeAssignmentId,
                dynamicFormTemplateId = owner.DynamicFormTemplateId,
                sourceScopeMode = metaMode,
                sourceFlowInstanceId = owner.SourceFlowInstanceId,
                sourceFlowStepId = owner.SourceFlowStepId,
                sourceFlowBranchId = owner.SourceFlowBranchId,
                sourceFlowEffectiveStatus = owner.SourceFlowEffectiveStatus,
                sourceSignatureHash = owner.SourceSignatureHash,
                configId = owner.ConfigId,
                configVersionId = owner.ConfigVersionId,
                configVersionNo = owner.ConfigVersionNo,
                configRevision = owner.ConfigRevision,
                configHash = owner.ConfigHash,
                candidateChainId = owner.CandidateChainId,
                candidatePromptId = owner.CandidatePromptId,
                candidateStage = owner.CandidateStage,
                candidateCatalogRawSha256 = owner.CandidateCatalogRawSha256,
                candidateCatalogSemanticSha256 = owner.CandidateCatalogSemanticSha256,
                candidateStageLockSha256 = owner.CandidateStageLockSha256
            },
            fields = new[] { new { fieldKey = "field-total", value = 7 } },
            tb = new[] { new { b = "block-a", values = new[] { 1, 2 } } }
        };
        owner.SnapshotJson = "\n" + JsonSerializer.Serialize(payload, Web) + " ";
        return owner;
    }

    internal static ActualAdvancedOwnerBoundary AdvancedBoundary()
        => new(
            "work-a",
            "assignment-a",
            "form-a",
            "section-a",
            "advanced-config-a",
            "advanced-config-version-a",
            5,
            11,
            Hash('a'),
            ImmutableArray.Create("dynamic-form:5", "summary-token:11"),
            "UTC_GREGORIAN",
            "p10-fixture-chain",
            "P9-05",
            3,
            AdvancedCandidateRaw,
            AdvancedCandidateSemantic,
            AdvancedStageLock,
            ImmutableArray.Create("advanced-day-a"),
            ImmutableArray.Create("advanced-month-a"),
            ImmutableArray.Create("advanced-year-a"));

    internal static (
        WorkAssignmentAdvancedSummaryDayNode Day,
        WorkAssignmentAdvancedSummaryMonthNode Month,
        WorkAssignmentAdvancedSummaryYearNode Year) AdvancedNodes(
        ActualAdvancedOwnerBoundary boundary)
    {
        var day = new WorkAssignmentAdvancedSummaryDayNode
        {
            Id = boundary.DayNodeIds[0],
            DayKey = "2026-08-01"
        };
        PopulateAdvanced(day, boundary, "DAY", day.DayKey,
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ["report-a", "report-b"], [], dirty: false);

        var monthInputs = Enumerable.Range(1, 31)
            .Select(dayNumber => $"2026-08-{dayNumber:00}").ToList();
        var month = new WorkAssignmentAdvancedSummaryMonthNode
        {
            Id = boundary.MonthNodeIds[0],
            MonthKey = "2026-08",
            YearKey = "2026"
        };
        PopulateAdvanced(month, boundary, "MONTH", month.MonthKey,
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            [], monthInputs, dirty: true);

        var yearInputs = Enumerable.Range(1, 12)
            .Select(monthNumber => $"2026-{monthNumber:00}").ToList();
        var year = new WorkAssignmentAdvancedSummaryYearNode
        {
            Id = boundary.YearNodeIds[0],
            YearKey = "2026"
        };
        PopulateAdvanced(year, boundary, "YEAR", year.YearKey,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            [], yearInputs, dirty: false);
        return (day, month, year);
    }

    internal static void SetMonthYearKey(
        WorkAssignmentAdvancedSummaryMonthNode month,
        string yearKey)
    {
        var oldToken = $"\"yearKey\":\"{month.YearKey}\"";
        var newToken = $"\"yearKey\":\"{yearKey}\"";
        if (month.ValueJson.Split(oldToken, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("month year key fixture token mismatch");
        month.YearKey = yearKey;
        month.ValueJson = month.ValueJson.Replace(
            oldToken, newToken, StringComparison.Ordinal);
        month.ValueHash = RawSha256(month.ValueJson);
    }

    internal static void SetAdvancedInnerConfigHash(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        string configSha256)
    {
        var oldToken = $"\"configHash\":\"{node.ConfigHash}\"";
        var newToken = $"\"configHash\":\"{configSha256}\"";
        if (node.ValueJson.Split(oldToken, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException(
                "advanced config hash fixture token mismatch");
        node.ValueJson = node.ValueJson.Replace(
            oldToken, newToken, StringComparison.Ordinal);
        node.ValueHash = RawSha256(node.ValueJson);
    }

    private static void PopulateAdvanced(
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        ActualAdvancedOwnerBoundary boundary,
        string grain,
        string grainKey,
        DateTime start,
        DateTime end,
        List<string> sourceIds,
        List<string> inputKeys,
        bool dirty)
    {
        node.WorkId = boundary.WorkId;
        node.AssignmentId = boundary.AssignmentId;
        node.DynamicFormTemplateId = boundary.DynamicFormTemplateId;
        node.SectionId = boundary.SectionId;
        node.ConfigId = boundary.ConfigId;
        node.ConfigVersionId = boundary.ConfigVersionId;
        node.ConfigVersionNo = boundary.ConfigVersionNo;
        node.ConfigRevision = boundary.ConfigRevision;
        node.ConfigHash = boundary.ConfigSha256;
        node.DependencyPins = boundary.DependencyPins.ToList();
        node.TimeAxis = boundary.TimeAxis;
        node.CandidateChainId = boundary.CandidateChainId;
        node.CandidatePromptId = boundary.CandidatePromptId;
        node.CandidateStage = boundary.CandidateStage;
        node.CandidateCatalogRawSha256 = boundary.CandidateCatalogRawSha256;
        node.CandidateCatalogSemanticSha256 = boundary.CandidateCatalogSemanticSha256;
        node.CandidateStageLockSha256 = boundary.CandidateStageLockSha256;
        node.Grain = grain;
        node.GrainKey = grainKey;
        node.WindowStartUtc = start;
        node.WindowEndExclusiveUtc = end;
        node.Status = dirty
            ? WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Dirty
            : WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean;
        node.IsDirty = dirty;
        node.DirtyReason = dirty ? "upstream-day-changed" : null;
        node.SourceSignatureHash = Hash(grain switch
        {
            "DAY" => 'b',
            "MONTH" => 'c',
            _ => 'd'
        });
        node.SourceReportCount = 2;
        node.SourceReportIds = sourceIds;
        node.InputNodeKeys = inputKeys;
        node.BuiltAtUtc = Utc(10, 11);
        node.BuildJobId = "advanced-job-a";
        node.BuildCorrelationId = "advanced-correlation-a";
        node.BuildCommandId = "advanced-command-a";
        node.BuildRequestHash = Hash('e');
        node.BuildReceiptId = "advanced-receipt-a";
        node.QuotaLedgerId = "advanced-quota-a";
        node.BuildAttemptNo = 1;
        node.FenceToken = 3;
        node.CreatedAtUtc = Utc(10, 7);
        node.UpdatedAtUtc = Utc(10, 11);
        node.CreatedByUserId = "user-a";
        node.UpdatedByUserId = "user-a";
        node.ValueJson = AdvancedValueJson(boundary, node, grain, grainKey,
            start, end, sourceIds.Count, inputKeys.Count);
        node.ValueHash = RawSha256(node.ValueJson);
    }

    private static string AdvancedValueJson(
        ActualAdvancedOwnerBoundary boundary,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node,
        string grain,
        string grainKey,
        DateTime start,
        DateTime end,
        int sourceIdCount,
        int inputCount)
    {
        var dayKey = node is WorkAssignmentAdvancedSummaryDayNode day
            ? day.DayKey : null;
        var monthKey = node is WorkAssignmentAdvancedSummaryMonthNode month
            ? month.MonthKey : null;
        var yearKey = node switch
        {
            WorkAssignmentAdvancedSummaryMonthNode monthNode => monthNode.YearKey,
            WorkAssignmentAdvancedSummaryYearNode yearNode => yearNode.YearKey,
            _ => null
        };
        var kind = $"ADVANCED_SUMMARY_{grain}_NODE_V1";
        var payload = new
        {
            schemaVersion = 1,
            kind,
            generatedAtUtc = Utc(10, 11),
            configId = grain == "DAY"
                ? boundary.ConfigVersionId
                : boundary.ConfigId,
            configHash = boundary.ConfigSha256,
            grain,
            grainKey,
            dayKey,
            monthKey,
            yearKey,
            windowStartUtc = start,
            windowEndExclusiveUtc = end,
            sourceScopeMode = grain == "DAY" ? "FLOW_STEP" : "FLOW_FINAL",
            sourceFlowInstanceId = "flow-instance-a",
            sourceFlowStepId = grain == "DAY" ? "step-a" : null,
            sourceFlowBranchId = (string?)null,
            sourceFlowEffectiveStatus = "COMPLETED",
            sourceAssignmentCount = 1,
            sourceReportCount = 2,
            sectionReportCount = 2,
            sectionFieldCount = 1,
            targetFieldCount = 1,
            inputNodeCount = inputCount,
            warnings = new[] { "owner-warning" },
            fields = new[]
            {
                new
                {
                    fieldId = "field-amount",
                    fieldKey = "amount",
                    label = "Amount",
                    dataType = "NUMBER",
                    method = "SUM",
                    valueCount = 2,
                    sourceReportCount = sourceIdCount == 0 ? 2 : sourceIdCount,
                    result = new { sum = 42m, unit = "VND" },
                    sampleValues = new[] { "20", "22" }
                }
            }
        };
        return "\n" + JsonSerializer.Serialize(payload, Web) + " ";
    }

    internal static ActualP9DiffOwnerBoundary DiffBoundary(string conceptKind)
    {
        var suffix = conceptKind.ToLowerInvariant().Replace('_', '-');
        return new ActualP9DiffOwnerBoundary(
            $"diff-result-{suffix}",
            $"diff-run-{suffix}",
            "work-a",
            "assignment-a",
            "form-a",
            "diff-config-a",
            "diff-config-version-a",
            6,
            13,
            Hash('1'),
            ImmutableArray.Create("diff-policy:13", "dynamic-form:6"),
            "p10-fixture-chain",
            "P9-06",
            4,
            DiffCandidateRaw,
            DiffCandidateSemantic,
            DiffStageLock);
    }

    internal static WorkReportStatisticDiffResult DiffOwner(
        ActualP9DiffOwnerBoundary boundary,
        string conceptKind,
        IReadOnlyList<P9StatisticDiffResultRow> rows)
    {
        var owner = new WorkReportStatisticDiffResult
        {
            Id = boundary.ResultId,
            RunId = boundary.RunId,
            WorkId = boundary.WorkId,
            AssignmentId = boundary.AssignmentId,
            DynamicFormTemplateId = boundary.DynamicFormTemplateId,
            ConfigId = boundary.ConfigId,
            ConfigVersionId = boundary.ConfigVersionId,
            ConfigVersionNo = boundary.ConfigVersionNo,
            ConfigRevision = boundary.ConfigRevision,
            ConfigHash = boundary.ConfigSha256,
            DependencyPins = boundary.DependencyPins.ToList(),
            CandidateChainId = boundary.CandidateChainId,
            CandidatePromptId = boundary.CandidatePromptId,
            CandidateStage = boundary.CandidateStage,
            CandidateCatalogRawSha256 = boundary.CandidateCatalogRawSha256,
            CandidateCatalogSemanticSha256 = boundary.CandidateCatalogSemanticSha256,
            CandidateStageLockSha256 = boundary.CandidateStageLockSha256,
            LeftConceptKind = conceptKind,
            LeftConceptKey = ConceptKey(conceptKind),
            LeftConceptCode = "concept-code",
            LeftDataType = DeclaredDataType(conceptKind),
            LeftPeriodJson =
                "{ \"mode\" : \"EXACT\", \"periodKey\" : \"2026-08\", " +
                "\"periodKeyFrom\" : null, \"periodKeyTo\" : null }",
            RightConceptKind = conceptKind,
            RightConceptKey = conceptKind == "FIELD"
                ? "field:balance"
                : ConceptKey(conceptKind),
            RightConceptCode = "concept-code",
            RightDataType = DeclaredDataType(conceptKind),
            RightPeriodJson =
                "{ \"mode\" : \"EXACT\", \"periodKey\" : \"2026-07\", " +
                "\"periodKeyFrom\" : null, \"periodKeyTo\" : null }",
            Direction = "LEFT_TO_RIGHT",
            MissingPolicy = "INCLUDE",
            EmptyPolicy = "INCLUDE",
            TimeAxis = "UTC_GREGORIAN",
            CommandId = "diff-command-a",
            RequestHash = Hash('2'),
            ReceiptId = "diff-receipt-a",
            RequestedByUserId = "user-a",
            Status = P9StatisticDiffResultStatuses.Completed,
            JobId = "diff-job-a",
            AttemptNo = 1,
            FenceToken = 5,
            SourcePins = SourcePins(),
            Rows = rows.ToList(),
            TotalRowCount = rows.Count,
            EqualRowCount = rows.Count(row => row.Equal),
            ChangedRowCount = rows.Count(row => !row.Equal),
            IsCurrent = true,
            IsFresh = true,
            IsDirty = false,
            CompletedAtUtc = Utc(10, 12),
            ExpiresAtUtc = null,
            CreatedAtUtc = Utc(10, 11),
            UpdatedAtUtc = Utc(10, 12),
            CreatedByUserId = "user-a",
            UpdatedByUserId = "user-a"
        };
        owner.ResultHash = DiffResultHash(owner);
        return owner;
    }

    internal static List<P9StatisticDiffResultRow> FieldRows(string conceptKind)
        =>
        [
            Row(conceptKind, 0, ProducerKey(conceptKind, 0),
                ValueNumber(12), State("NULL", "NUMBER"),
                false, "STATE_CHANGED"),
            Row(conceptKind, 1, ProducerKey(conceptKind, 1),
                State("REDACTED", "NUMBER"), State("MISSING", "NUMBER"),
                true, "REDACTED")
        ];

    internal static List<P9StatisticDiffResultRow> TableMetricRows(string conceptKind)
        =>
        [
            Row(conceptKind, 0, ProducerKey(conceptKind, 0),
                ValueDate(Utc(2)), ValueDate(Utc(1)), false, "VALUE_CHANGED"),
            Row(conceptKind, 1, ProducerKey(conceptKind, 1),
                ValueDate(Utc(3)), ValueDate(Utc(3)), true, "UNCHANGED")
        ];

    internal static List<P9StatisticDiffResultRow> PolicyStateRows(
        string conceptKind,
        string state,
        string dataType,
        string? canonicalValue)
        =>
        [
            Row(conceptKind, 0, ProducerKey(conceptKind, 0),
                State(state, dataType, canonicalValue),
                State(state, dataType, canonicalValue), true, "UNCHANGED")
        ];

    internal static List<P9StatisticDiffResultRow> TooManyFieldRows()
        =>
        [
            Row("FIELD", 0, ProducerKey("FIELD", 0),
                ValueNumber(1), ValueNumber(1), true, "UNCHANGED", 0),
            Row("FIELD", 1, ProducerKey("FIELD", 1),
                ValueNumber(2), ValueNumber(2), true, "UNCHANGED", 0),
            Row("FIELD", 2, ProducerKey("FIELD", 1),
                ValueNumber(3), ValueNumber(3), true, "UNCHANGED", 0)
        ];

    internal static List<P9StatisticDiffResultRow> MissingCanonicalRows(
        string conceptKind)
        =>
        [
            Row(conceptKind, 0, ProducerKey(conceptKind, 0),
                State("MISSING", DeclaredDataType(conceptKind), string.Empty),
                State("MISSING", DeclaredDataType(conceptKind), string.Empty),
                true, "UNCHANGED")
        ];

    internal static List<P9StatisticDiffResultRow> RowLabelChoiceRows(
        string conceptKind)
        =>
        [
            Row(conceptKind, 0, ProducerKey(conceptKind, 0),
                RowLabelValue("label-b", "label-a"),
                RowLabelValue("label-a", "label-b"),
                true, "UNCHANGED")
        ];

    internal static List<P9StatisticDiffResultRow> RowLabelRows(string conceptKind)
        =>
        [
            Row(conceptKind, 0, ProducerKey(conceptKind, 0),
                State("REDACTED", "TEXT"), State("REDACTED", "TEXT"),
                true, "REDACTED"),
            Row(conceptKind, 1, ProducerKey(conceptKind, 1),
                State("MISSING", "TEXT"), State("MISSING", "TEXT"),
                true, "UNCHANGED")
        ];

    private static string ProducerKey(string conceptKind, int ordinal)
        => conceptKind == "FIELD"
            ? ordinal == 0 ? "FIELD:field:amount" : "FIELD:field:balance"
            : $"ROW:row-{(char)('a' + ordinal)}";

    private static P9StatisticDiffResultRow Row(
        string conceptKind,
        int ordinal,
        string key,
        P9StatisticDiffTypedValue left,
        P9StatisticDiffTypedValue right,
        bool equal,
        string differenceKind,
        decimal? numericDelta = null)
    {
        var conceptKey = ConceptKey(conceptKind);
        return new P9StatisticDiffResultRow
        {
            RowId = RawSha256($"{conceptKind}\n{conceptKey}\n{key}"),
            Ordinal = ordinal,
            Key = key,
            ConceptKind = conceptKind,
            ConceptKey = conceptKey,
            Left = left,
            Right = right,
            Equal = equal,
            DifferenceKind = differenceKind,
            NumericDelta = numericDelta
        };
    }

    private static P9StatisticDiffTypedValue State(
        string state,
        string dataType,
        string? canonical = null)
        => new()
        {
            State = state,
            DataType = dataType,
            CanonicalValue = canonical
        };

    private static P9StatisticDiffTypedValue ValueNumber(decimal value)
        => new()
        {
            State = "VALUE",
            DataType = "NUMBER",
            CanonicalValue = value.ToString("G29", CultureInfo.InvariantCulture),
            NumericValue = value
        };

    private static P9StatisticDiffTypedValue ValueText(string value)
        => new()
        {
            State = "VALUE",
            DataType = "TEXT",
            CanonicalValue = value
        };

    private static P9StatisticDiffTypedValue ValueBoolean(bool value)
        => new()
        {
            State = "VALUE",
            DataType = "BOOLEAN",
            CanonicalValue = value ? "true" : "false",
            BooleanValue = value
        };

    private static P9StatisticDiffTypedValue ValueDate(DateTime value)
        => new()
        {
            State = "VALUE",
            DataType = "DATE",
            CanonicalValue = value.ToString("O", CultureInfo.InvariantCulture),
            DateValueUtc = value
        };

    private static P9StatisticDiffTypedValue ValueChoice(params string[] values)
        => new()
        {
            State = "VALUE",
            DataType = "CHOICE",
            CanonicalValue = string.Join("\u001f", values),
            ChoiceIds = values.ToList()
        };

    private static P9StatisticDiffTypedValue RowLabelValue(params string[] values)
    {
        var labels = values.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToList();
        return new P9StatisticDiffTypedValue
        {
            State = "VALUE",
            DataType = "TEXT",
            CanonicalValue = string.Join("\u001f", labels),
            ChoiceIds = labels
        };
    }

    private static List<P9StatisticDiffSourcePin> SourcePins()
        =>
        [
            SourcePin("LEFT", "report-a", 'a', 1),
            SourcePin("LEFT", "report-a", 'b', 2),
            SourcePin("RIGHT", "report-a", 'c', 3),
            SourcePin("RIGHT", "report-b", 'd', 4)
        ];

    private static P9StatisticDiffSourcePin SourcePin(
        string side,
        string reportId,
        char generation,
        int revision)
        => new()
        {
            Side = side,
            SourceReportId = reportId,
            SourcePayloadRevision = revision,
            SourcePayloadHash = Hash((char)('4' + revision)),
            SourceLifecycleRevision = revision,
            DirectRunId = $"direct-run-{side.ToLowerInvariant()}-{revision}",
            DirectGenerationId = Hash(generation)
        };

    internal static string DiffResultHash(WorkReportStatisticDiffResult owner)
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            owner.ConfigVersionId,
            owner.ConfigHash,
            owner.Direction,
            owner.MissingPolicy,
            owner.EmptyPolicy,
            rows = owner.Rows
        }, Web);
        return RawSha256(Canonicalize(element));
    }

    private static string ConceptKey(string kind) => kind switch
    {
        "FIELD" => "field:amount",
        "TABLE_METRIC" => "table:block-a:amount",
        "ROW_LABEL" => "row-label:block-a:risk",
        _ => throw new InvalidOperationException($"unsupported concept kind {kind}")
    };

    private static string DeclaredDataType(string kind) => kind switch
    {
        "FIELD" => "NUMBER",
        "TABLE_METRIC" => "DATE",
        "ROW_LABEL" => "TEXT",
        _ => throw new InvalidOperationException($"unsupported concept kind {kind}")
    };

    internal static string Hash(char value) => new(value, 64);

    internal static string RawSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
        {
            WriteCanonical(writer, value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (value.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(value.GetDouble());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"unsupported JSON token {value.ValueKind}");
        }
    }

    private static DateTime Utc(int day, int hour = 0)
        => new(2026, 8, day, hour, 0, 0, DateTimeKind.Utc);
}

internal sealed class BasicReader(WorkAssignmentBasicSummarySnapshot owner)
    : IStatisticReconciliationActualBasicOwnerReader
{
    internal int ReadCount { get; private set; }
    internal ActualBasicOwnerBoundary? LastBoundary { get; private set; }

    public Task<WorkAssignmentBasicSummarySnapshot?> ReadSnapshotAsync(
        ActualBasicOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        LastBoundary = boundary;
        return Task.FromResult<WorkAssignmentBasicSummarySnapshot?>(owner);
    }
}

internal sealed class AdvancedReader(
    IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode> days,
    IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode> months,
    IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode> years)
    : IStatisticReconciliationActualAdvancedOwnerReader
{
    internal int DayReads { get; private set; }
    internal int MonthReads { get; private set; }
    internal int YearReads { get; private set; }

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode>> ReadDayNodesAsync(
        ActualAdvancedOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DayReads++;
        return Task.FromResult(days);
    }

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode>> ReadMonthNodesAsync(
        ActualAdvancedOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MonthReads++;
        return Task.FromResult(months);
    }

    public Task<IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode>> ReadYearNodesAsync(
        ActualAdvancedOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        YearReads++;
        return Task.FromResult(years);
    }
}

internal sealed class DiffReader(WorkReportStatisticDiffResult owner)
    : IStatisticReconciliationActualP9DiffOwnerReader
{
    internal int ReadCount { get; private set; }

    public Task<WorkReportStatisticDiffResult?> ReadResultAsync(
        ActualP9DiffOwnerBoundary boundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return Task.FromResult<WorkReportStatisticDiffResult?>(owner);
    }
}
