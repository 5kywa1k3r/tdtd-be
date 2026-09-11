using System.Collections.Immutable;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal interface IStatisticReconciliationActualSummaryPlanOwner
{
    Task<StatisticReconciliationActualSummaryPlanBinding> ResolveAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedGenerationBinding exactBinding,
        CancellationToken cancellationToken);
}

/// <summary>
/// Projects only the value-free metric plan from one exact, integrity-validated
/// expected generation. Selection by latest/global cardinality and deriving a
/// plan from expected atom values are both forbidden.
/// </summary>
internal sealed class StatisticReconciliationActualSummaryPlanOwner(
    IStatisticReconciliationExpectedProjectionInputReader projectionReader)
    : IStatisticReconciliationActualSummaryPlanOwner
{
    public async Task<StatisticReconciliationActualSummaryPlanBinding>
        ResolveAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationExpectedGenerationBinding exactBinding,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(exactBinding);
        if (!StringComparer.Ordinal.Equals(
                run.Id, exactBinding.ReconciliationId))
            throw Fail("EXPECTED_RECONCILIATION_BINDING_MISMATCH");

        StatisticReconciliationExpectedProjectionInput input;
        try
        {
            input = await projectionReader.ResolveAsync(
                    run,
                    exactBinding,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Fail("EXPECTED_PROJECTION_NULL");
        }
        catch (Exception error) when (error is
            StatisticReconciliationExpectedProjectionInputException or
            StatisticReconciliationExpectedObservationIntegrityException or
            StatisticReconciliationExpectedLedgerInputException or
            StatisticReconciliationActualObservationException or
            OverflowException)
        {
            throw Fail("EXPECTED_PROJECTION_INVALID", error);
        }

        if (input.Binding != exactBinding)
            throw Fail("EXPECTED_PROJECTION_BINDING_MISMATCH");
        if (input.MetricPlan.IsDefaultOrEmpty ||
            input.MetricPlan.Length != exactBinding.MetricPlanEntryCount)
            throw Fail("EXPECTED_METRIC_PLAN_CARDINALITY_INVALID");

        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors;
        try
        {
            descriptors = input.MetricPlan
                .Select(StatisticReconciliationActualSummaryIdentityDescriptor
                    .Create)
                .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
                .ToImmutableArray();
            return StatisticReconciliationActualSummaryPlanBinding.Create(
                exactBinding,
                descriptors);
        }
        catch (Exception error) when (error is
            StatisticReconciliationExpectedProjectionInputException or
            StatisticReconciliationExpectedLedgerInputException or
            StatisticReconciliationActualObservationException or
            OverflowException)
        {
            throw Fail("EXPECTED_PLAN_PROJECTION_INVALID", error);
        }
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason,
        Exception? inner = null)
        => inner is null
            ? new($"ACTUAL_SUMMARY_PLAN_OWNER:{reason}")
            : new($"ACTUAL_SUMMARY_PLAN_OWNER:{reason}:{inner.GetType().Name}");
}