using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P808AtomicLinkOracle> RequireP808AtomicLinksAsync(
        string jobId,
        P8ConfigIdentity owner,
        string commandId,
        CancellationToken ct)
    {
        var job = await RequireP808JobAsync(jobId, ct);
        RequireP808NoDatasetOrReportIdentity(job, "P8 readiness job");
        HarnessAssert.Equal(
            P808QueueName,
            BsonString(job, "queueName"),
            "P8 readiness queue mismatch");
        HarnessAssert.Equal(
            owner.OwnerKind,
            BsonString(job, "ownerKind"),
            "P8 readiness ownerKind mismatch");
        HarnessAssert.Equal(
            owner.OwnerId,
            BsonString(job, "ownerId"),
            "P8 readiness ownerId mismatch");
        HarnessAssert.Equal(
            owner.ConfigId,
            BsonString(job, "configId"),
            "P8 readiness configId mismatch");
        HarnessAssert.Equal(
            owner.VersionId,
            BsonString(job, "versionId"),
            "P8 readiness versionId mismatch");
        HarnessAssert.Equal(
            owner.VersionNo,
            BsonInt(job, "versionNo"),
            "P8 readiness versionNo mismatch");
        HarnessAssert.Equal(
            owner.Revision,
            BsonLong(job, "configRevision"),
            "P8 readiness configRevision mismatch");
        HarnessAssert.Equal(
            owner.ConfigHash,
            BsonString(job, "configHash"),
            "P8 readiness configHash mismatch");
        HarnessAssert.Equal(
            commandId,
            BsonString(job, "enqueueCommandId"),
            "P8 readiness commandId mismatch");
        HarnessAssert.True(
            job.GetValue("dependencyPins", new BsonArray()).IsBsonArray,
            "P8 readiness dependencyPins are not persisted as an array.");
        RequireP808LowerSha(BsonString(job, "dependencyPinsHash"),
            "dependencyPinsHash");
        RequireP808LowerSha(BsonString(job, "bundleHash"), "bundleHash");
        RequireP808LowerSha(BsonString(job, "stateHash"), "stateHash");
        RequireP808LowerSha(BsonString(job, "dedupeKey"), "dedupeKey");

        var receiptId = BsonString(job, "commandReceiptId")
                        ?? throw new InvalidOperationException(
                            "P8 readiness job lacks commandReceiptId.");
        var outboxId = BsonString(job, "auditOutboxId")
                       ?? throw new InvalidOperationException(
                           "P8 readiness job lacks auditOutboxId.");
        var receipt = await _database
            .GetCollection<BsonDocument>(ReceiptsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", receiptId))
            .SingleAsync(ct);
        var outbox = await _database
            .GetCollection<BsonDocument>(P808AuditOutboxCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", outboxId))
            .SingleAsync(ct);

        HarnessAssert.Equal(
            owner.OwnerKind,
            BsonString(receipt, "ownerKind"),
            "P8 readiness receipt ownerKind mismatch");
        HarnessAssert.Equal(
            owner.OwnerId,
            BsonString(receipt, "ownerId"),
            "P8 readiness receipt ownerId mismatch");
        HarnessAssert.Equal(
            commandId,
            BsonString(receipt, "commandId"),
            "P8 readiness receipt commandId mismatch");
        RequireP808LowerSha(BsonString(receipt, "requestHash"),
            "receipt requestHash");
        RequireP808LowerSha(BsonString(receipt, "responseHash"),
            "receipt responseHash");
        HarnessAssert.Equal(
            owner.ConfigId,
            BsonString(receipt, "resultConfigId"),
            "P8 readiness receipt configId mismatch");
        HarnessAssert.Equal(
            owner.VersionId,
            BsonString(receipt, "resultVersionId"),
            "P8 readiness receipt versionId mismatch");
        HarnessAssert.Equal(
            owner.ConfigHash,
            BsonString(receipt, "resultConfigHash"),
            "P8 readiness receipt configHash mismatch");

        HarnessAssert.Equal(
            owner.OwnerKind,
            BsonString(outbox, "ownerKind"),
            "P8 audit outbox ownerKind mismatch");
        HarnessAssert.Equal(
            owner.OwnerId,
            BsonString(outbox, "ownerId"),
            "P8 audit outbox ownerId mismatch");
        HarnessAssert.Equal(
            commandId,
            BsonString(outbox, "commandId"),
            "P8 audit outbox commandId mismatch");
        HarnessAssert.Equal(
            receiptId,
            BsonString(outbox, "receiptId"),
            "P8 audit outbox receipt linkage mismatch");
        HarnessAssert.Equal(
            BsonString(job, "correlationId"),
            BsonString(outbox, "correlationId"),
            "P8 audit outbox correlation linkage mismatch");
        RequireP808LowerSha(BsonString(outbox, "payloadHash"),
            "outbox payloadHash");
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(BsonString(outbox, "safeSummary")),
            "P8 audit outbox lacks safeSummary.");
        RequireP808NoDatasetOrReportIdentity(
            outbox,
            "P8 config audit outbox");

        return new P808AtomicLinkOracle(
            jobId,
            receiptId,
            outboxId,
            BsonString(job, "dedupeKey")!,
            BsonString(job, "correlationId")!,
            BsonString(job, "status")!,
            Sha256(job.ToBson()),
            Sha256(receipt.ToBson()),
            Sha256(outbox.ToBson()));
    }

    private static void RequireP808LowerSha(
        string? value,
        string context)
        => HarnessAssert.True(
            value is { Length: 64 } && value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"P8-08 {context} is not lowercase SHA-256.");
}

internal sealed record P808AtomicLinkOracle(
    string JobId,
    string ReceiptId,
    string AuditOutboxId,
    string DedupeKey,
    string CorrelationId,
    string InternalStatus,
    string JobDocumentSha256,
    string ReceiptDocumentSha256,
    string AuditOutboxDocumentSha256);

