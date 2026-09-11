using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P8FlowOwnerOracleBundle> CaptureP807FlowOwnerOracleAsync(
        IReadOnlyCollection<string> commandIds,
        CancellationToken ct)
    {
        var receiptFilter = commandIds.Count == 0
            ? Builders<BsonDocument>.Filter.In(
                "commandId",
                Array.Empty<string>())
            : Builders<BsonDocument>.Filter.In("commandId", commandIds);
        var receipts = await _database
            .GetCollection<BsonDocument>(FlowDefinitionReceiptsCollection)
            .Find(receiptFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);

        var familyIds = receipts
            .Select(receipt => BsonString(receipt, "familyId"))
            .Where(value => ObjectId.TryParse(value, out _))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var versionIds = receipts
            .Select(receipt => BsonString(receipt, "versionId"))
            .Where(value => ObjectId.TryParse(value, out _))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var families = familyIds.Length == 0
            ? []
            : await _database
                .GetCollection<BsonDocument>(FlowFamiliesCollection)
                .Find(Builders<BsonDocument>.Filter.In(
                    "_id",
                    familyIds.Select(ObjectId.Parse)))
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
        var versions = versionIds.Length == 0
            ? []
            : await _database
                .GetCollection<BsonDocument>(FlowVersionsCollection)
                .Find(Builders<BsonDocument>.Filter.In(
                    "_id",
                    versionIds.Select(ObjectId.Parse)))
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);

        return new P8FlowOwnerOracleBundle(
            families.Select(document => new P8FlowFamilyOracle(
                BsonString(document, "_id"),
                BsonString(document, "code"),
                BsonInt(document, "familyRevision"),
                BsonString(document, "status"),
                BsonString(document, "rootDynamicFormTemplateId"),
                BsonString(document, "currentVersionId"),
                BsonInt(document, "currentVersionNo"),
                BsonString(document, "currentVersionHash"),
                BsonBool(document, "hasLockedVersion"),
                Sha256(document.ToBson())))
                .ToArray(),
            versions.Select(document => new P8FlowVersionOracle(
                BsonString(document, "_id"),
                BsonString(document, "templateId"),
                BsonInt(document, "versionNo"),
                BsonString(document, "status"),
                BsonInt(document, "draftRevision"),
                BsonInt(document, "schemaVersion"),
                BsonInt(document, "adapterVersion"),
                BsonString(document, "catalogVersion"),
                BsonString(document, "catalogSemanticHash"),
                BsonString(document, "payloadHash"),
                Sha256(System.Text.Encoding.UTF8.GetBytes(
                    BsonString(document, "payloadJson") ?? string.Empty)),
                BsonString(document, "originFamilyId"),
                BsonString(document, "originVersionId"),
                BsonString(document, "contributionPolicy"),
                BsonString(document, "contributionPolicyHash"),
                BsonString(document, "contributionWarning"),
                BsonString(document, "lockedByUserId"),
                Sha256(document.ToBson())))
                .ToArray(),
            receipts.Select(document =>
            {
                var snapshot = document.TryGetValue(
                                   "resultVersionSnapshot",
                                   out var snapshotValue) &&
                               snapshotValue.IsBsonDocument
                    ? snapshotValue.AsBsonDocument
                    : null;
                return new P8FlowDefinitionReceiptOracle(
                    BsonString(document, "_id"),
                    BsonString(document, "commandId"),
                    BsonString(document, "commandKind"),
                    BsonString(document, "actorUserId"),
                    BsonString(document, "requestHash"),
                    BsonString(document, "familyId"),
                    BsonString(document, "versionId"),
                    BsonInt(document, "resultFamilyRevision"),
                    BsonInt(document, "resultDraftRevision"),
                    BsonString(document, "resultPayloadHash"),
                    BsonString(document, "resultVersionSnapshotSha256"),
                    snapshot is null ? null : Sha256(snapshot.ToBson()),
                    BsonString(document, "outcome"),
                    Sha256(document.ToBson()));
            }).ToArray());
    }
}

internal sealed record P8FlowOwnerOracleBundle(
    IReadOnlyList<P8FlowFamilyOracle> Families,
    IReadOnlyList<P8FlowVersionOracle> Versions,
    IReadOnlyList<P8FlowDefinitionReceiptOracle> DefinitionReceipts);

internal sealed record P8FlowFamilyOracle(
    string? Id,
    string? Code,
    int? FamilyRevision,
    string? Status,
    string? RootDynamicFormTemplateId,
    string? CurrentVersionId,
    int? CurrentVersionNo,
    string? CurrentVersionHash,
    bool? HasLockedVersion,
    string DocumentSha256);

internal sealed record P8FlowVersionOracle(
    string? Id,
    string? FamilyId,
    int? VersionNo,
    string? Status,
    int? DraftRevision,
    int? SchemaVersion,
    int? AdapterVersion,
    string? CatalogVersion,
    string? CatalogSemanticHash,
    string? PayloadHash,
    string PayloadJsonSha256,
    string? OriginFamilyId,
    string? OriginVersionId,
    string? ContributionPolicy,
    string? ContributionPolicyHash,
    string? ContributionWarning,
    string? LockedByUserId,
    string DocumentSha256);

internal sealed record P8FlowDefinitionReceiptOracle(
    string? Id,
    string? CommandId,
    string? CommandKind,
    string? ActorUserId,
    string? RequestHash,
    string? FamilyId,
    string? VersionId,
    int? ResultFamilyRevision,
    int? ResultDraftRevision,
    string? ResultPayloadHash,
    string? StoredResultVersionSnapshotSha256,
    string? ComputedResultVersionSnapshotSha256,
    string? Outcome,
    string DocumentSha256);
