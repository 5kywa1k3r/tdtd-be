# Feature 12 - Task Action Center And Action Notifications

> **02/10/2026 — thay thế quyết định bố trí 08/05:** Yud duyệt một mục menu chính
> Công việc cần thực hiện, gom việc cá nhân toàn hệ thống, hai view Cần làm / Lịch sử thông báo.
> Hai tab nhỏ trong Giao việc không còn mount; URL cũ chuyển sang inbox có bộ lọc Work.
> [Quy tắc, API, routing và kiểm chứng mới](WORK_INBOX_IMPLEMENTATION_2026_10_02.md).
> Không áp dụng các chỉ dẫn bố trí hoặc `actionUrl` cũ bên dưới cho implementation mới.

> Navigation: `ARCHITECTURE_MAP.md` -> `DOCUMENTATION_MAP.md` -> `FEATURE_MAP.md` -> this file.
> Type: Feature.

## Status

Planned on 2026-05-07. This document supersedes the earlier UI assumption that assignment
handover can be executed directly from the Assignment tab.

2026-05-08 product correction: keep the Work Detail card launcher. `Công việc cần thực hiện` is
not a Work Detail launcher card and must not replace the card launcher. It is an internal page inside
the `Nhiệm vụ/Chỉ tiêu` card, at the same level as `Danh sách nhiệm vụ/chỉ tiêu`.

2026-05-08 notification UI correction:

- Keep `Công việc cần thực hiện` as the business/action surface for requests and unfinished work.
- Add a separate work-scoped `Thông báo` tab inside the `Nhiệm vụ/Chỉ tiêu` card, at the same level
  as `Danh sách nhiệm vụ/chỉ tiêu` and `Công việc cần thực hiện`.
- `Thông báo` lists persisted notifications for the current `workId`, supports unread/action
  filters, can mark rows read, and links each notification back to the relevant assignment detail,
  report/review context, or action center.
- Header bell notification clicks should use `actionUrl` for explicit actions; otherwise
  assignment/report/due work-scoped notifications can land on
  `?tab=ASSIGN&section=NOTIFICATIONS&assignmentId=...` so the user sees the notification context
  before opening the source item.

2026-05-07 implementation slice:

- Work Detail keeps the card launcher.
- Added `Công việc cần thực hiện` inside the `Nhiệm vụ/Chỉ tiêu` card.
- Renamed/merged the assignment card content to `Nhiệm vụ/Chỉ tiêu`.
- Moved Dynamic Form clone request/approval UI out of `Báo cáo` into `Công việc cần thực hiện`.
- Added notification action metadata fields and clone request/review notification producers.
- Notification bell uses `actionUrl` when present and otherwise maps action-like notification types
  to `?tab=ASSIGN&section=ACTIONS`.
- Disabled the old direct handover UI section in `Nhiệm vụ/Chỉ tiêu`.
- Guarded `Báo cáo` so `my-report-templates/search` is not called when the current user has no
  report assignment in the work.

Remaining implementation: shared `WorkActionRequest`, handover request create/approve/reject APIs,
source-action notification resolution, and full report-review/evaluation/status-risk producers.

## Goal

Create one work-scoped action surface for every workflow that needs a user decision or attention.
The internal assignment page should be named `Công việc cần thực hiện`.

This feature separates:

- business request state: durable workflow rows that can be approved, rejected, cancelled, or
  completed
- notifications: per-user inbox/action pointers that tell the user what needs attention
- source workflows: assignment handover, Dynamic Form clone permission, report review, evaluation,
  and status-risk handling

Notifications must not own workflow state. They point users to the correct request or source
screen.

## Scope Checklist

- Keep the Work Detail card launcher.
- Add `Công việc cần thực hiện` as an internal page under the `Nhiệm vụ/Chỉ tiêu` card.
- Add `Thông báo` as a separate internal notification-management page under the same card; do not
  mix notification inbox management into the business request UI.
- Merge `Nhiệm vụ` and `Chỉ tiêu` into one assignment card content named `Nhiệm vụ/Chỉ tiêu`.
- Put filters at the top of `Nhiệm vụ/Chỉ tiêu` so the user can narrow by task/target type,
  status, assignee, and ownership without switching page tabs.
- Move request/approval surfaces out of scattered tabs:
  - assignment handover request and acceptance
  - Dynamic Form clone permission approval
  - report review reminders
  - evaluation reminders
  - overdue and at-risk assignment/report status alerts
