using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicForms;

public sealed partial class DynamicFormStatisticConfigCommandService
{
    // Deliberately separate from P804TrustedPersistedView: a caller cannot
    // accidentally accept this view while dropping its native section.
    internal sealed record NativeStatisticInputView(
        string ConfigId,
        string VersionId,
        int VersionNo,
        long Revision,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        string FieldSectionJson,
        string TableSectionJson,
        string NativeTargetSectionJson,
        IReadOnlyList<DynamicFormNativeStatisticConfigDto> NativeTargets,
        string? NativePlanSectionJson,
        DynamicFormNativePlanConfigDto? NativePlan);

    internal static NativeStatisticInputView ReadNativeStatisticInputView(
        DynamicFormTemplate owner,
        string configId, string versionId, int versionNo, long revision, string configHash)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsPublished)
            throw IntegrityConflict(owner.Id, "NATIVE_INPUT_PUBLISHED_OWNER_REQUIRED");
        _ = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(owner);
        var state = ValidateTrustedPersistedState(owner, nativeIntake: true);
        if (state is null || state.Status != "LOCKED" || state.NativeTargetSectionJson is null)
            throw IntegrityConflict(owner.Id, "NATIVE_INPUT_LOCKED_CONFIG_REQUIRED");
        var snapshots = owner.StatisticConfigSnapshots!;
        if (snapshots.Select(snapshot => snapshot.VersionId).Distinct(StringComparer.Ordinal).Count() != snapshots.Count
            || snapshots.Select(snapshot => snapshot.VersionNo).Distinct().Count() != snapshots.Count
            || snapshots.Select(snapshot => snapshot.Revision).Distinct().Count() != snapshots.Count)
            throw IntegrityConflict(owner.Id, "NATIVE_INPUT_SNAPSHOT_IDENTITY_AMBIGUOUS");
        if (state.VersionNo == 1 ? state.PreviousVersionId is not null
            : snapshots.Count(snapshot => snapshot.VersionNo == state.VersionNo - 1
                && snapshot.VersionId == state.PreviousVersionId && snapshot.Revision < state.Revision) != 1)
            throw IntegrityConflict(owner.Id, "NATIVE_INPUT_SNAPSHOT_LINEAGE");

        // Staging consumes exactly the captured current configuration. A newer
        // owner must cause the caller to retry its capture, never rebase a run.
        // Historical result reads remain behind the existing P804 guard.
        if (state.ConfigId != configId || state.VersionId != versionId
            || state.VersionNo != versionNo || state.Revision != revision || state.ConfigHash != configHash)
            throw IntegrityConflict(owner.Id, "NATIVE_INPUT_CONFIG_PIN_MISMATCH");
        return new NativeStatisticInputView(state.ConfigId, state.VersionId, state.VersionNo,
            state.Revision, state.ConfigHash, state.DependencyPins.ToArray(), state.FieldSectionJson,
            state.TableSectionJson, state.NativeTargetSectionJson,
            DeserializeNativeSection(state.NativeTargetSectionJson)!.ToArray(),
            state.NativePlanSectionJson, DeserializeNativePlanSection(state.NativePlanSectionJson));
    }
}
