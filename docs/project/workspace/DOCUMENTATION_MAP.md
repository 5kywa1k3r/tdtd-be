# Documentation Map

> Mốc tiếp nhận hiện hành 08/10/2026: đọc [bàn giao chung cho 5 Lam](docs/coordination/LAM_CURRENT_2026_10_08.md) trước khi dùng các trạng thái và prompt lịch sử bên dưới.

> Navigation: `ARCHITECTURE_MAP.md` -> this file -> `FEATURE_MAP.md` or `docs/debug/DEBUG_MAP.md`.
> Type: Documentation map.

## How To Read

Use this order when re-entering the project later:

1. `ARCHITECTURE_MAP.md` - system overview and important architecture boundaries.
2. `DOCUMENTATION_MAP.md` - this routing map for all first-party docs.
3. `docs/WORK_DONE.md` - short ledger of applied feature, bug, and documentation work.
4. `docs/WORK_PLANNED.md` - short ledger of planned implementation and verification work.
5. `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md` - mandatory Git branch and commit traceability rules.
6. `FEATURE_MAP.md` - feature delivery index and status.
7. `docs/AUTOMATION_TEST_PLAN.md` - business-flow automation test strategy.
8. `docs/debug/DEBUG_MAP.md` - debug notes, incident plans, and investigation runbooks.
9. Open the specific feature or debug document listed by the relevant map.

Feature docs and debug docs are intentionally separate. A feature doc records product/technical
delivery. A debug doc records a failure, investigation plan, suspected root cause, verification
steps, and follow-up decisions.

## Documentation Standards

Every first-party markdown document should keep these conventions:

- Start with one `#` title.
- Add a short navigation note near the top that points back to this map or the right sub-map.
- State the doc type: `Architecture`, `Map`, `Feature`, `Debug`, `Patch`, `Backlog`,
  `Work log`, `App doc`, or `Rule doc`.
- Keep status explicit: completed, applied, planned, pending, investigating, or blocked.
- Keep code file references as repo-relative paths.
- Keep feature delivery details in `docs/features/*`.
- Keep operational/debug investigations in `docs/debug/*`.
- Tie every FE/BE commit to a work code and concrete summary, following
  `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md`.
- Do not put long debug plans inside `FEATURE_MAP.md` or inside feature docs unless the bug changes
  the feature contract.

## Top-Level Docs

| Doc | Type | Purpose |
| --- | --- | --- |
| `ARCHITECTURE_MAP.md` | Architecture | System entry point, FE/BE architecture map, runtime boundaries, and cross-feature notes. |
| `DOCUMENTATION_MAP.md` | Map | Canonical route into feature, debug, patch, and backlog docs. |
| `docs/WORK_DONE.md` | Work log | Short index of applied feature, bug, debug, and documentation work. |
| `docs/WORK_PLANNED.md` | Work log | Short index of upcoming implementation, verification, and cleanup work. |
| `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md` | Rule doc | Mandatory `dev` -> `prod` branch flow, work-code rules, and commit message format. |
| `docs/PROD_UPDATE_RUNBOOK.md` | App doc | Windows production update runbook for RDP-only access, backup, copy, restart, smoke test, and rollback. |
| `docs/PROD_DB_SEED_RUNBOOK.md` | App doc | Production Mongo clean/seed runbook for the `mu_pv01` catalog plus BT01-BT25 Dynamic Excel/Form seed package. |
| `docs/AUTOMATION_TEST_PLAN.md` | Test plan | Business-flow inventory and automation-test rollout plan. |
| `FEATURE_MAP.md` | Map | Feature index, feature status, and links to detailed feature docs. |

## Work Tracking Docs

- `docs/WORK_DONE.md`
- `docs/WORK_PLANNED.md`

## Rule Docs

- `docs/GIT_BRANCH_AND_COMMIT_GUIDE.md`
- `docs/DYNAMIC_EXCEL_IMPORT_SHEET_RULES.md`

## Operations Docs

- `docs/PROD_UPDATE_RUNBOOK.md`
- `docs/PROD_DB_SEED_RUNBOOK.md`

## Test Docs

