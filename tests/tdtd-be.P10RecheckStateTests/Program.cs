using System.Collections.Immutable;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Auth;
using tdtd_be.Controllers;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

const string RunId = "0123456789abcdef01234567";
const string ActorId = "1123456789abcdef01234567";
var at = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-RECHECK-01", DurableBeginReplayAfterCompletion),
    ("P10-RECHECK-02", ConcurrentBeginAndClaimHaveOneWinner),
    ("P10-RECHECK-03", OldCurrentVisibleUntilExactPromotion),
    ("P10-RECHECK-04", SameGenerationAndStaleFenceFailClosed),
    ("P10-RECHECK-05", RetryPreservesBaseAndTerminalAbortRestoresIt),
    ("P10-RECHECK-06", PromotionCrashReplayConverges),
    ("P10-RECHECK-07", AllFrozenTerminalStatusesPromote),
    ("P10-RECHECK-08", CaptureBindingTamperAndMixedSelectorFail),
    ("P10-RECHECK-09", ActiveAndDurableCurrentViewsNeverAlias),
    ("P10-RECHECK-10", ReviewReplaySurvivesRevisionFence),
    ("P10-RECHECK-11", LostReviewFenceReturnsStaleCas),
    ("P10-RECHECK-12", FinalizeReceiptBindsWholeSwap),
    ("P10-RECHECK-13", SupersessionReplaySurvivesClearedRevision),
    ("P10-RECHECK-14", IdentityMismatchRequiresTrustedRemediation),
    ("P10-RECHECK-15", ProductionLeaseRoutesAuthorizeBeforeBody)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine(
            $"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == cases.Length, "EXACT_CASE_COUNT");
Console.WriteLine(
    $"P10_RECHECK_STATE_OK cases={cases.Length} beginReceipt=true concurrency=true " +
    "oldCurrentVisible=true distinctSuccessor=true retry=true " +
    "promotionCrashReplay=true terminalMatrix=4 binding=true " +
    "reviewRevisionFence=true finalizeReceipt=true");
return 0;

Task DurableBeginReplayAfterCompletion()
{
    var (machine, command, binding) = Machine('1');
    var first = machine.Begin(command, ActorId, H('3'), H('4'), binding,
        at, TimeSpan.FromHours(1));
    var lease = machine.Claim("worker-1", at, TimeSpan.FromMinutes(1));
    var pending = machine.PublishTrusted(new(
        RunId, first.MarkerId, lease.WorkerId, lease.ClaimToken,
        H('5'), H('6')), at.AddSeconds(1));
    var promotion = machine.PreparePromotion(
        StatisticReconciliationRunStatuses.Matched, H('7'), H('8'));
    var promoted = machine.RecordTrustedPromotion(promotion);
    machine.CompleteReviewSupersession(first.MarkerId,
        promoted.StateRevision, promoted.StateHash);
    var replay = machine.Begin(command, ActorId, H('3'), H('4'), binding,
        at.AddMinutes(5), TimeSpan.FromHours(1));
    Require(replay.Replayed && replay.MarkerId == first.MarkerId,
        "DURABLE_BEGIN_REPLAY");
    ExpectCode(() => machine.Begin(command with
        {
            ExpectedStateHash = H('9')
        }, ActorId, H('3'), H('4'), binding, at.AddMinutes(6),
        TimeSpan.FromHours(1)),
        StatisticReconciliationRecheckFailureCodes.ReplayMismatch);
    Require(pending.CurrentGenerationId == H('1'),
        "PRE_PROMOTION_CURRENT_UNCHANGED");
    return Task.CompletedTask;
}

async Task ConcurrentBeginAndClaimHaveOneWinner()
{
    var (machine, command, binding) = Machine('a');
    var beginResults = await Task.WhenAll(
        Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            machine.Begin(command, ActorId, H('b'), H('c'), binding,
                at, TimeSpan.FromHours(1)))));
    Require(beginResults.Count(value => !value.Replayed) == 1 &&
            beginResults.Select(value => value.MarkerId).Distinct().Count() == 1,
        "BEGIN_SINGLE_WINNER");

    var claims = await Task.WhenAll(
        Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            try
            {
                return machine.Claim($"worker-{index}", at,
                    TimeSpan.FromMinutes(1));
            }
            catch (StatisticReconciliationRecheckException)
            {
                return null;
            }
        })));
    Require(claims.Count(value => value is not null) == 1,
        "CLAIM_SINGLE_WINNER");
}

