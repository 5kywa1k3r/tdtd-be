using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Services.Works;
using tdtd_be.Uploads;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Hangfire;
using MongoDB.Bson;
using tdtd_be.Jobs;

var tests = new (string Name, Action Run)[]
{
    ("root owner can create an assignment assigned to another user", AllowsRootOwnerAssignmentToAnotherUser),
    ("root owner cannot create an assignment assigned to themself", BlocksRootOwnerSelfAssignment),
    ("root non-owner cannot create even when assigning to themself", BlocksRootNonOwnerSelfAssignment),
    ("parent owner can create a child assignment assigned to another user", AllowsParentOwnerAssignmentToAnotherUser),
    ("parent owner cannot create a child assignment assigned to themself", BlocksParentOwnerSelfAssignment),
    ("direct parent assignee cannot create a child assignment assigned to themself", BlocksDirectAssigneeSelfAssignment),
    ("unrelated actor cannot create a child assignment even when assigning to themself", BlocksUnrelatedChildSelfAssignment),
    ("unit manager can assign peer unit manager", AllowsUnitManagerPeerUnitManagerAssignment),
    ("unit manager can assign descendant unit manager", AllowsUnitManagerDescendantUnitManagerAssignment),
    ("configured PHONG unit manager can assign PHUONG_XA unit manager", AllowsConfiguredPhongToPhuongXaUnitManagerAssignment),
    ("configured PHONG to PHUONG_XA rule does not allow normal user by default", BlocksConfiguredPhongToPhuongXaNormalUserAssignment),
    ("unit manager cannot assign normal user before final unit", BlocksUnitManagerNormalUserBeforeFinalUnit),
    ("final unit manager can assign normal user in own unit", AllowsFinalUnitManagerOwnUnitNormalUserAssignment),
    ("unit manager cannot assign normal user outside own final unit", BlocksUnitManagerNormalUserOutsideOwnUnit),
    ("blank actor is rejected before scope evaluation", BlocksBlankActor),
    ("assignment due date cannot be before start date", BlocksAssignmentDueDateBeforeStart),
    ("once assignment due date cannot be before assignment start", BlocksOnceDueBeforeAssignmentStart),
    ("periodic schedule start must stay inside assignment date range", BlocksPeriodicScheduleStartOutsideAssignmentRange),
    ("missing assignment start defaults to current date", DefaultsMissingAssignmentStartToCurrentDate),
    ("root missing assignment due date defaults to work due date", DefaultsRootDueDateToWorkDueDate),
    ("child missing assignment due date defaults to parent assignment due date", DefaultsChildDueDateToParentDueDate),
    ("periodic assignment date range caps occurrence validation", ValidatesPeriodicAssignmentDateRange),
    ("materialize job backfills elapsed monthly periods before rolling future", MaterializeJobBackfillsElapsedMonthlyPeriodsBeforeRollingFuture),
    ("materialize job limits multi-day monthly schedules to exact occurrences", MaterializeJobLimitsMonthlyMultiDayScheduleToExactOccurrences),
    ("materialize job backfills daily periods before rolling future", MaterializeJobBackfillsDailyPeriodsBeforeRollingFuture),
    ("materialize job backfills quarterly periods before rolling future", MaterializeJobBackfillsQuarterlyPeriodsBeforeRollingFuture),
    ("materialize job backfills semiannual periods before rolling future", MaterializeJobBackfillsSemiAnnualPeriodsBeforeRollingFuture),
    ("materialize job keeps separate due occurrences inside long periods", MaterializeJobKeepsSeparateDueOccurrencesInsideLongPeriods),
    ("backfill period policy uses due occurrence before assignment creation", BackfillPeriodPolicyUsesDueOccurrenceBeforeAssignmentCreation),
    ("draft can omit historical completed date while submit requires it", DraftCanOmitHistoricalCompletedDateWhileSubmitRequiresIt),
    ("assignment completion blocks open materialized report periods", BlocksCompletionWithOpenMaterializedReportPeriod),
    ("assignment completion blocks future expected report periods", BlocksCompletionWithFutureExpectedReportPeriods),
    ("assignment completion allows approved periods with no future expected periods", AllowsCompletionWhenReportsAreTerminalAndNoFutureExpected),
    ("historical data approval uses completed date for due status", ResolvesHistoricalDataApprovalFromCompletedDate),
    ("historical data submission uses completed date for due status", ResolvesHistoricalSubmittedStatusFromCompletedDate),
    ("historical report source window matches aggregate period filters", MatchesHistoricalReportSourceWindowForAggregation),
    ("generated unit manager can create root work", AllowsGeneratedUnitManagerWorkCreate),
    ("generated level manager can create root work", AllowsGeneratedLevelManagerWorkCreate),
    ("management account usernames use unit symbol", ManagementAccountUsernamesUseUnitSymbol),
    ("normal user cannot create root work", BlocksNormalUserWorkCreate),
    ("system admin cannot create root work directly", BlocksSystemAdminWorkCreate),
    ("manual manager-level account cannot create root work", BlocksManualManagerWorkCreate),
    ("report data origin resolves cumulative defaults", ResolvesReportContributionDefaultsByOrigin),
    ("report contribution mode can exclude a whole report", ExcludesWholeReportFromCumulativeStatistics),
    ("report contribution policy can exclude mapped field targets", ExcludesMappedFieldTargetsOnly),
    ("report contribution policy can exclude table metrics and labels", ExcludesMappedTableAndLabelTargetsOnly),
    ("aggregate draft partial mapping clears previous target cells", ClearsPreviousAggregateDraftTargetCells),
    ("dynamic form field display name is separated from statistic labels", ValidatesDynamicFormFieldDisplayName),
    ("dynamic form section title is required", BlocksBlankDynamicFormSectionTitle),
    ("dynamic form supports monthly BT 25 table blocks", SupportsMonthlyBtTwentyFiveTableBlocks),
    ("short text field statistics bucket by trimmed value", BucketsShortTextFieldStatistics),
    ("stat projection accepts verified external payload snapshot", AcceptsVerifiedExternalPayloadForStatisticProjection),
    ("stat projection rejects embedded payload fallback", RejectsEmbeddedPayloadForStatisticProjection),
    ("stat projection rejects unverified external payload hash", RejectsUnverifiedExternalPayloadHashForStatisticProjection),
    ("form-only report can omit dynamic excel template id", AllowsFormOnlyReportWithoutDynamicExcelTemplate),
    ("report service resolves dynamic excel id from form blocks", ResolvesDynamicExcelIdFromFormBlocks),
    ("report payload header compaction clears embedded detail fields", CompactsReportPayloadHeader),
    ("auto approve condition normalizes and matches report fields", MatchesAutoApproveCondition),
    ("automatic child aggregation source rules normalize to manual", NormalizesAutomaticChildSourceRulesToManual),
    ("dynamic form table target labels use dynamic excel default type", ValidatesDynamicFormTableTargetDefaultDataType),
    ("dynamic form metric label targets validate range type and uniqueness", ValidatesDynamicFormMetricLabelTargets),
    ("dynamic excel numeric grid validates spec metadata", ValidatesDynamicExcelNumericGridSpecMetadata),
    ("values1D compression round-trips null and zero runs", CompressesValues1DBlankRuns),
    ("basic summary normalizes typed methods", BasicSummaryNormalizesTypedMethods),
    ("basic summary supports typed default methods", BasicSummarySupportsTypedDefaultMethods),
    ("basic summary rejects table method rules", BasicSummaryRejectsTableMethodRules),
    ("basic summary refresh status controls enqueue", BasicSummaryRefreshStatusControlsEnqueue),
    ("basic summary extracts typed table values", BasicSummaryExtractsTypedTableValues),
    ("basic summary merges period snapshots by typed method", BasicSummaryMergesPeriodSnapshotsByTypedMethod),
    ("basic summary compact snapshot round-trips", BasicSummaryCompactSnapshotRoundTrips),
    ("basic summary respects compressed table null runs", BasicSummaryRespectsCompressedTableNullRuns),
    ("legacy work basis file resolves as work document", ResolvesLegacyWorkBasisFileAsWorkDocument),
    ("assignment file resolves as assignment branch document", ResolvesAssignmentFileAsBranchDocument),
    ("assignment document path resolves ancestors only", ResolvesAssignmentDocumentAncestorsFromPath),
    ("upload endpoint uses public base url override", BuildsUploadEndpointFromPublicBaseUrl),
    ("upload endpoint falls back to forwarded request scheme and host", BuildsUploadEndpointFromForwardedRequest),
    ("recurring job runner methods prevent overlap", RecurringJobRunnerMethodsPreventOverlap),
};

var failures = new List<string>();

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.GetType().Name} {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine($"     {ex}");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures.Count} test(s) failed.");
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"{tests.Length} test(s) passed.");

static void AllowsRootOwnerAssignmentToAnotherUser()
{
    var actorId = UserId(1);
    var work = WorkOwnedBy(actorId);

    WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
        work,
        parent: null,
        actorUserId: actorId,
        assigneeUserIds: new[] { UserId(2) });
}

static void BlocksRootOwnerSelfAssignment()
{
    var actorId = UserId(1);
    var work = WorkOwnedBy(actorId);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_SELF_ASSIGNMENT_NOT_ALLOWED,
        () => WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
            work,
            parent: null,
            actorUserId: actorId,
            assigneeUserIds: new[] { actorId }));
}

static void BlocksRootNonOwnerSelfAssignment()
{
    var actorId = UserId(2);
    var work = WorkOwnedBy(UserId(1));

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_ROOT_CREATE_FORBIDDEN,
        () => WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
            work,
            parent: null,
            actorUserId: actorId,
            assigneeUserIds: new[] { actorId }));
}

static void AllowsParentOwnerAssignmentToAnotherUser()
{
    var actorId = UserId(3);
    var work = WorkOwnedBy(UserId(1));
    var parent = ParentAssignment(createdByUserId: actorId);

    WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
        work,
        parent,
        actorUserId: actorId,
        assigneeUserIds: new[] { UserId(6) });
}

static void BlocksParentOwnerSelfAssignment()
{
    var actorId = UserId(3);
    var work = WorkOwnedBy(UserId(1));
    var parent = ParentAssignment(createdByUserId: actorId);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_SELF_ASSIGNMENT_NOT_ALLOWED,
        () => WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
            work,
            parent,
            actorUserId: actorId,
            assigneeUserIds: new[] { actorId }));
}

static void BlocksDirectAssigneeSelfAssignment()
{
    var actorId = UserId(4);
    var work = WorkOwnedBy(UserId(1));
    var parent = ParentAssignment(
        createdByUserId: UserId(1),
        assigneeUserIds: new[] { actorId });

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_SELF_ASSIGNMENT_NOT_ALLOWED,
        () => WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
            work,
            parent,
            actorUserId: actorId,
            assigneeUserIds: new[] { actorId }));
}

static void BlocksUnrelatedChildSelfAssignment()
{
    var actorId = UserId(5);
    var work = WorkOwnedBy(UserId(1));
    var parent = ParentAssignment(
        createdByUserId: UserId(1),
        assigneeUserIds: new[] { UserId(4) });

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN,
        () => WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
            work,
            parent,
            actorUserId: actorId,
            assigneeUserIds: new[] { actorId }));
}

static void AllowsUnitManagerPeerUnitManagerAssignment()
{
    var parentUnitId = ObjectId(1);
    var actorUnit = TestUnit(2, "001001", 2, parentUnitId);
    var peerUnit = TestUnit(3, "001002", 2, parentUnitId);
    var actor = TestUser(1, "mu_actor", ManagementAccountKind.UnitManager, actorUnit.Id);
    var peerManager = TestUser(2, "mu_peer", ManagementAccountKind.UnitManager, peerUnit.Id);
    var units = UnitMap(actorUnit, peerUnit);

    WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
        actor,
        actorUnit,
        new[] { peerManager },
        units,
        actorUnitHasAssignableDescendants: true);
}

static void AllowsUnitManagerDescendantUnitManagerAssignment()
{
    var actorUnit = TestUnit(4, "002", 1, parentUnitId: null);
    var childUnit = TestUnit(5, "002001", 2, actorUnit.Id);
    var actor = TestUser(3, "mu_actor", ManagementAccountKind.UnitManager, actorUnit.Id);
    var childManager = TestUser(4, "mu_child", ManagementAccountKind.UnitManager, childUnit.Id);
    var units = UnitMap(actorUnit, childUnit);

    WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
        actor,
        actorUnit,
        new[] { childManager },
        units,
        actorUnitHasAssignableDescendants: true);
}

static void AllowsConfiguredPhongToPhuongXaUnitManagerAssignment()
{
    var actorUnit = TestUnit(11, "100001002", 2, parentUnitId: ObjectId(10), primaryUnitTypeCode: "PHONG");
    var targetUnit = TestUnit(12, "200001001", 3, parentUnitId: ObjectId(99), primaryUnitTypeCode: "PHUONG_XA");
    var actor = TestUser(11, "mu_phong", ManagementAccountKind.UnitManager, actorUnit.Id);
    var targetManager = TestUser(12, "mu_phuong_xa", ManagementAccountKind.UnitManager, targetUnit.Id);
    var units = UnitMap(actorUnit, targetUnit);

    WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
        actor,
        actorUnit,
        new[] { targetManager },
        units,
        actorUnitHasAssignableDescendants: true,
        ConfiguredPhongToPhuongXaPolicy());
}

static void BlocksConfiguredPhongToPhuongXaNormalUserAssignment()
{
    var actorUnit = TestUnit(13, "100001003", 2, parentUnitId: ObjectId(10), primaryUnitTypeCode: "PHONG");
    var targetUnit = TestUnit(14, "200001002", 3, parentUnitId: ObjectId(99), primaryUnitTypeCode: "PHUONG_XA");
    var actor = TestUser(13, "mu_phong", ManagementAccountKind.UnitManager, actorUnit.Id);
    var staff = TestUser(14, "staff_phuong_xa", ManagementAccountKind.NormalUser, targetUnit.Id);
    var units = UnitMap(actorUnit, targetUnit);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
        () => WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
            actor,
            actorUnit,
            new[] { staff },
            units,
            actorUnitHasAssignableDescendants: true,
            ConfiguredPhongToPhuongXaPolicy()));
}

