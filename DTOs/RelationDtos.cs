namespace tdtd_be.DTOs
{
    public sealed record UnitReportRelationCreateDto(string FromUnitId, string ToUnitId, string? Note);

    public sealed record UnitReportRelationDto(
        string Id,
        string FromUnitId,
        string ToUnitId,
        AuditDto Audit
    );

    public sealed record UnitReportRelationQuery(
        string? FromUnitId,
        string? ToUnitId,
        int Page = 1,
        int PageSize = 50
    );
}
