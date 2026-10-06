using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.DTOs.WorkAssignments.AggregateTable;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.BasicSummary;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Runtime;
using tdtd_be.Services.WorkAssignments.SummaryTokens;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Services.Works;
using tdtd_be.Uploads;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Hangfire;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
    ("unit manager can assign personnel in own non-leaf unit", AllowsUnitManagerNormalUserBeforeFinalUnit),
    ("final unit manager can assign normal user in own unit", AllowsFinalUnitManagerOwnUnitNormalUserAssignment),
    ("unit manager cannot assign normal user outside own final unit", BlocksUnitManagerNormalUserOutsideOwnUnit),
    ("blank actor is rejected before scope evaluation", BlocksBlankActor),
    ("assignment due date cannot be before start date", BlocksAssignmentDueDateBeforeStart),
    ("once assignment due date cannot be before assignment start", BlocksOnceDueBeforeAssignmentStart),
    ("periodic schedule start must stay inside assignment date range", BlocksPeriodicScheduleStartOutsideAssignmentRange),
    ("missing assignment start defaults to current date", DefaultsMissingAssignmentStartToCurrentDate),
    ("root missing assignment due date defaults to work due date", DefaultsRootDueDateToWorkDueDate),
    ("child missing assignment due date defaults to parent assignment due date", DefaultsChildDueDateToParentDueDate),
    ("non-flow assignment maps null dynamic flow metadata", MapsNonFlowAssignmentWithNullFlowMetadata),
    ("P8 stat-config identity and label taxonomy contracts are strict", StatConfigLabelContractTests.Run),
    ("P8 Dynamic Form field statistic metadata is typed and isolated", DynamicFormStatisticConfigContractTests.Run),
    ("P8 Dynamic Form table statistic metadata uses stable typed identities", DynamicFormTableStatisticConfigContractTests.Run),
    ("P8 Basic Summary config is strict, versioned, and executor-isolated", WorkAssignmentBasicSummaryConfigContractTests.Run),
    ("P8 Advanced Summary config is strict, lifecycle-bound, and P9-isolated", WorkAssignmentAdvancedSummaryConfigContractTests.Run),
    ("P8 Advanced Summary quota ledger is atomic and replay-safe", WorkSummaryTokenP805ContractTests.Run),
    ("P8 Diff config is strict, versioned, dependency-bound, and P9-isolated", WorkReportStatisticDiffP806ContractTests.Run),
    ("P8 Flow contribution versions bind exact P7 baselines and keep profiles blocked", DynamicFlowContributionP807ContractTests.Run),
    ("P9 Direct results read only published generations with typed stable paging", P9DirectResultContractTests.Run),
    ("P9 Basic summary uses locked config and four exact Flow scopes", P9BasicSummaryContractTests.Run),
    ("P9 operations accepts published lifecycle prompt and rejects arbitrary prompts", P9OperationsLifecyclePromptContractTests.Run),
    ("P10 evidence list, readback, and download re-authorize current assignment scope", StatisticReconciliationEvidenceControllerContractTests.Run),
    ("P10 review drift recovery is opaque and recheck returns accepted", StatisticReconciliationRecoveryControllerContractTests.Run),
    ("dual-role SYSTEM_ADMIN authority takes precedence over ADMIN-only user management", UserAdminRolePrecedenceContractTests.Run),
    ("dashboard global read access is limited to the provincial police director position", DashboardAccessPolicyContractTests.Run),
    ("P8 operations readiness is no-dataset, redacted, authorized, and fail-closed", StatConfigOperationsP808ContractTests.Run),
    ("P8 canonical bundle, empty readiness, freshness, and P9/P10 barriers are exact", StatConfigBundleP809ContractTests.Run),
    ("P8 Basic Summary dependencies reject partial and untrusted state", WorkAssignmentBasicSummaryDependencyIntegrityContractTests.Run),
    ("P8 Basic Summary ROW_LABEL binds exact label history", WorkAssignmentBasicSummaryRowLabelHistoryContractTests.Run),
    ("dynamic form and flow capability catalog metadata is locked", DynamicFormFlowCapabilityCatalogContractTests.Run),
    ("dynamic form and flow capability endpoint supports ETag negotiation", DynamicFormFlowCapabilityEndpointContractTests.Run),
    ("dynamic form typed schema preserves legacy storage and OpenAPI contract", DynamicFormTypedSchemaContractTests.Run),
    ("dynamic form published schema snapshot is canonical and version-bound", DynamicFormPublishedSchemaSnapshotIsCanonicalAndVersionBound),
    ("dynamic form published schema integrity validation fails closed", DynamicFormPublishedSchemaIntegrityContractTests.Run),
    ("dynamic form binding policy rejects hidden design-time use", DynamicFormBindingAccessPolicyContractTests.Run),
    ("dynamic form version metadata backfill is bounded and idempotent", DynamicFormVersionMetadataBackfillContractTests.Run),
    ("dynamic form core mutation contracts preserve CAS and fail-closed guards", DynamicFormCoreMutationContractTests.Run),
    ("dynamic form runtime provenance backfill is bounded and fail closed", DynamicFormRuntimeProvenanceBackfillContractTests.Run),
    ("dynamic form runtime field canonicalizer enforces ten strict types", DynamicFormRuntimeFieldCanonicalizerContractTests.Run),
    ("dynamic form runtime persistence enforces sources, table modes, and payload CAS", DynamicFormRuntimePersistenceContractTests.Run),
    ("dynamic form native List v2 validates definition, draft, submit, limits, and catalogs", DynamicFormNativeListContractTests.Run),
    ("work report lifecycle commands enforce reviewer CAS and exact replay", WorkReportLifecycleCommandContractTests.Run),
    ("work report lifecycle projections use a durable monotonic outbox", WorkReportLifecycleProjectionOutboxContractTests.Run),
    ("work report section projections create repair and verify exact current rows", WorkAssignmentReportSectionProjectionContractTests.Run),
    ("work report raw GET hides report existence and identifiers", WorkAssignmentReportRawReadAccessContractTests.Run),
    ("dynamic form section snapshots are deterministic and ordered", DynamicFormSectionSnapshotsAreDeterministicAndOrdered),
    ("dynamic form section snapshots fail closed on malformed topology", DynamicFormSectionSnapshotsFailClosedOnMalformedTopology),
    ("advanced summary section lookup uses exact form snapshots", AdvancedSummarySectionLookupUsesExactFormSnapshots),
    ("dynamic flow template OpenAPI exposes typed response contracts", DynamicFlowTemplateOpenApiContractTests.Run),
    ("dynamic flow schema v2 contract canonicalizes and validates strictly", DynamicFlowDefinitionPayloadContractTests.Run),
    ("dynamic flow definition metadata backfill is bounded, idempotent, and fail closed", DynamicFlowDefinitionMetadataBackfillContractTests.Run),
    ("dynamic flow form nodes bind canonical Dynamic Form versions", DynamicFlowFormNodeVersionContractTests.Run),
    ("dynamic flow template reads are actor-scoped and fail closed", DynamicFlowTemplateReadAccessContractTests.Run),
    ("dynamic flow definition mutations are transactional, idempotent, and runtime-blocked", DynamicFlowDefinitionMutationContractTests.Run),
    ("locked dynamic flow snapshots fail closed on payload, catalog, family, and form pin drift", DynamicFlowLockedSnapshotIntegrityContractTests.Run),
    ("dynamic flow runtime persistence freezes aggregate, CAS, event, receipt, outbox, and index contracts", DynamicFlowRuntimePersistenceContractTests.Run),
    ("dynamic flow runtime preflight freezes typed API, auth, pins, replay, dedupe, and phase guards", DynamicFlowRuntimePreflightContractTests.Run),
    ("dynamic flow sequential topology freezes candidate boundary, exact forms, edges, and identities", DynamicFlowSequentialTopologyContractTests.Run),
    ("dynamic flow parallel fork freezes exact fan-out and deterministic branch identities", DynamicFlowParallelForkTopologyContractTests.Run),
    ("dynamic flow JOIN ALL freezes versioned contributors, ledger identities, and release threshold", DynamicFlowJoinAllTopologyContractTests.Run),
    ("dynamic flow JOIN quorum freezes ANY/N_OF_M, cancellation, late audit, and impossible detection", DynamicFlowJoinQuorumTopologyContractTests.Run),
    ("dynamic flow typed conditional freezes authorized facts, truth tables, default order, and fail-closed errors", DynamicFlowTypedConditionalTopologyContractTests.Run),
    ("dynamic flow review loop freezes exact target, bounded attempts, immutable lineage, and candidate barrier", DynamicFlowReviewLoopTopologyContractTests.Run),
    ("dynamic flow subflow freezes exact child pins, blocking lineage, depth/cycle guards, and candidate barrier", DynamicFlowSubflowTopologyContractTests.Run),
    ("dynamic flow periodic freezes schedule identity, timezone, leases, missed audit, and candidate barrier", DynamicFlowPeriodicTopologyContractTests.Run),
    ("dynamic flow supplemental freezes server coordinator, exact allow-list, cap, completion policy, replay, and audit", DynamicFlowSupplementalTopologyContractTests.Run),
    ("dynamic flow finalize freezes epoch commands, invalidation closure, deterministic replacement, replay, and P7/P8 barrier", DynamicFlowFinalizeTopologyContractTests.Run),
    ("dynamic flow runtime materialization uses durable intent, leased recovery, exact pins, and scoped compensation", DynamicFlowRuntimeMaterializationContractTests.Run),
    ("dynamic flow runtime state projection aggregates reports, guards transitions, and preserves deterministic replay", DynamicFlowRuntimeStateProjectionContractTests.Run),
    ("dynamic flow runtime reads freeze cursor, visibility, capability, recovery, revision, sanitization, and typed route contracts", DynamicFlowRuntimeReadContractTests.Run),
    ("dynamic flow mapping security freezes canonical signatures and scoped preview tokens", DynamicFlowMappingSecurityContractTests.Run),
    ("dynamic flow mapping apply freezes transaction, replay, provenance, outbox, and tamper guards", DynamicFlowMappingApplyDurabilityContractTests.Run),
    ("dynamic flow mapping P7-11 apply fault matrix is exact, Testing-only, and one-shot", DynamicFlowMappingP711ApplyFaultContractTests.Run),
    ("dynamic flow mapping lifecycle and rerun contract covers MAP-RERUN-01..08", DynamicFlowMappingLifecycleRerunContractTests.Run),
    ("dynamic flow template payload normalizes defaults", DynamicFlowTemplatePayloadNormalizesDefaults),
    ("dynamic flow template rejects statistic profiles without an executor", DynamicFlowTemplateRejectsUnexecutableStatisticProfile),
    ("dynamic flow template lock payload validates steps", DynamicFlowTemplateLockPayloadValidatesSteps),
    ("dynamic flow topology anchors root forms and legacy mappings", DynamicFlowTopologyAnchorsRootFormsAndLegacyMappings),
    ("dynamic flow policy validation checks dynamic form references", DynamicFlowPolicyValidationChecksDynamicFormReferences),
    ("dynamic flow mapping validation checks schema and references", DynamicFlowMappingValidationChecksSchemaAndReferences),
    ("dynamic flow structured mapping validates functions and unique targets", DynamicFlowStructuredMappingValidatesFunctionsAndUniqueTargets),
    ("dynamic flow expressions evaluate named value operands", DynamicFlowExpressionsEvaluateNamedValueOperands),
    ("dynamic flow P7 field evaluator freezes MAP-FIELD-01..20", DynamicFlowMappingFieldContractTests.Run),
    ("dynamic flow P7 table engine freezes MAP-TABLE-01..20", DynamicFlowMappingTableContractTests.Run),
    ("dynamic flow mapping authorization contract covers MAP-AUTH-01..12", DynamicFlowMappingAuthorizationContractTests.Run),
    ("dynamic flow mapping staged activation keeps preview token and apply slices blocked until their prompts", DynamicFlowMappingActivationContractTests.Run),
    ("dynamic flow mapping P7-08 barriers keep rerun, lifecycle, and invalidation closed until P7-09", DynamicFlowMappingP708BarrierContractTests.Run),
    ("dynamic flow mapping canonical source contract covers P7-SOURCE-EXACT", DynamicFlowMappingCanonicalSourceContractTests.Run),
    ("dynamic flow mapping runtime signature and parity contract covers P7-RUNTIME-01..12", DynamicFlowMappingRuntimeSignatureContractTests.Run),
    ("dynamic flow structured mapping rejects collection copy and invalid literals", DynamicFlowStructuredMappingRejectsCollectionCopyAndInvalidLiterals),
    ("dynamic flow mapping engine projects values and provenance", DynamicFlowMappingEngineProjectsValuesAndProvenance),
    ("dynamic flow mapping engine evaluates multiple inputs and fixed grid slots", DynamicFlowMappingEngineEvaluatesMultipleInputsAndFixedGridSlots),
    ("dynamic flow mapping engine keeps table runtime projections aligned", DynamicFlowMappingEngineKeepsTableRuntimeProjectionsAligned),
    ("dynamic flow mapping engine preserves sparse row coordinates", DynamicFlowMappingEnginePreservesSparseRowCoordinates),
    ("dynamic flow mapping provenance excludes skipped null values", DynamicFlowMappingProvenanceExcludesSkippedNullValues),
    ("dynamic flow mapping synchronizes top-level table values", DynamicFlowMappingSynchronizesTopLevelTableValues),
    ("dynamic flow policy evaluator applies field and table permissions", DynamicFlowPolicyEvaluatorAppliesFieldAndTablePermissions),
    ("dynamic flow policy evaluator prefers specific scopes", DynamicFlowPolicyEvaluatorPrefersSpecificScopes),
    ("dynamic flow report permissions reject field writes", DynamicFlowReportPermissionsRejectFieldWrites),
    ("dynamic flow report permissions reject table column writes", DynamicFlowReportPermissionsRejectTableColumnWrites),
    ("dynamic flow report permissions redact unreadable values", DynamicFlowReportPermissionsRedactUnreadableValues),
    ("dynamic flow report permissions preserve unreadable round trips", DynamicFlowReportPermissionsPreserveUnreadableRoundTrips),
    ("dynamic flow report permissions enforce required values", DynamicFlowReportPermissionsEnforceRequiredValues),
    ("dynamic flow mapping preview redacts unreadable values", DynamicFlowMappingPreviewRedactsUnreadableValues),
    ("dynamic flow mapping rejects ambiguous table row keys", DynamicFlowMappingRejectsAmbiguousTableRowKeys),
    ("dynamic flow mapping provenance cannot be forged by report requests", DynamicFlowMappingProvenanceCannotBeForgedByReportRequests),
    ("dynamic flow report actor roles follow assignment relationships", DynamicFlowReportActorRolesFollowAssignmentRelationships),
    ("dynamic flow report lifecycle keeps post-submit locks", DynamicFlowReportLifecycleKeepsPostSubmitLocks),
    ("dynamic flow runtime planner creates branch per target unit", DynamicFlowRuntimePlannerCreatesBranchPerTargetUnit),
    ("dynamic flow runtime planner rejects invalid launch inputs", DynamicFlowRuntimePlannerRejectsInvalidLaunchInputs),
    ("dynamic flow branch mutation planner invalidates downstream branches", DynamicFlowBranchMutationPlannerInvalidatesDownstreamBranches),
    ("dynamic flow branch visibility inherits downstream units", DynamicFlowBranchVisibilityInheritsDownstreamUnits),
    ("dynamic flow branch visibility hides sibling branches", DynamicFlowBranchVisibilityHidesSiblingBranches),
    ("statistic projection context captures dynamic flow metadata", StatisticProjectionContextCapturesDynamicFlowMetadata),
    ("statistic concept map resolves dynamic flow mapping targets", StatisticConceptMapResolvesDynamicFlowMappingTargets),
    ("statistic rebuild job dedupe key includes bounded flow scope", StatisticRebuildJobDedupeKeyIncludesBoundedFlowScope),
    ("periodic assignment date range caps occurrence validation", ValidatesPeriodicAssignmentDateRange),
    ("periodic child due cannot exceed inherited parent due within work", BlocksPeriodicChildDueAfterParentWithinWork),
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
    ("historical mutation policy allows recent approved changes", HistoricalMutationPolicyAllowsRecentApprovedChanges),
    ("historical mutation policy requires management for extended window", HistoricalMutationPolicyRequiresManagementForExtendedWindow),
    ("historical mutation policy requires admin beyond extended window", HistoricalMutationPolicyRequiresAdminBeyondExtendedWindow),
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
    ("dynamic form field schema accepts all published types and value sources", AcceptsDynamicFormFieldSchemaContract),
    ("dynamic form field schema rejects unknown types", RejectsUnknownDynamicFormFieldType),
    ("dynamic form field schema rejects duplicate ids and orphan sections", RejectsDuplicateDynamicFormFieldIdsAndOrphanSections),
    ("dynamic form field schema rejects duplicate and excessive options", RejectsDuplicateAndExcessiveDynamicFormFieldOptions),
    ("dynamic form field schema rejects rich text statistics and invalid sources", RejectsRichTextStatisticsAndInvalidDynamicFormValueSources),
    ("dynamic form section title is required", BlocksBlankDynamicFormSectionTitle),
    ("dynamic form supports monthly BT 25 table blocks", SupportsMonthlyBtTwentyFiveTableBlocks),
    ("short text field statistics bucket by trimmed value", BucketsShortTextFieldStatistics),
    ("table statistics keep metrics from eligible blocks", TableStatisticsKeepMetricsFromEligibleBlocks),
    ("table statistics do not revive a canonical disabled block from legacy json", TableStatisticsDoNotReviveDisabledCanonicalBlockFromLegacyJson),
    ("stat projection accepts verified external payload snapshot", AcceptsVerifiedExternalPayloadForStatisticProjection),
    ("stat projection rejects embedded payload fallback", RejectsEmbeddedPayloadForStatisticProjection),
    ("stat projection rejects unverified external payload hash", RejectsUnverifiedExternalPayloadHashForStatisticProjection),
    ("form-only report can omit dynamic excel template id", AllowsFormOnlyReportWithoutDynamicExcelTemplate),
    ("report service resolves dynamic excel id from form blocks", ResolvesDynamicExcelIdFromFormBlocks),
    ("report payload preflights every document before Mongo writes", WorkReportPayloadPreflightContractTests.Run),
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
    ("basic summary rejects incompatible field method rules", BasicSummaryRejectsIncompatibleFieldMethodRules),
    ("basic summary refresh status controls enqueue", BasicSummaryRefreshStatusControlsEnqueue),
    ("basic summary reset request rebuilds from snapshot json", BasicSummaryResetRequestRebuildsFromSnapshotJson),
    ("basic summary extracts typed table values", BasicSummaryExtractsTypedTableValues),
    ("basic summary merges period snapshots by typed method", BasicSummaryMergesPeriodSnapshotsByTypedMethod),
    ("basic summary compact snapshot round-trips", BasicSummaryCompactSnapshotRoundTrips),
    ("basic summary respects compressed table null runs", BasicSummaryRespectsCompressedTableNullRuns),
    ("summary source scope normalizes flow modes", SummarySourceScopeNormalizesFlowModes),
    ("statistic diff previous period resolves key formats", StatisticDiffPreviousPeriodResolvesKeyFormats),
    ("statistic diff row join requires table identity", StatisticDiffRowJoinRequiresTableIdentity),
    ("statistic diff compatibility rejects concept mismatch", StatisticDiffCompatibilityRejectsConceptMismatch),
    ("statistic diff compatibility rejects data category mismatch", StatisticDiffCompatibilityRejectsDataCategoryMismatch),
    ("statistic diff accepts a field statistic label selector", StatisticDiffAcceptsFieldStatisticLabelSelector),
    ("advanced summary config hash includes source scope", AdvancedSummaryConfigHashIncludesSourceScope),
    ("advanced summary config normalizes object json", AdvancedSummaryConfigNormalizesObjectJson),
    ("advanced summary preview blocks unsupported range condition", AdvancedSummaryPreviewBlocksUnsupportedRangeCondition),
    ("advanced summary field gate enforces section limits", AdvancedSummaryFieldGateEnforcesSectionLimits),
    ("advanced summary hierarchy keys roll up day month year", AdvancedSummaryHierarchyKeysRollUpDayMonthYear),
    ("advanced summary day node source day uses completed date first", AdvancedSummaryDayNodeSourceDayUsesCompletedDateFirst),
    ("advanced summary dirty scopes include self and parent assignments", AdvancedSummaryDirtyScopesIncludeSelfAndParentAssignments),
    ("advanced summary dirty flow branch scope includes descendants", AdvancedSummaryDirtyFlowBranchScopeIncludesDescendants),
    ("advanced summary dirty status mutation rules cover approved and active changes", AdvancedSummaryDirtyStatusMutationRulesCoverApprovedAndActiveChanges),
    ("advanced summary hierarchy rollup merges child fields", AdvancedSummaryHierarchyRollupMergesChildFields),
    ("advanced summary hierarchy rollup requires clean children", AdvancedSummaryHierarchyRollupRequiresCleanChildren),
    ("advanced summary hierarchy query plans largest grains", AdvancedSummaryHierarchyQueryPlansLargestGrains),
    ("summary token month key uses utc month", SummaryTokenMonthKeyUsesUtcMonth),
    ("summary token quota boundary allows free initial lock", SummaryTokenQuotaBoundaryAllowsFreeInitialLock),
    ("summary token unit base quota uses active users", SummaryTokenUnitBaseQuotaUsesActiveUsers),
    ("summary token admin grants extend unit allowance", SummaryTokenAdminGrantsExtendUnitAllowance),
    ("summary token manager scope follows unit hierarchy", SummaryTokenManagerScopeFollowsUnitHierarchy),
    ("legacy work basis file resolves as work document", ResolvesLegacyWorkBasisFileAsWorkDocument),
    ("assignment file resolves as assignment branch document", ResolvesAssignmentFileAsBranchDocument),
    ("assignment document path resolves ancestors only", ResolvesAssignmentDocumentAncestorsFromPath),
    ("upload endpoint uses public base url override", BuildsUploadEndpointFromPublicBaseUrl),
    ("upload endpoint falls back to forwarded request scheme and host", BuildsUploadEndpointFromForwardedRequest),
    ("recurring job runner methods prevent overlap", RecurringJobRunnerMethodsPreventOverlap),
    ("dynamic form statistic recurring trigger is post-commit best effort", HangfireRecurringTriggerContractTests.Run),
    ("advanced summary operations normalize hierarchy grain", AdvancedSummaryOperationsNormalizeHierarchyGrain),
    ("advanced summary operations reset actor uses original requester", AdvancedSummaryOperationsResetActorUsesOriginalRequester),
    ("advanced summary operations cleanup limit is bounded", AdvancedSummaryOperationsCleanupLimitIsBounded),
    ("advanced summary operations cleanup requires scope selector", AdvancedSummaryOperationsCleanupRequiresScopeSelector),
    ("flow statistic diagnostics limit is bounded", FlowStatisticDiagnosticsLimitIsBounded),
    ("statistic rebuild operation row includes flow scope", StatisticRebuildOperationRowIncludesFlowScope),
    ("advanced summary diagnostics comparable hash ignores generated time", AdvancedSummaryDiagnosticsComparableHashIgnoresGeneratedTime),
    ("advanced summary diagnostics comparable hash covers rollup input count", AdvancedSummaryDiagnosticsComparableHashCoversRollupInputCount),
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

static void AllowsUnitManagerNormalUserBeforeFinalUnit()
{
    var actorUnit = TestUnit(6, "003", 1, parentUnitId: null);
    var actor = TestUser(5, "mu_actor", ManagementAccountKind.UnitManager, actorUnit.Id);
    var staff = TestUser(6, "staff", ManagementAccountKind.NormalUser, actorUnit.Id);
    WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, actorUnit,
        new[] { staff }, UnitMap(actorUnit), actorUnitHasAssignableDescendants: true);
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

static void AdvancedSummaryOperationsNormalizeHierarchyGrain()
{
    AssertEqual(
        WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
        JobRunManagementService.NormalizeAdvancedSummaryGrain("day"),
        "advanced operations should accept lowercase day grain");
    AssertEqual(
        WorkAssignmentAdvancedSummaryHierarchyGrains.Month,
        JobRunManagementService.NormalizeAdvancedSummaryGrain("MONTH"),
        "advanced operations should accept month grain");
    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => JobRunManagementService.NormalizeAdvancedSummaryGrain("quarter"));
}

static void AdvancedSummaryOperationsResetActorUsesOriginalRequester()
{
    var node = new WorkAssignmentAdvancedSummaryDayNode
    {
        CreatedByUserId = UserId(1),
        UpdatedByUserId = UserId(2)
    };

    AssertEqual(
        UserId(2),
        JobRunManagementService.ResolveAdvancedSummaryResetJobActor(node, UserId(9)),
        "advanced reset should requeue under the last requester so assignment read scope is preserved");

    node.UpdatedByUserId = null;
    AssertEqual(
        UserId(1),
        JobRunManagementService.ResolveAdvancedSummaryResetJobActor(node, UserId(9)),
        "advanced reset should fall back to creator when updated actor is missing");
}