static void BlocksUnitManagerNormalUserBeforeFinalUnit()
{
    var actorUnit = TestUnit(6, "003", 1, parentUnitId: null);
    var actor = TestUser(5, "mu_actor", ManagementAccountKind.UnitManager, actorUnit.Id);
    var staff = TestUser(6, "staff", ManagementAccountKind.NormalUser, actorUnit.Id);
    var units = UnitMap(actorUnit);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
        () => WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
            actor,
            actorUnit,
            new[] { staff },
            units,
            actorUnitHasAssignableDescendants: true));
}

static void AllowsFinalUnitManagerOwnUnitNormalUserAssignment()
{
    var actorUnit = TestUnit(7, "004001", 2, parentUnitId: ObjectId(7));
    var actor = TestUser(7, "mu_actor", ManagementAccountKind.UnitManager, actorUnit.Id);
    var staff = TestUser(8, "staff", ManagementAccountKind.NormalUser, actorUnit.Id);
    var units = UnitMap(actorUnit);

    WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
        actor,
        actorUnit,
        new[] { staff },
        units,
        actorUnitHasAssignableDescendants: false);
}

static void BlocksUnitManagerNormalUserOutsideOwnUnit()
{
    var actorUnit = TestUnit(8, "005001", 2, parentUnitId: ObjectId(8));
    var otherUnit = TestUnit(9, "005002", 2, ObjectId(8));
    var actor = TestUser(9, "mu_actor", ManagementAccountKind.UnitManager, actorUnit.Id);
    var staff = TestUser(10, "staff", ManagementAccountKind.NormalUser, otherUnit.Id);
    var units = UnitMap(actorUnit, otherUnit);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID,
        () => WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(
            actor,
            actorUnit,
            new[] { staff },
            units,
            actorUnitHasAssignableDescendants: false));
}

static void BlocksBlankActor()
{
    var work = WorkOwnedBy(UserId(1));

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED,
        () => WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
            work,
            parent: null,
            actorUserId: " ",
            assigneeUserIds: Array.Empty<string>()));
}

static void BlocksAssignmentDueDateBeforeStart()
{
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(10),
        AssignmentType = WorkAssignmentTypes.Once,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(1) },
        StartDate = new DateTime(2026, 5, 10),
        DueDate = new DateTime(2026, 5, 9),
        DueAtUtc = new DateTime(2026, 5, 10)
    };

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_COMPLETED_BEFORE_START,
        () => WorkAssignmentScheduleHelper.ValidateRequest(
            WorkAssignmentScheduleHelper.NormalizeRequest(req),
            work));
}

static void RecurringJobRunnerMethodsPreventOverlap()
{
    var methodNames = new[]
    {
        nameof(NonOverlappingRecurringJobRunner.RunMinioCleanupAsync),
        nameof(NonOverlappingRecurringJobRunner.RunTusTempCleanupAsync),
        nameof(NonOverlappingRecurringJobRunner.RunHangfireHistoryArchiveAsync),
        nameof(NonOverlappingRecurringJobRunner.RunWorkAssignmentQueueScanAsync),
        nameof(NonOverlappingRecurringJobRunner.ProcessWorkAssignmentMaterializeJobsAsync),
        nameof(NonOverlappingRecurringJobRunner.RunNotificationDueScanAsync),
        nameof(NonOverlappingRecurringJobRunner.ProcessDocRoleProjectionRetryJobsAsync),
        nameof(NonOverlappingRecurringJobRunner.ProcessUserActionLogRetriesAsync),
        nameof(NonOverlappingRecurringJobRunner.ProcessDynamicFormStatisticRebuildJobsAsync)
    };

    foreach (var methodName in methodNames)
    {
        var method = typeof(NonOverlappingRecurringJobRunner).GetMethod(methodName)
            ?? throw new InvalidOperationException($"Missing recurring job runner method {methodName}");

        if (!method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false).Any())
            throw new InvalidOperationException($"Recurring job runner method {methodName} is missing overlap guard");
    }
}

static void BlocksOnceDueBeforeAssignmentStart()
{
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(15),
        AssignmentType = WorkAssignmentTypes.Once,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(1) },
        StartDate = new DateTime(2026, 5, 10),
        DueDate = new DateTime(2026, 5, 31),
        DueAtUtc = new DateTime(2026, 5, 9, 23, 59, 59, DateTimeKind.Utc)
    };

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_ONCE_DUE_BEFORE_ASSIGNMENT_START,
        () => WorkAssignmentScheduleHelper.ValidateRequest(
            WorkAssignmentScheduleHelper.NormalizeRequest(req),
            work));
}

static void BlocksPeriodicScheduleStartOutsideAssignmentRange()
{
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(16),
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(2) },
        StartDate = new DateTime(2026, 5, 10),
        DueDate = new DateTime(2026, 5, 31),
        Schedule = new AssignmentScheduleDto(
            CycleType: ReportCycleTypes.Weekly,
            StartDate: new DateTime(2026, 5, 9),
            WeekDays: new List<int> { 2 },
            MonthDays: null,
            QuarterDays: null,
            SemiAnnualDays: null,
            Note: null)
    };

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_PERIODIC_START_OUT_OF_RANGE,
        () => WorkAssignmentScheduleHelper.ValidateRequest(
            WorkAssignmentScheduleHelper.NormalizeRequest(req),
            work));
}

static void DefaultsMissingAssignmentStartToCurrentDate()
{
    var now = new DateTime(2026, 5, 13, 8, 0, 0, DateTimeKind.Utc);
    var work = WorkWithDates(
        startDate: new DateTime(2026, 1, 1),
        endDate: null,
        dueDate: new DateTime(2026, 5, 31));

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(12),
        AssignmentType = WorkAssignmentTypes.Once,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(1) },
        DueAtUtc = new DateTime(2026, 5, 20)
    };

    var effective = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(
        WorkAssignmentScheduleHelper.NormalizeRequest(req),
        work,
        parent: null,
        now);

    WorkAssignmentScheduleHelper.ValidateRequest(effective, work);

    AssertEqual(now.Date, effective.StartDate, "missing start date should default to current date");
}

static void DefaultsRootDueDateToWorkDueDate()
{
    var workDueDate = new DateTime(2026, 5, 31);
    var work = WorkWithDates(
        startDate: new DateTime(2026, 1, 1),
        endDate: null,
        dueDate: workDueDate);

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(13),
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(2) },
        StartDate = new DateTime(2026, 5, 1),
        Schedule = new AssignmentScheduleDto(
            CycleType: ReportCycleTypes.Weekly,
            StartDate: null,
            WeekDays: new List<int> { 2 },
            MonthDays: null,
            QuarterDays: null,
            SemiAnnualDays: null,
            Note: null)
    };

    var effective = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(
        WorkAssignmentScheduleHelper.NormalizeRequest(req),
        work,
        parent: null,
        new DateTime(2026, 5, 13));

    WorkAssignmentScheduleHelper.ValidateRequest(effective, work);

    AssertEqual(workDueDate, effective.DueDate, "root due date should default to work due date");
    AssertEqual<DateTime?>(null, effective.CompletedDate, "completion date should stay empty until explicit completion");
    AssertEqual(new DateTime(2026, 5, 1), effective.Schedule?.StartDate, "schedule start should keep assignment start date");
}

static void DefaultsChildDueDateToParentDueDate()
{
    var parentDueDate = new DateTime(2026, 6, 30);
    var work = WorkWithDates(
        startDate: new DateTime(2026, 1, 1),
        endDate: null,
        dueDate: new DateTime(2026, 12, 31));
    var parent = ParentAssignment(createdByUserId: UserId(1));
    parent.DueDate = parentDueDate;

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(14),
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(3) },
        StartDate = new DateTime(2026, 5, 1),
        Schedule = new AssignmentScheduleDto(
            CycleType: ReportCycleTypes.Weekly,
            StartDate: null,
            WeekDays: new List<int> { 2 },
            MonthDays: null,
            QuarterDays: null,
            SemiAnnualDays: null,
            Note: null)
    };

    var effective = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(
        WorkAssignmentScheduleHelper.NormalizeRequest(req),
        work,
        parent,
        new DateTime(2026, 5, 13));

    WorkAssignmentScheduleHelper.ValidateRequest(effective, work);

    AssertEqual(parentDueDate, effective.DueDate, "child due date should default to parent assignment due date");
    AssertEqual<DateTime?>(null, effective.CompletedDate, "completion date should stay empty until explicit completion");
}

static void ValidatesPeriodicAssignmentDateRange()
{
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));

    var req = new SaveWorkAssignmentRequest
    {
        DynamicFormTemplateId = ObjectId(11),
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(2) },
        StartDate = new DateTime(2026, 5, 1),
        DueDate = new DateTime(2026, 5, 31),
        Schedule = new AssignmentScheduleDto(
            CycleType: ReportCycleTypes.Weekly,
            StartDate: null,
            WeekDays: new List<int> { 2 },
            MonthDays: null,
            QuarterDays: null,
            SemiAnnualDays: null,
            Note: null)
    };

    var normalized = WorkAssignmentScheduleHelper.NormalizeRequest(req);
    WorkAssignmentScheduleHelper.ValidateRequest(normalized, work);

    AssertEqual(new DateTime(2026, 5, 1), normalized.StartDate, "assignment start date should normalize to date");
    AssertEqual(new DateTime(2026, 5, 1), normalized.Schedule?.StartDate, "schedule start should default from assignment start date");
}

static void MaterializeJobBackfillsElapsedMonthlyPeriodsBeforeRollingFuture()
{
    var now = new DateTime(2026, 6, 18);
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));
    var assignment = new WorkAssignment
    {
        Id = ObjectId(70),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 1, 1),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.Monthly,
            StartDate = new DateTime(2026, 1, 1),
            MonthDays = new List<int> { 17 },
            QuarterDays = Array.Empty<int>(),
            SemiAnnualDays = Array.Empty<int>()
        },
        IsActive = true
    };

    var items = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        assignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 3);

    AssertSequenceEqual(
        new[]
        {
            "20260117",
            "20260217",
            "20260317",
            "20260417",
            "20260517",
            "20260617",
            "20260717",
            "20260817",
            "20260917"
        },
        items.Select(x => x.PeriodKey).ToList(),
        "materialize job should process elapsed periods from assignment start before rolling future occurrences");
}

static void MaterializeJobLimitsMonthlyMultiDayScheduleToExactOccurrences()
{
    var now = new DateTime(2026, 6, 18);
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));
    var assignment = new WorkAssignment
    {
        Id = ObjectId(71),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 6, 18),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.Monthly,
            StartDate = new DateTime(2026, 6, 18),
            MonthDays = new List<int> { 5, 17, 25 },
            QuarterDays = Array.Empty<int>(),
            SemiAnnualDays = Array.Empty<int>()
        },
        IsActive = true
    };

    var items = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        assignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 3);

    AssertSequenceEqual(
        new[]
        {
            "20260625",
            "20260705",
            "20260717"
        },
        items.Select(x => x.PeriodKey).ToList(),
        "future rolling count should mean exact due occurrences, not full cycle buckets");
}

static void MaterializeJobBackfillsDailyPeriodsBeforeRollingFuture()
{
    var now = new DateTime(2026, 6, 18);
    var work = WorkWithDateRange(
        new DateTime(2026, 6, 15),
        new DateTime(2026, 6, 22));
    var assignment = new WorkAssignment
    {
        Id = ObjectId(170),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 6, 15),
        DueDate = new DateTime(2026, 6, 22),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.Daily,
            StartDate = new DateTime(2026, 6, 15),
            QuarterDays = Array.Empty<int>(),
            SemiAnnualDays = Array.Empty<int>()
        },
        IsActive = true
    };

    var items = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        assignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 2);

    AssertSequenceEqual(
        new[]
        {
            "20260615",
            "20260616",
            "20260617",
            "20260618",
            "20260619",
            "20260620"
        },
        items.Select(x => x.PeriodKey).ToList(),
        "daily materialize should process elapsed days before rolling future days");
}

static void MaterializeJobBackfillsQuarterlyPeriodsBeforeRollingFuture()
{
    var now = new DateTime(2026, 6, 18);
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));
    var assignment = new WorkAssignment
    {
        Id = ObjectId(171),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 1, 1),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.Quarterly,
            StartDate = new DateTime(2026, 1, 1),
            QuarterDays = new[] { 16 },
            SemiAnnualDays = Array.Empty<int>()
        },
        IsActive = true
    };

    var items = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        assignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 2);

    AssertSequenceEqual(
        new[]
        {
            "20260116",
            "20260416",
            "20260716",
            "20261016"
        },
        items.Select(x => x.PeriodKey).ToList(),
        "quarterly materialize should backfill elapsed quarters before rolling future quarters");
}

static void MaterializeJobBackfillsSemiAnnualPeriodsBeforeRollingFuture()
{
    var now = new DateTime(2026, 6, 18);
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));
    var assignment = new WorkAssignment
    {
        Id = ObjectId(172),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 1, 1),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.SemiAnnual,
            StartDate = new DateTime(2026, 1, 1),
            QuarterDays = Array.Empty<int>(),
            SemiAnnualDays = new[] { 167 }
        },
        IsActive = true
    };

    var items = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        assignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 2);

    AssertSequenceEqual(
        new[]
        {
            "20260616",
            "20261214"
        },
        items.Select(x => x.PeriodKey).ToList(),
        "semiannual materialize should backfill the elapsed half-year due before future half-year due");
}

