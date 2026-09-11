using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowRuntimeReadContractTests
{
    public static void Run()
    {
        CursorContractsRoundTripAndFailClosed();
        PreflightPagingIsSnapshotBound();
        LimitsAndStatesAreExplicitlyValidated();
        CapabilitiesAreServerDerivedAndKeepP6Blocked();
        RecoveryStatusIsSafeAndDeterministic();
        RevisionTokensAreDeterministicAndScopeBound();
        TimelineReasonsAndAffectedRefsAreSanitized();
        RecoveryAttemptAggregationIsDeterministic();
        TypedDtosPreserveDeepIdentityWithoutRawPayloads();
        TypedReadRoutesAreFrozen();
    }

    private static void CursorContractsRoundTripAndFailClosed()
    {
        Require(
            DynamicFlowRuntimeReadContract.InstanceCursorKind == "INSTANCE_UPDATED_DESC" &&
            DynamicFlowRuntimeReadContract.InboxCursorKind == "INBOX_UPDATED_DESC" &&
            DynamicFlowRuntimeReadContract.StepCursorKind == "STEP_IDENTITY_ASC" &&
            DynamicFlowRuntimeReadContract.TimelineCursorKind == "TIMELINE_SEQUENCE_ASC",
            "runtime cursor kinds drifted");

        var updatedAtUtc = new DateTime(2026, 7, 24, 3, 4, 5, DateTimeKind.Utc);
        var instanceCursorValue = DynamicFlowRuntimeReadContract.EncodeUpdatedCursor(
            DynamicFlowRuntimeReadContract.InstanceCursorKind,
            updatedAtUtc,
            Oid(1));
        var instanceCursor = DynamicFlowRuntimeReadContract.DecodeUpdatedCursor(
            instanceCursorValue,
            DynamicFlowRuntimeReadContract.InstanceCursorKind)
            ?? throw new InvalidOperationException("instance cursor must round trip");
        Require(instanceCursor.Kind == DynamicFlowRuntimeReadContract.InstanceCursorKind, "instance cursor kind drifted");
        Require(instanceCursor.UpdatedAtUtc == updatedAtUtc, "instance cursor timestamp drifted");
        Require(instanceCursor.Id == Oid(1), "instance cursor id drifted");
        Require(
            instanceCursor.AnchorUpdatedAtUtc == updatedAtUtc &&
            instanceCursor.AnchorId == Oid(1) &&
            instanceCursor.Total == 0,
            "updated cursor snapshot anchor drifted");
        Require(
            !instanceCursorValue.Contains('+') &&
            !instanceCursorValue.Contains('/') &&
            !instanceCursorValue.Contains('='),
            "runtime cursor must remain URL-safe");

        var stepCursorValue = DynamicFlowRuntimeReadContract.EncodeStepCursor(
            "entry-step",
            Oid(2),
            3,
            17,
            "actor-work-instance");
        var stepCursor = DynamicFlowRuntimeReadContract.DecodeStepCursor(
                stepCursorValue,
                "actor-work-instance")
            ?? throw new InvalidOperationException("step cursor must round trip");
        Require(stepCursor.StepId == "entry-step", "step-definition cursor identity drifted");
        Require(stepCursor.TargetUnitId == Oid(2), "step target-unit cursor identity drifted");
        Require(stepCursor.AttemptNo == 3, "step attempt cursor identity drifted");
        Require(stepCursor.Total == 17, "step snapshot total drifted");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.DecodeStepCursor(
                stepCursorValue,
                "another-actor-or-instance"),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");

        var timelineCursor = DynamicFlowRuntimeReadContract.DecodeTimelineCursor(
            DynamicFlowRuntimeReadContract.EncodeTimelineCursor(42));
        Require(timelineCursor?.Sequence == 42, "timeline sequence cursor drifted");
        Require(
            timelineCursor?.HighWaterSequence == 42 &&
            timelineCursor.Total == 0,
            "timeline cursor high-water contract drifted");

        Require(
            DynamicFlowRuntimeReadContract.DecodeUpdatedCursor(
                null,
                DynamicFlowRuntimeReadContract.InstanceCursorKind) is null,
            "empty updated cursor must mean first page");
        Require(
            DynamicFlowRuntimeReadContract.DecodeStepCursor(" ") is null,
            "blank step cursor must mean first page");
        Require(
            DynamicFlowRuntimeReadContract.DecodeTimelineCursor(null) is null,
            "empty timeline cursor must mean first page");

        AssertValidation(
            () => DynamicFlowRuntimeReadContract.DecodeUpdatedCursor(
                instanceCursorValue,
                DynamicFlowRuntimeReadContract.InboxCursorKind),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.DecodeUpdatedCursor(
                DynamicFlowRuntimeReadContract.EncodeUpdatedCursor(
                    DynamicFlowRuntimeReadContract.InstanceCursorKind,
                    updatedAtUtc,
                    "not-an-object-id"),
                DynamicFlowRuntimeReadContract.InstanceCursorKind),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.DecodeStepCursor(
                DynamicFlowRuntimeReadContract.EncodeStepCursor(
                    "entry-step",
                    Oid(2),
                    0,
                    0)),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.DecodeTimelineCursor(
                DynamicFlowRuntimeReadContract.EncodeTimelineCursor(-1)),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.DecodeTimelineCursor("not-base64"),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");
    }

    private static void PreflightPagingIsSnapshotBound()
    {
        static DynamicFlowPreflightResponse Preview(string token)
            => new()
            {
                SnapshotToken = token,
                Targets =
                [
                    new DynamicFlowTargetSnapshotDto { TargetUnitId = Oid(3) },
                    new DynamicFlowTargetSnapshotDto { TargetUnitId = Oid(1) },
                    new DynamicFlowTargetSnapshotDto { TargetUnitId = Oid(2) }
                ]
            };

        var first = DynamicFlowRuntimeReadContract.PagePreflight(
            Preview("snapshot-a"),
            new DynamicFlowPreflightPageQuery { Limit = 2 });
        Require(first.TargetTotal == 3, "preflight target total drifted");
        Require(first.TargetLimit == 2, "preflight target limit drifted");
        Require(first.TargetHasMore, "preflight first page must have a continuation");
        Require(
            first.Targets.Select(item => item.TargetUnitId)
                .SequenceEqual(new[] { Oid(1), Oid(2) }, StringComparer.Ordinal),
            "preflight target order/page boundary drifted");
        Require(
            !string.IsNullOrWhiteSpace(first.TargetNextCursor),
            "preflight target cursor missing");

        var second = DynamicFlowRuntimeReadContract.PagePreflight(
            Preview("snapshot-a"),
            new DynamicFlowPreflightPageQuery
            {
                Limit = 2,
                Cursor = first.TargetNextCursor
            });
        Require(
            second.Targets.Select(item => item.TargetUnitId)
                .SequenceEqual(new[] { Oid(3) }, StringComparer.Ordinal) &&
            !second.TargetHasMore &&
            second.TargetTotal == first.TargetTotal,
            "preflight continuation must preserve snapshot total and boundary");

        AssertValidation(
            () => DynamicFlowRuntimeReadContract.PagePreflight(
                Preview("snapshot-b"),
                new DynamicFlowPreflightPageQuery
                {
                    Limit = 2,
                    Cursor = first.TargetNextCursor
                }),
            "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID");
    }

    private static void LimitsAndStatesAreExplicitlyValidated()
    {
        Require(DynamicFlowRuntimeReadContract.MaxPageLimit == 200, "runtime page maximum drifted");
        Require(DynamicFlowRuntimeReadContract.RequireLimit(1) == 1, "minimum page limit must be accepted");
        Require(
            DynamicFlowRuntimeReadContract.RequireLimit(
                DynamicFlowRuntimeReadContract.MaxPageLimit) ==
            DynamicFlowRuntimeReadContract.MaxPageLimit,
            "maximum page limit must be accepted without a silent cap");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.RequireLimit(0),
            "DYNAMIC_FLOW_RUNTIME_PAGE_LIMIT_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.RequireLimit(
                DynamicFlowRuntimeReadContract.MaxPageLimit + 1),
            "DYNAMIC_FLOW_RUNTIME_PAGE_LIMIT_INVALID");

        foreach (var state in DynamicFlowInstanceStates.All)
        {
            Require(
                DynamicFlowRuntimeReadContract.RequireInstanceState(
                    $" {state.ToLowerInvariant()} ") == state,
                $"instance state normalization drifted for {state}");
        }
        foreach (var state in DynamicFlowStepStates.All)
        {
            Require(
                DynamicFlowRuntimeReadContract.RequireStepState(
                    $" {state.ToLowerInvariant()} ") == state,
                $"step state normalization drifted for {state}");
        }

        AssertValidation(
            () => DynamicFlowRuntimeReadContract.RequireInstanceState("ASSIGNED"),
            "DYNAMIC_FLOW_RUNTIME_STATE_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.RequireStepState("ACTIVE"),
            "DYNAMIC_FLOW_RUNTIME_STATE_INVALID");
        AssertValidation(
            () => DynamicFlowRuntimeReadContract.RequireStepState(" "),
            "DYNAMIC_FLOW_RUNTIME_STATE_INVALID");

        Require(new DynamicFlowRuntimePageQuery().Limit == 25, "page query default limit drifted");
        var inbox = new DynamicFlowRuntimeInboxQuery();
        Require(inbox.Limit == 25, "inbox default limit drifted");
        Require(inbox.State == DynamicFlowStepStates.Assigned, "inbox must use one indexed state by default");
    }

    private static void CapabilitiesAreServerDerivedAndKeepP6Blocked()
    {
        var issuer = DynamicFlowRuntimeReadContract.BuildInstanceCapabilities(
            revision: 17,
            isIssuer: true);
        Require(issuer.CanViewOverview && issuer.CanViewTimeline, "issuer read capabilities drifted");
        Require(issuer.CanViewAllBranches, "issuer must receive full branch visibility capability");
        Require(issuer.ExpectedRevision == 17, "instance capability revision drifted");
        RequireNoP5RecoveryOrP6Actions(issuer, "issuer instance");

        var participant = DynamicFlowRuntimeReadContract.BuildInstanceCapabilities(
            revision: 18,
            isIssuer: false);
        Require(!participant.CanViewAllBranches, "participant must not infer sibling branch visibility");
        RequireNoP5RecoveryOrP6Actions(participant, "participant instance");

        foreach (var state in DynamicFlowStepStates.All)
        {
            var inert = DynamicFlowRuntimeReadContract.BuildStepCapabilities(
                revision: 23,
                state,
                isIssuer: true,
                isReporter: false,
                isReviewer: false,
                hasAssignment: true,
                hasReport: true);
            Require(inert.ExpectedRevision == 23, $"step revision capability drifted for {state}");
            Require(inert.CanOpenAssignment && inert.CanOpenReport, $"step refs must drive open actions for {state}");
            Require(!inert.CanSubmitReport && !inert.CanReviewReport, $"roles must drive lifecycle actions for {state}");
            RequireNoP5RecoveryOrP6Actions(inert, $"step {state}");
        }

        foreach (var state in new[]
                 {
                     DynamicFlowStepStates.Assigned,
                     DynamicFlowStepStates.InProgress,
                     DynamicFlowStepStates.Returned
                 })
        {
            var reporter = DynamicFlowRuntimeReadContract.BuildStepCapabilities(
                7,
                state,
                isIssuer: false,
                isReporter: true,
                isReviewer: false,
                hasAssignment: true,
                hasReport: state != DynamicFlowStepStates.Assigned);
            Require(reporter.CanSubmitReport, $"reporter submit capability missing for {state}");
            Require(!reporter.CanReviewReport, $"reporter must not gain review capability for {state}");
        }

        var submittedReporter = DynamicFlowRuntimeReadContract.BuildStepCapabilities(
            8,
            DynamicFlowStepStates.Submitted,
            isIssuer: false,
            isReporter: true,
            isReviewer: false,
            hasAssignment: true,
            hasReport: true);
        Require(!submittedReporter.CanSubmitReport, "submitted reporter must not receive another submit action");

        var submittedReviewer = DynamicFlowRuntimeReadContract.BuildStepCapabilities(
            9,
            DynamicFlowStepStates.Submitted,
            isIssuer: false,
            isReporter: false,
            isReviewer: true,
            hasAssignment: true,
            hasReport: true);
        Require(submittedReviewer.CanReviewReport, "submitted reviewer must receive review capability");
        Require(!submittedReviewer.CanSubmitReport, "reviewer must not gain reporter submit capability");

        var approvedReviewer = DynamicFlowRuntimeReadContract.BuildStepCapabilities(
            10,
            DynamicFlowStepStates.Approved,
            isIssuer: false,
            isReporter: false,
            isReviewer: true,
            hasAssignment: true,
            hasReport: true);
        Require(!approvedReviewer.CanReviewReport, "approved report must not remain reviewable");
    }

    private static void RecoveryStatusIsSafeAndDeterministic()
    {
        var lastAttempt = new DateTime(2026, 7, 24, 4, 0, 0, DateTimeKind.Utc);
        var nextAttempt = lastAttempt.AddMinutes(2);
        var cases = new[]
        {
            (DynamicFlowInstanceStates.Partial, true, "DYNAMIC_FLOW_RUNTIME_PARTIAL", "WAIT_FOR_RECONCILE"),
            (DynamicFlowInstanceStates.Retrying, true, "DYNAMIC_FLOW_RUNTIME_RETRYING", "WAIT_FOR_RETRY"),
            (DynamicFlowInstanceStates.Reconciled, false, "DYNAMIC_FLOW_RUNTIME_RECONCILED", "REFRESH"),
            (DynamicFlowInstanceStates.Failed, true, "DYNAMIC_FLOW_RUNTIME_FAILED", "CONTACT_SYSTEM_ADMIN"),
            (DynamicFlowInstanceStates.Active, false, "DYNAMIC_FLOW_RUNTIME_HEALTHY", "NONE")
        };

        foreach (var (state, required, reason, action) in cases)
        {
            var recovery = DynamicFlowRuntimeReadContract.BuildRecovery(
                state,
                attemptCount: 4,
                lastAttempt,
                nextAttempt,
                reconcileRunning: state == DynamicFlowInstanceStates.Retrying);
            Require(recovery.State == state, $"recovery state drifted for {state}");
            Require(recovery.RecoveryRequired == required, $"recovery-required drifted for {state}");
            Require(recovery.ReasonCode == reason, $"safe recovery reason drifted for {state}");
            Require(recovery.NextAction == action, $"recovery next action drifted for {state}");
            Require(!string.IsNullOrWhiteSpace(recovery.StatusText), $"recovery text missing for {state}");
            Require(recovery.AttemptCount == 4, $"recovery attempt count drifted for {state}");
            Require(recovery.LastAttemptAtUtc == lastAttempt, $"last recovery attempt drifted for {state}");
            Require(recovery.NextAttemptAtUtc == nextAttempt, $"next recovery attempt drifted for {state}");
            Require(
                recovery.ReconcileStatus ==
                (state == DynamicFlowInstanceStates.Retrying
                    ? "RUNNING"
                    : state == DynamicFlowInstanceStates.Reconciled
                        ? "RECONCILED"
                        : "IDLE"),
                $"reconcile status drifted for {state}");
        }

        var reconciledReadback = DynamicFlowRuntimeReadContract.BuildRecovery(
            DynamicFlowInstanceStates.Active,
            attemptCount: 2,
            lastAttempt,
            nextAttemptAtUtc: null,
            reconcileRunning: false,
            lastOutcome: DynamicFlowRuntimeEventTypes.RecoveryCompleted);
        Require(!reconciledReadback.RecoveryRequired, "completed recovery readback must be healthy");
        Require(
            reconciledReadback.ReasonCode == "DYNAMIC_FLOW_RUNTIME_RECONCILED" &&
            reconciledReadback.ReconcileStatus == "RECONCILED" &&
            reconciledReadback.NextAction == "REFRESH",
            "completed recovery readback status drifted");

        var clamped = DynamicFlowRuntimeReadContract.BuildRecovery(
            DynamicFlowInstanceStates.Active,
            attemptCount: -3,
            lastAttemptAtUtc: null,
            nextAttemptAtUtc: null,
            reconcileRunning: false);
        Require(clamped.AttemptCount == 0, "negative recovery attempts must never reach the client");
    }

    private static void RevisionTokensAreDeterministicAndScopeBound()
    {
        var instanceId = Oid(11);
        var stableA = DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(instanceId, 5, 2);
        var stableB = DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(instanceId, 5, 2);
        Require(stableA == stableB, "instance revision token must be deterministic");
        Require(IsSha256(stableA), "instance revision token must remain a lowercase SHA-256 value");
        Require(
            stableA != DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(instanceId, 6, 2),
            "instance revision token must bind business revision");
        Require(
            stableA != DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(instanceId, 5, 3),
            "instance revision token must bind recovery epoch");
        Require(
            stableA != DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(Oid(12), 5, 2),
            "instance revision token must bind instance identity");

        var stepToken = DynamicFlowRuntimeReadContract.BuildStepRevisionToken(instanceId, 5);
        Require(IsSha256(stepToken), "step revision token must remain a lowercase SHA-256 value");
        Require(stepToken != stableA, "instance and step revision tokens must be domain separated");
        Require(
            stepToken != DynamicFlowRuntimeReadContract.BuildStepRevisionToken(instanceId, 6),
            "step revision token must bind revision");
        Require(
            stepToken != DynamicFlowRuntimeReadContract.BuildStepRevisionToken(Oid(13), 5),
            "step revision token must bind step-instance identity");
    }

    private static void TimelineReasonsAndAffectedRefsAreSanitized()
    {
        Require(
            DynamicFlowRuntimeReadContract.SanitizeReasonCode(null) is null &&
            DynamicFlowRuntimeReadContract.SanitizeReasonCode(" ") is null,
            "empty event reasons must remain absent");
        Require(
            DynamicFlowRuntimeReadContract.SanitizeReasonCode(" report_lifecycle_submitted ") ==
            "REPORT_LIFECYCLE_SUBMITTED",
            "public report lifecycle reason must normalize and survive");
        foreach (var reason in new[]
                 {
                     "ASSIGNMENT_COMPLETED",
                     "STATE_RECONCILE",
                     "STATE_RECONCILE_INSTANCE_COMPLETION",
                     "ALL_RUNTIME_STEPS_COMPLETED"
                 })
        {
            Require(
                DynamicFlowRuntimeReadContract.SanitizeReasonCode(reason) == reason,
                $"public runtime reason was unexpectedly redacted: {reason}");
        }
        foreach (var sensitive in new[]
                 {
                     "DYNAMIC_FLOW_RUNTIME_MONGO_FAILURE",
                     "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE",
                     "SOURCE_EVENT_PRIVATE_DETAIL",
                     "some exception message"
                 })
        {
            Require(
                DynamicFlowRuntimeReadContract.SanitizeReasonCode(sensitive) ==
                "DYNAMIC_FLOW_RUNTIME_STATE_CHANGED",
                $"sensitive runtime reason leaked: {sensitive}");
        }

        var reporterStep = Step(21, 31, 41, actorUserId: Oid(91));
        var reviewerStep = Step(22, 32, 42, actorUserId: Oid(92));
        var scope = DynamicFlowRuntimeReadScope.BranchScoped(
            Oid(91),
            Oid(81),
            [reporterStep],
            [reviewerStep],
            [reviewerStep.AssignmentId!]);
        Require(
            scope.VisibilityScopes.SequenceEqual(
                new[]
                {
                    DynamicFlowRuntimeVisibilityScopes.Reporter,
                    DynamicFlowRuntimeVisibilityScopes.Reviewer
                },
                StringComparer.Ordinal),
            "union visibility scopes drifted");
        Require(
            scope.VisibleStepIds.SetEquals(new[] { reporterStep.Id, reviewerStep.Id }),
            "branch scope must retain only explicitly visible step instances");

        var filtered = scope.FilterAffectedRefs(
            [
                $"report:{reviewerStep.ReportId}",
                $"assignment:{reporterStep.AssignmentId}",
                $"step:{reporterStep.Id}",
                $"assignment:{Oid(999)}",
                $"step:{reporterStep.Id}"
            ],
            [$"report:{reviewerStep.ReportId}"]);
        Require(
            filtered.SequenceEqual(
                new[]
                {
                    $"assignment:{reporterStep.AssignmentId}",
                    $"report:{reviewerStep.ReportId}",
                    $"step:{reporterStep.Id}"
                },
                StringComparer.Ordinal),
            "branch timeline must remove sibling refs and return deterministic ordering");
        Require(
            !scope.FilterAffectedRefs([$"report:{reviewerStep.ReportId}"]).Any(),
            "branch timeline must fail closed for report refs without actor-authorized report identity");

        Require(
            scope.ScopesFor(reporterStep, Oid(91))
                .SequenceEqual(
                    new[] { DynamicFlowRuntimeVisibilityScopes.Reporter },
                    StringComparer.Ordinal),
            "reporter step scope drifted");
        Require(
            scope.ScopesFor(reviewerStep, Oid(91))
                .SequenceEqual(
                    new[] { DynamicFlowRuntimeVisibilityScopes.Reviewer },
                    StringComparer.Ordinal),
            "reviewer step scope drifted");

        var issuer = DynamicFlowRuntimeReadScope.Issuer(Oid(90), Oid(80));
        Require(
            issuer.VisibilityScopes.SequenceEqual(
                new[] { DynamicFlowRuntimeVisibilityScopes.Issuer },
                StringComparer.Ordinal),
            "issuer scope drifted");
        Require(
            issuer.FilterAffectedRefs(
                    ["report:z", "assignment:a", "report:z", "private:x"])
                .SequenceEqual(
                new[] { "assignment:a", "report:z" },
                StringComparer.Ordinal),
            "issuer timeline refs must remain distinct and deterministic");

        Require(
            DynamicFlowRuntimeReadContract.SanitizeEventType(
                DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged) ==
            DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged,
            "public runtime event type was unexpectedly redacted");
        Require(
            DynamicFlowRuntimeReadContract.SanitizeEventType(
                "PRIVATE_RECOVERY_DIAGNOSTIC") ==
            "DYNAMIC_FLOW_RUNTIME_STATE_CHANGED",
            "unknown runtime event type must be redacted");
    }

    private static void RecoveryAttemptAggregationIsDeterministic()
    {
        var at1 = new DateTime(2026, 7, 24, 1, 0, 0, DateTimeKind.Utc);
        var at2 = at1.AddMinutes(1);
        var at3 = at2.AddMinutes(1);
        var rows = new[]
        {
            Outbox(1, DynamicFlowRuntimeOutboxStatuses.Completed, 2, at1, at3.AddMinutes(5)),
            Outbox(2, DynamicFlowRuntimeOutboxStatuses.Failed, 3, at2, at3.AddMinutes(2)),
            Outbox(3, DynamicFlowRuntimeOutboxStatuses.Pending, 4, at3, at3.AddMinutes(1)),
            Outbox(4, DynamicFlowRuntimeOutboxStatuses.DeadLetter, 7, at3.AddMinutes(1), at1)
        };
        var snapshot = RecoveryAttemptSnapshot.From(rows);
        Require(snapshot.AttemptCount == 7, "recovery aggregate must preserve maximum observed attempt count");
        Require(snapshot.LastAttemptAtUtc == at3.AddMinutes(1), "recovery aggregate last attempt drifted");
        Require(snapshot.NextAttemptAtUtc == at3.AddMinutes(1), "recovery aggregate must choose earliest actionable retry");
        Require(
            RecoveryAttemptSnapshot.Empty.AttemptCount == 0 &&
            RecoveryAttemptSnapshot.Empty.LastAttemptAtUtc is null &&
            RecoveryAttemptSnapshot.Empty.NextAttemptAtUtc is null,
            "empty recovery aggregate drifted");
    }

    private static void TypedDtosPreserveDeepIdentityWithoutRawPayloads()
    {
        RequireProperties<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>>(
            "Items", "Total", "Limit", "HasMore", "NextCursor");
        RequireProperties<DynamicFlowRuntimeInstanceListRow>(
            "WorkId", "FlowInstanceId", "FlowTemplateId", "FlowTemplateVersionId",
            "FlowTemplateVersionNo", "EntryFlowStepId", "PeriodKey", "State",
            "Revision", "RuntimeRecoveryEpoch", "RevisionToken", "Capabilities", "Recovery");
        RequireProperties<DynamicFlowRuntimeInstanceOverview>(
            "WorkId", "FlowInstanceId", "ParticipantSnapshotId", "VisibleStepCount",
            "Revision", "RevisionToken", "VisibilityScopes", "Capabilities", "Recovery");
        RequireProperties<DynamicFlowRuntimeStepRow>(
            "WorkId", "FlowInstanceId", "FlowStepDefinitionId", "StepInstanceId",
            "BranchId", "AttemptNo", "TargetUnitId", "AssignmentId", "ReportId",
            "ReportIds", "SubmitReportId", "ReviewReportId",
            "FormNodeId", "FormFamilyId", "FormVersionId", "FormVersionNo",
            "FormSchemaHash", "FormSnapshotHash", "State", "Revision", "RevisionToken",
            "VisibilityScopes", "Capabilities", "Recovery");
        RequireProperties<DynamicFlowRuntimeTimelineRow>(
            "WorkId", "FlowInstanceId", "EventId", "StepInstanceId",
            "FlowStepDefinitionId", "BranchId", "AttemptNo", "TargetUnitId",
            "AssignmentId", "ReportId", "FormVersionId", "FormVersionNo", "Sequence",
            "EventType", "FromState", "ToState", "FromRevision", "ToRevision",
            "ReasonCode", "AffectedRefs", "OccurredAtUtc");
        RequireProperties<DynamicFlowRuntimeRecoveryStatusDto>(
            "State", "RecoveryRequired", "StatusText", "ReasonCode", "NextAction",
            "AttemptCount", "LastAttemptAtUtc", "NextAttemptAtUtc", "ReconcileStatus",
            "ExpectedRevision", "RevisionToken", "CanRetry", "CanReconcile");
        RequireProperties<DynamicFlowRuntimeCapabilitiesDto>(
            "CanViewOverview", "CanViewTimeline", "CanViewAllBranches",
            "CanOpenAssignment", "CanOpenReport", "CanSubmitReport", "CanReviewReport",
            "CanRetry", "CanReconcile", "CanForward", "CanFinalize", "ExpectedRevision");

        var timelineProperties = typeof(DynamicFlowRuntimeTimelineRow)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var sensitive in new[]
                 {
                     "Payload",
                     "PayloadHash",
                     "SourceEventKey",
                     "VisibleUnitIds",
                     "RuntimeReconcileLeaseId",
                     "RuntimeReconcileLeaseExpiresAtUtc",
                     "LastErrorCode"
                 })
        {
            Require(!timelineProperties.Contains(sensitive), $"timeline DTO exposes sensitive field {sensitive}");
        }
    }

    private static void TypedReadRoutesAreFrozen()
    {
        Require(
            typeof(DynamicFlowRuntimeController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Length > 0,
            "runtime read routes must remain authenticated");
        var controllerRoute = typeof(DynamicFlowRuntimeController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .SingleOrDefault();
        Require(controllerRoute?.Template == "api", "runtime controller prefix drifted");

        AssertGetRoute(
            nameof(DynamicFlowRuntimeController.GetInstances),
            "works/{workId}/dynamic-flows/instances");
        AssertGetRoute(
            nameof(DynamicFlowRuntimeController.GetInstance),
            "works/{workId}/dynamic-flows/instances/{flowInstanceId}");
        AssertGetRoute(
            nameof(DynamicFlowRuntimeController.GetSteps),
            "works/{workId}/dynamic-flows/instances/{flowInstanceId}/steps");
        AssertGetRoute(
            nameof(DynamicFlowRuntimeController.GetInbox),
            "dynamic-flows/inbox");
        AssertGetRoute(
            nameof(DynamicFlowRuntimeController.GetTimeline),
            "works/{workId}/dynamic-flows/instances/{flowInstanceId}/timeline");
        AssertGetRoute(
            nameof(DynamicFlowRuntimeController.GetRecovery),
            "works/{workId}/dynamic-flows/instances/{flowInstanceId}/recovery");

        var readMethods = typeof(IDynamicFlowRuntimeReadService)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(
            readMethods.SequenceEqual(
                new[]
                {
                    "GetInboxAsync",
                    "GetInstanceAsync",
                    "GetInstancesAsync",
                    "GetRecoveryStatusAsync",
                    "GetStepsAsync",
                    "GetTimelineAsync"
                },
                StringComparer.Ordinal),
            "runtime read service surface drifted");
    }

    private static DynamicFlowStepInstance Step(
        int stepSeed,
        int assignmentSeed,
        int reportSeed,
        string actorUserId)
        => new()
        {
            Id = Oid(stepSeed),
            FlowInstanceId = Oid(100),
            FlowStepId = $"step-{stepSeed}",
            TargetUnitId = Oid(80),
            ParticipantUserIds = [actorUserId],
            BranchId = Oid(stepSeed + 100),
            AssignmentId = Oid(assignmentSeed),
            ReportId = Oid(reportSeed),
            AttemptNo = 1
        };

    private static DynamicFlowRuntimeOutboxItem Outbox(
        int seed,
        string status,
        int attemptCount,
        DateTime updatedAtUtc,
        DateTime nextAttemptAtUtc)
        => new()
        {
            Id = Oid(seed),
            FlowInstanceId = Oid(100),
            StepInstanceId = Oid(seed + 100),
            Status = status,
            AttemptCount = attemptCount,
            UpdatedAtUtc = updatedAtUtc,
            NextAttemptAtUtc = nextAttemptAtUtc
        };

    private static void RequireNoP5RecoveryOrP6Actions(
        DynamicFlowRuntimeCapabilitiesDto capabilities,
        string scope)
    {
        Require(!capabilities.CanRetry, $"{scope} must not expose an unimplemented retry action");
        Require(!capabilities.CanReconcile, $"{scope} must not expose the admin reconcile operation");
        Require(!capabilities.CanForward, $"{scope} must keep positive forward P6-blocked");
        Require(!capabilities.CanFinalize, $"{scope} must keep positive finalize P6-blocked");
    }

    private static void AssertGetRoute(string methodName, string expectedTemplate)
    {
        var method = typeof(DynamicFlowRuntimeController).GetMethod(methodName)
            ?? throw new MissingMethodException(nameof(DynamicFlowRuntimeController), methodName);
        var route = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
            .Cast<HttpGetAttribute>()
            .SingleOrDefault();
        Require(route?.Template == expectedTemplate, $"{methodName} route drifted");
    }

    private static void RequireProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in expected)
            Require(actual.Contains(property), $"{typeof(T).Name} missing {property}");
    }

    private static void AssertValidation(Action action, string expectedReason)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            Require(
                error.Code == AppErrorCode.COMMON_VALIDATION_FAILED,
                $"expected validation error, got {error.Code}");
            var reason = error.Details?.GetType()
                .GetProperty("reason")
                ?.GetValue(error.Details) as string;
            Require(reason == expectedReason, $"expected {expectedReason}, got {reason ?? "<null>"}");
            return;
        }

        throw new InvalidOperationException($"Expected validation reason {expectedReason}.");
    }

    private static bool IsSha256(string value)
        => value.Length == 64 &&
           value == value.ToLowerInvariant() &&
           value.All(Uri.IsHexDigit);

    private static string Oid(int seed)
        => seed.ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
