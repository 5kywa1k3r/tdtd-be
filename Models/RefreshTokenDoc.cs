//Models/RefreshTokenDoc.cs
namespace tdtd_be.Models
{
    using MongoDB.Bson;
    using MongoDB.Bson.Serialization.Attributes;

    public sealed class RefreshTokenDoc
    {
        [BsonId, BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        [BsonRepresentation(BsonType.ObjectId)]
        public string UserId { get; set; } = null!;

        public string TokenHash { get; set; } = null!;
        public DateTime ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RevokedAt { get; set; }

        public string? ReplacedByTokenHash { get; set; } // rotate
    }

}
