namespace tdtd_be.DTOs.Units
{
    public sealed record UnitPickNodeDTO(
        string Id,
        string FullName,
        string Code,
        int Level,
        string? ShortName,
        string Symbol,
        string? PrimaryUnitTypeCode,
        bool IsVirtual
    );
}