Task OldCurrentVisibleUntilExactPromotion()
{
    var (machine, command, binding) = Machine('d');
    var receipt = machine.Begin(command, ActorId, H('e'), H('f'), binding,
        at, TimeSpan.FromHours(1));
    Equal(H('d'), machine.Read().CurrentGenerationId,
        "BEGIN_OLD_CURRENT_VISIBLE");
    var lease = machine.Claim("worker", at, TimeSpan.FromMinutes(1));
    Equal(H('d'), machine.Read().CurrentGenerationId,
        "CAPTURE_OLD_CURRENT_VISIBLE");
    machine.PublishTrusted(new(RunId, receipt.MarkerId, lease.WorkerId,
        lease.ClaimToken, H('1'), H('2')), at.AddSeconds(1));
    Equal(H('d'), machine.Read().CurrentGenerationId,
        "PENDING_OLD_CURRENT_VISIBLE");
    var promotion = machine.PreparePromotion(
        StatisticReconciliationRunStatuses.Mismatched, H('3'), H('4'));
    var post = machine.RecordTrustedPromotion(promotion);
    Equal(H('1'), post.CurrentGenerationId, "PROMOTION_SWAPS_CURRENT");
    return Task.CompletedTask;
}

Task SameGenerationAndStaleFenceFailClosed()
{
    var (machine, command, binding) = Machine('5');
    var receipt = machine.Begin(command, ActorId, H('6'), H('7'), binding,
        at, TimeSpan.FromHours(1));
    var lease = machine.Claim("worker", at, TimeSpan.FromMinutes(1));
    ExpectCode(() => machine.PublishTrusted(new(
        RunId, receipt.MarkerId, lease.WorkerId, lease.ClaimToken,
        H('5'), H('8')), at.AddSeconds(1)),
        StatisticReconciliationRecheckFailureCodes.SuccessorNotDistinct);
    ExpectCode(() => machine.RequireCapture(
        lease.WorkerId, H('9'), at.AddSeconds(1)),
        StatisticReconciliationRecheckFailureCodes.FenceStale);
    return Task.CompletedTask;
}

Task RetryPreservesBaseAndTerminalAbortRestoresIt()
{
    var (machine, command, binding) = Machine('9', maxRetry: 2);
    machine.Begin(command, ActorId, H('a'), H('b'), binding,
        at, TimeSpan.FromHours(1));
    var first = machine.Claim("worker-1", at, TimeSpan.FromMinutes(1));
    var retry = machine.ReleaseCapture(first.WorkerId, first.ClaimToken,
        at.AddSeconds(1), transient: true);
    Equal(H('9'), retry.CurrentGenerationId, "RETRY_BASE_VISIBLE");
    Require(retry.Recheck?.Phase ==
        StatisticReconciliationRecheckPhases.ReadyToClaim,
        "RETRY_MARKER_RESET");
    var second = machine.Claim("worker-2", at.AddSeconds(1),
        TimeSpan.FromMinutes(1));
    var aborted = machine.ReleaseCapture(second.WorkerId, second.ClaimToken,
        at.AddSeconds(2), transient: true);
    Require(aborted.Recheck is null &&
            aborted.Status == StatisticReconciliationRunStatuses.Matched &&
            aborted.CurrentGenerationId == H('9'),
        "TERMINAL_ABORT_RESTORES_BASE");
    return Task.CompletedTask;
}

