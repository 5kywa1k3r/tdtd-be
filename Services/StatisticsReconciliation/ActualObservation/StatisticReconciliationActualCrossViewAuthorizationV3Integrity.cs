using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class
    StatisticReconciliationActualCrossViewAuthorizationV3Integrity
{
    internal static void RequireRelation(
        StatisticReconciliationActualCrossViewAuthorizationRelationV1 value,
        bool apiRequired)
    {
        StatisticReconciliationActualCrossViewAuthorization.RequireValid(
            value,
            apiRequired);
        if (!string.Equals(
                value.ExportAuthorizationPolicy,
                StatisticReconciliationActualCrossViewAuthorization
                    .ExportPolicy,
                StringComparison.Ordinal))
            throw Invalid("CROSS_VIEW_EXPORT_AUTH_POLICY_INVALID");

        if (!apiRequired)
            return;
        var permissions = value.ApiPermissionCodes;
        if (permissions.IsDefaultOrEmpty ||
            !permissions.SequenceEqual(
                permissions.OrderBy(item => item, StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            permissions.Distinct(StringComparer.Ordinal).Count() !=
                permissions.Length ||
            value.ApiRowCountBeforeRedaction is null or < 0 ||
            value.ApiRowCountAfterRedaction is null or < 0 ||
            value.ApiRowCountAfterRedaction >
                value.ApiRowCountBeforeRedaction)
            throw Invalid("CROSS_VIEW_API_AUTH_RELATION_INVALID");

        var apiSha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                Required(value.ApiActorUserId,
                    "CROSS_VIEW_API_AUTH_ACTOR"),
                Required(value.WorkId, "CROSS_VIEW_API_AUTH_WORK"),
                Required(value.ScopeAssignmentId,
                    "CROSS_VIEW_API_AUTH_SCOPE"),
                permissions,
                value.ApiRowCountBeforeRedaction.Value,
                value.ApiRowCountAfterRedaction.Value);
        if (!string.Equals(
                apiSha,
                value.ApiAuthorizationSnapshotSha256,
                StringComparison.Ordinal))
            throw Invalid("CROSS_VIEW_API_AUTH_RELATION_DIGEST_INVALID");
    }

    internal static void RequireExportBinding(
        StatisticReconciliationActualCrossViewExportAuthorizationBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(value.ScopeType, "ASSIGNMENT",
                StringComparison.Ordinal) ||
            !string.Equals(
                value.AuthorizationPolicy,
                StatisticReconciliationActualCrossViewAuthorization
                    .ExportPolicy,
                StringComparison.Ordinal) ||
            !string.Equals(
                value.CapabilityId,
                StatRunExportContract.CapabilityFor(value.ResultKind),
                StringComparison.Ordinal))
            throw Invalid("CROSS_VIEW_EXPORT_AUTH_BINDING_INVALID");

        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_CROSS_VIEW_EXPORT_AUTHORIZATION_BINDING_V1",
            Required(value.ExportId, "CROSS_VIEW_EXPORT_ID"),
            Required(value.WorkId, "CROSS_VIEW_EXPORT_WORK"),
            value.ScopeType,
            Required(value.ScopeId, "CROSS_VIEW_EXPORT_SCOPE"),
            Required(value.ResultKind, "CROSS_VIEW_EXPORT_RESULT_KIND"),
            Required(value.RequestedByUserId,
                "CROSS_VIEW_EXPORT_REQUESTOR"),
            Sha(value.AuthorizationSnapshotSha256,
                "CROSS_VIEW_EXPORT_AUTH_SHA"),
            value.AuthorizationPolicy,
            value.CapabilityId,
            Required(value.CommandId, "CROSS_VIEW_EXPORT_COMMAND"),
            Sha(value.RequestSha256, "CROSS_VIEW_EXPORT_REQUEST_SHA"),
            Sha(value.ReceiptId, "CROSS_VIEW_EXPORT_RECEIPT"),
            Sha(value.OwnerSemanticSha256,
                "CROSS_VIEW_EXPORT_OWNER_SHA"));
        if (!string.Equals(semantic, value.SemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("CROSS_VIEW_EXPORT_AUTH_BINDING_DIGEST_INVALID");
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
