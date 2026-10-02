using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services.StatisticsRun;

internal static class NativeStatisticPublicationContract
{
    // Server resource policy, never a default for missing user configuration.
    // Exceeding these bounds aborts the entire generation; no partial publication.
    internal static readonly NativeStatisticCalculationLimits CalculationLimits =
        new(1000, 50_000, 2_000_000, 64 * 1024 * 1024, 32 * 1024 * 1024, 16 * 1024);
    internal static readonly NativeStatisticStorageLimits StorageLimits =
        new(96 * 1024 * 1024, 256 * 1024, 384);
    internal const long MaxSourcePayloadBytes = 64L * 1024 * 1024;

    internal static string BindHash(string legacyGenerationHash, WorkReportNativeStatisticPublication? native)
    {
        if (native is null) return legacyGenerationHash; // Preserve historical bytes/hashes.
        Validate(native);
        return StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_NATIVE_GENERATION_HASH_V1", legacyGenerationHash, native
        });
    }

    internal static WorkReportNativeStatisticPublication Create(NativeStatisticArtifactReceipt receipt,
        NativeStatisticGenerationArtifact artifact) => new()
    {
        Version = 2, ArtifactHash = receipt.ArtifactHash, ManifestHash = receipt.ManifestHash,
        Bytes = receipt.Bytes, ChunkCount = receipt.ChunkCount, SourceOrderHash = artifact.Result.SourceOrderDigest
    };

    internal static async Task<NativeStatisticGenerationArtifact> ReadAsync(IMongoDatabase db,
        WorkReportStatisticRebuildJob job, CancellationToken ct, IClientSessionHandle? session = null)
    {
        var native = job.NativeStatisticPublication ?? throw Invalid("NATIVE_PUBLICATION_REQUIRED");
        Validate(native);
        var artifact = await new NativeStatisticArtifactStore(db).ReadAsync(
            new(job.Id, job.GenerationId!, native.ArtifactHash, native.ManifestHash, native.Bytes, native.ChunkCount),
            StorageLimits, ct, session);
        var pin = artifact.Generation;
        if (artifact.WorkId != job.WorkId || artifact.PeriodInstanceKey != job.PeriodInstanceKey
            || pin.RunId != job.Id || pin.GenerationId != job.GenerationId
            || pin.LifecycleEventKey != job.SourceLifecycleEventKey || pin.DirectSourceRevision != job.DirectSourceRevision
            || pin.DynamicFormFamilyId != job.DynamicFormFamilyId || pin.DynamicFormTemplateId != job.DynamicFormTemplateId
            || pin.DynamicFormVersionNo != job.DynamicFormVersionNo || pin.DynamicFormSchemaHash != job.DynamicFormSchemaHash
            || pin.ConfigId != job.ConfigId || pin.ConfigVersionId != job.ConfigVersionId
            || pin.ConfigVersionNo != job.ConfigVersionNo || pin.ConfigRevision != job.ConfigRevision || pin.ConfigHash != job.ConfigHash
            || pin.CandidateChainId != job.CandidateChainId || pin.CatalogVersion != job.CatalogVersion
            || pin.CatalogRawSha256 != job.CatalogRawSha256 || pin.CatalogSemanticSha256 != job.CatalogSemanticSha256
            || pin.SchemaRawSha256 != job.SchemaRawSha256 || pin.SchemaSemanticSha256 != job.SchemaSemanticSha256
            || pin.StageLockSha256 != job.StageLockSha256 || pin.SourceMembershipSignature != job.SourceMembershipSignature
            || pin.ComputedAtUtc != job.ComputedAtUtc || artifact.Sources.Count != job.TotalReportCount
            || artifact.Result.SourceOrderDigest != native.SourceOrderHash)
            throw Invalid("NATIVE_PUBLICATION_PIN_MISMATCH");
        return artifact;
    }

    private static void Validate(WorkReportNativeStatisticPublication native)
    {
        if (native.Version != 2 || !StatRunCanonicalJson.IsCanonicalSha256(native.ArtifactHash)
            || !StatRunCanonicalJson.IsCanonicalSha256(native.ManifestHash)
            || !StatRunCanonicalJson.IsCanonicalSha256(native.SourceOrderHash)
            || native.Bytes < 1 || native.Bytes > StorageLimits.MaxArtifactBytes
            || native.ChunkCount < 1 || native.ChunkCount > StorageLimits.MaxChunks)
            throw Invalid("NATIVE_PUBLICATION_RECEIPT_INVALID");
    }

    private static Exception Invalid(string reason) => NativeStatisticCalculationError.Invalid(reason);
}
