namespace tdtd_be.DTOs.WorkAssignments;

public sealed class CompleteWorkAssignmentRequest
{
    public DateTime? CompletedDate { get; set; }
    public string? Note { get; set; }
}
