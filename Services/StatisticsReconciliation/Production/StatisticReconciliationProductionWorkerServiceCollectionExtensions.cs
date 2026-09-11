using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace tdtd_be.Services.StatisticsReconciliation.Production;

public static class
    StatisticReconciliationProductionWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddStatisticReconciliationProductionWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StatisticReconciliationProductionWorkerOptions>(
            configuration.GetSection(
                StatisticReconciliationProductionWorkerOptions.SectionName));
        services.AddSingleton<
            IStatisticReconciliationInternalWorkerIdentity,
            StatisticReconciliationInternalWorkerIdentity>();
        services.AddScoped<
            IStatisticReconciliationProductionRuntime,
            StatisticReconciliationProductionRuntime>();
        services.AddScoped<
            IStatisticReconciliationProductionWorker,
            StatisticReconciliationProductionWorker>();
        return services;
    }
}