Task PromotionCrashReplayConverges()
{
    var (machine, command, binding) = Machine('c');
    var begin = machine.Begin(command, ActorId, H('d'), H('e'), binding,
        at, TimeSpan.FromHours(1));
    var lease = machine.Claim("worker", at, TimeSpan.FromMinutes(1));
    machine.PublishTrusted(new(RunId, begin.MarkerId, lease.WorkerId,
        lease.ClaimToken, H('f'), H('0')), at.AddSeconds(1));
    var commandToPromote = machine.PreparePromotion(
        StatisticReconciliationRunStatuses.Stale, H('1'), H('2'));
    var first = machine.RecordTrustedPromotion(commandToPromote);
    var replay = machine.RecordTrustedPromotion(commandToPromote);
    Require(!first.Replayed && replay.Replayed &&
            replay.StateRevision == first.StateRevision &&
            replay.StateHash == first.StateHash,
        "CRASH_AFTER_SWAP_REPLAY");
    var cleared = machine.CompleteReviewSupersession(begin.MarkerId,
        replay.StateRevision, replay.StateHash);
    Require(cleared.Recheck is null &&
            cleared.CurrentGenerationId == H('f'),
        "CRASH_AFTER_REVIEW_CLEAR");
    return Task.CompletedTask;
}

Task AllFrozenTerminalStatusesPromote()
{
    var index = 0;
    foreach (var status in StatisticReconciliationRecheckTerminalStatuses.All)
    {
        var seed = "123456789abcdef0"[index++];
        var (machine, command, binding) = Machine(seed);
        var begin = machine.Begin(command, ActorId, H('1'), H('2'), binding,
            at, TimeSpan.FromHours(1));
        var lease = machine.Claim("worker", at, TimeSpan.FromMinutes(1));
        machine.PublishTrusted(new(RunId, begin.MarkerId, lease.WorkerId,
            lease.ClaimToken, H('e'), H('f')), at.AddSeconds(1));
        var promoted = machine.RecordTrustedPromotion(
            machine.PreparePromotion(status, H('a'), H('b')));
        Equal(status, promoted.TerminalStatus, $"TERMINAL_{status}");
    }
    return Task.CompletedTask;
}

Task CaptureBindingTamperAndMixedSelectorFail()
{
    var binding = Binding('1');
    StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(binding);
    binding.SourcePayloadHash = H('2');
    Expect<InvalidOperationException>(() =>
        StatisticReconciliationRecheckCaptureBindingCanonical
            .RequireValid(binding));

    binding = Binding('3');
    binding.ActualCapturePlan.Basic.SnapshotId =
        "f123456789abcdef01234567";
    Expect<InvalidOperationException>(() =>
        StatisticReconciliationRecheckCaptureBindingCanonical
            .RequireValid(binding));
    return Task.CompletedTask;
}

Task ActiveAndDurableCurrentViewsNeverAlias()
{
    var current = Binding('4');
    var next = Binding('5');
    var marker = Marker(next, H('6'), H('7'));
    var run = new StatisticReconciliationRun
    {
        P9GenerationId = H('0'),
        P9GenerationHash = H('0'),
        CurrentGenerationRecheckCaptureBinding = current,
        Recheck = marker
    };
    var currentView = StatisticReconciliationRecheckCaptureBindingCanonical
        .EffectiveCurrentRun(run);
    var captureView = StatisticReconciliationRecheckCaptureBindingCanonical
        .EffectiveCaptureRun(run);
    Equal(current.P9GenerationId, currentView.P9GenerationId,
        "DURABLE_CURRENT_VIEW");
    Equal(next.P9GenerationId, captureView.P9GenerationId,
        "ACTIVE_CAPTURE_VIEW");
    Require(currentView.P9GenerationId != captureView.P9GenerationId,
        "VIEWS_DISJOINT");
    return Task.CompletedTask;
}