- `docs/AUTOMATION_TEST_PLAN.md`
- `docs/DYNAMIC_EXCEL_TESTER_GUIDE.md`
- `docs/DYNAMIC_EXCEL_DYNAMIC_FORM_CURRENT_FLOW.md`
- `docs/UAT_DYNAMIC_FORM_TABLE_DYNAMIC_FLOW_CREATION.md`
- `docs/UAT_DYNAMIC_FORM_DYNAMIC_FLOW_TEST_MATRIX.md`
- `docs/features/PATCH_2026_07_21_DYNAMIC_FORM_FLOW_UAT_CONTRACT_REBASE.md`
- `docs/features/PATCH_2026_07_21_DYNAMIC_FORM_FLOW_FULL_LIFECYCLE_PLAN.md`
- `docs/features/FULL_P3_DYNAMIC_FORM_RUNTIME_EXECUTION_PROMPT.md`
- `docs/features/FULL_P3_DYNAMIC_FORM_RUNTIME_IMPLEMENTATION_PLAN_2026_07_22.md`
- `docs/features/P3_LIFECYCLE_SECTION_AUDIT_2026_07_22.md`
- `docs/features/FULL_P3_DYNAMIC_FORM_RUNTIME_EVIDENCE_2026_07_22.md`
- `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_EXECUTION_PROMPT.md`
- `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_IMPLEMENTATION_PLAN_2026_07_23.md`
- `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_EVIDENCE_2026_07_23.md`
- `docs/features/p5-flow-runtime/FULL_P5_FLOW_RUNTIME_PROMPT_INDEX.md`
- `docs/features/p5-flow-runtime/FULL_P5_FLOW_RUNTIME_SHARED_ANCHOR.md`
- `docs/features/p5-flow-runtime/FULL_P5_FLOW_RUNTIME_IMPLEMENTATION_PLAN.md`
- `docs/features/FULL_P5_FLOW_RUNTIME_EVIDENCE_2026_07_24.md`
- `docs/features/p6-flow-topology/FULL_P6_FLOW_TOPOLOGY_PROMPT_INDEX.md`
- `docs/features/p6-flow-topology/FULL_P6_FLOW_TOPOLOGY_SHARED_ANCHOR.md`
- `docs/features/p6-flow-topology/FULL_P6_FLOW_TOPOLOGY_IMPLEMENTATION_PLAN.md`
- `docs/features/FULL_P6_FLOW_TOPOLOGY_EVIDENCE_2026_07_29.md`
- `docs/features/p8-stat-config/FULL_P8_STAT_CONFIG_SHARED_ANCHOR.md`
- `docs/features/p8-stat-config/FULL_P8_STAT_CONFIG_IMPLEMENTATION_PLAN.md`
- `docs/features/p8-stat-config/FULL_P8_STAT_CONFIG_PROMPT_INDEX.md`
- `docs/features/p8-stat-config/FULL_P8_STAT_CONFIG_TEST_MATRIX.md`
- `docs/features/FULL_P8_STAT_CONFIG_EVIDENCE_2026_08_03.md`
- `docs/features/DYNAMIC_FORM_FLOW_TRACE_MATRIX_V1.md`
- `docs/features/DYNAMIC_FORM_FLOW_UI_ROUTE_OWNERSHIP_V1.md`
- `docs/features/DYNAMIC_FORM_FLOW_UI_GATE_TEMPLATE.md`
- `docs/features/DYNAMIC_FORM_LIST_ACTION_UI_GATE_2026_07_21.md`
- `docs/UAT_PV01_ASSIGNMENT_SCENARIO.md`
- `docs/UAT_CATTH_FULL_FLOW.md`
- `docs/UAT_CATTH_AGGREGATE_STAT_MAP.md`
- `docs/features/PATCH_2026_05_18_TYPED_EXCEL_METRIC_UAT_PLAN.md`

### Current FULL-P4 Definition Gate - Achieved and Re-attested 2026-07-23

- The execution prompt is the P4 scope authority; the implementation plan records the canonical family/version
  aggregate, schema-v2/topology/policy/mapping validators, transactional command receipt/audit boundary, ACL,
  idempotent `REQUIRES_REVIEW` migration, and the P5/P6/P7/P8 runtime barriers.
