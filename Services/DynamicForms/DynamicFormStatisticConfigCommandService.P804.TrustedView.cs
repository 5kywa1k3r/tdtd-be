using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

public sealed partial class DynamicFormStatisticConfigCommandService
{
    internal sealed record P804TrustedPersistedView(
        string ConfigId,
        string VersionId,
        string? PreviousVersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        string FieldSectionJson,
        string TableSectionJson);

    private sealed record TrustedPersistedState(
        string ConfigId,
        string VersionId,
        string? PreviousVersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        string FieldSectionJson,
        string TableSectionJson,
        IReadOnlyList<PersistedFieldConfig> Fields,
        IReadOnlyList<PersistedTableConfig> Tables)
    {
        internal string? NativeTargetSectionJson { get; init; }
        internal string? NativePlanSectionJson { get; init; }
    }

    internal static P804TrustedPersistedView?
        GetP804TrustedPersistedView(
            DynamicFormTemplate owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.IsPublished)
        {
            _ = DynamicFormPublishedSchemaSnapshotBuilder
                .ValidateAgainstTemplate(owner);
        }

        var trusted = ValidateTrustedPersistedState(owner);
        return trusted is null
            ? null
            : new P804TrustedPersistedView(
                trusted.ConfigId,
                trusted.VersionId,
                trusted.PreviousVersionId,
                trusted.VersionNo,
                trusted.Revision,
                trusted.Status,
                trusted.ConfigHash,
                trusted.DependencyPins,
                trusted.FieldSectionJson,
                trusted.TableSectionJson);
    }

    /// <summary>
    /// Resolves one exact persisted statistic configuration version. P9 and
    /// reconciliation generations retain immutable P8 pins while the owning
    /// Dynamic Form document may legitimately advance to a newer version.
    /// </summary>
    internal static P804TrustedPersistedView?
        GetP804TrustedPersistedView(
            DynamicFormTemplate owner,
            string? configId,
            string? versionId,
            int? versionNo,
            long? revision,
            string? configHash)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.IsPublished)
        {
            _ = DynamicFormPublishedSchemaSnapshotBuilder
                .ValidateAgainstTemplate(owner);
        }

        // Validate the authoritative current state before trusting any history
        // embedded in the same owner document.
        var current = ValidateTrustedPersistedState(owner);
        if (current is null ||
            !string.Equals(current.ConfigId, configId,
                StringComparison.Ordinal) ||
            !IsCanonicalObjectId(versionId) ||
            versionNo is null or < 1 ||
            revision is null or < 1 ||
            !IsCanonicalSha256(configHash))
        {
            return null;
        }

        if (string.Equals(current.VersionId, versionId,
                StringComparison.Ordinal) &&
            current.VersionNo == versionNo &&
            current.Revision == revision &&
            string.Equals(current.ConfigHash, configHash,
                StringComparison.Ordinal))
        {
            return new P804TrustedPersistedView(
                current.ConfigId,
                current.VersionId,
                current.PreviousVersionId,
                current.VersionNo,
                current.Revision,
                current.Status,
                current.ConfigHash,
                current.DependencyPins,
                current.FieldSectionJson,
                current.TableSectionJson);
        }

        var snapshots = owner.StatisticConfigSnapshots ?? new();
        // Each identity dimension is append-only and unique. A collision on
        // version id, sequence number, or revision makes historical selection
        // ambiguous and therefore unavailable.
        var identityMatches = snapshots.Where(snapshot =>
                string.Equals(snapshot.VersionId, versionId,
                    StringComparison.Ordinal) ||
                snapshot.VersionNo == versionNo ||
                snapshot.Revision == revision)
            .Take(2)
            .ToArray();
        if (identityMatches.Length != 1)
            return null;

        var snapshot = identityMatches[0];
        if (!string.Equals(snapshot.VersionId, versionId,
                StringComparison.Ordinal) ||
            snapshot.VersionNo != versionNo ||
            snapshot.Revision != revision ||
            !string.Equals(snapshot.ConfigHash, configHash,
                StringComparison.Ordinal) ||
            !IsOptionalCanonicalObjectId(snapshot.PreviousVersionId))
        {
            return null;
        }

        if (snapshot.VersionNo == 1)
        {
            if (snapshot.PreviousVersionId is not null)
                throw IntegrityConflict(owner.Id, "SNAPSHOT_LINEAGE");
        }
        else
        {
            var predecessors = snapshots.Where(candidate =>
                    candidate.VersionNo == snapshot.VersionNo - 1 &&
                    string.Equals(candidate.VersionId,
                        snapshot.PreviousVersionId,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (predecessors.Length != 1)
                throw IntegrityConflict(owner.Id, "SNAPSHOT_LINEAGE");
        }

        var sections = snapshot.Sections ??
                       new DynamicFormStatisticConfigSections();
        var fieldSectionJson = CanonicalSectionJson(
            sections.FieldSectionJson,
            "$.historicalSnapshot.fieldConfig");
        var tableSectionJson = CanonicalSectionJson(
            sections.TableSectionJson,
            "$.historicalSnapshot.tableConfig");
        var fields = DeserializeFieldSection(fieldSectionJson);
        var tables = DeserializeTableSection(tableSectionJson);
        var schemaFields = BuildSchemaFieldMap(
            ParseFieldArray(owner.FieldsJson));
        ValidatePersistedFieldStructure(fields, schemaFields, owner.Id);
        ValidatePersistedTableStructure(tables, owner);
        EnsureUniqueStatisticLabelTargets(fields, tables);
        var dependencyPins = BuildDependencyPins(fields, tables);
        if (!(snapshot.DependencyPins ?? new List<string>()).SequenceEqual(
                dependencyPins,
                StringComparer.Ordinal))
        {
            throw IntegrityConflict(owner.Id,
                "HISTORICAL_SNAPSHOT_DEPENDENCY_PINS");
        }

        var recomputed = ComputeConfigHash(
            owner.Id,
            fieldSectionJson,
            tableSectionJson,
            dependencyPins);
        if (!string.Equals(snapshot.ConfigHash, recomputed,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(owner.Id,
                "HISTORICAL_SNAPSHOT_CONFIG_HASH");
        }

        return new P804TrustedPersistedView(
            current.ConfigId,
            snapshot.VersionId,
            snapshot.PreviousVersionId,
            snapshot.VersionNo,
            snapshot.Revision,
            snapshot.Status,
            recomputed,
            dependencyPins,
            fieldSectionJson,
            tableSectionJson);
    }

    internal static FilterDefinition<LabelCatalogItem>
        GetP804LabelVisibilityFilter(MeResponse me)
        => BuildLabelVisibilityFilter(me);

    private static TrustedPersistedState?
        ValidateTrustedPersistedState(
            DynamicFormTemplate owner,
            bool nativeIntake = false)
    {
        // Existing P804 callers remain legacy-only. The separate L5 intake
        // must explicitly request and retain the native section and its pins.
        if (!nativeIntake)
            DynamicFormNativeTableDefinition.RequireLegacyConsumer(owner, "statisticProjection");
        if (nativeIntake && !DynamicFormNativeTableDefinition.IsNative(owner))
            throw IntegrityConflict(owner.Id, "NATIVE_OWNER_REQUIRED");
        if (!nativeIntake && (owner.StatisticConfigSections?.NativePlanSectionJson is not null
            || DynamicFormNativeTableDefinition.IsNative(owner)
            && DynamicFormNativeStatisticState.HasPlan(DynamicFormNativeTableDefinition.ReadStored(owner.NativeTablesVersion, owner.TablesJson)!)))
            throw IntegrityConflict(owner.Id, "NATIVE_PLAN_V2_CONSUMER_REQUIRES_L5C");
        if (!nativeIntake && owner.StatisticConfigSections?.NativeTargetSectionJson is not null)
            throw IntegrityConflict(owner.Id, "NATIVE_SECTION_WITHOUT_SCHEMA");
        if (!HasStatisticConfigFootprint(owner))
            return null;

        if (!IsCanonicalObjectId(owner.StatisticConfigId) ||
            !IsCanonicalObjectId(
                owner.StatisticConfigVersionId) ||
            !IsOptionalCanonicalObjectId(
                owner.StatisticConfigPreviousVersionId) ||
            owner.StatisticConfigVersionNo < 1 ||
            owner.StatisticConfigRevision < 1 ||
            !IsCanonicalSha256(owner.StatisticConfigHash))
        {
            throw IntegrityConflict(owner.Id, "CONFIG_IDENTITY");
        }

        var expectedStatus = CurrentStatus(owner);
        if (!string.Equals(
                owner.StatisticConfigStatus,
                expectedStatus,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "CONFIG_STATUS");
        }

        var currentSnapshots =
            (owner.StatisticConfigSnapshots ?? new())
            .Where(snapshot =>
                string.Equals(
                    snapshot.VersionId,
                    owner.StatisticConfigVersionId,
                    StringComparison.Ordinal) &&
                snapshot.VersionNo ==
                owner.StatisticConfigVersionNo)
            .ToList();
        if (currentSnapshots.Count != 1)
        {
            throw IntegrityConflict(owner.Id, "CURRENT_SNAPSHOT");
        }

        var currentSnapshot = currentSnapshots[0];
        if (!IsCanonicalObjectId(currentSnapshot.VersionId) ||
            !IsOptionalCanonicalObjectId(
                currentSnapshot.PreviousVersionId) ||
            !string.Equals(
                currentSnapshot.PreviousVersionId,
                owner.StatisticConfigPreviousVersionId,
                StringComparison.Ordinal) ||
            currentSnapshot.Revision !=
            owner.StatisticConfigRevision ||
            !string.Equals(
                currentSnapshot.ConfigHash,
                owner.StatisticConfigHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                currentSnapshot.Status,
                owner.StatisticConfigStatus,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "CURRENT_SNAPSHOT");
        }

        var sections = owner.StatisticConfigSections ??
                       new DynamicFormStatisticConfigSections();
        var fieldSectionJson = CanonicalSectionJson(
            sections.FieldSectionJson,
            "$.fieldConfig");
        var tableSectionJson = CanonicalSectionJson(
            sections.TableSectionJson,
            "$.tableConfig");
        var snapshotSections = currentSnapshot.Sections ??
                               new DynamicFormStatisticConfigSections();
        var snapshotFieldSectionJson = CanonicalSectionJson(
            snapshotSections.FieldSectionJson,
            "$.currentSnapshot.fieldConfig");
        var snapshotTableSectionJson = CanonicalSectionJson(
            snapshotSections.TableSectionJson,
            "$.currentSnapshot.tableConfig");
        string? nativeSectionJson = null;
        string? nativePlanSectionJson = null;
        if (nativeIntake)
        {
            if (sections.NativeTargetSectionJson is not string nativeJson
                || snapshotSections.NativeTargetSectionJson is not string snapshotNativeJson)
                throw IntegrityConflict(owner.Id, "NATIVE_SECTION_REQUIRED");
            nativeSectionJson = ValidateNativeSection(owner, NativeTables(owner), DeserializeNativeSection(nativeJson)!);
            if (StatConfigCanonicalJson.Canonicalize(ParseElement(snapshotNativeJson)) != nativeSectionJson)
                throw IntegrityConflict(owner.Id, "NATIVE_CURRENT_SNAPSHOT");
            var nativeTables = NativeTables(owner);
            if (DynamicFormNativeStatisticState.HasPlan(nativeTables))
            {
                if (sections.NativePlanSectionJson is not string planJson
                    || snapshotSections.NativePlanSectionJson is not string snapshotPlanJson)
                    throw IntegrityConflict(owner.Id, "NATIVE_PLAN_SECTION_REQUIRED");
                nativePlanSectionJson = ValidateNativePlanSection(owner, nativeTables, DeserializeNativePlanSection(planJson)!);
                if (StatConfigCanonicalJson.Canonicalize(ParseElement(snapshotPlanJson)) != nativePlanSectionJson)
                    throw IntegrityConflict(owner.Id, "NATIVE_PLAN_CURRENT_SNAPSHOT");
            }
            else if (sections.NativePlanSectionJson is not null || snapshotSections.NativePlanSectionJson is not null)
                throw IntegrityConflict(owner.Id, "NATIVE_PLAN_WITHOUT_DEFINITION");
        }
        if (!string.Equals(
                snapshotFieldSectionJson,
                fieldSectionJson,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshotTableSectionJson,
                tableSectionJson,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(
                owner.Id,
                "CURRENT_SNAPSHOT_SECTIONS");
        }

        var fields = DeserializeFieldSection(fieldSectionJson);
        var tables = DeserializeTableSection(tableSectionJson);
        var fieldArray = ParseFieldArray(owner.FieldsJson);
        var schemaFields = BuildSchemaFieldMap(fieldArray);
        ValidatePersistedFieldStructure(
            fields,
            schemaFields,
            owner.Id);
        ValidatePersistedTableStructure(tables, owner);
        EnsureUniqueStatisticLabelTargets(fields, tables);
        ValidateNativeTargetLimits(fields, tables, nativeSectionJson, nativePlanSectionJson);
        var dependencyPins = nativeSectionJson is null
            ? BuildDependencyPins(fields, tables)
            : NativeDependencyPins(BuildDependencyPins(fields, tables), nativeSectionJson, nativePlanSectionJson);
        if (!(owner.StatisticConfigDependencyPins ??
              new List<string>()).SequenceEqual(
                dependencyPins,
                StringComparer.Ordinal) ||
            !(currentSnapshot.DependencyPins ??
              new List<string>()).SequenceEqual(
                dependencyPins,
                StringComparer.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "DEPENDENCY_PINS");
        }

        var recomputed = ComputeConfigHash(
            owner.Id,
            fieldSectionJson,
            tableSectionJson,
            dependencyPins,
            nativeSectionJson, nativePlanSectionJson);
        if (!string.Equals(
                owner.StatisticConfigHash,
                recomputed,
                StringComparison.Ordinal))
        {
            throw IntegrityConflict(owner.Id, "CONFIG_HASH");
        }

        return new TrustedPersistedState(
            owner.StatisticConfigId!,
            owner.StatisticConfigVersionId!,
            owner.StatisticConfigPreviousVersionId,
            owner.StatisticConfigVersionNo,
            owner.StatisticConfigRevision,
            owner.StatisticConfigStatus!,
            recomputed,
            dependencyPins,
            fieldSectionJson,
            tableSectionJson,
            fields,
            tables)
        {
            NativeTargetSectionJson = nativeSectionJson,
            NativePlanSectionJson = nativePlanSectionJson
        };
    }

    private static bool HasStatisticConfigFootprint(
        DynamicFormTemplate owner)
        => !string.IsNullOrWhiteSpace(
               owner.StatisticConfigId) ||
           !string.IsNullOrWhiteSpace(
               owner.StatisticConfigVersionId) ||
           !string.IsNullOrWhiteSpace(
               owner.StatisticConfigPreviousVersionId) ||
           owner.StatisticConfigVersionNo != 0 ||
           owner.StatisticConfigRevision != 0 ||
           !string.IsNullOrWhiteSpace(
               owner.StatisticConfigStatus) ||
           !string.IsNullOrWhiteSpace(
               owner.StatisticConfigHash) ||
           (owner.StatisticConfigDependencyPins?.Count ?? 0) != 0 ||
           (owner.StatisticConfigSnapshots?.Count ?? 0) != 0 ||
           HasNonDefaultSection(
               owner.StatisticConfigSections?.FieldSectionJson) ||
           HasNonDefaultSection(
               owner.StatisticConfigSections?.TableSectionJson);

    private static bool HasNonDefaultSection(string? json)
        => !string.IsNullOrWhiteSpace(json) &&
           !string.Equals(
               json.Trim(),
               "[]",
               StringComparison.Ordinal);

    private static bool IsCanonicalObjectId(string? value)
        => ObjectId.TryParse(value, out var parsed) &&
           string.Equals(
               parsed.ToString(),
               value,
               StringComparison.Ordinal);

    private static bool IsOptionalCanonicalObjectId(
        string? value)
        => value is null || IsCanonicalObjectId(value);

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' or
                   >= 'a' and <= 'f');
}
