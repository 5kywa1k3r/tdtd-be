using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Security.Cryptography;
using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public sealed partial class DynamicFlowTemplateService
{
    private static BsonDocument BuildReceiptFamilySnapshot(DynamicFlowTemplate family)
        => family.ToBsonDocument();

    private static BsonDocument? BuildReceiptVersionSnapshot(DynamicFlowTemplateVersion? version)
        => version?.ToBsonDocument();

    private static string ReceiptSnapshotSha256(BsonDocument snapshot)
        => Convert.ToHexString(SHA256.HashData(snapshot.ToBson())).ToLowerInvariant();

    private static DynamicFlowTemplate ReadReceiptFamilySnapshot(
        DynamicFlowDefinitionCommandReceipt receipt)
    {
        try
        {
            if (receipt.ResultFamilySnapshot is null ||
                string.IsNullOrWhiteSpace(receipt.ResultFamilySnapshotSha256))
            {
                throw MissingReceiptSnapshot("family");
            }
            if (!string.Equals(
                    ReceiptSnapshotSha256(receipt.ResultFamilySnapshot),
                    receipt.ResultFamilySnapshotSha256,
                    StringComparison.Ordinal))
            {
                throw InvalidReceiptSnapshot("family");
            }

            var family = BsonSerializer.Deserialize<DynamicFlowTemplate>(
                receipt.ResultFamilySnapshot.DeepClone().AsBsonDocument);
            if (!string.Equals(family.Id, receipt.FamilyId, StringComparison.Ordinal) ||
                family.FamilyRevision != receipt.ResultFamilyRevision)
            {
                throw InvalidReceiptSnapshot("family");
            }
            return family;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception error) when (IsSnapshotReadFailure(error))
        {
            throw InvalidReceiptSnapshot("family", error);
        }
    }

    private static DynamicFlowTemplateVersion ReadReceiptVersionSnapshot(
        DynamicFlowDefinitionCommandReceipt receipt)
    {
        try
        {
            if (receipt.ResultVersionSnapshot is null ||
                string.IsNullOrWhiteSpace(receipt.ResultVersionSnapshotSha256) ||
                string.IsNullOrWhiteSpace(receipt.VersionId))
            {
                throw MissingReceiptSnapshot("version");
            }
            if (!string.Equals(
                    ReceiptSnapshotSha256(receipt.ResultVersionSnapshot),
                    receipt.ResultVersionSnapshotSha256,
                    StringComparison.Ordinal))
            {
                throw InvalidReceiptSnapshot("version");
            }

            var version = BsonSerializer.Deserialize<DynamicFlowTemplateVersion>(
                receipt.ResultVersionSnapshot.DeepClone().AsBsonDocument);
            if (!string.Equals(version.Id, receipt.VersionId, StringComparison.Ordinal) ||
                !string.Equals(version.TemplateId, receipt.FamilyId, StringComparison.Ordinal) ||
                version.DraftRevision != receipt.ResultDraftRevision ||
                !string.Equals(version.PayloadHash, receipt.ResultPayloadHash, StringComparison.Ordinal))
            {
                throw InvalidReceiptSnapshot("version");
            }
            return version;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception error) when (IsSnapshotReadFailure(error))
        {
            throw InvalidReceiptSnapshot("version", error);
        }
    }

    private static AppException MissingReceiptSnapshot(string target)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
            new
            {
                reason = "DYNAMIC_FLOW_COMMAND_RESULT_SNAPSHOT_MISSING",
                target
            });

    private static AppException InvalidReceiptSnapshot(
        string target,
        Exception? innerException = null)
        => new(
            AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
            new
            {
                reason = "DYNAMIC_FLOW_COMMAND_RESULT_SNAPSHOT_INVALID",
                target
            },
            innerException: innerException);

    private static bool IsSnapshotReadFailure(Exception error)
        => error is BsonSerializationException or
           FormatException or
           InvalidOperationException or
           ArgumentException or
           NotSupportedException;
}
