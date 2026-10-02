using System.Text.RegularExpressions;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services;

public sealed partial class LabelConfigCommandService
{
    private static readonly Regex P804LabelCodeRegex = new(
        "^[a-z0-9][a-z0-9_.-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static void ValidateTrustedConfigOwner(
        LabelCatalogItem label)
    {
        _ = P804ValidateTrustedLabelOwner(label);
    }

    internal static void
        ValidateP804TrustedActiveTableTarget(
            LabelCatalogItem label)
    {
        _ = P804ValidateTrustedLabelOwner(label);
        if (label.IsDeleted ||
            !label.IsActive ||
            !string.Equals(
                label.Usage,
                LabelUsages.TableTarget,
                StringComparison.Ordinal))
        {
            throw P804LabelIntegrity(
                label,
                "BASIC_SUMMARY_ROW_LABEL_IDENTITY_INVALID");
        }
    }

    internal static void
        ValidateP804TrustedTableTargetSnapshot(
            LabelCatalogItem owner,
            DynamicFormStatisticLabelSnapshotDto snapshot)
        => ValidateTrustedPinnedLabelSnapshot(owner, snapshot, LabelUsages.TableTarget);

    internal static void ValidateTrustedStatisticSnapshot(LabelCatalogItem owner,
        DynamicFormStatisticLabelSnapshotDto snapshot)
        => ValidateTrustedPinnedLabelSnapshot(owner, snapshot, LabelUsages.Statistic);

    private static void ValidateTrustedPinnedLabelSnapshot(LabelCatalogItem owner,
        DynamicFormStatisticLabelSnapshotDto snapshot, string expectedUsage)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var versions = P804ValidateTrustedLabelOwner(owner);
        var matches = versions
            .Where(version =>
                string.Equals(
                    version.LabelId,
                    snapshot.LabelId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    version.VersionId,
                    snapshot.VersionId,
                    StringComparison.Ordinal) &&
                version.VersionNo == snapshot.VersionNo)
            .ToList();
        if (matches.Count != 1)
        {
            throw P804LabelIntegrity(
                owner,
                "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");
        }