static void MaterializeJobKeepsSeparateDueOccurrencesInsideLongPeriods()
{
    var now = new DateTime(2026, 6, 18);
    var work = WorkWithDateRange(
        new DateTime(2026, 6, 1),
        new DateTime(2026, 12, 31));

    var monthlyAssignment = new WorkAssignment
    {
        Id = ObjectId(173),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 6, 1),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.Monthly,
            StartDate = new DateTime(2026, 6, 1),
            MonthDays = new List<int> { 16, 25 },
            QuarterDays = Array.Empty<int>(),
            SemiAnnualDays = Array.Empty<int>()
        },
        IsActive = true
    };

    var monthlyItems = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        monthlyAssignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 2);

    AssertSequenceEqual(
        new[] { "20260616", "20260625", "20260716" },
        monthlyItems.Select(x => x.PeriodKey).ToList(),
        "monthly multi-day schedule should keep each due occurrence separate inside the same month");

    var quarterlyAssignment = new WorkAssignment
    {
        Id = ObjectId(174),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 4, 1),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.Quarterly,
            StartDate = new DateTime(2026, 4, 1),
            QuarterDays = new[] { 77, 80 },
            SemiAnnualDays = Array.Empty<int>()
        },
        IsActive = true
    };

    var quarterlyItems = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        quarterlyAssignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 2);

    AssertSequenceEqual(
        new[] { "20260616", "20260619", "20260915" },
        quarterlyItems.Select(x => x.PeriodKey).ToList(),
        "quarterly multi-day schedule should keep each due occurrence separate inside the same quarter");

    var semiAnnualAssignment = new WorkAssignment
    {
        Id = ObjectId(175),
        WorkId = work.Id,
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        StartDate = new DateTime(2026, 1, 1),
        DueDate = new DateTime(2026, 12, 31),
        Schedule = new AssignmentSchedule
        {
            CycleType = ReportCycleTypes.SemiAnnual,
            StartDate = new DateTime(2026, 1, 1),
            QuarterDays = Array.Empty<int>(),
            SemiAnnualDays = new[] { 167, 170 }
        },
        IsActive = true
    };

    var semiAnnualItems = WorkAssignmentMaterializeJobService.BuildDueItemsForMaterialize(
        semiAnnualAssignment,
        work,
        parent: null,
        nowUtc: now,
        rollingWindowCount: 2);

    AssertSequenceEqual(
        new[] { "20260616", "20260619", "20261214" },
        semiAnnualItems.Select(x => x.PeriodKey).ToList(),
        "semiannual multi-day schedule should keep each due occurrence separate inside the same half-year");
}

static void BackfillPeriodPolicyUsesDueOccurrenceBeforeAssignmentCreation()
{
    var now = new DateTime(2026, 6, 18, 9, 0, 0);
    var assignment = new WorkAssignment
    {
        CreatedAtUtc = new DateTime(2026, 6, 18, 8, 0, 0),
        StartDate = new DateTime(2026, 1, 1)
    };

    var isJanuaryBackfill = WorkAssignmentBackfillPeriodPolicy.TryResolveCompletedDateBounds(
        assignment,
        new DateTime(2026, 1, 1),
        new DateTime(2026, 1, 31),
        new DateTime(2026, 1, 17),
        now,
        out var minDate,
        out var maxDate);

    AssertTrue(isJanuaryBackfill, "wholly past scheduled windows before assignment creation should be historical backfill");
    AssertEqual(new DateTime(2026, 1, 1), minDate, "backfill completed date min should start at the source window");
    AssertEqual(new DateTime(2026, 6, 18), maxDate, "backfill completed date max should allow late completion after the source window up to today");

    var isJuneDueBackfill = WorkAssignmentBackfillPeriodPolicy.TryResolveCompletedDateBounds(
        assignment,
        new DateTime(2026, 6, 1),
        new DateTime(2026, 6, 30),
        new DateTime(2026, 6, 16),
        now,
        out var juneMinDate,
        out var juneMaxDate);

    AssertTrue(isJuneDueBackfill, "mixed monthly window should be historical backfill when its due occurrence is before assignment creation");
    AssertEqual(new DateTime(2026, 6, 1), juneMinDate, "backfill completed date min should keep the source window start");
    AssertEqual(new DateTime(2026, 6, 18), juneMaxDate, "mixed backfill completed date max should allow late completion up to today");

    var isJuneLateBackfill = WorkAssignmentBackfillPeriodPolicy.TryResolveCompletedDateBounds(
        assignment,
        new DateTime(2026, 6, 1),
        new DateTime(2026, 6, 30),
        new DateTime(2026, 6, 16),
        new DateTime(2026, 6, 20, 9, 0, 0),
        out _,
        out var juneLateMaxDate);

    AssertTrue(isJuneLateBackfill, "mixed monthly backfill should stay historical after assignment creation");
    AssertEqual(new DateTime(2026, 6, 20), juneLateMaxDate, "mixed backfill completed date max should move with today so late completion can be recorded");

    AssertFalse(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 6, 25),
            now),
        "monthly occurrence after assignment creation should stay current even when an earlier occurrence in the same month is backfill");

    AssertTrue(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 4, 1),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 4, 16),
            now),
        "quarterly window should be backfill when its due occurrence is before assignment creation");

    AssertFalse(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 4, 1),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 6, 19),
            now),
        "quarterly occurrence after assignment creation should stay current even when an earlier occurrence in the same quarter is backfill");

    AssertTrue(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 6, 16),
            now),
        "semiannual window should be backfill when its due occurrence is before assignment creation");

    AssertFalse(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 6, 19),
            now),
        "semiannual occurrence after assignment creation should stay current even when an earlier occurrence in the same half-year is backfill");

    AssertTrue(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 6, 17),
            new DateTime(2026, 6, 17),
            new DateTime(2026, 6, 17),
            now),
        "daily period before assignment creation should be backfill");

    AssertFalse(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 6, 18),
            new DateTime(2026, 6, 18),
            new DateTime(2026, 6, 18),
            now),
        "daily period on assignment creation date should use normal runtime due logic");

    AssertFalse(
        WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
            assignment,
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 31),
            new DateTime(2026, 7, 17),
            now),
        "future source window should use normal runtime due logic");
}