static void AdvancedSummaryOperationsCleanupLimitIsBounded()
{
    AssertEqual(
        500,
        JobRunManagementService.NormalizeAdvancedSummaryCleanupLimit(0),
        "advanced cleanup should default empty limits to 500");
    AssertEqual(
        500,
        JobRunManagementService.NormalizeAdvancedSummaryCleanupLimit(-10),
        "advanced cleanup should default negative limits to 500");
    AssertEqual(
        1000,
        JobRunManagementService.NormalizeAdvancedSummaryCleanupLimit(5000),
        "advanced cleanup should cap each run to 1000 nodes");
}

static void AdvancedSummaryOperationsCleanupRequiresScopeSelector()
{
    AssertFalse(
        JobRunManagementService.HasAdvancedSummaryCleanupSelector(new AdvancedSummaryNodeCleanupRequest
        {
            Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
            Status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Failed
        }),
        "advanced cleanup should not allow a broad grain/status-only sweep");

    AssertFalse(
        JobRunManagementService.HasAdvancedSummaryCleanupSelector(new AdvancedSummaryNodeCleanupRequest
        {
            UpdatedBeforeUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        }),
        "advanced cleanup should not allow a broad time-only sweep");

    AssertTrue(
        JobRunManagementService.HasAdvancedSummaryCleanupSelector(new AdvancedSummaryNodeCleanupRequest
        {
            ConfigVersionNo = 2
        }),
        "advanced cleanup should allow config version scoped cleanup");

    AssertTrue(
        JobRunManagementService.HasAdvancedSummaryCleanupSelector(new AdvancedSummaryNodeCleanupRequest
        {
            SectionId = "summary-section",
            SourceSignatureHash = "source-sig"
        }),
        "advanced cleanup should allow section/source-signature scoped cleanup");
}

static void AdvancedSummaryDiagnosticsComparableHashIgnoresGeneratedTime()
{
    var first = @"{
        ""schemaVersion"": 1,
        ""kind"": ""ADVANCED_SUMMARY_DAY_NODE_V1"",
        ""generatedAtUtc"": ""2026-06-20T01:00:00Z"",
        ""configId"": ""cfg"",
        ""configHash"": ""hash"",
        ""grain"": ""DAY"",
        ""grainKey"": ""2026-06-01"",
        ""dayKey"": ""2026-06-01"",
        ""monthKey"": ""2026-06"",
        ""yearKey"": ""2026"",
        ""windowStartUtc"": ""2026-06-01T00:00:00Z"",
        ""windowEndExclusiveUtc"": ""2026-06-02T00:00:00Z"",
        ""sourceAssignmentCount"": 1,
        ""sourceReportCount"": 2,
        ""sectionReportCount"": 2,
        ""sectionFieldCount"": 1,
        ""targetFieldCount"": 1,
        ""inputNodeCount"": 0,
        ""warnings"": [],
        ""fields"": [
            {
                ""fieldId"": ""f1"",
                ""fieldKey"": ""field_1"",
                ""label"": ""Field 1"",
                ""dataType"": ""number"",
                ""method"": ""SUM"",
                ""valueCount"": 2,
                ""sourceReportCount"": 2,
                ""result"": 10,
                ""sampleValues"": []
            }
        ]
    }";
    var second = first.Replace("2026-06-20T01:00:00Z", "2026-06-20T02:00:00Z");
    var changedResult = first.Replace(@"""result"": 10", @"""result"": 11");

    var firstHash = WorkAssignmentAdvancedSummaryHierarchyService.BuildAdvancedSummaryComparableValueHash(first);
    var secondHash = WorkAssignmentAdvancedSummaryHierarchyService.BuildAdvancedSummaryComparableValueHash(second);
    var changedHash = WorkAssignmentAdvancedSummaryHierarchyService.BuildAdvancedSummaryComparableValueHash(changedResult);

    AssertEqual(firstHash, secondHash, "diagnostics comparable hash should ignore generatedAtUtc");
    AssertFalse(
        string.Equals(firstHash, changedHash, StringComparison.Ordinal),
        "diagnostics comparable hash should still change when statistic values change");
}

static void AdvancedSummaryDiagnosticsComparableHashCoversRollupInputCount()
{
    var monthValue = @"{
        ""schemaVersion"": 1,
        ""kind"": ""ADVANCED_SUMMARY_MONTH_NODE_V1"",
        ""generatedAtUtc"": ""2026-06-20T01:00:00Z"",
        ""configId"": ""cfg"",
        ""configHash"": ""hash"",
        ""grain"": ""MONTH"",
        ""grainKey"": ""2026-06"",
        ""dayKey"": null,
        ""monthKey"": ""2026-06"",
        ""yearKey"": ""2026"",
        ""windowStartUtc"": ""2026-06-01T00:00:00Z"",
        ""windowEndExclusiveUtc"": ""2026-07-01T00:00:00Z"",
        ""sourceAssignmentCount"": 1,
        ""sourceReportCount"": 30,
        ""sectionReportCount"": 30,
        ""sectionFieldCount"": 1,
        ""targetFieldCount"": 1,
        ""inputNodeCount"": 30,
        ""warnings"": [],
        ""fields"": [
            {
                ""fieldId"": ""f1"",
                ""fieldKey"": ""field_1"",
                ""label"": ""Field 1"",
                ""dataType"": ""number"",
                ""method"": ""SUM"",
                ""valueCount"": 30,
                ""sourceReportCount"": 30,
                ""result"": 300,
                ""sampleValues"": []
            }
        ]
    }";
    var changedInputCount = monthValue.Replace(@"""inputNodeCount"": 30", @"""inputNodeCount"": 29");

    var firstHash = WorkAssignmentAdvancedSummaryHierarchyService.BuildAdvancedSummaryComparableValueHash(monthValue);
    var changedHash = WorkAssignmentAdvancedSummaryHierarchyService.BuildAdvancedSummaryComparableValueHash(changedInputCount);

    AssertFalse(
        string.Equals(firstHash, changedHash, StringComparison.Ordinal),
        "diagnostics comparable hash should change when rollup input node count changes");
}

static void FlowStatisticDiagnosticsLimitIsBounded()
{
    AssertEqual(
        100,
        JobRunManagementService.NormalizeFlowStatisticDiagnosticsLimit(0),
        "flow statistic diagnostics should default empty limits to 100");
    AssertEqual(
        12,
        JobRunManagementService.NormalizeFlowStatisticDiagnosticsLimit(12),
        "flow statistic diagnostics should preserve small explicit limits");
    AssertEqual(
        500,
        JobRunManagementService.NormalizeFlowStatisticDiagnosticsLimit(5000),
        "flow statistic diagnostics should cap each sample to 500 reports");
}

static void StatisticRebuildOperationRowIncludesFlowScope()
{
    var job = new WorkReportStatisticRebuildJob
    {
        Id = ObjectId(90),
        DedupeKey = "bounded:flow:period",
        DynamicFormTemplateId = ObjectId(91),
        DynamicFormTemplateCode = "T-FLOW",
        DynamicFormTemplateName = "Flow Template",
        ScopeKind = WorkReportStatisticRebuildJobScopeKinds.Bounded,
        WorkId = ObjectId(92),
        WorkAssignmentId = ObjectId(93),
        FlowInstanceId = "flow-2026-06",
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
        PeriodInstanceKey = "20260622",
        Status = WorkReportStatisticRebuildJobStatuses.RetryWaiting,
        RequestedByUserId = UserId(7),
        Priority = WorkReportStatisticRebuildJobPriorities.High,
        TotalReportCount = 10,
        ProcessedReportCount = 4,
        FailedReportCount = 1,
        IsActive = true
    };

    var row = InvokePrivateStatic<StatisticRebuildJobRow>(
        typeof(JobRunManagementService),
        "ToStatisticRebuildJobRow",
        job);

    AssertEqual(job.ScopeKind, row.ScopeKind, "statistic rebuild operations should expose scope kind");
    AssertEqual(job.WorkId, row.WorkId, "statistic rebuild operations should expose work scope");
    AssertEqual(job.WorkAssignmentId, row.WorkAssignmentId, "statistic rebuild operations should expose assignment scope");
    AssertEqual(job.FlowInstanceId, row.FlowInstanceId, "statistic rebuild operations should expose flow scope");
    AssertEqual(job.FlowEffectiveStatus, row.FlowEffectiveStatus, "statistic rebuild operations should expose flow status scope");
    AssertEqual(job.PeriodInstanceKey, row.PeriodInstanceKey, "statistic rebuild operations should expose period scope");
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

static void MapsNonFlowAssignmentWithNullFlowMetadata()
{
    var assignmentId = MongoDB.Bson.ObjectId.GenerateNewId();
    var workId = MongoDB.Bson.ObjectId.GenerateNewId();
    var rootAssignmentId = MongoDB.Bson.ObjectId.GenerateNewId();
    var now = DateTime.UtcNow;

    var assignment = BsonSerializer.Deserialize<WorkAssignment>(new BsonDocument
    {
        { "_id", assignmentId },
        { "workId", workId },
        { "rootAssignmentId", rootAssignmentId.ToString() },
        { "level", 0 },
        { "code", "A001" },
        { "name", "Non-flow assignment" },
        { "path", $"/{assignmentId}" },
        { "assignmentType", WorkAssignmentTypes.Once },
        { "aggregationType", WorkAggregationTypes.Matrix },
        { "isActive", true },
        { "createdAtUtc", now },
        { "updatedAtUtc", now },
        { "isDeleted", false }
    });

    var detail = WorkAssignmentResponseMapper.ToResponse(assignment, hasData: false);
    AssertFlowMetadataNull(detail, "detail response");

    var listDocRole = BsonSerializer.Deserialize<AssignmentListDocRole>(new BsonDocument
    {
        { "workId", workId },
        { "assignmentId", assignmentId },
        { "rootAssignmentId", rootAssignmentId },
        { "level", 0 },
        { "code", "A001" },
        { "name", "Non-flow assignment" },
        { "path", $"/{assignmentId}" },
        { "assignmentType", WorkAssignmentTypes.Once },
        { "aggregationType", WorkAggregationTypes.Matrix },
        { "isActive", true },
        { "assignmentCreatedAtUtc", now },
        { "assignmentUpdatedAtUtc", now },
        { "isDeleted", false }
    });

    var listMethod = typeof(tdtd_be.Services.WorkAssignments.WorkAssignmentService)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(x =>
            x.Name == "ToListResponse" &&
            x.GetParameters().Length == 1 &&
            x.GetParameters()[0].ParameterType == typeof(AssignmentListDocRole));

    var list = (WorkAssignmentListResponse)listMethod.Invoke(null, new object?[] { listDocRole })!;
    AssertFlowMetadataNull(list, "list response");
}

static void DynamicFlowTemplatePayloadNormalizesDefaults()
{
    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        """{ "steps": [] }""",
        requireLockable: false);

    using var doc = JsonDocument.Parse(normalized);
    foreach (var property in new[]
    {
        "formNodes",
        "steps",
        "transitions",
        "actorPolicies",
        "fieldPolicies",
        "tableColumnPolicies",
        "mappingRules"
    })
    {
        AssertEqual(JsonValueKind.Array, doc.RootElement.GetProperty(property).ValueKind, $"{property} should be an array");
    }

    AssertEqual(JsonValueKind.Object, doc.RootElement.GetProperty("rollbackPolicy").ValueKind, "rollbackPolicy should default to object");
    AssertEqual(JsonValueKind.Object, doc.RootElement.GetProperty("finalResultPolicy").ValueKind, "finalResultPolicy should default to object");
    AssertEqual(JsonValueKind.Object, doc.RootElement.GetProperty("statisticProfile").ValueKind, "statisticProfile should default to object");
}

static void DynamicFlowTemplateRejectsUnexecutableStatisticProfile()
{
    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        """
        {
          "steps": [],
          "statisticProfile": { "diffMode": "NONE" }
        }
        """,
        requireLockable: false);

    using (var doc = JsonDocument.Parse(normalized))
    {
        AssertEqual(
            0,
            doc.RootElement.GetProperty("statisticProfile").EnumerateObject().Count(),
            "legacy no-op statistic profile should normalize to an empty object");
    }

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "statisticProfile": {
                "diffMode": "ROW_COMPARE",
                "currentField": "score"
              }
            }
            """,
            requireLockable: true));
}

static void DynamicFlowTemplateLockPayloadValidatesSteps()
{
    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson("{}", requireLockable: true));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "self", "stepCode": "SELF" },
                { "stepId": "self", "stepCode": "REVIEW" }
              ]
            }
            """,
            requireLockable: true));

    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        """
        {
          "rootDynamicFormTemplateId": "100000000000000000000088",
          "formNodes": [
            { "formNodeId": "root", "role": "ROOT", "dynamicFormTemplateId": "100000000000000000000088" }
          ],
          "steps": [
            { "stepId": "self", "stepCode": "SELF", "dynamicFormTemplateId": "100000000000000000000088" },
            { "stepId": "review", "stepCode": "REVIEW", "dynamicFormTemplateId": "100000000000000000000088" }
          ],
          "transitions": [
            { "fromStepId": "self", "toStepId": "review" }
          ]
        }
        """,
        requireLockable: true);

    using var doc = JsonDocument.Parse(normalized);
    AssertEqual(2, doc.RootElement.GetProperty("steps").GetArrayLength(), "lockable payload should keep valid steps");
}

static void DynamicFlowTopologyAnchorsRootFormsAndLegacyMappings()
{
    var rootForm = new DynamicFormTemplate
    {
        Id = ObjectId(73),
        Code = "DF_FLOW_ROOT",
        Name = "Flow root",
        FieldsJson = """[{ "id": "f_total", "key": "total", "type": "NUMBER" }]""",
        BlocksJson = "[]"
    };
    var childForm = new DynamicFormTemplate
    {
        Id = ObjectId(74),
        Code = "DF_FLOW_CHILD",
        Name = "Flow child",
        FieldsJson = """[{ "id": "f_amount", "key": "amount", "type": "NUMBER" }]""",
        BlocksJson = "[]"
    };
    var forms = new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal)
    {
        [rootForm.Id] = rootForm,
        [childForm.Id] = childForm
    };

    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        $$"""
        {
          "rootDynamicFormTemplateId": "{{rootForm.Id}}",
          "formNodes": [
            { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
            { "formNodeId": "child_form", "role": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
          ],
          "steps": [
            { "stepId": "root", "stepCode": "ROOT", "formNodeId": "root_form", "dynamicFormTemplateId": "{{rootForm.Id}}" },
            { "stepId": "child", "stepCode": "CHILD", "formNodeId": "child_form", "dynamicFormTemplateId": "{{childForm.Id}}" }
          ],
          "transitions": [{ "fromStepId": "root", "toStepId": "child" }],
          "mappingRules": [{
            "mappingId": "legacy_child_total",
            "sourceStepId": "child",
            "targetStepId": "root",
            "sourceFieldKey": "amount",
            "targetFieldKey": "total",
            "valueTransform": "SUM"
          }]
        }
        """,
        requireLockable: true,
        rootForm.Id,
        forms);

    using (var doc = JsonDocument.Parse(normalized))
    {
        var rule = doc.RootElement.GetProperty("mappingRules")[0];
        AssertEqual(childForm.Id, rule.GetProperty("sourceDynamicFormTemplateId").GetString(),
            "legacy source form should be canonicalized from its step");
        AssertEqual(rootForm.Id, rule.GetProperty("targetDynamicFormTemplateId").GetString(),
            "legacy target form should be canonicalized from its step");
        AssertEqual("CHILD", rule.GetProperty("sourceStepCode").GetString(),
            "legacy source step code should be canonicalized");
        AssertEqual("ROOT", rule.GetProperty("targetStepCode").GetString(),
            "legacy target step code should be canonicalized");
    }

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "child_form", "role": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "steps": [
                { "stepId": "wrong_root", "stepCode": "WRONG_ROOT", "dynamicFormTemplateId": "{{childForm.Id}}" },
                { "stepId": "declared_root", "stepCode": "DECLARED_ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" }
              ],
              "transitions": [{ "fromStepId": "wrong_root", "toStepId": "declared_root" }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "root_form_copy", "role": "CHILD", "dynamicFormTemplateId": "{{rootForm.Id}}" }
              ],
              "steps": [{ "stepId": "root", "stepCode": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "child_form", "role": "ROOT", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "steps": [{ "stepId": "root", "stepCode": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" }
              ],
              "steps": [{
                "stepId": "root",
                "stepCode": "ROOT",
                "formNodeId": "missing_form_node",
                "dynamicFormTemplateId": "{{rootForm.Id}}"
              }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "same", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "same", "role": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "steps": [{ "stepId": "root", "stepCode": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "child_form", "role": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "steps": [{
                "stepId": "root",
                "stepCode": "ROOT",
                "formNodeId": "root_form",
                "dynamicFormTemplateId": "{{childForm.Id}}"
              }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "child_form", "role": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "steps": [
                { "stepId": "root", "stepCode": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "stepId": "middle", "stepCode": "MIDDLE", "dynamicFormTemplateId": "{{childForm.Id}}" },
                { "stepId": "leaf", "stepCode": "LEAF", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "transitions": [
                { "fromStepId": "root", "toStepId": "middle" },
                { "fromStepId": "middle", "toStepId": "leaf" }
              ],
              "mappingRules": [{
                "mappingId": "legacy_skip_level",
                "sourceStepId": "leaf",
                "targetStepId": "root",
                "sourceFieldKey": "amount",
                "targetFieldKey": "total"
              }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "rootDynamicFormTemplateId": "{{rootForm.Id}}",
              "formNodes": [
                { "formNodeId": "root_form", "role": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "formNodeId": "child_form", "role": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "steps": [
                { "stepId": "root", "stepCode": "ROOT", "dynamicFormTemplateId": "{{rootForm.Id}}" },
                { "stepId": "child", "stepCode": "CHILD", "dynamicFormTemplateId": "{{childForm.Id}}" }
              ],
              "transitions": [{ "fromStepId": "root", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "legacy_without_steps",
                "sourceFieldKey": "amount",
                "targetFieldKey": "total"
              }]
            }
            """,
            requireLockable: true,
            rootForm.Id,
            forms));
}

static void DynamicFlowPolicyValidationChecksDynamicFormReferences()
{
    var form = new DynamicFormTemplate
    {
        Id = ObjectId(90),
        Code = "FORM_FLOW",
        Name = "Flow Form",
        CreatedByUsername = "admin",
        FieldsJson = """
        [
          { "id": "f_total", "key": "total", "name": "Total", "type": "number" }
        ]
        """,
        BlocksJson = """
        [
          {
            "blockId": "b1",
            "excelSpecKind": "TOP",
            "defaultDataType": "NUMBER",
            "dataRect": { "r0": 1, "c0": 0, "r1": 2, "c1": 1 },
            "w": 2,
            "h": 2,
            "statisticColumns": [
              { "columnKey": "amount" },
              { "columnIndex": 1 }
            ]
          }
        ]
        """
    };

    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        """
        {
          "steps": [
            { "stepId": "draft", "stepCode": "DRAFT" }
          ],
          "fieldPolicies": [
            { "stepId": "draft", "actorRole": "ISSUER", "fieldKey": "total", "read": true, "write": true, "required": true }
          ],
          "tableColumnPolicies": [
            { "stepCode": "DRAFT", "actorRole": "ISSUER", "blockId": "b1", "columnKey": "col_1", "read": true },
            { "stepCode": "DRAFT", "actorRole": "ISSUER", "blockId": "b1", "columnKey": "col_2", "read": true }
          ]
        }
        """,
        requireLockable: true,
        form);

    using var doc = JsonDocument.Parse(normalized);
    AssertEqual(1, doc.RootElement.GetProperty("fieldPolicies").GetArrayLength(), "valid field policy should remain");
    AssertEqual(2, doc.RootElement.GetProperty("tableColumnPolicies").GetArrayLength(), "valid table policies should remain");
    var normalizedFieldPolicy = doc.RootElement.GetProperty("fieldPolicies")[0];
    AssertEqual("f_total", normalizedFieldPolicy.GetProperty("fieldId").GetString(), "field policy should persist the canonical field id");
    AssertEqual("total", normalizedFieldPolicy.GetProperty("fieldKey").GetString(), "field policy should persist the canonical field key");

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "draft", "stepCode": "DRAFT" }
              ],
              "fieldPolicies": [
                { "stepId": "draft", "fieldKey": "missing", "read": true }
              ]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "draft", "stepCode": "DRAFT" }
              ],
              "tableColumnPolicies": [
                { "stepId": "draft", "blockId": "b1", "columnKey": "missing", "read": true }
              ]
            }
            """,
            requireLockable: true,
            form));

}

static void DynamicFlowMappingValidationChecksSchemaAndReferences()
{
    var form = new DynamicFormTemplate
    {
        Id = ObjectId(91),
        Code = "FORM_FLOW_MAP",
        Name = "Flow Mapping Form",
        CreatedByUsername = "admin",
        FieldsJson = """
        [
          { "id": "f_total", "key": "total", "name": "Total", "type": "number" },
          { "id": "f_note", "key": "note", "name": "Note", "type": "text" }
        ]
        """,
        BlocksJson = """
        [
          {
            "blockId": "b1",
            "dataRect": { "r0": 1, "c0": 0, "r1": 2, "c1": 1 },
            "w": 2,
            "h": 2,
            "indexMap": []
          }
        ]
        """
    };

    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        """
        {
          "steps": [
            { "stepId": "child", "stepCode": "CHILD" },
            { "stepId": "parent", "stepCode": "PARENT" }
          ],
          "transitions": [
            { "fromStepId": "parent", "toStepId": "child" }
          ],
          "mappingRules": [
            {
              "mappingId": "m_field_to_field",
              "mappingVersion": 1,
              "sourceStepId": "child",
              "targetStepId": "parent",
              "sourceFieldId": "f_total",
              "targetFieldKey": "total",
              "conceptCode": "TOTAL",
              "dataType": "NUMBER",
              "valueTransform": "SUM",
              "conflictPolicy": "OVERWRITE",
              "contributionPolicy": "EXCLUDE"
            },
            {
              "mappingId": "m_table_to_table",
              "mappingVersion": 1,
              "sourceStepCode": "CHILD",
              "targetStepCode": "PARENT",
              "sourceBlockId": "b1",
              "sourceColumnKey": "col_1",
              "targetBlockId": "b1",
              "targetColumnKey": "col_1",
              "joinKey": "rowKey",
              "valueTransform": "COPY",
              "conflictPolicy": "TARGET_WINS",
              "contributionPolicy": "INCLUDE"
            }
          ]
        }
        """,
        requireLockable: true,
        form);

    using var doc = JsonDocument.Parse(normalized);
    AssertEqual(2, doc.RootElement.GetProperty("mappingRules").GetArrayLength(), "valid mapping rules should remain");

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "child", "stepCode": "CHILD" },
                { "stepId": "parent", "stepCode": "PARENT" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "legacy_field_to_unspecified_table_row",
                "sourceStepId": "child",
                "targetStepId": "parent",
                "sourceFieldId": "f_total",
                "targetBlockId": "b1",
                "targetColumnKey": "col_1",
                "valueTransform": "COPY"
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "mappingRules": [
                { "mappingId": "m1", "sourceFieldId": "missing", "targetFieldId": "f_total" }
              ]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "field_to_unspecified_table_row",
                "evaluationGrain": "FLOW_INSTANCE",
                "inputs": [{
                  "inputKey": "value",
                  "dataType": "NUMBER",
                  "cardinality": "ONE",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "fieldKey": "total",
                    "dataType": "NUMBER"
                  }
                }],
                "target": {
                  "kind": "TABLE_COLUMN",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "parent",
                  "blockId": "b1",
                  "columnKey": "col_2",
                  "dataType": "NUMBER"
                },
                "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "mappingRules": [
                { "mappingId": "dup", "sourceFieldId": "f_total", "targetFieldId": "f_note" },
                { "mappingId": "dup", "sourceFieldId": "f_note", "targetFieldId": "f_total" }
              ]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "mappingRules": [
                { "mappingId": "m_bad_policy", "sourceFieldId": "f_total", "targetFieldId": "f_note", "conflictPolicy": "SILENT_DROP" }
              ]
            }
            """,
            requireLockable: true,
            form));
}

static void DynamicFlowStructuredMappingValidatesFunctionsAndUniqueTargets()
{
    var form = new DynamicFormTemplate
    {
        Id = ObjectId(81),
        Code = "FORM_STRUCTURED_MAP",
        Name = "Structured Mapping Form",
        CreatedByUsername = "admin",
        FieldsJson = """
        [
          { "id": "f_numerator", "key": "numerator", "type": "NUMBER" },
          { "id": "f_denominator", "key": "denominator", "type": "NUMBER" },
          { "id": "f_percentage", "key": "percentage", "type": "NUMBER" },
          { "id": "f_note", "key": "note", "type": "TEXT" }
        ]
        """,
        BlocksJson = """
        [
          {
            "blockId": "grid",
            "excelSpecKind": "TOP",
            "defaultDataType": "NUMBER",
            "dataRect": { "r0": 2, "c0": 1, "r1": 3, "c1": 2 },
            "w": 2,
            "h": 2,
            "specialRanges": [
              { "role": "FORMULA", "r0": 2, "c0": 1, "r1": 3, "c1": 1 }
            ],
            "indexMap": [
              { "index": 0, "columnKey": "metric_only", "metricKey": "metric_only" }
            ]
          }
        ]
        """
    };

    var normalized = DynamicFlowTemplateService.NormalizePayloadJson(
        $$"""
        {
          "steps": [
            { "stepId": "parent", "stepCode": "PARENT" },
            { "stepId": "child", "stepCode": "CHILD" }
          ],
          "transitions": [
            { "fromStepId": "parent", "toStepId": "child" }
          ],
          "mappingRules": [
            {
              "mappingId": "percentage",
              "mappingVersion": 1,
              "mappingKind": "FIELD",
              "evaluationGrain": "FLOW_INSTANCE",
              "errorPolicy": "BLOCK_APPLY",
              "conflictPolicy": "OVERWRITE",
              "inputs": [
                {
                  "inputKey": "numerator",
                  "dataType": "NUMBER",
                  "cardinality": "ONE",
                  "nullPolicy": "ERROR",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "stepCode": "CHILD",
                    "fieldKey": "numerator",
                    "dataType": "NUMBER"
                  }
                },
                {
                  "inputKey": "denominator",
                  "dataType": "NUMBER",
                  "cardinality": "ONE",
                  "nullPolicy": "ERROR",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "stepCode": "CHILD",
                    "fieldKey": "denominator",
                    "dataType": "NUMBER"
                  }
                }
              ],
              "target": {
                "kind": "FIELD",
                "dynamicFormTemplateId": "{{form.Id}}",
                "stepId": "parent",
                "stepCode": "PARENT",
                "fieldKey": "percentage",
                "dataType": "NUMBER"
              },
              "calculation": {
                "kind": "REGISTERED_FUNCTION",
                "functionCode": "CALC_PERCENTAGE",
                "functionVersion": 1,
                "resultDataType": "NUMBER",
                "arguments": {
                  "numerator": { "input": "numerator" },
                  "denominator": { "input": "denominator" }
                }
              }
            },
            {
              "mappingId": "grid-column",
              "mappingVersion": 1,
              "mappingKind": "TABLE_COLUMN",
              "evaluationGrain": "TABLE_ROW",
              "inputs": [
                {
                  "inputKey": "amount",
                  "dataType": "NUMBER",
                  "cardinality": "ONE",
                  "nullPolicy": "SKIP",
                  "source": {
                    "kind": "TABLE_COLUMN",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "blockId": "grid",
                    "columnKey": "col_2",
                    "dataType": "NUMBER"
                  }
                }
              ],
              "target": {
                "kind": "TABLE_COLUMN",
                "dynamicFormTemplateId": "{{form.Id}}",
                "stepId": "parent",
                "blockId": "grid",
                "columnKey": "col_2",
                "dataType": "NUMBER"
              },
              "calculation": {
                "kind": "DIRECT",
                "operation": "copy",
                "resultDataType": "NUMBER"
              }
            }
          ]
        }
        """,
        requireLockable: true,
        form);

    using var doc = JsonDocument.Parse(normalized);
    AssertEqual(2, doc.RootElement.GetProperty("mappingRules").GetArrayLength(), "structured mappings should remain after validation");

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "fully-masked-column",
                "inputs": [{
                  "inputKey": "value",
                  "dataType": "NUMBER",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "fieldKey": "numerator",
                    "dataType": "NUMBER"
                  }
                }],
                "target": {
                  "kind": "TABLE_COLUMN",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "parent",
                  "blockId": "grid",
                  "columnKey": "col_1",
                  "dataType": "NUMBER"
                },
                "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "copy-number-as-text",
                "inputs": [{
                  "inputKey": "value",
                  "dataType": "NUMBER",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "fieldKey": "numerator",
                    "dataType": "NUMBER"
                  }
                }],
                "target": {
                  "kind": "FIELD",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "parent",
                  "fieldKey": "note",
                  "dataType": "TEXT"
                },
                "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "TEXT" }
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "percentage-with-text-argument",
                "inputs": [
                  {
                    "inputKey": "numerator",
                    "dataType": "NUMBER",
                    "source": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "child", "fieldKey": "numerator", "dataType": "NUMBER" }
                  },
                  {
                    "inputKey": "denominator",
                    "dataType": "NUMBER",
                    "source": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "child", "fieldKey": "denominator", "dataType": "NUMBER" }
                  }
                ],
                "target": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "parent", "fieldKey": "percentage", "dataType": "NUMBER" },
                "calculation": {
                  "kind": "REGISTERED_FUNCTION",
                  "functionCode": "CALC_PERCENTAGE",
                  "functionVersion": 1,
                  "resultDataType": "NUMBER",
                  "arguments": {
                    "numerator": { "value": "khong-phai-so" },
                    "denominator": { "input": "denominator" }
                  }
                }
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "statistic-index-is-not-a-column-catalog",
                "inputs": [{
                  "inputKey": "value",
                  "dataType": "NUMBER",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "fieldKey": "numerator",
                    "dataType": "NUMBER"
                  }
                }],
                "target": {
                  "kind": "TABLE_COLUMN",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "parent",
                  "blockId": "grid",
                  "columnKey": "metric_only",
                  "dataType": "NUMBER"
                },
                "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "mismatched-field-aliases",
                "inputs": [{
                  "inputKey": "value",
                  "dataType": "NUMBER",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "fieldId": "f_numerator",
                    "fieldKey": "denominator",
                    "dataType": "NUMBER"
                  }
                }],
                "target": {
                  "kind": "FIELD",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "parent",
                  "fieldKey": "percentage",
                  "dataType": "NUMBER"
                },
                "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
              }]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [
                {
                  "mappingId": "target-by-key",
                  "inputs": [{
                    "inputKey": "a",
                    "dataType": "NUMBER",
                    "source": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "child", "fieldKey": "numerator", "dataType": "NUMBER" }
                  }],
                  "target": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "parent", "fieldKey": "percentage", "dataType": "NUMBER" },
                  "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
                },
                {
                  "mappingId": "target-by-id",
                  "inputs": [{
                    "inputKey": "b",
                    "dataType": "NUMBER",
                    "source": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "child", "fieldKey": "denominator", "dataType": "NUMBER" }
                  }],
                  "target": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "parent", "fieldId": "f_percentage", "dataType": "NUMBER" },
                  "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
                }
              ]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [
                { "fromStepId": "parent", "toStepId": "child" }
              ],
              "mappingRules": [
                {
                  "mappingId": "first",
                  "inputs": [{
                    "inputKey": "a",
                    "dataType": "NUMBER",
                    "source": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "child", "fieldKey": "numerator" }
                  }],
                  "target": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "parent", "fieldKey": "percentage", "dataType": "NUMBER" },
                  "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
                },
                {
                  "mappingId": "second",
                  "inputs": [{
                    "inputKey": "b",
                    "dataType": "NUMBER",
                    "source": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "child", "fieldKey": "denominator" }
                  }],
                  "target": { "kind": "FIELD", "dynamicFormTemplateId": "{{form.Id}}", "stepId": "parent", "fieldKey": "percentage", "dataType": "NUMBER" },
                  "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
                }
              ]
            }
            """,
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            $$"""
            {
              "steps": [
                { "stepId": "parent", "stepCode": "PARENT" },
                { "stepId": "child", "stepCode": "CHILD" }
              ],
              "transitions": [{ "fromStepId": "parent", "toStepId": "child" }],
              "mappingRules": [{
                "mappingId": "mismatched-step",
                "inputs": [{
                  "inputKey": "a",
                  "dataType": "NUMBER",
                  "source": {
                    "kind": "FIELD",
                    "dynamicFormTemplateId": "{{form.Id}}",
                    "stepId": "child",
                    "stepCode": "PARENT",
                    "fieldKey": "numerator"
                  }
                }],
                "target": {
                  "kind": "FIELD",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "parent",
                  "dataType": "NUMBER",
                  "fieldKey": "percentage"
                },
                "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
              }]
            }
            """,
            requireLockable: true,
            form));
}

