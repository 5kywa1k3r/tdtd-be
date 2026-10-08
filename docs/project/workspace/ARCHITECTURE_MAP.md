# Architecture Map

> Mốc điều phối hiện hành 08/10/2026: [Lam Tổng và 5 Lam phụ trách](docs/coordination/LAM_CURRENT_2026_10_08.md). Đối chiếu mốc này trước các quyết định triển khai cũ.

> Navigation: this file -> `DOCUMENTATION_MAP.md` -> `FEATURE_MAP.md` or `docs/debug/DEBUG_MAP.md`.
> Type: Architecture.

## How To Read Documentation

Start here when coming back to the project:

1. `ARCHITECTURE_MAP.md` - architecture and system boundaries.
2. `DOCUMENTATION_MAP.md` - canonical map for all first-party documentation.
3. `docs/WORK_DONE.md` - short ledger of applied feature, bug, debug, and documentation work.
4. `docs/WORK_PLANNED.md` - short ledger of upcoming implementation, verification, and cleanup work.
5. `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md` - mandatory branch, release, and commit traceability rules.
6. `FEATURE_MAP.md` - feature status and feature delivery docs.
7. `docs/AUTOMATION_TEST_PLAN.md` - business-flow automation test strategy.
8. `docs/debug/DEBUG_MAP.md` - debug plans and operational investigations.

Feature notes and debug notes are intentionally separate. Feature docs explain product/technical
delivery; debug docs explain failures, root cause analysis, verification plans, and closeout notes.

## Documentation Index

First-party markdown files in this workspace:

