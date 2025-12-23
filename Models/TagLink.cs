using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models
{
    [BsonCollection("tag_links")]
    public sealed class TagLink : BaseEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = default!;

        public string EntityType { get; set; } = "Unit";

        [BsonRepresentation(BsonType.ObjectId)]
        public string EntityId { get; set; } = default!;

        [BsonRepresentation(BsonType.ObjectId)]
        public string TagId { get; set; } = default!;
    }
}