static void DraftCanOmitHistoricalCompletedDateWhileSubmitRequiresIt()
{
    var serviceType = typeof(WorkAssignmentReportService);
    var policyType = serviceType.GetNestedType("ReportCompletedDatePolicy", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ReportCompletedDatePolicy helper type was not found.");

    var policy = Activator.CreateInstance(
        policyType,
        true,
        true,
        new DateTime(2026, 1, 1),
        new DateTime(2026, 6, 18),
        WorkAssignmentBackfillPeriodPolicy.CompletedDatePolicyReason)
        ?? throw new InvalidOperationException("ReportCompletedDatePolicy helper instance was not created.");

    var draftCompletedDate = InvokePrivateStatic<DateTime?>(
        serviceType,
        "ValidateReportCompletedDateInput",
        policy,
        null,
        new { reportId = "draft" },
        false);

    AssertEqual<DateTime?>(null, draftCompletedDate, "draft save should allow missing historical completed date");

    AssertThrowsFromReflection(
        AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_COMPLETED_DATE_REQUIRED,
        () => InvokePrivateStatic<DateTime?>(
            serviceType,
            "ValidateReportCompletedDateInput",
            policy,
            null,
            new { reportId = "submit" },
            true));
}

static void BlocksCompletionWithOpenMaterializedReportPeriod()
{
    var now = new DateTime(2026, 5, 20);
    var workId = ObjectId(71);
    var assignmentId = ObjectId(72);
    var assigneeUserId = UserId(72);
    var work = new Work { Id = workId, DueDate = now.AddDays(2) };
    var assignment = PeriodicAssignment(workId, assignmentId, now.AddDays(-1), now.AddDays(2));
    var binding = Binding(workId, assignmentId, assigneeUserId);
    var period = ScheduledPeriod(workId, assignmentId, assigneeUserId, now, WorkReportPeriodStatus.Pending);

    var readiness = WorkAssignmentCompletionReadiness.Evaluate(
        work,
        new[] { assignment },
        new[] { assignmentId },
        new[] { binding },
        new[] { period },
        completedDate: now,
        nowUtc: now);

    AssertFalse(readiness.CanComplete, "completion should be blocked while a materialized period is still open");
    AssertEqual(1, readiness.OpenPeriodCount, "open materialized period should be counted");
}

static void BlocksCompletionWithFutureExpectedReportPeriods()
{
    var now = new DateTime(2026, 5, 20);
    var workId = ObjectId(73);
    var assignmentId = ObjectId(74);
    var assigneeUserId = UserId(74);
    var work = new Work { Id = workId, DueDate = now.AddDays(3) };
    var assignment = PeriodicAssignment(workId, assignmentId, now, now.AddDays(3));
    var binding = Binding(workId, assignmentId, assigneeUserId);

    var readiness = WorkAssignmentCompletionReadiness.Evaluate(
        work,
        new[] { assignment },
        new[] { assignmentId },
        new[] { binding },
        Array.Empty<WorkReportPeriod>(),
        completedDate: now,
        nowUtc: now);

    AssertFalse(readiness.CanComplete, "completion should be blocked when current/future expected periods are not terminal");
    AssertTrue(readiness.FutureExpectedPendingCount > 0, "future expected report periods should be counted");
}

static void AllowsCompletionWhenReportsAreTerminalAndNoFutureExpected()
{
    var now = new DateTime(2026, 5, 20);
    var workId = ObjectId(75);
    var assignmentId = ObjectId(76);
    var assigneeUserId = UserId(76);
    var work = new Work { Id = workId, DueDate = now.AddDays(-1) };
    var assignment = PeriodicAssignment(workId, assignmentId, now.AddDays(-3), now.AddDays(-1));
    var binding = Binding(workId, assignmentId, assigneeUserId);
    var period = ScheduledPeriod(workId, assignmentId, assigneeUserId, now.AddDays(-1), WorkReportPeriodStatus.Approved);

    var readiness = WorkAssignmentCompletionReadiness.Evaluate(
        work,
        new[] { assignment },
        new[] { assignmentId },
        new[] { binding },
        new[] { period },
        completedDate: now,
        nowUtc: now);

    AssertTrue(readiness.CanComplete, "completion should be allowed once report periods are terminal and no current/future periods are expected");
    AssertEqual(0, readiness.OpenPeriodCount, "terminal periods should not be counted as open");
    AssertEqual(0, readiness.FutureExpectedPendingCount, "past-only completed scope should not create future expected periods");
}

static void ResolvesHistoricalDataApprovalFromCompletedDate()
{
    var now = new DateTime(2026, 5, 13);
    var onTimePeriod = new WorkReportPeriod
    {
        Status = WorkReportPeriodStatus.OverdueSubmitted,
        DueAtUtc = new DateTime(2026, 5, 10, 23, 59, 59)
    };
    var onTimeReport = new WorkAssignmentReport
    {
        IsHistoricalData = true,
        HistoricalDataApproved = true,
        CompletedDate = new DateTime(2026, 5, 10),
        IsLateSubmission = true
    };

    var onTimeStatus = WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(onTimePeriod, onTimeReport, now);

    AssertEqual(WorkReportPeriodStatus.Approved, onTimeStatus, "historical report completed by due date should approve on time");

    var lateReport = new WorkAssignmentReport
    {
        IsHistoricalData = true,
        HistoricalDataApproved = true,
        CompletedDate = new DateTime(2026, 5, 11)
    };

    var lateStatus = WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(onTimePeriod, lateReport, now);

    AssertEqual(WorkReportPeriodStatus.OverdueApproved, lateStatus, "historical report completed after due date should approve as overdue");
}

static void ResolvesHistoricalSubmittedStatusFromCompletedDate()
{
    var now = new DateTime(2026, 5, 13);
    var period = new WorkReportPeriod
    {
        IsHistoricalData = true,
        DueAtUtc = new DateTime(2026, 5, 10, 23, 59, 59)
    };
    var report = new WorkAssignmentReport
    {
        IsHistoricalData = true,
        CompletedDate = new DateTime(2026, 5, 10)
    };

    var status = WorkAssignmentReportHistoricalDataHelper.ResolveSubmittedPeriodStatus(period, report, now);
    var isLate = WorkAssignmentReportHistoricalDataHelper.ResolveIsLateSubmission(
        isHistoricalData: true,
        completedDate: report.CompletedDate,
        dueAtUtc: period.DueAtUtc,
        now: now);

    AssertEqual(WorkReportPeriodStatus.Submitted, status, "historical report completed by due date should not become overdue because it is submitted later");
    AssertFalse(isLate, "historical late flag should use completed date instead of submit time");

    report.CompletedDate = new DateTime(2026, 5, 11);
    var lateStatus = WorkAssignmentReportHistoricalDataHelper.ResolveSubmittedPeriodStatus(period, report, now);
    var lateFlag = WorkAssignmentReportHistoricalDataHelper.ResolveIsLateSubmission(
        isHistoricalData: true,
        completedDate: report.CompletedDate,
        dueAtUtc: period.DueAtUtc,
        now: now);

    AssertEqual(WorkReportPeriodStatus.OverdueSubmitted, lateStatus, "historical report completed after due date should submit as overdue");
    AssertTrue(lateFlag, "historical late flag should turn on when completed date is after due date");
}

static void MatchesHistoricalReportSourceWindowForAggregation()
{
    var report = new WorkAssignmentReport
    {
        PeriodKind = WorkReportPeriodKind.Scheduled,
        PeriodKey = "20260110",
        ReportDate = new DateTime(2026, 1, 10),
        PeriodStart = new DateTime(2026, 1, 5),
        PeriodEnd = new DateTime(2026, 1, 10),
        CompletedDate = new DateTime(2026, 1, 10),
        IsHistoricalData = true
    };

    AssertTrue(
        WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(report, "SINGLE_PERIOD", "20260107", null, null),
        "single-period aggregate should include a scheduled report when the source window overlaps");
    AssertFalse(
        WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(report, "SINGLE_PERIOD", "20260111", null, null),
        "single-period aggregate should exclude dates outside the source window");
    AssertTrue(
        WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(report, "PERIOD_RANGE", null, "20260101", "20260106"),
        "period-range aggregate should include overlapping historical source windows");
    AssertTrue(
        WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(report, "CUMULATIVE_TO_PERIOD", null, null, "20260105"),
        "cumulative aggregate should include reports whose source window starts on or before the cutoff");
    AssertFalse(
        WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(report, "CUMULATIVE_TO_PERIOD", null, null, "20260104"),
        "cumulative aggregate should exclude reports whose source window starts after the cutoff");
}

static void AllowsGeneratedUnitManagerWorkCreate()
{
    WorkPermission().EnsureCanCreateRoot(Me(
        username: "mu_pv01",
        accountKind: "UNIT_MANAGER",
        roles: new List<string> { Roles.ManagerUnit(ObjectId(9)) }));
}

static void AllowsGeneratedLevelManagerWorkCreate()
{
    WorkPermission().EnsureCanCreateRoot(Me(
        username: "ml_pv01",
        accountKind: "LEVEL_MANAGER",
        roles: new List<string> { Roles.MANAGER_LEVEL }));
}

static void ManagementAccountUsernamesUseUnitSymbol()
{
    var convention = new ManagementAccountConvention();
    var unit = new Unit
    {
        Id = ObjectId(9),
        Code = "001002",
        Symbol = "PV01",
        Level = 2,
        FullName = "Phong Van"
    };

    AssertEqual("mu_pv01", convention.BuildUnitManagerUsername(unit), "unit manager username");
    AssertEqual("ml_pv01", convention.BuildLevelManagerUsername(unit), "level manager username");
}

static void BlocksNormalUserWorkCreate()
{
    AssertThrows(
        AppErrorCode.WORK_CREATE_FORBIDDEN,
        () => WorkPermission().EnsureCanCreateRoot(Me(
            username: "normal_user",
            accountKind: "NORMAL_USER",
            roles: new List<string>())));
}

static void BlocksSystemAdminWorkCreate()
{
    AssertThrows(
        AppErrorCode.WORK_CREATE_FORBIDDEN,
        () => WorkPermission().EnsureCanCreateRoot(Me(
            username: "sa_anhdd",
            accountKind: "SYSTEM_ADMIN",
            roles: new List<string> { Roles.SYSTEM_ADMIN })));
}

static void BlocksManualManagerWorkCreate()
{
    AssertThrows(
        AppErrorCode.WORK_CREATE_FORBIDDEN,
        () => WorkPermission().EnsureCanCreateRoot(Me(
            username: "manager_level_user",
            accountKind: "LEVEL_MANAGER",
            roles: new List<string> { Roles.MANAGER_LEVEL })));
}

static void ResolvesReportContributionDefaultsByOrigin()
{
    AssertEqual(
        WorkReportCumulativeContributionMode.Include,
        WorkReportDataOrigin.DefaultContributionMode(WorkReportDataOrigin.ManualInput),
        "manual reports should contribute by default");
    AssertEqual(
        WorkReportCumulativeContributionMode.Exclude,
        WorkReportDataOrigin.DefaultContributionMode(WorkReportDataOrigin.AutoSummary),
        "auto-summary reports should not double count by default");
    AssertEqual(
        WorkReportCumulativeContributionMode.Exclude,
        WorkReportDataOrigin.DefaultContributionMode(WorkReportDataOrigin.CopiedSummary),
        "copied-summary reports should not double count by default");
    AssertEqual(
        WorkReportCumulativeContributionMode.Include,
        WorkReportDataOrigin.DefaultContributionMode(WorkReportDataOrigin.PartialMapping),
        "partial mapping reports should keep manual targets contributing by default");
}

static void ExcludesWholeReportFromCumulativeStatistics()
{
    var policy = WorkReportCumulativeContributionPolicy.Parse(
        WorkReportCumulativeContributionMode.Exclude,
        null);

    AssertFalse(policy.IncludesReport, "whole report should be excluded");
    AssertFalse(policy.ShouldIncludeField("manual_count"), "fields follow excluded report mode");
    AssertFalse(
        policy.ShouldIncludeTableMetric("b1", "m1", "r1", "c1", "s1"),
        "table metrics follow excluded report mode");
}

static void ExcludesMappedFieldTargetsOnly()
{
    var policy = WorkReportCumulativeContributionPolicy.Parse(
        WorkReportCumulativeContributionMode.Include,
        """
        {
          "defaultMode": "INCLUDE",
          "rules": [
            { "targetKind": "FIELD", "fieldKey": "mapped_total", "mode": "EXCLUDE" }
          ]
        }
        """);

    AssertTrue(policy.IncludesReport, "partial mapping report should still be eligible");
    AssertFalse(policy.ShouldIncludeField("mapped_total"), "mapped field should be excluded");
    AssertTrue(policy.ShouldIncludeField("manual_extra"), "manual field should remain included");
}

static void ExcludesMappedTableAndLabelTargetsOnly()
{
    var policy = WorkReportCumulativeContributionPolicy.Parse(
        WorkReportCumulativeContributionMode.Include,
        """
        {
          "defaultMode": "INCLUDE",
          "rules": [
            { "targetKind": "TABLE_METRIC", "blockId": "b1", "metricKey": "child_sum", "mode": "EXCLUDE" },
            { "targetKind": "LABEL", "blockId": "b1", "labelCode": "child_label", "mode": "EXCLUDE" }
          ]
        }
        """);

    AssertFalse(
        policy.ShouldIncludeTableMetric("b1", "child_sum", "r1", "c1", "src"),
        "mapped table metric should be excluded");
    AssertTrue(
        policy.ShouldIncludeTableMetric("b1", "manual_sum", "r1", "c1", "src"),
        "manual table metric should remain included");
    AssertFalse(policy.ShouldIncludeLabel("b1", "r1", "row", "child_label"), "mapped label should be excluded");
    AssertTrue(policy.ShouldIncludeLabel("b1", "r1", "row", "manual_label"), "manual label should remain included");
}

static void ClearsPreviousAggregateDraftTargetCells()
{
    var serviceType = typeof(WorkAssignmentReportService);
    var summaryType = serviceType.GetNestedType("AggregateDraftSummary", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AggregateDraftSummary helper type was not found.");

    var summary = Activator.CreateInstance(
        summaryType,
        WorkReportDataOrigin.PartialMapping,
        new DynamicFormAggregateRequest
        {
            DynamicFormTemplateId = "form_1",
            BlockId = "block_1"
        },
        "MANUAL_MAP",
        "SUM",
        "block_1",
        false,
        new List<int> { 0, 2 },
        new List<string>(),
        new List<string>(),
        "form_1",
        null)
        ?? throw new InvalidOperationException("AggregateDraftSummary helper instance was not created.");

    var isSameTarget = InvokePrivateStatic<bool>(
        serviceType,
        "IsSameAggregateDraftTarget",
        summary,
        "form_1",
        "block_1");

    AssertTrue(isSameTarget, "previous aggregate target should match the same form/block");

    var values = new List<decimal?> { 10, 20, 30 };
    InvokePrivateStatic<object?>(
        serviceType,
        "ClearAggregateDraftTargetIndexes",
        values,
        new List<int> { 0, 2 });

    AssertEqual(null, values[0], "previous mapped cell 0 should be cleared before reapply");
    AssertEqual(20m, values[1], "unmapped manual cell should be preserved");
    AssertEqual(null, values[2], "previous mapped cell 2 should be cleared before reapply");
}

static void ValidatesDynamicFormFieldDisplayName()
{
    var serviceType = typeof(DynamicFormService);

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureFieldDisplayNames",
        """
        [
          { "id": "f1", "sectionId": "s1", "key": "revenue", "type": "number", "name": "Số hồ sơ xử lý / kỳ #1 (đợt A)", "label": "Số hồ sơ xử lý / kỳ #1 (đợt A)", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
        ]
        """);

    var normalizedAliases = InvokePrivateStatic<string?>(
        serviceType,
        "NormalizeFieldDisplayAliases",
        """
        [
          { "id": "f1", "sectionId": "s1", "key": "revenue", "type": "number", "displayName": "Doanh thu", "label": "Doanh thu", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
        ]
        """);
    using var normalizedAliasDoc = JsonDocument.Parse(normalizedAliases!);
    var normalizedAliasField = normalizedAliasDoc.RootElement[0];
    AssertEqual("Doanh thu", normalizedAliasField.GetProperty("name").GetString(), "field display alias should be stored as name");
    AssertFalse(normalizedAliasField.TryGetProperty("displayName", out _), "displayName alias should not be stored");
    AssertFalse(normalizedAliasField.TryGetProperty("label", out _), "label alias should not be stored");

    var normalizedKeys = InvokePrivateStatic<string?>(
        serviceType,
        "NormalizeFieldPayload",
        """
        [
          { "id": "f1", "sectionId": "s1", "type": "number", "name": "Doanh thu" },
          { "id": "f2", "sectionId": "s1", "type": "number", "name": "Chi phí", "key": "total" },
          { "id": "f3", "sectionId": "s1", "type": "number", "name": "Lợi nhuận", "key": "total" }
        ]
        """,
        """
        [
          { "id": "f1", "sectionId": "s1", "type": "number", "name": "Doanh thu", "key": "old_revenue" }
        ]
        """);
    using var normalizedKeyDoc = JsonDocument.Parse(normalizedKeys!);
    AssertEqual("old_revenue", normalizedKeyDoc.RootElement[0].GetProperty("key").GetString(), "existing field key should be retained when UI omits it");
    AssertEqual("total", normalizedKeyDoc.RootElement[1].GetProperty("key").GetString(), "first requested field key should be kept");
    AssertEqual("total_2", normalizedKeyDoc.RootElement[2].GetProperty("key").GetString(), "duplicate field key should be made unique");

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_NAME_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldDisplayNames",
            """
            [
              { "id": "f2", "sectionId": "s1", "key": "number_1", "type": "number", "name": "number 1", "label": "number 1", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
            ]
            """));

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureStatisticConfigOnlyChange",
        """
        [
          { "id": "f3", "sectionId": "s1", "key": "number_1", "type": "number", "label": "Số", "isStatistic": true, "statisticLabelCodes": ["old"] }
        ]
        """,
        """
        [
          { "id": "f3", "sectionId": "s1", "key": "number_1", "type": "number", "name": "Doanh thu kỳ này", "displayName": "Doanh thu kỳ này", "label": "Doanh thu kỳ này", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
        ]
        """,
        "FieldsJson");

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureStatisticLabelTypeCompatibility",
        """
        [
          { "id": "f4", "sectionId": "s1", "key": "approved_at", "type": "date", "name": "Ngày duyệt", "isStatistic": true, "statisticLabelCodes": ["approved_at"] }
        ]
        """,
        null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["approved_at"] = LabelDataTypes.Date
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["approved_at"] = LabelUsages.Statistic
        });

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureStatisticLabelTypeCompatibility",
            """
            [
              { "id": "f5", "sectionId": "s1", "key": "approved_at", "type": "date", "name": "Ngày duyệt", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
            ]
            """,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["revenue"] = LabelDataTypes.Number
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["revenue"] = LabelUsages.Statistic
            }));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureStatisticLabelTypeCompatibility",
            """
            [
              { "id": "f6", "sectionId": "s1", "key": "revenue", "type": "number", "name": "Doanh thu", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
            ]
            """,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["revenue"] = LabelDataTypes.Number
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["revenue"] = LabelUsages.Classification
            }));

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureTableTargetLabelTypeCompatibility",
        null,
        """
        [
          {
            "blockId": "b1",
            "allowedRowLabelCodes": ["budget"],
            "rowLabelDefaults": [
              { "rowIndex": 1, "rowLabelCodes": ["budget"] }
            ]
          }
        ]
        """,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["budget"] = LabelDataTypes.Number
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["budget"] = LabelUsages.TableTarget
        });

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureTableTargetLabelTypeCompatibility",
            null,
            """
            [
              {
                "blockId": "b1",
                "allowedRowLabelCodes": ["budget"]
              }
            ]
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["budget"] = LabelDataTypes.Number
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["budget"] = LabelUsages.Classification
            }));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureTableTargetLabelTypeCompatibility",
            null,
            """
            [
              {
                "blockId": "b1",
                "rowLabelDefaults": [
                  { "rowIndex": 1, "rowLabelCodes": ["budget"] }
                ]
              }
            ]
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["budget"] = LabelDataTypes.ShortText
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["budget"] = LabelUsages.TableTarget
            }));
}

static void BucketsShortTextFieldStatistics()
{
    var serviceType = typeof(WorkReportFieldStatisticsService);

    var fields = InvokePrivateStatic<object>(
        serviceType,
        "ExtractStatisticFields",
        """
        [
          { "id": "f_short", "sectionId": "s1", "key": "phan_loai_ngan", "type": "shortText", "name": "Phan loai ngan", "isStatistic": true, "statisticLabelCodes": ["short_label"] }
        ]
        """);

    var values = InvokePrivateStatic<object>(
        serviceType,
        "ExtractFieldValues",
        """
        { "values": { "f_short": "  Nhom A  " } }
        """,
        fields);

    var rows = ((System.Collections.IEnumerable)values).Cast<object>().ToList();
    AssertEqual(1, rows.Count, "shortText should produce one statistic value row");
    AssertEqual("Nhom A", GetReflectedProperty<string>(rows[0], "BucketKey"), "shortText bucket key should be the trimmed value");
    AssertEqual("Nhom A", GetReflectedProperty<string>(rows[0], "BucketLabel"), "shortText bucket label should be the trimmed value");
    AssertEqual("TEXT_BUCKET", GetReflectedProperty<string>(rows[0], "ValueKind"), "shortText should be stored as a text bucket statistic");
}

static void AcceptsVerifiedExternalPayloadForStatisticProjection()
{
    var report = ReadyPayloadReport();
    var payload = MatchingPayloadSnapshot(
        report,
        isExternalPayload: true,
        payloadHashVerified: true);

    WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
}

static void RejectsEmbeddedPayloadForStatisticProjection()
{
    var report = ReadyPayloadReport();
    var payload = MatchingPayloadSnapshot(
        report,
        isExternalPayload: false,
        payloadHashVerified: true);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_NOT_READY,
        () => WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload));
}

static void RejectsUnverifiedExternalPayloadHashForStatisticProjection()
{
    var report = ReadyPayloadReport();
    var payload = MatchingPayloadSnapshot(
        report,
        isExternalPayload: true,
        payloadHashVerified: false);

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_NOT_READY,
        () => WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload));
}

static void BlocksBlankDynamicFormSectionTitle()
{
    var serviceType = typeof(DynamicFormService);

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => InvokePrivateStatic<string>(
            serviceType,
            "NormalizeBlocksForSections",
            """
            [
              { "id": "s1", "title": " " }
            ]
            """,
            "[]"));
}

static void SupportsMonthlyBtTwentyFiveTableBlocks()
{
    var field = typeof(DynamicFormService).GetField(
        "MaxTableBlocksPerForm",
        BindingFlags.NonPublic | BindingFlags.Static);

    var limit = (int)(field?.GetRawConstantValue() ?? 0);
    AssertTrue(limit >= 25, "monthly BT dynamic form must allow at least 25 table blocks");
}

