namespace tdtd_be.DTOs.Users.Admin
{
    public sealed record UserSearchRow(
        string Id,
        string Username,
        string FullName,
        string UnitId,
        string UnitShortName,
        string UnitSymbol,
        string PositionCode,
        bool IsDeleted,
        IReadOnlyList<string> Roles
    );
}
