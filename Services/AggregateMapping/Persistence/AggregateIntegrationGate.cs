namespace tdtd_be.Services.AggregateMapping.Persistence;

// Integration is in source; runtime activation remains explicit and defaults OFF.
// A separate global phase change must not silently enable the periodic feature.
internal static class AggregateIntegrationGate
{
    internal static bool V2Ready(IConfiguration? configuration) => configuration?.GetValue<bool>("AggregateMapping:V2Enabled") == true;
    internal static bool Ready(IConfiguration configuration) => configuration.GetValue<bool>("AggregateMapping:Enabled");
    // Only the registered v2 URL selects this lane. Request bodies/headers cannot open it.
    internal static bool IsV2(HttpRequest request) => request.Path.StartsWithSegments("/api/aggregate-v2", StringComparison.OrdinalIgnoreCase);
    internal static bool RuntimeReady(IConfiguration? configuration) => V2Ready(configuration)
        || (configuration != null && Ready(configuration)
            && !StatisticsConfiguration.StatConfigPhaseBarrier.IsBlocked(StatisticsConfiguration.StatConfigPhaseBarrierEntries.P9Run));
    internal static readonly string[] RuntimeVerificationRequired =
    [
        "P05 authorized environment, indexes and confirmation key",
        "Native Save/Submit/review/materialize/binding transaction probes",
        "Isolated Mongo abort/unknown-commit/ACL/race/native-payload verification"
    ];
}
