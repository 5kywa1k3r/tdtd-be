using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCrossViewV2OwnerSchemas
{
    internal const string Command =
        "P10_ACTUAL_CROSS_VIEW_V2_OWNER_COMMAND_V1";
    internal const string Resolution =
        "P10_ACTUAL_CROSS_VIEW_V2_OWNER_RESOLUTION_V1";
}

internal static class StatisticReconciliationActualCrossViewV2OwnerStates
{
    internal const string Complete = "COMPLETE";
    internal const string Incomplete = "INCOMPLETE";
}

internal static class StatisticReconciliationActualCrossViewV2OwnerFailures
{
    internal const string None = "NONE";
    internal const string InputRequired = "CROSS_VIEW_OWNER_INPUT_REQUIRED";
    internal const string SchemaUnsupported =
        "CROSS_VIEW_OWNER_SCHEMA_UNSUPPORTED";
    internal const string FamilyUnsupported =
        "CROSS_VIEW_OWNER_FAMILY_UNSUPPORTED";
    internal const string AuthorizationRelationUnavailable =
        "CROSS_VIEW_OWNER_AUTHORIZATION_RELATION_UNAVAILABLE";
    internal const string AdvancedCaptureRequired =
        "CROSS_VIEW_OWNER_ADVANCED_CAPTURE_REQUIRED";
    internal const string AdvancedOwnerUnavailable =
        "CROSS_VIEW_OWNER_ADVANCED_OWNER_UNAVAILABLE";
    internal const string AdvancedOwnerDrift =
        "CROSS_VIEW_OWNER_ADVANCED_OWNER_DRIFT";
    internal const string ExportCaptureRequired =
        "CROSS_VIEW_OWNER_EXPORT_CAPTURE_REQUIRED";
    internal const string ExportOwnerUnavailable =
        "CROSS_VIEW_OWNER_EXPORT_OWNER_UNAVAILABLE";
    internal const string ExportOwnerDrift =
        "CROSS_VIEW_OWNER_EXPORT_OWNER_DRIFT";
    internal const string ExportProjectionInvalid =
        "CROSS_VIEW_OWNER_EXPORT_PROJECTION_INVALID";
    internal const string ProofIncomplete =
        "CROSS_VIEW_OWNER_PROOF_INCOMPLETE";
    internal const string OwnerReadInvalid =
        "CROSS_VIEW_OWNER_READ_INVALID";
}

internal static class
    StatisticReconciliationActualCrossViewV2RequiredPersistence
{
    // V2 has one authorization digest, while the API and export owners use
    // intentionally different canonical domains. A later signable schema must
    // persist both preimages and a server-produced relation, not equate their
    // opaque hashes.
    internal static readonly ImmutableArray<string> AuthorizationRelation =
    [
        "crossViewPlan.apiAuthorizationSnapshotSha256",
        "crossViewPlan.exportAuthorizationSnapshotSha256",
        "crossViewPlan.authorizationRelationCanonicalJson",
        "crossViewPlan.authorizationRelationSha256"
    ];

    internal static readonly ImmutableArray<string> AdvancedCapture =
    [
        "finalCaptures.advancedCapture"
    ];

    internal static readonly ImmutableArray<string> ExportCapture =
    [
        "finalCaptures.exportCapture"
    ];

    internal static readonly ImmutableArray<string> AdvancedOwner =
    [
        "advancedOwner.exactNodeValueJson",
        "advancedOwner.valueHash",
        "advancedOwner.sourceSignatureHash"
    ];

    internal static readonly ImmutableArray<string> ExportOwner =
    [
        "exportArtifact.canonicalFilterJson",
        "exportArtifact.periodInstanceKey",
        "exportArtifact.columnManifest",
        "exportArtifact.content"
    ];
}

/// <summary>
/// All values are server-owned capture outputs. The owner never accepts a
/// client semantic row, total, cell, filter or result binding.
/// </summary>
internal sealed record StatisticReconciliationActualCrossViewV2OwnerCommand(
    string SchemaVersion,
    StatisticReconciliationActualTrustedCaptureMaterial Material,
    ActualSourceMembershipCapture? Source,
    ActualDirectProjectionCapture? Direct,
    ActualAggregateCapture? Aggregate,
    ActualBasicResultObservation? Basic,
    ActualAdvancedCapture? Advanced,
    ActualP9DiffCapture? Diff,
    StatisticReconciliationActualApiCapture? Api,
    StatisticReconciliationActualExportCapture? Export);

internal sealed record StatisticReconciliationActualCrossViewV2OwnerResolution(
    string SchemaVersion,
    string State,
    string FailureCode,
    ImmutableArray<string> RequiredPersistenceFields,
    StatisticReconciliationActualCrossViewParityV2Plan? Plan,
    StatisticReconciliationActualCrossViewParityV2Base? Base,
    StatisticReconciliationActualCrossViewParityV2Actual? Actual,
    string ResolutionSha256);

internal interface IStatisticReconciliationActualCrossViewV2Owner
{
    Task<StatisticReconciliationActualCrossViewV2OwnerResolution> ResolveAsync(
        StatisticReconciliationActualCrossViewV2OwnerCommand command,
        CancellationToken cancellationToken = default);
}