async Task ReviewReplaySurvivesRevisionFence()
{
    var backend = new StatisticReconciliationIndependentReviewInMemoryBackend();
    var service = new StatisticReconciliationIndependentReviewService(backend);
    var permission = Permission();
    var generation5 = ReviewGeneration(5);
    var command = ReviewCommand(generation5, 5);
    var first = await service.SubmitAsync(permission, generation5,
        command, at);
    var generation6 = generation5 with { StateRevision = 6 };
    var replay = await service.SubmitAsync(permission, generation6,
        command, at.AddSeconds(1));
    Require(!first.Replayed && replay.Replayed &&
            replay.Decision.DocumentSha256 == first.Decision.DocumentSha256,
        "REVIEW_REPLAY_AFTER_REVISION_INCREMENT");
}

async Task LostReviewFenceReturnsStaleCas()
{
    var service = new StatisticReconciliationIndependentReviewService(
        new LostFenceBackend());
    var error = await ExpectAsync<
        StatisticReconciliationIndependentReviewException>(() =>
        service.SubmitAsync(Permission(), ReviewGeneration(5),
            ReviewCommand(ReviewGeneration(5), 5), at));
    Equal(StatisticReconciliationIndependentReviewFailureCodes.StaleCas,
        error.Code, "LOST_FENCE_STALE_CAS");
}

Task FinalizeReceiptBindsWholeSwap()
{
    var binding = Binding('8');
    var marker = Marker(binding, H('9'), H('a'));
    marker.SuccessorGenerationId = H('b');
    marker.SuccessorGenerationHash = H('c');
    marker.Phase = StatisticReconciliationRecheckPhases.PendingPublished;
    StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);
    var receipt = StatisticReconciliationRunService
        .BuildRecheckFinalizeReceipt(marker, H('d'), H('e'),
            StatisticReconciliationRunStatuses.Matched, 12, at);
    var run = new StatisticReconciliationRun
    {
        Status = StatisticReconciliationRunStatuses.Matched,
        CurrentGenerationId = H('b'),
        CurrentGenerationHash = H('c'),
        CurrentGenerationRecheckCaptureBinding = binding,
        CurrentRecheckFinalizeReceipt = receipt
    };
    Require(StatisticReconciliationRunService
        .HasValidRecheckFinalizeReceipt(run), "FINALIZE_RECEIPT_VALID");
    receipt.BaseVerdictGenerationSha256 = H('f');
    Require(!StatisticReconciliationRunService
        .HasValidRecheckFinalizeReceipt(run), "FINALIZE_RECEIPT_TAMPER");
    return Task.CompletedTask;
}

async Task SupersessionReplaySurvivesClearedRevision()
{
    var backend = new StatisticReconciliationIndependentReviewInMemoryBackend();
    var service = new StatisticReconciliationIndependentReviewService(backend);
    var permission = Permission();
    var previous = ReviewGeneration(5);
    _ = await service.SubmitAsync(permission, previous,
        ReviewCommand(previous, 5), at);

    var successor12 = ReviewGeneration(12, 'b', 'c', 'd');
    var command = SupersessionCommand(previous, successor12, 12);
    var first = await service.SupersedeApprovalsAsync(permission,
        successor12, command, at.AddSeconds(1));
    var successor13 = successor12 with { StateRevision = 13 };
    var replay = await service.SupersedeApprovalsAsync(permission,
        successor13, command, at.AddSeconds(2));
    Require(first.Count == 1 && replay.Count == 1 &&
            replay[0].DocumentSha256 == first[0].DocumentSha256,
        "SUPERSESSION_REPLAY_AFTER_CLEAR_REVISION");
}

