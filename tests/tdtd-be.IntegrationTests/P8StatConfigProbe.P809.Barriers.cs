using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP809BarrierCasesAsync(CancellationToken ct)
    {
        await RunP809SingleBarrierCaseAsync(
            "P8-BND-005",
            "P9_RUN",
            "A real Kestrel P9 run attempt returned the stable phase barrier before enqueue or write.",
            ct);

        await RunEvidenceCaseAsync(
            "P8-BND-006",
            "system_admin",
            new[] { "p809-barrier-projection-006", "p809-barrier-result-006" }
                .Concat(P809ActualRouteCommandIds("P8-BND-006"))
                .ToArray(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                await RequireP809BarrierAsync("P8-BND-006", "P9_PROJECTION", "p809-barrier-projection-006", ct);
                await RequireP809BarrierAsync("P8-BND-006", "P9_RESULT", "p809-barrier-result-006", ct);
                await RunP809ActualRouteMatrixForCaseAsync("P8-BND-006", ct);
                return new CaseObservation(
                    "Real Kestrel projection and result attempts both failed closed at the P9 boundary.",
                    "entries=P9_PROJECTION+P9_RESULT;http=409+409;writes=0;resultDelta=0");
            },
            ct);

        await RunP809SingleBarrierCaseAsync(
            "P8-BND-007",
            "P9_EXPORT",
            "A real Kestrel P9 export attempt returned the stable phase barrier before export write.",
            ct);
        await RunP809SingleBarrierCaseAsync(
            "P8-BND-008",
            "P10_RECONCILE",
            "A real Kestrel P10 reconcile attempt returned the stable phase barrier before reconcile write.",
            ct);
    }

    private async Task RunP809SingleBarrierCaseAsync(
        string caseId,
        string entry,
        string detail,
        CancellationToken ct)
    {
        var suffix = caseId[^3..];
        var commandId = $"p809-barrier-{entry.ToLowerInvariant().Replace('_', '-')}-{suffix}";
        await RunEvidenceCaseAsync(
            caseId,
            "system_admin",
            new[] { commandId }
                .Concat(P809ActualRouteCommandIds(caseId))
                .ToArray(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                await RequireP809BarrierAsync(caseId, entry, commandId, ct);
                await RunP809ActualRouteMatrixForCaseAsync(caseId, ct);
                return new CaseObservation(
                    detail,
                    $"entry={entry};http=409;writes=0;resultDelta=0");
            },
            ct);
    }

    private async Task<P809BarrierEvidence> RequireP809BarrierAsync(
        string caseId,
        string entry,
        string commandId,
        CancellationToken ct)
    {
        var targetPhase = entry.StartsWith("P10_", StringComparison.Ordinal)
            ? "P10"
            : "P9";
        var response = await _api.PostAsync(
            $"{P809BarrierRoute}/{entry}",
            new JsonObject
            {
                ["ownerKind"] = "UNIT",
                ["ownerId"] = _unitAId,
                ["commandId"] = commandId,
                ["expectedBundleHash"] = _p809FullBundleHash ?? EmptyConfigHash
            },
            Actor("system_admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, $"{entry} phase barrier");
        var code = RequiredP809RecursiveString(response.Json, "errorCode", "code");
        HarnessAssert.Equal(
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
            code,
            $"{entry} error code drifted");
        var actualEntry = RequiredP809RecursiveString(response.Json, "entry");
        var actualTargetPhase = RequiredP809RecursiveString(response.Json, "targetPhase");
        var reason = RequiredP809RecursiveString(response.Json, "reason");
        var eligibility = RequiredP809RecursiveString(response.Json, "eligibility");
        var freshness = RequiredP809RecursiveString(response.Json, "freshness");
        HarnessAssert.Equal(entry, actualEntry, $"{entry} response identity drifted");
        HarnessAssert.Equal(targetPhase, actualTargetPhase, $"{entry} target phase drifted");
        HarnessAssert.Equal("P8_CONFIG_ONLY", reason, $"{entry} reason drifted");
        HarnessAssert.Equal("BLOCKED_UNTIL_TARGET_PHASE", eligibility,
            $"{entry} eligibility drifted");
        HarnessAssert.Equal("NOT_APPLICABLE", freshness,
            $"{entry} freshness drifted");
        var evidence = new P809BarrierEvidence(
            caseId,
            entry,
            targetPhase,
            (int)response.StatusCode,
            code,
            reason,
            eligibility,
            freshness);
        _p809Barriers.Add(evidence);
        return evidence;
    }

    private static string RequiredP809RecursiveString(JsonNode? node, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = ApiHarnessClient.FindStringRecursive(node, property);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        throw new InvalidOperationException(
            $"P8-09 response lacks required [{string.Join(',', properties)}]. Body={node?.ToJsonString() ?? "<null>"}");
    }
}
