using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private Task<ApiHarnessResponse> EnqueueP808JobAsync(
        P8Actor actor,
        string ownerKind,
        string ownerId,
        JsonObject request,
        CancellationToken ct)
        => _api.PostAsync(
            $"api/stat-config/owners/{ownerKind}/{ownerId}/readiness-jobs",
            request,
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> ReadP808SafeJobAsync(
        P8Actor actor,
        string jobId,
        CancellationToken ct)
        => _api.GetAsync(
            $"{P808PublicJobsRoute}/{jobId}",
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> SearchP808AdminJobsAsync(
        P8Actor actor,
        string? query,
        CancellationToken ct)
        => _api.GetAsync(
            string.IsNullOrWhiteSpace(query)
                ? P808AdminJobsRoute
                : $"{P808AdminJobsRoute}?{query}",
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> ReadP808DiagnosticsAsync(
        P8Actor actor,
        string jobId,
        CancellationToken ct)
        => _api.GetAsync(
            $"{P808AdminJobsRoute}/{jobId}",
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> ProcessP808JobsAsync(
        P8Actor actor,
        int maxJobs,
        CancellationToken ct)
        => _api.PostAsync(
            $"{P808AdminJobsRoute}/process?maxJobs={maxJobs}",
            new JsonObject(),
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> ResetP808JobAsync(
        P8Actor actor,
        string jobId,
        JsonObject request,
        CancellationToken ct)
        => _api.PostAsync(
            $"{P808AdminJobsRoute}/{jobId}/reset",
            request,
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> CancelP808JobAsync(
        P8Actor actor,
        string jobId,
        JsonObject request,
        CancellationToken ct)
        => _api.PostAsync(
            $"{P808AdminJobsRoute}/{jobId}/cancel",
            request,
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> CleanupP808JobsAsync(
        P8Actor actor,
        JsonObject request,
        CancellationToken ct)
        => _api.PostAsync(
            $"{P808AdminJobsRoute}/cleanup",
            request,
            actor.Token,
            ct: ct);

    private Task<ApiHarnessResponse> ReadP808IndexReadinessAsync(
        P8Actor actor,
        CancellationToken ct)
        => _api.GetAsync(
            $"{P808AdminJobsRoute}/indexes",
            actor.Token,
            ct: ct);

    private static P808JobApiIdentity ParseP808JobIdentity(
        JsonNode? node,
        string context)
    {
        var jobId = RequiredP808String(node, context, "jobId", "id");
        var externalStatus = RequiredP808String(
            node,
            context,
            "externalStatus",
            "status");
        return new P808JobApiIdentity(
            jobId,
            externalStatus,
            OptionalP808String(node, "ownerKind"),
            OptionalP808String(node, "ownerId"),
            OptionalP808String(node, "configId"),
            OptionalP808String(node, "versionId"),
            ApiHarnessClient.FindIntRecursive(node, "versionNo"),
            OptionalP808String(node, "configHash"),
            OptionalP808String(node, "correlationId"),
            OptionalP808String(node, "receiptId"),
            OptionalP808String(node, "auditOutboxId"),
            node?.DeepClone());
    }

    private static string RequiredP808String(
        JsonNode? node,
        string context,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ApiHarnessClient.FindStringRecursive(
                node,
                propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        throw new InvalidOperationException(
            $"{context} lacks required string [{string.Join(',', propertyNames)}]. " +
            $"Body={node?.ToJsonString() ?? "<null>"}");
    }

    private static string? OptionalP808String(
        JsonNode? node,
        string propertyName)
        => ApiHarnessClient.FindStringRecursive(node, propertyName);
}

internal sealed record P808JobApiIdentity(
    string JobId,
    string ExternalStatus,
    string? OwnerKind,
    string? OwnerId,
    string? ConfigId,
    string? VersionId,
    int? VersionNo,
    string? ConfigHash,
    string? CorrelationId,
    string? ReceiptId,
    string? AuditOutboxId,
    JsonNode? Raw);

