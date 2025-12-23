//Models/RefreshTokenDoc.cs
namespace tdtd_be.Models
{
    using MongoDB.Bson;
    using MongoDB.Bson.Serialization.Attributes;
    using tdtd_be.Data.Infrastructure;

    [BsonCollection("refresh_token")]
    public class RefreshTokenDoc
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = default!;

        [BsonElement("userId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string UserId { get; set; } = default!;

        [BsonElement("tokenHash")]
        public string TokenHash { get; set; } = default!;

        [BsonElement("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("expiresAt")]
        public DateTime ExpiresAt { get; set; }

        [BsonElement("revokedAt")]
        public DateTime? RevokedAt { get; set; }

        [BsonElement("replacedByTokenHash")]
        public string? ReplacedByTokenHash { get; set; }

        [BsonIgnore]
        public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
    }

}
