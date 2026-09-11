namespace tdtd_be.DTOs.StatisticsReconciliation;

public sealed class StatisticReconciliationRemediationEvidenceResponse
{
    public string ReconciliationId { get; init; } = default!;
    public string EvidenceId { get; init; } = default!;
    public string EvidenceSha256 { get; init; } = default!;
    public string SuccessorP9GenerationId { get; init; } = default!;
    public bool IsReplay { get; init; }
}
