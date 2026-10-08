# Patch 2026-05-20 - Mongo Phase 4 Job Run Ops Overlap Guard

> Navigation: `ARCHITECTURE_MAP.md` -> `DOCUMENTATION_MAP.md` -> `FEATURE_MAP.md` -> this file.
> Type: Patch.

## Status

Phase 4 job-run/ops slice is applied locally after Phase 3 notification realtime invalidation.
Backend commit: `6925a7d`.

## Accepted Tradeoff

Add a Hangfire recurring-job overlap guard now; do not redesign job queues or add a distributed
operations lock in this slice.

Reasons:

- The risky gap in the current ops layer is recurring jobs being scheduled again before a previous
  invocation finishes.
- Most queue-backed processors already claim individual rows with status/lease/retry fields.
- Manual system-admin process endpoints are intentionally synchronous operational tools and still
  call the service layer directly.
- A broader distributed lock/lease model across both recurring and manual endpoints should wait
  until there is evidence of operator overlap or multi-node contention.

## Applied Behavior

- Added `NonOverlappingRecurringJobRunner` as a Hangfire-facing wrapper.
- Recurring jobs now register against the wrapper instead of directly against domain services.
- Wrapper methods carry `DisableConcurrentExecution`:
  - long lock window for cleanup/history archive jobs
  - shorter lock window for materialize, queue scan, notification due scan, projection retry,
    action-log retry, and statistic rebuild jobs
- Service interfaces stay free of Hangfire-specific attributes.
- Existing job ids and trigger helper methods remain unchanged.
- Added a backend runner assertion that every wrapper method used for recurring jobs has the
  overlap guard.

## Deferred

- Cross-path locking between recurring jobs and manual process endpoints.
- Operation-log retention for `work_status_operation_logs` and `user_action_logs`.
- Retry/dead-letter UI extensions beyond the current queue lists.
- Distributed lock tuning for multi-node deployments, if this backend is scaled horizontally.

## Verification

- `dotnet build tdtd-be.csproj --no-restore -p:UseAppHost=false -p:UseSharedCompilation=false`
  passed.
- `dotnet run --project tests\tdtd-be.Tests\tdtd-be.Tests.csproj --no-restore` passed 50 tests.

## Phase Chain Status

Phase 5 documentation closeout is recorded in
`PATCH_2026_05_20_MONGO_ARCHITECTURE_PHASE_CLOSEOUT.md` for the selected tradeoffs across Phases
1-4.