        var version = matches[0];
        if (!string.Equals(
                owner.Id,
                snapshot.LabelId,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.Status,
                StatConfigStatuses.Active,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.ConfigHash,
                snapshot.ConfigHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.Code,
                snapshot.Code,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.Usage,
                snapshot.Usage,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.DataType,
                snapshot.DataType,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.ScopeType,
                snapshot.ScopeType,
                StringComparison.Ordinal) ||
            !string.Equals(
                version.ScopeId,
                snapshot.ScopeId,
                StringComparison.Ordinal) ||
            version.IsActive != snapshot.IsActive ||
            !snapshot.IsActive ||
            !string.Equals(
                snapshot.Usage,
                expectedUsage,
                StringComparison.Ordinal))
        {
            throw P804LabelIntegrity(
                owner,
                "BASIC_SUMMARY_ROW_LABEL_HISTORICAL_SNAPSHOT_INVALID");
        }
    }

    private static IReadOnlyList<LabelConfigVersionSnapshot>
        P804ValidateTrustedLabelOwner(
            LabelCatalogItem label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var canonicalPrevious =
            label.VersionNo == 1
                ? label.PreviousVersionId is null
                : P804IsCanonicalLabelObjectId(
                    label.PreviousVersionId);
        if (!P804IsCanonicalLabelObjectId(label.Id) ||
            !P804IsCanonicalLabelObjectId(label.ConfigId) ||
            !string.Equals(
                label.ConfigId,
                label.Id,
                StringComparison.Ordinal) ||
            !P804IsCanonicalLabelObjectId(label.VersionId) ||
            !canonicalPrevious ||
            label.VersionNo < 1 ||
            label.Revision < 1 ||
            !P804IsCanonicalLabelSha256(label.ConfigHash) ||
            !P804IsCanonicalLabelCode(label.Code) ||
            !P804IsCanonicalLabelUsage(label.Usage) ||
            !P804IsCanonicalLabelDataType(label.DataType) ||
            !P804IsCanonicalLabelValueSource(
                label.ValueSourceType) ||
            !P804HasCanonicalLabelScope(
                label.ScopeType,
                label.ScopeId) ||
            label.DependencyPins is null ||
            !P804PinsAreCanonical(label.DependencyPins))
        {
            throw P804LabelIntegrity(
                label,
                "BASIC_SUMMARY_ROW_LABEL_IDENTITY_INVALID");
        }

        var recomputed = ComputeConfigHash(
            label,
            CurrentStatus(label));
        if (!string.Equals(
                label.ConfigHash,
                recomputed,
                StringComparison.Ordinal))
        {
            throw P804LabelIntegrity(
                label,
                "BASIC_SUMMARY_ROW_LABEL_HASH_MISMATCH");
        }

        var versions = (label.VersionSnapshots ?? new())
            .OrderBy(version => version.VersionNo)
            .ToList();
        if (versions.Count != label.VersionNo)
        {
            throw P804LabelIntegrity(
                label,
                "BASIC_SUMMARY_ROW_LABEL_LINEAGE_INVALID");
        }
        for (var index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            var previous = index == 0
                ? null
                : versions[index - 1];
            if (version.VersionNo != index + 1 ||
                version.Revision < 1 ||
                !string.Equals(
                    version.LabelId,
                    label.Id,
                    StringComparison.Ordinal) ||
                !P804IsCanonicalLabelObjectId(
                    version.LabelId) ||
                !P804IsCanonicalLabelObjectId(
                    version.VersionId) ||
                !P804IsCanonicalLabelSha256(
                    version.ConfigHash) ||
                !P804IsCanonicalLabelStatus(version.Status) ||
                !P804IsCanonicalLabelCode(version.Code) ||
                !P804IsCanonicalLabelUsage(version.Usage) ||
                !P804IsCanonicalLabelDataType(
                    version.DataType) ||
                !P804IsCanonicalLabelValueSource(
                    version.ValueSourceType) ||
                !P804HasCanonicalLabelScope(
                    version.ScopeType,
                    version.ScopeId) ||
                version.DependencyPins is null ||
                !P804PinsAreCanonical(version.DependencyPins) ||
                (previous is null
                    ? version.PreviousVersionId is not null
                    : !string.Equals(
                        version.PreviousVersionId,
                        previous.VersionId,
                        StringComparison.Ordinal)))
            {
                throw P804LabelIntegrity(
                    label,
                    "BASIC_SUMMARY_ROW_LABEL_LINEAGE_INVALID");
            }
        }

        var current = versions[^1];
        if (!string.Equals(
                current.VersionId,
                label.VersionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.PreviousVersionId,
                label.PreviousVersionId,
                StringComparison.Ordinal) ||
            current.Revision != label.Revision ||
            !string.Equals(
                current.Status,
                CurrentStatus(label),
                StringComparison.Ordinal) ||
            !string.Equals(
                current.ConfigHash,
                label.ConfigHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.Code,
                label.Code,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.Name,
                label.Name,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.Usage,
                label.Usage,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.DataType,
                label.DataType,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.ValueSourceType,
                label.ValueSourceType,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.ScopeType,
                label.ScopeType,
                StringComparison.Ordinal) ||
            !string.Equals(
                current.ScopeId,
                label.ScopeId,
                StringComparison.Ordinal) ||
            current.IsActive != label.IsActive ||
            !current.DependencyPins.SequenceEqual(
                label.DependencyPins,
                StringComparer.Ordinal))
        {
            throw P804LabelIntegrity(
                label,
                "BASIC_SUMMARY_ROW_LABEL_CURRENT_SNAPSHOT_INVALID");
        }
        return versions;
    }

    private static bool P804IsCanonicalLabelObjectId(
        string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           string.Equals(
               parsed.ToString(),
               value,
               StringComparison.Ordinal);

    private static bool P804IsCanonicalLabelSha256(
        string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               (character >= '0' && character <= '9') ||
               (character >= 'a' && character <= 'f'));

    private static bool P804IsCanonicalLabelCode(string? value)
        => value is not null &&
           P804LabelCodeRegex.IsMatch(value);

    private static bool P804IsCanonicalLabelUsage(string? value)
        => value is LabelUsages.Classification or
            LabelUsages.Statistic or
            LabelUsages.TableTarget;

    private static bool P804IsCanonicalLabelDataType(
        string? value)
        => value is LabelDataTypes.Number or
            LabelDataTypes.ShortText or
            LabelDataTypes.StringList or
            LabelDataTypes.LongText or
            LabelDataTypes.Date or
            LabelDataTypes.Boolean;

    private static bool P804IsCanonicalLabelValueSource(
        string? value)
        => value is LabelValueSourceTypes.None or
            LabelValueSourceTypes.FixedEnum or
            LabelValueSourceTypes.EnumCatalog or
            LabelValueSourceTypes.SystemUnit or
            LabelValueSourceTypes.SystemUser or
            LabelValueSourceTypes.SystemPosition or
            LabelValueSourceTypes.SystemUnitType;

    private static bool P804IsCanonicalLabelStatus(
        string? value)
        => value is StatConfigStatuses.Active or
            StatConfigStatuses.Inactive or
            StatConfigStatuses.Tombstoned;

    private static bool P804HasCanonicalLabelScope(
        string? scopeType,
        string? scopeId)
        => scopeType == LabelScopeTypes.Global
            ? scopeId is null
            : scopeType is (
                LabelScopeTypes.Level or LabelScopeTypes.Unit) &&
              P804IsCanonicalLabelObjectId(scopeId);

    private static bool P804PinsAreCanonical(
        IReadOnlyList<string> pins)
        => pins.SequenceEqual(
            pins
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static AppException P804LabelIntegrity(
        LabelCatalogItem label,
        string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                ownerKind = StatConfigOwnerKinds.Label,
                ownerId = label.Id,
                reason
            });
}
