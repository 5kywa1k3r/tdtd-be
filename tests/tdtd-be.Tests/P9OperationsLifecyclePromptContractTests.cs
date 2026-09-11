using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

internal static class P9OperationsLifecyclePromptContractTests
{
    public static void Run()
    {
        LegacyPublishedPromptIsAccepted();
        ExistingCandidatePromptRemainsAccepted();
        ArbitraryPromptIsRejected();
        FoundationRefreshPromptMatrixIsExact();
    }

    private static void LegacyPublishedPromptIsAccepted()
    {
        var job = new WorkReportStatisticRebuildJob
        {
            DirectProjectionIdentityVersion = null,
            DirectProjectionIdentityKey = null,
            CandidatePromptId = StatRunCapabilityActivation.PublishedPromptId
        };

        Assert(
            StatRunService.HasLifecycleOperationsPromptIntegrity(job),
            "Legacy lifecycle publications pinned to the published P9-12 prompt must be readable by operations.");
    }

    private static void ExistingCandidatePromptRemainsAccepted()
    {
        var job = new WorkReportStatisticRebuildJob
        {
            DirectProjectionIdentityVersion = null,
            DirectProjectionIdentityKey = null,
            CandidatePromptId = StatRunCapabilityActivation.LifecycleRequiredPromptId
        };

        Assert(
            StatRunService.HasLifecycleOperationsPromptIntegrity(job),
            "Existing sealed lifecycle candidate prompts must remain operations-readable.");
    }

    private static void ArbitraryPromptIsRejected()
    {
        var job = new WorkReportStatisticRebuildJob
        {
            DirectProjectionIdentityVersion = null,
            DirectProjectionIdentityKey = null,
            CandidatePromptId = "P9-UNPUBLISHED"
        };

        Assert(
            !StatRunService.HasLifecycleOperationsPromptIntegrity(job),
            "Operations must reject lifecycle publications pinned to an arbitrary prompt.");
    }

    private static void FoundationRefreshPromptMatrixIsExact()
    {
        var cases = new[]
        {
            (
                PromptId: StatRunCapabilityActivation.LifecycleRequiredPromptId,
                Expected: true,
                Description: "candidate P9-02"),
            (
                PromptId: StatRunCapabilityActivation.PublishedPromptId,
                Expected: true,
                Description: "published P9-12"),
            (
                PromptId: "P9-03",
                Expected: false,
                Description: "older P9-03")
        };

        foreach (var testCase in cases)
        {
            var job = new WorkReportStatisticRebuildJob
            {
                DirectProjectionIdentityVersion =
                    StatRunDirectProjectionService.FoundationRefreshIdentityVersion,
                DirectProjectionIdentityKey = new string('a', 64),
                CandidatePromptId = testCase.PromptId
            };

            Assert(
                StatRunService.HasLifecycleOperationsPromptIntegrity(job) ==
                    testCase.Expected,
                $"Foundation refresh V2 prompt matrix mismatch for {testCase.Description}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}