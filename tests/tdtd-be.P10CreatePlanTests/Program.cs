using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;

var cases = new (string Id, Action Run)[]
{
    ("PLAN-01", ValidPlanDigest),
    ("PLAN-02", CanonicalIdentifiersRequired),
    ("PLAN-03", EveryAdvancedGrainRequired),
    ("PLAN-04", BasicOwnerVersionDriftChangesFingerprint),
    ("PLAN-05", AdvancedOwnerVersionDriftChangesFingerprint),
    ("PLAN-06", DiffOwnerVersionDriftChangesFingerprint),
    ("PLAN-07", CompositeConfigDriftChangesDigest),
    ("FILTER-01", CanonicalFilterTextMatchesHash),
    ("TARGET-01", WrongBasicPeriodRejected),
    ("TARGET-02", OmittedAdvancedOwnerRejected),
    ("TARGET-03", DirectionAwareDiffPeriod),
    ("TARGET-04", AdvancedBoundaryDriftRejected),
    ("API-01", CompletePagePlanRequired),
    ("API-02", ApiFilterAllowlistAndBinding),
    ("EXPORT-01", ExportFilterSeparatedAndBound),
    ("EXPORT-02", LegacyPlanRejectsUnboundExportFilter),
    ("EXPORT-03", ExportIdAcceptsProductObjectIdAndLegacySha)
};

foreach (var item in cases)
{
    item.Run();
    Console.WriteLine($"PASS {item.Id}");
}
Console.WriteLine($"P10_CREATE_PLAN_OK cases={cases.Length}");
return 0;

static void ValidPlanDigest()
{
    var plan = Plan();
    StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
        plan,
        plan.PlanSha256);
}

static void CanonicalIdentifiersRequired()
{
    var plan = Plan();
    plan.Basic.SnapshotId = plan.Basic.SnapshotId.ToUpperInvariant();
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    Invalid(() =>
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256));

    plan = Plan();
    plan.Api.OwnerResultId = null;
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    Invalid(() =>
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256));
}

static void EveryAdvancedGrainRequired()
{
    var plan = Plan();
    plan.Advanced.MonthNodeIds.Clear();
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    Invalid(() =>
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256));
}

static void BasicOwnerVersionDriftChangesFingerprint()
{
    var owner = Basic();
    var before =
        StatisticReconciliationActualCapturePlanIntegrity.BasicSelectorSha(owner);
    owner.SnapshotJson = "{\"v\":2}";
    var after =
        StatisticReconciliationActualCapturePlanIntegrity.BasicSelectorSha(owner);
    NotEqual(before, after, "BASIC_SNAPSHOT_DRIFT_NOT_BOUND");
}

static void AdvancedOwnerVersionDriftChangesFingerprint()
{
    var (days, months, years) = Advanced();
    var before =
        StatisticReconciliationActualCapturePlanIntegrity.AdvancedSelectorSha(
            "section-a",
            days,
            months,
            years);
    days[0].ValueHash = Sha('9');
    var after =
        StatisticReconciliationActualCapturePlanIntegrity.AdvancedSelectorSha(
            "section-a",
            days,
            months,
            years);
    NotEqual(before, after, "ADVANCED_VALUE_DRIFT_NOT_BOUND");
}

static void DiffOwnerVersionDriftChangesFingerprint()
{
    var owner = Diff();
    var before =
        StatisticReconciliationActualCapturePlanIntegrity.DiffSelectorSha(owner);
    owner.ResultHash = Sha('8');
    var after =
        StatisticReconciliationActualCapturePlanIntegrity.DiffSelectorSha(owner);
    NotEqual(before, after, "DIFF_RESULT_DRIFT_NOT_BOUND");
}

static void CompositeConfigDriftChangesDigest()
{
    var direct = Direct();
    var basic = Basic();
    var (days, months, years) = Advanced();
    var diff = Diff();
    var before =
        StatisticReconciliationActualCapturePlanIntegrity.ConfigurationBundleSha(
            Sha('1'),
            direct,
            basic,
            days,
            months,
            years,
            diff);
    direct.ConfigRevision++;
    var after =
        StatisticReconciliationActualCapturePlanIntegrity.ConfigurationBundleSha(
            Sha('1'),
            direct,
            basic,
            days,
            months,
            years,
            diff);
    NotEqual(before, after, "DIRECT_CONFIG_DRIFT_NOT_BOUND");
}