static void DynamicFlowExpressionsEvaluateNamedValueOperands()
{
    var expression = JsonNode.Parse("""{"op":"trim","value":{"input":"text"}}""")!;
    var inferredType = DynamicFlowMappingExpressionEvaluator.Validate(
        expression,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = "TEXT" });
    AssertEqual("TEXT", inferredType, "named value operand should be validated as the operation argument");

    var result = DynamicFlowMappingExpressionEvaluator.Evaluate(
        expression,
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["text"] = JsonValue.Create("  Dữ liệu  ")
        });
    AssertEqual("Dữ liệu", result!.GetValue<string>(), "named value operand should be evaluated before trim");

    var copied = DynamicFlowMappingExpressionEvaluator.Evaluate(
        JsonNode.Parse("""{"op":"copy","args":[{"input":"items"}]}""")!,
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["items"] = new JsonArray(1, 2)
        });
    AssertTrue(copied is JsonArray copiedArray && copiedArray.Count == 2,
        "copy should preserve a collection instead of silently taking its first item");

    var concatenated = DynamicFlowMappingExpressionEvaluator.Evaluate(
        JsonNode.Parse("""{"op":"concat","separator":" ","args":[{"value":"Nguyễn"},{"value":"An"}]}""")!,
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal));
    AssertEqual("Nguyễn An", concatenated!.GetValue<string>(),
        "concat should preserve a whitespace-only separator literal");

    try
    {
        _ = DynamicFlowMappingExpressionEvaluator.Evaluate(
            JsonNode.Parse("""{"op":"divide","args":[{"input":"left"},{"input":"right"}]}""")!,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["left"] = new JsonArray(10, 20),
                ["right"] = JsonValue.Create(2)
            });
        throw new InvalidOperationException("divide should reject expanded collection arguments");
    }
    catch (DynamicFlowMappingEvaluationException ex)
    {
        AssertEqual("DYNAMIC_FLOW_MAPPING_EXPRESSION_ARGUMENT_COUNT_INVALID", ex.Reason,
            "scalar expressions must not silently ignore collection values");
    }
}

static void DynamicFlowStructuredMappingRejectsCollectionCopyAndInvalidLiterals()
{
    var form = new DynamicFormTemplate
    {
        Id = ObjectId(75),
        Code = "DF_MAPPING_LITERAL",
        Name = "Mapping literal contract",
        FieldsJson = """
        [
          { "id": "f_source", "key": "source", "type": "NUMBER" },
          { "id": "f_target", "key": "target", "type": "NUMBER" }
        ]
        """,
        BlocksJson = """
        [
          {
            "blockId": "grid",
            "tableMode": "FIXED_GRID",
            "excelSpecKind": "TOP",
            "defaultDataType": "NUMBER",
            "dataRect": { "r0": 0, "c0": 0, "r1": 1, "c1": 0 },
            "w": 1,
            "h": 2,
            "specialRanges": []
          }
        ]
        """
    };

    string Payload(string rule) => $$"""
    {
      "rootDynamicFormTemplateId": "{{form.Id}}",
      "formNodes": [
        { "formNodeId": "root", "role": "ROOT", "dynamicFormTemplateId": "{{form.Id}}" }
      ],
      "steps": [
        { "stepId": "parent", "stepCode": "PARENT", "stepOrder": 1, "formNodeId": "root", "dynamicFormTemplateId": "{{form.Id}}" },
        { "stepId": "child", "stepCode": "CHILD", "stepOrder": 2, "formNodeId": "root", "dynamicFormTemplateId": "{{form.Id}}" }
      ],
      "transitions": [
        { "fromStepId": "parent", "toStepId": "child" }
      ],
      "mappingRules": [{{rule}}]
    }
    """;

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            Payload($$"""
            {
              "mappingId": "copy-many",
              "evaluationGrain": "FLOW_INSTANCE",
              "inputs": [{
                "inputKey": "items",
                "dataType": "NUMBER",
                "cardinality": "MANY",
                "source": {
                  "kind": "TABLE_COLUMN",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "child",
                  "blockId": "grid",
                  "columnKey": "col_1",
                  "dataType": "NUMBER"
                }
              }],
              "target": {
                "kind": "FIELD",
                "dynamicFormTemplateId": "{{form.Id}}",
                "stepId": "parent",
                "fieldKey": "target",
                "dataType": "NUMBER"
              },
              "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
            }
            """),
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            Payload($$"""
            {
              "mappingId": "copy-many-table-row",
              "evaluationGrain": "TABLE_ROW",
              "inputs": [{
                "inputKey": "items",
                "dataType": "NUMBER",
                "cardinality": "MANY",
                "source": {
                  "kind": "TABLE_COLUMN",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "child",
                  "blockId": "grid",
                  "columnKey": "col_1",
                  "dataType": "NUMBER"
                }
              }],
              "target": {
                "kind": "TABLE_COLUMN",
                "dynamicFormTemplateId": "{{form.Id}}",
                "stepId": "parent",
                "blockId": "grid",
                "columnKey": "col_1",
                "dataType": "NUMBER"
              },
              "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
            }
            """),
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            Payload($$"""
            {
              "mappingId": "constant-type",
              "inputs": [{
                "inputKey": "constant",
                "dataType": "NUMBER",
                "constantValue": "không-phải-số",
                "source": { "kind": "CONSTANT" }
              }],
              "target": {
                "kind": "FIELD",
                "dynamicFormTemplateId": "{{form.Id}}",
                "stepId": "parent",
                "fieldKey": "target",
                "dataType": "NUMBER"
              },
              "calculation": { "kind": "DIRECT", "operation": "copy", "resultDataType": "NUMBER" }
            }
            """),
            requireLockable: true,
            form));

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            Payload($$"""
            {
              "mappingId": "default-type",
              "errorPolicy": "USE_DEFAULT",
              "inputs": [{
                "inputKey": "source",
                "dataType": "NUMBER",
                "source": {
                  "kind": "FIELD",
                  "dynamicFormTemplateId": "{{form.Id}}",
                  "stepId": "child",
                  "fieldKey": "source",
                  "dataType": "NUMBER"
                }
              }],
              "target": {
                "kind": "FIELD",
                "dynamicFormTemplateId": "{{form.Id}}",
                "stepId": "parent",
                "fieldKey": "target",
                "dataType": "NUMBER"
              },
              "calculation": {
                "kind": "DIRECT",
                "operation": "copy",
                "resultDataType": "NUMBER",
                "defaultValue": "sai-kiểu"
              }
            }
            """),
            requireLockable: true,
            form));
}

static void DynamicFlowMappingEngineProjectsValuesAndProvenance()
{
    var target = new WorkAssignmentReport
    {
        Id = ObjectId(106),
        WorkId = ObjectId(107),
        WorkAssignmentId = ObjectId(108),
        WorkReportPeriodId = ObjectId(109),
        DynamicFormTemplateId = ObjectId(110),
        AssigneeUserId = ObjectId(111),
        PeriodKey = "2026-06",
        PeriodInstanceKey = "2026-06",
        Values1DJson = "[]",
        FieldValuesJson = """{"values":{"existing":"keep"}}""",
        TableValuesJson = """{"blocks":[]}"""
    };
    var sourceReport = new WorkAssignmentReport
    {
        Id = ObjectId(112),
        WorkId = target.WorkId,
        WorkAssignmentId = ObjectId(113),
        WorkReportPeriodId = ObjectId(114),
        DynamicFormTemplateId = target.DynamicFormTemplateId,
        AssigneeUserId = ObjectId(115),
        PeriodKey = target.PeriodKey,
        PeriodInstanceKey = target.PeriodInstanceKey,
        Values1DJson = "[]",
        FieldValuesJson = """{"values":{"total":5}}""",
        TableValuesJson = """
        {
          "blocks": [
            {
              "blockId": "b1",
              "tableMode": "APPEND_ROWS",
              "rows": [
                { "rowKey": "r1", "cells": { "amount": 2 } },
                { "rowKey": "r2", "cells": { "amount": 3 } }
              ]
            }
          ]
        }
        """
    };
    var source = new DynamicFlowMappingSourceReport(
        sourceReport,
        "child",
        "CHILD",
        sourceReport.FieldValuesJson,
        sourceReport.TableValuesJson);

    var projection = DynamicFlowMappingEngine.Preview(
        target,
        new List<DynamicFlowMappingSourceReport> { source },
        new List<DynamicFlowMappingRuleDto>
        {
            new()
            {
                MappingId = "m_field",
                MappingVersion = 1,
                SourceStepId = "child",
                SourceFieldKey = "total",
                TargetFieldKey = "mapped_total",
                ContributionPolicy = "EXCLUDE"
            },
            new()
            {
                MappingId = "m_sum",
                MappingVersion = 1,
                SourceStepCode = "CHILD",
                SourceBlockId = "b1",
                SourceColumnKey = "amount",
                TargetFieldKey = "mapped_sum",
                ValueTransform = "SUM"
            }
        },
        requestConflictPolicy: null,
        requestContributionPolicy: null,
        nowUtc: new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc));

    using var fields = JsonDocument.Parse(projection.FieldValuesJson!);
    var values = fields.RootElement.GetProperty("values");
    AssertEqual(5m, values.GetProperty("mapped_total").GetDecimal(), "field mapping should copy source field value");
    AssertEqual(5m, values.GetProperty("mapped_sum").GetDecimal(), "table-to-field SUM mapping should aggregate values");
    AssertEqual(2, projection.Changes.Count(x => x.Status == "APPLIED"), "mapping preview should report applied changes");
    AssertFalse(projection.HasBlockingConflicts, "mapping preview should not mark non-conflicting projection as blocked");

    using var summary = JsonDocument.Parse(projection.SummarySourceJson!);
    AssertEqual("DYNAMIC_FLOW_MAPPING", summary.RootElement.GetProperty("kind").GetString(), "summary source should preserve mapping kind");
    AssertEqual(sourceReport.Id, summary.RootElement.GetProperty("sourceReportIds")[0].GetString(), "summary source should keep source report id");

    using var contribution = JsonDocument.Parse(projection.CumulativeContributionPolicyJson!);
    AssertEqual("EXCLUDE", contribution.RootElement.GetProperty("defaultMode").GetString(), "P7 mapping contribution should remain excluded by default");
    AssertEqual(2, contribution.RootElement.GetProperty("rules").GetArrayLength(), "generated mapping targets should be excluded by default");
}

static void DynamicFlowMappingEngineEvaluatesMultipleInputsAndFixedGridSlots()
{
    var formId = ObjectId(122);
    var target = new WorkAssignmentReport
    {
        Id = ObjectId(123),
        WorkId = ObjectId(124),
        WorkAssignmentId = ObjectId(125),
        WorkReportPeriodId = ObjectId(126),
        DynamicFormTemplateId = formId,
        AssigneeUserId = ObjectId(127),
        PeriodKey = "2026-07",
        PeriodInstanceKey = "2026-07",
        Values1DJson = "[]",
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """{"blocks":[]}"""
    };
    var sourceReport = new WorkAssignmentReport
    {
        Id = ObjectId(128),
        WorkId = target.WorkId,
        WorkAssignmentId = ObjectId(129),
        WorkReportPeriodId = ObjectId(130),
        DynamicFormTemplateId = formId,
        AssigneeUserId = ObjectId(131),
        PeriodKey = target.PeriodKey,
        PeriodInstanceKey = target.PeriodInstanceKey,
        Values1DJson = "[]",
        FieldValuesJson = """{"values":{"numerator":25,"denominator":100}}""",
        TableValuesJson = """
        {
          "blocks": [
            {
              "blockId": "grid",
              "tableMode": "FIXED_GRID",
              "hasSpecialRanges": true,
              "values1D": [1, 2, 3, 4],
              "indexMap": [],
              "valueSlots": [
                { "index": 0, "rowKey": "row_1", "columnKey": "col_1" },
                { "index": 1, "rowKey": "row_1", "columnKey": "col_2" },
                { "index": 2, "rowKey": "row_2", "columnKey": "col_1" },
                { "index": 3, "rowKey": "row_2", "columnKey": "col_2" }
              ],
              "cells": [
                { "rowKey": "row_1", "columnKey": "col_2", "value": 2 },
                { "rowKey": "row_2", "columnKey": "col_2", "value": 4 }
              ]
            }
          ]
        }
        """
    };
    var source = new DynamicFlowMappingSourceReport(
        sourceReport,
        "child",
        "CHILD",
        sourceReport.FieldValuesJson,
        sourceReport.TableValuesJson);

    DynamicFlowMappingEndpointDto FieldEndpoint(string stepId, string stepCode, string fieldKey) => new()
    {
        Kind = "FIELD",
        DynamicFormTemplateId = formId,
        StepId = stepId,
        StepCode = stepCode,
        FieldKey = fieldKey,
        DataType = "NUMBER"
    };

    var rules = new List<DynamicFlowMappingRuleDto>
    {
        new()
        {
            MappingId = "percentage",
            MappingVersion = 1,
            MappingKind = "FIELD",
            EvaluationGrain = "FLOW_INSTANCE",
            ErrorPolicy = "BLOCK_APPLY",
            ConflictPolicy = "OVERWRITE",
            Inputs = new List<DynamicFlowMappingInputDto>
            {
                new()
                {
                    InputKey = "numerator",
                    Source = FieldEndpoint("child", "CHILD", "numerator"),
                    DataType = "NUMBER",
                    Cardinality = "ONE",
                    NullPolicy = "ERROR"
                },
                new()
                {
                    InputKey = "denominator",
                    Source = FieldEndpoint("child", "CHILD", "denominator"),
                    DataType = "NUMBER",
                    Cardinality = "ONE",
                    NullPolicy = "ERROR"
                }
            },
            Target = FieldEndpoint("parent", "PARENT", "percentage"),
            Calculation = new DynamicFlowMappingCalculationDto
            {
                Kind = "REGISTERED_FUNCTION",
                FunctionCode = "CALC_PERCENTAGE",
                FunctionVersion = 1,
                ResultDataType = "NUMBER",
                Arguments = new Dictionary<string, JsonNode?>
                {
                    ["numerator"] = JsonNode.Parse("""{"input":"numerator"}"""),
                    ["denominator"] = JsonNode.Parse("""{"input":"denominator"}""")
                }
            }
        },
        new()
        {
            MappingId = "column-sum",
            MappingVersion = 1,
            MappingKind = "FIELD",
            EvaluationGrain = "FLOW_INSTANCE",
            ErrorPolicy = "BLOCK_APPLY",
            Inputs = new List<DynamicFlowMappingInputDto>
            {
                new()
                {
                    InputKey = "amounts",
                    Source = new DynamicFlowMappingEndpointDto
                    {
                        Kind = "TABLE_COLUMN",
                        DynamicFormTemplateId = formId,
                        StepId = "child",
                        StepCode = "CHILD",
                        BlockId = "grid",
                        ColumnKey = "col_2",
                        DataType = "NUMBER"
                    },
                    DataType = "NUMBER",
                    Cardinality = "MANY",
                    NullPolicy = "SKIP"
                }
            },
            Target = FieldEndpoint("parent", "PARENT", "column_total"),
            Calculation = new DynamicFlowMappingCalculationDto
            {
                Kind = "DIRECT",
                Operation = "sum",
                ResultDataType = "NUMBER"
            }
        }
    };

    var projection = DynamicFlowMappingEngine.Preview(
        target,
        new List<DynamicFlowMappingSourceReport> { source },
        rules,
        requestConflictPolicy: null,
        requestContributionPolicy: null,
        nowUtc: new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        targetStepId: "parent",
        targetStepCode: "PARENT");

    using var fields = JsonDocument.Parse(projection.FieldValuesJson!);
    var values = fields.RootElement.GetProperty("values");
    AssertEqual(25m, values.GetProperty("percentage").GetDecimal(), "registered function should evaluate two field inputs");
    AssertEqual(6m, values.GetProperty("column_total").GetDecimal(), "fixed grid value slots should resolve the requested column");
    AssertEqual(2, projection.Changes.Count(change => change.Status == "APPLIED"), "both structured rules should apply");
    AssertEqual(2, projection.Changes.Single(change => change.MappingId == "percentage").Sources.Count, "field function should preserve both input provenances");
    AssertEqual(2, projection.Changes.Single(change => change.MappingId == "column-sum").Sources.Count, "table sum should preserve each source cell provenance");
}