- The re-attested implementation includes raw-request hashing plus early historical receipt snapshots/SHA,
  active-actor replay checks, bounded family/direct-version snapshots with current permission/`isUsed`,
  tamper `409`/zero-write rejection, exact three-pin clone CAS, literal-only `"*"` wildcard, ACL-scoped validation,
  own-draft validation, and locked-snapshot integrity. Validator evidence covers 150 fixtures (offset 125), 36 real
  API topology/policy cases, and six faulted operations across 46 transaction boundaries and 58 requests.
- Migration evidence covers transactional `PREPARED`/`APPLIED`/`ROLLED_BACK` manifests, phase-2-before-phase-1
  rollback, the 14 MiB cap, drift refusal, exact rollback/reapply, and no payload rewrite in the oversize fallback.
  P7 preview/apply blocks both legacy `REQUIRES_REVIEW` and canonical definitions with `409` and zero writes. The FE
  has stable-ID three-way merge, raw JSON persistence, structured error focus, and permission-aware affordances.
- The trace matrix records the exact 60 definition cases: `FLOW-CRUD-01..12`, `FLOW-TOPO-01..20`,
  `FLOW-POL-01..16`, and `FLOW-PERM-01..12`. Final backend evidence passed fast `182/182`; two clean
  `201/201 DAT` runs in `p1_20260723061327_226784_36d93bb8` have normalized SHA
  `7a14439235ca8af077b08b5c62eab1b7f2a03667d8e2c074b6b22937b305c315` in both iterations and successful
  cleanup. Deliberate run `p1_20260723061602_57984_1fae6043` exited `1` with root `passed=false`,
  `201 DAT + 1 KHONG_DAT`, and successful cleanup.
- Catalog v1.1 passed with SHA
  `e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf`, two immutable versions, and
  21 P3-supported capabilities. FE application/test typechecks passed, scoped ESLint passed `19/19`, and Vitest
  passed `25/25` files and `135/135` tests with Vite `7.2.4` production artifact
  `tdtd-fe/.build/p4-production-final5-20260723` (`9,133` modules, `159` files, `7,410,952` bytes). Real production
  Browser/Kestrel/Mongo runs `p1_20260723011327_96688_3f13ce35`,
  `p1_20260723021913_130344_c812b9bd`, and `p1_20260723031909_217396_dd262c2b` used
  `networkMockCount=0` and cleaned up. Combined browser coverage includes all four actors, legacy redirect,
  desktop/mobile, small/large topology, 201-node rejection, migration review, and P6/P8 barriers without
  enabling runtime; direct Mongo receipt/success/linked-audit/orphan reconciliation was `9/9/9/0`,
  `4/4/4/0`, and `4/4/4/0`. Run C normalized SHA is
  `be2defce9b1be7153e0e4c4a366650ca08067cda4b86ffea1255f811320d7c5c`. No browser call occurred after
  Run C and no Run D was created; post-Run-C hardening is covered by the final automated gates. Exact commands and
  artifacts are in
  `docs/features/FULL_P4_DYNAMIC_FLOW_DEFINITION_EVIDENCE_2026_07_23.md`; Run C machine evidence is
  `.p4-artifacts/browser-uat/20260723/P4_PRODUCTION_BROWSER_RUN_C_2026_07_23.json`.
- Draft `definitionLockable=false` is not a pre-lock validator. The backend lock command strict-validates the
  payload, and only a successful locked snapshot attests `definitionLockable=true`. At this P4 checkpoint,
  `canExecute=false`; P5 instance runtime, P6 gateway execution, P7 mapping/raw-source/separator execution, and
  P8 statistic-profile execution were deferred, and work stopped before P5. The later P5 result is recorded below.

### FULL-P5 Flow Runtime Gate - Achieved 2026-07-24

- `FULL-P5-FLOW-RUNTIME` completed the append-only prompt chain and stopped before P6. Positive execution is
  limited to `FLOW-T01/T02`; P6 topology `T03..T12`, P7 mapping/raw-source execution and P8 statistics remain
  deferred.
- Catalog v1.2 promotes only T01/T02 with semantic SHA
  `b26549d5de7a3e93bd6fc9bab7bfdfbdaffb66a01347039b2c3629692b60068f`; catalog v1.1 and its locked
  snapshots remain immutable, readable and runtime-blocked.