static void CanonicalFilterTextMatchesHash()
{
    using var document = JsonDocument.Parse("{\"z\":1,\"a\":2}");
    var canonical = StatisticReconciliationCanonicalJson.Canonicalize(
        document.RootElement);
    Equal("{\"a\":2,\"z\":1}", canonical, "FILTER_NOT_CANONICAL");
    Equal(
        StatisticReconciliationCanonicalJson.HashObject(document.RootElement),
        StatisticReconciliationCanonicalJson.HashText(canonical),
        "FILTER_HASH_TEXT_MISMATCH");
}

static void WrongBasicPeriodRejected()
{
    var basic = Basic();
    basic.RequestJson = JsonSerializer.Serialize(new
    {
        scopeAssignmentId = basic.ScopeAssignmentId,
        dynamicFormTemplateId = basic.DynamicFormTemplateId,
        periodScopeMode = "SINGLE_PERIOD",
        periodKey = "2026-07",
        periodKeyFrom = (string?)null,
        periodKeyTo = (string?)null,
        sourceScopeMode = basic.SourceScopeMode,
        sourceFlowInstanceId = basic.SourceFlowInstanceId,
        sourceFlowStepId = basic.SourceFlowStepId,
        sourceFlowBranchId = basic.SourceFlowBranchId,
        sourceFlowEffectiveStatus = basic.SourceFlowEffectiveStatus
    });
    var p9 = Direct();
    p9.PeriodKey = "2026-08";
    Equal(false,
        StatisticReconciliationRunService.ValidBasicPeriod(basic, p9),
        "WRONG_BASIC_PERIOD_ACCEPTED");
}

static void OmittedAdvancedOwnerRejected()
{
    var (days, _, _) = Advanced();
    var requested = days.Select(row => row.Id).ToArray();
    days.Add(new WorkAssignmentAdvancedSummaryDayNode { Id = Id() });
    Equal(false,
        StatisticReconciliationRunService.ExactIds(days, requested),
        "OMITTED_ELIGIBLE_ADVANCED_NODE_ACCEPTED");
}

static void DirectionAwareDiffPeriod()
{
    var diff = Diff();
    diff.Direction = "LEFT_TO_RIGHT";
    diff.LeftPeriodJson =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\",\"periodKeyFrom\":null,\"periodKeyTo\":null}";
    diff.RightPeriodJson =
        "{\"mode\":\"RANGE\",\"periodKey\":null,\"periodKeyFrom\":\"2026-01\",\"periodKeyTo\":\"2026-07\"}";
    Equal(true,
        InvokeValidDiffPeriods(diff, "2026-08"),
        "DIFF_OTHER_PERIOD_FALSE_REJECT");
    diff.LeftPeriodJson =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-07\",\"periodKeyFrom\":null,\"periodKeyTo\":null}";
    Equal(false,
        InvokeValidDiffPeriods(diff, "2026-08"),
        "DIFF_EFFECTIVE_PERIOD_MISMATCH_ACCEPTED");
}

static void AdvancedBoundaryDriftRejected()
{
    var (days, months, _) = Advanced();
    Equal(true,
        StatisticReconciliationRunService.SameAdvancedBoundary(
            months[0], days[0]),
        "ADVANCED_EQUAL_BOUNDARY_REJECTED");
    months[0].ConfigRevision++;
    Equal(false,
        StatisticReconciliationRunService.SameAdvancedBoundary(
            months[0], days[0]),
        "ADVANCED_CONFIG_DRIFT_ACCEPTED");
}

static void CompletePagePlanRequired()
{
    Equal(true,
        StatisticReconciliationActualCapturePlanIntegrity.ValidApiPagePlan(
            201, 200, 2),
        "API_MULTI_PAGE_PLAN_REJECTED");
    Equal(false,
        StatisticReconciliationActualCapturePlanIntegrity.ValidApiPagePlan(
            201, 200, 1),
        "API_OMITTED_TAIL_PAGE_ACCEPTED");
    Equal(false,
        StatisticReconciliationActualCapturePlanIntegrity.ValidApiPagePlan(
            6401, 200, 33),
        "API_SELECTOR_CAP_BYPASSED");
}

