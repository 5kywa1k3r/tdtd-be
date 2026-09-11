using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models.Statistics;
using tdtd_be.Common.Errors;

namespace tdtd_be.Services.StatisticsRun;

public interface IStatRunExportService
{
    Task<StatRunExportResponse> CreateAsync(
        StatRunExportCreateRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunExportResponse> GetAsync(
        string exportId,
        string workId,
        string scopeType,
        string scopeId,
        string capabilityId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunExportDownload> DownloadAsync(
        string exportId,
        string workId,
        string scopeType,
        string scopeId,
        string capabilityId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunExportCleanupResponse> CleanupExpiredAsync(
        StatRunExportCleanupRequest request,
        MeResponse actor,
        CancellationToken ct = default);
}

public static class StatRunExportContract
{
    public const string SchemaVersion = "P9_CANONICAL_EXPORT_V1";
    public const int MaxRows = 50_000;
    public const int MaxBytes = 20 * 1024 * 1024;
    public static readonly TimeSpan MetadataTtl = TimeSpan.FromHours(24);

    public static string NormalizeResultKind(string? value)
    {
        var result = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!StatRunExportResultKinds.All.Contains(result))
            throw Invalid("EXPORT_RESULT_KIND_INVALID");
        return result;
    }

    public static string NormalizeFormat(string? value)
    {
        var result = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (result is not (StatRunExportFormats.Csv or StatRunExportFormats.Xlsx))
            throw Invalid("EXPORT_FORMAT_INVALID");
        return result;
    }

    public static string CapabilityFor(string resultKind)
        => NormalizeResultKind(resultKind) switch
        {
            StatRunExportResultKinds.DirectField or
            StatRunExportResultKinds.DirectTable or
            StatRunExportResultKinds.DirectLabel
                => StatRunCapabilities.DirectFieldTableLabel,
            StatRunExportResultKinds.Basic => StatRunCapabilities.BasicSummary,
            StatRunExportResultKinds.Advanced => StatRunCapabilities.AdvancedSummary,
            StatRunExportResultKinds.Diff => StatRunCapabilities.Diff,
            StatRunExportResultKinds.Flow => StatRunCapabilities.FlowScopes,
            _ => throw Invalid("EXPORT_RESULT_KIND_INVALID")
        };

    public static string RouteForCapability(string capabilityId)
        => capabilityId?.Trim().ToUpperInvariant() switch
        {
            StatRunCapabilities.DirectFieldTableLabel => StatRunRouteRegistry.DirectExport,
            StatRunCapabilities.BasicSummary => StatRunRouteRegistry.BasicExport,
            StatRunCapabilities.AdvancedSummary => StatRunRouteRegistry.AdvancedExport,
            StatRunCapabilities.Diff => StatRunRouteRegistry.DiffExport,
            StatRunCapabilities.FlowScopes => StatRunRouteRegistry.FlowExport,
            _ => throw Invalid("EXPORT_CAPABILITY_INVALID")
        };

    private static AppException Invalid(string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason });
}
