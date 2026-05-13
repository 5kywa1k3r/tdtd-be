//Models/AppUser.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace tdtd_be.DTOs.Auth
{
    public interface ICurrentUserContext
    {
        MeResponse Me { get; }
    }
    public sealed class MeResponse
    {
        public MeResponse(
            string id,
            string username,
            string fullName,
            List<string> unitTypeCodes,
            string unitId,
            string? unitSymbol,
            string? unitName,
            string? unitCode,
            List<string> roles,
            string? positionCode,
            bool isDeleted,
            string? accountKind = null)
        {
            Id = id;
            Username = username;
            FullName = fullName;
            UnitTypeCodes = unitTypeCodes;
            UnitId = unitId ?? "";
            UnitCode = unitCode ?? "";
            UnitName = unitName ?? "";
            UnitSymbol = unitSymbol ?? "";
            Roles = roles;
            PositionCode = positionCode ?? "";
            IsDeleted = isDeleted;
            AccountKind = accountKind ?? "";
        }

        [BsonId, BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;
        public string Username { get; set; } = null!;
        public string FullName { get; set; } = null!;
        public List<string> UnitTypeCodes { get; set; } = new()!;
        public string UnitId { get; set; } = "";
        public string UnitSymbol { get; set; } = "";
        public string UnitCode { get; set; } = "";
        public string UnitName { get; set; } = "";
        public List<string> Roles { get; set; } = new();
        public string PositionCode { get; set; } = "";
        public string AccountKind { get; set; } = "";
        public bool IsDeleted { get; set; } = false;
    }
}
