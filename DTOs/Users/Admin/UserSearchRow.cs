namespace tdtd_be.DTOs.Users.Admin
{
    public sealed record UserSearchRow(
        string Id,
        string Username,
        string FullName,
        string UnitId,
        string UnitShortName,
        string UnitSymbol,
        string UnitCode,
        string PositionCode,
        string PositionName,
        bool IsDeleted,
        IReadOnlyList<string> Roles
    );
}
