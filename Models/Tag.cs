using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models
{
    [BsonCollection("tags")]
    public sealed class Tag : BaseEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = default!;

        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string? Category { get; set; } // nhóm tag

        public long Version { get; set; } = 1;
    }
}
