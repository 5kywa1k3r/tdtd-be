using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Auth;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

internal static class StatisticReconciliationRecoveryControllerContractTests
{
    private const string WorkId = "507f1f77bcf86cd799439011";
    private const string AssignmentId = "507f1f77bcf86cd799439012";
    private const string ReconciliationId = "507f1f77bcf86cd799439013";
    private const string ActorId = "507f1f77bcf86cd799439014";
    private const string UnitId = "507f1f77bcf86cd799439015";

    public static void Run()
    {
        InitialDeadlinePinHasBackwardCompatibleBsonContract();
        InitialDeadlinePinStabilizesCanonicalHeaderHash();
        TargetNotSignableHasStableOpaqueRecoveryCode();
        TargetNotSignableRecoveryCodeIsReadOnly();
        RecheckBeginReturnsAcceptedWithUnchangedQueuedReceipt();
    }

    private static void InitialDeadlinePinHasBackwardCompatibleBsonContract()
    {
        var initial = new DateTime(
            2026, 9, 5, 7, 0, 0, DateTimeKind.Utc);
        var operational = initial.AddMinutes(10);
        var run = new StatisticReconciliationRun
        {
            Id = ReconciliationId,
            InitialDeadlineAtUtc = initial,
            DeadlineAtUtc = operational
        };
        var document = run.ToBsonDocument();
        Require(document.Contains("initialDeadlineAtUtc") &&
                document["initialDeadlineAtUtc"].ToUniversalTime() == initial,
            "new reconciliation rows must serialize the immutable initial deadline pin");

        var roundTrip = BsonSerializer.Deserialize<StatisticReconciliationRun>(
            document);
        Require(roundTrip.InitialDeadlineAtUtc == initial &&
                roundTrip.DeadlineAtUtc == operational,
            "initial and operational deadlines must round-trip independently");

        document.Remove("initialDeadlineAtUtc");
        var legacy = BsonSerializer.Deserialize<StatisticReconciliationRun>(
            document);
        Require(legacy.InitialDeadlineAtUtc is null &&
                legacy.DeadlineAtUtc == operational,
            "legacy rows without the deadline pin must remain readable");
    }

    private static void InitialDeadlinePinStabilizesCanonicalHeaderHash()
    {
        var initial = new DateTime(
            2026, 9, 5, 7, 0, 0, DateTimeKind.Utc);
        var run = new StatisticReconciliationRun
        {
            Id = ReconciliationId,
            CreatedAtUtc = initial.AddMinutes(-1),
            InitialDeadlineAtUtc = null,
            DeadlineAtUtc = initial
        };
        var method = typeof(StatisticReconciliationRunService).GetMethod(
            "BuildImmutableHeaderHash",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "immutable header hash builder is missing");
        string Hash() => (string)(method.Invoke(null, [run]) ??
            throw new InvalidOperationException(
                "immutable header hash is missing"));

        var legacyHash = Hash();
        run.InitialDeadlineAtUtc = initial;
        Require(Hash() == legacyHash,
            "backfilling the legacy deadline pin must preserve the header hash");
        run.DeadlineAtUtc = initial.AddMinutes(10);
        Require(Hash() == legacyHash,
            "advancing only the operational deadline must preserve the header hash");
        run.InitialDeadlineAtUtc = initial.AddMilliseconds(1);
        Require(Hash() != legacyHash,
            "changing the immutable deadline pin must change the header hash");
    }
    private static void TargetNotSignableHasStableOpaqueRecoveryCode()
    {
        var owner = new TargetNotSignableOwner(CurrentAuthorization());
        var controller = ReviewController(owner);

        var response = controller.ReadAsync(WorkId, AssignmentId,
                ReconciliationId, CancellationToken.None)
            .GetAwaiter().GetResult();
        var conflict = response as ObjectResult;
        Require(conflict?.StatusCode == StatusCodes.Status409Conflict,
            "unsignable review target must return HTTP 409");
        var problem = conflict!.Value as ProblemDetails;
        Require(problem is not null &&
                problem.Extensions.TryGetValue("code", out var code) &&
                string.Equals(code as string,
                    StatisticReconciliationIndependentReviewFailureCodes
                        .TargetNotSignable,
                    StringComparison.Ordinal),
            "unsignable review target must expose the exact recovery code");
        var wire = JsonSerializer.Serialize(problem);
        foreach (var forbidden in new[]
                 { "summary", "decisions", "generationId", "reviewRecord" })
        {
            Require(!wire.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"unsignable review response leaked stale {forbidden}");
        }
        Require(owner.AuthorizeCalls == 1 && owner.ReadCalls == 1,
            "review recovery path must authorize and read exactly once");
    }

