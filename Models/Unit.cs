using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models
{
    [BsonCollection("units")]
    public sealed class Unit: BaseEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = default!;

        public string FullName { get; set; } = default!;
        public string Code { get; set; } = default!;
        public string ShortName { get; set; } = default!;

        public long Version { get; set; } = 1;

        [BsonRepresentation(BsonType.ObjectId)]
        public string? ParentUnitId { get; set; }
    }
}
