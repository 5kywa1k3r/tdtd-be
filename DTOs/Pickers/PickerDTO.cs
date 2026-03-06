namespace tdtd_be.DTOs.Pickers;

public sealed class UnitPickRow
{
    public string Id { get; set; } = default!;
    public string Code { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string? ShortName { get; set; }
    public string? Symbol { get; set; }
    public int Level { get; set; }
    public string? ParentId { get; set; }
}

public sealed class UserPickRow
{
    public string Id { get; set; } = default!;
    public string Username { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string? UnitId { get; set; }
    public string? UnitCode { get; set; }
    public string? UnitShortName { get; set; }
    public string? UnitSymbol { get; set; }
    public string? PositionCode { get; set; } // enum chức vụ
}