static void DynamicFlowMappingEngineKeepsTableRuntimeProjectionsAligned()
{
    var formId = ObjectId(82);
    var target = new WorkAssignmentReport
    {
        Id = ObjectId(83),
        WorkId = ObjectId(84),
        WorkAssignmentId = ObjectId(85),
        WorkReportPeriodId = ObjectId(86),
        DynamicFormTemplateId = formId,
        AssigneeUserId = ObjectId(87),
        PeriodKey = "2026-07",
        PeriodInstanceKey = "2026-07",
        Values1DJson = "[]",
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """
        {
          "blocks": [
            {
              "blockId": "append_target",
              "tableMode": "APPEND_ROWS",
              "values1D": [null, null],
              "valueSlots": [
                { "index": 0, "rowKey": "row_1", "columnKey": "col_1" },
                { "index": 1, "rowKey": "row_1", "columnKey": "col_2" }
              ],
              "rows": []
            },
            {
              "blockId": "matrix_target",
              "tableMode": "MATRIX",
              "values1D": [null],
              "valueSlots": [
                { "index": 0, "rowKey": "row_1", "columnKey": "col_1" }
              ],
              "cells": []
            }
          ]
        }
        """
    };
    var sourceReport = new WorkAssignmentReport
    {
        Id = ObjectId(88),
        WorkId = target.WorkId,
        WorkAssignmentId = ObjectId(89),
        WorkReportPeriodId = ObjectId(90),
        DynamicFormTemplateId = formId,
        AssigneeUserId = ObjectId(91),
        PeriodKey = target.PeriodKey,
        PeriodInstanceKey = target.PeriodInstanceKey,
        Values1DJson = "[]",
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """
        {
          "blocks": [
            {
              "blockId": "append_source",
              "tableMode": "APPEND_ROWS",
              "values1D": [7],
              "valueSlots": [
                { "index": 0, "rowKey": "row_1", "columnKey": "col_1" }
              ],
              "rows": []
            }
          ]
        }
        """
    };
    var source = new DynamicFlowMappingSourceReport(
        sourceReport,
        "child",
        "CHILD",
        sourceReport.FieldValuesJson,
        sourceReport.TableValuesJson);

    DynamicFlowMappingRuleDto TableRule(string mappingId, string targetBlockId, string targetColumnKey) => new()
    {
        MappingId = mappingId,
        MappingVersion = 1,
        MappingKind = "TABLE_COLUMN",
        EvaluationGrain = "TABLE_ROW",
        ErrorPolicy = "BLOCK_APPLY",
        ConflictPolicy = "OVERWRITE",
        Inputs = new List<DynamicFlowMappingInputDto>
        {
            new()
            {
                InputKey = "amount",
                Source = new DynamicFlowMappingEndpointDto
                {
                    Kind = "TABLE_COLUMN",
                    DynamicFormTemplateId = formId,
                    StepId = "child",
                    StepCode = "CHILD",
                    BlockId = "append_source",
                    ColumnKey = "col_1",
                    DataType = "NUMBER"
                },
                DataType = "NUMBER",
                Cardinality = "ONE",
                NullPolicy = "ERROR"
            }
        },
        Target = new DynamicFlowMappingEndpointDto
        {
            Kind = "TABLE_COLUMN",
            DynamicFormTemplateId = formId,
            StepId = "parent",
            StepCode = "PARENT",
            BlockId = targetBlockId,
            ColumnKey = targetColumnKey,
            DataType = "NUMBER"
        },
        Calculation = new DynamicFlowMappingCalculationDto
        {
            Kind = "DIRECT",
            Operation = "copy",
            ResultDataType = "NUMBER"
        }
    };

    var projection = DynamicFlowMappingEngine.Preview(
        target,
        new List<DynamicFlowMappingSourceReport> { source },
        new List<DynamicFlowMappingRuleDto>
        {
            TableRule("append-copy", "append_target", "col_2"),
            TableRule("matrix-copy", "matrix_target", "col_1")
        },
        requestConflictPolicy: null,
        requestContributionPolicy: null,
        nowUtc: new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        targetStepId: "parent",
        targetStepCode: "PARENT");

    using var tables = JsonDocument.Parse(projection.TableValuesJson!);
    var blocks = tables.RootElement.GetProperty("blocks");
    var append = blocks.EnumerateArray().Single(block => block.GetProperty("blockId").GetString() == "append_target");
    var matrix = blocks.EnumerateArray().Single(block => block.GetProperty("blockId").GetString() == "matrix_target");

    AssertEqual(7m, append.GetProperty("values1D")[1].GetDecimal(), "append-row mapping should update the editor values1D payload");
    AssertEqual(7m, append.GetProperty("rows")[0].GetProperty("cells").GetProperty("col_2").GetDecimal(), "append-row mapping should update the statistic row projection");
    AssertEqual("row_1", append.GetProperty("rows")[0].GetProperty("rowKey").GetString(), "append-row value slots should preserve the data-rectangle row axis when the row projection is absent");
    AssertEqual(7m, matrix.GetProperty("values1D")[0].GetDecimal(), "matrix mapping should update the editor values1D payload");
    AssertEqual(7m, matrix.GetProperty("cells")[0].GetProperty("value").GetDecimal(), "matrix mapping should update the statistic cell projection");
    AssertEqual(2, projection.Changes.Count(change => change.Status == "APPLIED"), "both table mappings should apply once");
}

static void DynamicFlowMappingEnginePreservesSparseRowCoordinates()
{
    var formId = ObjectId(76);
    var target = new WorkAssignmentReport
    {
        Id = ObjectId(77),
        WorkId = ObjectId(78),
        WorkAssignmentId = ObjectId(79),
        WorkReportPeriodId = ObjectId(80),
        DynamicFormTemplateId = formId,
        PeriodKey = "2026-07",
        PeriodInstanceKey = "2026-07",
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """
        {
          "blocks": [{
            "blockId": "target_rows",
            "tableMode": "APPEND_ROWS",
            "values1D": [null, null],
            "valueSlots": [
              { "index": 0, "rowKey": "row_1", "columnKey": "col_1" },
              { "index": 1, "rowKey": "row_2", "columnKey": "col_1" }
            ],
            "rows": []
          }]
        }
        """
    };
    var sourceReport = new WorkAssignmentReport
    {
        Id = ObjectId(81),
        WorkId = target.WorkId,
        WorkAssignmentId = ObjectId(82),
        WorkReportPeriodId = ObjectId(83),
        DynamicFormTemplateId = formId,
        PeriodKey = target.PeriodKey,
        PeriodInstanceKey = target.PeriodInstanceKey,
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """
        {
          "blocks": [{
            "blockId": "source_rows",
            "tableMode": "APPEND_ROWS",
            "values1D": [null, 7],
            "valueSlots": [
              { "index": 0, "rowKey": "row_1", "columnKey": "col_1" },
              { "index": 1, "rowKey": "row_2", "columnKey": "col_1" }
            ],
            "rows": []
          }]
        }
        """
    };
    var source = new DynamicFlowMappingSourceReport(
        sourceReport,
        "child",
        "CHILD",
        sourceReport.FieldValuesJson,
        sourceReport.TableValuesJson);
    var rule = new DynamicFlowMappingRuleDto
    {
        MappingId = "sparse-row",
        MappingVersion = 1,
        MappingKind = "TABLE_COLUMN",
        EvaluationGrain = "TABLE_ROW",
        ErrorPolicy = "BLOCK_APPLY",
        ConflictPolicy = "OVERWRITE",
        Inputs = new List<DynamicFlowMappingInputDto>
        {
            new()
            {
                InputKey = "amount",
                Source = new DynamicFlowMappingEndpointDto
                {
                    Kind = "TABLE_COLUMN",
                    DynamicFormTemplateId = formId,
                    StepId = "child",
                    BlockId = "source_rows",
                    ColumnKey = "col_1",
                    DataType = "NUMBER"
                },
                DataType = "NUMBER",
                Cardinality = "ONE",
                NullPolicy = "SKIP"
            }
        },
        Target = new DynamicFlowMappingEndpointDto
        {
            Kind = "TABLE_COLUMN",
            DynamicFormTemplateId = formId,
            StepId = "parent",
            BlockId = "target_rows",
            ColumnKey = "col_1",
            DataType = "NUMBER"
        },
        Calculation = new DynamicFlowMappingCalculationDto
        {
            Kind = "DIRECT",
            Operation = "copy",
            ResultDataType = "NUMBER"
        }
    };

    var projection = DynamicFlowMappingEngine.Preview(
        target,
        new List<DynamicFlowMappingSourceReport> { source },
        new List<DynamicFlowMappingRuleDto> { rule },
        requestConflictPolicy: null,
        requestContributionPolicy: null,
        nowUtc: new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        targetStepId: "parent",
        targetStepCode: null);

    using var tables = JsonDocument.Parse(projection.TableValuesJson!);
    var block = tables.RootElement.GetProperty("blocks")[0];
    AssertEqual(7m, block.GetProperty("values1D")[1].GetDecimal(), "sparse source row should update the matching target slot");
    AssertEqual(2, block.GetProperty("rows")[0].GetProperty("rowOrder").GetInt32(), "created row projection should retain canonical row_2 order");
    AssertEqual("row_2", block.GetProperty("rows")[0].GetProperty("rowKey").GetString(), "created row projection should retain the mapping row key");
}

static void DynamicFlowMappingProvenanceExcludesSkippedNullValues()
{
    var formId = ObjectId(67);
    var target = new WorkAssignmentReport
    {
        Id = ObjectId(68),
        WorkId = ObjectId(69),
        WorkAssignmentId = ObjectId(70),
        DynamicFormTemplateId = formId,
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """{"blocks":[]}"""
    };
    var sourceReport = new WorkAssignmentReport
    {
        Id = ObjectId(71),
        WorkId = target.WorkId,
        WorkAssignmentId = ObjectId(72),
        DynamicFormTemplateId = formId,
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """
        {
          "blocks": [{
            "blockId": "grid",
            "tableMode": "FIXED_GRID",
            "values1D": [null, 5],
            "valueSlots": [
              { "index": 0, "rowKey": "row_1", "columnKey": "col_1" },
              { "index": 1, "rowKey": "row_2", "columnKey": "col_1" }
            ]
          }]
        }
        """
    };
    var source = new DynamicFlowMappingSourceReport(
        sourceReport,
        "child",
        "CHILD",
        sourceReport.FieldValuesJson,
        sourceReport.TableValuesJson);
    var rule = new DynamicFlowMappingRuleDto
    {
        MappingId = "sum-non-null",
        MappingVersion = 1,
        MappingKind = "FIELD",
        EvaluationGrain = "FLOW_INSTANCE",
        Inputs = new List<DynamicFlowMappingInputDto>
        {
            new()
            {
                InputKey = "amounts",
                Source = new DynamicFlowMappingEndpointDto
                {
                    Kind = "TABLE_COLUMN",
                    DynamicFormTemplateId = formId,
                    StepId = "child",
                    BlockId = "grid",
                    ColumnKey = "col_1",
                    DataType = "NUMBER"
                },
                DataType = "NUMBER",
                Cardinality = "MANY",
                NullPolicy = "SKIP"
            }
        },
        Target = new DynamicFlowMappingEndpointDto
        {
            Kind = "FIELD",
            DynamicFormTemplateId = formId,
            StepId = "parent",
            FieldKey = "total",
            DataType = "NUMBER"
        },
        Calculation = new DynamicFlowMappingCalculationDto
        {
            Kind = "DIRECT",
            Operation = "sum",
            ResultDataType = "NUMBER"
        }
    };

    var projection = DynamicFlowMappingEngine.Preview(
        target,
        new List<DynamicFlowMappingSourceReport> { source },
        new List<DynamicFlowMappingRuleDto> { rule },
        requestConflictPolicy: null,
        requestContributionPolicy: null,
        nowUtc: new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        targetStepId: "parent",
        targetStepCode: null);

    var change = projection.Changes.Single(item => item.MappingId == "sum-non-null");
    AssertEqual(1, change.Sources.Count, "null values removed by SKIP should not appear in provenance");
    AssertEqual("5", change.Sources[0].ValueJson, "provenance should retain the value that participated in the sum");
}

static void DynamicFlowMappingSynchronizesTopLevelTableValues()
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var synchronized = WorkAssignmentReportService.ResolveDynamicFlowMappingTopLevelValuesJson(
        "[1,2]",
        """
        {
          "blocks": [
            { "blockId": "secondary", "values1D": [7] },
            { "blockId": "top", "values1D": [9, "mapped"] }
          ]
        }
        """,
        "top",
        options);

    using var synchronizedDocument = JsonDocument.Parse(synchronized);
    AssertEqual(9, synchronizedDocument.RootElement[0].GetInt32(), "top-level values should come from the configured top block");
    AssertEqual("mapped", synchronizedDocument.RootElement[1].GetString(), "top-level text should stay aligned with the block payload");

    var unchanged = WorkAssignmentReportService.ResolveDynamicFlowMappingTopLevelValuesJson(
        "[1,2]",
        """{"blocks":[{"blockId":"secondary","values1D":[7]}]}""",
        "top",
        options);
    AssertEqual("[1,2]", unchanged, "missing top block should preserve the current top-level values");

    var initialized =
        WorkAssignmentReportService
            .NormalizeDynamicFlowMappingTopLevelValueSlots(
                "[]",
                expectedLength: 2,
                options);
    using var initializedDocument = JsonDocument.Parse(initialized);
    AssertEqual(
        2,
        initializedDocument.RootElement.GetArrayLength(),
        "fresh mapping targets must initialize every canonical top-level value slot");
    AssertEqual(
        JsonValueKind.Null,
        initializedDocument.RootElement[0].ValueKind,
        "fresh mapping target slots must remain semantic nulls");

    var existing =
        WorkAssignmentReportService
            .NormalizeDynamicFlowMappingTopLevelValueSlots(
                "[7]",
                expectedLength: 2,
                options);
    AssertEqual(
        "[7]",
        existing,
        "mapping slot initialization must never rewrite a non-empty payload");
}

static void DynamicFlowPolicyEvaluatorAppliesFieldAndTablePermissions()
{
    var evaluator = new DynamicFlowPolicyEvaluator();
    const string payload = """
    {
      "steps": [
        { "stepId": "draft", "stepCode": "DRAFT" }
      ],
      "fieldPolicies": [
        { "policyId": "issuer-secret", "stepId": "draft", "stepCode": "*", "actorRole": "ISSUER", "fieldKey": "secret", "read": true, "write": true, "required": true, "hidden": true },
        { "policyId": "review-score", "stepId": "*", "stepCode": "DRAFT", "actorRole": "REVIEWER", "fieldKey": "score", "read": true, "write": true, "required": true, "lockedAfterSubmit": true },
        { "policyId": "all-note", "stepId": "*", "stepCode": "DRAFT", "actorRole": "*", "fieldKey": "note", "read": true, "write": true }
      ],
      "tableColumnPolicies": [
        { "policyId": "review-amount", "stepId": "draft", "stepCode": "*", "actorRole": "REVIEWER", "blockId": "b1", "columnKey": "amount", "read": true, "write": true, "required": true, "lockedAfterSubmit": true },
        { "policyId": "issuer-secret-col", "stepId": "draft", "stepCode": "*", "actorRole": "ISSUER", "blockId": "b1", "columnKey": "secret_col", "read": true, "write": true, "required": true, "hidden": true }
      ]
    }
    """;

    var issuer = evaluator.Evaluate(payload, new DynamicFlowPolicyEvaluationContext
    {
        StepId = "draft",
        StepCode = "DRAFT",
        ActorRole = "ISSUER",
        IsAfterSubmit = false
    });

    var secret = issuer.Fields["secret"];
    AssertTrue(secret.Hidden, "hidden field should be marked hidden");
    AssertFalse(secret.Read, "hidden field should not be readable");
    AssertFalse(secret.Write, "hidden field should not be writable");
    AssertFalse(secret.Required, "hidden field should not remain required");

    var note = issuer.Fields["note"];
    AssertTrue(note.Read, "wildcard actor field should be readable");
    AssertTrue(note.Write, "wildcard actor field should be writable");

    var secretColumn = issuer.TableColumns["b1:secret_col"];
    AssertTrue(secretColumn.Hidden, "hidden table column should be marked hidden");
    AssertFalse(secretColumn.Read, "hidden table column should not be readable");
    AssertFalse(secretColumn.Write, "hidden table column should not be writable");
    AssertFalse(secretColumn.Required, "hidden table column should not remain required");

    var reviewer = evaluator.Evaluate(payload, new DynamicFlowPolicyEvaluationContext
    {
        StepId = "draft",
        StepCode = "DRAFT",
        ActorRole = "REVIEWER",
        IsAfterSubmit = true
    });

    var score = reviewer.Fields["score"];
    AssertTrue(score.Read, "locked field should remain readable");
    AssertFalse(score.Write, "locked field should not be writable after submit");
    AssertTrue(score.Required, "locked field should preserve required flag");
    AssertTrue(score.Locked, "lockedAfterSubmit field should lock after submit");
    AssertTrue(score.LockedAfterSubmit, "lockedAfterSubmit flag should be retained");

    var amount = reviewer.TableColumns["b1:amount"];
    AssertTrue(amount.Read, "locked table column should remain readable");
    AssertFalse(amount.Write, "locked table column should not be writable after submit");
    AssertTrue(amount.Required, "locked table column should preserve required flag");
    AssertTrue(amount.Locked, "lockedAfterSubmit table column should lock after submit");
    AssertTrue(amount.LockedAfterSubmit, "table lockedAfterSubmit flag should be retained");
}

static void DynamicFlowPolicyEvaluatorPrefersSpecificScopes()
{
    var evaluator = new DynamicFlowPolicyEvaluator();
    const string payload = """
    {
      "steps": [
        { "stepId": "draft", "stepCode": "DRAFT" }
      ],
      "fieldPolicies": [
        { "policyId": "all-denied", "stepId": "draft", "stepCode": "*", "actorRole": "*", "fieldKey": "amount", "write": false, "locked": true },
        { "policyId": "assignee-allowed", "stepId": "draft", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "amount", "read": true, "write": true, "locked": false }
      ]
    }
    """;

    var assignee = evaluator.Evaluate(payload, new DynamicFlowPolicyEvaluationContext
    {
        StepId = "draft",
        StepCode = "DRAFT",
        ActorRole = "ASSIGNEE"
    });
    AssertTrue(assignee.Fields["amount"].Write, "specific actor policy should override wildcard policy");
    AssertEqual("assignee-allowed", assignee.Fields["amount"].SourcePolicyId, "specific policy should be the recorded source");

    var reviewer = evaluator.Evaluate(payload, new DynamicFlowPolicyEvaluationContext
    {
        StepId = "draft",
        StepCode = "DRAFT",
        ActorRole = "REVIEWER"
    });
    AssertFalse(reviewer.Fields["amount"].Write, "wildcard policy should still apply to other roles");

    var missingSelector = evaluator.Evaluate(
        """
        {
          "steps": [{ "stepId": "draft", "stepCode": "DRAFT" }],
          "fieldPolicies": [
            { "policyId": "missing-step-code", "stepId": "draft", "actorRole": "ASSIGNEE", "fieldKey": "amount", "write": true }
          ]
        }
        """,
        new DynamicFlowPolicyEvaluationContext
        {
            StepId = "draft",
            StepCode = "DRAFT",
            ActorRole = "ASSIGNEE"
        });
    AssertTrue(
        missingSelector.DenyAllFields,
        "a missing policy selector must not act as an implicit wildcard");

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowTemplateService.NormalizePayloadJson(
            """
            {
              "steps": [{ "stepId": "draft", "stepCode": "DRAFT" }],
              "fieldPolicies": [
                { "policyId": "first", "stepId": "draft", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "amount", "write": true },
                { "policyId": "second", "stepId": "draft", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "amount", "write": false }
              ]
            }
            """,
            requireLockable: false));

    var unmatched = evaluator.Evaluate(
        """
        {
          "steps": [{ "stepId": "draft", "stepCode": "DRAFT" }],
          "fieldPolicies": [],
          "tableColumnPolicies": []
        }
        """,
        new DynamicFlowPolicyEvaluationContext
        {
            StepId = "draft",
            StepCode = "DRAFT",
            ActorRole = "ASSIGNEE"
        });
    AssertTrue(unmatched.DenyAllFields, "an unmatched field-policy scope must fail closed");
    AssertTrue(unmatched.DenyAllTableColumns, "an unmatched table-policy scope must fail closed");

    var deniedWrites = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        unmatched,
        """{"values":{"note":"old"}}""",
        """{"values":{"note":"new"}}""",
        """{"blocks":[{"blockId":"b1","rows":[{"cells":{"amount":1}}]}]}""",
        """{"blocks":[{"blockId":"b1","rows":[{"cells":{"amount":2}}]}]}""");
    AssertTrue(deniedWrites.Any(item => item.TargetKind == "FIELD"),
        "empty field policies must reject writes instead of allowing them implicitly");
    AssertTrue(deniedWrites.Any(item => item.TargetKind == "TABLE_COLUMN"),
        "empty table policies must reject writes instead of allowing them implicitly");

    var preservedDeniedWrite = DynamicFlowReportPermissionEnforcer.PreserveUnreadableValues(
        unmatched,
        """{"values":{"note":"old"}}""",
        """{"values":{"note":"new"}}""",
        null,
        null);
    AssertTrue(
        DynamicFlowReportPermissionEnforcer.FindWriteViolations(
            unmatched,
            """{"values":{"note":"old"}}""",
            preservedDeniedWrite.FieldValuesJson,
            null,
            preservedDeniedWrite.TableValuesJson).Any(),
        "fail-closed writes must be rejected rather than silently restored and accepted");

    var deniedRead = DynamicFlowReportPermissionEnforcer.ApplyReadRestrictions(
        unmatched,
        "[1]",
        """{"values":{"note":"secret"}}""",
        """{"blocks":[{"blockId":"b1","values1D":[1]}]}""",
        null);
    AssertEqual<string?>(null, deniedRead.FieldValuesJson, "empty field policies must redact all field values");
    AssertEqual("[]", deniedRead.Values1DJson, "empty table policies must redact top-level values");
    AssertEqual<string?>(null, deniedRead.TableValuesJson, "empty table policies must redact all table values");
}

static void DynamicFlowReportPermissionsRejectFieldWrites()
{
    var permissions = new DynamicFlowPolicyEvaluationResult();
    permissions.Fields["secret"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "secret",
        FieldId = "secret",
        Write = false,
        SourcePolicyId = "field-secret"
    };
    permissions.Fields["note"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "note",
        FieldId = "note",
        Read = true,
        Write = true,
        SourcePolicyId = "field-note"
    };

    var unchanged = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        """{"values":{"secret":"old","note":"same"}}""",
        """{"values":{"secret":"old","note":"changed"}}""",
        null,
        null);
    AssertEqual(0, unchanged.Count, "unchanged denied field should not be rejected");

    var changed = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        """{"values":{"secret":"old","note":"same"}}""",
        """{"values":{"secret":"new","note":"same"}}""",
        null,
        null);
    AssertEqual(1, changed.Count, "changed denied field should be rejected");
    AssertEqual("FIELD", changed[0].TargetKind, "field violation target kind should be field");
    AssertEqual("secret", changed[0].TargetKey, "field violation should identify the target key");
    AssertEqual("field-secret", changed[0].SourcePolicyId, "field violation should keep source policy id");
}

static void DynamicFlowReportPermissionsRejectTableColumnWrites()
{
    var permissions = new DynamicFlowPolicyEvaluationResult();
    permissions.TableColumns["b1:amount"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:amount",
        BlockId = "b1",
        ColumnKey = "amount",
        Write = false,
        SourcePolicyId = "column-amount"
    };
    permissions.TableColumns["b1:note"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:note",
        BlockId = "b1",
        ColumnKey = "note",
        Read = true,
        Write = true,
        SourcePolicyId = "column-note"
    };

    var unchanged = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        null,
        null,
        """{"blocks":[{"blockId":"b1","rows":[{"cells":{"amount":1,"note":"old"}}]}]}""",
        """{"blocks":[{"blockId":"b1","rows":[{"cells":{"amount":1,"note":"new"}}]}]}""");
    AssertEqual(0, unchanged.Count, "changed allowed table column should not be rejected");

    var changed = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        null,
        null,
        """{"blocks":[{"blockId":"b1","rows":[{"cells":{"amount":1,"note":"same"}}]}]}""",
        """{"blocks":[{"blockId":"b1","rows":[{"cells":{"amount":2,"note":"same"}}]}]}""");
    AssertEqual(1, changed.Count, "changed denied table column should be rejected");
    AssertEqual("TABLE_COLUMN", changed[0].TargetKind, "table violation target kind should be table column");
    AssertEqual("b1:amount", changed[0].TargetKey, "table violation should identify block and column");
    AssertEqual("column-amount", changed[0].SourcePolicyId, "table violation should keep source policy id");

    var slotOnlyAllowedChange = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        null,
        null,
        """{"blocks":[{"blockId":"b1","values1D":[1,"old"],"valueSlots":[{"index":0,"columnKey":"amount"},{"index":1,"columnKey":"note"}]}]}""",
        """{"blocks":[{"blockId":"b1","values1D":[1,"new"],"valueSlots":[{"index":0,"columnKey":"amount"},{"index":1,"columnKey":"note"}]}]}""");
    AssertEqual(0, slotOnlyAllowedChange.Count, "valueSlots should isolate changes in an allowed column");

    var slotDeniedChange = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        null,
        null,
        """{"blocks":[{"blockId":"b1","values1D":[1,"same"],"valueSlots":[{"index":0,"columnKey":"amount"},{"index":1,"columnKey":"note"}]}]}""",
        """{"blocks":[{"blockId":"b1","values1D":[2,"same"],"valueSlots":[{"index":0,"columnKey":"amount"},{"index":1,"columnKey":"note"}]}]}""");
    AssertEqual(1, slotDeniedChange.Count, "valueSlots should reject a change in the denied column");

    var directRowPropertyChange = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        null,
        null,
        """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"row_1","amount":1,"note":"same"}]}]}""",
        """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"row_1","amount":2,"note":"same"}]}]}""");
    AssertEqual(1, directRowPropertyChange.Count, "a denied column stored directly on an append row should not bypass write checks");
}

