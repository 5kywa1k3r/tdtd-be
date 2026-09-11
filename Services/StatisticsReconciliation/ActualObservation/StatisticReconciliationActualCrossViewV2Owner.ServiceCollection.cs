using Microsoft.Extensions.DependencyInjection;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class
    StatisticReconciliationActualCrossViewV2OwnerServiceCollectionExtensions
{
    /// <summary>
    /// Separate opt-in registration. The owner is deliberately not wired into
    /// capture/publication until its resolution is durably persisted by a
    /// later capture schema.
    /// </summary>
    internal static IServiceCollection
        AddStatisticReconciliationActualCrossViewV2Owner(
            this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<
            IStatisticReconciliationActualCrossViewV2Owner,
            StatisticReconciliationActualCrossViewV2Owner>();
        return services;
    }
}
