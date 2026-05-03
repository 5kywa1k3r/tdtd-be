namespace tdtd_be.DTOs.WorkAssignments.Aggregate
{
    public sealed class AggregateTableRowDto
    {
        public string? WorkAssignmentId { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? FullName { get; set; }
        public string? UnitSymbol { get; set; }
        public string? UnitShortName { get; set; }

        // chỉ chứa phần values trong data range
        public List<decimal?> Values { get; set; } = new();
    }
}
