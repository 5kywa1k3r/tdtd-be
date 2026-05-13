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
        public int? SourceRowIndex { get; set; }
        public int? SourceRowNumber { get; set; }
        public string? SourceRowKey { get; set; }
        public string? SourceRowLabel { get; set; }

        // chỉ chứa phần values trong data range
        public List<decimal?> Values { get; set; } = new();
    }

    public sealed class AggregateRecordTableColumnDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string DataType { get; set; } = "text";
        public bool IsCalculated { get; set; }
    }

    public sealed class AggregateRecordTableRowDto
    {
        public string? ReportId { get; set; }
        public string? WorkAssignmentId { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? FullName { get; set; }
        public string? UnitSymbol { get; set; }
        public string? UnitShortName { get; set; }
        public string? PeriodKey { get; set; }
        public int? SourceRowIndex { get; set; }
        public string? SourceRowKey { get; set; }
        public Dictionary<string, object?> Values { get; set; } = new(StringComparer.Ordinal);
    }
}