static void MatchesAutoApproveCondition()
{
    var fieldsJson = """
    [
      { "id": "f_status", "sectionId": "s1", "key": "ket_qua", "type": "singleSelect", "name": "Ket qua" },
      { "id": "f_score", "sectionId": "s1", "key": "diem", "type": "number", "name": "Diem" },
      { "id": "f_tags", "sectionId": "s1", "key": "nhom", "type": "multiSelect", "name": "Nhom" },
      { "id": "f_notes", "sectionId": "s1", "key": "ghi_chu", "type": "longText", "name": "Ghi chu" },
      { "id": "f_list", "sectionId": "s1", "key": "danh_sach", "type": "stringList", "name": "Danh sach" }
    ]
    """;

    var conditionJson = WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull(
        """
        { "enabled": true, "fieldKey": "ket_qua", "operator": "eq", "value": "PASS" }
        """,
        fieldsJson);

    AssertTrue(!string.IsNullOrWhiteSpace(conditionJson), "condition should normalize");
    AssertTrue(
        WorkAssignmentAutoApproveConditionNormalizer.Matches(
            conditionJson,
            """{ "values": { "f_status": "PASS", "f_score": 8 } }"""),
        "matching option code should auto approve");
    AssertFalse(
        WorkAssignmentAutoApproveConditionNormalizer.Matches(
            conditionJson,
            """{ "values": { "f_status": "FAIL", "f_score": 8 } }"""),
        "non-matching option code should not auto approve");

    var scoreConditionJson = WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull(
        """
        { "enabled": true, "fieldKey": "diem", "operator": "gte", "value": 7 }
        """,
        fieldsJson);
    AssertTrue(
        WorkAssignmentAutoApproveConditionNormalizer.Matches(
            scoreConditionJson,
            """{ "values": { "diem": "7.5" } }"""),
        "numeric compare should support field key fallback and string values");

    var tagConditionJson = WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull(
        """
        { "enabled": true, "fieldKey": "nhom", "operator": "contains", "value": "A" }
        """,
        fieldsJson);
    AssertTrue(
        WorkAssignmentAutoApproveConditionNormalizer.Matches(
            tagConditionJson,
            """{ "values": { "nhom": ["B", "A"] } }"""),
        "multi select condition should match contained option codes");

    var alwaysConditionJson = WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull(
        """
        { "enabled": true }
        """,
        fieldsJson);
    AssertTrue(!string.IsNullOrWhiteSpace(alwaysConditionJson), "no-field auto approve should normalize");
    using var alwaysDoc = JsonDocument.Parse(alwaysConditionJson!);
    AssertEqual("always", alwaysDoc.RootElement.GetProperty("operator").GetString(), "no-field auto approve should store always operator");
    AssertTrue(
        WorkAssignmentAutoApproveConditionNormalizer.Matches(alwaysConditionJson, null),
        "no-field auto approve should match without report field values");
    AssertTrue(
        WorkAssignmentAutoApproveConditionNormalizer.Matches(alwaysConditionJson, """{ "values": {} }"""),
        "no-field auto approve should match empty report field values");

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull(
            """
            { "enabled": true, "fieldKey": "ghi_chu", "operator": "contains", "value": "x" }
            """,
            fieldsJson));
    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull(
            """
            { "enabled": true, "fieldKey": "danh_sach", "operator": "contains", "value": "x" }
            """,
            fieldsJson));
}

static void NormalizesAutomaticChildSourceRulesToManual()
{
    var sectionsJson = """
    [
      { "id": "s1", "title": "Phần một" },
      { "id": "s2", "title": "Phần hai" }
    ]
    """;
    var rulesJson = """
    {
      "version": 1,
      "sectionRules": [
        { "sectionId": "s1", "sourceRule": "AGGREGATE_CHILDREN", "sourceAssignmentIds": ["child-a"] },
        { "sectionId": "s2", "sourceRule": "MAP_CHILD", "sourceAssignmentIds": ["child-b"] }
      ],
      "fieldRules": [],
      "blockRules": []
    }
    """;

    var normalized = DynamicFormDataSourceRuleNormalizer.NormalizeOrDefault(rulesJson, sectionsJson);
    using var doc = JsonDocument.Parse(normalized);
    var sectionRules = doc.RootElement.GetProperty("sectionRules").EnumerateArray().ToList();

    AssertEqual("MANUAL", sectionRules[0].GetProperty("sourceRule").GetString(), "automatic child source rule should be disabled");
    AssertEqual(0, sectionRules[0].GetProperty("sourceAssignmentIds").GetArrayLength(), "disabled automatic rule should clear saved source ids");
    AssertEqual("MAP_CHILD", sectionRules[1].GetProperty("sourceRule").GetString(), "manual mapping source rule should stay available");
    AssertEqual(
        WorkReportDataOrigin.ManualInput,
        DynamicFormDataSourceRuleNormalizer.ResolveDefaultReportDataOrigin(normalized),
        "disabled automatic source rules should keep report data origin manual");
}

static void ValidatesDynamicFormTableTargetDefaultDataType()
{
    var serviceType = typeof(DynamicFormService);

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureTableTargetLabelTypeCompatibility",
        null,
        """
        [
          {
            "blockId": "b1",
            "defaultDataType": "DATE",
            "allowedRowLabelCodes": ["deadline"]
          }
        ]
        """,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadline"] = LabelDataTypes.Date
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadline"] = LabelUsages.TableTarget
        });

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureTableTargetLabelTypeCompatibility",
            null,
            """
            [
              {
                "blockId": "b1",
                "defaultDataType": "DATE",
                "allowedRowLabelCodes": ["deadline"]
              }
            ]
            """,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deadline"] = LabelDataTypes.Number
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deadline"] = LabelUsages.TableTarget
            }));
}

static void ValidatesDynamicFormMetricLabelTargets()
{
    var serviceType = typeof(DynamicFormService);
    const string validBlocksJson = """
    [
      {
        "blockId": "b1",
        "excelSpecKind": "TOP",
        "dataRect": { "r0": 1, "c0": 0, "r1": 2, "c1": 1 },
        "defaultDataType": "NUMBER",
        "dataTypeOverrides": [
          { "scope": "COLUMN", "index": 1, "dataType": "DATE" }
        ],
        "metricLabelTargets": [
          { "range": { "r0": 1, "c0": 1, "r1": 2, "c1": 1 }, "statisticLabelCode": "deadline" }
        ]
      }
    ]
    """;

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureStatisticLabelTypeCompatibility",
        null,
        validBlocksJson,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadline"] = LabelDataTypes.Date
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadline"] = LabelUsages.Statistic
        });

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureStatisticLabelTypeCompatibility",
            null,
            validBlocksJson,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deadline"] = LabelDataTypes.Number
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["deadline"] = LabelUsages.Statistic
            }));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureUniqueLabelStatisticTargets",
            """
            [
              { "id": "f1", "sectionId": "s1", "key": "deadline", "type": "date", "name": "Deadline", "isStatistic": true, "statisticLabelCodes": ["deadline"] }
            ]
            """,
            validBlocksJson));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureTableStatisticContract",
            """
            {
              "blockId": "b1",
              "excelSpecKind": "TOP",
              "dataRect": { "r0": 1, "c0": 0, "r1": 2, "c1": 1 },
              "defaultDataType": "NUMBER",
              "dataTypeOverrides": [
                { "scope": "COLUMN", "index": 1, "dataType": "DATE" }
              ],
              "metricLabelTargets": [
                { "range": { "r0": 1, "c0": 0, "r1": 1, "c1": 1 }, "statisticLabelCode": "mixed" }
              ]
            }
            """,
            "ExcelBlockJson"));
}

