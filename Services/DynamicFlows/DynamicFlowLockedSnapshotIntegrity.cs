using System.Text.Json;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.DynamicFlows;

/// <summary>
/// Verifies immutable Flow snapshots before a locked payload is exposed to a
/// reader, diff engine, or runtime gate. Stored hashes and provenance are
/// evidence to verify, never trusted inputs.
/// </summary>
internal static class DynamicFlowLockedSnapshotIntegrity
{
    internal const string FailureReason = "DYNAMIC_FLOW_LOCKED_SNAPSHOT_INTEGRITY_FAILED";

    internal static IReadOnlySet<string> CollectReferencedFormIds(
        IEnumerable<DynamicFlowTemplateVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions.Where(IsLocked))
        {
            try
            {
                var canonical = Canonicalize(version);
                foreach (var formNode in canonical.Payload.FormNodes)
                {
                    if (!string.IsNullOrWhiteSpace(formNode.DynamicFormTemplateId))
                        ids.Add(formNode.DynamicFormTemplateId);
                }
            }
            catch (Exception error) when (IsIntegrityFailure(error))
            {
                throw Failure(error);
            }
        }

        return ids;
    }

    internal static void Validate(
        DynamicFlowTemplate family,
        IEnumerable<DynamicFlowTemplateVersion> versions,
        IReadOnlyDictionary<string, DynamicFormTemplate> forms)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(forms);

        try
        {
            foreach (var version in versions.Where(IsLocked))
                ValidateCore(family, version, forms);
        }
        catch (Exception error) when (IsIntegrityFailure(error))
        {
            throw Failure(error);
        }
    }

    private static void ValidateCore(
        DynamicFlowTemplate family,
        DynamicFlowTemplateVersion version,
        IReadOnlyDictionary<string, DynamicFormTemplate> forms)
    {
        if (!string.Equals(version.TemplateId, family.Id, StringComparison.Ordinal) ||
            version.SchemaVersion != DynamicFlowDefinitionSchema.CurrentVersion ||
            version.AdapterVersion != DynamicFlowDefinitionSchema.CurrentAdapterVersion ||
            !IsKnownCatalogPin(version) ||
            !string.Equals(
                version.MigrationState,
                DynamicFlowDefinitionMigrationStates.Canonical,
                StringComparison.Ordinal) ||
            !version.DefinitionLockable)
        {
            throw new InvalidOperationException("Locked Dynamic Flow metadata is not canonical.");
        }

        var canonical = Canonicalize(version);
        if (!string.Equals(
                canonical.Payload.CatalogVersion,
                version.CatalogVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                canonical.Payload.CatalogSemanticHash,
                version.CatalogSemanticHash,
                StringComparison.Ordinal) ||
            !string.Equals(canonical.CanonicalJson, version.PayloadJson, StringComparison.Ordinal) ||
            !string.Equals(canonical.PayloadHash, version.PayloadHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Locked Dynamic Flow payload bytes, hash, or catalog pins differ from canonical data.");
        }

        var familyRootFormId = Normalize(family.RootDynamicFormTemplateId);
        var versionRootFormId = Normalize(version.RootDynamicFormTemplateId);
        var payloadRootFormId = Normalize(canonical.Payload.RootDynamicFormTemplateId);
        if (familyRootFormId is null ||
            versionRootFormId is null ||
            payloadRootFormId is null ||
            !string.Equals(familyRootFormId, versionRootFormId, StringComparison.Ordinal) ||
            !string.Equals(versionRootFormId, payloadRootFormId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Locked Dynamic Flow root Form provenance is inconsistent.");
        }

        var rootDeclared = false;
        foreach (var formNode in canonical.Payload.FormNodes)
        {
            if (!forms.TryGetValue(formNode.DynamicFormTemplateId, out var form) ||
                form.IsDeleted ||
                !form.IsActive ||
                !form.IsPublished)
            {
                throw new InvalidOperationException("A locked Dynamic Flow referenced Form is unavailable.");
            }

            var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
            var expectedFamilyId = Normalize(form.FamilyId) ?? form.Id;
            if (!string.Equals(formNode.DynamicFormFamilyId, expectedFamilyId, StringComparison.Ordinal) ||
                formNode.DynamicFormVersionNo != Math.Max(1, form.VersionNo) ||
                !string.Equals(formNode.DynamicFormSchemaHash, snapshot.Sha256, StringComparison.Ordinal) ||
                !string.Equals(formNode.DynamicFormSnapshotHash, snapshot.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Locked Dynamic Flow Form pins differ from the published snapshot.");
            }

            rootDeclared |= string.Equals(
                formNode.DynamicFormTemplateId,
                versionRootFormId,
                StringComparison.Ordinal);
        }

        if (!rootDeclared)
            throw new InvalidOperationException("Locked Dynamic Flow root Form node is missing.");

        if (string.Equals(family.CurrentVersionId, version.Id, StringComparison.Ordinal) &&
            (!family.HasLockedVersion ||
             family.CurrentVersionNo != version.VersionNo ||
             !string.Equals(family.CurrentVersionHash, version.PayloadHash, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Dynamic Flow family current-version pins are inconsistent.");
        }
    }

    private static DynamicFlowCanonicalPayload Canonicalize(DynamicFlowTemplateVersion version)
        => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            version.PayloadJson,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));

    private static bool IsLocked(DynamicFlowTemplateVersion version)
        => string.Equals(
            version.Status,
            DynamicFlowTemplateVersionStatuses.Locked,
            StringComparison.Ordinal);

    private static bool IsKnownCatalogPin(DynamicFlowTemplateVersion version)
        => DynamicFlowStoredCatalogPinPolicy.IsKnownStored(
            version.CatalogVersion,
            version.CatalogSemanticHash);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsIntegrityFailure(Exception error)
        => error is AppException or
           InvalidOperationException or
           JsonException or
           ArgumentException or
           NotSupportedException;

    private static AppException Failure(Exception error)
        => new(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new { reason = FailureReason },
            innerException: error);
}
