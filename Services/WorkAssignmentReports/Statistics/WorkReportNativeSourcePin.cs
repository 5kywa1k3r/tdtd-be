using System.Globalization;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

/// <summary>Frozen ordering material, captured with the authorized source set.
/// L5c must carry its digest through staging, publish and historical readback.</summary>
internal sealed record WorkReportNativeSourcePin(
    string ReportId, int PayloadRevision, string PayloadHash, DateTime? PayloadUpdatedAtUtc)
{
    internal static WorkReportNativeSourcePin Capture(WorkAssignmentReport report, WorkReportPayloadSnapshot payload)
    {
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(report, payload);
        if (!StatRunCanonicalJson.IsCanonicalSha256(payload.PayloadHash)
            || !ValidTime(payload.SourcePayloadUpdatedAtUtc) || !ValidTime(report.PayloadUpdatedAtUtc)
            || payload.SourcePayloadUpdatedAtUtc != report.PayloadUpdatedAtUtc)
            throw WorkReportDirectGenerationValidationException.SourceDrift();
        return new(report.Id, payload.PayloadRevision, payload.PayloadHash!, payload.SourcePayloadUpdatedAtUtc);
    }

    internal void Validate(WorkAssignmentReport report, WorkReportPayloadSnapshot payload, bool requiresTimestamp)
    {
        var current = Capture(report, payload);
        if (this != current || requiresTimestamp && PayloadUpdatedAtUtc is null)
            throw WorkReportDirectGenerationValidationException.SourceDrift();
    }

    internal static string Digest(IEnumerable<WorkReportNativeSourcePin> sources)
        => StatRunCanonicalJson.HashText(string.Join("\n", new[] { "NATIVE_SOURCE_ORDER_V2" }.Concat(
            sources.OrderBy(source => source.ReportId, StringComparer.Ordinal).Select(source => string.Join("|",
                source.ReportId, source.PayloadRevision.ToString(CultureInfo.InvariantCulture), source.PayloadHash,
                source.PayloadUpdatedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "<missing>")))));

    private static bool ValidTime(DateTime? value)
        => value is null || value.Value != DateTime.MinValue && value.Value.Kind == DateTimeKind.Utc
            && value.Value.Ticks % TimeSpan.TicksPerMillisecond == 0;
}