    private static void TargetNotSignableRecoveryCodeIsReadOnly()
    {
        var owner = new TargetNotSignableOwner(CurrentAuthorization());
        var submit = ReviewControllerWithBody(owner,
            "{\"commandId\":\"submit\",\"gate\":\"FORM\",\"decision\":\"APPROVE\",\"expectedStateRevision\":1}")
            .SubmitAsync(WorkId, AssignmentId, ReconciliationId,
                CancellationToken.None).GetAwaiter().GetResult();
        var finalApproval = ReviewController(owner)
            .FinalApprovalAsync(WorkId, AssignmentId, ReconciliationId,
                CancellationToken.None).GetAwaiter().GetResult();
        var supersede = ReviewControllerWithBody(owner,
            "{\"commandId\":\"supersede\",\"previousGenerationId\":\"generation\",\"expectedStateRevision\":1}")
            .SupersedeAsync(WorkId, AssignmentId, ReconciliationId,
                CancellationToken.None).GetAwaiter().GetResult();

        foreach (var (operation, response) in new[]
                 {
                     ("submit", submit),
                     ("final approval", finalApproval),
                     ("supersede", supersede)
                 })
        {
            var conflict = response as ObjectResult;
            var problem = conflict?.Value as ProblemDetails;
            Require(conflict?.StatusCode == StatusCodes.Status409Conflict &&
                    problem is not null &&
                    !problem.Extensions.ContainsKey("code"),
                $"{operation} must keep target-not-signable as a generic 409");
        }
        Require(owner.AuthorizeCalls == 3 &&
                owner.SubmitCalls == 1 &&
                owner.FinalApprovalCalls == 1 &&
                owner.SupersedeCalls == 1,
            "non-read review paths must each execute exactly once");
    }

    private static void RecheckBeginReturnsAcceptedWithUnchangedQueuedReceipt()
    {
        var expected = new StatisticReconciliationBeginRecheckResponse
        {
            IsReplay = false,
            ReconciliationId = ReconciliationId,
            RecheckMarkerId = new string('a', 64),
            Status = "QUEUED",
            StateRevision = 12,
            StateHash = new string('b', 64)
        };
        var service = DispatchProxy.Create<IStatisticReconciliationRunService,
            RecheckRunServiceProbe>();
        var probe = (RecheckRunServiceProbe)(object)service;
        probe.Response = expected;
        var controller = RecheckController(service);

        var action = controller.Begin(WorkId, AssignmentId,
                ReconciliationId, CancellationToken.None)
            .GetAwaiter().GetResult();
        var accepted = action.Result as AcceptedResult;
        Require(accepted is not null,
            "recheck begin must return AcceptedResult");
        Require(accepted!.StatusCode == StatusCodes.Status202Accepted &&
                ReferenceEquals(accepted.Value, expected),
            "recheck begin must preserve the service receipt in HTTP 202");
        Require(probe.CallCount == 1,
            "recheck begin must delegate exactly once");

        var method = typeof(StatisticReconciliationRecheckController)
            .GetMethod(nameof(StatisticReconciliationRecheckController.Begin))
            ?? throw new InvalidOperationException("recheck Begin route is missing");
        var metadata = method.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .SingleOrDefault(value =>
                value.StatusCode == StatusCodes.Status202Accepted);
        Require(metadata?.Type ==
                typeof(StatisticReconciliationBeginRecheckResponse),
            "recheck Begin must publish its 202 response schema");
    }

