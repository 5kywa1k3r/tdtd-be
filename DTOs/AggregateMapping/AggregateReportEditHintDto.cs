namespace tdtd_be.DTOs.AggregateMapping;

// Presentation hint on an already-authorized report, never a mutation authorization.
public sealed record AggregateReportEditHintDto(string ReportId, string ActorId, long PayloadRevision,
    long LifecycleRevision, string SchemaHash, string ClaimDigest, IReadOnlyList<string> ReadOnlyMemberIds);
