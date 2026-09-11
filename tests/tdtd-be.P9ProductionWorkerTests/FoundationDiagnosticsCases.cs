using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

internal static class FoundationDiagnosticsCases
{
    // These codes are emitted as reason arguments to RequireObjectId rather than
    // direct Fail literals, so they need explicit producer/classifier parity coverage.
    private static readonly string[] HelperGeneratedValidationCodes =
    [
        "P9_DIRECT_MEMBER_APPROVAL_ACTOR_INVALID",
        "P9_DIRECT_MEMBER_APPROVAL_UNIT_INVALID",
        "P9_DIRECT_MEMBER_ISSUER_UNIT_INVALID",
        "P9_DIRECT_MEMBER_TARGET_UNIT_INVALID",
        "P9_DIRECT_MEMBER_ASSIGNEE_UNIT_INVALID"
    ];
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("projection diagnostic survives successful retry release", ProjectionDiagnostic),
        ("flow mapping lineage keeps its bounded validation code", FlowMappingLineageDiagnostic),
        ("helper-generated validation codes terminalize without retry", HelperGeneratedValidationCodesTerminalize),
        ("deterministic projection failures terminalize on their first failed attempt", DeterministicProjectionTerminalizes),
        ("typed Direct generation validation failures terminalize without broadening retry policy", TypedGenerationValidationTerminalizes),
        ("transient projection failures retain bounded retry", TransientProjectionFailuresRetry),
        ("failure transition tracks real attempts and terminal disposition", FailureTransitionPlanning),
        ("failure persistence rejects unlisted codes and disposition drift", FailurePersistenceAllowlist),
        ("deadline accounting does not fabricate exhausted retries", DeadlineAttemptAccounting),
        ("unlisted errors and domain details never reach logs", Redaction),
        ("custom exception message getter is never read", CustomException),
        ("completion failure is distinguished from projection", CompletionDiagnostic),
        ("retry release failure keeps the original diagnostic", RetryDiagnostic),
        ("late projection fault cannot replace heartbeat failure", HeartbeatThenProjectionFault),
        ("cancelled projection preserves cancellation and releases retry", CancellationDiagnostic),
        ("source selector rejects stale and ambiguous lifecycle entries", SourceEntryValidation),
        ("zero-write or malformed projection cannot complete foundation", ProjectionReceiptValidation),
        ("production typed domain reasons survive bounded logging", ProductionDomainReasons),
        ("production AppException conflicts preserve worker stage semantics", ProductionConflictWorkerStages),
        ("typed domain details preserve public JSON shape", TypedConflictJsonPreserved),
        ("arbitrary domain details and unknown reasons remain redacted", HostileDomainDetails)
    ];

    private static async Task ProjectionDiagnostic()
    {
        var logger = new CaptureLogger();
        var state = new FakeStateOwner(Lease());
        var worker = Worker(state, (_, _) => throw new InvalidOperationException(
            "P9_DIRECT_FORM_SOURCE_PIN_INVALID"), logger);
        await worker.ProcessPendingAsync(1);
        Check(state.TerminalCount == 1 && state.RetryCount == 0 && state.CompleteCount == 0,
            "deterministic projection must terminalize once without retry delay");
        Check(state.LastDisposition == StatRunFoundationFailureDisposition.Terminal,
            "source pin failure disposition");
        var row = logger.Rows.Single();
        Check(row["Stage"]?.ToString() == "Projection", "projection stage");
        Check(row["Code"]?.ToString() == "P9_DIRECT_FORM_SOURCE_PIN_INVALID", "stable source reason");
        Check(row["JobId"]?.ToString() == Lease().Job.JobId, "job correlation");
        Check(row["StateRevision"] is 2L && row["RetryCount"] is 0, "claimed revision and retry");
        Check(logger.Exceptions.All(error => error is null), "never pass an exception to logger");
    }

    private static Task FlowMappingLineageDiagnostic()
    {
        const string code = "P9_DIRECT_FLOW_MAPPING_LINEAGE_STALE";
        Check(StatRunFoundationDiagnostics.Describe(new InvalidOperationException(code)) ==
              ("VALIDATION", code), "exact flow mapping lineage code must survive");
        Check(StatRunFoundationDiagnostics.Describe(
                  new InvalidOperationException(code + ":PRIVATE_DETAIL")) ==
              ("VALIDATION", "UNLISTED_VALIDATION_ERROR"),
            "flow mapping lineage prefixes with arbitrary details remain redacted");
        return Task.CompletedTask;
    }
    private static async Task HelperGeneratedValidationCodesTerminalize()
    {
        foreach (var code in HelperGeneratedValidationCodes)
        {
            var error = new InvalidOperationException(code);
            Check(StatRunFoundationDiagnostics.Describe(error) == ("VALIDATION", code),
                "helper-generated diagnostic must survive exactly: " + code);
            var failure = StatRunFoundationDiagnostics.ClassifyProjectionFailure(error);
            Check(failure == new StatRunFoundationFailure(
                      code, StatRunFoundationFailureDisposition.Terminal) &&
                  StatRunFoundationDiagnostics.IsAcceptedFailure(failure),
                "helper-generated diagnostic must be accepted as terminal: " + code);
            var state = new FakeStateOwner(Lease());
            await Worker(state, (_, _) => throw error, new CaptureLogger())
                .ProcessPendingAsync(1);
            Check(state.TerminalCount == 1 && state.RetryCount == 0,
                "helper-generated diagnostic must bypass retry: " + code);
        }
    }

    private static async Task DeterministicProjectionTerminalizes()
    {
        foreach (var code in new[]
        {
            "P9_DIRECT_RUN_HEADER_CONFLICT",
            "P9_DIRECT_CURRENT_PUBLICATION_FAMILY_INVALID",
            "P9_DIRECT_PUBLICATION_SCOPE_KEY_INVALID",
            "P9_DIRECT_MEMBERSHIP_SIGNATURE_INVALID",
            "P9_DIRECT_FOUNDATION_REFRESH_SOURCE_PIN_STALE",
            "FOUNDATION_SOURCE_PIN_STALE",
            "P9_DIRECT_CONFIG_PIN_STALE",
            "P9_DIRECT_LOCKED_CONFIG_INVALID",
            "P9_DIRECT_COMPLETED_GENERATION_HASH_MISMATCH"
        })
        {
            var state = new FakeStateOwner(Lease());
            var logger = new CaptureLogger();
            await Worker(state, (_, _) => throw new InvalidOperationException(code), logger)
                .ProcessPendingAsync(1);
            Check(state.TerminalCount == 1 && state.RetryCount == 0,
                "deterministic failure must not enter retry: " + code);
            Check(state.LastDiagnostic == code &&
                  state.LastDisposition == StatRunFoundationFailureDisposition.Terminal,
                "exact allowlisted terminal diagnostic: " + code);
            Check(logger.Rows.Single()["Code"]?.ToString() == code,
                "terminal log code remains bounded: " + code);
        }
    }

    private static async Task TypedGenerationValidationTerminalizes()
    {
        var typed = new[]
        {
            WorkReportDirectGenerationValidationException.RowConflict(),
            WorkReportDirectGenerationValidationException.SourceDrift(),
            WorkReportDirectGenerationValidationException.PinConflict(),
            WorkReportDirectGenerationValidationException.PolicyDrift()
        };
        var constructors = typeof(WorkReportDirectGenerationValidationException)
            .GetConstructors(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        Check(typeof(WorkReportDirectGenerationValidationException).IsSealed &&
              constructors.Length == 1 && constructors[0].IsPrivate,
            "Direct generation validation codes must be sealed and constructible only by closed factories");

        foreach (var error in typed)
        {
            var described = StatRunFoundationDiagnostics.Describe(error);
            Check(described == ("VALIDATION", error.DiagnosticCode),
                "typed Direct generation diagnostic must survive exactly");
            var failure =
                StatRunFoundationDiagnostics.ClassifyProjectionFailure(error);
            Check(failure == new StatRunFoundationFailure(
                      error.DiagnosticCode,
                      StatRunFoundationFailureDisposition.Terminal) &&
                  StatRunFoundationDiagnostics.IsAcceptedFailure(failure),
                "typed Direct generation failure must be accepted as terminal");
            Check(StatRunDirectProjectionService
                    .ShouldTerminalizeClaimedProjectionFailure(true, error),
                "typed post-claim Foundation failure must terminalize the inner run");

            var state = new FakeStateOwner(Lease());
            await Worker(state, (_, _) => throw error, new CaptureLogger())
                .ProcessPendingAsync(1);
            Check(state.TerminalCount == 1 && state.RetryCount == 0 &&
                  state.LastDiagnostic == error.DiagnosticCode,
                "typed Direct generation failure must terminalize the outer run once");
        }

        var payload = new AppException(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_NOT_READY,
            new { marker = "PRIVATE_PAYLOAD_DETAIL" },
            "PRIVATE_PAYLOAD_MESSAGE");
        var payloadFailure =
            StatRunFoundationDiagnostics.ClassifyProjectionFailure(payload);
        Check(payloadFailure == new StatRunFoundationFailure(
                  WorkReportDirectGenerationValidationException
                      .PayloadNotReadyCode,
                  StatRunFoundationFailureDisposition.Terminal) &&
              StatRunFoundationDiagnostics.IsAcceptedFailure(payloadFailure) &&
              StatRunDirectProjectionService
                  .ShouldTerminalizeClaimedProjectionFailure(true, payload),
            "exact payload-not-ready AppErrorCode must terminalize without exposing details");
        var payloadState = new FakeStateOwner(Lease());
        var payloadLogger = new CaptureLogger();
        await Worker(payloadState, (_, _) => throw payload, payloadLogger)
            .ProcessPendingAsync(1);
        Check(payloadState.TerminalCount == 1 &&
              payloadState.RetryCount == 0 &&
              payloadState.LastDiagnostic ==
              WorkReportDirectGenerationValidationException.PayloadNotReadyCode &&
              payloadLogger.Text.All(value =>
                  !value.Contains("PRIVATE_PAYLOAD", StringComparison.Ordinal)),
            "payload-not-ready must terminalize once with redacted evidence");

        foreach (var transient in new Exception[]
        {
            new MongoException("PRIVATE_DATABASE_DETAIL"),
            new TimeoutException("PRIVATE_TIMEOUT_DETAIL"),
            new OperationCanceledException("PRIVATE_CANCELLATION_DETAIL"),
            new ArgumentException("PRIVATE_PROGRAMMING_GUARD"),
            new AppException(
                AppErrorCode.COMMON_INTERNAL_ERROR,
                new { marker = "PRIVATE_OTHER_DOMAIN_DETAIL" },
                "PRIVATE_OTHER_DOMAIN_MESSAGE")
        })
        {
            var failure =
                StatRunFoundationDiagnostics.ClassifyProjectionFailure(transient);
            Check(failure == new StatRunFoundationFailure(
                      StatRunFoundationWorker.ProjectionFailure,
                      StatRunFoundationFailureDisposition.Retry) &&
                  !StatRunDirectProjectionService
                      .ShouldTerminalizeClaimedProjectionFailure(
                          true,
                          transient),
                "infrastructure, cancellation, programming guard, and unrelated domain errors must retain retry");
        }
    }

    private static async Task TransientProjectionFailuresRetry()
    {
        var samples = new (Exception Error, string PersistedCode)[]
        {
            (new MongoException("PRIVATE_DATABASE_DETAIL"), StatRunFoundationWorker.ProjectionFailure),
            (new TimeoutException("PRIVATE_DEPENDENCY_TIMEOUT"), StatRunFoundationWorker.ProjectionFailure),
            (new Exception("PRIVATE_UNKNOWN_DETAIL"), StatRunFoundationWorker.ProjectionFailure),
            (new InvalidOperationException("P9_DIRECT_RUN_CLAIM_BUSY"), "P9_DIRECT_RUN_CLAIM_BUSY")
        };
        foreach (var sample in samples)
        {
            var state = new FakeStateOwner(Lease());
            var logger = new CaptureLogger();
            await Worker(state, (_, _) => throw sample.Error, logger).ProcessPendingAsync(1);
            Check(state.RetryCount == 1 && state.TerminalCount == 0,
                "transient failure must retain bounded retry");
            Check(state.LastDiagnostic == sample.PersistedCode &&
                  state.LastDisposition == StatRunFoundationFailureDisposition.Retry,
                "transient persisted diagnostic");
            Check(state.LastDiagnostic != "STAT_RUN_JOB_TIMEOUT",
                "dependency timeout must not masquerade as job deadline expiry");
            Check(logger.Text.All(value => !value.Contains("PRIVATE_", StringComparison.Ordinal)),
                "transient log must remain redacted");
        }
    }

    private static Task FailureTransitionPlanning()
    {
        var now = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
        var terminal = StatRunService.PlanFoundationFailure(
            0, 5, StatRunFoundationFailureDisposition.Terminal, now);
        Check(terminal.Status == WorkReportStatisticRebuildJobStatuses.DeadLetter &&
              terminal.RetryCount == 1 && terminal.NextRetryAtUtc is null &&
              !terminal.IsActive && terminal.CompletedAtUtc == now,
            "deterministic failure must terminalize after one recorded attempt");

        var retry = StatRunService.PlanFoundationFailure(
            0, 5, StatRunFoundationFailureDisposition.Retry, now);
        Check(retry.Status == WorkReportStatisticRebuildJobStatuses.RetryWaiting &&
              retry.RetryCount == 1 && retry.NextRetryAtUtc == now.AddMinutes(5) &&
              retry.IsActive && retry.CompletedAtUtc is null,
            "first transient failure must use bounded five-minute retry");

        var exhausted = StatRunService.PlanFoundationFailure(
            4, 5, StatRunFoundationFailureDisposition.Retry, now);
        Check(exhausted.Status == WorkReportStatisticRebuildJobStatuses.DeadLetter &&
              exhausted.RetryCount == 5 && exhausted.NextRetryAtUtc is null &&
              !exhausted.IsActive && exhausted.CompletedAtUtc == now,
            "fifth transient failure must exhaust the configured retry bound");
        return Task.CompletedTask;
    }

    private static Task FailurePersistenceAllowlist()
    {
        Check(!StatRunFoundationDiagnostics.IsAcceptedFailure(
                new StatRunFoundationFailure("PRIVATE_UNLISTED_CODE", StatRunFoundationFailureDisposition.Terminal)),
            "unlisted diagnostic must not be persisted");
        Check(!StatRunFoundationDiagnostics.IsAcceptedFailure(
                new StatRunFoundationFailure("P9_DIRECT_RUN_HEADER_CONFLICT", StatRunFoundationFailureDisposition.Retry)),
            "deterministic diagnostic cannot be downgraded to retry");
        Check(!StatRunFoundationDiagnostics.IsAcceptedFailure(
                new StatRunFoundationFailure("P9_DIRECT_RUN_CLAIM_BUSY", StatRunFoundationFailureDisposition.Terminal)),
            "transient diagnostic cannot be upgraded to terminal");
        Check(!StatRunFoundationDiagnostics.IsAcceptedFailure(
                new StatRunFoundationFailure(StatRunFoundationWorker.HeartbeatFailure,
                    StatRunFoundationFailureDisposition.Terminal)),
            "heartbeat failure must remain bounded retry");
        return Task.CompletedTask;
    }

    private static Task DeadlineAttemptAccounting()
    {
        var job = new WorkReportStatisticRebuildJob
        {
            Status = WorkReportStatisticRebuildJobStatuses.Pending,
            RetryCount = 0
        };
        Check(StatRunService.CountDeadlineAttempts(job, 5) == 0,
            "an unclaimed pending job has no failed attempt");
        job.Status = WorkReportStatisticRebuildJobStatuses.Running;
        Check(StatRunService.CountDeadlineAttempts(job, 5) == 1,
            "an expired running lease records its in-flight attempt");
        job.Status = WorkReportStatisticRebuildJobStatuses.RetryWaiting;
        job.RetryCount = 2;
        Check(StatRunService.CountDeadlineAttempts(job, 5) == 2,
            "waiting deadline expiry preserves actual prior attempts");
        return Task.CompletedTask;
    }

    private static async Task Redaction()
    {
        const string marker = "SYNTHETIC_PRIVATE_MESSAGE_DO_NOT_LOG";
        foreach (var error in new Exception[]
        {
            new InvalidOperationException(marker),
            new Exception(marker),
            new AppException(AppErrorCode.STAT_RUN_JOB_CONFLICT, new { marker }, marker)
        })
        {
            var logger = new CaptureLogger();
            await Worker(new FakeStateOwner(Lease()), (_, _) => throw error, logger).ProcessPendingAsync(1);
            Check(logger.Text.All(text => !text.Contains(marker, StringComparison.Ordinal)), "no raw message/details");
            Check(logger.Exceptions.All(value => value is null), "no raw exception");
            Check(logger.Text.All(text => !text.Contains(Lease().ClaimToken, StringComparison.Ordinal)), "no lease token");
        }
    }

    private static Task CustomException()
    {
        var description = StatRunFoundationDiagnostics.Describe(new HostileMessageException());
        Check(description.Code == "UNEXPECTED_WORKER_FAILURE", "unknown exception remains bounded");
        return Task.CompletedTask;
    }

    private static async Task CompletionDiagnostic()
    {
        var state = new FakeStateOwner(Lease()) { ThrowComplete = true };
        var logger = new CaptureLogger();
        await Worker(state, (_, _) => Task.FromResult(Receipt()), logger).ProcessPendingAsync(1);
        Check(logger.Rows.Single()["Stage"]?.ToString() == "Completion", "completion stage");
        Check(state.SuccessfulTerminalWrites == 0 && state.RetryCount == 1, "stale completion cannot publish");
    }

    private static async Task RetryDiagnostic()
    {
        var state = new FakeStateOwner(Lease()) { ThrowRetry = true };
        var logger = new CaptureLogger();
        await Worker(state, (_, _) => throw new InvalidOperationException("FOUNDATION_SOURCE_PIN_STALE"), logger)
            .ProcessPendingAsync(1);
        Check(logger.Rows.Count == 2, "original and release errors kept separately");
        Check(logger.Rows[0]["Code"]?.ToString() == "FOUNDATION_SOURCE_PIN_STALE", "original retained first");
        Check(logger.Rows[1]["Stage"]?.ToString() == "Retry" && logger.Rows[1]["Secondary"] is true,
            "retry failure is secondary");
    }

    private static async Task HeartbeatThenProjectionFault()
    {
        var state = new FakeStateOwner(Lease()) { ThrowHeartbeat = true };
        var logger = new CaptureLogger();
        await Worker(state, async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new InvalidOperationException("FOUNDATION_SOURCE_PIN_STALE"); }
            return Receipt();
        }, logger).ProcessPendingAsync(1).WaitAsync(TimeSpan.FromSeconds(3));
        Check(state.LastDiagnostic == StatRunFoundationWorker.HeartbeatFailure, "heartbeat remains original failure");
        Check(state.CompleteCount == 0, "no completion on heartbeat failure");
        Check(logger.Rows.Any(row => row["Stage"]?.ToString() == "Heartbeat"), "heartbeat diagnostic");
        Check(logger.Rows.Any(row => row["Stage"]?.ToString() == "Projection" && row["Secondary"] is true),
            "late projection fault retained as secondary");
    }

    private static async Task CancellationDiagnostic()
    {
        var state = new FakeStateOwner(Lease());
        var logger = new CaptureLogger();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Worker(state, async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Receipt();
        }, logger).ProcessPendingAsync(1, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        try { await work; throw new InvalidOperationException("cancellation was swallowed"); }
        catch (OperationCanceledException) { }
        Check(state.LastDiagnostic == StatRunFoundationWorker.CancellationFailure, "release cancellation category");
        Check(state.CompleteCount == 0, "no completion after cancellation");
        Check(logger.Rows.Any(row => row["Code"]?.ToString() == "OPERATION_CANCELLED"), "safe cancellation evidence");
    }

    private static Task SourceEntryValidation()
    {
        var job = Lease().Job;
        var report = Source();
        Check(ReferenceEquals(report.LifecycleProjectionOutbox![0],
            StatRunFoundationDirectProjectionOwner.RequireSourceEntry(job, report)), "exact effective entry selected");
        report.PayloadRevision++;
        Reject(() => StatRunFoundationDirectProjectionOwner.RequireSourceEntry(job, report), "FOUNDATION_SOURCE_PIN_STALE");
        report = Source(); report.LifecycleProjectionOutbox!.Clear();
        Reject(() => StatRunFoundationDirectProjectionOwner.RequireSourceEntry(job, report), "FOUNDATION_LIFECYCLE_ENTRY_INVALID");
        report = Source(); report.LifecycleProjectionOutbox!.Add(report.LifecycleProjectionOutbox[0]);
        Reject(() => StatRunFoundationDirectProjectionOwner.RequireSourceEntry(job, report), "FOUNDATION_LIFECYCLE_ENTRY_INVALID");
        report = Source(); report.LifecycleProjectionOutbox![0].ActorUserId = "not-an-actor-id";
        Reject(() => StatRunFoundationDirectProjectionOwner.RequireSourceEntry(job, report), "FOUNDATION_LIFECYCLE_ENTRY_INVALID");
        report = Source(); report.IsActive = false;
        Reject(() => StatRunFoundationDirectProjectionOwner.RequireSourceEntry(job, report), "FOUNDATION_SOURCE_PIN_STALE");
        return Task.CompletedTask;
    }

    private static Task ProjectionReceiptValidation()
    {
        var receipt = Receipt();
        var projected = new StatRunDirectProjectionResult("PUBLISHED", "PUBLISHED", receipt.ProjectionRunId,
            receipt.GenerationId, receipt.GenerationHash, false);
        Check(StatRunFoundationDirectProjectionOwner.RequirePublishedProjection(projected) == receipt, "exact published receipt");
        foreach (var invalid in new[]
        {
            projected with { State = "ZERO_WRITE", Reason = "STATISTICS_NOT_CONFIGURED" },
            projected with { RunId = null }, projected with { GenerationId = "invalid" },
            projected with { GenerationHash = "invalid" }
        }) Reject(() => StatRunFoundationDirectProjectionOwner.RequirePublishedProjection(invalid),
            "FOUNDATION_DIRECT_PROJECTION_NOT_PUBLISHED");
        return Task.CompletedTask;
    }


    private static Task ProductionDomainReasons()
    {
        foreach (var reason in new[] { "CLAIM_CONTENTION", "STALE_WORKER_FENCE", "JOB_DEADLINE_INVALID",
                     "JOB_DEADLINE_EXPIRED", "DIRECT_PROJECTION_RECEIPT_INVALID", "FOUNDATION_DIAGNOSTIC_INVALID",
                     "FOUNDATION_CAPABILITY_NOT_OWNED", "JOB_ACTIVATION_PIN_MISMATCH", "JOB_NOT_AVAILABLE",
                     "IMMUTABLE_HEADER_INTEGRITY_INVALID", "RECEIPT_INTEGRITY_INVALID", "STATE_INTEGRITY_INVALID",
                     "RESET_RECEIPT_HISTORY_INTEGRITY_INVALID" })
        {
            var error = new AppException(AppErrorCode.STAT_RUN_JOB_CONFLICT,
                new StatRunJobConflictDetails(reason, 0), "PRIVATE_MESSAGE");
            var described = StatRunFoundationDiagnostics.Describe(error);
            Check(described == ("DOMAIN", reason), "production typed reason must survive: " + reason);
            var failure = StatRunFoundationDiagnostics.ClassifyProjectionFailure(error);
            var retryable = reason is "CLAIM_CONTENTION" or "STALE_WORKER_FENCE" or
                "JOB_DEADLINE_EXPIRED" or "JOB_NOT_AVAILABLE";
            Check(failure.DiagnosticCode == reason &&
                  failure.Disposition == (retryable
                      ? StatRunFoundationFailureDisposition.Retry
                      : StatRunFoundationFailureDisposition.Terminal),
                "typed domain retry policy: " + reason);
            Check(StatRunFoundationDiagnostics.IsAcceptedFailure(failure),
                "typed domain failure must pass persistence allowlist: " + reason);
            Check(error.Code == AppErrorCode.STAT_RUN_JOB_CONFLICT, "public error code remains unchanged");
        }
        return Task.CompletedTask;
    }

    private static async Task ProductionConflictWorkerStages()
    {
        foreach (var stage in new[] { StatRunFoundationStage.Claim, StatRunFoundationStage.Heartbeat,
                     StatRunFoundationStage.Completion, StatRunFoundationStage.Retry })
        {
            var state = new ProductionConflictOwner(stage);
            var logger = new CaptureLogger();
            async Task<StatRunFoundationProjectionReceipt> Project(StatRunWorkerLeaseResponse _, CancellationToken ct)
            {
                if (stage == StatRunFoundationStage.Heartbeat) await Task.Delay(Timeout.Infinite, ct);
                if (stage == StatRunFoundationStage.Retry)
                    throw new InvalidOperationException("FOUNDATION_SOURCE_PIN_STALE");
                return Receipt();
            }
            var work = Worker(state, Project, logger).ProcessPendingAsync(1);
            if (stage == StatRunFoundationStage.Claim)
            {
                try { await work; throw new InvalidOperationException("claim conflict swallowed"); }
                catch (AppException error) when (error.Code == AppErrorCode.STAT_RUN_JOB_CONFLICT) { }
            }
            else await work.WaitAsync(TimeSpan.FromSeconds(3));
            var row = logger.Rows.Single(item => item["Stage"]?.ToString() == stage.ToString());
            Check(row["Code"]?.ToString() == state.Reason && row["Category"]?.ToString() == "DOMAIN",
                "production-shaped reason/stage retained");
            Check(logger.Exceptions.All(error => error is null) &&
                  logger.Text.All(text => !text.Contains("PRIVATE_DOMAIN_MESSAGE")), "raw production exception not logged");
            Check(state.Inner.SuccessfulTerminalWrites == 0, "conflict cannot publish success");
            if (stage == StatRunFoundationStage.Retry)
                Check(row["Secondary"] is true && logger.Rows[0]["Code"]?.ToString() == "FOUNDATION_SOURCE_PIN_STALE",
                    "typed retry conflict stays secondary");
            if (stage is StatRunFoundationStage.Heartbeat or StatRunFoundationStage.Completion)
                Check(state.Inner.RetryCount == 1, "typed conflict releases retry once");
        }
    }

    private static Task TypedConflictJsonPreserved()
    {
        foreach (var writes in new[] { 0, 1 })
        {
            object previous = new { reason = "STALE_WORKER_FENCE", writes };
            object current = new StatRunJobConflictDetails("STALE_WORKER_FENCE", writes);
            foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
                Check(JsonSerializer.Serialize(current, options) == JsonSerializer.Serialize(previous, options),
                    "typed details retain exact public reason/writes JSON");
        }
        return Task.CompletedTask;
    }

    private static async Task HostileDomainDetails()
    {
        const string marker = "PRIVATE_TYPED_REASON_DO_NOT_LOG";
        foreach (var details in new object[]
        {
            new HostileConflictDetails(),
            new { reason = "STALE_WORKER_FENCE" },
            new Dictionary<string, object> { ["reason"] = "STALE_WORKER_FENCE" },
            new StatRunJobConflictDetails(marker, 0)
        })
        {
            var error = new AppException(AppErrorCode.STAT_RUN_JOB_CONFLICT, details, marker);
            Check(StatRunFoundationDiagnostics.Describe(error).Code == "STAT_RUN_JOB_CONFLICT",
                "unknown or arbitrary details never masquerade as trusted reason");
            var logger = new CaptureLogger();
            await Worker(new FakeStateOwner(Lease()), (_, _) => throw error, logger).ProcessPendingAsync(1);
            Check(logger.Text.All(text => !text.Contains(marker)) && logger.Exceptions.All(value => value is null),
                "typed/untyped private reason and message remain hidden");
        }
        var other = new AppException(AppErrorCode.STAT_RUN_FORBIDDEN,
            new StatRunJobConflictDetails("STALE_WORKER_FENCE", 0), marker);
        Check(StatRunFoundationDiagnostics.Describe(other).Code == "STAT_RUN_FORBIDDEN",
            "typed conflict reason cannot override another public error code");
    }

    private sealed class HostileConflictDetails
    {
        public string Reason => throw new InvalidOperationException("arbitrary getter must not run");
        public string reason => throw new InvalidOperationException("arbitrary getter must not run");
    }

    private sealed class ProductionConflictOwner(StatRunFoundationStage stage) : IStatRunFoundationWorkerStateOwner
    {
        internal FakeStateOwner Inner { get; } = new(Lease());
        internal string Reason => stage switch
        {
            StatRunFoundationStage.Claim => "CLAIM_CONTENTION",
            StatRunFoundationStage.Heartbeat => "JOB_DEADLINE_EXPIRED",
            _ => "STALE_WORKER_FENCE"
        };
        private AppException Conflict() => new(AppErrorCode.STAT_RUN_JOB_CONFLICT,
            new StatRunJobConflictDetails(Reason, 0), "PRIVATE_DOMAIN_MESSAGE");
        public Task<StatRunWorkerLeaseResponse?> ClaimDirectAsync(StatRunFoundationWorkerIdentity identity,
            CancellationToken ct = default)
            => stage == StatRunFoundationStage.Claim ? throw Conflict() : Inner.ClaimDirectAsync(identity, ct);
        public Task HeartbeatAsync(StatRunFoundationWorkerIdentity identity, StatRunWorkerLeaseResponse lease,
            CancellationToken ct = default)
            => stage == StatRunFoundationStage.Heartbeat ? throw Conflict() : Inner.HeartbeatAsync(identity, lease, ct);
        public Task CompleteAsync(StatRunFoundationWorkerIdentity identity, StatRunWorkerLeaseResponse lease,
            StatRunFoundationProjectionReceipt receipt, CancellationToken ct = default)
            => stage == StatRunFoundationStage.Completion ? throw Conflict() : Inner.CompleteAsync(identity, lease, receipt, ct);
        public Task ReleaseFailureAsync(StatRunFoundationWorkerIdentity identity, StatRunWorkerLeaseResponse lease,
            StatRunFoundationFailure failure, CancellationToken ct = default)
            => stage == StatRunFoundationStage.Retry
                ? throw Conflict()
                : Inner.ReleaseFailureAsync(identity, lease, failure, ct);
    }

    private static StatRunFoundationWorker Worker(IStatRunFoundationWorkerStateOwner state,
        Func<StatRunWorkerLeaseResponse, CancellationToken, Task<StatRunFoundationProjectionReceipt>> project,
        CaptureLogger logger) => new(state, new FakeProjectionOwner(project),
            new StatRunFoundationWorkerIdentity("p9-foundation:test", "P9_FOUNDATION_DIRECT_PROJECTION"),
            TimeSpan.FromMilliseconds(10), logger);

    private static StatRunWorkerLeaseResponse Lease() => new()
    {
        ClaimToken = new string('d', 32), WorkerId = "p9-foundation:test", Job = new()
        {
            JobId = "100000000000000000000001", SourceReportId = "200000000000000000000001",
            SourceRevision = 6, SourceHash = new string('e', 64), LifecycleRevision = 4,
            StateRevision = 2, RetryCount = 0
        }
    };
    private static StatRunFoundationProjectionReceipt Receipt() => new("300000000000000000000001",
        new string('a', 64), new string('b', 64), false);
    private static WorkAssignmentReport Source() => new()
    {
        Status = WorkAssignmentReportStatus.Approved, IsCurrent = true, IsActive = true,
        PayloadRevision = 6, PayloadHash = new string('e', 64), LifecycleRevision = 4,
        LifecycleProjectionOutbox = [new()
        {
            Operation = "REVIEW_APPROVE", FromStatus = "SUBMITTED", ToStatus = "APPROVED",
            ToIsActive = true, State = WorkReportLifecycleProjectionOutboxStates.Completed,
            PayloadRevision = 6, PayloadHash = new string('e', 64), LifecycleRevision = 4,
            ActorUserId = "400000000000000000000001"
        }]
    };
    private static void Reject(Action action, string code)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message == code) { return; }
        throw new InvalidOperationException("expected exact validation failure: " + code);
    }
    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }
    private sealed class HostileMessageException : InvalidOperationException
    {
        public override string Message => throw new InvalidOperationException("message getter must not be called");
    }
    private sealed class CaptureLogger : ILogger<StatRunFoundationWorker>
    {
        internal List<Dictionary<string, object?>> Rows { get; } = [];
        internal List<string> Text { get; } = [];
        internal List<Exception?> Exceptions { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Rows.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(item => item.Key, item => item.Value));
            Text.Add(formatter(state, exception)); Exceptions.Add(exception);
        }
    }
}