static void DynamicFlowReportPermissionsRedactUnreadableValues()
{
    var permissions = new DynamicFlowPolicyEvaluationResult();
    permissions.Fields["secret"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "secret",
        FieldId = "secret",
        Read = false,
        Write = false
    };
    permissions.Fields["public"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "public",
        FieldId = "public",
        Read = true,
        Write = true
    };
    permissions.TableColumns["b1:amount"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:amount",
        BlockId = "b1",
        ColumnKey = "amount",
        Read = false,
        Write = false
    };
    permissions.TableColumns["b1:note"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:note",
        BlockId = "b1",
        ColumnKey = "note",
        Read = true,
        Write = true
    };

    var dynamicExcelTemplateId = ObjectId(87);
    var readable = DynamicFlowReportPermissionEnforcer.ApplyReadRestrictions(
        permissions,
        """[11,"visible"]""",
        """{"values":{"secret":"classified","public":"visible"}}""",
        $$"""
        {
          "blocks": [
            {
              "blockId": "b1",
              "dynamicExcelTemplateId": "{{dynamicExcelTemplateId}}",
              "values1D": [11, "visible"],
              "valueSlots": [
                { "index": 0, "rowKey": "row_1", "columnKey": "amount" },
                { "index": 1, "rowKey": "row_1", "columnKey": "note" }
              ],
              "rows": [{ "cells": { "amount": 11, "note": "visible" } }],
              "cells": [
                { "rowKey": "row_1", "columnKey": "amount", "value": 11 },
                { "rowKey": "row_1", "columnKey": "note", "value": "visible" }
              ]
            }
          ]
        }
        """,
        dynamicExcelTemplateId);

    using var fields = JsonDocument.Parse(readable.FieldValuesJson!);
    var fieldValues = fields.RootElement.GetProperty("values");
    AssertFalse(fieldValues.TryGetProperty("secret", out _), "unreadable field value should be removed from the response");
    AssertEqual("visible", fieldValues.GetProperty("public").GetString(), "readable field value should remain");

    var topLevelValues = Values1DCompression.DeserializeObjects(readable.Values1DJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    AssertTrue(topLevelValues[0] is null, "unreadable top-level table value should be redacted");
    AssertEqual("visible", ((JsonElement)topLevelValues[1]!).GetString(), "readable top-level table value should remain");

    var expandedTables = Values1DCompression.ExpandTableValuesJson(
        readable.TableValuesJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var tables = JsonDocument.Parse(expandedTables!);
    var block = tables.RootElement.GetProperty("blocks")[0];
    AssertEqual(JsonValueKind.Null, block.GetProperty("values1D")[0].ValueKind, "unreadable slot should be null in the table payload");
    AssertFalse(block.GetProperty("rows")[0].GetProperty("cells").TryGetProperty("amount", out _), "unreadable row cell should be removed");
    AssertEqual(1, block.GetProperty("cells").GetArrayLength(), "unreadable matrix cell should be removed");
    AssertEqual("note", block.GetProperty("cells")[0].GetProperty("columnKey").GetString(), "readable matrix cell should remain");
}

static void DynamicFlowReportPermissionsPreserveUnreadableRoundTrips()
{
    var permissions = new DynamicFlowPolicyEvaluationResult();
    permissions.Fields["secret"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "secret",
        FieldId = "secret",
        Read = false,
        Write = false
    };
    permissions.Fields["public"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "public",
        FieldId = "public",
        Read = true,
        Write = true
    };
    permissions.TableColumns["B1:Amount"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "B1:Amount",
        BlockId = "B1",
        ColumnKey = "Amount",
        Read = false,
        Write = false
    };
    permissions.TableColumns["b1:note"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:note",
        BlockId = "b1",
        ColumnKey = "note",
        Read = true,
        Write = true
    };

    const string currentFields = """{"values":{"secret":"kept","public":"old"}}""";
    const string nextFields = """{"values":{"public":"new"}}""";
    const string currentTables = """
    {
      "blocks": [{
        "blockId": "b1",
        "values1D": [11, "old"],
        "valueSlots": [
          { "index": 0, "rowKey": "row_1", "columnKey": "amount" },
          { "index": 1, "rowKey": "row_1", "columnKey": "note" }
        ],
        "rows": [{ "rowKey": "row_1", "cells": { "amount": 11, "note": "old" } }],
        "cells": [
          { "rowKey": "row_1", "columnKey": "amount", "value": 11 },
          { "rowKey": "row_1", "columnKey": "note", "value": "old" }
        ]
      }]
    }
    """;
    const string nextTables = """
    {
      "blocks": [{
        "blockId": "b1",
        "values1D": [null, "new"],
        "valueSlots": [
          { "index": 0, "rowKey": "row_1", "columnKey": "amount" },
          { "index": 1, "rowKey": "row_1", "columnKey": "note" }
        ],
        "rows": [{ "rowKey": "row_1", "cells": { "note": "new" } }],
        "cells": [{ "rowKey": "row_1", "columnKey": "note", "value": "new" }]
      }]
    }
    """;

    var preserved = DynamicFlowReportPermissionEnforcer.PreserveUnreadableValues(
        permissions,
        currentFields,
        nextFields,
        currentTables,
        nextTables);
    var violations = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        currentFields,
        preserved.FieldValuesJson,
        currentTables,
        preserved.TableValuesJson);
    AssertEqual(0, violations.Count, "a redacted round trip should preserve denied values without a false violation");

    using var fields = JsonDocument.Parse(preserved.FieldValuesJson!);
    AssertEqual("kept", fields.RootElement.GetProperty("values").GetProperty("secret").GetString(), "unreadable field should be restored from the server value");
    AssertEqual("new", fields.RootElement.GetProperty("values").GetProperty("public").GetString(), "readable field edits should remain");

    var expandedTables = Values1DCompression.ExpandTableValuesJson(
        preserved.TableValuesJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var tables = JsonDocument.Parse(expandedTables!);
    var block = tables.RootElement.GetProperty("blocks")[0];
    AssertEqual(11, block.GetProperty("values1D")[0].GetInt32(), "unreadable value slot should be restored");
    AssertEqual("new", block.GetProperty("values1D")[1].GetString(), "readable value slot edits should remain");
    AssertEqual(11, block.GetProperty("rows")[0].GetProperty("cells").GetProperty("amount").GetInt32(), "row projection should restore the denied column");
    AssertEqual(2, block.GetProperty("cells").GetArrayLength(), "matrix projection should restore the denied cell");

    var malicious = DynamicFlowReportPermissionEnforcer.PreserveUnreadableValues(
        permissions,
        currentFields,
        """{"values":{"secret":"changed","public":"new"}}""",
        currentTables,
        """
        {
          "blocks": [{
            "blockId": "B1",
            "values1D": [99, "new"],
            "valueSlots": [
              { "index": 0, "rowKey": "row_1", "columnKey": "AMOUNT" },
              { "index": 1, "rowKey": "row_1", "columnKey": "note" }
            ]
          }]
        }
        """);
    var maliciousViolations = DynamicFlowReportPermissionEnforcer.FindWriteViolations(
        permissions,
        currentFields,
        malicious.FieldValuesJson,
        currentTables,
        malicious.TableValuesJson);
    AssertTrue(maliciousViolations.Any(item => item.TargetKind == "FIELD"), "a non-null hidden field override should still be rejected");
    AssertTrue(maliciousViolations.Any(item => item.TargetKind == "TABLE_COLUMN"), "a case-variant hidden column override should still be rejected");
}

static void DynamicFlowMappingPreviewRedactsUnreadableValues()
{
    var permissions = new DynamicFlowPolicyEvaluationResult();
    permissions.Fields["secret"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "secret",
        FieldKey = "secret",
        Read = false,
        Hidden = true
    };
    permissions.Fields["public"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "public",
        FieldKey = "public",
        Read = true,
        Write = true
    };
    permissions.TableColumns["b1:amount"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:amount",
        BlockId = "b1",
        ColumnKey = "amount",
        Read = false,
        Hidden = true
    };

    var preview = new DynamicFlowMappingPreviewResponse
    {
        FieldValuesJson = """{"values":{"secret":"classified","public":"visible"}}""",
        TableValuesJson = """{"blocks":[{"blockId":"b1","values1D":[11],"valueSlots":[{"index":0,"rowKey":"row_1","columnKey":"amount"}]}]}""",
        HasBlockingConflicts = true,
        SummarySourceJson = """{"kind":"DYNAMIC_FLOW_MAPPING","changes":[{"sources":[{"valueJson":"11"}]}]}""",
        Changes = new List<DynamicFlowMappingChangeDto>
        {
            new()
            {
                MappingId = "hidden-field",
                TargetKind = "FIELD",
                TargetKey = "secret",
                PreviousValueJson = "\"old\"",
                NextValueJson = "\"classified\"",
                Sources = new List<DynamicFlowMappingInputProvenanceDto> { new() { ValueJson = "\"classified\"" } }
            },
            new()
            {
                MappingId = "hidden-column",
                TargetKind = "TABLE",
                TargetKey = "B1:AMOUNT",
                PreviousValueJson = "10",
                NextValueJson = "11",
                Sources = new List<DynamicFlowMappingInputProvenanceDto> { new() { ValueJson = "11" } }
            },
            new()
            {
                MappingId = "visible-field",
                TargetKind = "FIELD",
                TargetKey = "public",
                NextValueJson = "\"visible\"",
                Sources = new List<DynamicFlowMappingInputProvenanceDto> { new() { ValueJson = "\"source\"" } }
            }
        }
    };

    var readable = DynamicFlowReportPermissionEnforcer.ApplyMappingPreviewReadRestrictions(permissions, preview);
    AssertEqual(1, readable.Changes.Count, "preview should omit changes targeting unreadable fields and columns");
    AssertEqual("visible-field", readable.Changes[0].MappingId, "readable mapping change should remain");
    AssertTrue(readable.Changes[0].Sources[0].ValueJson is null, "preview provenance should not expose raw source values");
    AssertTrue(readable.HasBlockingConflicts, "redaction must not clear the original blocking-conflict state");

    using var fields = JsonDocument.Parse(readable.FieldValuesJson!);
    AssertFalse(fields.RootElement.GetProperty("values").TryGetProperty("secret", out _), "preview should remove unreadable target field values");
    var expandedTables = Values1DCompression.ExpandTableValuesJson(
        readable.TableValuesJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var tables = JsonDocument.Parse(expandedTables!);
    AssertEqual(JsonValueKind.Null, tables.RootElement.GetProperty("blocks")[0].GetProperty("values1D")[0].ValueKind, "preview should redact unreadable target column values");
    using var summary = JsonDocument.Parse(readable.SummarySourceJson!);
    AssertFalse(summary.RootElement.GetProperty("changes")[0].GetProperty("sources")[0].TryGetProperty("valueJson", out _), "response provenance should remove raw source values");
}

static void DynamicFlowReportPermissionsEnforceRequiredValues()
{
    var permissions = new DynamicFlowPolicyEvaluationResult();
    permissions.Fields["confirmed"] = new DynamicFlowFieldPermissionDto
    {
        TargetKey = "confirmed",
        FieldId = "confirmed",
        Read = true,
        Required = true,
        SourcePolicyId = "required-confirmed"
    };
    permissions.TableColumns["b1:amount"] = new DynamicFlowTableColumnPermissionDto
    {
        TargetKey = "b1:amount",
        BlockId = "b1",
        ColumnKey = "amount",
        Read = true,
        Required = true,
        SourcePolicyId = "required-amount"
    };

    var missing = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
        permissions,
        """{"values":{"confirmed":null}}""",
        """{"blocks":[{"blockId":"b1","tableMode":"APPEND_ROWS","rows":[{"rowKey":"row_1","cells":{"amount":""}}]}]}""");
    AssertEqual(2, missing.Count, "required field and table column should both be enforced on submit");
    AssertTrue(missing.Any(item => item.Reason == "DYNAMIC_FLOW_FIELD_REQUIRED"), "missing required field should be identified");
    AssertTrue(missing.Any(item => item.Reason == "DYNAMIC_FLOW_TABLE_COLUMN_REQUIRED"), "missing required table column should be identified");

    var valid = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
        permissions,
        """{"values":{"confirmed":false}}""",
        """{"blocks":[{"blockId":"B1","tableMode":"APPEND_ROWS","rows":[{"rowKey":"row_1","amount":0}]}]}""");
    AssertEqual(0, valid.Count, "false and zero are valid required values, including direct row properties");

    var noAppendRows = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
        permissions,
        """{"values":{"confirmed":true}}""",
        """{"blocks":[{"blockId":"b1","tableMode":"APPEND_ROWS","rows":[]}]}""");
    AssertEqual(0, noAppendRows.Count, "a required append-row column should apply to existing rows without forcing a synthetic row");

    var activeSlotRowMissingAmount = DynamicFlowReportPermissionEnforcer.FindRequiredViolations(
        permissions,
        """{"values":{"confirmed":true}}""",
        """
        {
          "blocks": [{
            "blockId": "b1",
            "tableMode": "APPEND_ROWS",
            "values1D": ["entered", null],
            "valueSlots": [
              { "index": 0, "rowKey": "row_1", "columnKey": "description" },
              { "index": 1, "rowKey": "row_1", "columnKey": "amount" }
            ],
            "rows": []
          }]
        }
        """);
    AssertEqual(1, activeSlotRowMissingAmount.Count, "an active append row must enforce required columns even when its row projection is absent");
}

static void DynamicFlowMappingRejectsAmbiguousTableRowKeys()
{
    var sourceFormId = ObjectId(72);
    var targetFormId = ObjectId(73);
    WorkAssignmentReport SourceReport(string id, string assignmentId, decimal amount) => new()
    {
        Id = id,
        WorkId = ObjectId(74),
        WorkAssignmentId = assignmentId,
        WorkReportPeriodId = ObjectId(75),
        DynamicFormTemplateId = sourceFormId,
        AssigneeUserId = ObjectId(76),
        PeriodKey = "2026-07",
        PeriodInstanceKey = "2026-07",
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = $$"""
        {
          "blocks": [{
            "blockId": "source_rows",
            "tableMode": "APPEND_ROWS",
            "rows": [{ "rowOrder": 1, "amount": {{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }]
          }]
        }
        """
    };

    var first = SourceReport(ObjectId(77), ObjectId(78), 10m);
    var second = SourceReport(ObjectId(79), ObjectId(80), 20m);
    var target = new WorkAssignmentReport
    {
        Id = ObjectId(81),
        WorkId = first.WorkId,
        WorkAssignmentId = ObjectId(82),
        WorkReportPeriodId = ObjectId(83),
        DynamicFormTemplateId = targetFormId,
        AssigneeUserId = ObjectId(84),
        PeriodKey = "2026-07",
        PeriodInstanceKey = "2026-07",
        FieldValuesJson = """{"values":{}}""",
        TableValuesJson = """{"blocks":[{"blockId":"target_rows","tableMode":"APPEND_ROWS","rows":[]}]}"""
    };
    var rule = new DynamicFlowMappingRuleDto
    {
        MappingId = "ambiguous-row",
        MappingVersion = 1,
        EvaluationGrain = "TABLE_ROW",
        ErrorPolicy = "BLOCK_APPLY",
        ConflictPolicy = "OVERWRITE",
        Inputs = new List<DynamicFlowMappingInputDto>
        {
            new()
            {
                InputKey = "amount",
                Source = new DynamicFlowMappingEndpointDto
                {
                    Kind = "TABLE_COLUMN",
                    DynamicFormTemplateId = sourceFormId,
                    StepId = "source",
                    BlockId = "source_rows",
                    ColumnKey = "amount",
                    DataType = "NUMBER"
                },
                DataType = "NUMBER",
                Cardinality = "ONE",
                NullPolicy = "ERROR"
            }
        },
        Target = new DynamicFlowMappingEndpointDto
        {
            Kind = "TABLE_COLUMN",
            DynamicFormTemplateId = targetFormId,
            StepId = "target",
            BlockId = "target_rows",
            ColumnKey = "amount",
            DataType = "NUMBER"
        },
        Calculation = new DynamicFlowMappingCalculationDto
        {
            Kind = "DIRECT",
            Operation = "copy",
            ResultDataType = "NUMBER"
        }
    };

    var projection = DynamicFlowMappingEngine.Preview(
        target,
        new List<DynamicFlowMappingSourceReport>
        {
            new(first, "source", "SOURCE", first.FieldValuesJson, first.TableValuesJson),
            new(second, "source", "SOURCE", second.FieldValuesJson, second.TableValuesJson)
        },
        new List<DynamicFlowMappingRuleDto> { rule },
        requestConflictPolicy: null,
        requestContributionPolicy: null,
        nowUtc: new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        targetStepId: "target",
        targetStepCode: "TARGET");

    AssertTrue(projection.HasBlockingConflicts, "two source reports with the same relative row key should fail closed");
    AssertEqual(
        "DYNAMIC_FLOW_MAPPING_TABLE_ROW_KEY_AMBIGUOUS",
        projection.Changes.Single().Reason,
        "ambiguous relative row keys should require a future explicit join contract");
}

static void DynamicFlowMappingProvenanceCannotBeForgedByReportRequests()
{
    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_REPORT_SUMMARY_SOURCE_JSON_INVALID,
        () => WorkAssignmentReportService.EnsureDynamicFlowMappingSummaryOverrideAllowed(
            currentSummarySourceJson: null,
            requestedSummarySourceJson: """{"kind":"dynamic_flow_mapping","changes":[]}""",
            reportId: ObjectId(70),
            actorUserId: ObjectId(71)));

    WorkAssignmentReportService.EnsureDynamicFlowMappingSummaryOverrideAllowed(
        currentSummarySourceJson: """{"kind":"DYNAMIC_FLOW_MAPPING"}""",
        requestedSummarySourceJson: """{"kind":"DYNAMIC_FLOW_MAPPING","changes":[]}""",
        reportId: ObjectId(70),
        actorUserId: ObjectId(71));
}

static void DynamicFlowReportActorRolesFollowAssignmentRelationships()
{
    var assigneeId = ObjectId(84);
    var issuerId = ObjectId(85);
    var coordinatorId = ObjectId(86);
    var assignment = new WorkAssignment
    {
        Id = ObjectId(83),
        WorkId = ObjectId(82),
        CreatedByUserId = issuerId,
        FlowRole = "REVIEWER",
        Assignees = new List<UserRef> { new() { UserId = assigneeId } },
        LeaderWatcherUserIds = new List<string> { coordinatorId }
    };
    var report = new WorkAssignmentReport
    {
        Id = ObjectId(81),
        WorkId = assignment.WorkId,
        WorkAssignmentId = assignment.Id,
        AssigneeUserId = assigneeId
    };

    AssertEqual(
        "REVIEWER",
        WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(assignment, report, assigneeId),
        "branch assignee should use the role captured on the branch");
    AssertEqual(
        "ISSUER",
        WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(assignment, report, issuerId),
        "assignment creator should resolve as issuer");
    AssertEqual(
        "COORDINATOR",
        WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(assignment, report, coordinatorId),
        "leader watcher should resolve as coordinator");
    AssertTrue(
        WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(assignment, report, ObjectId(80)) is null,
        "unrelated readers should require the projected review or ancestor relationship");

    assignment.FlowRole = "UNKNOWN";
    AssertEqual(
        "ASSIGNEE",
        WorkAssignmentReportService.ResolveDirectDynamicFlowActorRole(assignment, report, assigneeId),
        "unknown legacy branch role should fall back to assignee");
}

static void DynamicFlowReportLifecycleKeepsPostSubmitLocks()
{
    var freshDraft = new WorkAssignmentReport { Status = WorkAssignmentReportStatus.Draft };
    AssertFalse(
        WorkAssignmentReportService.IsDynamicFlowReportAfterSubmit(freshDraft),
        "a never-submitted draft should remain editable by lockedAfterSubmit policies");

    var returnedDraft = new WorkAssignmentReport
    {
        Status = WorkAssignmentReportStatus.Draft,
        SubmittedAtUtc = DateTime.UtcNow.AddDays(-1),
        ReturnedAtUtc = DateTime.UtcNow
    };
    AssertTrue(
        WorkAssignmentReportService.IsDynamicFlowReportAfterSubmit(returnedDraft),
        "a returned draft should remain locked after its first submission");

    var withdrawnDraft = new WorkAssignmentReport
    {
        Status = WorkAssignmentReportStatus.Draft,
        SubmittedByUserId = ObjectId(79)
    };
    AssertTrue(
        WorkAssignmentReportService.IsDynamicFlowReportAfterSubmit(withdrawnDraft),
        "a withdrawn draft with submission history should remain locked");

    var submitted = new WorkAssignmentReport { Status = WorkAssignmentReportStatus.Submitted };
    AssertTrue(
        WorkAssignmentReportService.IsDynamicFlowReportAfterSubmit(submitted),
        "a submitted report should be treated as post-submit even if legacy timestamps are missing");
}

static void DynamicFlowRuntimePlannerCreatesBranchPerTargetUnit()
{
    var rootFormTemplateId = ObjectId(92);
    var childFormTemplateId = ObjectId(89);
    var template = new DynamicFlowTemplate
    {
        Id = ObjectId(91),
        Code = "FLOW_RUNTIME",
        Name = "Flow runtime",
        DynamicFormTemplateId = rootFormTemplateId,
        Status = DynamicFlowTemplateStatuses.Active
    };

    var version = new DynamicFlowTemplateVersion
    {
        Id = ObjectId(93),
        TemplateId = template.Id,
        DynamicFormTemplateId = template.DynamicFormTemplateId,
        VersionNo = 3,
        Status = DynamicFlowTemplateVersionStatuses.Locked,
        PayloadJson = """
        {
          "formNodes": [
            { "formNodeId": "root", "role": "ROOT", "dynamicFormTemplateId": "100000000000000000000092" },
            { "formNodeId": "child-review", "role": "CHILD", "dynamicFormTemplateId": "100000000000000000000089" }
          ],
          "steps": [
            { "stepId": "draft", "stepCode": "DRAFT", "stepOrder": 1, "formNodeId": "root" },
            { "stepId": "review", "stepCode": "REVIEW", "stepOrder": 2, "formNodeId": "child-review" },
            { "stepId": "final", "stepCode": "FINAL", "stepOrder": 3, "formNodeId": "root" }
          ],
          "transitions": [
            { "fromStepId": "draft", "toStepId": "review" },
            { "fromStepCode": "REVIEW", "toStepId": "final" }
          ],
          "actorPolicies": [
            { "stepId": "*", "stepCode": "REVIEW", "actorRole": "ASSIGNEE", "allowSubFlow": true }
          ]
        }
        """
    };

    var parent = new WorkAssignment
    {
        Id = ObjectId(94),
        WorkId = ObjectId(95),
        FlowInstanceId = ObjectId(96),
        FlowBranchId = ObjectId(97),
        FlowAttemptNo = 2,
        FlowTemplateId = template.Id,
        FlowTemplateVersionNo = version.VersionNo,
        FlowStepId = "draft",
        FlowStepCode = "DRAFT",
        AllowSubFlow = true,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };

    var targetUnitA = ObjectId(98);
    var targetUnitB = ObjectId(99);
    var request = new CreateDynamicFlowInstanceRequest
    {
        FlowTemplateVersionId = version.Id,
        ParentAssignmentId = parent.Id,
        StepCode = "REVIEW",
        FlowRole = "FINALIZER",
        TargetUnitIds = new List<string> { targetUnitA, targetUnitB, targetUnitA }
    };

    var plan = DynamicFlowRuntimePlanner.CreateLaunchPlan(
        template,
        version,
        request,
        parent,
        actorUnitId: ObjectId(90));

    AssertEqual(template.Id, plan.FlowTemplateId, "flow template id should come from locked version");
    AssertEqual(3, plan.FlowTemplateVersionNo, "flow template version should be captured");
    AssertEqual(childFormTemplateId, plan.DynamicFormTemplateId, "selected step dynamic form template should be captured");
    AssertEqual(parent.FlowInstanceId, plan.FlowInstanceId, "child launch should reuse parent flow instance");
    AssertEqual("review", plan.Step.StepId, "requested step should resolve by step code");
    AssertEqual("REVIEW", plan.Step.StepCode, "requested step code should be captured");
    AssertEqual(2, plan.Step.StepOrder, "step order should come from payload");
    AssertSequenceEqual(
        new List<string> { targetUnitA, targetUnitB },
        plan.Branches.Select(x => x.TargetUnitId).ToList(),
        "target unit ids should be deduplicated in order");

    foreach (var branch in plan.Branches)
    {
        AssertEqual(parent.FlowBranchId, branch.ParentFlowBranchId, "branch should link back to parent flow branch");
        AssertEqual(2, branch.FlowAttemptNo, "branch should inherit parent flow attempt");
        AssertEqual(DynamicFlowRuntimePlanner.AssignmentFlowRole, branch.FlowRole,
            "branch role should be derived by the server instead of accepting request elevation");
        AssertEqual(DynamicFlowRuntimePlanner.EffectiveStatus, branch.FlowEffectiveStatus, "branch status should start effective");
        AssertTrue(branch.AllowSubFlow, "actor policy should allow sub-flow on selected step");
        AssertFalse(branch.IsFlowFinalNode, "review step should not be final while it has an outgoing transition");
        AssertTrue(MongoDB.Bson.ObjectId.TryParse(branch.FlowBranchId, out _), "branch id should be a generated ObjectId");
    }

    var directChild = new WorkAssignment
    {
        Id = ObjectId(100),
        WorkId = parent.WorkId,
        ParentAssignmentId = parent.Id,
        IsActive = true,
        FlowInstanceId = parent.FlowInstanceId,
        FlowTemplateId = parent.FlowTemplateId,
        FlowTemplateVersionNo = parent.FlowTemplateVersionNo,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };
    AssertTrue(
        WorkAssignmentReportService.DynamicFlowSourceContextMatches(parent, directChild),
        "mapping source should accept an effective direct child");

    directChild.FlowTemplateVersionNo = parent.FlowTemplateVersionNo + 1;
    AssertFalse(
        WorkAssignmentReportService.DynamicFlowSourceContextMatches(parent, directChild),
        "mapping source should reject a direct child from another locked flow version");
    directChild.FlowTemplateVersionNo = parent.FlowTemplateVersionNo;

    directChild.FlowInstanceId = ObjectId(79);
    AssertFalse(
        WorkAssignmentReportService.DynamicFlowSourceContextMatches(parent, directChild),
        "mapping source should reject a direct child from another flow instance");
    directChild.FlowInstanceId = parent.FlowInstanceId;

    directChild.ParentAssignmentId = ObjectId(80);
    AssertFalse(
        WorkAssignmentReportService.DynamicFlowSourceContextMatches(parent, directChild),
        "mapping source should reject a sibling or unrelated branch");

    directChild.ParentAssignmentId = parent.Id;
    directChild.FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Invalidated;
    AssertFalse(
        WorkAssignmentReportService.DynamicFlowSourceContextMatches(parent, directChild),
        "mapping source should reject an invalidated child branch");

    parent.FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Invalidated;
    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowRuntimePlanner.CreateLaunchPlan(
            template,
            version,
            request,
            parent,
            actorUnitId: ObjectId(90)));
}

