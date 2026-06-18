using System.Text.Json;

namespace tdtd_be.DTOs.WorkAssignmentReports;

public sealed class SaveWorkAssignmentReportDraftPatchRequest
{
    public int? Values1DLength { get; set; }
    public List<WorkReportValuePatchItem>? Values1DPatch { get; set; }
    public string? FieldValuesJson { get; set; }
    public List<WorkReportTableBlockPatch>? TableBlockPatches { get; set; }
    public string? DataOrigin { get; set; }
    public string? CumulativeContributionMode { get; set; }
    public string? CumulativeContributionPolicyJson { get; set; }
    public string? SummarySourceJson { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string? LateReason { get; set; }
    public string? Note { get; set; }
}

public sealed class WorkReportValuePatchItem
{
    public int Index { get; set; }
    public JsonElement Value { get; set; }
}

public sealed class WorkReportTableBlockPatch
{
    public string BlockId { get; set; } = string.Empty;
    public string BlockJson { get; set; } = string.Empty;
}