Task IdentityMismatchRequiresTrustedRemediation()
{
    var run = new StatisticReconciliationRun
    {
        Status = StatisticReconciliationRunStatuses.Matched
    };
    var verdict = new StatisticReconciliationReview
    {
        Verdict = StatisticReconciliationFinalVerdicts.Matched,
        CompleteEvidence = true,
        AllRequiredLayersZero = true,
        Signable = true,
        CloseoutAllowed = true,
        UnknownBlocksCloseout = false,
        MissingOrExtraIdentity = false
    };
    Require(StatisticReconciliationRunService
            .IsApprovedMatchedRecheckBase(run, verdict),
        "MATCHED_BASE_ACCEPTED");

    run.Status = StatisticReconciliationRunStatuses.Mismatched;
    verdict.Verdict = StatisticReconciliationFinalVerdicts.Mismatched;
    verdict.CompleteEvidence = true;
    verdict.AllRequiredLayersZero = false;
    verdict.Signable = false;
    verdict.CloseoutAllowed = false;
    verdict.MissingOrExtraIdentity = true;
    verdict.RootCauseClass =
        StatisticReconciliationRootCauseClasses.MissingIdentity;
    Require(StatisticReconciliationRunService
            .IsIdentityRemediationBase(run, verdict),
        "MISSING_IDENTITY_BASE_ACCEPTED");

    verdict.RootCauseClass =
        StatisticReconciliationRootCauseClasses.ExtraIdentity;
    Require(StatisticReconciliationRunService
            .IsIdentityRemediationBase(run, verdict),
        "EXTRA_IDENTITY_BASE_ACCEPTED");

    verdict.RootCauseClass = "VALUE_MISMATCH";
    Require(!StatisticReconciliationRunService
            .IsIdentityRemediationBase(run, verdict),
        "WRONG_ROOT_CAUSE_REJECTED");
    verdict.RootCauseClass =
        StatisticReconciliationRootCauseClasses.MissingIdentity;
    verdict.MissingOrExtraIdentity = false;
    Require(!StatisticReconciliationRunService
            .IsIdentityRemediationBase(run, verdict),
        "MISSING_IDENTITY_FLAG_REJECTED");
    return Task.CompletedTask;
}

async Task ProductionLeaseRoutesAuthorizeBeforeBody()
{
    foreach (var action in new[]
             { "Claim", "Heartbeat", "Fail", "AuthorizeRemediation" })
    {
        var method = typeof(StatisticReconciliationRecheckController)
            .GetMethod(action, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"ROUTE_{action}_MISSING");
        Require(method.GetParameters().All(parameter =>
                parameter.GetCustomAttribute<FromBodyAttribute>() is null),
            $"ROUTE_{action}_MUST_NOT_MODEL_BIND_BODY");

        var service = DispatchProxy.Create<IStatisticReconciliationRunService,
            RunServiceProbe>();
        var probe = (RunServiceProbe)(object)service;
        var body = new ThrowingReadStream();
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        var controller = new StatisticReconciliationRecheckController(
            service,
            new MeAccessor(new HttpContextAccessor
            {
                HttpContext = context
            }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = context
            }
        };
        _ = await ExpectAsync<Exception>(async () =>
        {
            if (action == "Claim")
                _ = await controller.Claim(RunId, CancellationToken.None);
            else if (action == "Heartbeat")
                _ = await controller.Heartbeat(RunId, CancellationToken.None);
            else if (action == "Fail")
                _ = await controller.Fail(RunId, CancellationToken.None);
            else
                _ = await controller.AuthorizeRemediation(
                    RunId, CancellationToken.None);
        });
        Require(body.ReadCount == 0 && probe.CallCount == 0,
            $"ROUTE_{action}_UNAUTHORIZED_READ");
    }
}
(StatisticReconciliationRecheckStateMachine Machine,
    StatisticReconciliationBeginRecheckCommand Command,
    StatisticReconciliationRecheckCaptureBinding Binding) Machine(
        char seed, int maxRetry = 3)
{
    var initial = StatisticReconciliationRecheckStateMachine.CreateTerminal(
        RunId, StatisticReconciliationRunStatuses.Matched,
        H(seed), H(seed), 10, 2, maxRetry, at.AddDays(1));
    return (new(initial, () => H('f')),
        new("recheck-command", initial.StateRevision, initial.StateHash),
        Binding(seed == 'f' ? 'e' : 'f'));
}