- Keep `Nhiệm vụ/Chỉ tiêu` focused on assignment/target creation, list, detail, and preview.
- Keep `Bao cao` focused on assignee report work.
- Remove a separate top-level `Duyet bao cao` page/card; expose review work through `Công việc
  cần thực hiện` and link to the report context by `workId`.
- Push a notification to the recipient/approver whenever a request needs action.
- Push a notification to the requester when a request is approved/rejected/completed.
- Add Work Detail URL state so notification/request clicks can open the correct work function and
  focused entity.
- Add a `Công việc chưa thực hiện` tab for the current user's unfinished work, assignment, and
  report obligations.

## Work Detail UI Contract

The Work Detail card launcher remains role-aware:

| Card | Work creator | Assignment assignee | Notes |
| --- | --- | --- | --- |
| `Thuoc tinh chung` | visible | visible when detail access exists | view/edit depends on existing permission |
| `Nhiệm vụ/Chỉ tiêu` | visible when the user can create child assignments/targets or inspect assigned scope | visible when detail access exists | contains `Danh sách nhiệm vụ/chỉ tiêu` and `Công việc cần thực hiện`; no direct handover execution here |
| `Bao cao` | hidden unless the current user has an assigned report lane | visible when the current user has an assigned report lane | report writing only |
| `Tong hop` | visible if permitted | visible if permitted | unchanged aggregation surface |
| `Duyet bao cao` | visible if permitted | visible if permitted | can stay as a launcher card until review work is fully routed through action items |

`Công việc cần thực hiện` is work-scoped but is opened through the `Nhiệm vụ/Chỉ tiêu` card. The
global header bell stays as the lightweight notification entry; clicking an action notification
deep-links to Work Detail with `tab=ASSIGN&section=ACTIONS` when a work id exists.

`Thông báo` is also work-scoped and opened through the `Nhiệm vụ/Chỉ tiêu` card. It is the inbox
management view for notifications tied to the current work, while `Công việc cần thực hiện` remains
the place to execute request/review/clone/handover business decisions.

## Nhiệm vụ/Chỉ tiêu Internal Pages

| Page | URL state | Purpose | Source data |
| --- | --- | --- | --- |
| `Danh sách nhiệm vụ/chỉ tiêu` | `?tab=ASSIGN` | Assignment/target list, filters, detail, preview Dynamic Form, create assignment | `WorkAssignmentService.GetByWork` and assignment detail APIs |
| `Công việc cần thực hiện` | `?tab=ASSIGN&section=ACTIONS` | Business requests, clone approvals, handover approvals, unfinished/report/review/evaluation work | clone request APIs now; future `WorkActionRequest` and report/review projections |
| `Thông báo` | `?tab=ASSIGN&section=NOTIFICATIONS` | Work-scoped notification inbox and link-back surface | `POST /api/notifications/search` filtered by `workId`; optional `assignmentId` focus |

## Action Center Tabs

`Công việc cần thực hiện` should be a router/detail surface inside `Nhiệm vụ/Chỉ tiêu`, not the
place where every business form is duplicated. Its internal groups may be displayed as compact tabs
or filters inside the page, but they must not become separate Work Detail launcher cards.

Tabs:

| Tab | Purpose | Primary source |
| --- | --- | --- |
| `Yêu cầu cần xử lý` | Pending requests, approvals, review/evaluation reminders, and action notifications that need attention | `WorkActionRequest`, existing clone requests, review/report projections, notification action metadata |
| `Công việc chưa thực hiện` | Current user's unfinished work, assignments, and reports | work list/read models, assignment list/read models, my-report period/template projections |
| `Đã xử lý` | Resolved/completed/rejected request history and recently completed action notifications | request status rows plus resolved notifications |

`Công việc chưa thực hiện` includes:

- works visible to the current user that are not completed
- assignments for the current user that are not completed
- report periods/templates the current user still needs to open, draft, submit, correct, or finish

This tab is not an approval workflow. It is a work queue for the logged-in user.

Navigation rule:

- notification -> `Nhiệm vụ/Chỉ tiêu` card with `section=ACTIONS` for `Công việc cần thực hiện`
  request/action context
- non-action work-scoped notification -> `Nhiệm vụ/Chỉ tiêu` card with `section=NOTIFICATIONS` and,
  when available, `assignmentId` so the notification row can open the matching assignment detail
- action detail -> the real Work Detail page/section that performs the business action
- overdue/at-risk notification -> `Thực hiện ngay` deep-link directly to the overdue/at-risk work,
  assignment, report period, or report editor
- if the user completes the work directly in the source page, backend services must still resolve
  the matching request/notification by `requestId` or `sourceEntityId`