    private static StatisticReconciliationIndependentReviewController
        ReviewController(IStatisticReconciliationIndependentReviewOwner owner)
    {
        var context = Context();
        return new StatisticReconciliationIndependentReviewController(
            new MeAccessor(new HttpContextAccessor { HttpContext = context }),
            owner)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static StatisticReconciliationIndependentReviewController
        ReviewControllerWithBody(
            IStatisticReconciliationIndependentReviewOwner owner,
            string json)
    {
        var controller = ReviewController(owner);
        var bytes = Encoding.UTF8.GetBytes(json);
        controller.Request.Body = new MemoryStream(bytes, writable: false);
        controller.Request.ContentType = "application/json";
        controller.Request.ContentLength = bytes.Length;
        return controller;
    }
    private static StatisticReconciliationRecheckController RecheckController(
        IStatisticReconciliationRunService service)
    {
        var context = Context();
        context.Request.Body = new MemoryStream([0x7b, 0x7d], writable: false);
        return new StatisticReconciliationRecheckController(service,
            new MeAccessor(new HttpContextAccessor { HttpContext = context }))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Items[MeAccessor.MeItemKey] = new MeResponse(ActorId,
            "reviewer", "Reviewer", [], UnitId, null, null, null,
            ["ADMIN"], null, false);
        return context;
    }

    private static StatisticReconciliationReviewScopeAuthorization
        CurrentAuthorization()
    {
        var constructor = typeof(StatisticReconciliationReviewScopeAuthorization)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        return (StatisticReconciliationReviewScopeAuthorization)
            constructor.Invoke([
                ActorId, UnitId, WorkId, AssignmentId,
                new[] { "STAT_RECONCILIATION_REVIEW" },
                new string('c', 64), 1, 1, true
            ]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TargetNotSignableOwner(
        StatisticReconciliationReviewScopeAuthorization authorization)
        : IStatisticReconciliationIndependentReviewOwner
    {
        public int AuthorizeCalls { get; private set; }
        public int ReadCalls { get; private set; }

        public Task<StatisticReconciliationReviewScopeAuthorization?>
            AuthorizeScopeAsync(string? workId, string? scopeAssignmentId,
                MeResponse actor, CancellationToken ct = default)
        {
            AuthorizeCalls++;
            return Task.FromResult<
                StatisticReconciliationReviewScopeAuthorization?>(authorization);
        }

        public int SubmitCalls { get; private set; }
        public int FinalApprovalCalls { get; private set; }
        public int SupersedeCalls { get; private set; }

        public Task<StatisticReconciliationReviewReadResult> ReadAsync(
            StatisticReconciliationReviewScopeAuthorization scope,
            string reconciliationId, CancellationToken ct = default)
        {
            ReadCalls++;
            return Task.FromException<StatisticReconciliationReviewReadResult>(
                NotSignable());
        }

        public Task<StatisticReconciliationReviewSubmissionDto> SubmitAsync(
            StatisticReconciliationReviewScopeAuthorization authorization,
            string reconciliationId,
            StatisticReconciliationReviewOwnerCommand command,
            CancellationToken ct = default)
        {
            SubmitCalls++;
            return Task.FromException<
                StatisticReconciliationReviewSubmissionDto>(NotSignable());
        }

        public Task<StatisticReconciliationReviewFinalApproval>
            GetFinalApprovalAsync(
                StatisticReconciliationReviewScopeAuthorization authorization,
                string reconciliationId, CancellationToken ct = default)
        {
            FinalApprovalCalls++;
            return Task.FromException<
                StatisticReconciliationReviewFinalApproval>(NotSignable());
        }

        public Task<StatisticReconciliationReviewSupersessionDto> SupersedeAsync(
            StatisticReconciliationReviewScopeAuthorization authorization,
            string reconciliationId,
            StatisticReconciliationReviewOwnerSupersessionCommand command,
            CancellationToken ct = default)
        {
            SupersedeCalls++;
            return Task.FromException<
                StatisticReconciliationReviewSupersessionDto>(NotSignable());
        }

        private static StatisticReconciliationIndependentReviewException
            NotSignable()
            => new(
                StatisticReconciliationIndependentReviewFailureCodes
                    .TargetNotSignable,
                "matchedCompleteCoherentRequired");
    }
}

public class RecheckRunServiceProbe : DispatchProxy
{
    public int CallCount { get; private set; }
    public StatisticReconciliationBeginRecheckResponse? Response { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name !=
            nameof(IStatisticReconciliationRunService.BeginRecheckAsync))
            throw new NotSupportedException(targetMethod?.Name);
        CallCount++;
        return Task.FromResult(Response ??
            throw new InvalidOperationException("probe response is missing"));
    }
}