using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP809ActualRouteMatrixForCaseAsync(
        string caseId,
        CancellationToken ct)
    {
        var fakeId = caseId switch
        {
            "P8-BND-005" => "6a6f71b4d34757841341ddc2",
            "P8-BND-006" => "6a6f71b5d34757841341ddc3",
            _ => ObjectId.GenerateNewId().ToString()
        };
        var routes = caseId switch
        {
            "P8-BND-005" => BuildP809RunRouteSpecs(fakeId),
            "P8-BND-006" => BuildP809ProjectionResultRouteSpecs(fakeId),
            "P8-BND-007" => BuildP809ExportRouteSpecs(fakeId),
            "P8-BND-008" => Array.Empty<P809ActualRouteSpec>(),
            _ => throw new InvalidOperationException($"Unexpected P8-09 route-matrix case {caseId}.")
        };

        for (var index = 0; index < routes.Length; index++)
        {
            var spec = routes[index];
            var commandId = $"p809-actual-{caseId[^3..]}-{index + 1:00}";
            await RequireP809ActualRouteBarrierAsync(
                caseId,
                spec,
                commandId,
                ct);
        }
    }

    private static string[] P809ActualRouteCommandIds(string caseId)
    {
        var count = caseId switch
        {
            "P8-BND-005" => 14,
            "P8-BND-006" => 20,
            "P8-BND-007" => 1,
            "P8-BND-008" => 0,
            _ => 0
        };
        return Enumerable.Range(1, count)
            .Select(index => $"p809-actual-{caseId[^3..]}-{index:00}")
            .ToArray();
    }

    private static P809ActualRouteSpec[] BuildP809RunRouteSpecs(string id)
        =>
        [
            Post("P9_PROJECTION", "api/work-report-field-statistics/rebuild", new JsonObject { ["workId"] = id }),
            Post("P9_PROJECTION", "api/work-report-table-statistics/rebuild", new JsonObject { ["workId"] = id }),
            Post("P9_RUN", "api/work-report-statistic-diffs/run", new JsonObject { ["configId"] = id }),
            Post("P9_RUN", $"api/work-assignment-advanced-summary/configs/{id}/preview", new JsonObject()),
            Post("P9_RUN", $"api/work-assignment-advanced-summary/configs/{id}/hierarchy/day/2026-08-02/build", new JsonObject()),
            Post("P9_RUN", $"api/work-assignment-advanced-summary/configs/{id}/hierarchy/month/2026-08/build", new JsonObject()),
            Post("P9_RUN", $"api/work-assignment-advanced-summary/configs/{id}/hierarchy/year/2026/build", new JsonObject()),
            Post("P9_RUN", $"api/work-assignment-reports/{id}/draft/apply-dynamic-form-aggregate", ReportAggregateBody(id)),
            Post("P9_RUN", $"api/work-assignment-reports/{id}/draft/preview-dynamic-form-aggregate", ReportAggregateBody(id)),
            Post("P9_PROJECTION", "api/admin/operations/job-runs/statistic-rebuild-jobs/process?maxJobs=1&batchSize=1", new JsonObject()),
            Post("P9_PROJECTION", $"api/admin/operations/job-runs/statistic-rebuild-jobs/{id}/reset", new JsonObject()),
            Post("P9_RESULT", $"api/admin/operations/job-runs/basic-summary-jobs/{id}/reset", new JsonObject()),
            Post("P9_RESULT", $"api/admin/operations/job-runs/advanced-summary-nodes/DAY/{id}/reset", new JsonObject()),
            Post("P9_RESULT", "api/admin/operations/job-runs/advanced-summary-nodes/cleanup", new JsonObject())
        ];

    private static P809ActualRouteSpec[] BuildP809ProjectionResultRouteSpecs(string id)
        =>
        [
            Post("P9_RESULT", "api/work-report-field-statistics/summary", new JsonObject()),
            Post("P9_RESULT", "api/work-report-field-statistics/text-concat", new JsonObject { ["workId"] = id, ["dynamicFormTemplateId"] = id }),
            Post("P9_RESULT", "api/work-report-table-statistics/summary", new JsonObject()),
            Post("P9_RESULT", "api/work-report-label-statistics/summary", new JsonObject()),
            Post("P9_RESULT", "api/work-assignment-basic-summary/summary", new JsonObject { ["scopeAssignmentId"] = id }),
            Post("P9_RESULT", "api/work-assignment-basic-summary/once", new JsonObject { ["scopeAssignmentId"] = id }),
            Post("P9_RESULT", $"api/work-assignment-advanced-summary/configs/{id}/hierarchy/query", new JsonObject()),
            Get("P9_RESULT", $"api/dashboard-mindmap/nodes/{id}/summary"),
            Post("P9_RESULT", $"api/dashboard-mindmap/nodes/{id}/table-metrics/reports/search", new JsonObject()),
            Post("P9_RESULT", $"api/dashboard-mindmap/nodes/{id}/field-metrics/reports/search", new JsonObject()),
            Post("P9_RESULT", $"api/dashboard-mindmap/nodes/{id}/labels/reports/search", new JsonObject()),
            Post("P9_RESULT", "api/work-assignment-aggregate-table/table", new JsonObject { ["workId"] = id, ["parentAssignmentId"] = id, ["dynamicExcelId"] = id }),
            Post("P9_RESULT", "api/work-assignment-aggregate-table/dynamic-form/table", new JsonObject { ["scopeAssignmentId"] = id, ["dynamicFormTemplateId"] = id }),
            Get("P9_PROJECTION", "api/admin/operations/job-runs/statistic-rebuild-jobs?page=0&pageSize=1"),
            Get("P9_PROJECTION", "api/admin/operations/job-runs/flow-statistics/diagnostics?limit=1"),
            Get("P9_RESULT", "api/admin/operations/job-runs/basic-summary-jobs?page=0&pageSize=1"),
            Get("P9_RESULT", "api/admin/operations/job-runs/advanced-summary-nodes?page=0&pageSize=1"),
            Post("P9_RESULT", "api/admin/operations/job-runs/advanced-summary-nodes/diagnostics/day", new JsonObject()),
            Post("P9_RESULT", "api/admin/operations/job-runs/advanced-summary-nodes/diagnostics/month", new JsonObject()),
            Post("P9_RESULT", "api/admin/operations/job-runs/advanced-summary-nodes/diagnostics/year", new JsonObject())
        ];

    private static P809ActualRouteSpec[] BuildP809ExportRouteSpecs(string id)
        =>
        [
            Post("P9_EXPORT", "api/work-report-field-statistics/text-concat/export", new JsonObject { ["workId"] = id, ["dynamicFormTemplateId"] = id })
        ];

    private static JsonObject ReportAggregateBody(string id)
        => new()
        {
            ["commandId"] = $"p809-report-aggregate-{id}",
            ["expectedPayloadRevision"] = 0,
            ["aggregateRequest"] = new JsonObject
            {
                ["scopeAssignmentId"] = id,
                ["dynamicFormTemplateId"] = id
            }
        };

    private static P809ActualRouteSpec Get(string entry, string path)
        => new(entry, HttpMethod.Get, path, null);

    private static P809ActualRouteSpec Post(string entry, string path, JsonNode body)
        => new(entry, HttpMethod.Post, path, body);

    private async Task RequireP809ActualRouteBarrierAsync(
        string caseId,
        P809ActualRouteSpec spec,
        string commandId,
        CancellationToken ct)
    {
        var before = await CaptureP809ZeroWriteInventoryAsync(ct);
        var response = await _api.SendAsync(
            spec.Method,
            spec.Path,
            spec.Body?.DeepClone(),
            Actor("system_admin").Token,
            new Dictionary<string, string>
            {
                ["X-P8-Command-Id"] = commandId,
                ["X-P8-Expected-Bundle-Hash"] = _p809FullBundleHash ?? EmptyConfigHash
            },
            ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            $"P8-09 actual route barrier {spec.Method.Method} {spec.Path}");
        var code = RequiredP809RecursiveString(response.Json, "errorCode", "code");
        HarnessAssert.Equal(
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
            code,
            $"Actual route {spec.Path} error code drifted");
        HarnessAssert.Equal(
            spec.Entry,
            RequiredP809RecursiveString(response.Json, "entry"),
            $"Actual route {spec.Path} barrier entry drifted");
        HarnessAssert.Equal(
            "P9",
            RequiredP809RecursiveString(response.Json, "targetPhase"),
            $"Actual route {spec.Path} target phase drifted");
        HarnessAssert.Equal(
            "P8_CONFIG_ONLY",
            RequiredP809RecursiveString(response.Json, "reason"),
            $"Actual route {spec.Path} reason drifted");
        HarnessAssert.Equal(
            "BLOCKED_UNTIL_TARGET_PHASE",
            RequiredP809RecursiveString(response.Json, "eligibility"),
            $"Actual route {spec.Path} eligibility drifted");
        HarnessAssert.Equal(
            "NOT_APPLICABLE",
            RequiredP809RecursiveString(response.Json, "freshness"),
            $"Actual route {spec.Path} freshness drifted");
        var after = await CaptureP809ZeroWriteInventoryAsync(ct);
        RequireP809ZeroWrite($"{caseId}:{spec.Method.Method}:{spec.Path}", before, after);
        var beforeHash = P809InventoryHash(before);
        var afterHash = P809InventoryHash(after);
        HarnessAssert.Equal(beforeHash, afterHash,
            $"Actual route {spec.Path} exact 53-store inventory hash drifted");
        _p809ActualRoutes.Add(new P809ActualRouteEvidence(
            caseId,
            spec.Entry,
            spec.Method.Method,
            spec.Path,
            (int)response.StatusCode,
            code,
            ApiHarnessClient.FindStringRecursive(response.Json, "requestedOperation"),
            P809ZeroWriteCollections.Length,
            beforeHash,
            afterHash));
    }

    private static string P809InventoryHash(IEnumerable<P809ZeroWriteState> rows)
    {
        var text = string.Join(
            "\n",
            rows.OrderBy(row => row.Collection, StringComparer.Ordinal)
                .Select(row =>
                    $"{row.Collection}|{row.Exists}|{row.Count}|{row.DocumentSetSha256}"));
        return Sha256(Encoding.UTF8.GetBytes(text));
    }
}

internal sealed record P809ActualRouteSpec(
    string Entry,
    HttpMethod Method,
    string Path,
    JsonNode? Body);
