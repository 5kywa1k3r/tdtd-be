using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Services.StatisticsConfiguration;

/// <summary>
/// Fail-closed release boundary for every durable P8 statistics-configuration
/// mutation. It is deliberately evaluated before a Mongo session starts so a
/// catalog rollback cannot create an owner, receipt, job, audit/outbox or
/// result write. Reads and the inherited P7 mapping runtime remain available.
/// </summary>
public static class StatConfigCatalogActivation
{
    public const string ConflictReason = "STAT_CONFIG_CAPABILITY_CONFLICT";

    public static bool ActivationEnabled =>
        DynamicFlowP8CatalogCandidate.ActivationEnabled &&
        DynamicFormFlowCapabilityCatalogMetadata
            .StatisticsConfigurationCapabilities.Count == 11;

    public static void EnsureMutationEnabled()
    {
        if (ActivationEnabled)
            return;

        throw new AppException(
            AppErrorCode.STAT_CONFIG_CAPABILITY_CONFLICT,
            new
            {
                reason = ConflictReason,
                requiredCatalogVersion = DynamicFlowP8CatalogCandidate.Version,
                requiredCatalogSha256 = DynamicFlowP8CatalogCandidate.SemanticHash,
                actualCatalogVersion = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                actualCatalogSha256 = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                writes = 0,
            });
    }
}
