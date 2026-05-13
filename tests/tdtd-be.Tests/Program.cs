using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Services.Works;
using tdtd_be.Uploads;
using System.Reflection;
using Microsoft.AspNetCore.Http;

var tests = new (string Name, Action Run)[]
{
    ("root owner can create an assignment assigned to themself", AllowsRootOwnerSelfAssignment),
    ("root non-owner cannot create even when assigning to themself", BlocksRootNonOwnerSelfAssignment),
    ("parent owner can create a child assignment assigned to themself", AllowsParentOwnerSelfAssignment),
    ("direct parent assignee can create a child assignment assigned to themself", AllowsDirectAssigneeSelfAssignment),
    ("unrelated actor cannot create a child assignment even when assigning to themself", BlocksUnrelatedChildSelfAssignment),
    ("blank actor is rejected before scope evaluation", BlocksBlankActor),
    ("assignment completed date cannot be before start date", BlocksAssignmentCompletedBeforeStart),
    ("missing assignment start defaults to current date", DefaultsMissingAssignmentStartToCurrentDate),
    ("root missing assignment completed date defaults to work due date", DefaultsRootCompletedDateToWorkDueDate),
    ("child missing assignment completed date defaults to parent assignment end", DefaultsChildCompletedDateToParentAssignmentEnd),
    ("periodic assignment date range caps occurrence validation", ValidatesPeriodicAssignmentDateRange),
    ("historical data approval resolves as normal approved", ResolvesHistoricalDataApprovalAsNormalApproved),
    ("historical user-created period is data-only for progress", TreatsHistoricalUserCreatedPeriodAsDataOnly),
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
    ("dynamic excel record table contract accepts typed calculated outputs and rules", ValidatesDynamicExcelRecordTableContract),
    ("dynamic excel record table contract rejects upstream calculated outputs", BlocksDynamicExcelCalculatedOutputAsUpstreamData),
    ("dynamic excel record table contract limits calculated outputs", LimitsDynamicExcelCalculatedOutputs),
    ("dynamic excel record table runtime accepts rows and calculates outputs", ValidatesDynamicExcelRecordTableRuntimeRows),
    ("dynamic excel record table runtime rejects invalid rows", RejectsInvalidDynamicExcelRecordTableRuntimeRows),
    ("legacy work basis file resolves as work document", ResolvesLegacyWorkBasisFileAsWorkDocument),
    ("assignment file resolves as assignment branch document", ResolvesAssignmentFileAsBranchDocument),
    ("assignment document path resolves ancestors only", ResolvesAssignmentDocumentAncestorsFromPath),
    ("upload endpoint uses public base url override", BuildsUploadEndpointFromPublicBaseUrl),
    ("upload endpoint falls back to forwarded request scheme and host", BuildsUploadEndpointFromForwardedRequest),
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

static void AllowsRootOwnerSelfAssignment()
{
    var actorId = UserId(1);
    var work = WorkOwnedBy(actorId);

    WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
        work,
        parent: null,
        actorUserId: actorId,
        assigneeUserIds: new[] { actorId });
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

static void AllowsParentOwnerSelfAssignment()
{
    var actorId = UserId(3);
    var work = WorkOwnedBy(UserId(1));
    var parent = ParentAssignment(createdByUserId: actorId);

    WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
        work,
        parent,
        actorUserId: actorId,
        assigneeUserIds: new[] { actorId });
}

static void AllowsDirectAssigneeSelfAssignment()
{
    var actorId = UserId(4);
    var work = WorkOwnedBy(UserId(1));
    var parent = ParentAssignment(
        createdByUserId: UserId(1),
        assigneeUserIds: new[] { actorId });

    WorkAssignmentCreateScopeGuard.EnsureCanCreateWithinScope(
        work,
        parent,
        actorUserId: actorId,
        assigneeUserIds: new[] { actorId });
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

static void BlocksAssignmentCompletedBeforeStart()
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
        CompletedDate = new DateTime(2026, 5, 9),
        DueAtUtc = new DateTime(2026, 5, 10)
    };

    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_COMPLETED_BEFORE_START,
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

static void DefaultsRootCompletedDateToWorkDueDate()
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

    AssertEqual(workDueDate, effective.CompletedDate, "root completed date should default to work due date");
    AssertEqual(new DateTime(2026, 5, 1), effective.Schedule?.StartDate, "schedule start should keep assignment start date");
}

