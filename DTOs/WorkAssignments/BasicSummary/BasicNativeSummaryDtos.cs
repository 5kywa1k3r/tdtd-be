using System.Text.Json.Serialization;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;

namespace tdtd_be.DTOs.WorkAssignments.BasicSummary;

// Membership, periods and methods come exclusively from the locked Basic config.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BasicNativeSummaryRequest(string ScopeAssignmentId, string? DynamicFormTemplateId = null,
    IReadOnlyList<string>? SelectedUnitIds = null, string? SnapshotId = null, bool Historical = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommandId = null);

// REVALIDATED describes the read boundary; this is not a Direct job publication
// or a lease on future freshness. A supplied stale SnapshotId is rejected.
public sealed record BasicNativeSummaryResponse(int Version, string SnapshotId, string SnapshotHash,
    string SourceSetHash, string Freshness, DateTime CheckedAtUtc, StatConfigIdentity Configuration,
    WorkAssignmentBasicSummaryResponse Legacy, NativeStatisticResultDocument Native)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeStatisticResultMetadata? NativeMetadata { get; init; }
}

// Completion records an exact capture, not a promise that future reads are fresh.
public sealed record BasicNativeRefreshResponse(string RefreshId, string State, int Attempt,
    string SnapshotId, string? SnapshotHash, string? BackgroundJobId, string? ErrorCode,
    DateTime UpdatedAtUtc);