static void ValidatesDynamicExcelNumericGridSpecMetadata()
{
    var serviceType = typeof(DynamicExcelService);
    const string workbookJson = """
    [
      { "row": 20, "column": 10, "data": [] }
    ]
    """;

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau so",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 3,
          "dataRows": 2,
          "dataTypeOverrides": [
            { "scope": "COLUMN", "index": 1, "dataType": "DATE" }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 2, 2),
        3,
        2);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau lua chon",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 3,
          "dataRows": 2,
          "defaultDataType": "SHORT_TEXT",
          "defaultOptions": [
            { "code": "A", "label": "Lua chon A" },
            { "code": "B", "label": "Lua chon B" }
          ],
          "dataTypeOverrides": [
            { "scope": "COLUMN", "index": 1, "dataType": "BOOLEAN" }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 2, 2),
        3,
        2);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau bang rong 250 cot",
        "FIXED_GRID",
        """
        [
          { "row": 2, "column": 250, "data": [] }
        ]
        """,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 250,
          "dataRows": 1
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 1, 249),
        250,
        1);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau ma tran lon 14x85",
        "FIXED_GRID",
        """
        [
          { "row": 14, "column": 85, "data": [] }
        ]
        """,
        """
        {
          "kind": "MATRIX",
          "topRows": 2,
          "topCols": 81,
          "leftRows": 12,
          "leftCols": 4,
          "specialRanges": [
            { "role": "FORMULA", "r0": 2, "c0": 4, "r1": 2, "c1": 84 }
          ]
        }
        """,
        new DynamicExcelDataRectDto(2, 4, 13, 84),
        81,
        12);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau BT03 334x18",
        "FIXED_GRID",
        """
        [
          { "row": 334, "column": 18, "data": [] }
        ]
        """,
        """
        {
          "kind": "MATRIX",
          "topRows": 5,
          "topCols": 14,
          "leftRows": 329,
          "leftCols": 4
        }
        """,
        new DynamicExcelDataRectDto(5, 4, 333, 17),
        14,
        329);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau BT15 334x37 co vung dac biet",
        "FIXED_GRID",
        """
        [
          { "row": 334, "column": 37, "data": [] }
        ]
        """,
        """
        {
          "kind": "MATRIX",
          "topRows": 5,
          "topCols": 34,
          "leftRows": 329,
          "leftCols": 3,
          "specialRanges": [
            { "role": "FORMULA", "r0": 5, "c0": 3, "r1": 72, "c1": 36 }
          ]
        }
        """,
        new DynamicExcelDataRectDto(5, 3, 333, 36),
        34,
        329);

    AssertTrue(
        DynamicExcelRuntimePolicy.ShouldDisableBackgroundTableStatistics(891),
        "large 891-cell Dynamic Excel matrix should save but disable background table statistics");
    AssertTrue(
        DynamicExcelRuntimePolicy.CanRunDirectTableAggregation(891),
        "large 891-cell Dynamic Excel matrix should still support direct table aggregation");
    AssertTrue(
        DynamicExcelRuntimePolicy.CanRunDirectTableAggregation(11000),
        "direct table aggregation should allow the 11000-cell validation limit");
    AssertFalse(
        DynamicExcelRuntimePolicy.CanRunDirectTableAggregation(11001),
        "direct table aggregation should reject tables above the 11000-cell validation limit");

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau cong thuc tieu de va bo trong dang vung",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 5,
          "dataRows": 4,
          "specialRanges": [
            { "role": "FORMULA", "r0": 1, "c0": 0, "r1": 2, "c1": 1 },
            { "role": "TITLE", "r0": 1, "c0": 3, "r1": 2, "c1": 4 },
            { "role": "BLANK", "r0": 3, "c0": 3, "r1": 4, "c1": 4 }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 4, 4),
        5,
        4);

    using (var nonInputPolicyDoc = JsonDocument.Parse("""
        {
          "specialRanges": [
            { "role": "FORMULA", "r0": 1, "c0": 0, "r1": 2, "c1": 1 },
            { "role": "TITLE", "r0": 1, "c0": 3, "r1": 2, "c1": 4 },
            { "role": "BLANK", "r0": 3, "c0": 3, "r1": 4, "c1": 4 }
          ]
        }
        """))
    {
        var dataRect = new DynamicExcelRuntimeRect(1, 0, 4, 4);
        var ranges = DynamicExcelRuntimePolicy.ReadSpecialRanges(nonInputPolicyDoc.RootElement, dataRect);
        AssertEqual(3, ranges.Count, "formula/title/blank rectangular ranges should be accepted by runtime policy");
        AssertEqual(8, DynamicExcelRuntimePolicy.CountInputCells(dataRect, ranges), "formula/title/blank ranges should be skipped from input cell count");
    }

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau special range chong",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "TOP",
              "topRows": 1,
              "topCols": 3,
              "dataRows": 2,
              "specialRanges": [
                { "role": "FORMULA", "r0": 1, "c0": 0, "r1": 1, "c1": 1 },
                { "role": "BLANK", "r0": 1, "c0": 1, "r1": 2, "c1": 1 }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau ma tran phan vung kin",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "MATRIX",
          "topRows": 1,
          "topCols": 3,
          "leftRows": 2,
          "leftCols": 1,
          "dataTypeOverrides": [
            { "scope": "RANGE", "r0": 1, "c0": 1, "r1": 2, "c1": 1, "dataType": "NUMBER" },
            { "scope": "RANGE", "r0": 1, "c0": 2, "r1": 2, "c1": 2, "dataType": "DATE" },
            { "scope": "RANGE", "r0": 1, "c0": 3, "r1": 2, "c1": 3, "dataType": "NUMBER" }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 1, 2, 3),
        3,
        2);

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau ma tran vung chong",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "MATRIX",
              "topRows": 1,
              "topCols": 2,
              "leftRows": 2,
              "leftCols": 1,
              "dataTypeOverrides": [
                { "scope": "RANGE", "r0": 1, "c0": 1, "r1": 2, "c1": 1, "dataType": "NUMBER" },
                { "scope": "RANGE", "r0": 2, "c0": 1, "r1": 2, "c1": 2, "dataType": "DATE" }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 1, 2, 2),
            2,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau ma tran thieu vung",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "MATRIX",
              "topRows": 1,
              "topCols": 2,
              "leftRows": 2,
              "leftCols": 1,
              "dataTypeOverrides": [
                { "scope": "RANGE", "r0": 1, "c0": 1, "r1": 1, "c1": 2, "dataType": "NUMBER" },
                { "scope": "RANGE", "r0": 2, "c0": 1, "r1": 2, "c1": 1, "dataType": "DATE" }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 1, 2, 2),
            2,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau so",
            "FIXED_GRID",
            workbookJson,
            """
            { "kind": "TOP", "topRows": 1, "topCols": 3, "dataRows": 2 }
            """,
            new DynamicExcelDataRectDto(0, 0, 1, 2),
            3,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau so",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "TOP",
              "topRows": 1,
              "topCols": 3,
              "dataRows": 2,
              "defaultDataType": "LONG_TEXT"
            }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau van ban ngan",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 3,
          "dataRows": 2,
          "defaultDataType": "SHORT_TEXT",
          "defaultOptions": [
            { "code": "DAT", "label": "Dat" },
            { "code": "CHUA_DAT", "label": "Chua dat" }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 2, 2),
        3,
        2);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau chon nhieu",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 3,
          "dataRows": 2,
          "dataTypeOverrides": [
            {
              "scope": "COLUMN",
              "index": 1,
              "dataType": "MULTI_SELECT",
              "options": [
                { "code": "PV01", "label": "PV01" },
                { "code": "PX01", "label": "PX01" },
                { "code": "PA06", "label": "PA06" }
              ]
            }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 2, 2),
        3,
        2);

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelPayloadCore",
        "Mau ignore va bo trong",
        "FIXED_GRID",
        workbookJson,
        """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 3,
          "dataRows": 2,
          "dataTypeOverrides": [
            { "scope": "COLUMN", "index": 2, "dataType": "IGNORE" }
          ],
          "specialRanges": [
            { "role": "BLANK", "r0": 2, "c0": 0, "r1": 2, "c1": 2 }
          ]
        }
        """,
        new DynamicExcelDataRectDto(1, 0, 2, 2),
        3,
        2);

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau lua chon",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "TOP",
              "topRows": 1,
              "topCols": 3,
              "dataRows": 2,
              "dataTypeOverrides": [
                { "scope": "COLUMN", "index": 1, "dataType": "STRING_LIST" }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau so",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "TOP",
              "topRows": 1,
              "topCols": 3,
              "dataRows": 2,
              "dataTypeOverrides": [
                { "scope": "ROW", "index": 1, "dataType": "DATE" }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau ma tran",
            "FIXED_GRID",
            workbookJson,
            """
            {
              "kind": "MATRIX",
              "topRows": 1,
              "topCols": 3,
              "leftRows": 2,
              "leftCols": 1,
              "dataTypeOverrides": [
                { "scope": "RANGE", "r0": 0, "c0": 1, "r1": 1, "c1": 2, "dataType": "DATE" }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 1, 2, 3),
            3,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau co du lieu trong o nhap",
            "FIXED_GRID",
            """
            [
              {
                "row": 3,
                "column": 3,
                "data": [
                  [{ "v": "Header" }, null, null],
                  [{ "v": 123 }, null, null]
                ]
              }
            ]
            """,
            """
            { "kind": "TOP", "topRows": 1, "topCols": 3, "dataRows": 2 }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelPayloadCore",
            "Mau co script",
            "FIXED_GRID",
            """
            [
              {
                "row": 3,
                "column": 3,
                "data": [
                  [{ "v": "<script>alert(1)</script>" }, null, null]
                ]
              }
            ]
            """,
            """
            { "kind": "TOP", "topRows": 1, "topCols": 3, "dataRows": 2 }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    var semanticTemplate = new DynamicExcelTemplate
    {
        Id = ObjectId(90),
        Code = "DX_SEMANTIC",
        Name = "Mau semantic",
        TableMode = "FIXED_GRID",
        ContractVersion = 1,
        CreatedByUsername = "tester",
        RawWorkbookDataJson = workbookJson,
        SpecJson = """
        {
          "kind": "TOP",
          "topRows": 1,
          "topCols": 3,
          "dataRows": 2,
          "specialRanges": [
            { "role": "BLANK", "r0": 2, "c0": 2, "r1": 2, "c1": 2 }
          ]
        }
        """,
        DataRectR0 = 1,
        DataRectC0 = 0,
        DataRectR1 = 2,
        DataRectC1 = 2,
        W = 3,
        H = 2
    };

    InvokePrivateStatic<object?>(
        serviceType,
        "ValidateDynamicExcelSemanticUpdateContract",
        semanticTemplate,
        new CreateDynamicExcelReq(
            semanticTemplate.Code,
            "Mau semantic update",
            "FIXED_GRID",
            1,
            workbookJson,
            """
            {
              "kind": "TOP",
              "topRows": 1,
              "topCols": 3,
              "dataRows": 2,
              "defaultDataType": "NUMBER",
              "defaultOptions": [],
              "dataTypeOverrides": [],
              "specialRanges": [
                { "role": "FORMULA", "r0": 1, "c0": 1, "r1": 1, "c1": 1 },
                { "role": "TITLE", "r0": 1, "c0": 2, "r1": 1, "c1": 2 },
                { "role": "BLANK", "r0": 2, "c0": 2, "r1": 2, "c1": 2 }
              ]
            }
            """,
            new DynamicExcelDataRectDto(1, 0, 2, 2),
            3,
            2));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelSemanticUpdateContract",
            semanticTemplate,
            new CreateDynamicExcelReq(
                semanticTemplate.Code,
                "Mau doi schema",
                "FIXED_GRID",
                1,
                workbookJson,
                """
                {
                  "kind": "TOP",
                  "topRows": 1,
                  "topCols": 4,
                  "dataRows": 2,
                  "specialRanges": [
                    { "role": "BLANK", "r0": 2, "c0": 2, "r1": 2, "c1": 2 }
                  ]
                }
                """,
                new DynamicExcelDataRectDto(1, 0, 2, 2),
                3,
                2)));

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "ValidateDynamicExcelSemanticUpdateContract",
            semanticTemplate,
            new CreateDynamicExcelReq(
                semanticTemplate.Code,
                "Mau doi blank",
                "FIXED_GRID",
                1,
                workbookJson,
                """
                {
                  "kind": "TOP",
                  "topRows": 1,
                  "topCols": 3,
                  "dataRows": 2,
                  "specialRanges": [
                    { "role": "BLANK", "r0": 2, "c0": 1, "r1": 2, "c1": 1 }
                  ]
                }
                """,
                new DynamicExcelDataRectDto(1, 0, 2, 2),
                3,
                2)));
}

static void CompressesValues1DBlankRuns()
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var smallValues = new List<object?>();
    smallValues.AddRange(Enumerable.Repeat<object?>(null, Values1DCompression.MinValues1DCompressionLength - 1));
    var smallJson = Values1DCompression.Serialize(smallValues, options);
    AssertFalse(
        smallJson.Contains("\"values1DCompressed\":true", StringComparison.Ordinal),
        "values1D at or below compression threshold should stay dense");

    var values = new List<object?> { 1, "A" };
    values.AddRange(Enumerable.Repeat<object?>(null, 300));
    values.Add(true);
    values.Add(null);
    values.Add(3);
    var denseJson = JsonSerializer.Serialize(values, options);

    var compressedJson = Values1DCompression.Serialize(values, options);
    AssertTrue(
        compressedJson.Contains("\"values1DCompressed\":true", StringComparison.Ordinal),
        "top-level values1D should compress long null runs");
    AssertEqual(
        denseJson,
        JsonSerializer.Serialize(Values1DCompression.DeserializeObjects(compressedJson, options), options),
        "compressed top-level values1D should round-trip to dense values");

    var tableValuesJson = JsonSerializer.Serialize(new
    {
        blocks = new[]
        {
            new
            {
                blockId = "b1",
                values1D = values
            }
        }
    }, options);
    var compressedTableJson = Values1DCompression.CompressTableValuesJson(tableValuesJson, options) ?? "";
    AssertTrue(
        compressedTableJson.Contains("\"values1DCompressed\":true", StringComparison.Ordinal),
        "table block values1D should store compression metadata");

    using var compressedDoc = JsonDocument.Parse(compressedTableJson);
    var block = compressedDoc.RootElement.GetProperty("blocks")[0];
    AssertEqual(values.Count, Values1DCompression.ReadBlockValuesLength(block) ?? -1, "compressed block should report original values1D length");
    using var reader = Values1DCompression.CreateBlockReader(block);
    AssertTrue(reader is not null && reader.IsCompressed, "compressed block should create an indexed values1D reader");
    AssertEqual(values.Count, reader!.Length, "indexed reader should expose original values1D length");
    AssertEqual<decimal?>(1m, reader.ReadDecimal(0), "indexed reader should read value before compressed run");
    AssertEqual<decimal?>(null, reader.ReadDecimal(2), "indexed reader should return null inside compressed run");
    AssertEqual<decimal?>(3m, reader.ReadDecimal(values.Count - 1), "indexed reader should read value after compressed run");
    AssertEqual(
        denseJson,
        JsonSerializer.Serialize(Values1DCompression.ReadBlockObjects(block, options), options),
        "compressed table block values1D should expand to dense values");

    var zeroValues = new List<object?> { "head" };
    zeroValues.AddRange(Enumerable.Repeat<object?>(0, 300));
    zeroValues.Add(5);
    var denseZeroJson = JsonSerializer.Serialize(zeroValues, options);
    var compressedZeroJson = Values1DCompression.Serialize(zeroValues, options);
    AssertTrue(
        compressedZeroJson.Contains("\"values1DCompressed\":true", StringComparison.Ordinal),
        "top-level values1D should compress long zero runs");
    AssertEqual(
        denseZeroJson,
        JsonSerializer.Serialize(Values1DCompression.DeserializeObjects(compressedZeroJson, options), options),
        "compressed zero-run values1D should round-trip to dense values");

    using var compressedZeroDoc = JsonDocument.Parse(compressedZeroJson);
    using var zeroReader = Values1DCompression.CreateBlockReader(compressedZeroDoc.RootElement);
    AssertTrue(zeroReader is not null && zeroReader.IsCompressed, "zero-run payload should create an indexed values1D reader");
    AssertEqual<decimal?>(0m, zeroReader!.ReadDecimal(1), "indexed reader should return zero inside compressed zero run");
    AssertEqual<decimal?>(5m, zeroReader.ReadDecimal(zeroValues.Count - 1), "indexed reader should read value after compressed zero run");
}

static void BasicSummaryNormalizesTypedMethods()
{
    var normalize = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "NormalizeOperation",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "NormalizeOperation");

    var cases = new (string? Input, string Expected)[]
    {
        ("SUM", "SUM"),
        ("count", "COUNT"),
        ("average", "MEAN"),
        ("MIN", "MIN"),
        ("MAX", "MAX"),
        ("JOIN", "JOIN"),
        ("sample", "JOIN"),
        ("MAX_DATE", "MAX_DATE"),
        ("latest_date", "MAX_DATE"),
        ("TRUE_COUNT", "TRUE_COUNT"),
        ("BUCKET_COUNT", "BUCKET_COUNT"),
        ("option_count", "BUCKET_COUNT"),
        (null, "SUM"),
        ("PERCENTILE", "SUM")
    };

    foreach (var (input, expected) in cases)
    {
        var actual = (string)normalize.Invoke(null, new object?[] { input })!;
        AssertEqual(expected, actual, $"basic summary operation should normalize {input ?? "<null>"}");
    }
}

static void BasicSummarySupportsTypedDefaultMethods()
{
    var normalize = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "NormalizeDefaultMethods",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "NormalizeDefaultMethods");
    var normalizeForDataType = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "NormalizeOperationForDataType",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "NormalizeOperationForDataType");

    var typedDefaults = new WorkAssignmentBasicSummaryDefaultMethodsDto
    {
        Number = "average",
        Date = "latest_date",
        Boolean = "false_count",
        Text = "sample",
        Selection = "option_count"
    };

    var typedNormalized = normalize.Invoke(null, new object?[] { typedDefaults })!;
    AssertEqual("MEAN", GetReflectedProperty<string>(typedNormalized, "Number"), "basic summary should normalize numeric default method");
    AssertEqual("MAX_DATE", GetReflectedProperty<string>(typedNormalized, "Date"), "basic summary should normalize date default method");
    AssertEqual("FALSE_COUNT", GetReflectedProperty<string>(typedNormalized, "Boolean"), "basic summary should normalize boolean default method");
    AssertEqual("JOIN", GetReflectedProperty<string>(typedNormalized, "Text"), "basic summary should normalize text default method");
    AssertEqual("BUCKET_COUNT", GetReflectedProperty<string>(typedNormalized, "Selection"), "basic summary should normalize selection default method");

    var legacyNumericDefaults = new WorkAssignmentBasicSummaryDefaultMethodsDto
    {
        Number = "count",
        Date = "SUM",
        Boolean = "MAX",
        Text = null,
        Selection = ""
    };

    var normalized = normalize.Invoke(null, new object?[] { legacyNumericDefaults })!;
    AssertEqual("COUNT", GetReflectedProperty<string>(normalized, "Number"), "basic summary should keep numeric default method");
    AssertEqual("MAX_DATE", GetReflectedProperty<string>(normalized, "Date"), "basic summary should fall back legacy numeric date default");
    AssertEqual("TRUE_COUNT", GetReflectedProperty<string>(normalized, "Boolean"), "basic summary should fall back legacy numeric boolean default");
    AssertEqual("COUNT", GetReflectedProperty<string>(normalized, "Text"), "basic summary should fall back blank text default");
    AssertEqual("BUCKET_COUNT", GetReflectedProperty<string>(normalized, "Selection"), "basic summary should fall back blank selection default");

    AssertThrowsFromReflection(
        AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_MODE_INVALID,
        () => normalize.Invoke(null, new object?[]
        {
            new WorkAssignmentBasicSummaryDefaultMethodsDto { Text = "PERCENTILE" }
        }));

    AssertThrowsFromReflection(
        AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_MODE_INVALID,
        () => normalize.Invoke(null, new object?[]
        {
            new WorkAssignmentBasicSummaryDefaultMethodsDto { Selection = "CONDITION" }
        }));

    AssertEqual(
        "MAX_DATE",
        (string)normalizeForDataType.Invoke(null, new object?[] { "SUM", "DATE", "MAX_DATE" })!,
        "basic summary rule should fall back when a numeric method targets date data");
    AssertEqual(
        "BUCKET_COUNT",
        (string)normalizeForDataType.Invoke(null, new object?[] { "SUM", "MULTI_SELECT", "BUCKET_COUNT" })!,
        "basic summary rule should fall back when a numeric method targets choice data");
    AssertEqual(
        "JOIN",
        (string)normalizeForDataType.Invoke(null, new object?[] { "sample", "SHORT_TEXT", "COUNT" })!,
        "basic summary rule should keep a typed text method");
}

static void BasicSummaryRejectsTableMethodRules()
{
    var normalizeRules = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "NormalizeRules",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "NormalizeRules");

    var fieldRules = (List<WorkAssignmentBasicSummaryRuleDto>)normalizeRules.Invoke(null, new object?[]
    {
        new List<WorkAssignmentBasicSummaryRuleDto>
        {
            new()
            {
                TargetKind = "FIELD",
                TargetKey = "field:score",
                Operation = "average"
            }
        }
    })!;

    AssertEqual(1, fieldRules.Count, "basic summary should still allow field-level method overrides");
    AssertEqual("FIELD", fieldRules[0].TargetKind, "basic summary field rule should normalize target kind");
    AssertEqual("MEAN", fieldRules[0].Operation, "basic summary field rule should normalize operation");

    AssertThrowsFromReflection(
        AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_MODE_INVALID,
        () => normalizeRules.Invoke(null, new object?[]
        {
            new List<WorkAssignmentBasicSummaryRuleDto>
            {
                new()
                {
                    TargetKind = "TABLE",
                    TargetKey = "table:block_1:index:0",
                    Operation = "SUM"
                }
            }
        }));
}

static void BasicSummaryRefreshStatusControlsEnqueue()
{
    var shouldEnqueue = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "ShouldEnqueueSnapshotRefresh",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "ShouldEnqueueSnapshotRefresh");

    bool Invoke(string? status, bool forceRefresh)
        => (bool)shouldEnqueue.Invoke(null, new object?[] { status, forceRefresh })!;

    AssertTrue(Invoke(null, false), "missing refresh status should enqueue a basic summary refresh");
    AssertTrue(Invoke("DONE", false), "dirty completed snapshot should enqueue a refresh");
    AssertFalse(Invoke("QUEUED", false), "queued snapshot should not enqueue duplicate refresh jobs");
    AssertFalse(Invoke("RUNNING", true), "running snapshot should not enqueue duplicate refresh jobs even on force refresh");
    AssertFalse(Invoke("FAILED", false), "failed snapshot should wait for explicit reset/force refresh");
    AssertTrue(Invoke("FAILED", true), "force refresh should reset a failed basic summary job");
}

static void BasicSummaryExtractsTypedTableValues()
{
    var extract = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "ExtractTableValues",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "ExtractTableValues");

    var tableValuesJson = """
        {
          "blocks": [
            {
              "blockId": "block_1",
              "tableMode": "FIXED_GRID",
              "w": 6,
              "h": 1,
              "metricDefinitions": [
                { "index": 0, "metricKey": "num", "rowKey": "r1", "columnKey": "c1", "dataType": "NUMBER" },
                { "index": 1, "metricKey": "flag", "rowKey": "r1", "columnKey": "c2", "dataType": "BOOLEAN" },
                { "index": 2, "metricKey": "date", "rowKey": "r1", "columnKey": "c3", "dataType": "FULL_DATE" },
                { "index": 3, "metricKey": "text", "rowKey": "r1", "columnKey": "c4", "dataType": "SHORT_TEXT" },
                {
                  "index": 4,
                  "metricKey": "choice",
                  "rowKey": "r1",
                  "columnKey": "c5",
                  "dataType": "SINGLE_SELECT",
                  "options": [{ "code": "A", "label": "Alpha" }]
                },
                {
                  "index": 5,
                  "metricKey": "choices",
                  "rowKey": "r1",
                  "columnKey": "c6",
                  "dataType": "MULTI_SELECT",
                  "options": [
                    { "code": "A", "label": "Alpha" },
                    { "code": "B", "label": "Beta" }
                  ]
                }
              ],
              "values1D": [12.5, true, "20/06/2026", "ghi chú", "Alpha", ["B", "A"]]
            }
          ]
        }
        """;

    var skipped = new HashSet<string>(StringComparer.Ordinal);
    var rows = ((System.Collections.IEnumerable)extract.Invoke(null, new object?[] { tableValuesJson, skipped })!)
        .Cast<object>()
        .ToList();

    AssertEqual(7, rows.Count, "basic summary should extract every typed table value, expanding multi-select buckets");
    AssertEqual(0, skipped.Count, "typed table fixture should stay below direct aggregate limit");

    var byMetric = rows.GroupBy(row => GetReflectedProperty<string>(row, "MetricKey") ?? string.Empty, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    AssertEqual<decimal?>(12.5m, GetReflectedProperty<decimal?>(byMetric["num"][0], "NumericValue"), "basic summary should extract numeric table value");
    AssertEqual<bool?>(true, GetReflectedProperty<bool?>(byMetric["flag"][0], "BooleanValue"), "basic summary should extract boolean table value");
    AssertEqual<DateTime?>(
        new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
        GetReflectedProperty<DateTime?>(byMetric["date"][0], "DateValueUtc"),
        "basic summary should extract full date table value");
    AssertEqual("ghi chú", GetReflectedProperty<string>(byMetric["text"][0], "TextValue"), "basic summary should extract text table value");
    AssertEqual("A", GetReflectedProperty<string>(byMetric["choice"][0], "BucketKey"), "basic summary should resolve single choice code from label");
    AssertEqual("Alpha", GetReflectedProperty<string>(byMetric["choice"][0], "BucketLabel"), "basic summary should resolve single choice label");

    var choiceKeys = byMetric["choices"]
        .Select(row => GetReflectedProperty<string>(row, "BucketKey"))
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToList();
    AssertEqual("A,B", string.Join(",", choiceKeys), "basic summary should expand multiple choice codes");
}

static void BasicSummaryMergesPeriodSnapshotsByTypedMethod()
{
    var merge = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "MergeSummaryItems",
        BindingFlags.NonPublic | BindingFlags.Static,
        null,
        new[] { typeof(IGrouping<string, WorkAssignmentBasicSummaryItemDto>) },
        null)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "MergeSummaryItems");

    var items = new List<WorkAssignmentBasicSummaryItemDto>
    {
        new()
        {
            TargetKind = "TABLE",
            TargetKey = "table:block_1:revenue",
            BlockId = "block_1",
            MetricKey = "revenue",
            Label = "Revenue",
            DataType = "NUMBER",
            Operation = "MEAN",
            ValueCount = 2,
            ReportCount = 2,
            Sum = 10m,
            Min = 4m,
            Max = 6m,
            Mean = 5m
        },
        new()
        {
            TargetKind = "TABLE",
            TargetKey = "table:block_1:revenue",
            BlockId = "block_1",
            MetricKey = "revenue",
            Label = "Revenue",
            DataType = "NUMBER",
            Operation = "MEAN",
            ValueCount = 2,
            ReportCount = 2,
            Sum = 20m,
            Min = 8m,
            Max = 12m,
            Mean = 10m
        }
    };

    var group = items.GroupBy(x => x.TargetKey, StringComparer.Ordinal).First();
    var merged = (WorkAssignmentBasicSummaryItemDto)merge.Invoke(null, new object?[] { group })!;

    AssertEqual(4, merged.ValueCount, "merged numeric summary should sum value counts");
    AssertEqual(4, merged.ReportCount, "merged numeric summary should sum report counts across periods");
    AssertEqual<decimal?>(30m, merged.Sum, "merged numeric summary should sum period sums");
    AssertEqual<decimal?>(4m, merged.Min, "merged numeric summary should keep global min");
    AssertEqual<decimal?>(12m, merged.Max, "merged numeric summary should keep global max");
    AssertEqual<decimal?>(7.5m, merged.Mean, "merged numeric summary should compute weighted mean from sum/count");
    AssertEqual(7.5m, (decimal)merged.Value!, "MEAN operation value should be the weighted mean");

    var booleanItems = new List<WorkAssignmentBasicSummaryItemDto>
    {
        new()
        {
            TargetKind = "FIELD",
            TargetKey = "field:approved",
            FieldId = "approved",
            Label = "Approved",
            DataType = "BOOLEAN",
            Operation = "TRUE_COUNT",
            ValueCount = 2,
            ReportCount = 2,
            TrueCount = 1,
            FalseCount = 1
        },
        new()
        {
            TargetKind = "FIELD",
            TargetKey = "field:approved",
            FieldId = "approved",
            Label = "Approved",
            DataType = "BOOLEAN",
            Operation = "TRUE_COUNT",
            ValueCount = 2,
            ReportCount = 2,
            TrueCount = 2,
            FalseCount = 0
        }
    };
    var mergedBoolean = (WorkAssignmentBasicSummaryItemDto)merge.Invoke(
        null,
        new object?[] { booleanItems.GroupBy(x => x.TargetKey, StringComparer.Ordinal).First() })!;
    AssertEqual(4, mergedBoolean.ValueCount, "merged boolean summary should sum value counts");
    AssertEqual<int?>(3, mergedBoolean.TrueCount, "merged boolean summary should sum true counts");
    AssertEqual<int?>(1, mergedBoolean.FalseCount, "merged boolean summary should sum false counts");
    AssertEqual(3, (int)mergedBoolean.Value!, "TRUE_COUNT operation value should be the merged true count");

    var bucketItems = new List<WorkAssignmentBasicSummaryItemDto>
    {
        new()
        {
            TargetKind = "TABLE",
            TargetKey = "table:block_1:choice",
            BlockId = "block_1",
            MetricKey = "choice",
            Label = "Choice",
            DataType = "MULTI_SELECT",
            Operation = "BUCKET_COUNT",
            ValueCount = 2,
            ReportCount = 1,
            Buckets = new List<WorkAssignmentBasicSummaryBucketDto>
            {
                new() { Key = "A", Label = "Alpha", Count = 1 },
                new() { Key = "B", Label = "Beta", Count = 1 }
            }
        },
        new()
        {
            TargetKind = "TABLE",
            TargetKey = "table:block_1:choice",
            BlockId = "block_1",
            MetricKey = "choice",
            Label = "Choice",
            DataType = "MULTI_SELECT",
            Operation = "BUCKET_COUNT",
            ValueCount = 2,
            ReportCount = 1,
            Buckets = new List<WorkAssignmentBasicSummaryBucketDto>
            {
                new() { Key = "A", Label = "Alpha", Count = 2 }
            }
        }
    };
    var mergedBuckets = (WorkAssignmentBasicSummaryItemDto)merge.Invoke(
        null,
        new object?[] { bucketItems.GroupBy(x => x.TargetKey, StringComparer.Ordinal).First() })!;
    AssertEqual(4, mergedBuckets.ValueCount, "merged choice summary should sum value counts");
    AssertEqual(2, mergedBuckets.Buckets.Count, "merged choice summary should keep distinct buckets");
    AssertEqual(3, mergedBuckets.Buckets.First(x => x.Key == "A").Count, "merged choice summary should sum matching bucket counts");
    AssertTrue(ReferenceEquals(mergedBuckets.Value, mergedBuckets.Buckets), "BUCKET_COUNT operation value should reuse merged buckets");
}

static void BasicSummaryCompactSnapshotRoundTrips()
{
    var serialize = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "SerializeSnapshotJson",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "SerializeSnapshotJson");

    var deserialize = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "DeserializeSnapshot",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "DeserializeSnapshot");

    const string blockId = "block_1";
    var response = new WorkAssignmentBasicSummaryResponse
    {
        Tables = new List<WorkAssignmentBasicSummaryItemDto>
        {
            new()
            {
                TargetKind = "TABLE",
                TargetKey = $"table:{blockId}:table:{blockId}.row:row_1.column:col_4",
                BlockId = blockId,
                TableMode = "FIXED_GRID",
                MetricKey = $"table:{blockId}.row:row_1.column:col_4",
                RowKey = "row_1",
                ColumnKey = "col_4",
                Index = 3,
                Label = $"table:{blockId}.row:row_1.column:col_4",
                DataType = "NUMBER",
                Operation = "SUM",
                Value = 10m,
                ValueCount = 1,
                ReportCount = 1,
                Sum = 10m,
                Min = 10m,
                Max = 10m,
                Mean = 10m
            },
            new()
            {
                TargetKind = "TABLE",
                TargetKey = $"table:{blockId}:table:{blockId}.row:row_22.column:col_7",
                BlockId = blockId,
                TableMode = "FIXED_GRID",
                MetricKey = $"table:{blockId}.row:row_22.column:col_7",
                RowKey = "row_22",
                ColumnKey = "col_7",
                Index = 300,
                Label = $"table:{blockId}.row:row_22.column:col_7",
                DataType = "NUMBER",
                Operation = "SUM",
                Value = 20m,
                ValueCount = 1,
                ReportCount = 1,
                Sum = 20m,
                Min = 20m,
                Max = 20m,
                Mean = 20m
            }
        },
        Sources = new List<WorkAssignmentBasicSummarySourceDto>
        {
            new() { WorkAssignmentReportId = ObjectId(301), WorkAssignmentId = ObjectId(302), PeriodKey = "20260616" }
        },
        Warnings = new List<string> { "ok" }
    };

    var snapshotJson = (string)serialize.Invoke(null, new object?[] { response })!;
    AssertFalse(snapshotJson.StartsWith("gzip:", StringComparison.Ordinal), "basic summary snapshot should stay readable JSON, not gzip");
    AssertTrue(snapshotJson.Contains("\"tb\"", StringComparison.Ordinal), "basic summary snapshot should store compact table blocks");
    AssertTrue(snapshotJson.Contains("\"values1DCompressed\":true", StringComparison.Ordinal), "sparse table vectors should reuse values1D null-run compression");
    AssertFalse(snapshotJson.Contains("workAssignmentReportId", StringComparison.Ordinal), "basic summary snapshot should not embed source rows");
    AssertFalse(snapshotJson.Contains($"table:{blockId}:table:{blockId}.row", StringComparison.Ordinal), "compact table snapshot should not store long target keys per cell");

    var snapshot = new WorkAssignmentBasicSummarySnapshot { SnapshotJson = snapshotJson };
    var decoded = (WorkAssignmentBasicSummaryResponse)deserialize.Invoke(
        null,
        new object?[] { snapshot, new List<WorkAssignment>(), new List<WorkAssignmentReport>() })!;

    AssertEqual(2, decoded.Tables.Count, "compact snapshot should deserialize table cells");
    AssertEqual(0, decoded.Sources.Count, "compact snapshot sources should hydrate from source reports, not snapshot JSON");
    AssertEqual($"table:{blockId}:table:{blockId}.row:row_22.column:col_7", decoded.Tables[1].TargetKey, "compact snapshot should rebuild target keys from block/index");
    AssertEqual(301, decoded.SummaryValues.Tables[0].Values1D.Count, "compact snapshot should rebuild sparse values1D shape");
    AssertEqual(20m, (decimal)decoded.SummaryValues.Tables[0].Values1D[300]!, "compact snapshot should rebuild summary values");
}

static void BasicSummaryRespectsCompressedTableNullRuns()
{
    var extract = typeof(WorkAssignmentBasicSummaryService).GetMethod(
        "ExtractTableValues",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentBasicSummaryService), "ExtractTableValues");

    var tableValuesJson = """
        {
          "blocks": [
            {
              "blockId": "block_1",
              "tableMode": "FIXED_GRID",
              "w": 4,
              "h": 3,
              "statisticsInputCellCount": 12,
              "values1DCompressed": true,
              "values1DCompression": "NULL_RUNS",
              "values1DLength": 12,
              "values1D": [null, 11, 22, 33, 44],
              "values1DCompressedIndexes": [0],
              "values1DCompressedCounts": [8]
            }
          ]
        }
        """;

    var skipped = new HashSet<string>(StringComparer.Ordinal);
    var rows = ((System.Collections.IEnumerable)extract.Invoke(null, new object?[] { tableValuesJson, skipped })!)
        .Cast<object>()
        .ToList();
    var indexes = rows
        .Select(row => GetReflectedProperty<int>(row, "Index"))
        .OrderBy(x => x)
        .ToList();

    AssertEqual("8,9,10,11", string.Join(",", indexes), "basic summary should not read raw compressed values for null-run cells");
    AssertEqual(0, skipped.Count, "compressed null-run fixture should stay below direct aggregate limit");
}

static void ResolvesLegacyWorkBasisFileAsWorkDocument()
{
    var workId = ObjectId(7);
    var file = new FileDoc
    {
        Id = ObjectId(8),
        SourceType = WorkDocumentConstants.SourceTypeWorkBasis,
        SourceId = workId
    };

    var scope = WorkDocumentScopeResolver.Resolve(file);

    AssertEqual(WorkDocumentConstants.ScopeWork, scope.Scope, "legacy work basis should become work-scope document");
    AssertEqual(workId, scope.WorkId, "legacy work basis should use SourceId as work id");
    AssertEqual<string?>(null, scope.AssignmentId, "legacy work basis should not have assignment id");
}

static void ResolvesAssignmentFileAsBranchDocument()
{
    var workId = ObjectId(7);
    var assignmentId = ObjectId(8);
    var file = new FileDoc
    {
        Id = ObjectId(9),
        SourceType = WorkDocumentConstants.SourceTypeAssignmentDocument,
        SourceId = assignmentId,
        WorkId = workId,
        AssignmentCode = "CV-001",
        AssignmentPath = $"/{ObjectId(1)}/{assignmentId}"
    };

    var scope = WorkDocumentScopeResolver.Resolve(file);

    AssertEqual(WorkDocumentConstants.ScopeAssignmentBranch, scope.Scope, "assignment source should become assignment-branch scope");
    AssertEqual(workId, scope.WorkId, "assignment source should keep work id");
    AssertEqual(assignmentId, scope.AssignmentId, "assignment source should use source id as assignment id fallback");
    AssertEqual("CV-001", scope.AssignmentCode, "assignment code should be preserved");
}

static void ResolvesAssignmentDocumentAncestorsFromPath()
{
    var rootId = ObjectId(1);
    var childId = ObjectId(2);
    var assignmentId = ObjectId(3);
    var siblingId = ObjectId(4);

    var ids = WorkDocumentScopeResolver.ParseAssignmentPath($"/{rootId}/{childId}/{assignmentId}", assignmentId);

    AssertSequenceEqual(
        new[] { rootId, childId, assignmentId },
        ids,
        "assignment document readers should be resolved from the attachment node and ancestors only");
    AssertFalse(ids.Contains(siblingId), "assignment document path should not include sibling branches");
}

static void BuildsUploadEndpointFromPublicBaseUrl()
{
    var request = new DefaultHttpContext().Request;
    request.Scheme = "http";
    request.Host = new HostString("internal:5080");

    var endpoint = UploadEndpointBuilder.BuildUploadsEndpoint(
        request,
        new UploadOptions { PublicBaseUrl = "https://tdtd.conganthanhhoa.vn/api/" });

    AssertEqual("https://tdtd.conganthanhhoa.vn/api/uploads", endpoint, "public base url should produce external HTTPS TUS endpoint");
}

static void BuildsUploadEndpointFromForwardedRequest()
{
    var request = new DefaultHttpContext().Request;
    request.Scheme = "https";
    request.Host = new HostString("tdtd.conganthanhhoa.vn");

    var endpoint = UploadEndpointBuilder.BuildUploadsEndpoint(request, new UploadOptions());

    AssertEqual("https://tdtd.conganthanhhoa.vn/api/uploads", endpoint, "forwarded request scheme and host should produce HTTPS TUS endpoint");
}

static WorkAssignment PeriodicAssignment(string workId, string assignmentId, DateTime startDate, DateTime dueDate) => new()
{
    Id = assignmentId,
    WorkId = workId,
    Path = $"/{assignmentId}",
    AssignmentType = WorkAssignmentTypes.PeriodicReport,
    StartDate = startDate.Date,
    DueDate = dueDate.Date,
    Schedule = new AssignmentSchedule
    {
        CycleType = ReportCycleTypes.Daily,
        StartDate = startDate.Date,
        QuarterDays = Array.Empty<int>(),
        SemiAnnualDays = Array.Empty<int>()
    },
    IsActive = true
};

static WorkTemplateAssignee Binding(string workId, string assignmentId, string assigneeUserId) => new()
{
    Id = ObjectId(77),
    WorkId = workId,
    WorkAssignmentId = assignmentId,
    AssigneeUserId = assigneeUserId,
    IsActive = true
};

static WorkReportPeriod ScheduledPeriod(
    string workId,
    string assignmentId,
    string assigneeUserId,
    DateTime dueAtUtc,
    WorkReportPeriodStatus status) => new()
{
    Id = ObjectId(78),
    WorkId = workId,
    WorkAssignmentId = assignmentId,
    WorkTemplateAssigneeId = ObjectId(77),
    AssigneeUserId = assigneeUserId,
    PeriodKey = $"{dueAtUtc:yyyyMMdd}",
    PeriodInstanceKey = $"{dueAtUtc:yyyyMMdd}",
    PeriodKind = WorkReportPeriodKind.Scheduled,
    DueAtUtc = dueAtUtc,
    Status = status,
    IsActive = true
};

static Work WorkOwnedBy(string userId) => new()
{
    Id = ObjectId(1),
    CreatedByUserId = userId
};

static Work WorkWithDateRange(DateTime startDate, DateTime endDate) => new()
{
    Id = ObjectId(3),
    CreatedByUserId = UserId(1),
    StartDate = startDate,
    EndDate = endDate
};

static Work WorkWithDates(DateTime? startDate, DateTime? endDate, DateTime? dueDate) => new()
{
    Id = ObjectId(3),
    CreatedByUserId = UserId(1),
    StartDate = startDate,
    EndDate = endDate,
    DueDate = dueDate
};

static WorkAssignment ParentAssignment(string createdByUserId, IEnumerable<string>? assigneeUserIds = null) => new()
{
    Id = ObjectId(2),
    WorkId = ObjectId(1),
    CreatedByUserId = createdByUserId,
    Assignees = (assigneeUserIds ?? Array.Empty<string>())
        .Select(userId => new UserRef { UserId = userId })
        .ToList()
};

static WorkAssignmentReport ReadyPayloadReport() => new()
{
    Id = ObjectId(51),
    WorkId = ObjectId(52),
    WorkAssignmentId = ObjectId(53),
    WorkReportPeriodId = ObjectId(54),
    Values1DJson = "[]",
    PayloadRevision = 3,
    PayloadHash = "payload_hash_3",
    PayloadStatus = WorkReportPayloadStatus.Ready,
    PayloadSizeBytes = 128,
    IsActive = true
};

static WorkReportPayloadSnapshot MatchingPayloadSnapshot(
    WorkAssignmentReport report,
    bool isExternalPayload,
    bool payloadHashVerified)
    => new(
        Values1DJson: report.Values1DJson,
        FieldValuesJson: report.FieldValuesJson,
        TableValuesJson: report.TableValuesJson,
        SummarySourceJson: report.SummarySourceJson,
        PayloadRevision: report.PayloadRevision,
        PayloadHash: report.PayloadHash,
        PayloadSizeBytes: report.PayloadSizeBytes,
        PayloadStatus: report.PayloadStatus,
        IsExternalPayload: isExternalPayload,
        PayloadHashVerified: payloadHashVerified);

static void AllowsFormOnlyReportWithoutDynamicExcelTemplate()
{
    var report = ReadyPayloadReport();
    report.DynamicFormTemplateId = ObjectId(81);
    report.DynamicExcelTemplateId = null;
    report.DynamicExcelTemplateCode = string.Empty;
    report.DynamicExcelTemplateName = string.Empty;

    var doc = report.ToBsonDocument();
    if (doc.TryGetValue("dynamicExcelTemplateId", out var value) && !value.IsBsonNull)
        throw new InvalidOperationException("Form-only reports must not require dynamicExcelTemplateId.");
}

static void ResolvesDynamicExcelIdFromFormBlocks()
{
    var excelId = ObjectId(82);
    var method = typeof(WorkAssignmentReportService).GetMethod(
        "ExtractPrimaryDynamicExcelTemplateId",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentReportService), "ExtractPrimaryDynamicExcelTemplateId");

    var resolved = (string?)method.Invoke(null, new object?[]
    {
        null,
        "[{\"blockId\":\"b1\",\"dynamicExcelTemplateId\":\"" + excelId + "\"}]"
    });
    if (resolved != excelId)
        throw new InvalidOperationException($"Expected {excelId}, got {resolved ?? "<null>"}.");

    var noExcel = (string?)method.Invoke(null, new object?[] { null, "[]" });
    if (noExcel is not null)
        throw new InvalidOperationException("A Dynamic Form with no Excel block should resolve no Excel template.");
}

static void CompactsReportPayloadHeader()
{
    var report = ReadyPayloadReport();
    report.Values1DJson = "[1,2,3]";
    report.FieldValuesJson = "{\"field\":\"value\"}";
    report.TableValuesJson = "{\"blocks\":[{\"blockId\":\"b1\"}]}";
    report.SummarySourceJson = "{\"kind\":\"AUTO_SUMMARY\"}";

    var method = typeof(WorkAssignmentReportService).GetMethod(
        "CompactEmbeddedPayloadHeader",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentReportService), "CompactEmbeddedPayloadHeader");

    method.Invoke(null, new object?[] { report });

    if (report.Values1DJson != "[]")
        throw new InvalidOperationException("Report header must keep an empty values vector after payload compaction.");
    if (report.FieldValuesJson is not null || report.TableValuesJson is not null || report.SummarySourceJson is not null)
        throw new InvalidOperationException("Report header must not keep heavy embedded payload fields after compaction.");
}

static WorkAssignmentTargetScopePolicy ConfiguredPhongToPhuongXaPolicy()
    => new(Options.Create(new tdtd_be.Options.WorkAssignmentScopeOptions
    {
        UnitTypeAssignmentRules = new List<tdtd_be.Options.UnitTypeAssignmentRuleOptions>
        {
            new()
            {
                ActorUnitTypeCodes = new List<string> { "PHONG" },
                TargetUnitTypeCodes = new List<string> { "PHUONG_XA" },
                TargetAccountKinds = new List<string> { ManagementAccountKind.UnitManager }
            }
        }
    }));

static Unit TestUnit(
    int seed,
    string code,
    int level,
    string? parentUnitId,
    string? primaryUnitTypeCode = null) => new()
{
    Id = ObjectId(seed),
    Code = code,
    Level = level,
    ParentUnitId = parentUnitId,
    PrimaryUnitTypeCode = primaryUnitTypeCode,
    UnitTypeCodes = string.IsNullOrWhiteSpace(primaryUnitTypeCode) ? new List<string>() : new List<string> { primaryUnitTypeCode },
    FullName = $"Unit {seed}"
};

static AppUser TestUser(int seed, string username, string accountKind, string unitId) => new()
{
    Id = UserId(seed),
    Username = username,
    FullName = username,
    AccountKind = accountKind,
    UnitId = unitId
};

static IReadOnlyDictionary<string, Unit> UnitMap(params Unit[] units)
    => units.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

static void AssertThrows(AppErrorCode expectedCode, Action action)
{
    try
    {
        action();
    }
    catch (AppException ex) when (ex.Code == expectedCode)
    {
        return;
    }
    catch (AppException ex)
    {
        throw new InvalidOperationException($"Expected {expectedCode}, got {ex.Code}.", ex);
    }

    throw new InvalidOperationException($"Expected {expectedCode}, but no exception was thrown.");
}

static void AssertThrowsFromReflection(AppErrorCode expectedCode, Action action)
{
    try
    {
        action();
    }
    catch (TargetInvocationException ex) when (ex.InnerException is AppException appEx && appEx.Code == expectedCode)
    {
        return;
    }
    catch (TargetInvocationException ex) when (ex.InnerException is AppException appEx)
    {
        throw new InvalidOperationException($"Expected {expectedCode}, got {appEx.Code}.", ex);
    }

    throw new InvalidOperationException($"Expected {expectedCode}, but no exception was thrown.");
}

static T InvokePrivateStatic<T>(Type type, string name, params object?[] args)
{
    var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{type.Name}.{name} helper method was not found.");

    var result = method.Invoke(null, args);
    return result is null ? default! : (T)result;
}

static T? GetReflectedProperty<T>(object source, string name)
{
    var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException($"{source.GetType().Name}.{name} property was not found.");

    var value = property.GetValue(source);
    return value is null ? default : (T)value;
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertFalse(bool condition, string message)
{
    if (condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
}

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (expected.Count != actual.Count || !expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{message}. Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
}

static string UserId(int seed) => $"0000000000000000000000{seed:00}";

static string ObjectId(int seed) => $"1000000000000000000000{seed:00}";

static WorkPermissionService WorkPermission() => new(null!);

static MeResponse Me(string username, string accountKind, List<string> roles)
    => new(
        id: UserId(9),
        username,
        fullName: username,
        unitTypeCodes: new List<string>(),
        unitId: ObjectId(9),
        unitSymbol: "PV01",
        unitName: "PV01",
        unitCode: "PV01",
        roles,
        positionCode: "",
        isDeleted: false,
        accountKind);
