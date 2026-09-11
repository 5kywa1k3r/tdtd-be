using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private const string DirectFieldRoute =
        "api/work-report-field-statistics/summary";
    private const string DirectTextRoute =
        "api/work-report-field-statistics/text-concat";
    private const string DirectTableRoute =
        "api/work-report-table-statistics/summary";
    private const string DirectLabelRoute =
        "api/work-report-label-statistics/summary";

    private async Task RunDirectCasesAsync(CancellationToken ct)
    {
        await RunDirectFieldCasesAsync(ct);
        await RunDirectTableCasesAsync(ct);
        await RunDirectLabelCasesAsync(ct);
        await RunDirectPagingCasesAsync("FIELD", DirectFieldRoute, ct);
        await RunDirectPagingCasesAsync("TABLE", DirectTableRoute, ct);
        await RunDirectPagingCasesAsync("LABEL", DirectLabelRoute, ct);
    }

    private async Task RunDirectFieldCasesAsync(CancellationToken ct)
    {
        await RunReadyCaseAsync(
            "P9-DIR-FIELD-01", "FIELD", DirectFieldRoute, ct);

        await RunDirectCaseAsync(
            "P9-DIR-FIELD-02",
            async () =>
            {
                var root = await ReadReadyAsync(
                    DirectFieldRoute, DirectBody("FIELD"), Actor("executor").Token, ct);
                var metadata = DirectObject(root, "metadata");
                var publication = DirectArray(metadata, "publications")
                    .Select(item => item!.AsObject())
                    .Single();
                HarnessAssert.Equal(
                    Fixture().ConfigHash,
                    DirectString(publication, "configHash"),
                    "Direct field config hash pin");
                HarnessAssert.Equal(
                    Fixture().ConfigHash,
                    CanonicalJsonFileSha256(Encoding.UTF8.GetBytes(Fixture().ConfigPayloadJson)),
                    "Direct field trusted config recompute");
                HarnessAssert.Equal(
                    LifecycleStageLockSha256,
                    DirectString(publication, "stageLockSha256"),
                    "Direct field projection stage pin");
                HarnessAssert.True(
                    DirectLong(root, "totalRows") > 0,
                    "Direct field full-filter total missing");
                return DirectObservation(
                    "Field metadata pins the immutable P9-02 publication and recomputed locked config.",
                    root);
            });

        await RunFilterCaseAsync(
            "P9-DIR-FIELD-03",
            "FIELD",
            DirectFieldRoute,
            "fieldId",
            "field_amount",
            ct);

        await RunDirectCaseAsync(
            "P9-DIR-FIELD-04",
            async () =>
            {
                var body = DirectBody("FIELD");
                body.Remove("includeDrilldown");
                body["dynamicFormTemplateId"] = Fixture().TemplateId;
                var response = await RequireApi().PostAsync(
                    DirectTextRoute, body, Actor("executor").Token, ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response, HttpStatusCode.BadRequest, "Direct text selector required");
                return new CaseObservation(
                    "Canonical text route rejects an omitted field selector instead of reading mutable report payload.",
                    $"http={(int)response.StatusCode};reason=selector-required");
            });

        await RunDeterministicCaseAsync(
            "P9-DIR-FIELD-05", "FIELD", DirectFieldRoute, ct);
        await RunForbiddenCaseAsync(
            "P9-DIR-FIELD-06", "FIELD", DirectFieldRoute, ct);
        await RunEmptyCaseAsync(
            "P9-DIR-FIELD-07", "FIELD", DirectFieldRoute, ct);
        await RunStaleCaseAsync(
            "P9-DIR-FIELD-08", "FIELD", DirectFieldRoute, ct);
    }

    private async Task RunDirectTableCasesAsync(CancellationToken ct)
    {
        await RunReadyCaseAsync(
            "P9-DIR-TABLE-01", "TABLE", DirectTableRoute, ct);
        await RunDirectCaseAsync(
            "P9-DIR-TABLE-02",
            async () =>
            {
                var root = await ReadReadyAsync(
                    DirectTableRoute, DirectBody("TABLE"), Actor("executor").Token, ct);
                var metrics = DirectArray(root, "rows")
                    .Select(row => DirectString(row!.AsObject(), "metricKey"))
                    .ToArray();
                HarnessAssert.True(
                    metrics.Length > 0 && metrics.All(value => !string.IsNullOrWhiteSpace(value)),
                    "Direct table metricKey must be stable and non-empty");
                HarnessAssert.Equal(
                    metrics.Length,
                    metrics.Distinct(StringComparer.Ordinal).Count(),
                    "Direct table metricKey duplicate in fixture slice");
                return DirectObservation(
                    "Table results expose stable metric keys rather than positions or tag arrays.",
                    root);
            });
        await RunFilterCaseAsync(
            "P9-DIR-TABLE-03",
            "TABLE",
            DirectTableRoute,
            "blockId",
            LifecycleBlockId,
            ct);
        await RunDrilldownCaseAsync(
            "P9-DIR-TABLE-04", "TABLE", "TABLE_METRIC", DirectTableRoute, ct);
        await RunDeterministicCaseAsync(
            "P9-DIR-TABLE-05", "TABLE", DirectTableRoute, ct);
        await RunForbiddenCaseAsync(
            "P9-DIR-TABLE-06", "TABLE", DirectTableRoute, ct);
        await RunEmptyCaseAsync(
            "P9-DIR-TABLE-07", "TABLE", DirectTableRoute, ct);
        await RunStaleCaseAsync(
            "P9-DIR-TABLE-08", "TABLE", DirectTableRoute, ct);
    }

    private async Task RunDirectLabelCasesAsync(CancellationToken ct)
    {
        await RunReadyCaseAsync(
            "P9-DIR-LABEL-01", "LABEL", DirectLabelRoute, ct);
        await RunDirectCaseAsync(
            "P9-DIR-LABEL-02",
            async () =>
            {
                var root = await ReadReadyAsync(
                    DirectLabelRoute, DirectBody("LABEL"), Actor("executor").Token, ct);
                var row = DirectArray(root, "rows").First()!.AsObject();
                HarnessAssert.Equal("FIELD_STATISTIC_LABEL", DirectString(row, "statisticLabelLayer"), "Statistic-label layer");
                HarnessAssert.Equal("RUNTIME_ROW_LABEL", DirectString(row, "runtimeLabelLayer"), "Runtime-row-label layer");
                HarnessAssert.Equal("LABEL_CATALOG", DirectString(row, "catalogLabelLayer"), "Label-catalog layer");
                HarnessAssert.Equal("LOCKED_P8_CONFIG", DirectString(row, "configurationLayer"), "Locked-config layer");
                return DirectObservation(
                    "Label results preserve all four frozen label layers without collapsing identities.",
                    root);
            });
        await RunFilterCaseAsync(
            "P9-DIR-LABEL-03",
            "LABEL",
            DirectLabelRoute,
            "labelCode",
            LifecycleLabelCode,
            ct);
        await RunDrilldownCaseAsync(
            "P9-DIR-LABEL-04", "LABEL", "ROW_LABEL", DirectLabelRoute, ct);
        await RunDeterministicCaseAsync(
            "P9-DIR-LABEL-05", "LABEL", DirectLabelRoute, ct);
        await RunForbiddenCaseAsync(
            "P9-DIR-LABEL-06", "LABEL", DirectLabelRoute, ct);
        await RunEmptyCaseAsync(
            "P9-DIR-LABEL-07", "LABEL", DirectLabelRoute, ct);
        await RunStaleCaseAsync(
            "P9-DIR-LABEL-08", "LABEL", DirectLabelRoute, ct);
    }

    private async Task RunReadyCaseAsync(
        string caseId,
        string family,
        string route,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var root = await ReadReadyAsync(
                    route, DirectBody(family), Actor("executor").Token, ct);
                HarnessAssert.True(
                    DirectArray(root, "rows").Count > 0,
                    $"Direct {family} rows missing");
                return DirectObservation(
                    $"{family} reads only the completed current generation through real Kestrel.",
                    root);
            });

    private async Task RunFilterCaseAsync(
        string caseId,
        string family,
        string route,
        string filterName,
        string filterValue,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var body = DirectBody(family);
                body[filterName] = filterValue;
                var root = await ReadReadyAsync(
                    route, body, Actor("executor").Token, ct);
                HarnessAssert.True(
                    DirectArray(root, "rows").Count > 0,
                    $"Direct {family} authorized filter returned no rows");
                return DirectObservation(
                    $"{family} server-side filter retained a READY canonical result.",
                    root);
            });

    private async Task RunDrilldownCaseAsync(
        string caseId,
        string family,
        string sourceKind,
        string route,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var body = DirectBody(family);
                body["includeDrilldown"] = true;
                var root = await ReadReadyAsync(
                    route, body, Actor("executor").Token, ct);
                var rows = DirectArray(root, "drilldownRows");
                HarnessAssert.True(rows.Count > 0, $"Direct {family} drilldown missing");
                HarnessAssert.True(
                    rows.All(row => string.Equals(
                        DirectString(row!.AsObject(), "sourceKind"),
                        sourceKind,
                        StringComparison.Ordinal)),
                    $"Direct {family} drilldown source kind");
                HarnessAssert.True(
                    rows.All(row => string.Equals(
                        DirectString(row!.AsObject(), "workAssignmentId"),
                        Fixture().AssignmentId,
                        StringComparison.Ordinal)),
                    $"Direct {family} drilldown escaped authorized assignment");
                return DirectObservation(
                    $"{family} drilldown is server-authorized and stable-identity typed.",
                    root);
            });

    private async Task RunDeterministicCaseAsync(
        string caseId,
        string family,
        string route,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var body = DirectBody(family);
                var responses = await Task.WhenAll(
                    ReadReadyAsync(route, (JsonObject)body.DeepClone(), Actor("executor").Token, ct),
                    ReadReadyAsync(route, (JsonObject)body.DeepClone(), Actor("executor").Token, ct));
                var left = DirectArray(responses[0], "rows").ToJsonString();
                var right = DirectArray(responses[1], "rows").ToJsonString();
                HarnessAssert.Equal(left, right, $"Direct {family} concurrent read ordering");
                HarnessAssert.Equal(
                    DirectLong(responses[0], DirectTotalProperty(family)),
                    DirectLong(responses[1], DirectTotalProperty(family)),
                    $"Direct {family} concurrent totals");
                return new CaseObservation(
                    $"Concurrent repeated {family} reads returned identical deterministic rows and totals.",
                    $"rowsSha={HashBytes(Encoding.UTF8.GetBytes(left))};total={DirectLong(responses[0], DirectTotalProperty(family))}");
            });

    private async Task RunForbiddenCaseAsync(
        string caseId,
        string family,
        string route,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var response = await RequireApi().PostAsync(
                    route, DirectBody(family), Actor("outsider").Token, ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response, HttpStatusCode.Forbidden, $"Direct {family} outsider");
                HarnessAssert.True(
                    !response.Body.Contains(Fixture().ReportId, StringComparison.Ordinal),
                    $"Direct {family} forbidden body leaked report identity");
                return new CaseObservation(
                    $"{family} authorization ran before result existence and disclosed no report identity.",
                    $"http={(int)response.StatusCode};writes=0");
            });

    private async Task RunEmptyCaseAsync(
        string caseId,
        string family,
        string route,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var body = DirectBody(family);
                body["periodInstanceKey"] = $"{Fixture().PeriodInstanceKey}:EMPTY";
                var response = await RequireApi().PostAsync(
                    route, body, Actor("executor").Token, ct: ct);
                ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"Direct {family} empty");
                var root = ApiHarnessClient.RequiredObject(response.Json, $"Direct {family} empty");
                HarnessAssert.Equal("EMPTY", DirectState(root), $"Direct {family} empty state");
                HarnessAssert.Equal(0, DirectArray(root, "rows").Count, $"Direct {family} empty rows");
                return DirectObservation(
                    $"{family} authorized no-publication period is explicitly EMPTY.",
                    root);
            });

    private async Task RunStaleCaseAsync(
        string caseId,
        string family,
        string route,
        CancellationToken ct)
        => await RunDirectCaseAsync(
            caseId,
            async () =>
            {
                var works = RequireDatabase().GetCollection<BsonDocument>("works");
                var filter = new BsonDocument("_id", ObjectId.Parse(Fixture().WorkId));
                var work = await works.Find(filter).SingleAsync(ct);
                var original = BsonLong(work, "directSourceRevision");
                JsonObject root;
                try
                {
                    await works.UpdateOneAsync(
                        filter,
                        Builders<BsonDocument>.Update.Set("directSourceRevision", original + 1),
                        cancellationToken: ct);
                    var response = await RequireApi().PostAsync(
                        route, DirectBody(family), Actor("executor").Token, ct: ct);
                    ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"Direct {family} stale");
                    root = ApiHarnessClient.RequiredObject(response.Json, $"Direct {family} stale");
                    HarnessAssert.Equal("STALE", DirectState(root), $"Direct {family} stale state");
                    HarnessAssert.Equal(0, DirectArray(root, "rows").Count, $"Direct {family} stale rows");
                }
                finally
                {
                    await works.UpdateOneAsync(
                        filter,
                        Builders<BsonDocument>.Update.Set("directSourceRevision", original),
                        cancellationToken: CancellationToken.None);
                }
                return DirectObservation(
                    $"{family} source-revision drift returns STALE and never exposes the old generation.",
                    root);
            });

    private async Task RunDirectPagingCasesAsync(
        string family,
        string route,
        CancellationToken ct)
    {
        var prefix = $"P9-DIR-PAGE-{family}";
        await RunDirectCaseAsync(
            $"{prefix}-01",
            async () =>
            {
                var root = await ReadReadyAsync(
                    route, DirectBody(family, 0, 1), Actor("executor").Token, ct);
                HarnessAssert.Equal(0, DirectInt(root, "page"), $"{family} page base");
                HarnessAssert.Equal(1, DirectInt(root, "pageSize"), $"{family} page size 1");
                HarnessAssert.True(DirectInt(root, "returnedRows") <= 1, $"{family} returned rows cap");
                HarnessAssert.True(DirectLong(root, DirectTotalProperty(family)) > 0, $"{family} global total");
                return DirectObservation($"{family} zero-based pageSize=1 is bounded.", root);
            });
        await RunDirectCaseAsync(
            $"{prefix}-02",
            async () =>
            {
                var one = await ReadReadyAsync(
                    route, DirectBody(family, 0, 1), Actor("executor").Token, ct);
                var fifty = await ReadReadyAsync(
                    route, DirectBody(family, 0, 50), Actor("executor").Token, ct);
                HarnessAssert.Equal(
                    DirectLong(one, DirectTotalProperty(family)),
                    DirectLong(fifty, DirectTotalProperty(family)),
                    $"{family} full-filter total across page sizes");
                return new CaseObservation(
                    $"{family} full-filter totals are invariant for pageSize 1 and 50.",
                    $"total={DirectLong(one, DirectTotalProperty(family))};sizes=1,50");
            });
        await RunDirectCaseAsync(
            $"{prefix}-03",
            async () =>
            {
                var body = DirectBody(family, 0, 1);
                var first = await ReadReadyAsync(
                    route, (JsonObject)body.DeepClone(), Actor("executor").Token, ct);
                var replay = await ReadReadyAsync(
                    route, (JsonObject)body.DeepClone(), Actor("executor").Token, ct);
                HarnessAssert.Equal(
                    DirectArray(first, "rows").ToJsonString(),
                    DirectArray(replay, "rows").ToJsonString(),
                    $"{family} repeated page");
                return DirectObservation($"{family} repeated page is deterministic.", replay);
            });
        await RunDirectCaseAsync(
            $"{prefix}-04",
            async () =>
            {
                var negative = await RequireApi().PostAsync(
                    route, DirectBody(family, -1, 50), Actor("executor").Token, ct: ct);
                var oversized = await RequireApi().PostAsync(
                    route, DirectBody(family, 0, 201), Actor("executor").Token, ct: ct);
                ApiHarnessClient.ExpectStatus(negative, HttpStatusCode.BadRequest, $"{family} negative page");
                ApiHarnessClient.ExpectStatus(oversized, HttpStatusCode.BadRequest, $"{family} pageSize 201");
                return new CaseObservation(
                    $"{family} rejects negative page and pageSize outside 1..200.",
                    $"negative={(int)negative.StatusCode};oversized={(int)oversized.StatusCode}");
            });
    }

    private JsonObject DirectBody(
        string family,
        int page = 0,
        int pageSize = 50)
    {
        var body = new JsonObject
        {
            ["workId"] = Fixture().WorkId,
            ["scopeType"] = "WORK",
            ["scopeId"] = Fixture().WorkId,
            ["dynamicFormTemplateId"] = Fixture().TemplateId,
            ["periodInstanceKey"] = Fixture().PeriodInstanceKey,
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["includeDrilldown"] = false
        };
        if (family == "LABEL")
            body["labelCode"] = LifecycleLabelCode;
        return body;
    }

    private async Task<JsonObject> ReadReadyAsync(
        string route,
        JsonObject body,
        string token,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(route, body, token, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"P9-DIR {route}");
        var root = ApiHarnessClient.RequiredObject(response.Json, $"P9-DIR {route}");
        HarnessAssert.Equal("READY", DirectState(root), $"P9-DIR {route} state");
        return root;
    }

    private static CaseObservation DirectObservation(
        string summary,
        JsonObject root)
        => new(
            summary,
            $"state={DirectState(root)};rows={DirectArray(root, "rows").Count};total={DirectLong(root, root.ContainsKey("totalRows") ? "totalRows" : "returnedRows")}");

    private static string DirectState(JsonObject root)
        => DirectString(DirectObject(root, "metadata"), "state");

    private static string DirectTotalProperty(string family)
        => family == "LABEL" ? "totalRowCount" : "totalValueCount";

    private static JsonObject DirectObject(JsonObject root, string property)
        => root[property] as JsonObject
           ?? throw new InvalidOperationException($"Direct property '{property}' is not an object: {root.ToJsonString()}");

    private static JsonArray DirectArray(JsonObject root, string property)
        => root[property] as JsonArray
           ?? throw new InvalidOperationException($"Direct property '{property}' is not an array: {root.ToJsonString()}");

    private static string DirectString(JsonObject root, string property)
        => root[property]?.GetValue<string>()
           ?? throw new InvalidOperationException($"Direct property '{property}' is not a string: {root.ToJsonString()}");

    private static int DirectInt(JsonObject root, string property)
        => root[property]?.GetValue<int>()
           ?? throw new InvalidOperationException($"Direct property '{property}' is not an integer: {root.ToJsonString()}");

    private static long DirectLong(JsonObject root, string property)
        => root[property]?.GetValue<long>()
           ?? throw new InvalidOperationException($"Direct property '{property}' is not an integer: {root.ToJsonString()}");
}
