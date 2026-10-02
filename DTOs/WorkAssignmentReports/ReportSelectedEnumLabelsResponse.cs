namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>Labels for only the choice codes already stored in a readable report.</summary>
public sealed record ReportSelectedEnumLabelsResponse(
    string ReportId,
    int PayloadRevision,
    string? SchemaHash,
    Dictionary<string, Dictionary<string, Dictionary<string, string>>> Tables);