static void DefaultsChildCompletedDateToParentAssignmentEnd()
{
    var parentEndDate = new DateTime(2026, 6, 30);
    var work = WorkWithDates(
        startDate: new DateTime(2026, 1, 1),
        endDate: null,
        dueDate: new DateTime(2026, 12, 31));
    var parent = ParentAssignment(createdByUserId: UserId(1));
    parent.CompletedDate = parentEndDate;

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

    AssertEqual(parentEndDate, effective.CompletedDate, "child completed date should default to parent assignment end");
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
        CompletedDate = new DateTime(2026, 5, 31),
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

static void ResolvesHistoricalDataApprovalAsNormalApproved()
{
    var now = new DateTime(2026, 5, 13);
    var period = new WorkReportPeriod
    {
        Status = WorkReportPeriodStatus.OverdueSubmitted,
        DueAtUtc = new DateTime(2026, 5, 1)
    };
    var report = new WorkAssignmentReport
    {
        IsHistoricalData = true,
        HistoricalDataApproved = true,
        IsLateSubmission = true
    };

    var status = WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(period, report, now);

    AssertEqual(WorkReportPeriodStatus.Approved, status, "historical approved report should not remain overdue");
}

static void TreatsHistoricalUserCreatedPeriodAsDataOnly()
{
    var dataOnly = new WorkReportPeriod
    {
        PeriodKind = WorkReportPeriodKind.UserCreated,
        IsHistoricalData = true
    };

    AssertFalse(
        WorkAssignmentReportTemporalPolicy.ContributesToProgress(dataOnly),
        "unlinked historical user-created period should not drive assignment progress");

    var linked = new WorkReportPeriod
    {
        PeriodKind = WorkReportPeriodKind.UserCreated,
        IsHistoricalData = true,
        LinkedScheduledPeriodId = ObjectId(101)
    };

    AssertTrue(
        WorkAssignmentReportTemporalPolicy.ContributesToProgress(linked),
        "linked historical user-created period can still be treated as progress-contributing");
}

static void MatchesHistoricalReportSourceWindowForAggregation()
{
    var report = new WorkAssignmentReport
    {
        PeriodKind = WorkReportPeriodKind.UserCreated,
        PeriodKey = "USER_CREATED:manual",
        ReportDate = new DateTime(2026, 1, 10),
        PeriodStart = new DateTime(2026, 1, 5),
        PeriodEnd = new DateTime(2026, 1, 10),
        CompletedDate = new DateTime(2026, 1, 10),
        IsHistoricalData = true
    };

    AssertTrue(
        WorkAssignmentReportTemporalPolicy.MatchesPeriodScope(report, "SINGLE_PERIOD", "20260107", null, null),
        "single-period aggregate should include a custom-key historical report when the source window overlaps");
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
        "SUM",
        "block_1",
        false,
        new List<int> { 0, 2 })
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
            }));
}

static void ValidatesDynamicExcelRecordTableContract()
{
    DynamicExcelRecordTableContractValidator.Validate(
        """
        {
          "orientation": "ROWS",
          "columns": [
            { "key": "ho_so", "label": "Hồ sơ", "dataType": "text" },
            { "key": "sl_x", "label": "Số lượng đối tượng X", "dataType": "number" },
            { "key": "sl_y", "label": "Số lượng đối tượng Y", "dataType": "number" },
            { "key": "ngay_mo", "label": "Ngày mở", "dataType": "date" },
            { "key": "ngay_ket_thuc", "label": "Ngày kết thúc", "dataType": "date" }
          ],
          "calculatedColumns": [
            {
              "key": "tong_doi_tuong",
              "label": "Tổng đối tượng",
              "dataType": "number",
              "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
            },
            {
              "key": "so_ngay_xu_ly",
              "label": "Số ngày xử lý",
              "dataType": "number",
              "expression": { "op": "dateDiffDays", "args": [ { "col": "ngay_mo" }, { "col": "ngay_ket_thuc" } ] }
            }
          ],
          "validationRules": [
            {
              "key": "ngay_ket_thuc_sau_ngay_mo",
              "message": "Ngày kết thúc không được trước ngày mở.",
              "condition": { "op": "gte", "args": [ { "col": "ngay_ket_thuc" }, { "col": "ngay_mo" } ] }
            },
            {
              "key": "sl_x_khong_lon_hon_sl_y",
              "message": "Số lượng X không được lớn hơn số lượng Y.",
              "condition": { "op": "lte", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
            }
          ]
        }
        """);
}

