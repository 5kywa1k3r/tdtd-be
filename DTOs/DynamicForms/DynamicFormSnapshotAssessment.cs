namespace tdtd_be.DTOs.DynamicForms;

// An exact caller-supplied locator, never a reverse-reference discovery result.
public sealed record DynamicFormSnapshotAssessment(string ContractId, int Version, string OwnerId,
    string StorageSha256, string AssessmentSha256, string Kind, string SnapshotId, string? RefreshId,
    string Consistency, bool ReadOnly, bool MigrationSupported, bool ReferenceScanComplete,
    string State, string? SnapshotSha256, string? RefreshSha256, IReadOnlyList<string> Gaps);