- `ARCHITECTURE_MAP.md`
- `DOCUMENTATION_MAP.md`
- `FEATURE_MAP.md`
- `docs/AUTOMATION_TEST_PLAN.md`
- `docs/UAT_DYNAMIC_FORM_TABLE_DYNAMIC_FLOW_CREATION.md`
- `docs/UAT_DYNAMIC_FORM_DYNAMIC_FLOW_TEST_MATRIX.md`
- `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md`
- `docs/WORK_DONE.md`
- `docs/WORK_PLANNED.md`
- `docs/features/FEATURE_02_DASHBOARD_MIND_MAP.md`
- `docs/features/FEATURE_03_RUNTIME_CONSISTENCY_AND_DOCROLE_READ_MODEL.md`
- `docs/features/FEATURE_04_DOCROLE_GETLIST_READ_MODEL_OPTIMIZATION.md`
- `docs/features/FEATURE_05_ADMIN_UNIT_USER_CATALOG_REFACTOR.md`
- `docs/features/FEATURE_06_DYNAMIC_FORM_TEMPLATE.md`
- `docs/features/FEATURE_07_LABEL_MANAGEMENT.md`
- `docs/features/FEATURE_08_AGGREGATION_WORKFLOW.md`
- `docs/features/FEATURE_09_NOTIFICATION_CENTER.md`
- `docs/features/FEATURE_10_OPERATIONS_HISTORY_AND_JOB_RUNS.md`
- `docs/features/FEATURE_11_EXCEPTION_AND_LABEL_STANDARDIZATION.md`
- `docs/features/FEATURE_12_REQUEST_CENTER_AND_ACTION_NOTIFICATIONS.md`
- `docs/features/FEATURE_13_LABEL_ATTACHMENT_CONTEXT.md`
- `docs/features/FEATURE_14_ASSIGNMENT_REPORTING_COORDINATION_REFACTOR.md`
- `docs/features/FEATURE_15_WORK_DOCUMENT_LIBRARY.md`
- `docs/features/FEATURE_16_PV01_ONCE_LARGE_SUMMARY_EXPORT.md`
- `docs/features/FEATURE_17_DYNAMIC_EXCEL_SEMANTIC_WORKBOOK_FORMULA.md`
- `docs/features/PATCH_2026_09_11_CANVAS_WORKSPACE_FOUNDATION_PLAN.md`
- `docs/features/canvas-workspace/README.md`
- `docs/features/PATCH_2026_04_29_WORKFLOW_FIXES.md`
- `docs/features/PATCH_2026_05_05_SYSTEM_ADMIN_LABEL_FORM_FIXES.md`
- `docs/features/PATCH_2026_05_06_UNIT_SCOPE_ACCOUNT_ALIGNMENT.md`
- `docs/features/PATCH_2026_05_06_ADMIN_USER_AUTH_PERFORMANCE_FIXES.md`
- `docs/features/PATCH_2026_05_07_WORK_DETAIL_CARD_HANDOVER_CLONE_PERMISSION.md`
- `docs/features/PATCH_2026_05_07_DYNAMIC_FORM_PREVIEW_ENTRYPOINTS.md`
- `docs/features/PATCH_2026_05_07_WORK_FORM_INP_AND_DETAIL_CANEDIT_FIXES.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE1_PAYLOAD_STAT_READ_MODEL_PLAN.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE2_READ_MODEL_WORK_DOCUMENTS.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE3_NOTIFICATION_REALTIME_INVALIDATION.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE4_JOB_RUN_OPS_OVERLAP_GUARD.md`
- `docs/features/PATCH_2026_05_20_MONGO_ARCHITECTURE_PHASE_CLOSEOUT.md`
- `docs/features/PATCH_2026_06_18_LARGE_PERIODIC_BASIC_SUMMARY_SNAPSHOTS.md`
- `docs/features/PATCH_2026_06_20_SUMMARY_REFACTOR_PLAN.md`
- `docs/features/PATCH_2026_07_21_DYNAMIC_FLOW_MAPPING_CONTRACT_AND_BUILDER_FIX.md`
- `docs/features/PATCH_2026_07_21_DYNAMIC_FORM_FLOW_STATISTICS_AUDIT.md`
- `docs/features/PATCH_2026_07_21_DYNAMIC_FORM_FLOW_UAT_CONTRACT_REBASE.md`
- `docs/features/PATCH_2026_07_21_DYNAMIC_FORM_FLOW_FULL_LIFECYCLE_PLAN.md`
- `docs/features/FULL_P3_DYNAMIC_FORM_RUNTIME_EXECUTION_PROMPT.md`
- `docs/features/FULL_P3_DYNAMIC_FORM_RUNTIME_IMPLEMENTATION_PLAN_2026_07_22.md`
- `docs/features/P3_LIFECYCLE_SECTION_AUDIT_2026_07_22.md`
- `docs/features/FULL_P3_DYNAMIC_FORM_RUNTIME_EVIDENCE_2026_07_22.md`
- `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_EXECUTION_PROMPT.md`
- `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_IMPLEMENTATION_PLAN_2026_07_23.md`
- `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_EVIDENCE_2026_07_23.md`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1.schema.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_1.schema.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_1.json`
- `docs/features/DYNAMIC_FORM_FLOW_TRACE_MATRIX_V1.md`
- `docs/features/DYNAMIC_FORM_FLOW_UI_ROUTE_OWNERSHIP_V1.md`
- `docs/features/DYNAMIC_FORM_FLOW_UI_GATE_TEMPLATE.md`
- `docs/features/TO_DO.md`
- `docs/debug/DEBUG_MAP.md`
- `docs/debug/DEBUG_2026_05_03_MONGO_INDEXES.md`
- `docs/debug/DEBUG_2026_05_03_ASSIGNMENT_HANDOVER_F03.md`
- `docs/debug/DEBUG_2026_05_04_DOCROLE_READ_MODEL_WRITE_PATH_REVIEW.md`
- `docs/debug/DEBUG_2026_05_04_SYSTEM_BOOTSTRAP_MU_TEST_PLAN.md`
- `docs/debug/DEBUG_2026_05_08_ASSIGNMENT_SELF_ASSIGN_SCOPE_TESTS.md`
- `docs/debug/DEBUG_2026_05_08_AUTOMATION_RANDOM_TEST.md`
- `docs/debug/DEBUG_2026_05_08_DYNAMIC_FORM_AGGREGATION_PROBE.md`
- `docs/debug/DEBUG_2026_05_08_MINDMAP_EMPTY_FILTER_REPORT_STATUS.md`
- `docs/debug/DEBUG_2026_05_08_UI_STATISTICS_FIXTURE.md`
- `docs/debug/DEBUG_2026_05_09_PRE_FINAL_TEST_READINESS.md`
- `tdtd-fe/README.md`
- `tdtd-fe/FRONTEND_COMMON_RULES.md`

Dependency documentation under `node_modules` and `.dotnet/.nuget` is not part of this index.

## Repo Overview

The repo has 2 main applications:

- `tdtd-fe`: React + Vite + TypeScript
- `tdtd-be`: ASP.NET Core + MongoDB + Hangfire + Redis + MinIO + TUS

The current business axis is still:

`work -> assignment -> report/review/aggregate -> dashboard`

Current date/status boundary as of 2026-05-20:

- Work and assignment planning use start date plus due/deadline. Legacy `endDate` or assignment
  `completedDate` must not be used as the primary planning/statistic boundary.
- Work completion and assignment completion are explicit user actions with completion metadata.
  They are the only paths that set work `S3` or assignment `Completed`.
- Reports keep their own lifecycle and period windows. Report approval feeds official statistics
  and period state, but does not complete assignment/work.
- Periodic reports are scoped by server-owned `periodStart`, `periodEnd`, and `dueAtUtc`.
  Proactive/user-created reports also persist those source-window fields, but FE must not provide
  them as business inputs; BE derives them from linked scheduled periods, assignment schedules, or
  assignment/work effective dates. Backfill completion is captured through `completedDate` when the
  historical completion-date policy requires it.
- Completed work or completed ancestor assignment locks descendant assignment/report mutations
  while keeping read/review history available.

## Change Control Boundary

Development and release control is part of the architecture boundary:

- `tdtd-fe` and `tdtd-be` are separate Git repositories.
- All feature, debug, bug, test, docs, and deploy-script work starts on `dev`.
- `prod` is the production baseline and accepts only verified `dev` merges or explicit revert
  commits.
- Production merges use `--no-ff` so each release can be reverted as one merge commit.
- Every commit must include a work code and concrete content summary, following
  `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md`.
- FE/BE cross-app work uses the same work code in both repositories.
- Files outside `tdtd-fe` and `tdtd-be`, including `deploy/`, `docs/`, and `scripts/`, are not
  captured by those app repos unless a root repository is added later.

## FE Map

### Entry, Routing, Store

Main entry points:

- `tdtd-fe/src/main.tsx`
- `tdtd-fe/src/App.tsx`
- `tdtd-fe/src/routes/appRoutes.tsx`
- `tdtd-fe/src/stores/store.ts`

Current FE state shape is still light:

- `authSlice` stores auth/session state
- `baseApi` and RTK Query store most server state
- feature-level UI state is added only where local orchestration is needed

### Core API Layer

The FE follows a shared API base with per-domain injected endpoints.

Base:

- `tdtd-fe/src/api/base/axios.ts`
- `tdtd-fe/src/api/base/axiosBaseQuery.ts`
- `tdtd-fe/src/api/base/baseApi.ts`

Main domain APIs:

- `tdtd-fe/src/api/workApi.ts`
- `tdtd-fe/src/api/workAssignmentApi.ts`
- `tdtd-fe/src/api/reportApi.ts`
- `tdtd-fe/src/api/dashboardApi.ts`
- `tdtd-fe/src/api/dashboardMindMapApi.ts`
- `tdtd-fe/src/api/adminUsersApi.ts`
- `tdtd-fe/src/api/adminUnitsApi.ts`
- `tdtd-fe/src/api/pickersApi.ts`
- `tdtd-fe/src/api/operationsApi.ts`

Consistency-sensitive FE files to watch:

- `tdtd-fe/src/types/reportStatus.ts`
- review and report screens that assume current status values
- future list/search responses if they move to `doc role` summary-driven payloads

### Dashboard Area

Existing flat dashboard:

- page: `tdtd-fe/src/pages/dashboard/DashboardPage.tsx`
- types: `tdtd-fe/src/types/dashboard.ts`
- api: `tdtd-fe/src/api/dashboardApi.ts`

Mind map dashboard:

- page: `tdtd-fe/src/pages/dashboard/mindmap/WorkMindMapPage.tsx`
- components: `tdtd-fe/src/components/dashboard/mindmap/*`
- types: `tdtd-fe/src/types/dashboardMindMap.ts`
- api: `tdtd-fe/src/api/dashboardMindMapApi.ts`
- local UI state: `tdtd-fe/src/stores/dashboardMindMapSlice.ts`

Mind map UI responsibilities are split as:

- dashboard page owns the launch button and full-screen dialog
- work picker popup owns work type/search/select before opening the graph
- mind map page owns graph orchestration and can run in `canvasOnly` mode
- canvas renders nodes and edges with React Flow
- popover loads node summary on demand
- drawers show server-paged drilldown lists
- canvas legend should be hide/show-able
- canvas legend should explain entity/card colors and, when enabled, subtle status accent colors
- card status coloring is optional and should use border/accent plus light background tint, not strong full-card fills
- template and user nodes show compact server-backed stacked-bar summaries

### FE Watchouts

- assignment create flow still has legacy contract edges and some temporary mapping patterns
- user-facing labels, validation messages, dialog titles, button text, and toast/snackbar messages
  should move behind enum-backed constants as part of F11 so later translation does not require
  searching JSX and helpers
- FE API error rendering should normalize BE `errorCode/service/message/details/traceId` responses
  through one shared helper instead of reading anonymous `message` fields directly
- dashboard flat view and mind map view are now separate flows and should stay decoupled
- `AppTable` is still flat-table oriented; mind map should continue to use canvas components instead
- notification list loading should stay lazy in the header; only unread count should be considered
  for lightweight authenticated-layout refresh

## BE Map

### Entry and Composition Root

Main backend entry point:

- `tdtd-be/Program.cs`

`Program.cs` wires:

- Mongo context and indexes
- JWT auth
- Redis
- Hangfire
- MinIO
- TUS upload
- Swagger and CORS

Index initialization is part of startup and must be treated as runtime-critical:

- index definitions live in `tdtd-be/Data/Indexes/MongoIndexInitializer.cs`
- index debug/runbook notes live under `docs/debug/*`
- current Mongo index investigation: `docs/debug/DEBUG_2026_05_03_MONGO_INDEXES.md`
- assignment handover runtime assessment: `docs/debug/DEBUG_2026_05_03_ASSIGNMENT_HANDOVER_F03.md`

### Core Domain Paths

Important models:

- `tdtd-be/Models/Work.cs`
- `tdtd-be/Models/WorkAssignment.cs`
- `tdtd-be/Models/WorkReportPeriod.cs`
- `tdtd-be/Models/WorkAssignmentReport.cs`

Main controllers and services:

- Works:
  - `tdtd-be/Controllers/WorksController.cs`
  - `tdtd-be/Services/Works/WorkServices.cs`
- Assignments:
  - `tdtd-be/Controllers/WorkAssignmentsController.cs`
  - `tdtd-be/Services/WorkAssignments/WorkAssignmentService.cs`
- Reports:
  - `tdtd-be/Controllers/WorkAssignmentReportsController.cs`
  - `tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.cs`
- Review:
  - `tdtd-be/Controllers/WorkAssignmentReviewController.cs`
  - `tdtd-be/Services/WorkAssignments/Review/WorkAssignmentReviewService.cs`
- Aggregate:
  - `tdtd-be/Controllers/WorkAssignmentAggregateTableController.cs`
  - `tdtd-be/Services/WorkAssignments/Aggregate/AggregateTableService.cs`

### Cross-Cutting Error Contract

Exception and label standardization is tracked as F11:

- `docs/features/FEATURE_11_EXCEPTION_AND_LABEL_STANDARDIZATION.md`

Target BE boundary:

- business/API failures use a central `AppErrorCode` enum, error catalog, `AppException`, and
  factory
- every API business error has stable `errorCode`, service owner, HTTP status, user message,
  developer description, optional detail, and trace id
- global middleware is the main translation point from exception to API response
- direct `InvalidOperationException`, `BadHttpRequestException`, `UnauthorizedAccessException`,
  controller `BadRequest(...)`, and anonymous `{ message }` responses are migration targets when
  they represent business/API failures

Target FE boundary:

- API errors normalize to one `ApiError` shape and render by `errorCode`
- visible UI text moves to enum-backed constants/dictionaries, with feature-level grouping when
  needed
- dynamic business labels from the Label Catalog stay server data; static UI labels stay in FE
  constants for future i18n

Consistency and permission hardening area:

- `tdtd-be/Services/WorkAssignments/WorkAssignmentService.cs`
- `tdtd-be/Services/WorkAssignmentReports/WorkAssignmentReportService.cs`
- `tdtd-be/Services/WorkAssignments/Review/WorkAssignmentReviewService.cs`
- `tdtd-be/Services/WorkAssignments/Runtime/WorkAssignmentMaterializeJobService.cs`
- `tdtd-be/Services/WorkAssignments/Queue/WorkAssignmentQueueJobService.cs`
- `tdtd-be/Services/WorkAssignments/Progress/WorkAssignmentProgressService.cs`
- `tdtd-be/Services/Common/WorkReportPeriodStatusHelper.cs`
- `tdtd-be/Services/WorkAssignments/Internal/WorkAssignmentScheduleHelper.cs`
- `tdtd-be/Services/DocRoleService.cs`
- `tdtd-be/Models/DocRole.cs`
- `tdtd-be/Services/Works/WorkServices.cs`
- `tdtd-be/Services/Works/WorkPermisson.cs`
- `tdtd-be/Data/Indexes/MongoIndexInitializer.cs`

### Dashboard Area

Existing flat dashboard:

- `tdtd-be/DashboardModel/Controllers/DashboardController.cs`
- `tdtd-be/DashboardModel/Services/DashboardOverviewService.cs`
- `tdtd-be/DashboardModel/Services/DashboardQueryService .cs`
- `tdtd-be/DashboardModel/Cache/RedisDashboardCache.cs`

Mind map dashboard:

- `tdtd-be/DashboardModel/Controllers/DashboardMindMapController.cs`
- `tdtd-be/DashboardModel/Services/DashboardMindMapQueryService.cs`
- `tdtd-be/DashboardModel/DTOs/MindMap/DashboardMindMapDtos.cs`
- registration in `tdtd-be/Program.cs`

Mind map backend rules:

- entry point is `1 work -> assignment entry nodes`
- owner/full work access starts from root assignments
- assignment assignee access can start directly from the assigned branch
- root nodes are returned first, then children are fetched lazily by `parentAssignmentId`
- node summary is loaded separately from tree structure
- drilldown lists for `units` and `reports` are server paged
- expensive user/report branches are cursor loaded and must remain bounded on the canvas

### Background Jobs and Runtime

Recurring and runtime-related entry points:

- `tdtd-be/Jobs/HangfireRecurringJobRegistrar.cs`
- `tdtd-be/Services/WorkAssignments/Queue/WorkAssignmentQueueJobService.cs`
- `tdtd-be/Services/WorkAssignments/Queue/WorkAssignmentQueueService.cs`
- `tdtd-be/Services/WorkAssignments/Runtime/WorkAssignmentMaterializeJobService.cs`
- `tdtd-be/Services/WorkAssignments/Runtime/WorkAssignmentRuntimeMaterializeService.cs`
- `tdtd-be/Services/WorkAssignments/Runtime/WorkAssignmentStatusSyncService.cs`
- `tdtd-be/Services/WorkAssignments/Runtime/WorkAssignmentStatusRepairService.cs`
- `tdtd-be/Services/WorkAssignments/Progress/WorkAssignmentProgressService.cs`

Notification runtime:

- `docs/features/FEATURE_09_NOTIFICATION_CENTER.md`
- use Mongo as the persisted per-user notification inbox
- use SignalR as online invalidation only after a Mongo notification row is persisted
- use Hangfire for bounded due scans over work, assignment, and report-period due rows
- use assignment create/activate and handover service hooks for assignment-assigned and handover
  notifications
- support work-scoped notification reads with `workId`/`workAssignmentId` filters; Work Detail uses
  this for the internal `Thông báo` tab under `Nhiệm vụ/Chỉ tiêu`
- keep notification writes as secondary read-model writes; they must not own runtime status changes

Request/action runtime:

- `docs/features/FEATURE_12_REQUEST_CENTER_AND_ACTION_NOTIFICATIONS.md`
- keep the Work Detail card launcher; add `Công việc cần thực hiện` and `Thông báo` as internal
  pages under the `Nhiệm vụ/Chỉ tiêu` card. `Công việc cần thực hiện` handles handover requests,
  clone approvals, report review reminders, evaluation reminders, status-risk alerts, and unfinished
  work/assignment/report items for the current user; `Thông báo` manages persisted notification
  rows tied to the current work and links back to the source item
- merge `Nhiệm vụ` and `Chỉ tiêu` into one assignment card content with top filters for type,
  status, assignee, and ownership; do not split the list into separate page tabs
- assignment handover is no longer a direct UI execution flow; the current assignee creates a
  pending request, the target recipient approves/rejects it, and only approval triggers the existing
  runtime handover operation
- notifications point users to pending action rows and source work/report contexts; they do not own
  request approval state
- report review and evaluation actions should deep-link to the report context by `workId` instead
  of duplicating review/evaluation forms in `Công việc cần thực hiện`

Runtime status baseline:

- `WorkReportPeriod` is the period/list/progress/dashboard status source of truth
- `WorkAssignmentReport` keeps only the report-record lifecycle
- queue scan only advances due-time transitions and disables terminal/overdue queue rows
- queue scans are capped and log summary counts only when rows are scanned
- materialize job scans are batch-limited and log processed/failed counts plus retry/dead-letter
  metadata on failures
- notification due scans are bounded over work, assignment, and report-period due rows and persist
  `NOTIFICATION_DUE_SCAN` operation logs with scanned/created counts plus failure detail
- recurring assignment materialization is rolling-window based; default horizon is 3 days via
  `WorkAssignmentMaterialize:RollingWindowDays`, and unchanged existing periods are skipped.
  Cron, max jobs, and batch size are configurable through `WorkAssignmentMaterialize:Cron`,
  `MaxJobsPerRun`, and `BatchSize`.
- list-read projection guards only check for existing rows and log missing projection state; broad
  repair is an explicit operation, not a normal list-read side effect
- `getlist` must not become a hidden repair pipeline. Current list APIs return current projection
  data only when projection rows are missing.
- `get detail` may do best-effort micro-repair for the exact source document loaded, but repair
  errors are logged and must not fail the detail response.
- job-run failures are logged with retry/dead-letter context; DocRole projection failures enqueue a
  scoped projection retry job, while automatic full projection repair is deliberately avoided.
  The admin operation UI exposes job-run state and retry queues under `/operations`.
- `WorkAssignmentProgressService` rolls assignment status from active runtime periods, including
  `ONCE` assignments and `OverdueApproved`
- `WorkAssignmentStatusSyncService` rolls assignment parents and `Work.Status` from assignment
  progress snapshots

## Doc Role Read Model

Current `DocRole` responsibilities:

- access check by `docType + docId + userId`
- role presence check
- accessible id lookup

Current main files:

- `tdtd-be/Models/DocRole.cs`
- `tdtd-be/Services/DocRoleService.cs`
- `tdtd-be/Services/Common/DocRoleReadModelProjectionService.cs`
- `tdtd-be/Services/Common/DocRoleReadModelProjectionResilientService.cs`
- `tdtd-be/Services/Common/DocRoleReadModelProjectionRetryJobService.cs`
- `tdtd-be/Services/Common/DocRoleReadModelFreshnessService.cs`
- `tdtd-be/Services/Common/DocRoleReadModelRepairService.cs`
- `tdtd-be/Services/Common/DocRoleReadModelDriftService.cs`
- `tdtd-be/Services/Common/WorkStatusOperationLogService.cs`
- `tdtd-be/Services/Common/UserActionLogService.cs`
- `tdtd-be/Services/Common/JobRunManagementService.cs`
- `tdtd-be/Services/Works/WorkPermisson.cs`
- `tdtd-be/Services/Works/WorkServices.cs`

Current implemented direction:

- keep current `DocRole` stable for access checks and compatibility
- use purpose-built flattened read models for `getlist/search`
- split read models by list surface, not one broad domain model:
  - `WorkListDocRole`
  - `AssignmentListDocRole`
  - `MyReportTemplateListDocRole`
  - `MyReportPeriodListDocRole`
  - `ReviewReportListDocRole`
- keep `get by id` and detail reads on source documents
- use one active `user + business scope` row with `roles[]` to avoid duplicate list rows
- keep detail-only fields out of list projections
- query paths have been migrated in small slices; operations history and job-run UI are tracked in
  `docs/features/FEATURE_10_OPERATIONS_HISTORY_AND_JOB_RUNS.md`

The detailed roadmap is tracked as F04 in
`docs/features/FEATURE_04_DOCROLE_GETLIST_READ_MODEL_OPTIMIZATION.md`.

Current implementation state:

- read-model collections and indexes exist for work, assignment, my-report template, my-report
  period, and review report list projections
- `WorkServices.SearchAsync` reads from `WorkListDocRoles`
- work search reads only current list projections and logs missing projection state instead of
  running hidden rebuilds
- `WorkAssignmentService` list/candidate paths read from `AssignmentListDocRoles`
- assignment list projection keeps branch visibility through a dedicated
  `ASSIGNMENT_BRANCH_VIEWER` read role for parent owners/assignees
- assignee report template list reads from `MyReportTemplateListDocRoles`
- assignee report period search reads from `MyReportPeriodListDocRoles`
- report list rows expose `periodStatus` and `reportStatus` separately so period state and report
  record lifecycle stay distinct
- review child list reads from `AssignmentListDocRoles` and enriches latest report state from
  `ReviewReportListDocRoles`
- review summary and flat report list read from `ReviewReportListDocRoles`
- review report projections omit report body/detail payload; they keep only list-visible feedback
  fields needed by the current review table
- `WorkReportPeriodStatusHelper` centralizes overdue/terminal/waiting-review buckets, queue-active
  eligibility, draft/submitted/approved transitions, due-scan transitions, and risk ranking
- DashboardModel bucket grouping uses the shared helper; exact per-status counters and label/color
  mappings remain local presentation logic
- list APIs do not run hidden projection rebuilds; missing list projections are logged and the
  current projection data is returned
- internal DocRole repair endpoints are dry-run by default, capped, scoped by
  work/assignment/period, and log planned/rebuilt/failed counts
- internal DocRole drift diagnostics are read-only, capped, scoped by
  work/assignment/period/user, and check all five list projections for duplicate, missing, orphan,
  stale, field mismatch, role-set mismatch, template aggregate mismatch, and reviewer-set mismatch
- internal operation-log search/detail endpoints expose `work_status_operation_logs` for current
  job, repair, drift, status transition, and handover records
- main write flows rebuild the relevant DocRole list projections after source writes, but source and
  projection writes are not atomic. If projection fails after source commit, the source remains
  authoritative; the resilient projection wrapper enqueues a scoped retry job and rethrows, so the
  list projection can be stale until retry, detail micro-repair, or scoped internal repair rebuilds
  it. The write-path review is recorded in
  `docs/debug/DEBUG_2026_05_04_DOCROLE_READ_MODEL_WRITE_PATH_REVIEW.md`.
- Hangfire recurring jobs `docrole:projection-retry:day` and `docrole:projection-retry:night`
  process `docrole_read_model_projection_retry_jobs`; daytime runs default to once per hour, while
  night runs start at 22:00 local time and default to every 5 minutes. The internal manual endpoint
  is `POST /api/internal/status-repair/docroles/projection-retry/process?maxJobs=20`.
- Hangfire internal execution history is operational data, not business state. Weekly
  `hangfire:history-archive` runs after 22:00 Sunday, archives only old `Succeeded` execution
  history to MinIO, and expires those Hangfire jobs through official storage APIs so
  Hangfire.Mongo can remove `jobGraph`/`stateHistory`. `Failed` jobs are retained for sys-admin
  review. Successful job expiration is extended to 15 days so weekly archive can run before normal
  Hangfire.Mongo expiration deletes those records.
- report/review/queue/handover flows sync assignment/work rollups before rebuilding report-period
  projections that copy rollup fields
- `work_status_operation_logs` persists report/review status transitions, queue scans,
  materialize scans/jobs, notification due scans, assignment/work rollup sync, handover, DocRole
  projection retry scans, internal repair, and drift checks
- current `DocRole` still owns detail permission checks and ACL compatibility

Operations backlog:

- detail-scoped projection freshness checks are implemented through
  `DocRoleReadModelFreshnessService` on detail paths only; repair is best-effort, scoped to the
  exact loaded source document, logged, and never fails the response
- initial bounded internal repair/dry-run operations are implemented for DocRole read models
- initial bounded internal drift diagnostics are implemented for DocRole read models
- scoped DocRole projection retry worker is implemented for failed post-source projection writes
- core status-chain, job-run, projection retry, repair, and drift operation logs exist with an
  internal query API; the remaining work is the admin/operator UI, retention/audit policy, and
  operational workflow
- future job-run management UI, projection retry queue UI, broader operations management,
  drift-check operational extensions, and optional distributed repair lock are tracked in
  `docs/features/TO_DO.md`

## Admin Catalog Area

Admin unit/user management now follows the F05 catalog refactor:

- Unit type is the business classifier through `Unit.PrimaryUnitTypeCode`.
- Positions are persisted in a `Position` catalog and are attached to unit type codes.
- Unit and user active/deleted state still uses `isDeleted`; no separate user/unit status catalog
  exists.
- `Unit.IsVirtual` marks grouping-only units. Normal pickers keep virtual unit ids, while assignment
  save/update expands selected virtual units server-side by unit `code` prefix to non-virtual,
  non-deleted descendants before resolving `mu_*` management accounts.
- Management account naming/provisioning is centralized in `ManagementAccountConvention` and
  `ManagementAccountProvisioner`.
- `SYSTEM_ADMIN` authorization accepts either the role or `accountKind = SYSTEM_ADMIN`.
  Generated `ml_{level}` accounts are no-unit, level-wide accounts; Unit tree/search derives the
  managed level from the generated username and returns entry units at that level plus lower
  descendants. Prefix search accepts empty/missing prefix for global or level-wide tree loading.
  Generated `mu_*`/`ml_*` accounts use default password `123456@Aa` when provisioned; pre-patch
  generated accounts with old random hashes require one admin reset.
- Work creation is a generated-management-account action: only `mu_*` and `ml_*` accounts should
  create TASK/INDICATOR records. Assignment create defaults to selecting unit ids, which resolve to
  the selected units' generated `mu_*` accounts.
- Unit/User import supports `.xlsx` and `.csv` through `AdminImportController`; CSV parsing is
  first-party and `.xlsx` uses `ClosedXML`.
- The FE admin entry is `tdtd-fe/src/pages/admin/AdminAccountPages.tsx`, with tabs for unit types,
  evaluation templates, units, and users. The old `/evaluation-templates` route redirects to the
  admin tab.

Current main files:

- `tdtd-be/Models/UnitType.cs`
- `tdtd-be/Models/Position.cs`
- `tdtd-be/Models/Unit.cs`
- `tdtd-be/Services/UnitTypeAdminService.cs`
- `tdtd-be/Services/PositionAdminService.cs`
- `tdtd-be/Services/UnitService.cs`
- `tdtd-be/Services/UserAdminService.cs`
- `tdtd-be/Services/AdminImportService.cs`
- `tdtd-be/Services/UnitSelectionService.cs`
- `tdtd-be/Services/ManagementAccountProvisioner.cs`
- `tdtd-be/Controllers/UnitTypesController.cs`
- `tdtd-be/Controllers/PositionsController.cs`
- `tdtd-be/Controllers/AdminImportController.cs`
- `tdtd-fe/src/pages/admin/AdminAccountPages.tsx`
- `tdtd-fe/src/features/admin/unitTypes/UnitTypesPanel.tsx`
- `tdtd-fe/src/features/admin/units/UnitsPanel.tsx`
- `tdtd-fe/src/features/admin/users/UsersPanel.tsx`

## Operations Area

Operations history and job-run management are described in F10:

- `docs/features/FEATURE_10_OPERATIONS_HISTORY_AND_JOB_RUNS.md`

Current main files:

- `tdtd-be/Controllers/AdminOperationsController.cs`
- `tdtd-be/Services/Common/UserActionLogService.cs`
- `tdtd-be/Services/Common/JobRunManagementService.cs`
- `tdtd-be/Models/UserActionLog.cs`
- `tdtd-be/Models/UserActionLogRetryJob.cs`
- `tdtd-fe/src/api/operationsApi.ts`
- `tdtd-fe/src/pages/operations/OperationsPage.tsx`

Rules:

- job-run management is backend-guarded for `SYSTEM_ADMIN` only
- action history is scoped by unit ownership for `MANAGER_UNIT:*`, by subtree or generated level
  scope for `MANAGER_LEVEL`, and globally for `SYSTEM_ADMIN`
- operation lists are server-paged and should stay lazy-loaded by active tab, unit, user, and page
- action-history logging is a secondary write with retry and must not roll back the source workflow

## Mind Map Data Flow

1. User clicks the mind map button on dashboard.
2. FE opens a popup to select work type and one work.
3. FE opens a full-screen canvas-only mind map view.
4. FE requests assignment entry nodes for that work.
5. FE renders the work node and the first bounded assignment page.
6. User expands a node and FE requests the next assignment/template/user/report page lazily.
7. User opens assignment detail and FE requests stacked-bar summary for that node.
8. User clicks a stacked-bar segment and FE requests a paged drawer list for units or reports.
9. User/report dropdown filters reset only the affected branch and do not reload the full tree.
10. Template/user node stacked bars are aggregated server-side and reused by the canvas.

## Notes for Future Features

- Read documentation through `DOCUMENTATION_MAP.md` first, then choose `FEATURE_MAP.md` or
  `docs/debug/DEBUG_MAP.md`.
- Use `docs/WORK_DONE.md` and `docs/WORK_PLANNED.md` for quick status review before opening long
  feature/debug docs.
- Keep feature-specific design or delivery notes in `docs/features/*`.
- Keep debug plans, incident notes, and operational investigations in `docs/debug/*`.
- Keep `FEATURE_MAP.md` as the aggregate index, not as the only place for long delivery notes.
- If a new dashboard feature is work-centric and tree-like, prefer extending the mind map module instead of bending the flat dashboard.
- For Dynamic Form/report-template migration, use `docs/features/FEATURE_06_DYNAMIC_FORM_TEMPLATE.md`.
  The first implementation slice must audit TOP/LEFT/MATRIX `values1D` indexing before schema work,
  every report assignment, including periodic assignments, may have user-created ad-hoc report
  instances, legacy `ONCE` should map into that model, `periodInstanceKey`/period id must be the
  identity instead of `periodKey`, and dashboard tree statistics should read aggregate-level
  projections instead of grouping dynamic field rows on every request.
- Dynamic Form blocks must not duplicate Dynamic Excel workbook UI/UX. Keep `rawWorkbookDataJson`
  and `specJson` on `DynamicExcelTemplate`, store compact block contracts on Dynamic Form, and use
  `tableValuesJson.blocks[]` as the primary runtime table data source.
- Full-P3 Dynamic Form runtime binds every report to the exact published form id/family/version/schema
  hash and keeps external payload as canonical body. `WorkAssignmentReportSection` is a source-revision/
  hash projection with monotonic repair and canonical fallback, including Advanced Summary reads.
  Payload and lifecycle revisions are independent CAS tokens with replay-safe command ids. A lifecycle
  CAS embeds its durable outbox entry in the same report-document update; foreground/retry projectors use
  deterministic event keys for eventual exactly-once period/log/queue/statistic/read-model effects.
  `COMMITTED_PENDING_PROJECTION` is a committed aggregate awaiting projection, not a failed mutation.
  This design is not a shared Mongo multi-document transaction and does not promise synchronous rollback.
- At the `FULL-P3-DF-RUNTIME` closeout, capability catalog v1.1 is append-only over v1.0 and marks exactly
  10 field types, six value sources and
  five table modes `SUPPORTED`. Its catalog SHA-256 is
  `e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf`; schema SHA-256 is
  `1783a21e1edb731002d5907a3405c829cdd00ec9f9e496264ff8543dc8f95bf8`. Flow definition,
  mapping, topology and statistics executors remain deferred; P3 completion must not be described as full
  BPMN or full Dynamic Form/Flow lifecycle completion.
- `FULL-P3-DF-RUNTIME` achieved its release gate on 2026-07-22: backend `178/178`, two deterministic
  API/Kestrel/Mongo runs of `139/139 DAT`, FE `86/86`, production `dist-final5`, real-browser four-actor/
  lifecycle/table/conflict/mobile coverage with `networkMockCount=0`, and clean fixture teardown. That historical
  closeout stopped before P4; the completed P4 definition gate is recorded below.
- `FULL-P4-FLOW-DEFINITION` achieved its definition release gate on 2026-07-23 and was re-attested by the final
  clean/deliberate-failure gates recorded below. The canonical logical aggregate
  is split between `dynamic_flow_templates` family metadata
  (`familyRevision`, owner/root/origin/current/archive state) and `dynamic_flow_template_versions`: a mutable
  draft is guarded by `draftRevision + payloadHash`, while a locked version is an immutable canonical schema-v2
  snapshot with catalog/Form pins, origin, and execution eligibility.
- Flow create/save/lock/reopen/clone/archive/delete and related multi-document definition commands run through
  `DynamicFlowDefinitionTransactionRunner`. Family, version, unique `DynamicFlowDefinitionCommandReceipt`, and
  receipt-keyed `UserActionLog` success audit commit or roll back together. The idempotency key hashes the raw
  canonical request before Form/domain lookups, so an early historical replay is independent of later mutable
  domain state. Receipts keep SHA-verified, bounded family plus direct-version response snapshots; replay returns
  the historical result while recomputing current permission/`isUsed` metadata and rechecking the active actor.
  Snapshot tampering fails `409` with zero writes. P4 has no fallback to non-transactional multi-write.
- Clone uses exact source CAS over `sourceVersionId + sourceDraftRevision + sourcePayloadHash`; wildcard grants
  accept only the literal `"*"` and treat null/blank as deny.
- P4 definition validation is fail-closed: a versioned legacy adapter produces strict schema v2; unknown canonical
  fields are rejected; topology/archetype/owner/gateway rules and budgets are enforced (200 nodes, 400 edges,
  1,048,576 UTF-8 bytes, condition depth 12 and 128 AST nodes); catalog v1.1 and exact published Form versions are
  pinned; actor, scalar-field and table-endpoint policy coverage must be complete before lock. Mapping metadata is
  validated at definition time, but mapping evaluation/apply remains a P7 capability. Final coverage includes
  150 fixtures with the boundary case at offset 125, 36 real API topology/policy cases, and fault injection across
  six operations, 46 transaction boundaries, and 58 requests.
- A draft response with `definitionLockable=false` is not a pre-lock validation verdict and must not be used by
  the client to disable Lock. The backend lock command is authoritative and strict-validates the current payload;
  only a successfully locked snapshot attests `definitionLockable=true`.
- Flow reads and actions are server-scoped before detail-bearing validation: owner/admin can manage, an
  exact-version participant can read and hold a separate execute grant, and an outsider receives a generic
  non-leaking denial; the owner can still validate its own draft. Hidden and missing Forms are indistinguishable,
  and locked-version integrity is rechecked before use. An execute grant never overrides P4 runtime eligibility.
  Both canonical and legacy `REQUIRES_REVIEW` definitions remain blocked from P7 preview/apply with `409` and zero
  writes. Locked P4 definitions remain `executionEligibility=BLOCKED_UNTIL_TARGET_PHASE` with exact P5/P6 (and
  `statisticProfile` P8) metadata and `canExecute=false`.
- Startup migration is a transactional manifest state machine with `PREPARED`, `APPLIED`, and `ROLLED_BACK`
  states, family phase 1 and version phase 2, and reverse phase-2-before-phase-1 rollback. Before/after hashes,
  drift refusal, exact rollback, rerun/reapply, and a 14 MiB BSON manifest cap are enforced. The oversize legacy
  fallback keeps the raw payload unchanged while marking it `REQUIRES_REVIEW`, `definitionLockable=false`, and
  runtime-blocked; it does not silently rewrite payload bytes.
- The FE definition workspace preserves invalid/unapplied raw JSON across tabs, performs stable-ID three-way
  merge, blocks Save until conflicts are resolved, routes structured errors to the owning tab/control, and keeps
  archive/reopen/clone affordances aligned with server permissions.
- Phase ownership remains explicit: P4 makes definitions lockable but not executable; P5 owns instance/step state,
  multi-unit launch and runtime; P6 owns topology/gateway execution; P7 owns mapping execution, raw-source
  authorization and separator runtime parity; P8 owns the versioned statistics config/profile executor.
  Work stopped before P5.
- The final P4 backend gate passed fast `182/182`. Artifact
  `.p1-artifacts/backend-integration/p1_20260723061327_226784_36d93bb8` contains two clean runs of `201/201 DAT`
  each, including the exact 60 definition cases (`FLOW-CRUD` 12 + `FLOW-TOPO` 20 + `FLOW-POL` 16 +
  `FLOW-PERM` 12), with normalized SHA
  `7a14439235ca8af077b08b5c62eab1b7f2a03667d8e2c074b6b22937b305c315` in both iterations and successful
  cleanup. Deliberate-failure artifact `.p1-artifacts/backend-integration/p1_20260723061602_57984_1fae6043`
  exited `1`,
  recorded root `passed=false`, `201 DAT + 1 KHONG_DAT`, and still cleaned up successfully.
- Catalog v1.1 regenerated/validated at
  `e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf`, with two immutable versions and
  21 P3-supported capabilities. FE application/test typechecks passed, scoped ESLint passed `19/19`, and
  Vitest passed `25/25` files and `135/135` tests. Production artifact is
  `tdtd-fe/.build/p4-production-final5-20260723` (`159` files, `7,410,952` bytes; Vite `7.2.4`,
  `9,133` modules). Production Browser/Kestrel/Mongo runs
  `p1_20260723011327_96688_3f13ce35`, draft-only projection run
  `p1_20260723021913_130344_c812b9bd`, and supplemental route/responsive/topology/barrier run
  `p1_20260723031909_217396_dd262c2b` (Run C normalized SHA
  `be2defce9b1be7153e0e4c4a366650ca08067cda4b86ffea1255f811320d7c5c`) used
  `networkMockCount=0` and all cleaned up. Combined coverage
  includes owner/admin/participant/outsider, legacy redirect, desktop/mobile, 2-node P6, 200/400 graph,
  201 rejection, `REQUIRES_REVIEW`, and the P8 Lock barrier without enabling runtime. Direct Mongo
  reconciliation proved receipt/success/linked-audit/orphan counts `9/9/9/0`, `4/4/4/0`, and
  `4/4/4/0` respectively. No browser call was made after Run C and no Run D was created; post-Run-C hardening is
  attested by the final automated gates above. See
  `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_EVIDENCE_2026_07_23.md`; Run C machine evidence is
  `.p4-artifacts/browser-uat/20260723/P4_PRODUCTION_BROWSER_RUN_C_2026_07_23.json`.
- Large Dynamic Excel blocks inside Dynamic Forms may be assigned periodically, but periodic runtime
  jobs are metadata jobs for periods, reminders, and queue state. They must not parse large
  `tableValuesJson` payloads to compute Basic summary values. Large Basic summary is lazy,
  method-scoped, rejects cumulative-to-period, and uses separate snapshots per effective method
  config/source window. See
  `docs/features/PATCH_2026_06_18_LARGE_PERIODIC_BASIC_SUMMARY_SNAPSHOTS.md`.
- Summary refactor direction after the large Basic slice: Basic summary remains fixed-scope and
  removes per-method data ranges; Advanced summary is the only surface for data range + method +
  condition logic. Advanced configs are section-scoped, immutable after lock, versioned, job-backed,
  and governed by token/quota/admin controls. Cumulative Advanced summary uses day/month/year nodes
  instead of long prefix snapshots. Initial historical/backfill entry remains valid; post-approval
  mutation, config changes, and broad historical rebuilds are controlled operations. See
  `docs/features/PATCH_2026_06_20_SUMMARY_REFACTOR_PLAN.md`.
- Mongo Phase 1 payload/stat/read-model direction lives in
  `docs/features/PATCH_2026_05_20_MONGO_PHASE1_PAYLOAD_STAT_READ_MODEL_PLAN.md`. Dynamic
  Form/Excel templates and assignments are immutable once used, so template ids act as version
  identities and reports should not copy full schema snapshots. User/unit/position/catalog display
  fields stored in report/stat/log rows are historical snapshots and must not be rewritten when
  master data changes. Keep report headers lean, move large runtime payloads behind payload/table
  collections, and use statistic/read projections as the fast query surface.
- Mongo Phase 2 denormalization/read-model direction starts with the work-document list path in
  `docs/features/PATCH_2026_05_20_MONGO_PHASE2_READ_MODEL_WORK_DOCUMENTS.md`: keep `FileDoc`
  normalized for now, reuse existing file metadata and `AssignmentListDocRole`, push deterministic
  list filters into Mongo, and bulk-load access state instead of adding a new summary collection
  before there is production-scale evidence.
- Mongo Phase 3 notification/socket direction lives in
  `docs/features/PATCH_2026_05_20_MONGO_PHASE3_NOTIFICATION_REALTIME_INVALIDATION.md`: Mongo
  `notifications` is the source of truth, SignalR is transient invalidation only, and both create
  plus read-state mutations must refresh UI from API state across tabs/devices before adding any
  broker or outbox.
- Mongo Phase 4 job-run/ops direction lives in
  `docs/features/PATCH_2026_05_20_MONGO_PHASE4_JOB_RUN_OPS_OVERLAP_GUARD.md`: recurring Hangfire
  jobs should run through an overlap-guarded wrapper, while queue row leases/retries stay in the
  domain services and broader manual-endpoint/distributed locking waits for concrete operations
  pressure.
- The 2026-05-20 Mongo architecture pass closeout lives in
  `docs/features/PATCH_2026_05_20_MONGO_ARCHITECTURE_PHASE_CLOSEOUT.md`.
- Official statistic projections and rollups are approve-only. Draft and submit update source/list
  state; approve extracts values into rollup/read models; recall/return after approve must reverse
  or rebuild the contribution. Dynamic Form statistic repair/rebuild batches rewrite per-report
  value rows first, then rebuild aggregates once per distinct
  `workId + periodInstanceKey + dynamicFormTemplateId`.
- Label display metadata is read live from the label catalog. Label deactivate/delete/reactivate
  enqueues affected Dynamic Form statistic rebuild jobs; rebuild keeps only active, non-deleted
  row-label codes so dashboard/summary aggregates eventually drop stale label contributions.
- Report contribution is not scope. Scope limits which branch is read; report-owned
  `cumulativeContributionMode` and optional `cumulativeContributionPolicyJson` decide whether the
  approved report, field, table metric, or label contributes to statistics. This prevents a parent
  summary report from double-counting child reports that already rolled up.
- Dynamic Form aggregate-to-report materialization belongs to the report service, not the aggregate
  preview service. The aggregate service calculates rows; `WorkAssignmentReportService` decides
  whether the target draft can be written, records `summarySourceJson`, and applies contribution
  defaults/policy before normal draft save.
- The old `api/work-assignment-aggregate/view` runtime workbook aggregation surface is removed. Keep
  `work-assignment-aggregate-table/table` only as the active legacy fixed-grid Dynamic Excel
  compatibility endpoint while Dynamic Form uses table-mode metric aggregation.
- Persisted user-created/proactive report periods use `PeriodKind = USER_CREATED`; "proactive" is a
  product/UI label. Approval transitions must enforce chronological order within the same
  assignment/template/report lane.
- User-created/proactive report creation is a server-owned temporal contract: FE sends `reportDate`
  and optional title/link only, while BE derives `PeriodStart`, `PeriodEnd`, `StartedDate`, and
  `DueAtUtc`. Report draft/submit may carry `CompletedDate` only when the historical
  completion-date policy allows/requires it.
- For transaction/state-machine/runtime hardening, use `docs/features/FEATURE_03_RUNTIME_CONSISTENCY_AND_DOCROLE_READ_MODEL.md`
  as the planning note.
- For admin catalog cleanup, use `docs/features/FEATURE_05_ADMIN_UNIT_USER_CATALOG_REFACTOR.md`:
  `SYSTEM_ADMIN` is an `accountKind` without position, and Position management plus Unit Type
  Position Rules/quotas remain planned.
- For DocRole getlist/read-model history and operations rules, use
  `docs/features/FEATURE_04_DOCROLE_GETLIST_READ_MODEL_OPTIMIZATION.md`.
- For Mongo index startup/debug work, use `docs/debug/DEBUG_2026_05_03_MONGO_INDEXES.md`.
- For production Mongo clean/seed with the `mu_pv01` BT01-BT25 package, use
  `docs/PROD_DB_SEED_RUNBOOK.md`; the destructive clean command must keep its explicit
  `DROP_TDTD_DB` confirmation.
- For assignment assignee handover/runtime ownership changes, use
  `docs/debug/DEBUG_2026_05_03_ASSIGNMENT_HANDOVER_F03.md` before patching.
- For in-app notifications, use `docs/features/FEATURE_09_NOTIFICATION_CENTER.md`; v1 is Mongo
  persisted + SignalR invalidation + Hangfire due scan + RTK Query lazy-load. RabbitMQ/Kafka stay
  deferred until notification becomes an event bus or multi-consumer delivery pipeline.
- For request/action workflows, use
  `docs/features/FEATURE_12_REQUEST_CENTER_AND_ACTION_NOTIFICATIONS.md`; direct handover execution
  from Assignment UI is superseded by recipient-approved handover requests.
- For operations history and job-run management, use
  `docs/features/FEATURE_10_OPERATIONS_HISTORY_AND_JOB_RUNS.md`; job-run actions are
  `SYSTEM_ADMIN` only and action-history reads must stay unit/user/page scoped.
