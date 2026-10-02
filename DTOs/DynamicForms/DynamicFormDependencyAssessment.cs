namespace tdtd_be.DTOs.DynamicForms;

public sealed record CanvasDependencySlot(string Path, string State, string? Sha256);
public sealed record CanvasDependencyIssue(string Path, string Code);
public sealed record CanvasDependencyPin(string Path, string State, string? TargetSha256);
public sealed record DynamicFormDependencyAssessment(string ContractId, int Version, string OwnerId,
    string StorageSha256, string AssessmentSha256, string Consistency, bool ReadOnly, bool MigrationSupported,
    bool ReferenceScanComplete, bool Truncated, IReadOnlyList<CanvasDependencySlot> Slots,
    IReadOnlyList<CanvasDependencyPin> Pins, IReadOnlyList<CanvasDependencyIssue> Issues, IReadOnlyList<string> Gaps);
