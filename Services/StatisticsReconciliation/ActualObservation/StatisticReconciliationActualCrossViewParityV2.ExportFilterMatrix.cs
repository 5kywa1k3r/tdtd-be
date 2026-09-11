using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static void RequireFamilyExportFilterShape(NormalizedPlan plan)
    {
        if (plan.Family == StatisticReconciliationActualCrossViewFamilies.Direct)
            return;
        using var document = StatisticReconciliationActualJson.ParseStrict(
            plan.CanonicalExportFilterJson,
            "CROSS_VIEW_EXPORT_FILTER_FAMILY");
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Any(property =>
                property.Value.ValueKind != JsonValueKind.Null))
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FilterMismatch);
        }
    }
}
