using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("dynamic_excel_template")]
public sealed class DynamicExcelTemplate : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    // ✅ code: saAnhdd2026001...
    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    // ✅ labels nhẹ: list string (sau này muốn object {id,name} cũng được)
    [BsonElement("labels")]
    public string[] Labels { get; set; } = Array.Empty<string>();

    // ✅ để search/filter theo người tạo mà không join
    [BsonElement("createdByUsername")]
    public string CreatedByUsername { get; set; } = default!;

    // NUMERIC_GRID: legacy dataRect/values1D decimal grid.
    // RECORD_TABLE: typed record schema for raw-data consolidation.
    [BsonElement("tableKind")]
    public string TableKind { get; set; } = DynamicExcelTableKind.NumericGrid;

    [BsonElement("recordTableSpecJson")]
    public string? RecordTableSpecJson { get; set; }

    // FortuneSheet full JSON
    [BsonElement("rawWorkbookDataJson")]
    public string RawWorkbookDataJson { get; set; } = default!;

    // TOP/LEFT/MATRIX spec JSON
    [BsonElement("specJson")]
    public string SpecJson { get; set; } = default!;

    // dataRect (atomic)
    [BsonElement("dataRectR0")]
    public int DataRectR0 { get; set; }

    [BsonElement("dataRectC0")]
    public int DataRectC0 { get; set; }

    [BsonElement("dataRectR1")]
    public int DataRectR1 { get; set; }

    [BsonElement("dataRectC1")]
    public int DataRectC1 { get; set; }

    [BsonElement("w")]
    public int W { get; set; }

    [BsonElement("h")]
    public int H { get; set; }
}

public static class DynamicExcelTableKind
{
    public const string NumericGrid = "NUMERIC_GRID";
    public const string RecordTable = "RECORD_TABLE";
}