## Request Types

| Request type | Source action | Action owner | Completion behavior |
| --- | --- | --- | --- |
| `ASSIGNMENT_HANDOVER` | current assignee requests transfer to another user | target recipient | approve triggers runtime handover; reject leaves current lane unchanged |
| `DYNAMIC_FORM_CLONE` | assignment assignee asks to clone assigned Dynamic Form | assignment owner | approve grants read/clone visibility; user clones from existing Dynamic Form UI |
| `REPORT_REVIEW` | submitted report waits for reviewer action | report reviewer/assignment owner | item links to report context by `workId`; review UI stays in report flow |
| `ASSIGNMENT_EVALUATION` | evaluation is required or requested | evaluator/assignment owner | item links to report/evaluation context by `workId` |
| `STATUS_RISK` | assignment/report becomes at-risk or overdue | responsible user and/or owner | notification/link only unless product later defines an approval action |

## Assignment Handover Request Flow

Direct handover from the Assignment tab is no longer the product flow. Handover must be accepted by
the target recipient before runtime ownership changes.

Flow:

1. Current assignee selects an assignment, target recipient, reason, and optional comment.
2. Backend validates only request creation preconditions:
   - requester is the current `fromAssigneeUserId`
   - assignment is active
   - target user exists
   - source/target transition is allowed
   - no duplicate pending handover request exists for the same assignment/source/target lane
3. Backend creates a pending `ASSIGNMENT_HANDOVER` request.
4. Backend creates a notification for the target recipient.
5. Target recipient opens `Công việc cần thực hiện` and approves or rejects.
6. On approve, backend revalidates current runtime state:
   - source user is still the active lane owner
   - target user still has no active collision for the same assignment/template/period identity
   - assignment/binding/report-period/report/queue rows have not moved in a conflicting way
7. Only after approval succeeds does backend execute the existing runtime handover operation.
8. Backend writes dedicated handover history and marks the request `COMPLETED`.
9. Backend notifies requester and recipient of completion.

If approval-time validation fails, do not partially hand over. Mark the request as `FAILED` or keep
it pending with a visible failure reason; implementation should choose one behavior and document it
before coding. The safer first implementation is terminal `FAILED`, so users submit a fresh request
after data changes.

## Handover Runtime Boundary

The current handover service already owns the actual lane transfer and data updates. Keep that
logic, but stop exposing it as the primary UI action.

Required backend adjustment:

- Public UI endpoint creates a handover request, not a direct transfer.
- Request approval endpoint validates the approver is the target recipient.
- Approval service calls an internal handover runtime method after the request is approved.
- The internal runtime method must receive request context:
  - request id
  - requested by user id
  - approved by user id
  - source user id
  - target user id
  - reason/comment
- Handover history should record both the requester and the approving recipient. Historical report
  actors remain unchanged.

Do not bypass the existing collision, projection, status-rollup, and audit-preservation rules.

## Dynamic Form Clone Request Flow

The existing clone permission model remains valid, but its UI moves to `Công việc cần thực hiện`.

Requester:

1. Assignment assignee requests clone permission from the report/work context.
2. Request appears in the owner's `Công việc cần thực hiện`.
3. Requester receives notifications for pending/approved/rejected state changes.

Owner:

1. Assignment owner approves or rejects in `Công việc cần thực hiện`.
2. Approval grants visibility and clone permission only.
3. The requester opens the existing Dynamic Form list/detail page and uses the existing clone
   action there.

No automatic clone is created during approval.

## Report Review And Evaluation Links

Do not duplicate report-review or evaluation forms inside `Công việc cần thực hiện`.

Action-center items for these flows should:

- show compact context: work, assignment/template, period/report, requester/assignee, status
- mark whether the item needs action or is informational
- link to Work Detail by `workId`
- open the report context through URL state, for example:
  - `?tab=REPORT&reportId=...`
  - `?tab=REPORT&periodId=...`
  - `?tab=REPORT&assignmentId=...`

The report/review/evaluation screens remain the source of truth for the actual business action.

## Notification Model Direction

Keep one persisted `notifications` collection for the user inbox. Split behavior by type/category,
not by separate collections, until scale proves otherwise.

Add fields to `UserNotification`:

- `Category`: `GENERAL`, `HANDOVER`, `APPROVAL`, `REPORT`, `STATUS`
- `RequiresAction`
- `ActionState`: `OPEN`, `RESOLVED`, `DISMISSED`
- `SourceEntityType`
- `SourceEntityId`
- `RequestId`
- `ActionUrl`
- `ResolvedAtUtc`

