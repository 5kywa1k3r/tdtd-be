using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonCollection("file_docs")]
public sealed class FileDoc: BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonElement("id")]
    public string Id { get; set; } = default!;
    [BsonElement("bucket")]

    public string Bucket { get; set; } = default!;
    [BsonElement("objectKey")]
    public string ObjectKey { get; set; } = default!;
    [BsonElement("uploadId")]
    public string UploadId { get; set; } = default!;
    [BsonElement("originalName")]

    public string OriginalName { get; set; } = default!;
    [BsonElement("mimeType")]
    public string MimeType { get; set; } = "application/octet-stream";
    [BsonElement("size")]
    public long Size { get; set; }

    // nhẹ: dùng etag của MinIO để nhận diện (dedupe/integrity)
    [BsonElement("etag")]
    public string? ETag { get; set; }

    // nếu muốn hash mạnh sau này
    [BsonElement("sha256")]
    public string? Sha256 { get; set; }

    // source trace
    public string SourceType { get; set; } = "UPLOAD"; // WORK/INDICATOR/EXCEL/...
    public string? SourceId { get; set; }
}