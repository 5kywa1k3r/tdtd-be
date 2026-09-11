using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP805BarrierCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-010",
            "system_admin",
            ["p8-adv-010-draft", "p8-adv-010-lock"],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("011");
                var (_, draft) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-010-draft",
                    AdvancedPayload(fixture, targetCount: 2),
                    ct);
                var ownerBefore = await ReadAdvancedOwnerDocumentAsync(
                    draft.VersionId,
                    ct);
                var previewBefore = AdvancedPreviewProjection(ownerBefore);
                var reportCountBefore = await _database
                    .GetCollection<BsonDocument>("work_assignment_reports")
                    .CountDocumentsAsync(
                        FilterDefinition<BsonDocument>.Empty,
                        cancellationToken: ct);
                var (_, locked) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "lock",
                    "p8-adv-010-lock",
                    draft,
                    ct);
                var ownerAfter = await ReadAdvancedOwnerDocumentAsync(
                    locked.VersionId,
                    ct);
                HarnessAssert.Equal(previewBefore,
                    AdvancedPreviewProjection(ownerAfter),
                    "P8 lock changed/read-derived preview state");
                HarnessAssert.Equal(reportCountBefore,
                    await _database
                        .GetCollection<BsonDocument>("work_assignment_reports")
                        .CountDocumentsAsync(
                            FilterDefinition<BsonDocument>.Empty,
                            cancellationToken: ct),
                    "P8 lock changed report cardinality");
                var receipt = locked.ValidationReceipt
                              ?? throw new InvalidOperationException(
                                  "P8 lock validation receipt is absent");
                HarnessAssert.Equal(false, RequiredBool(receipt, "previewRead"),
                    "P8 lock reported a preview read");
                HarnessAssert.Equal(false, RequiredBool(receipt, "previewWrite"),
                    "P8 lock reported a preview write");
                return new CaseObservation(
                    "P8 lock preserved every preview field and report owner while receipt attested zero preview reads/writes.",
                    "lock=configOnly;previewProjection=stable;reports=stable;receipt.previewRead=false;receipt.previewWrite=false");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-011",
            "system_admin",
            ["p8-adv-011-draft", "p8-adv-011-preview-barrier"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("012");
                var (_, draft) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-011-draft",
                    AdvancedPayload(fixture),
                    ct);
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-011/legacy-preview",
                    () => _api.PostAsync(
                        $"api/work-assignment-advanced-summary/configs/" +
                        $"{draft.VersionId}/preview",
                        new JsonObject { ["forceRefresh"] = true },
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    null,
                    "P8_CONFIG_ONLY",
                    ct);
                return new CaseObservation(
                    "The legacy latest-three-period preview writer was a hard P9 barrier with whole-database zero delta.",
                    "legacyPreview=409+0W;previewJob=0;previewResult=0;runtime=P9");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-012",
            "system_admin",
            [
                "p8-adv-012-draft",
                "p8-adv-012-day-build",
                "p8-adv-012-month-build",
                "p8-adv-012-year-build",
                "p8-adv-012-query"
            ],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("013");
                var (_, draft) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-012-draft",
                    AdvancedPayload(fixture),
                    ct);
                var probes = new (string Id, string Route, JsonObject Body)[]
                {
                    (
                        "p8-adv-012-day-build",
                        $"api/work-assignment-advanced-summary/configs/" +
                        $"{draft.VersionId}/hierarchy/day/2026-08-02/build",
                        new JsonObject { ["forceRefresh"] = true }),
                    (
                        "p8-adv-012-month-build",
                        $"api/work-assignment-advanced-summary/configs/" +
                        $"{draft.VersionId}/hierarchy/month/2026-08/build",
                        new JsonObject { ["forceRefresh"] = true }),
                    (
                        "p8-adv-012-year-build",
                        $"api/work-assignment-advanced-summary/configs/" +
                        $"{draft.VersionId}/hierarchy/year/2026/build",
                        new JsonObject { ["forceRefresh"] = true }),
                    (
                        "p8-adv-012-query",
                        $"api/work-assignment-advanced-summary/configs/" +
                        $"{draft.VersionId}/hierarchy/query",
                        new JsonObject
                        {
                            ["startDayKey"] = "2026-08-01",
                            ["endDayKey"] = "2026-08-02",
                            ["enqueueMissing"] = true
                        })
                };
                foreach (var probe in probes)
                {
                    await RequireZeroWriteAdvancedRejectionAsync(
                        $"P8-ADV-012/{probe.Id}",
                        () => _api.PostAsync(
                            probe.Route,
                            probe.Body,
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.Conflict,
                        "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                        null,
                        "P8_CONFIG_ONLY",
                        ct);
                }
                foreach (var collection in new[]
                         {
                             "work_assignment_advanced_summary_day_nodes",
                             "work_assignment_advanced_summary_month_nodes",
                             "work_assignment_advanced_summary_year_nodes"
                         })
                {
                    HarnessAssert.Equal(0L,
                        await _database.GetCollection<BsonDocument>(collection)
                            .CountDocumentsAsync(
                                FilterDefinition<BsonDocument>.Empty,
                                cancellationToken: ct),
                        $"P8 hierarchy barrier left {collection} rows");
                }
                return new CaseObservation(
                    "Legacy DAY/MONTH/YEAR build and hierarchy query paths remained P9 barriers and persisted zero nodes.",
                    "day+month+year+query=409+0W;dayNodes=0;monthNodes=0;yearNodes=0;jobs=0");
            },
            ct);
    }

    private async Task<BsonDocument> ReadAdvancedOwnerDocumentAsync(
        string configId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(AdvancedConfigsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id", ObjectId.Parse(configId)))
            .SingleAsync(ct);

    private static string AdvancedPreviewProjection(BsonDocument owner)
    {
        var projection = new BsonDocument();
        foreach (var name in new[]
                 {
                     "previewStatus",
                     "previewJobId",
                     "previewCorrelationId",
                     "previewPeriodKeys",
                     "previewResultJson",
                     "previewError",
                     "previewRequestedAtUtc",
                     "previewFinishedAtUtc"
                 })
        {
            projection[name] = owner.GetValue(name, BsonNull.Value);
        }
        return projection.ToJson();
    }
}
