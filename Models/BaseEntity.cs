using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models
{
    public abstract class BaseEntity
    {
        [BsonRepresentation(BsonType.ObjectId)]
        public string? CreatedByUserId { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string? UpdatedByUserId { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public string? Note { get; set; }
    }
}
