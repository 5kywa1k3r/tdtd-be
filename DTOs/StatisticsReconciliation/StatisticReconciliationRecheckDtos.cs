using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsReconciliation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationBeginRecheckRequest
{
    public string CommandId { get; init; } = string.Empty;
    public long ExpectedStateRevision { get; init; }
    public string ExpectedStateHash { get; init; } = string.Empty;
}

public sealed class StatisticReconciliationBeginRecheckResponse
{
    public bool IsReplay { get; init; }
    public string ReconciliationId { get; init; } = string.Empty;
    public string RecheckMarkerId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long StateRevision { get; init; }
    public string StateHash { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationRecheckClaimRequest
{
    public string WorkerId { get; init; } = string.Empty;
}
