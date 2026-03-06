namespace tdtd_be.DTOs.Users
{
    public class UserRefDTO
    {
        public UserRefDTO() { }
        public UserRefDTO(
            string userId,
            string? username,
            string? fullName,
            string? unitId,
            string? unitSymbol,
            string? unitShortName,
            string? unitName,
            string? positionCode,
            string? positionName)
        {
            UserId = userId;
            Username = username;
            FullName = fullName;
            UnitId = unitId;
            UnitSymbol = unitSymbol;
            UnitShortName = unitShortName;
            UnitName = unitName;
            PositionCode = positionCode;
            PositionName = positionName;
        }
        public string UserId { get; set; } = default!;
        public string? Username { get; set; }
        public string? FullName { get; set; }
        public string? UnitId { get; set; }
        public string? UnitSymbol { get; set; }
        public string? UnitShortName { get; set; }
        public string? UnitName { get; set; }
        public string? PositionCode { get; set; }
        public string? PositionName { get; set; }
    }
}
