using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualCrossViewV3OwnerResolution(
    string SchemaVersion,
    string State,
    string FailureCode,
    ImmutableArray<string> RequiredPersistenceFields,
    StatisticReconciliationActualCrossViewAuthorizationRelationV1?
        Authorization,
    StatisticReconciliationActualCrossViewParityV3Plan? Plan,
    StatisticReconciliationActualCrossViewParityV2Base? Base,
    StatisticReconciliationActualCrossViewParityV2Actual? Actual,
    StatisticReconciliationActualCrossViewParityV3Proof? Proof,
    string ResolutionSha256);

internal interface IStatisticReconciliationActualCrossViewV3Owner
{
    Task<StatisticReconciliationActualCrossViewV3OwnerResolution> ResolveAsync(
        StatisticReconciliationActualCrossViewV3OwnerCommand command,
        CancellationToken cancellationToken = default);
}
