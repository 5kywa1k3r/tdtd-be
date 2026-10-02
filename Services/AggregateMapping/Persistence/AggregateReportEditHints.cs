using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateReportEditHints
{
    internal static AggregateReportEditHintDto Build(string reportId, string actorId, long payloadRevision,
        long lifecycleRevision, string schemaHash, IReadOnlyList<AggregateTargetClaim> claims)
    {
        if (claims.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        if (claims.Any(c => c == null || c.ReportId != reportId || string.IsNullOrWhiteSpace(c.MemberId)
            || string.IsNullOrWhiteSpace(c.InstanceId))) throw new AggregatePreviewException("AGG_TARGET_CLAIM_INVALID");
        var members = claims.Select(c => c.MemberId).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        return new(reportId, actorId, payloadRevision, lifecycleRevision, schemaHash,
            AggregateDigest.Of(claims.OrderBy(c => c.MemberId, StringComparer.Ordinal).ThenBy(c => c.InstanceId, StringComparer.Ordinal)), members);
    }
}
