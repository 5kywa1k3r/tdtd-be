namespace tdtd_be.DTOs.DynamicForms;

public sealed record DynamicFormReferenceGroup(string Source, IReadOnlyList<string> Paths,
    int ObservedCount, string State, string? EvidenceSha256);

public sealed record DynamicFormReferenceAssessment(string ContractId, int Version, string OwnerId,
    string StorageSha256, string AssessmentSha256, string Consistency, bool ReadOnly,
    bool MigrationSupported, bool ReferenceScanComplete, bool PublishedEvidence,
    IReadOnlyList<DynamicFormReferenceGroup> Groups, IReadOnlyList<string> Gaps);