Add notification types:

- `ASSIGNMENT_HANDOVER_REQUESTED`
- `ASSIGNMENT_HANDOVER_APPROVED`
- `ASSIGNMENT_HANDOVER_REJECTED`
- `DYNAMIC_FORM_CLONE_REQUESTED`
- `DYNAMIC_FORM_CLONE_APPROVED`
- `DYNAMIC_FORM_CLONE_REJECTED`
- `REPORT_REVIEW_REQUIRED`
- `ASSIGNMENT_EVALUATION_REQUIRED`
- `ASSIGNMENT_AT_RISK`
- `ASSIGNMENT_OVERDUE`
- `REPORT_PERIOD_AT_RISK`
- `REPORT_PERIOD_OVERDUE`

Service code can be split into focused producer methods for handover, approval, report, and status
notifications. Storage can stay unified.

## Notification Business Map

This table is the trace map for notification-related business flows. The source service owns
completion/resolution; notification only points to the action or reports the result.

| Business flow | Notification behavior | Primary link | Resolved when | Controller | Service |
| --- | --- | --- | --- | --- | --- |
| Assignment assigned | Assignee receives an informational/action notification | Work Detail -> `Nhiệm vụ/Chỉ tiêu` or `Báo cáo` | Read-only unless a later product action is added | `WorkAssignmentsController.Create`, `WorkAssignmentsController.Activate` | `WorkAssignmentService.CreateAsync`, `WorkAssignmentService.ActivateAsync`, `NotificationService.NotifyAssignmentAssignedAsync` |
| Assignment handover request | Target recipient receives pending request; requester receives approval/rejection/completion result | Notification -> `tab=ASSIGN&section=ACTIONS`; detail -> `Nhiệm vụ/Chỉ tiêu` | Handover request is approved, rejected, failed, or completed | current direct route `WorkAssignmentsController.Handover`; future handover-request endpoints | future `WorkActionRequestService`; `WorkAssignmentHandoverService` remains the internal runtime transfer owner |
| Dynamic Form clone request | Assignment owner receives clone approval request; requester receives result | Notification -> `tab=ASSIGN&section=ACTIONS`; detail -> Dynamic Form UI or report context | `DynamicFormCloneRequest.Status` becomes `APPROVED` or `REJECTED` | `DynamicFormCloneRequestsController` | `DynamicFormCloneRequestService` |
| Report submitted for review | Reviewer/assignment owner receives review-required notification | Notification -> `tab=ASSIGN&section=ACTIONS`; detail -> report/review context by `workId` | Report is approved, returned, recalled, deactivated, or reactivated according to the source action | `WorkAssignmentReportsController.Submit`; `WorkAssignmentReviewController` review endpoints | `WorkAssignmentReportService.SubmitAsync`; `WorkAssignmentReviewService.ApproveReportAsync`, `ReturnReportAsync`, `RecallApprovedReportAsync`, `DeactivateReportAsync`, `ReactivateReportAsync` |
| Assignment evaluation | Evaluator receives evaluation-required notification/reminder | Notification -> `tab=ASSIGN&section=ACTIONS`; detail -> report/evaluation context by `workId` | `EvaluateAssignmentAsync` succeeds | `WorkAssignmentReviewController.EvaluateAssignment` | `WorkAssignmentReviewService.EvaluateAssignmentAsync` |
| Work/assignment/report overdue or at risk | Responsible user and/or owner receives status notification | CTA `Thực hiện ngay` -> delayed/risky work, assignment, report period, or report editor | Source status exits overdue/at-risk or source report/work is completed | no dedicated controller; surfaced through existing work/report/detail routes | current due producer `NotificationDueScanJobService`; future risk producer should read `WorkAssignmentProgressService`/`WorkReportPeriodStatusHelper` state transitions |
| Dynamic Form statistic rebuild | Requester receives completion/failure notification after statistic config changes | Dynamic Form detail or Operations statistic rebuild job when failed | Rebuild job status is `COMPLETED` or `DEAD_LETTER` | `DynamicFormController.UpdateStatisticConfig`; admin job routes in `AdminOperationsController` | `DynamicFormService.UpdateStatisticConfigAsync`; `WorkReportStatisticRebuildJobService` |
| Existing due reminders | User receives due work/assignment/report reminder | CTA `Thực hiện ngay` -> source item | Usually read-only; source completion may resolve related action notification if one exists | no user controller; Hangfire job | `NotificationDueScanJobService` |

