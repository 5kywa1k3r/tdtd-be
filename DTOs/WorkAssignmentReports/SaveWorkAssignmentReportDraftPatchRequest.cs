using System.Text.Json;

namespace tdtd_be.DTOs.WorkAssignmentReports;

public sealed class SaveWorkAssignmentReportDraftPatchRequest
{
    public int? ExpectedPayloadRevision { get; set; }
    public string? CommandId { get; set; }
    public int? Values1DLength { get; set; }
    public List<WorkReportValuePatchItem>? Values1DPatch { get; set; }
    public string? FieldValuesJson { get; set; }
    public List<WorkReportTableBlockPatch>? TableBlockPatches { get; set; }
    // Tạm giữ CLR compatibility cho code nội bộ; JSON client không còn được sửa Advanced cũ.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DataOrigin { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CumulativeContributionMode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CumulativeContributionPolicyJson { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
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
