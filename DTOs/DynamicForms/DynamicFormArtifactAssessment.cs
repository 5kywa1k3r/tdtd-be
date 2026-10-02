namespace tdtd_be.DTOs.DynamicForms;

public sealed record CanvasArtifactCheck(int Index, string State, string JobSha256, string? ArtifactSha256);
public sealed record DynamicFormArtifactAssessment(string ContractId, int Version, string OwnerId,
    string StorageSha256, string AssessmentSha256, string Consistency, bool ReadOnly, bool MigrationSupported,
    bool ReferenceScanComplete, bool Truncated, IReadOnlyList<CanvasArtifactCheck> Jobs, IReadOnlyList<string> Gaps);