Resolution rule:

- `ReadAtUtc`/`ClickedAtUtc` means the notification was seen or clicked.
- `ResolvedAtUtc`/`ActionState = RESOLVED` means the underlying source work was handled.
- Source services must resolve matching notifications by `RequestId` or `SourceEntityType +
  SourceEntityId` after the business mutation succeeds, even when the user worked directly from
  `Nhiệm vụ/Chỉ tiêu`, `Báo cáo`, or another source page instead of entering through the notification.

## Request Data Model Direction

Use dedicated workflow models for requests that have an approval lifecycle.

Recommended shared model: `WorkActionRequest`.

Fields:

- `Id`
- `RequestType`
- `Status`: `PENDING`, `APPROVED`, `REJECTED`, `CANCELLED`, `COMPLETED`, `FAILED`
- `WorkId`
- `WorkAssignmentId`
- `WorkReportPeriodId`
- `WorkAssignmentReportId`
- `DynamicFormTemplateId`
- `RequestedByUserId`
- `AssignedToUserId`
- `ApprovedByUserId`
- `RejectedByUserId`
- `SourceUserId`
- `TargetUserId`
- `Reason`
- `Comment`
- `DecisionComment`
- `FailureCode`
- `FailureMessage`
- `CreatedAtUtc`
- `DecidedAtUtc`
- `CompletedAtUtc`

`dynamic_form_clone_requests` may stay as a dedicated model in the current codebase. `Công việc cần
thực hiện` can aggregate it with `WorkActionRequest` rows through a read API. If the request types
grow, migrate clone requests into the shared model in a later slice.

## API Direction

Work-scoped action center:

- `POST /api/works/{workId}/requests/search`
- `GET /api/works/{workId}/requests/summary`

Handover request:

- `POST /api/work-assignments/{assignmentId}/handover-requests`
- `POST /api/work-action-requests/{id}/approve`
- `POST /api/work-action-requests/{id}/reject`
- `POST /api/work-action-requests/{id}/cancel`

Dynamic Form clone request can keep existing APIs initially, but the action-center search should
surface those rows together with handover/action rows.

Notification API additions:

- `POST /api/notifications/search` should accept `workId`, `workAssignmentId`,
  category/requiresAction filters.
- `POST /api/notifications/{id}/resolve` may be added only if the notification is not resolved by a
  source request state change.

## Implementation Slices

1. Documentation and contract cleanup.
2. Keep the Work Detail card launcher and add URL state for `tab=ASSIGN&section=ACTIONS`.
3. Merge `Nhiệm vụ` and `Chỉ tiêu` inside the assignment card content with top filters.
4. Add work-scoped `Thông báo` page under `Nhiệm vụ/Chỉ tiêu` and extend notification search with
   `workId`/`workAssignmentId` filters.
5. Add action-center search API aggregating existing clone requests, report-review work, and
   unfinished work/assignment/report items.
6. Convert handover UI from direct execution to request creation.
7. Add handover approval endpoint and internal runtime execution after recipient approval.
8. Extend notification model/types and producers for request lifecycle events.
9. Move clone approval UI from `Bao cao` into `Công việc cần thực hiện`.
10. Add status-risk/overdue notification producers and action-center filters.
11. Verify BE build, FE typecheck/build, and manual flows.

## Verification Checklist

- Work creator opens `Công việc cần thực hiện` from the `Nhiệm vụ/Chỉ tiêu` card.
- Work creator/assignee opens `Thông báo` from the `Nhiệm vụ/Chỉ tiêu` card and sees only
  notifications for the current `workId`.
- A notification row with `workAssignmentId` opens the matching assignment detail through
  `assignmentId` URL state.
- `Bao cao` card is hidden unless the current user has an assigned report lane.
- `Nhiệm vụ/Chỉ tiêu` contains one list page; task/target/status/assignee separation is handled by
  filters, not separate page tabs.
- `Công việc chưa thực hiện` shows the current user's unfinished works, assignments, and reports.
- Assignee creates a handover request and no runtime lane changes before approval.
- Target recipient receives a notification and pending request row.
- Rejecting a handover request leaves assignment/report/period/queue ownership unchanged.
- Approving a handover request executes the existing single-lane handover and writes history.
- If the source lane changes before approval, approval does not execute a stale handover.
- Clone permission approval grants Dynamic Form visibility/clone but not mutate/publish/delete.
- Report review/evaluation request items deep-link to the report context by work id.
- At-risk/overdue notifications appear once per dedupe key and do not change source statuses.
