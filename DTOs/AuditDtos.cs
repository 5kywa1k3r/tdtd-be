namespace tdtd_be.DTOs
{
    public sealed record AuditDto(
        string? CreatedByUserId,
        string? UpdatedByUserId,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        string? Note
    );
}
