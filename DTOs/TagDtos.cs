namespace tdtd_be.DTOs
{
    public sealed record TagCreateDto(string Name, string? Description, string? Category, string? Note);
    public sealed record TagUpdateDto(string Name, string? Description, string? Category, string? Note, long ExpectedVersion);

    public sealed record TagDto(
    string Id,
    string Name,
    string? Description,
    string? Category,
    long Version,
    AuditDto Audit
);

    public sealed record TagQuery(string? Q, string? Category, int Page = 1, int PageSize = 50);

    public sealed record TagLinkCreateDto(string EntityType, string EntityId, string TagId, string? Note);

    public sealed record TagLinkDto(
        string Id,
        string EntityType,
        string EntityId,
        string TagId,
        AuditDto Audit
    );

    public sealed record TagLinkQuery(
        string? EntityType,
        string? EntityId,
        string? TagId,
        int Page = 1,
        int PageSize = 100
    );
}