- Final verification passed BE `187/187`, FE `186/186`, two deterministic P5 `20/20` iterations with normalized
  SHA `7ede59532554d0bf0cb483c8b8f37e393ca957cf0777f7be213c12e2a06b7a2e`, and production
  Browser/Kestrel/Mongo `295/295` with `networkMockCount=0`. Exact commands, negative gates, direct-Mongo
  reconciliation, cleanup and artifacts are recorded in
  `docs/features/FULL_P5_FLOW_RUNTIME_EVIDENCE_2026_07_24.md`.

### FULL-P6 Flow Topology Gate - Achieved 2026-07-29

- `FULL-P6-FLOW-TOPOLOGY` completed its append-only `P6-00..P6-12` prompt chain. T01/T02 remain the P5
  regression baseline; T03..T12 now have positive BPMN-lite execution for sequential/fork, versioned joins,
  typed condition, bounded review loop, subflow, periodic, supplemental and terminal/invalidation semantics.
- Catalog v1.3 is canonical with semantic SHA
  `55cfa0a4420e01db6707011ffc7a0271088c5b01b63edb8f2d21978edd3e2497`. V1.1/v1.2 remain immutable
  historical pairs, and rollback to exact v1.2 stays readable and fail closed for T03..T12.
- Production runtime keeps the canonical route
  `/works/:workId/flow-instances/:instanceId/:tab?`, exact tabs `overview`, `work-to-do`, `timeline`, and the
  existing `DynamicFlowRuntimeEntryPanel`/`DynamicFlowLaunchWizard`/`DynamicFlowRuntimePage` owners; there is
  no `/steps` route.
- Final gates include full API/Kestrel/Mongo integration/race/chaos, deliberate failure, production browser
  owner/coordinator/reporter/reviewer/outsider with no network mocks, direct-Mongo reconciliation and cleanup.
- P7 mapping/raw-source/separator and P8 statistic config/profile/executor remain blocked; P6 is not a full
  BPMN or full-lifecycle claim. Exact evidence:
  `docs/features/FULL_P6_FLOW_TOPOLOGY_EVIDENCE_2026_07_29.md`.

### FULL-P8 Statistics Configuration Gate - Achieved 2026-08-03

- `FULL-P8-STAT-CONFIG` completed `P8-00..P8-12`; `P8-STAT-001..025` passed `25/25`, and all 12 P8 configuration/readiness groups passed `200/200`.
- Catalog v1.5 is canonical with semantic SHA `e3c335617721bbf8cd09c62c5e76377f23f3d024b20c848bf66c8c539f0c9d2f`; v1.0..v1.4 remain immutable. Exact-v1.4 rollback stayed readable and fail closed with zero P8 writes before exact v1.5 was restored.
- Production Browser/Kestrel/Mongo covered seven actors with `networkMockCount=0`; security, cleanup, responsive/deep-link/accessibility and direct-Mongo reconciliation gates passed.
- P8 closes statistics configuration/readiness only. P9 run/projection/result/export and P10 reconciliation remain blocked. Exact evidence is in `docs/features/FULL_P8_STAT_CONFIG_EVIDENCE_2026_08_03.md`.

### FULL-P9 Statistics Run Gate - ACHIEVED 2026-08-06

- `FULL-P9-STAT-RUN` completed the exact `P9-00..P9-12` chain at `ACHIEVED/P9-A2/P9-D2`.
- Requirements passed `30/30`; all 12 groups passed `260/260`; P9-11 two-clean passed `500/500`;
  P9-12 `P9-CLOSE` passed `10/10` and stopped with `nextPrompt=null`.
- Canonical closeout evidence: `docs/features/FULL_P9_STAT_RUN_EVIDENCE_2026_08_06.md`.
- Pack navigation: `docs/features/p9-stat-run/FULL_P9_STAT_RUN_SHARED_ANCHOR.md`,
  `docs/features/p9-stat-run/FULL_P9_STAT_RUN_PROMPT_INDEX.md`,
  `docs/features/p9-stat-run/FULL_P9_STAT_RUN_IMPLEMENTATION_PLAN.md`, and
  `docs/features/p9-stat-run/FULL_P9_STAT_RUN_TEST_MATRIX.md`.
- Published machine-readable artifacts: `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json`
  and `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json`. P10 was not started.

