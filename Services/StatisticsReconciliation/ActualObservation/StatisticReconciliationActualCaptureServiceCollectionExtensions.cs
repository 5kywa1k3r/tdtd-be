using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using tdtd_be.Data;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class
    StatisticReconciliationActualCaptureServiceCollectionExtensions
{
    internal static IServiceCollection AddStatisticReconciliationActualCapture(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StatisticReconciliationActualHttpApiOwnerOptions>(
            configuration.GetSection(
                StatisticReconciliationActualHttpApiOwnerOptions.SectionName));
        services.AddHttpClient<
            StatisticReconciliationActualHttpApiOwnerReader>();
        services.AddScoped<IStatisticReconciliationActualApiOwnerReader>(sp =>
            sp.GetRequiredService<
                StatisticReconciliationActualHttpApiOwnerReader>());

        services.AddScoped<IStatRunDirectProjectionReadOwner>(sp =>
            sp.GetRequiredService<StatRunDirectProjectionService>());
        services.AddScoped<IStatRunDirectProjectionLifecycleAuditOwner>(sp =>
            sp.GetRequiredService<StatRunDirectProjectionService>());
        services.AddSingleton<IStatisticReconciliationActualLifecycleBridge,
            StatisticReconciliationActualLifecycleBridge>();
        services.AddScoped<IStatisticReconciliationActualLifecycleLedger,
            StatisticReconciliationActualLifecycleMongoLedger>();
        services.AddScoped<IStatisticReconciliationActualLifecyclePriorOwner,
            StatisticReconciliationActualLifecyclePriorOwner>();
        services.AddScoped<IStatisticReconciliationActualExportOwnerReader>(sp =>
            sp.GetRequiredService<StatRunExportService>());

        services.AddScoped(sp =>
            StatisticReconciliationActualOwnerReaderFactory.Create(
                sp.GetRequiredService<MongoDbContext>(),
                sp.GetRequiredService<IStatRunDirectProjectionReadOwner>()));
        services.AddScoped<IStatisticReconciliationActualSourceOwnerReader>(sp =>
            sp.GetRequiredService<
                    StatisticReconciliationActualMongoOwnerReaderBundle>()
                .Source);
        services.AddScoped<
            IStatisticReconciliationActualDirectProjectionOwnerReader>(sp =>
            sp.GetRequiredService<
                    StatisticReconciliationActualMongoOwnerReaderBundle>()
                .DirectProjection);
        services.AddScoped<IStatisticReconciliationActualAggregateOwnerReader>(sp =>
            sp.GetRequiredService<
                    StatisticReconciliationActualMongoOwnerReaderBundle>()
                .Aggregate);
        services.AddScoped<IStatisticReconciliationActualBasicOwnerReader>(sp =>
            sp.GetRequiredService<
                    StatisticReconciliationActualMongoOwnerReaderBundle>()
                .Basic);
        services.AddScoped<IStatisticReconciliationActualAdvancedOwnerReader>(sp =>
            sp.GetRequiredService<
                    StatisticReconciliationActualMongoOwnerReaderBundle>()
                .Advanced);
        services.AddScoped<IStatisticReconciliationActualP9DiffOwnerReader>(sp =>
            sp.GetRequiredService<
                    StatisticReconciliationActualMongoOwnerReaderBundle>()
                .Diff);

        services.AddScoped<
            IStatisticReconciliationActualCoherentBoundaryReaderFactory,
            StatisticReconciliationActualMongoBoundaryReaderFactory>();
        services.AddSingleton<
            IStatisticReconciliationActualAdapterTypedMapper,
            StatisticReconciliationActualAdapterTypedMapper>();
        services.AddStatisticReconciliationActualExtendedRawSource();
        services.AddStatisticReconciliationActualCrossViewV3Owner();
        services.AddSingleton<IStatisticReconciliationActualRawSummaryCompiler,
            StatisticReconciliationActualRawSummaryCompiler>();
        services.AddScoped<IStatisticReconciliationActualRawSummaryOwner,
            StatisticReconciliationActualMongoRawSummaryOwner>();
        services.AddSingleton<
            IStatisticReconciliationActualSummaryOwnerParityProver,
            StatisticReconciliationActualSummaryOwnerParityProver>();
        services.AddScoped<IStatisticReconciliationActualRelationalProofOwner,
            StatisticReconciliationActualRelationalProofOwner>();
        services.AddScoped<
            IStatisticReconciliationActualObservationBackend,
            StatisticReconciliationActualObservationMongoBackend>();
        services.AddScoped<
            IStatisticReconciliationTrustedVerdictPipeline,
            StatisticReconciliationTrustedVerdictPipeline>();
        services.AddScoped<
            IStatisticReconciliationTrustedLifecycleVerifier,
            StatisticReconciliationTrustedLifecycleVerifier>();
        services.AddScoped<
            IStatisticReconciliationActualGenerationCas,
            StatisticReconciliationRunServiceActualGenerationCas>();
        services.AddScoped<StatisticReconciliationActualGenerationPublisher>();
        services.AddScoped<StatisticReconciliationActualCaptureService>();
        services.AddScoped<
            StatisticReconciliationActualMongoClaimedCaptureMaterialOwner>();
        services.AddScoped<
            IStatisticReconciliationActualClaimedCaptureMaterialOwner>(sp =>
            sp.GetRequiredService<
                StatisticReconciliationActualMongoClaimedCaptureMaterialOwner>());
        services.AddScoped<
            IStatisticReconciliationActualPendingVerdictMaterialOwner>(sp =>
            sp.GetRequiredService<
                StatisticReconciliationActualMongoClaimedCaptureMaterialOwner>());
        services.AddScoped<
            IStatisticReconciliationTrustedCurrentOwnerReader,
            StatisticReconciliationTrustedCurrentOwnerReader>();
        services.AddSingleton<
            IStatisticReconciliationActualBoundaryRegistry,
            StatisticReconciliationActualBoundaryRegistry>();
        services.AddScoped<
            IStatisticReconciliationActualSummaryPlanOwner,
            StatisticReconciliationActualSummaryPlanOwner>();
        services.AddScoped<
            StatisticReconciliationActualTrustedCaptureRequestFactory>();
        services.AddScoped<StatisticReconciliationActualClaimedCaptureWorker>();
        services.AddScoped<IStatisticReconciliationActualClaimedCaptureWorker>(sp =>
            sp.GetRequiredService<
                StatisticReconciliationActualClaimedCaptureWorker>());
        return services;
    }
}
