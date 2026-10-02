using System.Text.Json.Serialization;
using tdtd_be.DTOs.Statistics;

namespace tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvancedNativeComparisonRequest(string ScopeAssignmentId, string DynamicFormTemplateId,
    string SectionId, string Grain, string GrainKey, string SnapshotId, string SnapshotHash);
public sealed record AdvancedNativeComparisonDifference(string Reason, NativeStatisticAddressDto? Address, string? OperationId);
public sealed record AdvancedNativeComparisonRecordResponse(int Version, string RecordId, string RecordHash,
    string ActorId, DateTime RecordedAtUtc, string Freshness, AdvancedNativeComparisonResponse Observation);
public sealed record AdvancedNativeComparisonRecordDiagnostic(int Version, string RecordId, string RecordHash,
    string Coverage, string Consistency, string ReferenceStatus, bool CanDelete, bool CanRewrite);
public sealed record AdvancedNativeComparisonRecordPage(int Version, string ScopeAssignmentId, string DynamicFormTemplateId,
    string Coverage, string Consistency, string? NextAfter, bool CanDelete, bool CanRewrite,
    IReadOnlyList<AdvancedNativeComparisonRecordResponse> Items);
// Observation only; does not publish, repair, or create a durable reconciliation run.
public sealed record AdvancedNativeComparisonResponse(int Version, string Verdict, string Coverage,
    string ScopeAssignmentId, string DynamicFormTemplateId, string SectionId, string Grain, string GrainKey,
    string SnapshotId, string SnapshotHash, string SourceSetHash, DateTime CheckedAtUtc,
    IReadOnlyList<AdvancedNativeComparisonDifference> Differences);
