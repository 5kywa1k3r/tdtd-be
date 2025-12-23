//Models/AppUser.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;
namespace tdtd_be.Models
{
    [BsonCollection("users")]
    public sealed class AppUser
    {
        [BsonId, BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        public string Username { get; set; } = null!;
        public string PasswordHash { get; set; } = null!;

        public string FullName { get; set; } = null!;
        public List<String> UnitTypeId { get; set; } = new()!;
        public string UnitId { get; set; } = null!;
        public string UnitName { get; set; } = null!;

        public List<string> Roles { get; set; } = new();
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