static void BlocksDynamicExcelCalculatedOutputAsUpstreamData()
{
    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicExcelRecordTableContractValidator.Validate(
            """
            {
              "columns": [
                { "key": "sl_x", "label": "Số lượng X", "dataType": "number" },
                { "key": "sl_y", "label": "Số lượng Y", "dataType": "number" }
              ],
              "calculatedColumns": [
                {
                  "key": "tong",
                  "label": "Tổng",
                  "dataType": "number",
                  "includeInUpstream": true,
                  "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
                }
              ]
            }
            """));
}

static void LimitsDynamicExcelCalculatedOutputs()
{
    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicExcelRecordTableContractValidator.Validate(
            """
            {
              "columns": [
                { "key": "sl_x", "label": "Số lượng X", "dataType": "number" },
                { "key": "sl_y", "label": "Số lượng Y", "dataType": "number" }
              ],
              "calculatedColumns": [
                { "key": "c01", "label": "C01", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c02", "label": "C02", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c03", "label": "C03", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c04", "label": "C04", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c05", "label": "C05", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c06", "label": "C06", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c07", "label": "C07", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c08", "label": "C08", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c09", "label": "C09", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c10", "label": "C10", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } },
                { "key": "c11", "label": "C11", "dataType": "number", "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] } }
              ]
            }
            """));
}

static void ValidatesDynamicExcelRecordTableRuntimeRows()
{
    const string specJson = """
    {
      "columns": [
        { "key": "ho_so", "label": "Hồ sơ", "dataType": "text", "required": true },
        { "key": "sl_x", "label": "Số lượng X", "dataType": "number" },
        { "key": "sl_y", "label": "Số lượng Y", "dataType": "number" },
        { "key": "ngay_mo", "label": "Ngày mở", "dataType": "date" },
        { "key": "ngay_ket_thuc", "label": "Ngày kết thúc", "dataType": "date" }
      ],
      "calculatedColumns": [
        {
          "key": "tong",
          "label": "Tổng",
          "dataType": "number",
          "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
        }
      ],
      "validationRules": [
        {
          "key": "x_khong_lon_hon_y",
          "condition": { "op": "lte", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
        }
      ]
    }
    """;

    const string tableValuesJson = """
    {
      "blocks": [
        {
          "blockId": "excel_tpl",
          "dynamicExcelTemplateId": "tpl1",
          "tableKind": "RECORD_TABLE",
          "records": [
            {
              "rowKey": "row_1",
              "values": {
                "ho_so": "A",
                "sl_x": 2,
                "sl_y": 3,
                "ngay_mo": "2026-05-01",
                "ngay_ket_thuc": "2026-05-02"
              }
            }
          ]
        }
      ]
    }
    """;

    DynamicExcelRecordTableRuntime.ValidateTableValues(tableValuesJson, specJson, "tpl1", "report1");
    var spec = DynamicExcelRecordTableRuntime.ParseSpec(specJson);
    var row = DynamicExcelRecordTableRuntime.ExtractRows(tableValuesJson, spec, "tpl1").Single();
    var calculated = DynamicExcelRecordTableRuntime.BuildCalculatedValues(spec, row.Values);
    if (!Equals(calculated["tong"], 5m))
        throw new Exception("Expected tong calculated output to equal 5.");
}

static void RejectsInvalidDynamicExcelRecordTableRuntimeRows()
{
    const string specJson = """
    {
      "columns": [
        { "key": "ho_so", "label": "Hồ sơ", "dataType": "text", "required": true },
        { "key": "sl_x", "label": "Số lượng X", "dataType": "number" },
        { "key": "sl_y", "label": "Số lượng Y", "dataType": "number" }
      ],
      "calculatedColumns": [
        {
          "key": "tong",
          "label": "Tổng",
          "dataType": "number",
          "expression": { "op": "add", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
        }
      ],
      "validationRules": [
        {
          "key": "x_khong_lon_hon_y",
          "condition": { "op": "lte", "args": [ { "col": "sl_x" }, { "col": "sl_y" } ] }
        }
      ]
    }
    """;

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicExcelRecordTableRuntime.ValidateTableValues(
            """
            {
              "records": [
                {
                  "rowKey": "row_1",
                  "values": { "ho_so": "A", "sl_x": 5, "sl_y": 3 }
                }
              ]
            }
            """,
            specJson,
            "tpl1",
            "report1"));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicExcelRecordTableRuntime.ValidateTableValues(
            """
            {
              "records": [
                {
                  "rowKey": "row_1",
                  "values": { "ho_so": "A", "sl_x": 2, "sl_y": 3, "tong": 5 }
                }
              ]
            }
            """,
            specJson,
            "tpl1",
            "report1"));
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
