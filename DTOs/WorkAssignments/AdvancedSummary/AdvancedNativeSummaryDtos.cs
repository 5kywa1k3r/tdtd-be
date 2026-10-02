using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvancedNativeSummaryRequest(string ScopeAssignmentId, string DynamicFormTemplateId,
    string SectionId, string Grain, string GrainKey, string? SnapshotId = null, bool Historical = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommandId = null);

public sealed record AdvancedNativeSummaryResponse(int Version, string SnapshotId, string SnapshotHash,
    string SourceSetHash, string Freshness, DateTime CheckedAtUtc, StatConfigIdentity Configuration,
    string SectionId, string Grain, string GrainKey, string TimeAxis, DateTime StartUtc, DateTime EndExclusiveUtc,
    int SourceAssignmentCount, JsonElement LegacyFields, NativeStatisticResultDocument Native, string? QuotaLedgerId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeStatisticResultMetadata? NativeMetadata { get; init; }
}

public sealed record AdvancedNativeRefreshResponse(string RefreshId, string State, int Attempt,
    string SnapshotId, string? SnapshotHash, string? BackgroundJobId, string? ErrorCode, DateTime UpdatedAtUtc);