static void DynamicFlowRuntimePlannerRejectsInvalidLaunchInputs()
{
    var template = new DynamicFlowTemplate
    {
        Id = ObjectId(101),
        Code = "FLOW_RUNTIME_BLOCKED",
        Name = "Flow runtime blocked",
        DynamicFormTemplateId = ObjectId(82),
        Status = DynamicFlowTemplateStatuses.Active
    };

    var version = new DynamicFlowTemplateVersion
    {
        Id = ObjectId(103),
        TemplateId = template.Id,
        DynamicFormTemplateId = template.DynamicFormTemplateId,
        VersionNo = 1,
        Status = DynamicFlowTemplateVersionStatuses.Draft,
        PayloadJson = """
        {
          "steps": [
            { "stepId": "draft", "stepCode": "DRAFT" }
          ]
        }
        """
    };

    AssertThrows(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => DynamicFlowRuntimePlanner.CreateLaunchPlan(
            template,
            version,
            new CreateDynamicFlowInstanceRequest
            {
                FlowTemplateVersionId = version.Id,
                TargetUnitIds = new List<string> { ObjectId(104) }
            },
            parent: null,
            actorUnitId: ObjectId(105)));

    version.Status = DynamicFlowTemplateVersionStatuses.Locked;

    AssertThrows(
        AppErrorCode.COMMON_ARGUMENT_REQUIRED,
        () => DynamicFlowRuntimePlanner.CreateLaunchPlan(
            template,
            version,
            new CreateDynamicFlowInstanceRequest
            {
                FlowTemplateVersionId = version.Id,
                TargetUnitIds = new List<string>()
            },
            parent: null,
            actorUnitId: ObjectId(105)));
}

static void DynamicFlowBranchMutationPlannerInvalidatesDownstreamBranches()
{
    var workId = ObjectId(131);
    var flowInstanceId = ObjectId(132);
    var eventId = ObjectId(133);
    var rootId = ObjectId(134);
    var childId = ObjectId(135);
    var grandchildId = ObjectId(136);
    var siblingId = ObjectId(137);

    var root = new WorkAssignment
    {
        Id = rootId,
        WorkId = workId,
        FlowInstanceId = flowInstanceId,
        Path = $"/{rootId}",
        FlowAttemptNo = 2,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };
    var child = new WorkAssignment
    {
        Id = childId,
        WorkId = workId,
        ParentAssignmentId = root.Id,
        FlowInstanceId = flowInstanceId,
        Path = $"{root.Path}/{childId}",
        FlowAttemptNo = 2,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Invalidated
    };
    var grandchild = new WorkAssignment
    {
        Id = grandchildId,
        WorkId = workId,
        ParentAssignmentId = child.Id,
        FlowInstanceId = flowInstanceId,
        Path = $"{child.Path}/{grandchildId}",
        FlowAttemptNo = 2,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };
    var sibling = new WorkAssignment
    {
        Id = siblingId,
        WorkId = workId,
        ParentAssignmentId = root.Id,
        FlowInstanceId = flowInstanceId,
        Path = $"{root.Path}/{siblingId}",
        FlowAttemptNo = 2,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };
    var assignments = new List<WorkAssignment> { root, child, grandchild, sibling };

    var rollbackPlan = DynamicFlowBranchMutationPlanner.Create(
        assignments,
        child,
        DynamicFlowEventActions.Rollback,
        eventId,
        includeDescendants: true);

    AssertEqual(child.Id, rollbackPlan.TargetUpdate.AssignmentId, "rollback target should stay on selected branch");
    AssertEqual(DynamicFlowEffectiveStatuses.Effective, rollbackPlan.TargetUpdate.NextStatus, "rollback target should become effective");
    AssertEqual(3, rollbackPlan.TargetUpdate.NextAttemptNo, "rollback should open a new target attempt");
    AssertEqual<string?>(null, rollbackPlan.TargetUpdate.InvalidatedByFlowEventId, "rollback target should clear invalidation marker");
    AssertEqual(1, rollbackPlan.DownstreamUpdates.Count, "rollback should affect only downstream descendants");
    AssertEqual(grandchild.Id, rollbackPlan.DownstreamUpdates[0].AssignmentId, "rollback downstream should include grandchild");
    AssertEqual(DynamicFlowEffectiveStatuses.Invalidated, rollbackPlan.DownstreamUpdates[0].NextStatus, "rollback downstream should be invalidated");
    AssertEqual(eventId, rollbackPlan.DownstreamUpdates[0].InvalidatedByFlowEventId, "rollback downstream should reference flow event");
    AssertSequenceEqual(
        new List<string> { child.Id, grandchild.Id },
        rollbackPlan.AffectedAssignmentIds,
        "rollback affected ids should exclude siblings");
    AssertFalse(
        rollbackPlan.AffectedAssignmentIds.Contains(sibling.Id),
        "rollback should not affect sibling branches");
    AssertTrue(
        DynamicFlowBranchMutationPlanner.IsAncestorOrSelf(assignments, child, grandchild),
        "child should be an ancestor of grandchild");
    AssertFalse(
        DynamicFlowBranchMutationPlanner.IsAncestorOrSelf(assignments, sibling, grandchild),
        "sibling should not be an ancestor of grandchild");

    var terminatePlan = DynamicFlowBranchMutationPlanner.Create(
        assignments,
        child,
        DynamicFlowEventActions.Terminated,
        eventId,
        includeDescendants: true);

    AssertEqual(DynamicFlowEffectiveStatuses.Terminated, terminatePlan.TargetUpdate.NextStatus, "terminate target should be terminated");
    AssertEqual(2, terminatePlan.TargetUpdate.NextAttemptNo, "terminate should not create a new attempt");
    AssertEqual(eventId, terminatePlan.TargetUpdate.InvalidatedByFlowEventId, "terminate target should reference flow event");
    AssertEqual(DynamicFlowEffectiveStatuses.Terminated, terminatePlan.DownstreamUpdates[0].NextStatus, "terminate downstream should be terminated");
}

static void DynamicFlowBranchVisibilityInheritsDownstreamUnits()
{
    var issuerUnitId = ObjectId(111);
    var targetUnitId = ObjectId(112);
    var downstreamUnitId = ObjectId(113);

    var branch = new WorkAssignment
    {
        Id = ObjectId(114),
        WorkId = ObjectId(115),
        FlowInstanceId = ObjectId(116),
        FlowBranchId = ObjectId(117),
        IssuedByUnitId = issuerUnitId,
        TargetUnitIds = new List<string> { targetUnitId },
        Assignees = new List<UserRef>
        {
            new() { UserId = UserId(11), UnitId = targetUnitId }
        }
    };

    var branchVisibleUnits = DynamicFlowBranchVisibility.BuildVisibleUnitIds(branch);
    AssertSequenceEqual(
        new List<string> { issuerUnitId, targetUnitId },
        branchVisibleUnits,
        "issuer and target units should see the assigned branch");

    var downstream = new WorkAssignment
    {
        Id = ObjectId(118),
        WorkId = branch.WorkId,
        ParentAssignmentId = branch.Id,
        FlowInstanceId = branch.FlowInstanceId,
        FlowBranchId = ObjectId(119),
        ParentFlowBranchId = branch.FlowBranchId,
        IssuedByUnitId = targetUnitId,
        TargetUnitIds = new List<string> { downstreamUnitId },
        Assignees = new List<UserRef>
        {
            new() { UserId = UserId(12), UnitId = downstreamUnitId }
        }
    };

    var downstreamVisibleUnits = DynamicFlowBranchVisibility.BuildVisibleUnitIds(downstream, branchVisibleUnits);
    AssertSequenceEqual(
        new List<string> { issuerUnitId, targetUnitId, downstreamUnitId },
        downstreamVisibleUnits,
        "downstream branch should inherit parent visible units and add its target");
    AssertTrue(
        DynamicFlowBranchVisibility.CanUnitRead(downstream, targetUnitId, downstreamVisibleUnits),
        "target unit should see downstream branches it creates");
}

static void DynamicFlowBranchVisibilityHidesSiblingBranches()
{
    var issuerUnitId = ObjectId(121);
    var targetUnitA = ObjectId(122);
    var targetUnitB = ObjectId(123);

    var branchA = new AssignmentListDocRole
    {
        AssignmentId = ObjectId(124),
        FlowInstanceId = ObjectId(125),
        FlowBranchId = ObjectId(126),
        VisibleUnitIds = new List<string> { issuerUnitId, targetUnitA }
    };
    var branchB = new AssignmentListDocRole
    {
        AssignmentId = ObjectId(127),
        FlowInstanceId = branchA.FlowInstanceId,
        FlowBranchId = ObjectId(128),
        VisibleUnitIds = new List<string> { issuerUnitId, targetUnitB }
    };

    AssertTrue(
        DynamicFlowBranchVisibility.CanUnitRead(branchA, targetUnitA),
        "target unit A should see its own branch");
    AssertFalse(
        DynamicFlowBranchVisibility.CanUnitRead(branchB, targetUnitA),
        "target unit A should not see sibling branch B");
    AssertTrue(
        DynamicFlowBranchVisibility.CanUnitRead(branchB, issuerUnitId),
        "issuer unit should see branches it created");
}

static void StatisticProjectionContextCapturesDynamicFlowMetadata()
{
    var report = ReadyPayloadReport();
    report.AssigneeUserId = UserId(21);
    var period = new WorkReportPeriod
    {
        Id = ObjectId(41),
        AssigneeUserId = report.AssigneeUserId,
        AssigneeUnitId = ObjectId(42)
    };
    var assignment = new WorkAssignment
    {
        Id = report.WorkAssignmentId,
        WorkId = report.WorkId,
        IsActive = true,
        Assignees = new List<UserRef>
        {
            new() { UserId = report.AssigneeUserId, UnitId = ObjectId(43) }
        },
        FlowTemplateId = ObjectId(44),
        FlowTemplateVersionNo = 5,
        FlowInstanceId = ObjectId(45),
        FlowStepId = "review",
        FlowStepCode = "REVIEW",
        FlowStepOrder = 2,
        FlowBranchId = ObjectId(46),
        ParentFlowBranchId = ObjectId(47),
        FlowAttemptNo = 3,
        FlowRole = "ASSIGNEE",
        IsFlowFinalNode = true,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
        InvalidatedByFlowEventId = ObjectId(48)
    };

    var context = WorkReportStatisticProjectionContextBuilder.From(
        report,
        assignment,
        period,
        MatchingPayloadSnapshot(report, isExternalPayload: true, payloadHashVerified: true));

    AssertEqual(period.AssigneeUnitId, context.AssigneeUnitId, "period unit should override assignment assignee unit");
    AssertEqual(assignment.FlowTemplateId, context.FlowTemplateId, "flow template should be projected");
    AssertEqual(assignment.FlowTemplateVersionNo, context.FlowTemplateVersionNo, "flow template version should be projected");
    AssertEqual(assignment.FlowInstanceId, context.FlowInstanceId, "flow instance should be projected");
    AssertEqual(assignment.FlowStepId, context.FlowStepId, "flow step id should be projected");
    AssertEqual(assignment.FlowStepCode, context.FlowStepCode, "flow step code should be projected");
    AssertEqual(assignment.FlowStepOrder, context.FlowStepOrder, "flow step order should be projected");
    AssertEqual(assignment.FlowBranchId, context.FlowBranchId, "flow branch should be projected");
    AssertEqual(assignment.ParentFlowBranchId, context.ParentFlowBranchId, "parent flow branch should be projected");
    AssertEqual(assignment.FlowAttemptNo, context.FlowAttemptNo, "flow attempt should be projected");
    AssertEqual(assignment.FlowRole, context.FlowRole, "flow role should be projected");
    AssertEqual(assignment.IsFlowFinalNode, context.IsFlowFinalNode, "flow final-node flag should be projected");
    AssertEqual(assignment.FlowEffectiveStatus, context.FlowEffectiveStatus, "flow status should be projected");
    AssertEqual(assignment.InvalidatedByFlowEventId, context.InvalidatedByFlowEventId, "invalidation event should be projected");
}

static void StatisticConceptMapResolvesDynamicFlowMappingTargets()
{
    var report = ReadyPayloadReport();
    report.SummarySourceJson = """
    {
      "kind": "DYNAMIC_FLOW_MAPPING",
      "mappingRules": [
        {
          "targetFieldId": "f_total",
          "targetFieldKey": "total",
          "conceptCode": "total"
        },
        {
          "targetBlockId": "b1",
          "targetColumnKey": "amount",
          "conceptCode": "amount"
        }
      ],
      "changes": [
        {
          "targetKind": "FIELD",
          "targetKey": "f_note",
          "conceptCode": "note"
        },
        {
          "targetKind": "TABLE",
          "targetKey": "b1:count",
          "conceptCode": "count"
        }
      ]
    }
    """;

    var map = WorkReportStatisticConceptMapBuilder.From(report);

    AssertEqual("TOTAL", map.ResolveField("f_total", "total"), "field concept should resolve from mapping rule");
    AssertEqual("NOTE", map.ResolveField("f_note", null), "field concept should resolve from mapping change");
    AssertEqual("AMOUNT", map.ResolveTable("b1", "amount", null), "table concept should resolve by block and column");
    AssertEqual("COUNT", map.ResolveTable("b1", "count", null), "table concept should resolve from target key");
}

static void StatisticRebuildJobDedupeKeyIncludesBoundedFlowScope()
{
    var scope = new StatisticRebuildScopeRequest
    {
        DynamicFormTemplateId = ObjectId(55),
        WorkId = ObjectId(56),
        WorkAssignmentId = ObjectId(57),
        FlowInstanceId = ObjectId(58),
        FlowEffectiveStatus = "effective",
        PeriodInstanceKey = "2026-06"
    };

    var normalized = InvokePrivateStatic<StatisticRebuildScopeRequest>(
        typeof(WorkReportStatisticRebuildJobService),
        "NormalizeScope",
        scope);
    var dedupeKey = InvokePrivateStatic<string>(
        typeof(WorkReportStatisticRebuildJobService),
        "BuildDedupeKey",
        tdtd_be.Models.Statistics.WorkReportStatisticRebuildJobScopeKinds.Bounded,
        normalized);

    AssertEqual("EFFECTIVE", normalized.FlowEffectiveStatus, "flow status should normalize for bounded job scope");
    AssertTrue(dedupeKey.Contains($"dynamic-form-statistic-rebuild:{scope.DynamicFormTemplateId}"), "dedupe key should include template");
    AssertTrue(dedupeKey.Contains($"work={scope.WorkId}"), "dedupe key should include work scope");
    AssertTrue(dedupeKey.Contains($"assignment={scope.WorkAssignmentId}"), "dedupe key should include assignment scope");
    AssertTrue(dedupeKey.Contains($"flow={scope.FlowInstanceId}"), "dedupe key should include flow scope");
    AssertTrue(dedupeKey.Contains("flowStatus=EFFECTIVE"), "dedupe key should include normalized flow status");
    AssertTrue(dedupeKey.Contains("period=2026-06"), "dedupe key should include period scope");
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

static void BlocksPeriodicChildDueAfterParentWithinWork()
{
    var work = WorkWithDateRange(
        new DateTime(2026, 1, 1),
        new DateTime(2026, 12, 31));
    var parentDue = new DateTime(2026, 5, 31);
    var req = new SaveWorkAssignmentRequest
    {
        ParentAssignmentId = ObjectId(10),
        DynamicFormTemplateId = ObjectId(11),
        AssignmentType = WorkAssignmentTypes.PeriodicReport,
        AggregationType = WorkAggregationTypes.Matrix,
        AssigneeUserIds = new List<string> { UserId(2) },
        StartDate = new DateTime(2026, 5, 1),
        DueDate = new DateTime(2026, 6, 30),
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
    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_PERIODIC_DUE_AFTER_PARENT,
        () => WorkAssignmentScheduleHelper.ValidateRequest(normalized, work, parentDue));

    req.DueDate = parentDue;
    normalized = WorkAssignmentScheduleHelper.NormalizeRequest(req);
    WorkAssignmentScheduleHelper.ValidateRequest(normalized, work, parentDue);

    req.DueDate = null;
    var inherited = WorkAssignmentScheduleHelper.ApplyEffectiveDateDefaults(
        WorkAssignmentScheduleHelper.NormalizeRequest(req),
        work,
        new WorkAssignment { Id = req.ParentAssignmentId, DueDate = parentDue },
        new DateTime(2026, 5, 1),
        inheritedParentDueDate: parentDue);
    WorkAssignmentScheduleHelper.ValidateRequest(inherited, work, parentDue);
    AssertEqual(parentDue, inherited.DueDate, "blank child due should inherit parent due");
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

static void HistoricalMutationPolicyAllowsRecentApprovedChanges()
{
    var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
    var report = ApprovedHistoricalMutationReport(new DateTime(2026, 6, 1));
    var actor = Me("reviewer", "NORMAL_USER", new List<string>());

    var decision = WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, actor, now);

    AssertTrue(decision.IsAllowed, "recent approved historical mutation should be allowed for normal review flow");
    AssertEqual("RECENT_WINDOW", decision.Required, "recent mutation should not require elevated role");
    WorkAssignmentHistoricalMutationPolicy.EnsureApprovedMutationAllowed(report, null, actor, "TEST_RECENT", now);
}

static void HistoricalMutationPolicyRequiresManagementForExtendedWindow()
{
    var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
    var report = ApprovedHistoricalMutationReport(new DateTime(2026, 4, 20));
    var normalActor = Me("reviewer", "NORMAL_USER", new List<string>());
    var unitManager = Me(
        username: "mu_pv01",
        accountKind: "UNIT_MANAGER",
        roles: new List<string> { Roles.ManagerUnit(ObjectId(9)) });

    var normalDecision = WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, normalActor, now);
    var managerDecision = WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, unitManager, now);

    AssertFalse(normalDecision.IsAllowed, "older than one month should not be allowed for normal reviewer");
    AssertEqual("MANAGEMENT_OR_ADMIN", normalDecision.Required, "extended window should require management role");
    AssertTrue(managerDecision.IsAllowed, "generated management account should be allowed up to three months");
    AssertThrows(
        AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_MUTATION_WINDOW_FORBIDDEN,
        () => WorkAssignmentHistoricalMutationPolicy.EnsureApprovedMutationAllowed(report, null, normalActor, "TEST_EXTENDED", now));
}

static void HistoricalMutationPolicyRequiresAdminBeyondExtendedWindow()
{
    var now = new DateTime(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);
    var report = ApprovedHistoricalMutationReport(new DateTime(2026, 2, 20));
    var unitManager = Me(
        username: "mu_pv01",
        accountKind: "UNIT_MANAGER",
        roles: new List<string> { Roles.ManagerUnit(ObjectId(9)) });
    var admin = Me("admin", "NORMAL_USER", new List<string> { Roles.ADMIN });

    var managerDecision = WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, unitManager, now);
    var adminDecision = WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, admin, now);

    AssertFalse(managerDecision.IsAllowed, "older than three months should not be allowed for management account");
    AssertEqual("ADMIN_OR_SYSTEM_ADMIN", managerDecision.Required, "old mutation should require admin");
    AssertTrue(adminDecision.IsAllowed, "admin should be allowed beyond the extended historical mutation window");
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

static void DynamicFormSectionSnapshotsAreDeterministicAndOrdered()
{
    var first = DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate());
    var equivalent = DynamicFormSectionSnapshotBuilder.Build(
        TestDynamicFormSectionSnapshotTemplate(
            sectionsJson: """
            [
              { "tagCodes": ["beta"], "order": 2, "title": "Beta", "id": "s_beta" },
              { "description": "Alpha section", "id": "s_alpha", "order": 1, "title": "Alpha" }
            ]
            """,
            fieldsJson: """
            [
              { "name": "Beta", "type": "number", "id": "f_beta", "sectionId": "s_beta" },
              { "type": "shortText", "sectionId": "s_alpha", "name": "Alpha one", "id": "f_alpha" },
              { "sectionId": "s_alpha", "id": "f_alpha_2", "name": "Alpha two", "type": "boolean" }
            ]
            """,
            blocksJson: """
            [
              { "tableMode": "FIXED_GRID", "sectionId": "s_beta", "blockId": "b_beta" },
              { "sectionId": "s_alpha", "tableMode": "FIXED_GRID", "blockId": "b_alpha" }
            ]
            """));

    AssertSequenceEqual(
        new[] { "s_alpha", "s_beta" },
        first.Sections.Select(x => x.SectionId).ToArray(),
        "section snapshots should be ordered by section order");
    var alpha = first.GetRequiredSection(" s_alpha ");
    AssertSequenceEqual(
        new[] { "f_alpha", "f_alpha_2" },
        alpha.FieldIds,
        "alpha snapshot should retain exact field ids in form order");
    AssertSequenceEqual(new[] { "b_alpha" }, alpha.BlockIds, "alpha snapshot should retain exact block ids");
    AssertEqual(
        "[{\"id\":\"f_alpha\",\"name\":\"Alpha one\",\"sectionId\":\"s_alpha\",\"type\":\"shortText\"},{\"id\":\"f_alpha_2\",\"name\":\"Alpha two\",\"sectionId\":\"s_alpha\",\"type\":\"boolean\"}]",
        alpha.FieldsJson,
        "section fields should be compact canonical JSON");
    AssertFalse(alpha.BlocksJson.Contains('\n'), "section blocks should not contain formatting whitespace");
    AssertEqual(64, alpha.ContentHash.Length, "section content hash should be SHA-256 hex");
    AssertEqual(alpha.ContentHash, alpha.ContentHash.ToLowerInvariant(), "section content hash should use lowercase hex");

    AssertSequenceEqual(
        first.Sections.Select(x => x.ContentHash).ToArray(),
        equivalent.Sections.Select(x => x.ContentHash).ToArray(),
        "equivalent property ordering and whitespace should produce deterministic hashes");
}

static void DynamicFormPublishedSchemaSnapshotIsCanonicalAndVersionBound()
{
    var first = DynamicFormPublishedSchemaSnapshotBuilder.Build(
        3,
        """[{ "title": "Main", "id": "s1" }]""",
        """[{ "label": "Amount", "sectionId": "s1", "id": "f1", "config": { "scale": 2, "required": true } }]""",
        """[{ "title": "Rows", "sectionId": "s1", "blockId": "b1" }]""");
    var equivalent = DynamicFormPublishedSchemaSnapshotBuilder.Build(
        3,
        """
        [
          { "id": "s1", "title": "Main" }
        ]
        """,
        """[{ "config": { "required": true, "scale": 2.00 }, "id": "f1", "sectionId": "s1", "label": "Amount" }]""",
        """[{ "blockId": "b1", "sectionId": "s1", "title": "Rows" }]""");

    AssertEqual(first.Json, equivalent.Json, "equivalent schemas should have the same canonical JSON");
    AssertEqual(first.Sha256, equivalent.Sha256, "equivalent schemas should have the same SHA-256");
    AssertEqual(64, first.Sha256.Length, "schema hash should be a 64-character SHA-256 hex value");
    AssertEqual(first.Sha256.ToLowerInvariant(), first.Sha256, "schema hash should use lowercase hex");

    var reorderedArray = DynamicFormPublishedSchemaSnapshotBuilder.Build(
        3,
        """[{ "id": "s2", "title": "Second" }, { "id": "s1", "title": "Main" }]""",
        """[{ "id": "f1", "sectionId": "s1", "label": "Amount", "config": { "required": true, "scale": 2 } }]""",
        """[{ "blockId": "b1", "sectionId": "s1", "title": "Rows" }]""");
    AssertFalse(
        string.Equals(first.Sha256, reorderedArray.Sha256, StringComparison.Ordinal),
        "array order is part of the published schema contract");

    var nextSchemaVersion = DynamicFormPublishedSchemaSnapshotBuilder.Build(
        4,
        """[{ "id": "s1", "title": "Main" }]""",
        """[{ "id": "f1", "sectionId": "s1", "label": "Amount", "config": { "required": true, "scale": 2 } }]""",
        """[{ "blockId": "b1", "sectionId": "s1", "title": "Rows" }]""");
    AssertFalse(
        string.Equals(first.Sha256, nextSchemaVersion.Sha256, StringComparison.Ordinal),
        "schemaVersion is part of the published schema contract");

    var legacyTemplate = new DynamicFormTemplate
    {
        SchemaVersion = 3,
        SectionsJson = """[{ "id": "s1", "title": "Main" }]""",
        FieldsJson = """[{ "id": "f1", "sectionId": "s1", "label": "Amount", "config": { "required": true, "scale": 2 } }]""",
        BlocksJson = "[]",
        ExcelBlockJson = """{ "blockId": "b1", "sectionId": "s1", "title": "Rows" }"""
    };
    var legacySnapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(legacyTemplate);
    var normalizedLegacySnapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(
        3,
        legacyTemplate.SectionsJson,
        legacyTemplate.FieldsJson,
        """[{ "blockId": "b1", "sectionId": "s1", "title": "Rows" }]""");
    AssertEqual(
        normalizedLegacySnapshot.Sha256,
        legacySnapshot.Sha256,
        "legacy ExcelBlockJson should hash as the equivalent canonical blocks array");
}

