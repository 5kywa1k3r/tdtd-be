using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.Statistics;

// Optional on old jobs. Every field is bound into GenerationHash. The immutable
// artifact belongs to the job's RunId/GenerationId, never an implicit "latest".
public sealed class WorkReportNativeStatisticPublication
{
    [BsonElement("version")]
    public int Version { get; set; } = 2;
    [BsonElement("artifactHash")]
    public string ArtifactHash { get; set; } = string.Empty;
    [BsonElement("manifestHash")]
    public string ManifestHash { get; set; } = string.Empty;
    [BsonElement("bytes")]
    public int Bytes { get; set; }
    [BsonElement("chunkCount")]
    public int ChunkCount { get; set; }
    [BsonElement("sourceOrderHash")]
    public string SourceOrderHash { get; set; } = string.Empty;
}