## Feature Docs

Canvas Workspace entry point:

- [Canvas Workspace — map tài liệu và handoff](D:/Job/CA/tdtd/docs/features/canvas-workspace/README.md) — baseline P11-04 sau push, nhánh FE riêng, source lanes, checklist/gate S1 và ranh giới P11; root docs ngoài Git, implementation chưa bắt đầu.

Feature index:

- `FEATURE_MAP.md`

Detailed feature docs:

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
- `docs/features/FEATURE_18_IMPLEMENTATION_SLICE_DELIVERY_GOVERNANCE.md`
- `docs/features/FEATURE_19_DYNAMIC_FLOW_ASSIGNMENT_TREE_AND_SUMMARY_INTEGRATION.md`

Feature patch notes:

- [Canvas Workspace implementation plan](D:/Job/CA/tdtd/docs/features/PATCH_2026_09_11_CANVAS_WORKSPACE_FOUNDATION_PLAN.md) — Proposed; review DOC20260911-canvas-plan: contract state/read-only, shared layout/canvas/toolbar dark/light, fixture API-free và production build riêng, CW-S1-01..10/80 tổ hợp render; backlog S2–S4 trước tích hợp Form/Flow.
- `docs/features/PATCH_2026_04_29_WORKFLOW_FIXES.md`
- `docs/features/PATCH_2026_05_05_SYSTEM_ADMIN_LABEL_FORM_FIXES.md`
- `docs/features/PATCH_2026_05_06_UNIT_SCOPE_ACCOUNT_ALIGNMENT.md`
- `docs/features/PATCH_2026_05_06_ADMIN_USER_AUTH_PERFORMANCE_FIXES.md`
- `docs/features/PATCH_2026_05_07_WORK_DETAIL_CARD_HANDOVER_CLONE_PERMISSION.md`
- `docs/features/PATCH_2026_05_07_DYNAMIC_FORM_PREVIEW_ENTRYPOINTS.md`
- `docs/features/PATCH_2026_05_07_WORK_FORM_INP_AND_DETAIL_CANEDIT_FIXES.md`
- `docs/features/PATCH_2026_05_09_VIETNAMESE_TEXT_STANDARDIZATION.md`
- `docs/features/PATCH_2026_05_10_DYNAMIC_FORM_RUNTIME_PREVIEW_ACCESS.md`
- `docs/features/PATCH_2026_05_10_DYNAMIC_EXCEL_LOGIC_AUDIT.md`
- `docs/features/PATCH_2026_05_11_AGGREGATION_AUTOTEST_AUDIT.md`
- `docs/features/PATCH_2026_05_11_ASSIGNMENT_SOURCE_RULES.md`
- `docs/features/PATCH_2026_05_11_DYNAMIC_FORM_AGGREGATE_APPROVED_REFRESH.md`
- `docs/features/PATCH_2026_05_12_AGGREGATE_DRAFT_WRITE_FIX.md`
- `docs/features/PATCH_2026_05_12_DYNAMIC_FORM_FIELD_NAME_LABEL_SPLIT.md`
- `docs/features/PATCH_2026_05_13_ASSIGNMENT_AGGREGATION_LAYOUT_PLAN.md`
- `docs/features/PATCH_2026_05_13_DYNAMIC_EXCEL_LABEL_FIX_CHECKLIST.md`
- `docs/features/PATCH_2026_05_13_HEADER_TOOLBAR_REFACTOR_PLAN.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE1_PAYLOAD_STAT_READ_MODEL_PLAN.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE2_READ_MODEL_WORK_DOCUMENTS.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE3_NOTIFICATION_REALTIME_INVALIDATION.md`
- `docs/features/PATCH_2026_05_20_MONGO_PHASE4_JOB_RUN_OPS_OVERLAP_GUARD.md`
- `docs/features/PATCH_2026_05_20_MONGO_ARCHITECTURE_PHASE_CLOSEOUT.md`
- `docs/features/PATCH_2026_05_21_REPORT_REQUIRED_ADHOC_FLOW.md`
- `docs/features/PATCH_2026_05_22_ASSIGNMENT_STACKED_AGGREGATION.md`
- `docs/features/PATCH_2026_05_25_AGGREGATE_DATA_UI_SIMPLIFICATION.md`
- `docs/features/PATCH_2026_06_17_ASSIGNMENT_BRANCH_DRILLDOWN_VIEW.md`
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
- `docs/features/p5-flow-runtime/FULL_P5_FLOW_RUNTIME_IMPLEMENTATION_PLAN.md`
- `docs/features/FULL_P5_FLOW_RUNTIME_EVIDENCE_2026_07_24.md`
- `docs/features/p6-flow-topology/FULL_P6_FLOW_TOPOLOGY_PROMPT_INDEX.md`
- `docs/features/p6-flow-topology/FULL_P6_FLOW_TOPOLOGY_SHARED_ANCHOR.md`
- `docs/features/p6-flow-topology/FULL_P6_FLOW_TOPOLOGY_IMPLEMENTATION_PLAN.md`
- `docs/features/FULL_P6_FLOW_TOPOLOGY_EVIDENCE_2026_07_29.md`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1.schema.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_1.schema.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_1.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_2.schema.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_2.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_3.schema.json`
- `docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_3.json`
- `docs/features/DYNAMIC_FORM_FLOW_TRACE_MATRIX_V1.md`
- `docs/features/DYNAMIC_FORM_FLOW_UI_ROUTE_OWNERSHIP_V1.md`
- `docs/features/DYNAMIC_FORM_FLOW_UI_GATE_TEMPLATE.md`

Feature/operations backlog:

- `docs/features/TO_DO.md`

## Debug Docs

Debug index:

- `docs/debug/DEBUG_MAP.md`

Current debug notes:

- `docs/debug/DEBUG_2026_05_03_MONGO_INDEXES.md`
- `docs/debug/DEBUG_2026_05_03_ASSIGNMENT_HANDOVER_F03.md`
- `docs/debug/DEBUG_2026_05_04_SYSTEM_BOOTSTRAP_MU_TEST_PLAN.md`
- `docs/debug/DEBUG_2026_05_04_DOCROLE_READ_MODEL_WRITE_PATH_REVIEW.md`
- `docs/debug/DEBUG_2026_05_08_ASSIGNMENT_SELF_ASSIGN_SCOPE_TESTS.md`
- `docs/debug/DEBUG_2026_05_08_AUTOMATION_RANDOM_TEST.md`
- `docs/debug/DEBUG_2026_05_08_DYNAMIC_FORM_AGGREGATION_PROBE.md`
- `docs/debug/DEBUG_2026_05_08_MINDMAP_EMPTY_FILTER_REPORT_STATUS.md`
- `docs/debug/DEBUG_2026_05_08_UI_STATISTICS_FIXTURE.md`
- `docs/debug/DEBUG_2026_05_09_PRE_FINAL_TEST_READINESS.md`
- `docs/debug/DEBUG_2026_05_12_PV01_DYNAMIC_FORM_EXCEL_UAT_FLOW.md`
- `docs/debug/DEBUG_2026_07_21_DYNAMIC_FORM_TABLE_DYNAMIC_FLOW_CREATION_AUDIT.md`

## App Docs

Frontend local docs:

- `tdtd-fe/README.md`
- `tdtd-fe/FRONTEND_COMMON_RULES.md`

## Excluded Docs

Dependency documentation under `node_modules` and `.dotnet/.nuget` is intentionally excluded from
this project map.

<!-- FULL-P10-RECONCILE P10-12 TERMINAL -->
### FULL-P10 Reconciliation Gate - ACHIEVED 2026-08-14

- Terminal state: `ACHIEVED/P10-A2/P10-D1`; chain `p10_chain_20260810002129_9f56`.
- Coverage: requirements `32/32`, groups `12/12`, cases `280/280`, P10-CLOSE `10/10`, two-clean `540/540`.
- Catalog v1.7 raw `072831d879352c76ca9e5af5f9cc2a20e13c632653fed466131f5f7a359204c4`; exact-v1.6 CURRENT-only rollback `19/19` with `57` stores and zero writes; restored CURRENT/LOCK v1.7.
- Production browser `3/3`, `networkMockCount=0`; P9 regression, security, accessibility and cleanup passed.
- Final evidence: `docs/features/FULL_P10_RECONCILE_EVIDENCE_2026_08_14.md`. Deferred items: none.
