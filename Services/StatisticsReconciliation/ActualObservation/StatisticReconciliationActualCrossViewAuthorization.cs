using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCrossViewAuthorization
{
    internal const string ExportPolicy = "REQUESTOR_OR_SYSTEM_ADMIN";

    internal static StatisticReconciliationActualCrossViewAuthorizationRelationV1
        Create(
            string workId,
            string scopeAssignmentId,
            StatisticReconciliationActualApiAuthorizationContext? api,
            StatisticReconciliationActualCrossViewExportAuthorizationBinding
                export)
    {
        workId = Required(workId, "CROSS_VIEW_AUTH_WORK_ID");
        scopeAssignmentId = Required(
            scopeAssignmentId,
            "CROSS_VIEW_AUTH_SCOPE_ID");
        ArgumentNullException.ThrowIfNull(export);
        if (!string.Equals(export.WorkId, workId, StringComparison.Ordinal) ||
            !string.Equals(export.ScopeId, scopeAssignmentId,
                StringComparison.Ordinal) ||
            !string.Equals(export.ScopeType, "ASSIGNMENT",
                StringComparison.Ordinal) ||
            !string.Equals(export.AuthorizationPolicy, ExportPolicy,
                StringComparison.Ordinal))
        {
            throw Invalid("CROSS_VIEW_EXPORT_AUTH_TARGET_MISMATCH");
        }

        string? apiSha = null;
        string? apiActor = null;
        ImmutableArray<string> permissions = [];
        long? before = null;
        long? after = null;
        if (api is not null)
        {
            if (!api.IsAuthorized ||
                !string.Equals(api.WorkId, workId, StringComparison.Ordinal) ||
                !string.Equals(api.ScopeAssignmentId, scopeAssignmentId,
                    StringComparison.Ordinal) ||
                api.PermissionCodes.IsDefaultOrEmpty ||
                api.RowCountBeforeRedaction < 0 ||
                api.RowCountAfterRedaction < 0 ||
                api.RowCountAfterRedaction > api.RowCountBeforeRedaction)
            {
                throw Invalid("CROSS_VIEW_API_AUTH_INVALID");
            }
            permissions = api.PermissionCodes;
            var ordered = permissions.OrderBy(value => value,
                StringComparer.Ordinal).ToImmutableArray();
            if (!permissions.SequenceEqual(ordered, StringComparer.Ordinal) ||
                permissions.Distinct(StringComparer.Ordinal).Count() !=
                    permissions.Length)
                throw Invalid("CROSS_VIEW_API_AUTH_PERMISSIONS_INVALID");
            apiActor = Required(api.ActorUserId, "CROSS_VIEW_API_AUTH_ACTOR");
            before = api.RowCountBeforeRedaction;
            after = api.RowCountAfterRedaction;
            apiSha = StatisticReconciliationActualApiObservationAdapter
                .AuthorizationSha(
                    apiActor,
                    workId,
                    scopeAssignmentId,
                    permissions,
                    before.Value,
                    after.Value);
            if (!string.Equals(apiSha, api.AuthorizationSnapshotSha256,
                    StringComparison.Ordinal))
                throw Invalid("CROSS_VIEW_API_AUTH_DIGEST_MISMATCH");
        }

        var semantic = Semantic(
            workId,
            scopeAssignmentId,
            apiSha,
            export.AuthorizationSnapshotSha256,
            apiActor,
            permissions,
            before,
            after,
            export.RequestedByUserId,
            export.AuthorizationPolicy,
            export.CapabilityId,
            export.CommandId,
            export.ReceiptId,
            export.OwnerSemanticSha256);
        return new(
            StatisticReconciliationActualCrossViewParityV3Schemas
                .AuthorizationRelation,
            workId,
            scopeAssignmentId,
            apiSha,
            export.AuthorizationSnapshotSha256,
            apiActor,
            permissions,
            before,
            after,
            export.RequestedByUserId,
            export.AuthorizationPolicy,
            export.CapabilityId,
            export.CommandId,
            export.ReceiptId,
            export.OwnerSemanticSha256,
            semantic);
    }

    internal static void RequireValid(
        StatisticReconciliationActualCrossViewAuthorizationRelationV1 value,
        bool apiRequired)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.SchemaVersion,
                StatisticReconciliationActualCrossViewParityV3Schemas
                    .AuthorizationRelation,
                StringComparison.Ordinal) ||
            apiRequired != (value.ApiAuthorizationSnapshotSha256 is not null) ||
            apiRequired != (value.ApiActorUserId is not null) ||
            apiRequired != value.ApiRowCountBeforeRedaction.HasValue ||
            apiRequired != value.ApiRowCountAfterRedaction.HasValue ||
            apiRequired != !value.ApiPermissionCodes.IsDefaultOrEmpty)
            throw Invalid("CROSS_VIEW_AUTH_RELATION_SHAPE_INVALID");
        var semantic = Semantic(
            value.WorkId,
            value.ScopeAssignmentId,
            value.ApiAuthorizationSnapshotSha256,
            value.ExportAuthorizationSnapshotSha256,
            value.ApiActorUserId,
            value.ApiPermissionCodes.IsDefault
                ? []
                : value.ApiPermissionCodes,
            value.ApiRowCountBeforeRedaction,
            value.ApiRowCountAfterRedaction,
            value.ExportRequestedByUserId,
            value.ExportAuthorizationPolicy,
            value.ExportCapabilityId,
            value.ExportCommandId,
            value.ExportReceiptId,
            value.ExportOwnerSemanticSha256);
        if (!string.Equals(semantic, value.SemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("CROSS_VIEW_AUTH_RELATION_DIGEST_MISMATCH");
    }

    private static string Semantic(
        string workId,
        string scopeAssignmentId,
        string? apiAuthorizationSha,
        string exportAuthorizationSha,
        string? apiActor,
        ImmutableArray<string> permissions,
        long? before,
        long? after,
        string exportActor,
        string policy,
        string capability,
        string command,
        string receipt,
        string exportOwnerSha)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_CROSS_VIEW_AUTHORIZATION_RELATION_V1",
            Required(workId, "CROSS_VIEW_AUTH_WORK_ID"),
            Required(scopeAssignmentId, "CROSS_VIEW_AUTH_SCOPE_ID"),
            apiAuthorizationSha is null
                ? "API_NOT_APPLICABLE"
                : Sha(apiAuthorizationSha, "CROSS_VIEW_API_AUTH_SHA"),
            Sha(exportAuthorizationSha, "CROSS_VIEW_EXPORT_AUTH_SHA"),
            apiActor ?? "API_NOT_APPLICABLE",
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_CROSS_VIEW_API_PERMISSION_CODES_V1",
                permissions),
            before.HasValue
                ? StatisticReconciliationActualCanonical.Integer(before.Value)
                : "API_NOT_APPLICABLE",
            after.HasValue
                ? StatisticReconciliationActualCanonical.Integer(after.Value)
                : "API_NOT_APPLICABLE",
            Required(exportActor, "CROSS_VIEW_EXPORT_ACTOR"),
            Required(policy, "CROSS_VIEW_EXPORT_POLICY"),
            Required(capability, "CROSS_VIEW_EXPORT_CAPABILITY"),
            Required(command, "CROSS_VIEW_EXPORT_COMMAND"),
            Sha(receipt, "CROSS_VIEW_EXPORT_RECEIPT"),
            Sha(exportOwnerSha, "CROSS_VIEW_EXPORT_OWNER_SHA"));

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