StatisticReconciliationRecheckCaptureBinding Binding(char seed)
{
    var oid = $"{seed}123456789abcdef01234567";
    var config = H(seed);
    var plan = new StatisticReconciliationActualCapturePlan
    {
        BoundaryRegistryVersion =
            StatisticReconciliationActualCapturePlanIntegrity
                .BoundaryRegistryVersion,
        ActualConfigurationBundleSha256 = config,
        Basic = new()
        {
            SnapshotId = oid,
            Mode = "FLOW_FINAL",
            ImmutableSelectorSha256 = H('1')
        },
        Advanced = new()
        {
            SectionId = "section",
            DayNodeIds = ["2123456789abcdef01234567"],
            MonthNodeIds = ["3123456789abcdef01234567"],
            YearNodeIds = ["4123456789abcdef01234567"],
            ImmutableSelectorSha256 = H('2')
        },
        Diff = new()
        {
            ResultId = "5123456789abcdef01234567",
            RunId = "6123456789abcdef01234567",
            ImmutableSelectorSha256 = H('3')
        },
        Api = new()
        {
            Surface = "DIRECT_FIELD",
            OwnerResultId = oid,
            ExpectedTotalRows = 0,
            PageSize = StatisticReconciliationActualCapturePlanIntegrity
                .ApiPageSize,
            PageCount = 1
        },
        Export = new()
        {
            ExportId = "export-id",
            ResultKind = "DIRECT",
            WorkId = "7123456789abcdef01234567",
            ScopeType = "ASSIGNMENT",
            ScopeId = "8123456789abcdef01234567",
            ResultId = oid,
            RequestSha256 = H('4'),
            AuthorizationSnapshotSha256 = H('5'),
            ContentSha256 = H('6'),
            ColumnManifestSha256 = H('7'),
            OwnerSemanticSha256 = H('8')
        }
    };
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    var binding = new StatisticReconciliationRecheckCaptureBinding
    {
        P9ResultId = oid,
        P9RunId = oid,
        P9GenerationId = H(seed),
        P9GenerationHash = H(seed),
        P9RunKind = "LIFECYCLE_DIRECT_PROJECTION",
        P9CapabilityId = "P9",
        P9RouteId = "route",
        P9CandidateChainId = "chain",
        P9CandidatePromptId = "prompt",
        SourceReportId = "9123456789abcdef01234567",
        SourcePayloadRevision = 1,
        SourcePayloadHash = H('9'),
        SourceLifecycleRevision = 1,
        SourceLifecycleEventKey = H('a'),
        SourceLifecycleHash = H('b'),
        SourceLifecycleStatus = "APPROVED",
        DynamicFormVersionId = "a123456789abcdef01234567",
        DynamicFormSchemaHash = H('c'),
        P8ConfigBundleHash = H('d'),
        P9CatalogVersion = "v1",
        P9CatalogRawSha256 = H('e'),
        P9CatalogSemanticSha256 = H('f'),
        P9SchemaRawSha256 = H('0'),
        P9SchemaSemanticSha256 = H('1'),
        P9StageLockSha256 = H('2'),
        PeriodKey = "2026",
        PeriodInstanceKey = "2026",
        PeriodKind = "YEAR",
        PeriodStartUtc = at,
        PeriodEndUtc = at.AddYears(1),
        TimeAxis = "UTC_GREGORIAN",
        ActualCapturePlan = plan,
        ActualCapturePlanSha256 = plan.PlanSha256,
        ActualConfigurationBundleSha256 = config
    };
    StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(binding);
    StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(binding);
    return binding;
}

StatisticReconciliationRecheckMarker Marker(
    StatisticReconciliationRecheckCaptureBinding binding,
    string baseActual,
    string baseActualHash)
{
    var command = new StatisticReconciliationBeginRecheckCommand(
        "marker-command", 5, H('1'));
    return StatisticReconciliationRecheckCanonical.NewMarker(
        RunId, ActorId, command,
        StatisticReconciliationRunStatuses.Matched,
        baseActual, baseActualHash, H('2'), H('3'), binding, at);
}

StatisticReconciliationReviewPermission Permission() => new(
    true, true, true, true, true,
    "reviewer", H('a'), 1, 1);