static void DynamicFormSectionSnapshotsFailClosedOnMalformedTopology()
{
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(fieldsJson: "{")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            sectionsJson: """[{ "id": "s_alpha", "title": "Alpha" }, { "id": "s_alpha", "title": "Again" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            sectionsJson: """[{ "title": "Missing id" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            fieldsJson: """[{ "id": "f_same", "sectionId": "s_alpha" }, { "id": "f_same", "sectionId": "s_beta" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            blocksJson: """[{ "blockId": "b_same", "sectionId": "s_alpha" }, { "blockId": "b_same", "sectionId": "s_beta" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            fieldsJson: """[{ "id": "f_orphan", "sectionId": "s_missing" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            fieldsJson: """[{ "id": "f_missing_section" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            blocksJson: """[{ "blockId": "b_orphan", "sectionId": "s_missing" }]""")));
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => DynamicFormSectionSnapshotBuilder.Build(TestDynamicFormSectionSnapshotTemplate(
            blocksJson: """[{ "blockId": "b_missing_section" }]""")));
}

static void AdvancedSummarySectionLookupUsesExactFormSnapshots()
{
    var template = TestDynamicFormSectionSnapshotTemplate();
    var snapshots = DynamicFormSectionSnapshotBuilder.Build(template);
    AssertTrue(snapshots.TryGetSection("s_alpha", out var alpha), "snapshot lookup should find an exact section");
    AssertEqual("Alpha", alpha.Title, "snapshot lookup returned the wrong section");
    AssertFalse(snapshots.TryGetSection("missing", out _), "snapshot lookup should not invent missing sections");
    AssertThrows(
        AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
        () => snapshots.GetRequiredSection("missing"));

    var configFields = (System.Collections.IEnumerable)InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "LoadSectionFields",
        template,
        "s_alpha");
    var hierarchyFields = (System.Collections.IEnumerable)InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryHierarchyService),
        "LoadSectionFields",
        template,
        "s_alpha");
    var expectedIds = new[] { "f_alpha", "f_alpha_2" };
    AssertSequenceEqual(
        expectedIds,
        configFields.Cast<object>().Select(x => GetReflectedProperty<string>(x, "FieldId")!).ToArray(),
        "advanced summary config lookup should use exact template snapshot fields");
    AssertSequenceEqual(
        expectedIds,
        hierarchyFields.Cast<object>().Select(x => GetReflectedProperty<string>(x, "FieldId")!).ToArray(),
        "advanced summary hierarchy lookup should use exact template snapshot fields");
}

static DynamicFormTemplate TestDynamicFormSectionSnapshotTemplate(
    string? sectionsJson = null,
    string? fieldsJson = null,
    string? blocksJson = null)
    => new()
    {
        Id = ObjectId(94),
        Code = "DF_SECTION_SNAPSHOT",
        Name = "Dynamic Form section snapshot",
        SchemaVersion = 3,
        SectionsJson = sectionsJson ?? """
        [
          { "id": "s_beta", "title": "Beta", "order": 2, "tagCodes": ["beta"] },
          { "title": "Alpha", "order": 1, "id": "s_alpha", "description": "Alpha section" }
        ]
        """,
        FieldsJson = fieldsJson ?? """
        [
          { "sectionId": "s_beta", "id": "f_beta", "type": "number", "name": "Beta" },
          { "name": "Alpha one", "id": "f_alpha", "type": "shortText", "sectionId": "s_alpha" },
          { "id": "f_alpha_2", "sectionId": "s_alpha", "name": "Alpha two", "type": "boolean" }
        ]
        """,
        BlocksJson = blocksJson ?? """
        [
          { "blockId": "b_beta", "sectionId": "s_beta", "tableMode": "FIXED_GRID" },
          { "tableMode": "FIXED_GRID", "blockId": "b_alpha", "sectionId": "s_alpha" }
        ]
        """,
        IsDeleted = false
    };

static void ValidatesDynamicFormFieldDisplayName()
{
    var serviceType = typeof(DynamicFormService);
    foreach (var name in new[] { "Số", "Ngày", "Nội dung", "number 1", "Tình hình xử lý hồ sơ: số lượng, kết quả (đợt 1) / năm 2026?", "<b>Nội dung</b>" })
    {
        var fields = JsonSerializer.Serialize(new[] {
            new { id = "f1", sectionId = "s1", key = "first", type = "number", name },
            new { id = "f2", sectionId = "s1", key = "second", type = "number", name }
        });
        InvokePrivateStatic<object?>(serviceType, "EnsureFieldDisplayNames", fields);
        var stored = InvokePrivateStatic<string?>(serviceType, "NormalizeFieldPayload", fields, fields);
        using var readback = JsonDocument.Parse(stored!);
        AssertEqual(name, readback.RootElement[0].GetProperty("name").GetString(), "display name is retained");
        AssertEqual("f1", readback.RootElement[0].GetProperty("id").GetString(), "first identity is retained");
        AssertEqual("second", readback.RootElement[1].GetProperty("key").GetString(), "duplicate name keeps distinct keys");
    }

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
          { "id": "f2", "sectionId": "s1", "type": "number", "name": "Chi phí", "key": "total" }
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

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<string?>(
            serviceType,
            "NormalizeFieldPayload",
            """
            [
              { "id": "f2", "sectionId": "s1", "type": "number", "name": "Chi phí", "key": "total" },
              { "id": "f3", "sectionId": "s1", "type": "number", "name": "Lợi nhuận", "key": "total" }
            ]
            """,
            null));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_NAME_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldDisplayNames",
            """
            [
              { "id": "f2", "sectionId": "s1", "key": "number_1", "type": "number", "name": "   ", "label": "   ", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
            ]
            """));

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureStatisticConfigOnlyChange",
        """
        [
          { "id": "f3", "sectionId": "s1", "key": "number_1", "type": "number", "name": "Doanh thu", "isStatistic": false, "statisticLabelCodes": ["old"] }
        ]
        """,
        """
        [
          { "id": "f3", "sectionId": "s1", "key": "number_1", "type": "number", "name": "Doanh thu", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
        ]
        """,
        "FieldsJson");

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureStatisticConfigOnlyChange",
            """
            [
              { "id": "f3", "sectionId": "s1", "key": "number_1", "type": "number", "name": "Doanh thu", "isStatistic": true, "statisticLabelCodes": ["old"] }
            ]
            """,
            """
            [
              { "id": "f3", "sectionId": "s1", "key": "number_1", "type": "number", "name": "Doanh thu kỳ này", "isStatistic": true, "statisticLabelCodes": ["revenue"] }
            ]
            """,
            "FieldsJson"));

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

static void AcceptsDynamicFormFieldSchemaContract()
{
    InvokePrivateStatic<object?>(
        typeof(DynamicFormService),
        "EnsureFieldSchemaContract",
        """
        [
          {
            "id": "f_short_fixed",
            "sectionId": "s1",
            "type": "shortText",
            "valueSource": {
              "sourceType": "FIXED_ENUM",
              "options": [{ "code": "FIXED_A", "label": "Fixed A" }]
            }
          },
          { "id": "f_long", "sectionId": "s1", "type": "longText" },
          { "id": "f_rich", "sectionId": "s1", "type": "richText" },
          { "id": "f_list", "sectionId": "s1", "type": "stringList" },
          { "id": "f_number", "sectionId": "s1", "type": "number" },
          { "id": "f_date", "sectionId": "s1", "type": "date" },
          { "id": "f_full_date", "sectionId": "s1", "type": "fullDate" },
          {
            "id": "f_single_catalog",
            "sectionId": "s1",
            "type": "singleSelect",
            "valueSource": { "sourceType": "ENUM_CATALOG", "catalogId": "catalog_1" }
          },
          {
            "id": "f_multi_unit",
            "sectionId": "s1",
            "type": "multiSelect",
            "valueSource": { "sourceType": "SYSTEM_UNIT" }
          },
          { "id": "f_boolean", "sectionId": "s1", "type": "boolean" },
          {
            "id": "f_short_user",
            "sectionId": "s1",
            "type": "shortText",
            "valueSource": { "sourceType": "SYSTEM_USER" }
          },
          {
            "id": "f_single_position",
            "sectionId": "s1",
            "type": "singleSelect",
            "valueSource": { "sourceType": "SYSTEM_POSITION" }
          },
          {
            "id": "f_multi_unit_type",
            "sectionId": "s1",
            "type": "multiSelect",
            "valueSource": { "sourceType": "SYSTEM_UNIT_TYPE" }
          }
        ]
        """,
        """
        [
          { "id": "s1", "title": "Main section" }
        ]
        """);
}

static void RejectsUnknownDynamicFormFieldType()
{
    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            typeof(DynamicFormService),
            "EnsureFieldSchemaContract",
            """
            [
              { "id": "f_unknown", "sectionId": "s1", "type": "currency" }
            ]
            """,
            """
            [
              { "id": "s1", "title": "Main section" }
            ]
            """));
}

static void RejectsDuplicateDynamicFormFieldIdsAndOrphanSections()
{
    var serviceType = typeof(DynamicFormService);
    const string sectionsJson = """
        [
          { "id": "s1", "title": "Main section" }
        ]
        """;

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            """
            [
              { "id": "f_duplicate", "sectionId": "s1", "type": "number" },
              { "id": "f_duplicate", "sectionId": "s1", "type": "date" }
            ]
            """,
            sectionsJson));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            """
            [
              { "id": "f_orphan", "sectionId": "missing_section", "type": "number" }
            ]
            """,
            sectionsJson));
}

static void RejectsDuplicateAndExcessiveDynamicFormFieldOptions()
{
    var serviceType = typeof(DynamicFormService);
    const string sectionsJson = """
        [
          { "id": "s1", "title": "Main section" }
        ]
        """;

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            """
            [
              {
                "id": "f_duplicate_options",
                "sectionId": "s1",
                "type": "singleSelect",
                "options": [
                  { "code": "A", "label": "Option A" },
                  { "code": "a", "label": "Option A duplicate" }
                ]
              }
            ]
            """,
            sectionsJson));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            """
            [
              {
                "id": "f_mismatched_fixed_options",
                "sectionId": "s1",
                "type": "shortText",
                "options": [{ "code": "A", "label": "Field option" }],
                "valueSource": {
                  "sourceType": "FIXED_ENUM",
                  "options": [{ "code": "B", "label": "Source option" }]
                }
              }
            ]
            """,
            sectionsJson));

    var options = new JsonArray();
    for (var index = 0; index < 101; index++)
    {
        options.Add(new JsonObject
        {
            ["code"] = $"OPTION_{index}",
            ["label"] = $"Option {index}"
        });
    }

    var fields = new JsonArray
    {
        new JsonObject
        {
            ["id"] = "f_excessive_options",
            ["sectionId"] = "s1",
            ["type"] = "multiSelect",
            ["options"] = options
        }
    };

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_LIMIT_EXCEEDED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            fields.ToJsonString(),
            sectionsJson));
}

static void RejectsRichTextStatisticsAndInvalidDynamicFormValueSources()
{
    var serviceType = typeof(DynamicFormService);
    const string sectionsJson = """
        [
          { "id": "s1", "title": "Main section" }
        ]
        """;

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            """
            [
              { "id": "f_rich_stat", "sectionId": "s1", "type": "richText", "isStatistic": true }
            ]
            """,
            sectionsJson));

    AssertThrowsFromReflection(
        AppErrorCode.DYNAMIC_FORM_FIELD_CONFIG_INVALID,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureFieldSchemaContract",
            """
            [
              {
                "id": "f_invalid_source",
                "sectionId": "s1",
                "type": "singleSelect",
                "valueSource": { "sourceType": "REMOTE_API" }
              }
            ]
            """,
            sectionsJson));
}

