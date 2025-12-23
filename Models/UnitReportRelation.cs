using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models
{
    [BsonCollection("unit_report_relations")]
    public sealed class UnitReportRelation : BaseEntity
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = default!;

        [BsonRepresentation(BsonType.ObjectId)]
        public string FromUnitId { get; set; } = default!; // đơn vị báo cáo

        [BsonRepresentation(BsonType.ObjectId)]
        public string ToUnitId { get; set; } = default!;   // đơn vị nhận báo cáo
    }
}
