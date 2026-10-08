# Feature 10 - Operations History And Job Runs

> Navigation: `ARCHITECTURE_MAP.md` -> `DOCUMENTATION_MAP.md` -> `FEATURE_MAP.md` -> this file.
> Type: Feature.

## Status

Applied on 2026-05-04. Job-run scheduling/statistic rebuild tuning was updated on 2026-05-07;
label lifecycle rebuild and notification due-scan operation logging were aligned on 2026-05-08.

This feature adds two operation surfaces:

- job-run management for system administrators only
- user action history for work, assignment, handover, report submit, report approve, and report
  return events

## Permission Contract

Job-run management:

- only `SYSTEM_ADMIN` can query job-run logs, job queues, or manually process retry queues
- `ADMIN`, `MANAGER_LEVEL`, and `MANAGER_UNIT:*` are rejected by the backend guard

User action history:

- `SYSTEM_ADMIN` can see all action logs
- `MANAGER_UNIT:*` can see logs for its exact unit
- anchored `MANAGER_LEVEL` users with `UnitId` can see logs for their subtree, using unit code
  prefix plus level
- generated level-wide `ml_{level}` style accounts can see logs across the whole level and lower
  levels, without a fixed `UnitId`

The implementation follows the existing account conventions from F05:

- role enum/type stays `SYSTEM_ADMIN`, `MANAGER_LEVEL`, and dynamic `MANAGER_UNIT:{unitId}`
- generated management accounts are interpreted through `ManagementAccountConvention`

## Backend Surface

Controller:

- `tdtd-be/Controllers/AdminOperationsController.cs`

User action history endpoints:

- `GET /api/admin/operations/action-logs`
- `GET /api/admin/operations/action-logs/{id}`

Job-run endpoints:

- `GET /api/admin/operations/job-runs/operation-logs`
- `GET /api/admin/operations/job-runs/operation-logs/{id}`
- `GET /api/admin/operations/job-runs/materialize-jobs`
- `POST /api/admin/operations/job-runs/materialize-jobs/process`
- `POST /api/admin/operations/job-runs/queue-daily-scan/process`
- `POST /api/admin/operations/job-runs/notification-due-scan/process`
- `GET /api/admin/operations/job-runs/projection-retry-jobs`
- `POST /api/admin/operations/job-runs/projection-retry-jobs/process`
- `GET /api/admin/operations/job-runs/action-log-retry-jobs`
- `POST /api/admin/operations/job-runs/action-log-retry-jobs/process`
- `GET /api/admin/operations/job-runs/statistic-rebuild-jobs`
- `POST /api/admin/operations/job-runs/statistic-rebuild-jobs/process`

All list endpoints are server-paged and support narrow filters. Action history supports `unitId`
and `userId` filters so heavy views can be lazy-loaded by unit or user. Job-run lists expose status,
action/operation, work, assignment, report period, user, text query, and active/inactive filters
where relevant.

Queue-backed job rows expose run status:

- `Pending` / `PENDING`: not run yet or waiting for next scheduled run
- `Running` / `RUNNING`: currently claimed by a worker lease
- `RetryWaiting` / `RETRY_WAITING`: failed and waiting for retry
- `Completed` / `COMPLETED`: completed
- `DeadLetter` / `DEAD_LETTER`: failed after retry budget

Operation log rows are historical run records and use result values such as `SUCCESS`,
`PARTIAL_FAILED`, `FAILED`, and `SKIPPED`.

## Action History Model

Collections:

- `user_action_logs`
- `user_action_log_retry_jobs`

Captured actions:

- `WORK_CREATED`
- `ASSIGNMENT_CREATED`
- `ASSIGNMENT_HANDOVER`
- `REPORT_SUBMITTED`
- `REPORT_APPROVED`
- `REPORT_RETURNED`

Each action log stores a compact denormalized snapshot for query/display:

- actor user, target user, from/to users where applicable
- user ids and user unit snapshots involved in the event
- unit scopes derived from involved users
- work, assignment, report period, and report ids/status metadata
- summary, result, action scope, and occurrence time

## Retry Behavior

The action-history write path is intentionally secondary to the business mutation:

1. Main workflow commits first.
2. `UserActionLogService.RecordAsync` attempts to build and insert the action log.
3. If action-log insertion fails, the main workflow is not rolled back.
4. A compact retry job with the seed payload and dedupe key is persisted.
5. Hangfire runs `user-action-log:retry` to rebuild missing action logs from the seed and current
   source documents.
6. Retry jobs use bounded attempts, lease fields, backoff, last-error fields, and dead-letter state.

This mirrors the F04 read-model retry pattern: the source workflow remains primary, and secondary
logs/read models are retried from durable minimal payloads.

## Recurring Job Tuning

Current recurring defaults after the 2026-05-07 operations tuning:

| Job id | Default schedule | Default batch |
| --- | --- | --- |
| `hangfire:history-archive` | `HangfireHistoryArchive:Cron=30 22 * * 0` | `SucceededRetentionDays=7`, `SucceededExpirationDays=15`, `MaxJobsPerRun=30000` |
| `work-assignment:queue-daily-scan` | `WorkAssignmentQueue:LocalHour=0`, `WorkAssignmentQueue:LocalMinute=10` | cap `2000` queue rows per service run |
| `work-assignment:materialize-scan` | `WorkAssignmentMaterialize:Cron=*/1 * * * *` | `MaxJobsPerRun=5`, `BatchSize=20` |
| `notifications:due-scan` | `Notifications:DueScanCron=*/5 * * * *` | caps: work `1000`, assignment `1000`, report period `2000` |
| `docrole:projection-retry:day` | `DocRoleProjectionRetry:DayCron=17 6-21 * * *` | `DayMaxJobsPerRun=5` |
| `docrole:projection-retry:night` | `DocRoleProjectionRetry:NightCron=*/5 22-23,0-5 * * *` | `NightMaxJobsPerRun=20` |
| `dynamic-form:statistic-rebuild` | `DynamicFormStatisticRebuild:Cron=0 0 * * *` | `MaxJobsPerRun=3`, `BatchSize=25` |

Notes:

- Projection retry runs light during 06:00-21:59 and drains more aggressively from 22:00 through
  05:59 local time. `DocRoleProjectionRetry:ScheduleMode=Single` keeps compatibility with the old
  `docrole:projection-retry` id and `Cron`/`MaxJobsPerRun` settings.
- Hangfire history archive runs weekly after 22:00 local time on Sunday. It archives only
  non-failed terminal history, defaulting to `Succeeded` jobs older than 7 days. `Failed` jobs are
  intentionally left in Hangfire for system-admin investigation. The job writes compressed JSONL to
  MinIO under `HangfireHistoryArchive:ObjectPrefix`, then marks those jobs expired through Hangfire
  storage APIs so Hangfire.Mongo removes `jobGraph` and matching `stateHistory`. It does not query
  or delete application business collections. A small Hangfire state filter keeps successful jobs'
  built-in expiration at 15 days so the weekly archive job has time to export them before
  Hangfire.Mongo's normal expiration manager removes them.
- Materialize remains rolling-window based; cron, max jobs, batch size, and rolling record count
  (`WorkAssignmentMaterialize:RollingWindowCount`) are configurable without code changes.
- System administrators can run deterministic local/test cycles through the synchronous process
  endpoints for materialize, queue due scan, notification due scan, projection retry, action-log
  retry, and statistic rebuild. These endpoints execute the underlying service immediately instead
  of waiting for the Hangfire recurring timer.
- `scripts/AutomationRandomTest` exposes the same deterministic cycle as a sequential accelerator:
  `dotnet run --project scripts\AutomationRandomTest\AutomationRandomTest.csproj -- --job-run-sequence http://localhost:5164`.
  The runner calls one process endpoint at a time, waits for it to finish, and records call order,
  status, processed counts, and recent operation logs. Test fixtures should use this path instead
  of changing cron values or running process endpoints in parallel.
- 2026-05-20 Phase 4 update: recurring Hangfire jobs are registered through
  `NonOverlappingRecurringJobRunner`, whose methods use `DisableConcurrentExecution`. This prevents
  a recurring job from overlapping with a previous invocation while keeping job ids and service
  contracts unchanged. Manual system-admin process endpoints still call domain services directly;
  cross-path locking is deferred until there is concrete operator or multi-node contention.
- Dynamic Form statistic rebuild keeps the monthly creator limit and midnight schedule. The
  operation is intentionally rare; `SYSTEM_ADMIN` can still trigger high-priority manual rebuilds
  when needed.
- Statistic rebuild batches now rewrite value rows first, collect distinct
  `workId + periodInstanceKey + dynamicFormTemplateId`, then rebuild aggregate rows once per
  distinct key instead of once per report.
- Label deactivate/delete/reactivate uses the same `dynamic-form:statistic-rebuild` job path. The
  label service finds affected Dynamic Form templates from template JSON and current report
  `tableValuesJson`, upserts high-priority rebuild rows, then triggers the recurring job.
- The label-change enqueue path accepts both ObjectId-backed and string-backed
  `dynamicFormTemplateId` values from existing report documents, so statistic rebuild queueing is
  stable across current Mongo storage shapes.
- Assignment-related job-run coverage:
  - `work_assignment_materialize_jobs` stores per-assignment queue state for rolling-window period
    materialization. The scan logs processed/failed counts when jobs are processed or fail.
  - `work-assignment:queue-daily-scan` updates due/overdue period state, disables terminal queue
    rows, syncs assignment/work rollups, rebuilds report-period list projections, and writes
    `QUEUE_DUE_SCAN` / `QUEUE_DUE_SCAN_ITEM` operation logs when rows are scanned or item failures
    occur.
  - `notifications:due-scan` now writes `NOTIFICATION_DUE_SCAN` operation logs with
    work/assignment/report-period scanned counts and created notification count, including failure
    details.

