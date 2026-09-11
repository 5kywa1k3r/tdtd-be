using Microsoft.Extensions.DependencyInjection;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class
    StatisticReconciliationActualExtendedRawSourceServiceCollection
{
    internal static IServiceCollection
        AddStatisticReconciliationActualExtendedRawSource(
            this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<
            StatisticReconciliationActualExtendedRawSourceTypedCompiler>();
        services.AddScoped<
            IStatisticReconciliationActualExtendedRawSourceCollectReader,
            StatisticReconciliationActualMongoExtendedRawSourceCollectReader>();
        services.AddScoped<
            IStatisticReconciliationActualExtendedRawSourceOwner,
            StatisticReconciliationActualExtendedRawSourceOwner>();
        services.AddScoped<
            IStatisticReconciliationActualExtendedRawSourceOwnerParity,
            StatisticReconciliationActualExtendedRawSourceOwnerParity>();

        return services;
    }
}