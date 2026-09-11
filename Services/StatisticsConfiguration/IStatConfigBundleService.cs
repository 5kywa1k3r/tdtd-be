using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

public interface IStatConfigBundleService
{
    StatConfigBundleReadback ReadEmpty(string ownerKind, string ownerId);

    Task<StatConfigBundleReadback> ReadAsync(
        StatConfigBundleReadRequest request,
        CancellationToken ct = default);

    Task<StatConfigBundleReadback> ValidateAsync(
        StatConfigBundleValidateRequest request,
        CancellationToken ct = default);
}