StatisticReconciliationReviewGeneration ReviewGeneration(
    long revision,
    char generationSeed = '8',
    char generationHashSeed = '9',
    char semanticSeed = 'a')
{
    var columns = new StatisticReconciliationEightColumnRecord(
        H('1'), H('2'), H('3'), H('4'), H('5'), H('6'), H('7'),
        "MATCHED");
    return new(RunId, H(generationSeed), H(generationHashSeed),
        H(semanticSeed), columns,
        StatisticReconciliationIndependentReviewCanonical
            .ReviewRecordHash(columns),
        "MATCHED", true, true, false,
        "initiator", "writer", revision);
}

StatisticReconciliationReviewCommand ReviewCommand(
    StatisticReconciliationReviewGeneration generation,
    long expectedRevision)
{
    var command = new StatisticReconciliationReviewCommand(
        "review-command", H('0'), RunId,
        generation.GenerationId, generation.GenerationSha256,
        generation.SemanticVerdictSha256,
        StatisticReconciliationReviewGates.Form,
        StatisticReconciliationReviewDecisions.Approve,
        expectedRevision);
    return command with
    {
        RequestSha256 = StatisticReconciliationIndependentReviewCanonical
            .CommandHash(command)
    };
}

StatisticReconciliationReviewSupersessionCommand SupersessionCommand(
    StatisticReconciliationReviewGeneration previous,
    StatisticReconciliationReviewGeneration successor,
    long expectedRevision)
{
    var command = new StatisticReconciliationReviewSupersessionCommand(
        "supersession-command", H('0'), RunId,
        previous.GenerationId, successor.GenerationId,
        successor.GenerationSha256, expectedRevision);
    return command with
    {
        RequestSha256 = StatisticReconciliationIndependentReviewCanonical
            .SupersessionCommandHash(command)
    };
}
static string H(char value) => new(value, 64);

static void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static void Equal<T>(T expected, T actual, string reason)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{reason}: expected={expected} actual={actual}");
}

static void ExpectCode(Action action, string code)
{
    var error = Expect<StatisticReconciliationRecheckException>(action);
    Equal(code, error.Code, "RECHECK_FAILURE_CODE");
}

static T Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T error)
    {
        return error;
    }
    throw new InvalidOperationException($"EXPECTED_{typeof(T).Name}");
}

static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T error)
    {
        return error;
    }
    throw new InvalidOperationException($"EXPECTED_{typeof(T).Name}");
}

sealed class LostFenceBackend : IStatisticReconciliationIndependentReviewBackend
{
    public Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadLineageAsync(string reconciliationId,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            StatisticReconciliationIndependentReviewAuditRecord>>([]);

    public Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadOperationAsync(string operationCommandId,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            StatisticReconciliationIndependentReviewAuditRecord>>([]);

    public Task<StatisticReconciliationReviewAppendOutcome>
        TryAppendDecisionAsync(
            StatisticReconciliationIndependentReviewAuditRecord record,
            CancellationToken ct = default)
        => Task.FromResult(
            StatisticReconciliationReviewAppendOutcome.StateFenceLost);

    public Task<StatisticReconciliationReviewAppendOutcome> TryAppendSupersessionsAsync(
        IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>
            records,
        CancellationToken ct = default)
        => Task.FromResult(
            StatisticReconciliationReviewAppendOutcome.DuplicateId);
}

public class RunServiceProbe : DispatchProxy
{
    public int CallCount { get; private set; }

    protected override object? Invoke(
        MethodInfo? targetMethod,
        object?[]? args)
    {
        CallCount++;
        throw new InvalidOperationException("SERVICE_MUST_NOT_BE_CALLED");
    }
}

sealed class ThrowingReadStream : Stream
{
    public int ReadCount { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCount++;
        throw new InvalidOperationException("BODY_MUST_NOT_BE_READ");
    }
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ReadCount++;
        throw new InvalidOperationException("BODY_MUST_NOT_BE_READ");
    }
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();
    public override void SetLength(long value) =>
        throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}