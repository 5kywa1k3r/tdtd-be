using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal static partial class LifecycleFixture
{
    internal static async Task<StatisticReconciliationActualLifecycleBuildInput> FreshnessInputAsync(
        string caseId, string tag, bool configDrift,
        StatisticReconciliationActualLifecyclePriorGeneration prior)
    {
        var oldPins = Pins.Create(caseId, tag);
        var currentPins = oldPins with
        {
            ConfigVersionId = O(caseId + ":new-config-version"),
            ConfigSha256 = H(caseId + ":new-config-sha")
        };
        var expected = Expected(currentPins, "APPROVED", "V_INCLUDE", 1,
            runtimeKind: StatisticReconciliationExpectedRuntimeKinds.NonFlow);
        var owner = ActualOwner(currentPins, expected, ordinal: 0);
        var source = await CaptureSourceAsync(currentPins, [owner]);
        var direct = await CaptureDirectAsync(currentPins, source);
        var auditExpected = configDrift
            ? Expected(oldPins, "APPROVED", "V_INCLUDE", 1,
                runtimeKind: StatisticReconciliationExpectedRuntimeKinds.NonFlow)
            : expected;
        var audit = IncludeAudit(configDrift ? oldPins : currentPins, auditExpected, 2);
        return Input(currentPins, expected, source, direct, audit, prior);
    }
}
