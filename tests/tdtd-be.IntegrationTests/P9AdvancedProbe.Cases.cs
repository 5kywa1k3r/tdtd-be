using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunAdvancedCasesAsync(CancellationToken ct)
    {
        await RunAdvancedDayCasesAsync(ct);
        await RunAdvancedMonthCasesAsync(ct);
        await RunAdvancedYearCasesAsync(ct);
        await RunAdvancedBudgetCasesAsync(ct);
    }

    private async Task RunAdvancedDayCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-ADV-DAY-01", async () =>
        {
            var response = await RequestAdvancedBuildAsync(
                "day", "2026-08-01", "p9-adv-day-01", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(
                response, HttpStatusCode.Accepted, "P9-ADV DAY build");
            _advDay = await WaitAdvancedNodeAsync(
                "work_assignment_advanced_summary_day_nodes",
                "2026-08-01", "CLEAN", ct);
            HarnessAssert.Equal(1L, _advDay["sourceReportCount"].ToInt64(),
                "DAY approved source count");
            return AdvancedObservation(
                "DAY source worker built the approved report exactly once.", _advDay);
        }, ct);

        await RunCaseAsync("P9-ADV-DAY-02", async () =>
        {
            var response = await RequestAdvancedBuildAsync(
                "day", "2026-08-02", "p9-adv-day-02", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV empty DAY build");
            var node = await WaitAdvancedNodeAsync(
                "work_assignment_advanced_summary_day_nodes",
                "2026-08-02", "CLEAN", ct);
            HarnessAssert.Equal(0L, node["sourceReportCount"].ToInt64(),
                "Empty DAY report count");
            HarnessAssert.Equal(
                new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
                node["windowStartUtc"].ToUniversalTime(),
                "DAY UTC boundary");
            return AdvancedObservation(
                "Empty DAY is an explicit clean zero node with UTC boundaries.", node);
        }, ct);

        await RunCaseAsync("P9-ADV-DAY-03", async () =>
        {
            var before = await CountAdvancedNodesAsync(
                "work_assignment_advanced_summary_day_nodes", "2026-08-01", ct);
            var receipt = BsonRequiredText(_advDay!, "buildReceiptId");
            var response = await RequestAdvancedBuildAsync(
                "day", "2026-08-01", "p9-adv-day-01", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV DAY replay");
            var replay = await LoadAdvancedNodeAsync(
                "work_assignment_advanced_summary_day_nodes", "2026-08-01", ct);
            HarnessAssert.Equal(before, await CountAdvancedNodesAsync(
                    "work_assignment_advanced_summary_day_nodes", "2026-08-01", ct),
                "DAY replay cardinality");
            HarnessAssert.Equal(receipt, BsonRequiredText(replay, "buildReceiptId"),
                "DAY replay receipt");
            return AdvancedObservation(
                "Duplicate DAY command replayed one owner/receipt without another job.", replay);
        }, ct);

        await RunCaseAsync("P9-ADV-DAY-04", async () =>
        {
            var before = await CountAdvancedNodesAsync(
                "work_assignment_advanced_summary_day_nodes", "2026-07-01", ct);
            var body = AdvancedBuildBody("p9-adv-day-stale", false);
            body["expectedConfigRevision"] = _advConfigRevision + 1;
            var response = await RequireApi().PostAsync(
                AdvancedBuildRoute("day", "2026-07-01"), body,
                Actor("executor").Token, ct: ct);
            HarnessAssert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
                "Stale DAY config pin was not rejected");
            HarnessAssert.Equal(before, await CountAdvancedNodesAsync(
                    "work_assignment_advanced_summary_day_nodes", "2026-07-01", ct),
                "Stale DAY zero write");
            return new CaseObservation(
                "Stale config revision rejected before owner/job writes.",
                $"status={(int)response.StatusCode};writes=0");
        }, ct);

        await RunCaseAsync("P9-ADV-DAY-05", async () =>
        {
            var before = await CountAdvancedNodesAsync(
                "work_assignment_advanced_summary_day_nodes", "2026-08-01", ct);
            var response = await RequestAdvancedBuildAsync(
                "day", "2026-08-01", "p9-adv-day-01", true,
                Actor("executor").Token, ct);
            HarnessAssert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
                "Mismatched DAY replay was not rejected");
            HarnessAssert.Equal(before, await CountAdvancedNodesAsync(
                    "work_assignment_advanced_summary_day_nodes", "2026-08-01", ct),
                "Mismatched replay zero write");
            return new CaseObservation(
                "Same command with a different request hash failed closed.",
                $"status={(int)response.StatusCode};writes=0");
        }, ct);

        await RunCaseAsync("P9-ADV-DAY-06", () =>
        {
            var node = _advDay ?? throw new InvalidOperationException("DAY node unavailable");
            HarnessAssert.Equal(ChainId, BsonRequiredText(node, "candidateChainId"),
                "DAY candidate chain");
            HarnessAssert.Equal(AdvancedPromptId, BsonRequiredText(node, "candidatePromptId"),
                "DAY candidate prompt");
            HarnessAssert.Equal(3, node["candidateStage"].ToInt32(),
                "DAY candidate stage");
            HarnessAssert.Equal(_advVersionId, BsonRequiredText(node, "configVersionId"),
                "DAY config version");
            HarnessAssert.True(node.GetValue("leaseOwner", BsonNull.Value).IsBsonNull,
                "Terminal DAY retained worker lease");
            return Task.FromResult(AdvancedObservation(
                "Terminal DAY pins candidate/config lineage and releases its lease.", node));
        }, ct);
    }

    private async Task RunAdvancedMonthCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-ADV-MONTH-01", async () =>
        {
            await EnsureAugustDayNodesAsync(ct);
            var response = await RequestAdvancedBuildAsync(
                "month", "2026-08", "p9-adv-month-01", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV MONTH build");
            _advMonth = await WaitAdvancedNodeAsync(
                "work_assignment_advanced_summary_month_nodes",
                "2026-08", "CLEAN", ct);
            HarnessAssert.Equal(31, _advMonth["inputNodeKeys"].AsBsonArray.Count,
                "MONTH exact DAY inputs");
            return AdvancedObservation(
                "MONTH worker rolled up the exact 31 August DAY owners.", _advMonth);
        }, ct);

        await RunCaseAsync("P9-ADV-MONTH-02", () =>
        {
            var month = _advMonth!;
            var keys = month["inputNodeKeys"].AsBsonArray.Select(x => x.AsString).ToArray();
            HarnessAssert.Equal(31, keys.Distinct(StringComparer.Ordinal).Count(),
                "MONTH duplicate DAY inputs");
            HarnessAssert.Equal("2026-08-01", keys[0], "MONTH first DAY");
            HarnessAssert.Equal("2026-08-31", keys[^1], "MONTH last DAY");
            return Task.FromResult(AdvancedObservation(
                "MONTH input keys are ordered, complete and duplicate-free.", month));
        }, ct);

        await RunCaseAsync("P9-ADV-MONTH-03", async () =>
        {
            var hash = BsonRequiredText(_advMonth!, "valueHash");
            var response = await RequestAdvancedBuildAsync(
                "month", "2026-08", "p9-adv-month-01", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV MONTH replay");
            var replay = await LoadAdvancedNodeAsync(
                "work_assignment_advanced_summary_month_nodes", "2026-08", ct);
            HarnessAssert.Equal(hash, BsonRequiredText(replay, "valueHash"),
                "MONTH replay hash");
            return AdvancedObservation(
                "MONTH replay preserved deterministic value and receipt hashes.", replay);
        }, ct);

        await RunCaseAsync("P9-ADV-MONTH-04", async () =>
        {
            var response = await RequestAdvancedBuildAsync(
                "month", "2026-09", "p9-adv-month-missing", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV missing-child MONTH");
            var failed = await WaitAdvancedNodeAsync(
                "work_assignment_advanced_summary_month_nodes",
                "2026-09", "FAILED", ct);
            HarnessAssert.True(BsonRequiredText(failed, "buildError")
                    .Contains("child", StringComparison.OrdinalIgnoreCase),
                "MONTH missing-child telemetry");
            return AdvancedObservation(
                "MONTH with missing DAY children reached explicit FAILED telemetry.", failed);
        }, ct);

        await RunCaseAsync("P9-ADV-MONTH-05", async () =>
        {
            var months = RequireDatabase().GetCollection<BsonDocument>(
                "work_assignment_advanced_summary_month_nodes");
            await months.UpdateOneAsync(
                new BsonDocument("_id", _advMonth!["_id"]),
                Builders<BsonDocument>.Update.Set("isDirty", true).Set("status", "DIRTY"),
                cancellationToken: ct);
            var query = await QueryAdvancedAsync("2026-08-01", "2026-08-31", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(query, HttpStatusCode.OK,
                "P9-ADV dirty MONTH query");
            var root = ApiHarnessClient.RequiredObject(query.Json, "dirty MONTH query");
            var selected = root["selectedNodes"]?.AsArray()
                           ?? throw new InvalidOperationException("selectedNodes missing");
            HarnessAssert.Equal(31, selected.Count,
                "Dirty MONTH did not fall back to clean DAY nodes");
            HarnessAssert.True(selected.All(item =>
                    item?["grain"]?.GetValue<string>() == "DAY"),
                "Dirty MONTH returned old MONTH as current");
            await months.UpdateOneAsync(
                new BsonDocument("_id", _advMonth["_id"]),
                Builders<BsonDocument>.Update.Set("isDirty", false).Set("status", "CLEAN"),
                cancellationToken: ct);
            return new CaseObservation(
                "Dirty MONTH was not returned current; query fell back to 31 clean DAY nodes.",
                "selected=31;grain=DAY");
        }, ct);

        await RunCaseAsync("P9-ADV-MONTH-06", async () =>
        {
            var indexes = await ListAdvancedIndexesAsync(
                "work_assignment_advanced_summary_month_nodes", ct);
            HarnessAssert.True(indexes.Any(name => name.Contains("scope_grain", StringComparison.Ordinal)),
                "MONTH identity index missing");
            HarnessAssert.True(indexes.Any(name => name.Contains("lease", StringComparison.Ordinal)),
                "MONTH lease index missing");
            return new CaseObservation(
                "MONTH identity/range/lease indexes exist in the isolated database.",
                string.Join(',', indexes));
        }, ct);
    }

    private async Task RunAdvancedYearCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-ADV-YEAR-01", async () =>
        {
            await SeedRemaining2026MonthsAsync(ct);
            var response = await RequestAdvancedBuildAsync(
                "year", "2026", "p9-adv-year-01", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV YEAR build");
            _advYear = await WaitAdvancedNodeAsync(
                "work_assignment_advanced_summary_year_nodes", "2026", "CLEAN", ct);
            HarnessAssert.Equal(12, _advYear["inputNodeKeys"].AsBsonArray.Count,
                "YEAR exact MONTH inputs");
            return AdvancedObservation(
                "YEAR worker rolled up the exact 12 clean MONTH owners.", _advYear);
        }, ct);

        await RunCaseAsync("P9-ADV-YEAR-02", () =>
        {
            var (start, end) = AdvancedSummaryHierarchyKeyHelper.GetYearBoundsUtc("2024");
            HarnessAssert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                start, "Leap YEAR start");
            HarnessAssert.Equal(366d, (end - start).TotalDays, "Leap YEAR day count");
            return Task.FromResult(new CaseObservation(
                "UTC Gregorian leap-year boundary contains exactly 366 days.",
                $"start={start:O};end={end:O};days=366"));
        }, ct);

        await RunCaseAsync("P9-ADV-YEAR-03", async () =>
        {
            var hash = BsonRequiredText(_advYear!, "valueHash");
            var response = await RequestAdvancedBuildAsync(
                "year", "2026", "p9-adv-year-01", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV YEAR replay");
            var replay = await LoadAdvancedNodeAsync(
                "work_assignment_advanced_summary_year_nodes", "2026", ct);
            HarnessAssert.Equal(hash, BsonRequiredText(replay, "valueHash"),
                "YEAR replay hash");
            return AdvancedObservation(
                "YEAR replay preserved deterministic value and receipt hashes.", replay);
        }, ct);

        await RunCaseAsync("P9-ADV-YEAR-04", async () =>
        {
            var response = await RequestAdvancedBuildAsync(
                "year", "2027", "p9-adv-year-missing", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                "P9-ADV missing-child YEAR");
            var failed = await WaitAdvancedNodeAsync(
                "work_assignment_advanced_summary_year_nodes", "2027", "FAILED", ct);
            return AdvancedObservation(
                "YEAR with missing MONTH children failed without a current value.", failed);
        }, ct);

        await RunCaseAsync("P9-ADV-YEAR-05", () =>
        {
            var year = _advYear!;
            HarnessAssert.True(year.GetValue("leaseOwner", BsonNull.Value).IsBsonNull,
                "YEAR terminal lease retained");
            HarnessAssert.True(year["fenceToken"].ToInt64() >= 1,
                "YEAR fence token missing");
            HarnessAssert.Equal(1L, year["buildAttemptNo"].ToInt64(),
                "YEAR replay created another attempt");
            return Task.FromResult(AdvancedObservation(
                "YEAR CAS winner released its lease; replay created no second attempt.", year));
        }, ct);

        await RunCaseAsync("P9-ADV-YEAR-06", async () =>
        {
            var response = await QueryAdvancedAsync(
                "2026-01-01", "2026-12-31", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
                "P9-ADV YEAR query");
            var root = ApiHarnessClient.RequiredObject(response.Json, "YEAR query");
            HarnessAssert.Equal("READY", root["status"]!.GetValue<string>(),
                "YEAR query status");
            var selected = root["selectedNodes"]!.AsArray();
            HarnessAssert.Equal(1, selected.Count, "YEAR query selected cardinality");
            HarnessAssert.Equal("YEAR", selected[0]!["grain"]!.GetValue<string>(),
                "YEAR query largest clean grain");
            return new CaseObservation(
                "Full-year query selected the one clean YEAR owner.",
                "status=READY;selected=YEAR:2026");
        }, ct);
    }

    private async Task RunAdvancedBudgetCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-ADV-BUDGET-01", async () =>
        {
            var entries = await RequireDatabase()
                .GetCollection<BsonDocument>("work_summary_token_ledgers")
                .Find(new BsonDocument
                {
                    ["recordKind"] = "ENTRY",
                    ["tokenKind"] = "ADVANCED_SUMMARY_BROAD_HISTORICAL_BUILD",
                    ["outcome"] = "SUCCESS",
                    ["isDeleted"] = false
                }).ToListAsync(ct);
            HarnessAssert.True(entries.Count >= 3, "Broad-build token entries missing");
            HarnessAssert.Equal(entries.Count,
                entries.Select(x => BsonRequiredText(x, "requestTokenId"))
                    .Distinct(StringComparer.Ordinal).Count(),
                "Broad-build token replay duplication");
            return new CaseObservation(
                "Each accepted broad build consumed one idempotent ledger entry.",
                $"entries={entries.Count};kind=ADVANCED_SUMMARY_BROAD_HISTORICAL_BUILD");
        }, ct);

        await RunCaseAsync("P9-ADV-BUDGET-02", async () =>
        {
            var before = await CountAllAdvancedNodesAsync(ct);
            var response = await QueryAdvancedAsync(
                "2010-01-01", "2025-12-31", true,
                Actor("executor").Token, ct);
            HarnessAssert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
                "Over-range query was not rejected");
            HarnessAssert.Equal(before, await CountAllAdvancedNodesAsync(ct),
                "Over-range query changed official owners");
            return new CaseObservation(
                "Query above 3660 DAYs failed before enqueue/owner writes.",
                $"status={(int)response.StatusCode};writes=0");
        }, ct);

        await RunCaseAsync("P9-ADV-BUDGET-03", async () =>
        {
            var before = await CountAllAdvancedNodesAsync(ct);
            var response = await QueryAdvancedAsync(
                "2026-01-01", "2026-12-31", false,
                Actor("outsider").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden,
                "P9-ADV cross-unit query");
            HarnessAssert.Equal(before, await CountAllAdvancedNodesAsync(ct),
                "Cross-unit query changed official owners");
            return new CaseObservation(
                "Cross-unit actor was forbidden with zero hierarchy writes.",
                "status=403;writes=0");
        }, ct);

        await RunCaseAsync("P9-ADV-BUDGET-04", async () =>
        {
            var pool = await RequireDatabase()
                .GetCollection<BsonDocument>("work_summary_token_ledgers")
                .Find(new BsonDocument
                {
                    ["recordKind"] = "POOL",
                    ["ownerUnitId"] = ObjectId.Parse(Fixture().UnitAId),
                    ["tokenKind"] = "ADVANCED_SUMMARY_BROAD_HISTORICAL_BUILD",
                    ["isDeleted"] = false
                }).SingleAsync(ct);
            var remaining = pool["monthlyQuota"].ToInt32() - pool["usedUnits"].ToInt32();
            for (var index = 0; index < remaining; index++)
            {
                var accepted = await RequestAdvancedBuildAsync(
                    "month", $"{2040 + index:0000}-01",
                    $"p9-adv-budget-fill-{index:00}", false,
                    Actor("executor").Token, ct);
                ApiHarnessClient.ExpectStatus(accepted, HttpStatusCode.Accepted,
                    "P9-ADV quota fill");
            }
            var before = await CountAllAdvancedNodesAsync(ct);
            var denied = await RequestAdvancedBuildAsync(
                "month", "2099-12", "p9-adv-budget-denied", false,
                Actor("executor").Token, ct);
            HarnessAssert.True(denied.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
                "Exhausted broad quota was not rejected");
            HarnessAssert.Equal(before, await CountAllAdvancedNodesAsync(ct),
                "Quota denial made a partial official node");
            _advQuotaDenied = true;
            return new CaseObservation(
                "Exhausted broad-build quota rejected the next node with zero partial promotion.",
                $"remainingBeforeFill={remaining};status={(int)denied.StatusCode};writes=0");
        }, ct);
    }

    private string AdvancedBuildRoute(string grain, string key)
        => $"api/work-assignment-advanced-summary/configs/{_advVersionId}/" +
           $"hierarchy/{grain}/{key}/build";

    private JsonObject AdvancedBuildBody(string commandId, bool forceRefresh)
        => new()
        {
            ["commandId"] = commandId,
            ["forceRefresh"] = forceRefresh,
            ["expectedConfigRevision"] = _advConfigRevision,
            ["expectedConfigHash"] = _advConfigHash
        };

    private Task<ApiHarnessResponse> RequestAdvancedBuildAsync(
        string grain,
        string key,
        string commandId,
        bool forceRefresh,
        string token,
        CancellationToken ct)
        => RequireApi().PostAsync(
            AdvancedBuildRoute(grain, key),
            AdvancedBuildBody(commandId, forceRefresh),
            token,
            ct: ct);

    private Task<ApiHarnessResponse> QueryAdvancedAsync(
        string startDayKey,
        string endDayKey,
        bool enqueueMissing,
        string token,
        CancellationToken ct)
        => RequireApi().PostAsync(
            $"api/work-assignment-advanced-summary/configs/{_advVersionId}/hierarchy/query",
            new JsonObject
            {
                ["startDayKey"] = startDayKey,
                ["endDayKey"] = endDayKey,
                ["enqueueMissing"] = enqueueMissing,
                ["commandId"] = $"p9-adv-query-{startDayKey}-{endDayKey}"
            },
            token,
            ct: ct);

    private async Task EnsureAugustDayNodesAsync(CancellationToken ct)
    {
        for (var day = 3; day <= 31; day++)
        {
            var key = $"2026-08-{day:00}";
            var response = await RequestAdvancedBuildAsync(
                "day", key, $"p9-adv-day-{day:00}", false,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
                $"P9-ADV DAY warm {key}");
        }
        await WaitAdvancedNodeCountAsync(
            "work_assignment_advanced_summary_day_nodes", 31, "CLEAN", ct);
    }

    private async Task SeedRemaining2026MonthsAsync(CancellationToken ct)
    {
        var collection = RequireDatabase().GetCollection<BsonDocument>(
            "work_assignment_advanced_summary_month_nodes");
        for (var month = 1; month <= 12; month++)
        {
            if (month == 8)
                continue;
            var key = $"2026-{month:00}";
            var start = new DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var clone = (BsonDocument)_advMonth!.DeepClone();
            var existing = await collection.Find(AdvancedNodeFilter(key))
                .FirstOrDefaultAsync(ct);
            clone["_id"] = existing?["_id"] ?? ObjectId.GenerateNewId();
            clone["grainKey"] = key;
            clone["monthKey"] = key;
            clone["yearKey"] = "2026";
            clone["windowStartUtc"] = start;
            clone["windowEndExclusiveUtc"] = start.AddMonths(1);
            clone["inputNodeKeys"] = new BsonArray(
                Enumerable.Range(1, DateTime.DaysInMonth(2026, month))
                    .Select(day => $"{key}-{day:00}"));
            clone["sourceSignatureHash"] = Sha256Text($"fixture:{key}");
            clone["buildCommandId"] = $"p9-adv-fixture-{key}";
            clone["buildRequestHash"] = Sha256Text($"request:{key}");
            clone["buildReceiptId"] = Sha256Text($"receipt:{key}");
            clone["buildJobId"] = $"fixture:{key}";
            clone["buildCorrelationId"] = ObjectId.GenerateNewId().ToString();
            clone["createdAtUtc"] = DateTime.UtcNow;
            clone["updatedAtUtc"] = DateTime.UtcNow;
            clone["builtAtUtc"] = DateTime.UtcNow;
            await collection.ReplaceOneAsync(
                AdvancedNodeFilter(key),
                clone,
                new ReplaceOptions { IsUpsert = true },
                ct);
        }
    }

    private async Task<BsonDocument> WaitAdvancedNodeAsync(
        string collectionName,
        string grainKey,
        string expectedStatus,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        BsonDocument? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RequireDatabase().GetCollection<BsonDocument>(collectionName)
                .Find(AdvancedNodeFilter(grainKey)).FirstOrDefaultAsync(ct);
            if (last is not null &&
                string.Equals(last.GetValue("status", "").AsString,
                    expectedStatus, StringComparison.Ordinal))
                return last;
            if (last is not null &&
                string.Equals(expectedStatus, "CLEAN", StringComparison.Ordinal) &&
                string.Equals(last.GetValue("status", "").AsString,
                    "FAILED", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"P9-ADV node {collectionName}/{grainKey} failed: " +
                    last.GetValue("buildError", "").AsString);
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException(
            $"P9-ADV node {collectionName}/{grainKey} did not reach {expectedStatus}; last={last}");
    }

    private async Task WaitAdvancedNodeCountAsync(
        string collectionName,
        int count,
        string status,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var actual = await RequireDatabase().GetCollection<BsonDocument>(collectionName)
                .CountDocumentsAsync(new BsonDocument
                {
                    ["configVersionId"] = ObjectId.Parse(_advVersionId),
                    ["status"] = status,
                    ["isDeleted"] = false
                }, cancellationToken: ct);
            if (actual == count)
                return;
            await Task.Delay(250, ct);
        }
        throw new TimeoutException(
            $"P9-ADV {collectionName} did not reach {count} {status} nodes.");
    }

    private BsonDocument AdvancedNodeFilter(string grainKey)
        => new()
        {
            ["configVersionId"] = ObjectId.Parse(_advVersionId),
            ["grainKey"] = grainKey,
            ["isDeleted"] = false
        };

    private async Task<BsonDocument> LoadAdvancedNodeAsync(
        string collectionName,
        string grainKey,
        CancellationToken ct)
        => await RequireDatabase().GetCollection<BsonDocument>(collectionName)
               .Find(AdvancedNodeFilter(grainKey)).SingleAsync(ct);

    private Task<long> CountAdvancedNodesAsync(
        string collectionName,
        string grainKey,
        CancellationToken ct)
        => RequireDatabase().GetCollection<BsonDocument>(collectionName)
            .CountDocumentsAsync(AdvancedNodeFilter(grainKey), cancellationToken: ct);

    private async Task<long> CountAllAdvancedNodesAsync(CancellationToken ct)
    {
        long count = 0;
        foreach (var name in new[]
                 {
                     "work_assignment_advanced_summary_day_nodes",
                     "work_assignment_advanced_summary_month_nodes",
                     "work_assignment_advanced_summary_year_nodes"
                 })
        {
            count += await RequireDatabase().GetCollection<BsonDocument>(name)
                .CountDocumentsAsync(
                    new BsonDocument("configVersionId", ObjectId.Parse(_advVersionId)),
                    cancellationToken: ct);
        }
        return count;
    }

    private async Task<string[]> ListAdvancedIndexesAsync(
        string collectionName,
        CancellationToken ct)
        => (await (await RequireDatabase().GetCollection<BsonDocument>(collectionName)
                    .Indexes.ListAsync(ct)).ToListAsync(ct))
            .Select(x => x["name"].AsString)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    private static CaseObservation AdvancedObservation(
        string detail,
        BsonDocument node)
        => new(
            detail,
            $"grain={node.GetValue("grain", "")};" +
            $"key={node.GetValue("grainKey", "")};" +
            $"status={node.GetValue("status", "")};" +
            $"reports={node.GetValue("sourceReportCount", 0).ToInt64()};" +
            $"hash={node.GetValue("valueHash", "")}");
}
