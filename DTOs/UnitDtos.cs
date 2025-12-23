namespace tdtd_be.DTOs
{
    public sealed record UnitCreateDto(
        string FullName,
        string Code,
        string ShortName,
        string? ParentUnitId,
        string? Note
    );

    public sealed record UnitUpdateDto(
        string FullName,
        string Code,
        string ShortName,
        string? ParentUnitId,
        string? Note,
        long ExpectedVersion
    );

    public sealed record UnitDetailDto(
        string Id,
        string FullName,
        string Code,
        string ShortName,
        long Version,
        string? ParentUnitId,
        AuditDto Audit
    );

    public sealed record UnitListItemDto(
        string Id,
        string FullName,
        string Code,
        string ShortName,
        long Version,
        string? ParentUnitId,
        DateTime UpdatedAtUtc
    );

    public sealed record UnitQuery(
        string? Q,
        string? ParentUnitId,
        string? TagIds,
        int Page = 1,
        int PageSize = 20
    );
}
