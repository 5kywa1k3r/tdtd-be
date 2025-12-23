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
        public MeResponse(string id, string username, string fullName, List<string> unitTypeId, string unitId, string unitName, List<string> roles, bool isActive)
        {
            Id = id;
            Username = username;
            FullName = fullName;
            UnitTypeId = unitTypeId;
            UnitId = unitId;
            UnitName = unitName;
            Roles = roles;
            IsActive = isActive;
        }

        [BsonId, BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;
        public string Username { get; set; } = null!;
        public string FullName { get; set; } = null!;
        public List<string> UnitTypeId { get; set; } = new()!;
        public string UnitId { get; set; } = null!;
        public string UnitName { get; set; } = null!;
        public List<string> Roles { get; set; } = new();
        public bool IsActive { get; set; } = true;
    }
}
