using tdtd_be.DTOs.WorkAssignments.Aggregate;

namespace tdtd_be.DTOs.WorkAssignments.AggregateTable;

public sealed class AggregateTableResponse
{
    public string DynamicExcelId { get; set; } = default!;
    public string? DynamicExcelCode { get; set; }
    public string? DynamicExcelName { get; set; }

    public string? PeriodScopeMode { get; set; }
    public string? PeriodKey { get; set; }
    public string? PeriodKeyFrom { get; set; }
    public string? PeriodKeyTo { get; set; }

    public List<string> SelectedUnitIds { get; set; } = new();

    public string? AggregateMode { get; set; }

    public int? PeriodCount { get; set; }
    public List<string> IncludedPeriodKeys { get; set; } = new();

    public int DataRectR0 { get; set; }
    public int DataRectC0 { get; set; }
    public int DataRectR1 { get; set; }
    public int DataRectC1 { get; set; }
    public decimal? W { get; set; }
    public decimal? H { get; set; }

    public List<string> MetaColumns { get; set; } = new();
    public List<AggregateTableRowDto> Rows { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<AggregateSourceRowDto> Sources { get; set; } = new();
}
