//Models/AppUser.cs
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace tdtd_be.Models
{
    public sealed class AppUser
    {
        [BsonId, BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        public string Username { get; set; } = null!;
        public string PasswordHash { get; set; } = null!;

        public string FullName { get; set; } = null!;
        public string JobTitle { get; set; } = null!;

        public string UnitId { get; set; } = null!;
        public string UnitName { get; set; } = null!;

        public List<string> Roles { get; set; } = new();
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
