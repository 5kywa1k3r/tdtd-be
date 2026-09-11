using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models.Statistics;

/// <summary>
/// Immutable P9 Direct-generation lineage carried by every staged value and aggregate.
/// Publication is owned by the referenced rebuild job; rows never publish themselves.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class WorkReportDirectProjectionPin
{
    [BsonElement("runId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string RunId { get; set; } = default!;

    [BsonElement("generationId")]
    public string GenerationId { get; set; } = default!;

    [BsonElement("lifecycleEventKey")]
    public string LifecycleEventKey { get; set; } = default!;

    [BsonElement("sourceReportId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string SourceReportId { get; set; } = default!;

    [BsonElement("sourcePayloadRevision")]
    public int SourcePayloadRevision { get; set; }

    [BsonElement("sourcePayloadHash")]
    public string SourcePayloadHash { get; set; } = default!;

    [BsonElement("sourceLifecycleRevision")]
    public int SourceLifecycleRevision { get; set; }

    [BsonElement("directSourceRevision")]
    public long DirectSourceRevision { get; set; }

    [BsonElement("dynamicFormFamilyId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormFamilyId { get; set; } = default!;

    [BsonElement("dynamicFormTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicFormTemplateId { get; set; } = default!;

    [BsonElement("dynamicFormVersionNo")]
    public int DynamicFormVersionNo { get; set; }

    [BsonElement("dynamicFormSchemaHash")]
    public string DynamicFormSchemaHash { get; set; } = default!;

    [BsonElement("configId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ConfigId { get; set; } = default!;

    [BsonElement("configVersionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string ConfigVersionId { get; set; } = default!;

    [BsonElement("configVersionNo")]
    public int ConfigVersionNo { get; set; }

    [BsonElement("configRevision")]
    public long ConfigRevision { get; set; }

    [BsonElement("configHash")]
    public string ConfigHash { get; set; } = default!;

    [BsonElement("candidateChainId")]
    public string CandidateChainId { get; set; } = default!;

    [BsonElement("catalogVersion")]
    public string CatalogVersion { get; set; } = default!;

    [BsonElement("catalogRawSha256")]
    public string CatalogRawSha256 { get; set; } = default!;

    [BsonElement("catalogSemanticSha256")]
    public string CatalogSemanticSha256 { get; set; } = default!;

    [BsonElement("schemaRawSha256")]
    public string SchemaRawSha256 { get; set; } = default!;

    [BsonElement("schemaSemanticSha256")]
    public string SchemaSemanticSha256 { get; set; } = default!;

    [BsonElement("stageLockSha256")]
    public string StageLockSha256 { get; set; } = default!;

    [BsonElement("sourceMembershipSignature")]
    public string SourceMembershipSignature { get; set; } = default!;

    [BsonElement("computedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ComputedAtUtc { get; set; }
}