static void BucketsShortTextFieldStatistics()
{
    var serviceType = typeof(WorkReportFieldStatisticsService);

    var fields = InvokePrivateStatic<object>(
        serviceType,
        "ExtractStatisticFields",
        """
        [
          { "id": "f_short", "sectionId": "s1", "key": "phan_loai_ngan", "type": "shortText", "name": "Phan loai ngan", "isStatistic": true, "statisticLabelCodes": [" Short_Label ", "short_label"] }
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

    var field = GetReflectedProperty<object>(rows[0], "Field");
    AssertSequenceEqual(
        new[] { "short_label" },
        GetReflectedProperty<List<string>>(field, "StatisticLabelCodes"),
        "field statistic labels should be normalized and preserved in projected values");
}

static void TableStatisticsKeepMetricsFromEligibleBlocks()
{
    var serviceType = typeof(WorkReportTableStatisticsService);
    var mapType = serviceType.GetNestedType("MetricTargetMap", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MetricTargetMap helper type was not found.");
    var map = Activator.CreateInstance(mapType, nonPublic: true)
        ?? throw new InvalidOperationException("MetricTargetMap could not be created.");
    var addTargets = serviceType.GetMethod(
        "AddMetricTargetsFromBlockJson",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(serviceType.Name, "AddMetricTargetsFromBlockJson");
    var contains = mapType.GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingMethodException(mapType.Name, "Contains");

    addTargets.Invoke(null, new object?[]
    {
        """
        [
          {
            "blockId": "large",
            "statisticsInputCellCount": 400,
            "metricRules": [{ "metricKey": "large_metric" }]
          },
          {
            "blockId": "small",
            "statisticsInputCellCount": 1,
            "metricRules": [{ "metricKey": "small_metric" }]
          }
        ]
        """,
        map
    });

    AssertFalse(
        (bool)contains.Invoke(map, new object?[] { "large", "large_metric" })!,
        "oversized block metrics should be excluded");
    AssertTrue(
        (bool)contains.Invoke(map, new object?[] { "small", "small_metric" })!,
        "an oversized sibling block must not disable eligible block metrics");
}

static void TableStatisticsDoNotReviveDisabledCanonicalBlockFromLegacyJson()
{
    var serviceType = typeof(WorkReportTableStatisticsService);
    var buildMap = serviceType.GetMethod(
        "BuildMetricTargetMap",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(serviceType.Name, "BuildMetricTargetMap");
    var map = buildMap.Invoke(null, new object?[]
    {
        """
        [
          {
            "blockId": "primary",
            "statisticsDisabled": true,
            "metricRules": [{ "metricKey": "canonical_metric" }]
          }
        ]
        """,
        """
        {
          "blockId": "primary",
          "statisticsDisabled": false,
          "metricRules": [{ "metricKey": "legacy_metric" }]
        }
        """
    }) ?? throw new InvalidOperationException("Metric target map could not be built.");
    var contains = map.GetType().GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingMethodException(map.GetType().Name, "Contains");

    AssertFalse(
        (bool)contains.Invoke(map, new object?[] { "primary", "canonical_metric" })!,
        "disabled canonical block metrics should stay excluded");
    AssertFalse(
        (bool)contains.Invoke(map, new object?[] { "primary", "legacy_metric" })!,
        "legacy duplicate must not revive a disabled canonical block");
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

static void BasicSummaryRejectsIncompatibleFieldMethodRules()
{
    var serviceType = typeof(WorkAssignmentBasicSummaryService);
    var normalizeRules = serviceType.GetMethod(
        "NormalizeRules",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(serviceType.Name, "NormalizeRules");

    AssertThrowsFromReflection(
        AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_MODE_INVALID,
        () => normalizeRules.Invoke(null, new object?[]
        {
            new List<WorkAssignmentBasicSummaryRuleDto>
            {
                new()
                {
                    TargetKind = "FIELD",
                    TargetKey = "field:note",
                    Operation = "PERCENTILE"
                }
            }
        }));

    var rules = new List<WorkAssignmentBasicSummaryRuleDto>
    {
        new()
        {
            TargetKind = "FIELD",
            TargetKey = "field:note",
            Operation = "SUM"
        }
    };

    AssertThrowsFromReflection(
        AppErrorCode.WORK_ASSIGNMENT_AGGREGATE_MODE_INVALID,
        () => InvokePrivateStatic<string>(
            serviceType,
            "ResolveOperation",
            rules,
            "FIELD",
            "field:note",
            "note",
            "TEXT",
            "COUNT"));
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

static void BasicSummaryResetRequestRebuildsFromSnapshotJson()
{
    var requestJson = JsonSerializer.Serialize(new
    {
        scopeAssignmentId = ObjectId(1),
        dynamicFormTemplateId = ObjectId(2),
        periodScopeMode = "SINGLE_PERIOD",
        periodKey = "20260601",
        selectedUnitIds = new[] { ObjectId(3) },
        defaultMethods = new { number = "AVG", text = "LIST" },
        rules = new[]
        {
            new { targetKind = "FIELD", targetKey = "field:score", operation = "COUNT" }
        },
        maxTextChars = 500000
    });

    var req = InvokePrivateStatic<WorkAssignmentBasicSummaryRequest>(
        typeof(WorkAssignmentBasicSummaryService),
        "BuildBasicSummaryRequestFromSnapshotJson",
        requestJson);

    AssertEqual(ObjectId(1), req.ScopeAssignmentId, "reset request should keep scope assignment");
    AssertEqual(ObjectId(2), req.DynamicFormTemplateId, "reset request should keep dynamic form template");
    AssertEqual("SINGLE_PERIOD", req.PeriodScopeMode, "reset request should keep period scope mode");
    AssertEqual("20260601", req.PeriodKey, "reset request should keep period key");
    AssertEqual(ObjectId(3), req.SelectedUnitIds?.Single(), "reset request should keep selected units");
    AssertEqual("AVG", req.DefaultMethods?.Number, "reset request should keep typed number method");
    AssertEqual("LIST", req.DefaultMethods?.Text, "reset request should keep typed text method");
    AssertEqual("field:score", req.Rules?.Single().TargetKey, "reset request should keep field rule");
    AssertTrue(req.ForceRefresh, "reset request should force refresh");
    AssertTrue(req.IncludeSourceRows, "reset request should include source rows for rebuild");
    AssertEqual(100000, req.MaxTextChars, "reset request should clamp max text chars");
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

static void SummarySourceScopeNormalizesFlowModes()
{
    var scope = new WorkAssignment
    {
        Id = ObjectId(70),
        WorkId = ObjectId(71),
        Path = $"/{ObjectId(70)}",
        FlowInstanceId = ObjectId(72),
        FlowStepId = "review",
        FlowBranchId = ObjectId(73)
    };

    var stepScope = NormalizeSummarySourceScope(scope, " flow_step ", null, null, ObjectId(74), null);
    AssertEqual("FLOW_STEP", GetReflectedProperty<string>(stepScope, "Mode"), "source scope mode should normalize to uppercase");
    AssertEqual(scope.FlowInstanceId, GetReflectedProperty<string>(stepScope, "FlowInstanceId"), "source scope should inherit flow instance from scope assignment");
    AssertEqual(scope.FlowStepId, GetReflectedProperty<string>(stepScope, "FlowStepId"), "FLOW_STEP should inherit step id from scope assignment");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(stepScope, "FlowBranchId"), "FLOW_STEP should ignore irrelevant branch id input");
    AssertEqual(
        DynamicFlowEffectiveStatuses.Effective,
        GetReflectedProperty<string>(stepScope, "FlowEffectiveStatus"),
        "flow source scopes should default to effective data");

    var finalScope = NormalizeSummarySourceScope(
        scope,
        "FLOW_FINAL",
        null,
        "ignored-step",
        ObjectId(75),
        "any");
    AssertEqual("FLOW_FINAL", GetReflectedProperty<string>(finalScope, "Mode"), "final mode should be preserved");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(finalScope, "FlowStepId"), "FLOW_FINAL should ignore irrelevant step id input");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(finalScope, "FlowBranchId"), "FLOW_FINAL should ignore irrelevant branch id input");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(finalScope, "FlowEffectiveStatus"), "ANY should remove the effective-status filter");
}

static void StatisticDiffPreviousPeriodResolvesKeyFormats()
{
    var serviceType = typeof(WorkReportStatisticDiffService);

    AssertEqual(
        "20240621",
        InvokePrivateStatic<string>(serviceType, "ResolveComparisonPeriodKey", "20240622", "PREVIOUS_PERIOD"),
        "compact day period should subtract one day");
    AssertEqual(
        "2024-05",
        InvokePrivateStatic<string>(serviceType, "ResolveComparisonPeriodKey", "2024-06", "PREVIOUS_PERIOD"),
        "month period should subtract one month");
    AssertEqual(
        "2023",
        InvokePrivateStatic<string>(serviceType, "ResolveComparisonPeriodKey", "2024", "PREVIOUS_PERIOD"),
        "year period should subtract one year");
    AssertEqual(
        "20240622",
        InvokePrivateStatic<string>(serviceType, "ResolveComparisonPeriodKey", "20240622", "SAME_PERIOD"),
        "same-period compare should keep the current period");
}

static void StatisticDiffRowJoinRequiresTableIdentity()
{
    var serviceType = typeof(WorkReportStatisticDiffService);
    var field = new WorkReportStatisticDiffTargetDto { SourceKind = "FIELD", DynamicFormTemplateId = ObjectId(81), FieldId = "score" };
    var table = new WorkReportStatisticDiffTargetDto { SourceKind = "TABLE", DynamicFormTemplateId = ObjectId(81), BlockId = "b1", MetricKey = "score" };

    AssertThrowsFromReflection(
        AppErrorCode.WORK_REPORT_STATISTIC_DIFF_ROW_JOIN_REQUIRED,
        () => InvokePrivateStatic<object?>(
            serviceType,
            "EnsureRowJoinCompatible",
            field,
            table,
            "ROW_KEY"));

    InvokePrivateStatic<object?>(
        serviceType,
        "EnsureRowJoinCompatible",
        table,
        table,
        "ROW_KEY");
}

static void StatisticDiffCompatibilityRejectsConceptMismatch()
{
    AssertThrowsFromReflection(
        AppErrorCode.WORK_REPORT_STATISTIC_DIFF_INCOMPATIBLE,
        () => InvokePrivateStatic<object?>(
            typeof(WorkReportStatisticDiffService),
            "EnsureConceptCompatible",
            "PLAN_SCORE",
            "FINAL_SCORE",
            true));

    InvokePrivateStatic<object?>(
        typeof(WorkReportStatisticDiffService),
        "EnsureConceptCompatible",
        "PLAN_SCORE",
        "FINAL_SCORE",
        false);
}

static void StatisticDiffCompatibilityRejectsDataCategoryMismatch()
{
    AssertThrowsFromReflection(
        AppErrorCode.WORK_REPORT_STATISTIC_DIFF_INCOMPATIBLE,
        () => InvokePrivateStatic<object?>(
            typeof(WorkReportStatisticDiffService),
            "EnsureDataCategoryCompatible",
            "NUMBER",
            "BUCKET"));

    InvokePrivateStatic<object?>(
        typeof(WorkReportStatisticDiffService),
        "EnsureDataCategoryCompatible",
        "NUMBER",
        "NUMBER");
}

static void StatisticDiffAcceptsFieldStatisticLabelSelector()
{
    var normalized = InvokePrivateStatic<WorkReportStatisticDiffTargetDto>(
        typeof(WorkReportStatisticDiffService),
        "NormalizeTarget",
        new WorkReportStatisticDiffTargetDto
        {
            SourceKind = WorkReportStatisticDiffSourceKinds.Field,
            StatisticLabelCode = " Revenue_Total "
        },
        ObjectId(82),
        "current");

    AssertEqual(
        "revenue_total",
        normalized.StatisticLabelCode,
        "field statistic label selector should be trimmed and normalized");
    AssertEqual(
        ObjectId(82),
        normalized.DynamicFormTemplateId,
        "field statistic label selector should keep the fallback dynamic form template");
}

static void AdvancedSummaryConfigHashIncludesSourceScope()
{
    var scope = new WorkAssignment
    {
        Id = ObjectId(76),
        WorkId = ObjectId(77),
        Path = $"/{ObjectId(76)}",
        FlowInstanceId = ObjectId(78),
        FlowStepId = "review",
        FlowBranchId = ObjectId(79)
    };
    var configJson = """{"targets":[{"fieldRef":"score","method":"SUM"}]}""";
    var buildHash = typeof(WorkAssignmentAdvancedSummaryConfigService).GetMethod(
        "BuildConfigHash",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentAdvancedSummaryConfigService), "BuildConfigHash");

    var stepScope = NormalizeSummarySourceScope(scope, "FLOW_STEP", null, null, null, null);
    var branchScope = NormalizeSummarySourceScope(scope, "FLOW_BRANCH", null, null, null, null);

    var stepHash = (string)buildHash.Invoke(null, new object?[] { configJson, stepScope })!;
    var branchHash = (string)buildHash.Invoke(null, new object?[] { configJson, branchScope })!;

    AssertTrue(!string.Equals(stepHash, branchHash, StringComparison.Ordinal), "advanced config hash should change when source scope changes");
}

static void AdvancedSummaryConfigNormalizesObjectJson()
{
    var normalized = InvokePrivateStatic<string>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "NormalizeConfigJson",
        "{ \"method\": \"SUM\", \"condition\": { \"field\": \"score\" } }");

    AssertEqual(
        "{\"method\":\"SUM\",\"condition\":{\"field\":\"score\"}}",
        normalized,
        "advanced summary config should normalize object json");

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<string>(
            typeof(WorkAssignmentAdvancedSummaryConfigService),
            "NormalizeConfigJson",
            "[]"));
}

static void AdvancedSummaryPreviewBlocksUnsupportedRangeCondition()
{
    InvokePrivateStatic<object?>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "EnsurePreviewConfigSupported",
        """{ "targets": [{ "fieldId": "score", "method": "SUM" }] }""");

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStatic<object?>(
            typeof(WorkAssignmentAdvancedSummaryConfigService),
            "EnsurePreviewConfigSupported",
            """{ "dataRanges": [{ "fieldIds": ["score"] }], "conditions": [{ "fieldId": "status" }] }"""));
}

static void AdvancedSummaryFieldGateEnforcesSectionLimits()
{
    var nonCumulativeOk = InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "BuildFieldGateInfoFromCounts",
        "{}",
        1000,
        1000);
    AssertEqual("ALLOWED", GetReflectedProperty<string>(nonCumulativeOk, "Status"), "1000 non-cumulative section fields should be allowed");
    AssertEqual(false, GetReflectedProperty<bool>(nonCumulativeOk, "IsCumulative"), "empty config should not be cumulative");

    var nonCumulativeBlocked = InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "BuildFieldGateInfoFromCounts",
        "{}",
        1001,
        10);
    AssertEqual("BLOCKED", GetReflectedProperty<string>(nonCumulativeBlocked, "Status"), "1001 non-cumulative section fields should be blocked");
    AssertEqual(1000, GetReflectedProperty<int>(nonCumulativeBlocked, "FieldLimit"), "non-cumulative field limit should be 1000");

    var cumulativeOk = InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "BuildFieldGateInfoFromCounts",
        """{ "cumulative": true }""",
        249,
        249);
    AssertEqual("ALLOWED", GetReflectedProperty<string>(cumulativeOk, "Status"), "249 cumulative section fields should be allowed");
    AssertEqual(true, GetReflectedProperty<bool>(cumulativeOk, "IsCumulative"), "cumulative flag should be detected");
    AssertEqual(249, GetReflectedProperty<int>(cumulativeOk, "FieldLimit"), "cumulative display limit should be 249");

    var cumulativeBlocked = InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryConfigService),
        "BuildFieldGateInfoFromCounts",
        """{ "periodScopeMode": "CUMULATIVE_TO_PERIOD" }""",
        250,
        1);
    AssertEqual("BLOCKED", GetReflectedProperty<string>(cumulativeBlocked, "Status"), "250 cumulative section fields should be blocked");
    AssertEqual(true, GetReflectedProperty<bool>(cumulativeBlocked, "IsCumulative"), "cumulative period scope should be detected");
}

static void AdvancedSummaryHierarchyKeysRollUpDayMonthYear()
{
    var date = new DateTime(2026, 6, 20, 13, 45, 0, DateTimeKind.Utc);
    var dateOnly = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Unspecified);

    AssertEqual("2026-06-20", AdvancedSummaryHierarchyKeyHelper.ToDayKey(date), "day key should be yyyy-MM-dd");
    AssertEqual("2026-06-20", AdvancedSummaryHierarchyKeyHelper.ToDayKey(dateOnly), "date-only unspecified values should not shift day key");
    AssertEqual("2026-06", AdvancedSummaryHierarchyKeyHelper.ToMonthKey("2026-06-20"), "day should roll up to month key");
    AssertEqual("2026", AdvancedSummaryHierarchyKeyHelper.ToYearKeyFromMonth("2026-06"), "month should roll up to year key");

    var month = AdvancedSummaryHierarchyKeyHelper.GetMonthBoundsUtc("2026-02");
    AssertEqual(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), month.StartUtc, "month start should be utc first day");
    AssertEqual(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), month.EndExclusiveUtc, "month end should be exclusive next month");

    var year = AdvancedSummaryHierarchyKeyHelper.GetYearBoundsUtc("2024");
    AssertEqual(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), year.StartUtc, "year start should be utc Jan 1");
    AssertEqual(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), year.EndExclusiveUtc, "year end should be exclusive next year");
}

static void AdvancedSummaryDayNodeSourceDayUsesCompletedDateFirst()
{
    var report = new WorkAssignmentReport
    {
        CompletedDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified),
        PeriodKey = "2026-01-07",
        PeriodStart = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc),
        ReportDate = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
        ApprovedAtUtc = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)
    };

    var completedDay = InvokePrivateStatic<string>(
        typeof(WorkAssignmentAdvancedSummaryHierarchyService),
        "ResolveReportSourceDayKey",
        report);
    AssertEqual("2026-01-05", completedDay, "completed date should define source day before period metadata");
    AssertEqual(
        completedDay,
        AdvancedSummaryReportSourceDayResolver.Resolve(report),
        "day builder and dirty marker should share the same source-day resolver");

    report.CompletedDate = null;
    var periodDay = InvokePrivateStatic<string>(
        typeof(WorkAssignmentAdvancedSummaryHierarchyService),
        "ResolveReportSourceDayKey",
        report);
    AssertEqual("2026-01-07", periodDay, "period key should define source day when completed date is missing");
}

static void AdvancedSummaryDirtyScopesIncludeSelfAndParentAssignments()
{
    var ids = InvokePrivateStatic<List<string>>(
        typeof(WorkAssignmentAdvancedSummaryDirtyService),
        "BuildCandidateScopeAssignmentIds",
        " child ",
        " parent ");

    AssertSequenceEqual(
        new[] { "child", "parent" },
        ids,
        "dirty marker should touch configs on the source assignment and its direct parent scope");

    var selfOnly = InvokePrivateStatic<List<string>>(
        typeof(WorkAssignmentAdvancedSummaryDirtyService),
        "BuildCandidateScopeAssignmentIds",
        "child",
        " child ");

    AssertSequenceEqual(
        new[] { "child" },
        selfOnly,
        "dirty scope should not duplicate the same assignment id");
}

static void AdvancedSummaryDirtyFlowBranchScopeIncludesDescendants()
{
    var workId = ObjectId(80);
    var flowInstanceId = ObjectId(81);
    var rootBranchId = ObjectId(82);

    var scope = new WorkAssignment
    {
        Id = ObjectId(83),
        WorkId = workId,
        Path = $"/{ObjectId(83)}",
        FlowInstanceId = flowInstanceId,
        FlowBranchId = rootBranchId,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };
    var descendant = new WorkAssignment
    {
        Id = ObjectId(84),
        WorkId = workId,
        ParentAssignmentId = scope.Id,
        Path = $"{scope.Path}/{ObjectId(84)}",
        FlowInstanceId = flowInstanceId,
        FlowBranchId = ObjectId(85),
        ParentFlowBranchId = rootBranchId,
        FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Invalidated
    };
    var config = new WorkAssignmentAdvancedSummaryConfig
    {
        Id = ObjectId(86),
        WorkId = workId,
        AssignmentId = scope.Id,
        DynamicFormTemplateId = ObjectId(87),
        SectionId = "main",
        SourceScopeMode = "FLOW_BRANCH",
        SourceFlowInstanceId = flowInstanceId,
        SourceFlowBranchId = rootBranchId,
        SourceFlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective
    };

    var matcher = typeof(WorkAssignmentAdvancedSummaryDirtyService).GetMethod(
        "ConfigSourceMayIncludeAssignment",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WorkAssignmentAdvancedSummaryDirtyService), "ConfigSourceMayIncludeAssignment");

    AssertTrue(
        (bool)matcher.Invoke(null, new object?[] { config, scope, descendant })!,
        "FLOW_BRANCH dirty matcher should include descendant assignment paths even after status changes");
}

static void AdvancedSummaryDirtyStatusMutationRulesCoverApprovedAndActiveChanges()
{
    var submittedReport = new WorkAssignmentReport { Status = WorkAssignmentReportStatus.Submitted };
    var approveMutation = InvokePrivateStatic<bool>(
        typeof(WorkAssignmentAdvancedSummaryDirtyService),
        "ShouldDirtyForStatusMutation",
        submittedReport,
        WorkAssignmentReportStatus.Submitted.ToString(),
        WorkAssignmentReportStatus.Approved.ToString());
    AssertTrue(approveMutation, "moving into approved status should dirty advanced summary nodes");

    var approvedReport = new WorkAssignmentReport { Status = WorkAssignmentReportStatus.Approved };
    var deactivateMutation = InvokePrivateStatic<bool>(
        typeof(WorkAssignmentAdvancedSummaryDirtyService),
        "ShouldDirtyForStatusMutation",
        approvedReport,
        "ACTIVE",
        "INACTIVE");
    AssertTrue(deactivateMutation, "deactivating an approved report should dirty advanced summary nodes");

    var draftMutation = InvokePrivateStatic<bool>(
        typeof(WorkAssignmentAdvancedSummaryDirtyService),
        "ShouldDirtyForStatusMutation",
        submittedReport,
        WorkAssignmentReportStatus.Draft.ToString(),
        WorkAssignmentReportStatus.Submitted.ToString());
    AssertFalse(draftMutation, "draft to submitted should not dirty advanced summary nodes before approval");
}

static void AdvancedSummaryHierarchyRollupMergesChildFields()
{
    var childNodes = new List<WorkAssignmentAdvancedSummaryDayNode>
    {
        TestAdvancedSummaryDayNode(
            "2026-01-01",
            2,
            """
            {
              "schemaVersion": 1,
              "kind": "ADVANCED_SUMMARY_DAY_NODE_V1",
              "configId": "cfg",
              "configHash": "hash",
              "warnings": [],
              "fields": [
                { "fieldId": "num", "fieldKey": "num", "label": "Number", "dataType": "NUMBER", "method": "SUM", "valueCount": 2, "sourceReportCount": 2, "result": 10, "sampleValues": ["4", "6"] },
                { "fieldId": "avg", "fieldKey": "avg", "label": "Average", "dataType": "NUMBER", "method": "MEAN", "valueCount": 2, "sourceReportCount": 2, "result": 5, "sampleValues": ["5"] },
                { "fieldId": "choice", "fieldKey": "choice", "label": "Choice", "dataType": "SINGLE_SELECT", "method": "BUCKET_COUNT", "valueCount": 1, "sourceReportCount": 1, "result": { "A": 1 }, "sampleValues": ["A"] }
              ]
            }
            """),
        TestAdvancedSummaryDayNode(
            "2026-01-02",
            3,
            """
            {
              "schemaVersion": 1,
              "kind": "ADVANCED_SUMMARY_DAY_NODE_V1",
              "configId": "cfg",
              "configHash": "hash",
              "warnings": [],
              "fields": [
                { "fieldId": "num", "fieldKey": "num", "label": "Number", "dataType": "NUMBER", "method": "SUM", "valueCount": 1, "sourceReportCount": 1, "result": 7, "sampleValues": ["7"] },
                { "fieldId": "avg", "fieldKey": "avg", "label": "Average", "dataType": "NUMBER", "method": "MEAN", "valueCount": 1, "sourceReportCount": 1, "result": 11, "sampleValues": ["11"] },
                { "fieldId": "choice", "fieldKey": "choice", "label": "Choice", "dataType": "SINGLE_SELECT", "method": "BUCKET_COUNT", "valueCount": 3, "sourceReportCount": 3, "result": { "A": 2, "B": 1 }, "sampleValues": ["A", "B"] }
              ]
            }
            """)
    };

    var rollup = InvokePrivateStaticGeneric<object>(
        typeof(WorkAssignmentAdvancedSummaryHierarchyService),
        "BuildRollupValue",
        new[] { typeof(WorkAssignmentAdvancedSummaryDayNode) },
        "ADVANCED_SUMMARY_MONTH_NODE_V1",
        "MONTH",
        "2026-01",
        "2026-01",
        "2026",
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        childNodes,
        new Func<WorkAssignmentAdvancedSummaryDayNode, string>(x => x.DayKey));

    AssertEqual("MONTH", GetReflectedProperty<string>(rollup, "Grain"), "rollup grain should be month");
    AssertEqual(5L, GetReflectedProperty<long>(rollup, "SourceReportCount"), "source report count should sum child nodes");

    var sumField = GetRollupField(rollup, "num");
    AssertEqual(17m, ToDecimalResult(sumField), "sum rollup should add child sums");

    var meanField = GetRollupField(rollup, "avg");
    AssertEqual(7m, ToDecimalResult(meanField), "mean rollup should weight by child value count");

    var choiceField = GetRollupField(rollup, "choice");
    var buckets = GetReflectedProperty<object>(choiceField, "Result") as System.Collections.IDictionary
        ?? throw new InvalidOperationException("bucket result should be a dictionary");
    AssertEqual(3L, Convert.ToInt64(buckets["A"], System.Globalization.CultureInfo.InvariantCulture), "bucket A should merge child counts");
    AssertEqual(1L, Convert.ToInt64(buckets["B"], System.Globalization.CultureInfo.InvariantCulture), "bucket B should merge child counts");
}

static void AdvancedSummaryHierarchyRollupRequiresCleanChildren()
{
    var childNodes = new List<WorkAssignmentAdvancedSummaryDayNode>
    {
        TestAdvancedSummaryDayNode("2026-01-01", 1, """{ "fields": [] }""", WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Dirty, isDirty: true)
    };

    AssertThrowsFromReflection(
        AppErrorCode.COMMON_VALIDATION_FAILED,
        () => InvokePrivateStaticGeneric<object?>(
            typeof(WorkAssignmentAdvancedSummaryHierarchyService),
            "ValidateCleanChildNodes",
            new[] { typeof(WorkAssignmentAdvancedSummaryDayNode) },
            "MONTH",
            "2026-01",
            "DAY",
            new List<string> { "2026-01-01", "2026-01-02" },
            childNodes,
            new Func<WorkAssignmentAdvancedSummaryDayNode, string>(x => x.DayKey)));
}

static void AdvancedSummaryHierarchyQueryPlansLargestGrains()
{
    var spans = InvokePrivateStatic<object>(
        typeof(WorkAssignmentAdvancedSummaryHierarchyService),
        "PlanHierarchyQuerySpans",
        new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2027, 2, 3, 0, 0, 0, DateTimeKind.Utc)) as System.Collections.IEnumerable
        ?? throw new InvalidOperationException("query spans should be enumerable");

    var keys = spans
        .Cast<object>()
        .Select(x => $"{GetReflectedProperty<string>(x, "Grain")}:{GetReflectedProperty<string>(x, "GrainKey")}")
        .ToList();

    AssertSequenceEqual(
        new[]
        {
            "DAY:2025-12-31",
            "YEAR:2026",
            "MONTH:2027-01",
            "DAY:2027-02-01",
            "DAY:2027-02-02"
        },
        keys,
        "query planner should use the largest exact day/month/year spans");
}

static void SummaryTokenMonthKeyUsesUtcMonth()
{
    var localKindDate = new DateTime(2026, 6, 20, 23, 0, 0, DateTimeKind.Utc);

    AssertEqual("2026-06", WorkSummaryTokenService.ToMonthKey(localKindDate), "token period should use utc yyyy-MM");
}

static void SummaryTokenQuotaBoundaryAllowsFreeInitialLock()
{
    AssertFalse(
        WorkSummaryTokenService.WouldExceedQuota(30, 0, 30),
        "free initial advanced config lock should not consume quota");

    AssertFalse(
        WorkSummaryTokenService.WouldExceedQuota(29, 1, 30),
        "last available monthly token should be consumable");

    AssertTrue(
        WorkSummaryTokenService.WouldExceedQuota(30, 1, 30),
        "next config change after quota exhaustion should be blocked");
}

static void SummaryTokenUnitBaseQuotaUsesActiveUsers()
{
    AssertEqual(0, WorkSummaryTokenService.CalculateBaseQuotaFromActiveUsers(-2), "negative active user counts should not reduce quota");
    AssertEqual(0, WorkSummaryTokenService.CalculateBaseQuotaFromActiveUsers(0), "empty unit should have no base quota");
    AssertEqual(12, WorkSummaryTokenService.CalculateBaseQuotaFromActiveUsers(12), "unit base quota should equal active users in that unit");
}

static void SummaryTokenAdminGrantsExtendUnitAllowance()
{
    AssertEqual(35, WorkSummaryTokenService.CalculateAllowance(30, 5), "grant units should extend the monthly quota");
    AssertEqual(5, WorkSummaryTokenService.CalculateAllowance(0, 5), "grant units should work when base quota is zero");
    AssertEqual(30, WorkSummaryTokenService.CalculateAllowance(30, -5), "negative grants should not reduce allowance");

    var admin = Me("admin", "NORMAL_USER", new List<string> { Roles.ADMIN });
    var systemAdmin = Me("sysadmin", ManagementAccountKind.SystemAdmin, new List<string> { Roles.SYSTEM_ADMIN });
    var unitManager = Me(
        username: "mu_pv01",
        accountKind: ManagementAccountKind.UnitManager,
        roles: new List<string> { Roles.ManagerUnit(ObjectId(9)) });

    AssertTrue(WorkSummaryTokenService.CanGrantExtraQuota(admin), "admin should be allowed to grant extra unit quota");
    AssertTrue(WorkSummaryTokenService.CanGrantExtraQuota(systemAdmin), "system admin should be allowed to grant extra unit quota");
    AssertFalse(WorkSummaryTokenService.CanGrantExtraQuota(unitManager), "mu should use the unit pool and contact admin for extra quota");
}

static void SummaryTokenManagerScopeFollowsUnitHierarchy()
{
    var parent = new Unit
    {
        Id = ObjectId(1),
        Code = "001",
        Level = 1
    };
    var child = new Unit
    {
        Id = ObjectId(2),
        Code = "001001",
        Level = 2,
        ParentUnitId = parent.Id
    };
    var sibling = new Unit
    {
        Id = ObjectId(3),
        Code = "002",
        Level = 1
    };

    AssertTrue(
        WorkSummaryTokenService.IsSameOrDescendantUnit(parent, child),
        "manager unit should cover descendant units");
    AssertTrue(
        WorkSummaryTokenService.IsSameOrDescendantUnit(parent, parent),
        "manager unit should cover its own unit");
    AssertFalse(
        WorkSummaryTokenService.IsSameOrDescendantUnit(child, parent),
        "child manager should not cover parent unit");
    AssertFalse(
        WorkSummaryTokenService.IsSameOrDescendantUnit(parent, sibling),
        "manager unit should not cover sibling branches");
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

static WorkAssignmentAdvancedSummaryDayNode TestAdvancedSummaryDayNode(
    string dayKey,
    long sourceReportCount,
    string valueJson,
    string status = WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean,
    bool isDirty = false)
    => new()
    {
        Id = ObjectId(Math.Abs(dayKey.GetHashCode()) % 100000 + 1000),
        WorkId = ObjectId(90),
        AssignmentId = ObjectId(91),
        DynamicFormTemplateId = ObjectId(92),
        SectionId = "section",
        ConfigId = ObjectId(93),
        ConfigVersionNo = 1,
        ConfigHash = "hash",
        Grain = WorkAssignmentAdvancedSummaryHierarchyGrains.Day,
        GrainKey = dayKey,
        DayKey = dayKey,
        WindowStartUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey),
        WindowEndExclusiveUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey).AddDays(1),
        Status = status,
        IsDirty = isDirty,
        SourceSignatureHash = $"sig-{dayKey}",
        SourceReportCount = sourceReportCount,
        ValueJson = valueJson,
        ValueHash = $"value-{dayKey}",
        BuiltAtUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey).AddHours(1),
        CreatedAtUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey),
        UpdatedAtUtc = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(dayKey).AddHours(2),
        IsDeleted = false
    };

static WorkAssignmentReport ApprovedHistoricalMutationReport(DateTime sourceDate)
    => new()
    {
        Id = ObjectId(120),
        WorkId = ObjectId(121),
        WorkAssignmentId = ObjectId(122),
        WorkReportPeriodId = ObjectId(123),
        AssigneeUserId = UserId(4),
        Status = WorkAssignmentReportStatus.Approved,
        CompletedDate = sourceDate.Date,
        PeriodKey = sourceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        IsHistoricalData = true,
        HistoricalDataApproved = true,
        ApprovedAtUtc = sourceDate.Date.AddHours(9),
        UpdatedAtUtc = sourceDate.Date.AddHours(10)
    };

static object GetRollupField(object rollup, string fieldId)
{
    var fields = GetReflectedProperty<object>(rollup, "Fields") as System.Collections.IEnumerable
        ?? throw new InvalidOperationException("rollup fields should be enumerable");

    return fields
        .Cast<object>()
        .Single(x => string.Equals(GetReflectedProperty<string>(x, "FieldId"), fieldId, StringComparison.Ordinal));
}

static decimal ToDecimalResult(object field)
{
    var result = GetReflectedProperty<object>(field, "Result")
        ?? throw new InvalidOperationException("field result should not be null");
    return Convert.ToDecimal(result, System.Globalization.CultureInfo.InvariantCulture);
}

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

static void AssertFlowMetadataNull(object value, string scope)
{
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowTemplateId"), $"{scope} flow template should be null");
    AssertEqual<int?>(null, GetReflectedProperty<int?>(value, "FlowTemplateVersionNo"), $"{scope} flow template version should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowInstanceId"), $"{scope} flow instance should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowStepId"), $"{scope} flow step id should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowStepCode"), $"{scope} flow step code should be null");
    AssertEqual<int?>(null, GetReflectedProperty<int?>(value, "FlowStepOrder"), $"{scope} flow step order should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowBranchId"), $"{scope} flow branch should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "ParentFlowBranchId"), $"{scope} parent flow branch should be null");
    AssertEqual<int?>(null, GetReflectedProperty<int?>(value, "FlowAttemptNo"), $"{scope} flow attempt should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowRole"), $"{scope} flow role should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "FlowEffectiveStatus"), $"{scope} flow effective status should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "IssuedByUnitId"), $"{scope} issuer unit should be null");
    AssertEqual<List<string>?>(null, GetReflectedProperty<List<string>?>(value, "TargetUnitIds"), $"{scope} target units should be null");
    AssertEqual<bool?>(null, GetReflectedProperty<bool?>(value, "AllowSubFlow"), $"{scope} allow sub-flow should be null");
    AssertEqual<bool?>(null, GetReflectedProperty<bool?>(value, "IsFlowFinalNode"), $"{scope} final node should be null");
    AssertEqual<string?>(null, GetReflectedProperty<string?>(value, "InvalidatedByFlowEventId"), $"{scope} invalidating event should be null");
}

static T InvokePrivateStatic<T>(Type type, string name, params object?[] args)
{
    var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{type.Name}.{name} helper method was not found.");

    var result = method.Invoke(null, args);
    return result is null ? default! : (T)result;
}

static T InvokePrivateStaticGeneric<T>(Type type, string name, Type[] genericTypes, params object?[] args)
{
    var method = type
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .SingleOrDefault(x => x.Name == name && x.IsGenericMethodDefinition && x.GetGenericArguments().Length == genericTypes.Length)
        ?? throw new InvalidOperationException($"{type.Name}.{name} generic helper method was not found.");

    var result = method.MakeGenericMethod(genericTypes).Invoke(null, args);
    return result is null ? default! : (T)result;
}

static object NormalizeSummarySourceScope(
    WorkAssignment scope,
    string? sourceScopeMode,
    string? sourceFlowInstanceId,
    string? sourceFlowStepId,
    string? sourceFlowBranchId,
    string? sourceFlowEffectiveStatus)
{
    var type = typeof(WorkAssignmentBasicSummaryService).Assembly.GetType(
        "tdtd_be.Services.WorkAssignments.Internal.WorkAssignmentSummarySourceScope")
        ?? throw new InvalidOperationException("WorkAssignmentSummarySourceScope helper type was not found.");
    var method = type.GetMethod("Normalize", BindingFlags.Static | BindingFlags.Public)
        ?? throw new InvalidOperationException($"{type.Name}.Normalize helper method was not found.");

    return method.Invoke(
        null,
        new object?[]
        {
            scope,
            sourceScopeMode,
            sourceFlowInstanceId,
            sourceFlowStepId,
            sourceFlowBranchId,
            sourceFlowEffectiveStatus
        })!;
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
