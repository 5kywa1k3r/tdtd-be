using Microsoft.Extensions.DependencyInjection;
using tdtd_be.Services.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class
    StatisticReconciliationActualCrossViewV3OwnerServiceCollectionExtensions
{
    internal static IServiceCollection
        AddStatisticReconciliationActualCrossViewV3Owner(
            this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IStatisticReconciliationActualBasicApiReadOwner>(sp =>
            sp.GetRequiredService<IWorkAssignmentBasicSummaryService>() is
                IStatisticReconciliationActualBasicApiReadOwner owner
                ? owner
                : throw new InvalidOperationException(
                    "BASIC_API_READ_OWNER_NOT_AVAILABLE"));
        services.AddScoped<
            IStatisticReconciliationActualCrossViewLabelCatalogOwner,
            StatisticReconciliationActualMongoCrossViewLabelCatalogOwner>();
        services.AddScoped<
            IStatisticReconciliationActualCrossViewExportAuthorizationOwner,
            StatisticReconciliationActualMongoCrossViewExportAuthorizationOwner>();
        services.AddScoped<
            StatisticReconciliationActualCrossViewV3DirectParityProjector>();
        services.AddScoped<
            StatisticReconciliationActualCrossViewV3BasicProjector>();
        services.AddScoped<
            StatisticReconciliationActualCrossViewV3AdvancedProjector>();
        services.AddScoped<
            StatisticReconciliationActualCrossViewV3DiffProjector>();
        services.AddScoped<
            IStatisticReconciliationActualCrossViewV3Owner,
            StatisticReconciliationActualCrossViewV3Owner>();
        return services;
    }
}
