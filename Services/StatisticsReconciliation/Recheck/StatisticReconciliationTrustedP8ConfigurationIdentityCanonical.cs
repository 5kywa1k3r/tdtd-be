using MongoDB.Bson;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class
    StatisticReconciliationTrustedP8ConfigurationIdentityCanonical
{
    internal static StatisticReconciliationTrustedP8ConfigurationIdentity Create(
        string ownerId,
        string configId,
        string versionId,
        int versionNo,
        long revision,
        string configHash)
    {
        ownerId = ObjectIdValue(ownerId, "OWNER_ID");
        configId = ObjectIdValue(configId, "CONFIG_ID");
        versionId = ObjectIdValue(versionId, "VERSION_ID");
        if (versionNo < 1)
            throw Invalid("VERSION_NO");
        if (revision < 1)
            throw Invalid("REVISION");
        configHash = Sha(configHash, "CONFIG_HASH");
        var bundleSha256 = StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
            ownerId,
            configId,
            configVersionId = versionId,
            configVersionNo = versionNo,
            configRevision = revision,
            configHash
        });
        var identity = new StatisticReconciliationTrustedP8ConfigurationIdentity
        {
            OwnerId = ownerId,
            ConfigId = configId,
            VersionId = versionId,
            VersionNo = versionNo,
            Revision = revision,
            ConfigHash = configHash,
            BundleSha256 = bundleSha256
        };
        identity.SemanticSha256 = SemanticSha256(identity);
        return identity;
    }

    internal static void RequireValid(
        StatisticReconciliationTrustedP8ConfigurationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!StringComparer.Ordinal.Equals(
                identity.SchemaVersion,
                StatisticReconciliationTrustedP8ConfigurationIdentity
                    .CurrentSchemaVersion))
            throw Invalid("SCHEMA_VERSION");
        var normalized = Create(
            identity.OwnerId,
            identity.ConfigId,
            identity.VersionId,
            identity.VersionNo,
            identity.Revision,
            identity.ConfigHash);
        if (!StringComparer.Ordinal.Equals(
                identity.BundleSha256, normalized.BundleSha256) ||
            !StringComparer.Ordinal.Equals(
                identity.SemanticSha256, normalized.SemanticSha256))
            throw Invalid("SEMANTIC");
    }

    internal static StatisticReconciliationTrustedP8ConfigurationIdentity
        Normalize(StatisticReconciliationTrustedP8ConfigurationIdentity identity)
    {
        RequireValid(identity);
        return Create(
            identity.OwnerId,
            identity.ConfigId,
            identity.VersionId,
            identity.VersionNo,
            identity.Revision,
            identity.ConfigHash);
    }

    internal static bool IsValid(
        StatisticReconciliationTrustedP8ConfigurationIdentity? identity)
    {
        if (identity is null)
            return false;
        try
        {
            RequireValid(identity);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string SemanticSha256(
        StatisticReconciliationTrustedP8ConfigurationIdentity identity)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = identity.SchemaVersion,
            identity.OwnerId,
            identity.ConfigId,
            identity.VersionId,
            identity.VersionNo,
            identity.Revision,
            identity.ConfigHash,
            identity.BundleSha256
        });

    private static string ObjectIdValue(string? value, string path)
    {
        if (!ObjectId.TryParse(value, out var parsed) ||
            !StringComparer.Ordinal.Equals(value, parsed.ToString()))
            throw Invalid(path);
        return value!;
    }

    private static string Sha(string? value, string path)
        => StatisticReconciliationCanonicalJson.IsCanonicalSha256(value)
            ? value!
            : throw Invalid(path);

    private static InvalidOperationException Invalid(string path)
        => new($"TRUSTED_CURRENT_P8_IDENTITY_{path}_INVALID");
}