2026-05-20 completion/status correction:

- Materialize and queue jobs may create/update report period rows while the compatibility runtime
  still uses materialized periods, but jobs must never set `Work.Status = S3` or
  `WorkAssignment.ProgressStatus = Completed`.
- Explicit work completion and explicit assignment completion are user actions, not job outcomes.
  Their operation logs should capture actor, scope, prior status, next status, completion date,
  and affected queue/materialize/report-lock counts when available.
- Once a work or assignment is completed, assignment materialize jobs and queue scan jobs must skip
  that completed scope or disable its active queue rows. They may rebuild projections/read models
  for display consistency, but must not reopen report editing.
- Report-period due transitions remain job-owned: pending/draft/submitted can become their overdue
  variants according to `dueAtUtc`. Approved reports and completed work/assignment locks are
  terminal for mutation purposes.
- Backfill/historical report period policy is data validation, not a job completion trigger. Jobs
  should use period windows and assignment deadline for due/reminder scanning; report
  `completedDate` is validated during report write paths.

## Frontend Surface

Route:

- `/operations`

Files:

- `tdtd-fe/src/api/operationsApi.ts`
- `tdtd-fe/src/pages/operations/OperationsPage.tsx`
- `tdtd-fe/src/routes/appRoutes.tsx`
- `tdtd-fe/src/layouts/Sidebar.tsx`

UI behavior:

- `Lich su thao tac` tab is visible to `SYSTEM_ADMIN`, `MANAGER_LEVEL`, and `MANAGER_UNIT:*`
- `Job run` tab is rendered only for `SYSTEM_ADMIN`
- each tab loads only its active server-paged list
- filters are applied explicitly instead of querying on every keystroke
- retry queues expose manual `Retry 20` actions for projection retry and action-log retry queues
- job-run status is rendered with operator labels for not-run, running, completed, retry-waiting,
  dead-letter, failed, skipped, and partial-failed states
- Dynamic Form statistic rebuild jobs are visible in the job-run tab with progress counts and a
  bounded manual `Run 3` action

## Verification

Verified on 2026-05-04:

- backend build: `dotnet build .\tdtd-be\tdtd-be.csproj --no-restore -o .\tdtd-be\bin\codex-check`
  passed with existing nullability warnings
- frontend typecheck: bundled Node runtime with `node node_modules\typescript\bin\tsc -b`

The regular backend debug output was locked by a running `tdtd-be.exe`, so backend verification used
a separate output directory.

Verified on 2026-05-06 after the job-run status UI patch:

- frontend typecheck: `node node_modules\typescript\bin\tsc -b`
- touched-file lint:
  `node node_modules\eslint\bin\eslint.js src\api\operationsApi.ts src\pages\operations\OperationsPage.tsx`
- local Mongo snapshot showed no current job-run rows in `work_assignment_materialize_jobs`,
  `docrole_read_model_projection_retry_jobs`, `user_action_log_retry_jobs`,
  `work_report_statistic_rebuild_jobs`, or `work_status_operation_logs`.

Verified on 2026-05-08 after the label/job-run alignment patch:

- backend build: `dotnet build tdtd-be\tdtd-be.csproj --no-restore --configfile scripts\NuGet.offline.config -p:UseAppHost=false -p:UseSharedCompilation=false -o .build\be-verify`
  passed with 0 warnings.
- backend test runner: `dotnet .build\tests-verify\tdtd-be.Tests.dll` after rebuilding the test
  project to `.build\tests-verify`; 15 tests passed.

Verified on 2026-05-09 after the focused Dynamic Form statistics fixture:

- backend build: `dotnet build tdtd-be\tdtd-be.csproj --no-restore --configfile scripts\NuGet.offline.config -p:UseAppHost=false -p:UseSharedCompilation=false -o .build\be-label-rebuild-fix`
  passed with 0 warnings.
- backend test runner: `dotnet .build\tests-deep-stat-fixture\tdtd-be.Tests.dll` after building
  the test project to `.build\tests-deep-stat-fixture`; 15 tests passed. The direct
  `dotnet run --project` path was avoided because the existing dirty `bin\Debug` tree triggered a
  recursive copy error unrelated to the test code.
- automation build: `dotnet build scripts\AutomationRandomTest\AutomationRandomTest.csproj --no-restore --configfile scripts\NuGet.offline.config -p:UseAppHost=false -p:UseSharedCompilation=false -o .build\automation-stat-fixture`
  passed with 0 warnings.
- fixture run `0509004902`: `dotnet .build\automation-stat-fixture\AutomationRandomTest.dll --dynamic-form-statistics-fixture http://localhost:5164`
  passed with 218 PASS rows and 3 expected negative assignment cases.
