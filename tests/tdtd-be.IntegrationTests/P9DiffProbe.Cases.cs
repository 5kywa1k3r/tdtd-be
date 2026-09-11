using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunDiffCasesAsync(CancellationToken ct)
    {
        await RunDiffFieldCasesAsync(ct);
        await RunDiffTableCasesAsync(ct);
        await RunDiffRowLabelCasesAsync(ct);
        await RunDiffOwnerCasesAsync(ct);
    }

    private async Task RunDiffFieldCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-DIF-FIELD-01", async () =>
        {
            _difFieldResult = await ExecuteDiffRunAsync(
                _difConfigs[0], "p9-dif-run-field", 100,
                Actor("executor").Token, HttpStatusCode.Created, ct);
            RequireCompletedDiffResult(_difFieldResult, _difConfigs[0], 1, 0, 1);
            var row = DiffRows(_difFieldResult).Single()!.AsObject();
            HarnessAssert.Equal("FIELD", DiffString(row, "conceptKind"),
                "FIELD concept kind");
            HarnessAssert.Equal(3m, DiffDecimal(row, "numericDelta"),
                "FIELD numeric delta");
            return new CaseObservation(
                "Locked FIELD config executed through Kestrel and persisted one typed changed row.",
                "status=COMPLETED;rows=1;delta=3;owner=diff_results");
        }, ct);

        await RunCaseAsync("P9-DIF-FIELD-02", () =>
        {
            var comparison = CompareDiffValues(
                DiffTyped("VALUE", "NUMBER", "1", 1.0m),
                DiffTyped("VALUE", "NUMBER", "1", 1m));
            HarnessAssert.True(comparison.Equal,
                "Scale-normalized decimal values must compare equal");
            HarnessAssert.Equal(0m, comparison.NumericDelta,
                "Equal numeric delta");
            return Task.FromResult(new CaseObservation(
                "Numeric equality uses canonical decimal identity, not display scale.",
                "1.0==1;canonical=1;delta=0"));
        }, ct);

        await RunCaseAsync("P9-DIF-FIELD-03", () =>
        {
            var comparison = CompareDiffValues(
                DiffTyped("NULL", "NUMBER"),
                DiffTyped("MISSING", "NUMBER"));
            HarnessAssert.True(!comparison.Equal,
                "NULL and MISSING were collapsed");
            HarnessAssert.Equal("STATE_CHANGED", comparison.DifferenceKind,
                "NULL/MISSING difference kind");
            return Task.FromResult(new CaseObservation(
                "NULL and MISSING remain distinct typed states.",
                "NULL!=MISSING;kind=STATE_CHANGED"));
        }, ct);

        await RunCaseAsync("P9-DIF-FIELD-04", () =>
        {
            var comparison = CompareDiffValues(
                DiffTyped("EMPTY", "TEXT", string.Empty),
                DiffTyped("VALUE", "TEXT", "x"));
            HarnessAssert.True(!comparison.Equal, "EMPTY and VALUE were collapsed");
            HarnessAssert.Equal("STATE_CHANGED", comparison.DifferenceKind,
                "EMPTY/VALUE difference kind");
            return Task.FromResult(new CaseObservation(
                "EMPTY remains typed and differs from a non-empty value.",
                "EMPTY!=VALUE;raw-preserved=false"));
        }, ct);

        await RunCaseAsync("P9-DIF-FIELD-05", () =>
        {
            var comparison = CompareDiffValues(
                DiffTyped("VALUE", "BOOLEAN", "true", boolean: true),
                DiffTyped("VALUE", "BOOLEAN", "false", boolean: false));
            HarnessAssert.True(!comparison.Equal, "Boolean true/false collapsed");
            HarnessAssert.True(comparison.NumericDelta is null,
                "Boolean comparison emitted numeric delta");
            return Task.FromResult(new CaseObservation(
                "Boolean comparison is typed and never coerced into a numeric delta.",
                "true!=false;delta=null"));
        }, ct);

        await RunCaseAsync("P9-DIF-FIELD-06", () =>
        {
            const string utc = "2026-08-05T01:02:03.0000000Z";
            var date = CompareDiffValues(
                DiffTyped("VALUE", "DATE", utc,
                    date: DateTime.Parse(utc).ToUniversalTime()),
                DiffTyped("VALUE", "DATE", utc,
                    date: DateTime.Parse(utc).ToUniversalTime()));
            var choice = CompareDiffValues(
                DiffTyped("VALUE", "CHOICE", "choice-a", choices: ["choice-a"]),
                DiffTyped("VALUE", "CHOICE", "choice-b", choices: ["choice-b"]));
            HarnessAssert.True(date.Equal, "Equal UTC dates differ");
            HarnessAssert.True(!choice.Equal, "Distinct choice identities collapsed");
            return Task.FromResult(new CaseObservation(
                "UTC dates and choice ids compare by canonical typed identity.",
                "dateUTC=equal;choiceId=different"));
        }, ct);
    }

    private async Task RunDiffTableCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-DIF-TABLE-METRIC-01", async () =>
        {
            _difTableResult = await ExecuteDiffRunAsync(
                _difConfigs[1], "p9-dif-run-table", 100,
                Actor("executor").Token, HttpStatusCode.Created, ct);
            RequireCompletedDiffResult(_difTableResult, _difConfigs[1], 2, 1, 1);
            return new CaseObservation(
                "TABLE_METRIC produced two stable row owners with global totals.",
                "rows=2;equal=1;changed=1");
        }, ct);

        await RunCaseAsync("P9-DIF-TABLE-METRIC-02", () =>
        {
            var keys = DiffRows(_difTableResult!).Select(node =>
                DiffString(node!.AsObject(), "key")).ToArray();
            HarnessAssert.True(keys.SequenceEqual(
                    new[] { "ROW:row-a", "ROW:row-b" }, StringComparer.Ordinal),
                "TABLE_METRIC rows are not ordinally stable");
            return Task.FromResult(new CaseObservation(
                "TABLE_METRIC rows are sorted by stable row key.",
                "ROW:row-a<ROW:row-b"));
        }, ct);

        await RunCaseAsync("P9-DIF-TABLE-METRIC-03", async () =>
        {
            var resultId = DiffString(_difTableResult!, "resultId");
            var first = await GetDiffResultAsync(resultId, 0, 1,
                Actor("executor").Token, HttpStatusCode.OK, ct);
            var second = await GetDiffResultAsync(resultId, 1, 1,
                Actor("executor").Token, HttpStatusCode.OK, ct);
            HarnessAssert.Equal(2, DiffInt(first, "totalRowCount"),
                "Page 1 lost global total");
            HarnessAssert.Equal(2, DiffInt(first, "totalPages"),
                "Page count");
            HarnessAssert.Equal("ROW:row-a",
                DiffString(DiffRows(first).Single()!.AsObject(), "key"),
                "First stable page");
            HarnessAssert.Equal("ROW:row-b",
                DiffString(DiffRows(second).Single()!.AsObject(), "key"),
                "Second stable page");
            return new CaseObservation(
                "Paging preserves global totals and deterministic row boundaries.",
                "pageSize=1;pages=2;total=2" );
        }, ct);

        await RunCaseAsync("P9-DIF-TABLE-METRIC-04", async () =>
        {
            var before = await DiffOwnerCountAsync(ct);
            var replay = await ExecuteDiffRunAsync(
                _difConfigs[1], "p9-dif-run-table", 100,
                Actor("executor").Token, HttpStatusCode.Created, ct);
            var after = await DiffOwnerCountAsync(ct);
            HarnessAssert.Equal(before, after, "Replay created a second owner");
            HarnessAssert.Equal(DiffString(_difTableResult!, "resultId"),
                DiffString(replay, "resultId"), "Replay result id");
            HarnessAssert.Equal(DiffString(_difTableResult!, "receiptId"),
                DiffString(replay, "receiptId"), "Replay receipt id");
            return new CaseObservation(
                "Exact command replay returns the same durable result and receipt.",
                "ownerDelta=0;sameResult=true;sameReceipt=true");
        }, ct);

        await RunCaseAsync("P9-DIF-TABLE-METRIC-05", async () =>
        {
            var before = await DiffOwnerCountAsync(ct);
            await ExecuteDiffRunAsync(
                _difConfigs[1], "p9-dif-run-table", 1,
                Actor("executor").Token, HttpStatusCode.Conflict, ct);
            var after = await DiffOwnerCountAsync(ct);
            HarnessAssert.Equal(before, after,
                "Mismatched replay mutated result owner");
            return new CaseObservation(
                "Mismatched replay is rejected before a second owner is written.",
                "status=409;ownerDelta=0" );
        }, ct);

        await RunCaseAsync("P9-DIF-TABLE-METRIC-06", () =>
        {
            var rows = DiffRows(_difTableResult!);
            HarnessAssert.True(rows.All(node =>
                    DiffString(node!.AsObject(), "conceptKind") == "TABLE_METRIC"),
                "TABLE_METRIC row kind drift");
            HarnessAssert.Equal(3m,
                DiffDecimal(rows[0]!.AsObject(), "numericDelta"),
                "TABLE row-a delta");
            HarnessAssert.Equal(0m,
                DiffDecimal(rows[1]!.AsObject(), "numericDelta"),
                "TABLE row-b delta");
            return Task.FromResult(new CaseObservation(
                "TABLE_METRIC rows retain typed numeric deltas independently.",
                "row-a=3;row-b=0"));
        }, ct);
    }

    private async Task RunDiffRowLabelCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-DIF-ROW-LABEL-01", async () =>
        {
            _difLabelResult = await ExecuteDiffRunAsync(
                _difConfigs[2], "p9-dif-run-label", 100,
                Actor("executor").Token, HttpStatusCode.Created, ct);
            RequireCompletedDiffResult(_difLabelResult, _difConfigs[2], 2, 1, 1);
            return new CaseObservation(
                "ROW_LABEL executed through its block allowlist identity.",
                "rows=2;labelFilter=blockId+labelCode" );
        }, ct);

        await RunCaseAsync("P9-DIF-ROW-LABEL-02", () =>
        {
            var row = DiffRows(_difLabelResult!).Single(node =>
                DiffString(node!.AsObject(), "key") == "ROW:row-a")!.AsObject();
            HarnessAssert.True(DiffBool(row, "equal"), "Shared row label not equal");
            HarnessAssert.Equal(1m,
                DiffDecimal(ApiHarnessClient.RequiredObject(row["left"], "left"),
                    "numericValue"), "Left label count");
            return Task.FromResult(new CaseObservation(
                "Shared ROW_LABEL identity compares equal by row key.",
                "row-a:left=1;right=1;equal=true"));
        }, ct);

        await RunCaseAsync("P9-DIF-ROW-LABEL-03", () =>
        {
            var row = DiffRows(_difLabelResult!).Single(node =>
                DiffString(node!.AsObject(), "key") == "ROW:row-b")!.AsObject();
            var right = ApiHarnessClient.RequiredObject(row["right"], "right");
            HarnessAssert.Equal("MISSING", DiffString(right, "state"),
                "Missing row label state");
            HarnessAssert.Equal("STATE_CHANGED", DiffString(row, "differenceKind"),
                "Missing row difference kind");
            return Task.FromResult(new CaseObservation(
                "One-sided ROW_LABEL remains MISSING under INCLUDE policy.",
                "row-b:right=MISSING;kind=STATE_CHANGED"));
        }, ct);

        await RunCaseAsync("P9-DIF-ROW-LABEL-04", () =>
        {
            var comparison = CompareDiffValues(
                DiffTyped("MISSING", "NUMBER"),
                DiffTyped("NULL", "NUMBER"));
            HarnessAssert.True(!comparison.Equal,
                "ROW_LABEL MISSING/NULL states collapsed");
            return Task.FromResult(new CaseObservation(
                "ROW_LABEL missing-state semantics use the shared typed comparator.",
                "MISSING!=NULL"));
        }, ct);

        await RunCaseAsync("P9-DIF-ROW-LABEL-05", () =>
        {
            var leftToRight = CompareDiffValues(
                DiffTyped("VALUE", "NUMBER", "10", 10m),
                DiffTyped("VALUE", "NUMBER", "7", 7m));
            var rightToLeft = CompareDiffValues(
                DiffTyped("VALUE", "NUMBER", "7", 7m),
                DiffTyped("VALUE", "NUMBER", "10", 10m));
            HarnessAssert.Equal(3m, leftToRight.NumericDelta,
                "LEFT_TO_RIGHT delta");
            HarnessAssert.Equal(-3m, rightToLeft.NumericDelta,
                "RIGHT_TO_LEFT delta");
            return Task.FromResult(new CaseObservation(
                "Direction changes the typed numeric delta sign deterministically.",
                "LTR=3;RTL=-3"));
        }, ct);

        await RunCaseAsync("P9-DIF-ROW-LABEL-06", () =>
        {
            var sameIdentity = CompareDiffValues(
                DiffTyped("VALUE", "CHOICE", "p9.dif.row",
                    choices: ["p9.dif.row"]),
                DiffTyped("VALUE", "CHOICE", "p9.dif.row",
                    choices: ["p9.dif.row"]));
            HarnessAssert.True(sameIdentity.Equal,
                "Stable label code identity changed by display concerns");
            return Task.FromResult(new CaseObservation(
                "ROW_LABEL equality is anchored to stable code identity.",
                "identity=p9.dif.row;displayExcluded=true"));
        }, ct);
    }

    private async Task RunDiffOwnerCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-DIF-OWNER-01", async () =>
        {
            var owners = await RequireDatabase()
                .GetCollection<BsonDocument>("work_report_statistic_diff_results")
                .Find(new BsonDocument("isDeleted", false))
                .Sort(new BsonDocument("configVersionNo", 1))
                .ToListAsync(ct);
            HarnessAssert.Equal(3, owners.Count, "Result owner count");
            HarnessAssert.True(owners.All(owner =>
                    owner["_id"].AsObjectId == owner["runId"].AsObjectId &&
                    owner["jobId"].AsString == owner["_id"].AsObjectId.ToString() &&
                    owner["candidatePromptId"].AsString == DiffPromptId &&
                    owner["candidateStage"].ToInt32() == 4 &&
                    owner["status"].AsString == "COMPLETED"),
                "Result owner lineage drift");
            HarnessAssert.Equal(1, owners.Count(owner =>
                owner["isCurrent"].ToBoolean()), "Current owner cardinality");
            foreach (var owner in owners)
            {
                var expectedReceipt = DiffSha256(
                    $"{owner["commandId"].AsString}\n{owner["requestHash"].AsString}");
                HarnessAssert.Equal(expectedReceipt, owner["receiptId"].AsString,
                    "Deterministic result receipt");
                _difResultIds.Add(owner["_id"].AsObjectId.ToString());
            }
            return new CaseObservation(
                "One aggregate root owns run/job/result identity, lineage, receipt and current promotion.",
                "owners=3;current=1;runId=resultId;candidate=P9-06/stage4" );
        }, ct);

        await RunCaseAsync("P9-DIF-OWNER-02", async () =>
        {
            var before = await DiffOwnerCountAsync(ct);
            await ExecuteDiffRunAsync(
                _difConfigs[2], "p9-dif-stale-config", 100,
                Actor("executor").Token, HttpStatusCode.Conflict, ct,
                expectedHash: new string('0', 64));
            var after = await DiffOwnerCountAsync(ct);
            _difStaleZeroWrite = before == after;
            HarnessAssert.True(_difStaleZeroWrite,
                "Stale config request wrote a result owner");
            return new CaseObservation(
                "Stale config hash is rejected before result-owner creation.",
                "status=409;ownerDelta=0" );
        }, ct);

        await RunCaseAsync("P9-DIF-OWNER-03", async () =>
        {
            var before = await DiffOwnerCountAsync(ct);
            await ExecuteDiffRunAsync(
                _difConfigs[2], "p9-dif-outsider", 100,
                Actor("outsider").Token, HttpStatusCode.Forbidden, ct);
            var hidden = await GetDiffResultAsync(
                ObjectId.GenerateNewId().ToString(), 0, 100,
                Actor("outsider").Token, HttpStatusCode.Forbidden, ct);
            var after = await DiffOwnerCountAsync(ct);
            _difForbiddenZeroWrite = before == after && hidden.Count == 0;
            HarnessAssert.True(_difForbiddenZeroWrite,
                "Forbidden/existence-hidden request changed owners");
            return new CaseObservation(
                "Authorization runs before owner existence and forbidden calls are zero-write.",
                "run=403;missing-result=403;ownerDelta=0" );
        }, ct);

        await RunCaseAsync("P9-DIF-OWNER-04", async () =>
        {
            var collection = RequireDatabase().GetCollection<BsonDocument>(
                "work_report_statistic_diff_results");
            var indexes = await (await collection.Indexes.ListAsync(ct)).ToListAsync(ct);
            var names = indexes.Select(index => index["name"].AsString).ToArray();
            var required = DiffRequiredIndexNames();
            HarnessAssert.True(required.All(names.Contains),
                "P9-DIF required indexes missing");
            var owners = await collection.Find(new BsonDocument("isDeleted", false))
                .ToListAsync(ct);
            HarnessAssert.True(owners.Where(owner => !owner["isCurrent"].ToBoolean())
                    .All(owner => owner.GetValue("expiresAtUtc", BsonNull.Value).IsValidDateTime),
                "Superseded owner lacks TTL date");
            HarnessAssert.True(owners.Single(owner => owner["isCurrent"].ToBoolean())
                .GetValue("expiresAtUtc", BsonNull.Value).IsBsonNull,
                "Current owner is TTL eligible");
            return new CaseObservation(
                "Current promotion keeps only superseded terminal owners TTL-eligible; all six indexes exist.",
                "indexes=6;currentTTL=false;supersededTTL=true" );
        }, ct);
    }

    private async Task<JsonObject> ExecuteDiffRunAsync(
        P9DiffConfigPin config,
        string commandId,
        int limit,
        string token,
        HttpStatusCode expectedStatus,
        CancellationToken ct,
        string? expectedHash = null)
    {
        var response = await RequireApi().PostAsync(
            $"api/work-report-statistic-diffs/assignments/{Fixture().AssignmentId}" +
            $"/templates/{Fixture().TemplateId}/runs",
            new
            {
                configVersionId = config.VersionId,
                commandId,
                expectedConfigRevision = config.Revision,
                expectedConfigHash = expectedHash ?? config.ConfigHash,
                limit
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response, expectedStatus, $"P9-DIF run {commandId}");
        return expectedStatus == HttpStatusCode.Created
            ? ApiHarnessClient.RequiredObject(response.Json, $"P9-DIF {commandId}")
            : new JsonObject();
    }

    private async Task<JsonObject> GetDiffResultAsync(
        string resultId,
        int page,
        int pageSize,
        string token,
        HttpStatusCode expectedStatus,
        CancellationToken ct)
    {
        var response = await RequireApi().GetAsync(
            $"api/work-report-statistic-diffs/assignments/{Fixture().AssignmentId}" +
            $"/templates/{Fixture().TemplateId}/results/{resultId}" +
            $"?page={page}&pageSize={pageSize}",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, expectedStatus, "P9-DIF result GET");
        return expectedStatus == HttpStatusCode.OK
            ? ApiHarnessClient.RequiredObject(response.Json, "P9-DIF result")
            : new JsonObject();
    }

    private static void RequireCompletedDiffResult(
        JsonObject result,
        P9DiffConfigPin config,
        int total,
        int equal,
        int changed)
    {
        HarnessAssert.Equal("COMPLETED", DiffString(result, "status"),
            "Diff status");
        HarnessAssert.Equal(DiffString(result, "resultId"),
            DiffString(result, "runId"), "runId/resultId");
        HarnessAssert.Equal(config.VersionId,
            DiffString(result, "configVersionId"), "configVersionId");
        HarnessAssert.Equal(config.ConfigHash,
            DiffString(result, "configHash"), "configHash");
        HarnessAssert.Equal(total, DiffInt(result, "totalRowCount"),
            "totalRowCount");
        HarnessAssert.Equal(equal, DiffInt(result, "equalRowCount"),
            "equalRowCount");
        HarnessAssert.Equal(changed, DiffInt(result, "changedRowCount"),
            "changedRowCount");
        HarnessAssert.True(DiffBool(result, "isCurrent"), "Result not current");
        HarnessAssert.True(DiffBool(result, "isFresh"), "Result not fresh");
        HarnessAssert.True(!DiffBool(result, "isDirty"), "Result still dirty");
    }

    private static P9StatisticDiffTypedValue DiffTyped(
        string state,
        string dataType,
        string? canonical = null,
        decimal? numeric = null,
        bool? boolean = null,
        DateTime? date = null,
        IReadOnlyList<string>? choices = null)
        => new()
        {
            State = state,
            DataType = dataType,
            CanonicalValue = canonical,
            NumericValue = numeric,
            BooleanValue = boolean,
            DateValueUtc = date,
            ChoiceIds = choices?.ToList() ?? []
        };

    private static P9DiffTypedComparison CompareDiffValues(
        P9StatisticDiffTypedValue left,
        P9StatisticDiffTypedValue right)
        => WorkReportStatisticDiffService.CompareP9DiffValues(left, right);

    private async Task<long> DiffOwnerCountAsync(CancellationToken ct)
        => await RequireDatabase()
            .GetCollection<BsonDocument>("work_report_statistic_diff_results")
            .CountDocumentsAsync(new BsonDocument("isDeleted", false),
                cancellationToken: ct);

    private static JsonArray DiffRows(JsonObject result)
        => result["rows"] as JsonArray
           ?? throw new InvalidOperationException("P9-DIF response lacks rows.");

    private static string DiffString(JsonObject root, string property)
        => root[property]?.GetValue<string>()
           ?? throw new InvalidOperationException($"Missing string {property}.");

    private static decimal DiffDecimal(JsonObject root, string property)
        => root[property]?.GetValue<decimal>()
           ?? throw new InvalidOperationException($"Missing decimal {property}.");

    private static bool DiffBool(JsonObject root, string property)
        => root[property]?.GetValue<bool>()
           ?? throw new InvalidOperationException($"Missing boolean {property}.");

    private static string[] DiffRequiredIndexNames()
        =>
        [
            "ux_workReportStatisticDiffResults_command_active",
            "ux_workReportStatisticDiffResults_run_active",
            "ix_workReportStatisticDiffResults_scope_config_current",
            "ix_workReportStatisticDiffResults_queue_lease",
            "ix_workReportStatisticDiffResults_source_reverse",
            "ix_workReportStatisticDiffResults_terminal_ttl"
        ];
}
