namespace tdtd_be.DTOs.WorkAssignments;

public sealed record AssignmentFormPickerQueryRequest(
    string? ParentAssignmentId,
    string? Q,
    string? SelectedId,
    int Page = 0,
    int PageSize = 20);

public sealed record AssignmentFormPickerRow(
    string Id,
    string Code,
    string Name,
    int VersionNo,
    bool IsInherited,
    bool CanBind);

public sealed record AssignmentFormPickerResult(
    List<AssignmentFormPickerRow> Rows,
    long TotalRows,
    int Page,
    int PageSize,
    bool SelectedIdValid);
