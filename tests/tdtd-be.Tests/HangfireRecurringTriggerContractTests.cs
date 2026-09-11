using tdtd_be.Jobs;

internal static class HangfireRecurringTriggerContractTests
{
    public static void Run()
    {
        DisabledOrUninitializedTriggerIsSkippedWithoutTouchingStorage();
        EnabledTriggerFailureIsContainedAndReported();
        MissingRegisteredJobIsContainedAndReported();
        EnabledRegisteredJobTriggersSuccessfully();
    }

    private static void DisabledOrUninitializedTriggerIsSkippedWithoutTouchingStorage()
    {
        var invocationCount = 0;
        bool ThrowIfInvoked(string _)
        {
            invocationCount++;
            throw new InvalidOperationException("Uninitialized Hangfire storage must not be touched.");
        }

        var disabledResult = HangfireRecurringJobRegistrar.TryTriggerDynamicFormStatisticRebuildNow(
            recurringRegistrationEnabled: false,
            recurringRegistrationReady: true,
            ThrowIfInvoked);
        var uninitializedResult = HangfireRecurringJobRegistrar.TryTriggerDynamicFormStatisticRebuildNow(
            recurringRegistrationEnabled: true,
            recurringRegistrationReady: false,
            _ =>
            {
                invocationCount++;
                throw new InvalidOperationException("Uninitialized Hangfire storage must not be touched.");
            });

        AssertFalse(disabledResult, "disabled recurring trigger must report skipped");
        AssertFalse(uninitializedResult, "uninitialized recurring trigger must report skipped");
        AssertEqual(0, invocationCount, "disabled/uninitialized trigger storage access count");
    }

    private static void EnabledTriggerFailureIsContainedAndReported()
    {
        string? reportedReason = null;
        Exception? reportedException = null;
        var storageFailure = new InvalidOperationException("storage unavailable");

        var result = HangfireRecurringJobRegistrar.TryTriggerDynamicFormStatisticRebuildNow(
            recurringRegistrationEnabled: true,
            recurringRegistrationReady: true,
            _ => throw storageFailure,
            (reason, exception) =>
            {
                reportedReason = reason;
                reportedException = exception;
            });

        AssertFalse(result, "best-effort trigger failure must not escape the post-commit boundary");
        AssertEqual("RECURRING_JOB_TRIGGER_FAILED", reportedReason, "failure reason");
        AssertTrue(ReferenceEquals(storageFailure, reportedException), "logger callback must receive the Hangfire exception");
    }

    private static void MissingRegisteredJobIsContainedAndReported()
    {
        string? reportedReason = null;
        Exception? reportedException = new InvalidOperationException("not reset");

        var result = HangfireRecurringJobRegistrar.TryTriggerDynamicFormStatisticRebuildNow(
            recurringRegistrationEnabled: true,
            recurringRegistrationReady: true,
            _ => false,
            (reason, exception) =>
            {
                reportedReason = reason;
                reportedException = exception;
            });

        AssertFalse(result, "missing recurring registration must be a best-effort skip");
        AssertEqual("RECURRING_JOB_NOT_REGISTERED", reportedReason, "missing registration reason");
        AssertTrue(reportedException is null, "missing registration must not fabricate an exception");
    }

    private static void EnabledRegisteredJobTriggersSuccessfully()
    {
        string? triggeredId = null;
        var failureReported = false;

        var result = HangfireRecurringJobRegistrar.TryTriggerDynamicFormStatisticRebuildNow(
            recurringRegistrationEnabled: true,
            recurringRegistrationReady: true,
            recurringJobId =>
            {
                triggeredId = recurringJobId;
                return true;
            },
            (_, _) => failureReported = true);

        AssertTrue(result, "registered recurring job must report triggered");
        AssertEqual(
            HangfireRecurringJobRegistrar.DynamicFormStatisticRebuildJobId,
            triggeredId,
            "triggered recurring job id");
        AssertFalse(failureReported, "successful trigger must not report a failure");
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message)
        => AssertTrue(!value, message);
}