static void ApiFilterAllowlistAndBinding()
{
    const string direct = "{\"periodInstanceKey\":\"period-01\"}";
    Equal(true,
        StatisticReconciliationRunService.ValidActualApiFilter(
            "DIRECT_FIELD", direct,
            StatisticReconciliationCanonicalJson.HashText(direct),
            "period-01"),
        "DIRECT_FILTER_REJECTED");
    Equal(false,
        StatisticReconciliationRunService.ValidActualApiFilter(
            "DIRECT_FIELD", direct,
            StatisticReconciliationCanonicalJson.HashText(direct),
            "period-02"),
        "DIRECT_WRONG_PERIOD_ACCEPTED");
    const string basicInvalid = "{\"forceRefresh\":false}";
    Equal(false,
        StatisticReconciliationRunService.ValidActualApiFilter(
            "BASIC_SOURCE", basicInvalid,
            StatisticReconciliationCanonicalJson.HashText(basicInvalid),
            "period-01"),
        "BASIC_FORCE_REFRESH_ACCEPTED");
    const string diffInvalid = "{\"q\":\"x\"}";
    Equal(false,
        StatisticReconciliationRunService.ValidActualApiFilter(
            "P9_DIFF", diffInvalid,
            StatisticReconciliationCanonicalJson.HashText(diffInvalid),
            "period-01"),
        "DIFF_NON_EMPTY_FILTER_ACCEPTED");
}
static void ExportFilterSeparatedAndBound()
{
    var plan = Plan();
    var apiFilterSha = Sha('a');
    Equal(false,
        StringComparer.Ordinal.Equals(
            apiFilterSha,
            plan.Export.FilterSha256),
        "API_AND_EXPORT_FILTERS_FALSELY_COUPLED");
    StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
        plan,
        plan.PlanSha256);

    var before = plan.PlanSha256;
    plan.Export.FilterSha256 = Sha('c');
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    NotEqual(before, plan.PlanSha256, "EXPORT_FILTER_NOT_BOUND");
    StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
        plan,
        plan.PlanSha256);
}

