using System.Collections.Immutable;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class AdvancedReaderOrderCases
{
    internal static int Run()
    {
        IReadOnlyList<WorkAssignmentReport> consumptionOrder =
        [
            new WorkAssignmentReport
            {
                Id = "rZ",
                WorkAssignmentId = "assignment-A"
            },
            new WorkAssignmentReport
            {
                Id = "rA",
                WorkAssignmentId = "assignment-B"
            }
        ];
        var persistedIdOrder = ImmutableArray.Create("rA", "rZ");
        var valid = (bool)TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualMongoExtendedRawSourceCollectReader),
            "AdvancedDayReportIdsMatch",
            consumptionOrder,
            persistedIdOrder);
        TestSupport.Equal(true, valid,
            "P10-EXT-ADV-17 day-membership-id-sort-vs-consumption-order");
        var invalid = (bool)TestSupport.InvokePrivate(
            typeof(StatisticReconciliationActualMongoExtendedRawSourceCollectReader),
            "AdvancedDayReportIdsMatch",
            consumptionOrder,
            ImmutableArray.Create("rZ", "rA"));
        TestSupport.Equal(false, invalid,
            "P10-EXT-ADV-18 persisted-id-order-mutation");
        return 2;
    }
}
