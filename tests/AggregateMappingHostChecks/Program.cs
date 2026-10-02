// These existing suites execute pure native payload/lifecycle contracts and source assertions.
// No Mongo client, service provider, WebApplication, API, job or scheduler is created.
WorkReportPayloadPreflightContractTests.Run();
Console.WriteLine("PASS: native payload preflight contract suite");
WorkReportLifecycleCommandContractTests.Run();
Console.WriteLine("PASS: native lifecycle command contract suite");
// Continue through individual existing checks so one unrelated source assertion does not
// hide regressions in the remaining lease/worker checks. Any failure still exits nonzero.
string[] outboxChecks =
[
    "EntryKeyAndPendingEnvelopeAreDeterministic",
    "PayloadOnlyEntriesCanShareLifecycleRevisionSafely",
    "BusinessEventsAreDurableAndDeterministic",
    "PeriodProjectionUsesCurrentReportState",
    "PeriodProjectionPreservesLifecycleOwnedFieldsAndTransitionSemantics",
    "DirtyRangePreservesApprovedAndActiveImpact",
    "AggregateDependentRecoveryGatesAndCoalescesTransitions",
    "AggregateRecoveryExecutionScopeIsNestedAndRestored",
    "DirectProjectionKeepsUnconfiguredStatisticsOutOfScope",
    "ReconcilerSourceKeepsLeaseFailureAndMonotonicContracts",
    "RecurringWorkerAndDependencyInjectionAreRegistered"
];
var failures = 0;
foreach (var name in outboxChecks)
{
    try
    {
        var method = typeof(WorkReportLifecycleProjectionOutboxContractTests).GetMethod(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(name);
        method.Invoke(null, null);
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine("FAIL: " + name + ": " + (error.InnerException ?? error).Message);
    }
}
Console.WriteLine($"Native outbox: {outboxChecks.Length - failures}/{outboxChecks.Length} passed; {failures} failed.");
Environment.ExitCode = failures == 0 ? 0 : 1;