static void LegacyPlanRejectsUnboundExportFilter()
{
    var plan = Plan();
    plan.SchemaVersion = StatisticReconciliationActualCapturePlanVersions.V2;
    plan.Export.FilterSha256 = null;
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
        plan,
        plan.PlanSha256);

    plan.Export.FilterSha256 = Sha('d');
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    Invalid(() =>
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256));

    plan = Plan();
    plan.Export.ResultKind = "BASIC_SOURCE";
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    Invalid(() =>
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan,
            plan.PlanSha256));
}
static void ExportIdAcceptsProductObjectIdAndLegacySha()
{
    var objectId = ObjectId.GenerateNewId().ToString();
    Equal(true,
        StatisticReconciliationRunService.ValidActualExportId(objectId),
        "PRODUCT_EXPORT_OBJECT_ID_REJECTED");
    Equal(true,
        StatisticReconciliationRunService.ValidActualExportId(Sha('a')),
        "LEGACY_EXPORT_SHA_REJECTED");
    Equal(false,
        StatisticReconciliationRunService.ValidActualExportId(
            objectId.ToUpperInvariant()),
        "NON_CANONICAL_EXPORT_OBJECT_ID_ACCEPTED");
    Equal(false,
        StatisticReconciliationRunService.ValidActualExportId(objectId[..23]),
        "SHORT_EXPORT_OBJECT_ID_ACCEPTED");
    Equal(false,
        StatisticReconciliationRunService.ValidActualExportId(
            new string('g', 64)),
        "NON_HEX_EXPORT_ID_ACCEPTED");
}
static bool InvokeValidDiffPeriods(
    WorkReportStatisticDiffResult diff,
    string periodKey)
{
    var method = typeof(StatisticReconciliationRunService).GetMethod(
        "ValidDiffPeriods",
        System.Reflection.BindingFlags.Static |
        System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VALID_DIFF_PERIODS_MISSING");
    return (bool)(method.Invoke(null, [diff, periodKey])
        ?? throw new InvalidOperationException("VALID_DIFF_PERIODS_NULL"));
}
static StatisticReconciliationActualCapturePlan Plan()
{
    var plan = new StatisticReconciliationActualCapturePlan
    {
        SchemaVersion = StatisticReconciliationActualCapturePlanVersions.V3,
        BoundaryRegistryVersion =
            StatisticReconciliationActualCapturePlanIntegrity
                .BoundaryRegistryVersion,
        ActualConfigurationBundleSha256 = Sha('0'),
        Basic = new StatisticReconciliationActualBasicTargetPlan
        {
            SnapshotId = Id(),
            Mode = "FLOW_FINAL",
            ImmutableSelectorSha256 = Sha('1')
        },
        Advanced = new StatisticReconciliationActualAdvancedTargetPlan
        {
            SectionId = "section-a",
            DayNodeIds = [Id()],
            MonthNodeIds = [Id()],
            YearNodeIds = [Id()],
            ImmutableSelectorSha256 = Sha('2')
        },
        Diff = new StatisticReconciliationActualDiffTargetPlan
        {
            ResultId = Id(),
            RunId = Id(),
            ImmutableSelectorSha256 = Sha('3')
        },
        Api = new StatisticReconciliationActualApiTargetPlan
        {
            Surface = "DIRECT_FIELD",
            OwnerResultId = Id(),
            ExpectedTotalRows = 201,
            PageSize = 200,
            PageCount = 2
        },
        Export = new StatisticReconciliationActualExportTargetPlan
        {
            ExportId = Sha('4'),
            ResultKind = "DIRECT_FIELD",
            WorkId = Id(),
            ScopeType = "ASSIGNMENT",
            ScopeId = Id(),
            ResultId = Id(),
            FilterSha256 = Sha('b'),
            RequestSha256 = Sha('5'),
            AuthorizationSnapshotSha256 = Sha('6'),
            ContentSha256 = Sha('7'),
            ColumnManifestSha256 = Sha('8'),
            OwnerSemanticSha256 = Sha('9')
        }
    };
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    return plan;
}

static WorkAssignmentBasicSummarySnapshot Basic()
    => new()
    {
        Id = Id(),
        WorkId = Id(),
        ScopeAssignmentId = Id(),
        DynamicFormTemplateId = Id(),
        SourceScopeMode = "FLOW_FINAL",
        SourceFlowInstanceId = Id(),
        RequestHash = Sha('1'),
        RequestJson = "{\"periodScopeMode\":\"SINGLE_PERIOD\"}",
        ConfigId = Id(),
        ConfigVersionId = Id(),
        ConfigVersionNo = 1,
        ConfigRevision = 1,
        ConfigHash = Sha('2'),
        ConfigDependencyPins = ["pin-a"],
        CandidateChainId = "chain",
        CandidatePromptId = "P9-04",
        CandidateStage = 2,
        CandidateCatalogRawSha256 = Sha('3'),
        CandidateCatalogSemanticSha256 = Sha('4'),
        CandidateStageLockSha256 = Sha('5'),
        SourceAssignmentIds = [Id()],
        SourceReportIds = [Id()],
        SourceSignatureHash = Sha('6'),
        SnapshotJson = "{\"v\":1}",
        SnapshotDirty = false,
        SnapshotRefreshedAtUtc = Utc(2),
        RefreshStatus = "DONE"
    };

static (List<WorkAssignmentAdvancedSummaryDayNode> Days,
    List<WorkAssignmentAdvancedSummaryMonthNode> Months,
    List<WorkAssignmentAdvancedSummaryYearNode> Years) Advanced()
{
    var seed = AdvancedSeed();
    return (
        [new WorkAssignmentAdvancedSummaryDayNode
        {
            Id = Id(), DayKey = "2026-08-11", Grain = "DAY",
            GrainKey = "2026-08-11", WindowStartUtc = Utc(1),
            WindowEndExclusiveUtc = Utc(2), ValueHash = Sha('1'),
            ValueJson = "{}", BuiltAtUtc = Utc(2),
            WorkId = seed.WorkId, AssignmentId = seed.AssignmentId,
            DynamicFormTemplateId = seed.DynamicFormTemplateId,
            SectionId = seed.SectionId, ConfigId = seed.ConfigId,
            ConfigVersionId = seed.ConfigVersionId, ConfigVersionNo = 1,
            ConfigRevision = 1, ConfigHash = seed.ConfigHash,
            DependencyPins = ["pin-a"], TimeAxis = "UTC_GREGORIAN",
            CandidateChainId = "chain", CandidatePromptId = "P9-05",
            CandidateStage = 3, CandidateCatalogRawSha256 = Sha('2'),
            CandidateCatalogSemanticSha256 = Sha('3'),
            CandidateStageLockSha256 = Sha('4')
        }],
        [new WorkAssignmentAdvancedSummaryMonthNode
        {
            Id = Id(), MonthKey = "2026-08", YearKey = "2026",
            Grain = "MONTH", GrainKey = "2026-08", WindowStartUtc = Utc(1),
            WindowEndExclusiveUtc = Utc(3), ValueHash = Sha('1'),
            ValueJson = "{}", BuiltAtUtc = Utc(2),
            WorkId = seed.WorkId, AssignmentId = seed.AssignmentId,
            DynamicFormTemplateId = seed.DynamicFormTemplateId,
            SectionId = seed.SectionId, ConfigId = seed.ConfigId,
            ConfigVersionId = seed.ConfigVersionId, ConfigVersionNo = 1,
            ConfigRevision = 1, ConfigHash = seed.ConfigHash,
            DependencyPins = ["pin-a"], TimeAxis = "UTC_GREGORIAN",
            CandidateChainId = "chain", CandidatePromptId = "P9-05",
            CandidateStage = 3, CandidateCatalogRawSha256 = Sha('2'),
            CandidateCatalogSemanticSha256 = Sha('3'),
            CandidateStageLockSha256 = Sha('4')
        }],
        [new WorkAssignmentAdvancedSummaryYearNode
        {
            Id = Id(), YearKey = "2026", Grain = "YEAR",
            GrainKey = "2026", WindowStartUtc = Utc(1),
            WindowEndExclusiveUtc = Utc(4), ValueHash = Sha('1'),
            ValueJson = "{}", BuiltAtUtc = Utc(2),
            WorkId = seed.WorkId, AssignmentId = seed.AssignmentId,
            DynamicFormTemplateId = seed.DynamicFormTemplateId,
            SectionId = seed.SectionId, ConfigId = seed.ConfigId,
            ConfigVersionId = seed.ConfigVersionId, ConfigVersionNo = 1,
            ConfigRevision = 1, ConfigHash = seed.ConfigHash,
            DependencyPins = ["pin-a"], TimeAxis = "UTC_GREGORIAN",
            CandidateChainId = "chain", CandidatePromptId = "P9-05",
            CandidateStage = 3, CandidateCatalogRawSha256 = Sha('2'),
            CandidateCatalogSemanticSha256 = Sha('3'),
            CandidateStageLockSha256 = Sha('4')
        }]);
}

static (string WorkId, string AssignmentId, string DynamicFormTemplateId,
    string SectionId, string ConfigId, string ConfigVersionId,
    string ConfigHash) AdvancedSeed()
    => (Id(), Id(), Id(), "section-a", Id(), Id(), Sha('1'));

static WorkReportStatisticDiffResult Diff()
    => new()
    {
        Id = Id(), RunId = Id(), WorkId = Id(), AssignmentId = Id(),
        DynamicFormTemplateId = Id(), ConfigId = Id(), ConfigVersionId = Id(),
        ConfigVersionNo = 1, ConfigRevision = 1, ConfigHash = Sha('1'),
        DependencyPins = ["pin-a"], CandidateChainId = "chain",
        CandidatePromptId = "P9-06", CandidateStage = 4,
        CandidateCatalogRawSha256 = Sha('2'),
        CandidateCatalogSemanticSha256 = Sha('3'),
        CandidateStageLockSha256 = Sha('4'), LeftConceptKind = "FIELD",
        LeftConceptKey = "field-a", LeftConceptCode = "field-a",
        LeftDataType = "NUMBER",
        LeftPeriodJson = "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\",\"periodKeyFrom\":null,\"periodKeyTo\":null}",
        RightConceptKind = "FIELD", RightConceptKey = "field-a",
        RightConceptCode = "field-a", RightDataType = "NUMBER",
        RightPeriodJson = "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\",\"periodKeyFrom\":null,\"periodKeyTo\":null}",
        Direction = "LEFT_TO_RIGHT", MissingPolicy = "AS_MISSING",
        EmptyPolicy = "AS_EMPTY", TimeAxis = "UTC_GREGORIAN",
        CommandId = "command", RequestHash = Sha('5'), ReceiptId = Sha('6'),
        Status = "COMPLETED", JobId = Id(), SourcePins = [], Rows = [],
        ResultHash = Sha('7'), IsCurrent = true, IsFresh = true,
        CompletedAtUtc = Utc(2)
    };

static WorkReportStatisticRebuildJob Direct()
    => new()
    {
        DynamicFormTemplateId = Id(), ConfigId = Id(), ConfigVersionId = Id(),
        ConfigVersionNo = 1, ConfigRevision = 1, ConfigHash = Sha('1')
    };

static DateTime Utc(int day) => new(2026, 8, day, 0, 0, 0, DateTimeKind.Utc);
static string Id() => ObjectId.GenerateNewId().ToString();
static string Sha(char value) => new(value, 64);

static void Invalid(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("EXPECTED_INVALID_OPERATION");
}

static void Equal<T>(T expected, T actual, string reason)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(reason);
}

static void NotEqual<T>(T left, T right, string reason)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
        throw new InvalidOperationException(reason);
}
