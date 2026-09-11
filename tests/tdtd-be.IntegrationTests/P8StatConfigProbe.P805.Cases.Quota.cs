using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private P8AdvancedConfigIdentity? _p805QuotaV1Locked;
    private P8AdvancedConfigIdentity? _p805QuotaV2Locked;
    private P8AdvancedConfigIdentity? _p805QuotaV2Archived;

    private async Task RunP805QuotaCasesAsync(CancellationToken ct)
    {
        await RunP805FirstFreeLockCaseAsync(ct);
        await RunP805LaterLockAndGrantCaseAsync(ct);
        await RunP805ConcurrentQuotaCaseAsync(ct);
        await RunP805ArchiveCaseAsync(ct);
        await RunP805HistoricalCaseAsync(ct);
    }

    private async Task RunP805FirstFreeLockCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-013",
            "unit_manager_a",
            ["p8-adv-013-draft", "p8-adv-013-first-lock"],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("unit_manager_a");
                var fixture = AdvancedFixture("quota-a");
                var beforeQuota = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(2, beforeQuota.BaseMonthlyQuota,
                    "Unit A monthly base is not exact active-user cardinality");
                HarnessAssert.Equal(0, beforeQuota.UsedUnits,
                    "Unit A quota was not clean before first lock");
                var (_, draft) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-013-draft",
                    AdvancedPayload(fixture, targetCount: 2),
                    ct);
                var (_, locked) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "lock",
                    "p8-adv-013-first-lock",
                    draft,
                    ct);
                _p805QuotaV1Locked = locked;
                var afterQuota = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(beforeQuota.UsedUnits, afterQuota.UsedUnits,
                    "First Advanced lock consumed monthly quota");
                HarnessAssert.Equal(beforeQuota.RemainingUnits,
                    afterQuota.RemainingUnits,
                    "First Advanced lock changed remaining quota");
                var free = await ReadConfigTokenEntryAsync(
                    locked.VersionId,
                    locked.VersionNo,
                    "FREE",
                    ct);
                HarnessAssert.Equal(0, BsonInt(free, "units"),
                    "First Advanced lock FREE entry units mismatch");
                HarnessAssert.Equal(actor.UnitId,
                    BsonString(free, "ownerUnitId"),
                    "First lock FREE entry owner unit mismatch");
                var validationReceipt = locked.ValidationReceipt
                                        ?? throw new InvalidOperationException(
                                            "First lock validation receipt is absent");
                HarnessAssert.Equal(
                    RequiredString(validationReceipt, "receiptId"),
                    BsonString(free, "requestTokenId"),
                    "First lock FREE entry is not bound to validation receipt");
                return new CaseObservation(
                    "The first locked Advanced version persisted one FREE ledger entry and consumed zero monthly quota.",
                    "firstLock=FREE;units=0;used=0>0;remaining=2>2;baseActiveUsers=2;ownerUnit=A");
            },
            ct);
    }

    private async Task RunP805LaterLockAndGrantCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-014",
            "unit_manager_a+system_admin",
            [
                "p8-adv-014-next-draft",
                "p8-adv-014-later-lock",
                "p8-adv-014-grant"
            ],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("unit_manager_a");
                var admin = Actor("system_admin");
                var fixture = AdvancedFixture("quota-a");
                var v1 = _p805QuotaV1Locked
                         ?? throw new HarnessCaseNotRunnableException(
                             "P8-ADV-013 did not produce locked v1");
                var quotaBefore = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                var (_, next) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "next-draft",
                    "p8-adv-014-next-draft",
                    v1,
                    ct);
                var lockEnvelope = Envelope(
                    "p8-adv-014-later-lock",
                    next.Revision,
                    next.ConfigHash,
                    AdvancedActionPayload());
                var lockResponse = await _api.PostAsync(
                    $"{AdvancedConfigRoute(fixture)}/lock",
                    lockEnvelope,
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(lockResponse, HttpStatusCode.OK,
                    "P8 later Advanced lock");
                var locked = ParseAdvancedIdentity(
                    lockResponse.Json,
                    requireCommandReceipt: true);
                RequireAdvancedIdentityContract(locked, fixture);
                await RequireDirectAdvancedIdentityAsync(
                    locked,
                    fixture,
                    "p8-adv-014-later-lock",
                    "LOCK_ADVANCED_SUMMARY_CONFIG",
                    actor.Id,
                    requirePersisted: true,
                    ct);
                _p805QuotaV2Locked = locked;
                var quotaAfterLock = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(quotaBefore.UsedUnits + 1,
                    quotaAfterLock.UsedUnits,
                    "Later Advanced lock did not consume exactly one quota");
                var consume = await ReadConfigTokenEntryAsync(
                    locked.VersionId,
                    locked.VersionNo,
                    "CONSUME",
                    ct);
                HarnessAssert.Equal(1, BsonInt(consume, "units"),
                    "Later Advanced lock consume units mismatch");
                var validationReceipt = locked.ValidationReceipt
                                        ?? throw new InvalidOperationException(
                                            "Later lock validation receipt is absent");
                HarnessAssert.Equal(
                    RequiredString(validationReceipt, "receiptId"),
                    BsonString(consume, "requestTokenId"),
                    "Later lock consume is not bound to validation receipt");

                var retryBefore = await CaptureDatabaseSnapshotAsync(ct);
                var retry = await _api.PostAsync(
                    $"{AdvancedConfigRoute(fixture)}/lock",
                    lockEnvelope,
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(retry, HttpStatusCode.OK,
                    "P8 later lock exact retry");
                HarnessAssert.Equal(Canonicalize(locked.Raw),
                    Canonicalize(ApiHarnessClient.RequiredObject(
                        retry.Json,
                        "P8 later lock replay")),
                    "P8 later lock exact retry response drifted");
                VerifyCollectionContract(
                    "P8-ADV-014/lock-retry",
                    BuildDeltas(retryBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());

                var grantPoolBefore = await ReadTokenQuotaAsync(
                    admin,
                    actor.UnitId,
                    ct);
                var grantEnvelope = Envelope(
                    "p8-adv-014-grant",
                    grantPoolBefore.Revision,
                    grantPoolBefore.PoolHash,
                    TokenGrantPayload(1, "P8-ADV-014 exact admin grant"));
                var grantResponse = await _api.PostAsync(
                    TokenPoolGrantRoute(actor.UnitId),
                    grantEnvelope,
                    admin.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(grantResponse, HttpStatusCode.OK,
                    "P8 admin token grant");
                var grant = ParseTokenMutation(
                    grantResponse.Json,
                    compensation: false);
                RequireTokenMutationContract(grant, actor.UnitId, 1);
                await RequireDirectTokenEntryAsync(
                    grant.LedgerId,
                    "GRANT",
                    actor.UnitId,
                    grant.CommandReceiptId,
                    admin.Id,
                    1,
                    ct);
                var afterGrant = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(grantPoolBefore.GrantedUnits + 1,
                    afterGrant.GrantedUnits,
                    "Admin grant did not add exactly one quota unit");

                var grantReplayBefore = await CaptureDatabaseSnapshotAsync(ct);
                var grantReplay = await _api.PostAsync(
                    TokenPoolGrantRoute(actor.UnitId),
                    grantEnvelope,
                    admin.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(grantReplay, HttpStatusCode.OK,
                    "P8 grant exact replay");
                HarnessAssert.Equal(Canonicalize(grant.Raw),
                    Canonicalize(ApiHarnessClient.RequiredObject(
                        grantReplay.Json,
                        "P8 grant replay response")),
                    "P8 grant exact replay response drifted");
                VerifyCollectionContract(
                    "P8-ADV-014/grant-replay",
                    BuildDeltas(grantReplayBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-014/grant-divergent-replay",
                    () => _api.PostAsync(
                        TokenPoolGrantRoute(actor.UnitId),
                        Envelope(
                            "p8-adv-014-grant",
                            grantPoolBefore.Revision,
                            grantPoolBefore.PoolHash,
                            TokenGrantPayload(2, "divergent")),
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                    null,
                    null,
                    ct);

                var nextMonth = DateTime.UtcNow.AddMonths(1)
                    .ToString("yyyy-MM");
                var boundaryBefore = await CaptureDatabaseSnapshotAsync(ct);
                var nextMonthQuota = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct,
                    nextMonth);
                HarnessAssert.Equal(2, nextMonthQuota.BaseMonthlyQuota,
                    "Next-month base active-user quota mismatch");
                HarnessAssert.Equal(0, nextMonthQuota.UsedUnits,
                    "Next-month derived reset retained used quota");
                HarnessAssert.Equal(0, nextMonthQuota.GrantedUnits,
                    "Current-month grant leaked across month boundary");
                VerifyCollectionContract(
                    "P8-ADV-014/month-boundary-reset",
                    BuildDeltas(boundaryBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                return new CaseObservation(
                    "Later user lock consumed one, monthly base equaled two active users, admin grant added one, and retry/reset/month boundary cost zero.",
                    "laterLock=CONSUME1;activeUsers=base2;grant=+1;lockRetry=0W;grantReplay=0W;divergent=409+0W;nextMonthUsed=0+0W");
            },
            ct);
    }

    private async Task RunP805ConcurrentQuotaCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-015",
            "unit_manager_a+system_admin",
            [
                "p8-adv-015-race-a-draft",
                "p8-adv-015-race-a-free",
                "p8-adv-015-race-a-next",
                "p8-adv-015-race-a-consume",
                "p8-adv-015-race-b-draft",
                "p8-adv-015-race-b-free",
                "p8-adv-015-race-b-next",
                "p8-adv-015-race-b-consume",
                "p8-adv-015-race-c-draft",
                "p8-adv-015-race-c-free",
                "p8-adv-015-race-c-next",
                "p8-adv-015-race-c-consume",
                "p8-adv-015-compensate"
            ],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("unit_manager_a");
                var admin = Actor("system_admin");
                var prepared = new List<(
                    P8AdvancedFixture Fixture,
                    P8AdvancedConfigIdentity Draft,
                    string LockCommand)>();
                foreach (var suffix in new[] { "a", "b", "c" })
                {
                    var fixture = AdvancedFixture($"race-{suffix}");
                    var (_, draft) = await PutAdvancedConfigAsync(
                        actor,
                        fixture,
                        $"p8-adv-015-race-{suffix}-draft",
                        AdvancedPayload(fixture),
                        ct);
                    var (_, firstLocked) = await PostAdvancedActionAsync(
                        actor,
                        fixture,
                        "lock",
                        $"p8-adv-015-race-{suffix}-free",
                        draft,
                        ct);
                    var (_, next) = await PostAdvancedActionAsync(
                        actor,
                        fixture,
                        "next-draft",
                        $"p8-adv-015-race-{suffix}-next",
                        firstLocked,
                        ct);
                    prepared.Add((
                        fixture,
                        next,
                        $"p8-adv-015-race-{suffix}-consume"));
                }

                var quotaBefore = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(2, quotaBefore.RemainingUnits,
                    "Concurrent N/N+1 fixture does not have exact N=2 remaining");
                var responses = await Task.WhenAll(prepared.Select(item =>
                    _api.PostAsync(
                        $"{AdvancedConfigRoute(item.Fixture)}/lock",
                        Envelope(
                            item.LockCommand,
                            item.Draft.Revision,
                            item.Draft.ConfigHash,
                            AdvancedActionPayload()),
                        actor.Token,
                        ct: ct)));
                var winners = responses
                    .Select((response, index) => (Response: response, Index: index))
                    .Where(item => item.Response.StatusCode == HttpStatusCode.OK)
                    .ToArray();
                var losers = responses
                    .Select((response, index) => (Response: response, Index: index))
                    .Where(item => item.Response.StatusCode != HttpStatusCode.OK)
                    .ToArray();
                HarnessAssert.Equal(2, winners.Length,
                    "Concurrent quota N=2 did not produce exactly two winners");
                HarnessAssert.Equal(1, losers.Length,
                    "Concurrent quota N+1 did not produce exactly one loser");
                ExpectAdvancedFailure(
                    losers.Single().Response,
                    HttpStatusCode.Conflict,
                    "WORK_SUMMARY_TOKEN_QUOTA_EXCEEDED");

                var winnerIdentities = new List<P8AdvancedConfigIdentity>();
                foreach (var winner in winners)
                {
                    var item = prepared[winner.Index];
                    var identity = ParseAdvancedIdentity(
                        winner.Response.Json,
                        requireCommandReceipt: true);
                    RequireAdvancedIdentityContract(identity, item.Fixture);
                    await RequireDirectAdvancedIdentityAsync(
                        identity,
                        item.Fixture,
                        item.LockCommand,
                        "LOCK_ADVANCED_SUMMARY_CONFIG",
                        actor.Id,
                        requirePersisted: true,
                        ct);
                    winnerIdentities.Add(identity);
                }
                var loserItem = prepared[losers.Single().Index];
                var loserReadback = await ReadAdvancedConfigAsync(
                    actor,
                    loserItem.Fixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal("DRAFT", loserReadback.Status,
                    "Concurrent quota loser partially locked");
                HarnessAssert.Equal(0L,
                    await CountConfigTokenEntriesAsync(
                        loserReadback.VersionId,
                        loserReadback.VersionNo,
                        "CONSUME",
                        ct),
                    "Concurrent quota loser left a consume entry");
                HarnessAssert.Equal(0L,
                    await CountAdvancedReceiptsAsync(
                        loserReadback.OwnerId,
                        loserItem.LockCommand,
                        ct),
                    "Concurrent quota loser left a command receipt");
                var quotaFull = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(quotaFull.MonthlyQuota,
                    quotaFull.UsedUnits,
                    "Concurrent winners did not fill exact monthly quota");
                HarnessAssert.Equal(0, quotaFull.RemainingUnits,
                    "Concurrent winners left unexpected quota");

                var compensatedConfig = winnerIdentities[0];
                var consume = await ReadConfigTokenEntryAsync(
                    compensatedConfig.VersionId,
                    compensatedConfig.VersionNo,
                    "CONSUME",
                    ct);
                var consumeId = BsonString(consume, "_id")
                                ?? throw new InvalidOperationException(
                                    "Concurrent consume entry id missing");
                var validationReceipt = compensatedConfig.ValidationReceipt
                                        ?? throw new InvalidOperationException(
                                            "Winning lock validation receipt is absent");
                HarnessAssert.Equal(
                    RequiredString(validationReceipt, "receiptId"),
                    BsonString(consume, "requestTokenId"),
                    "Winning CONSUME entry is not bound to validation receipt");
                var compensationPoolBefore = await ReadTokenQuotaAsync(
                    admin,
                    actor.UnitId,
                    ct);
                var compensationEnvelope = Envelope(
                    "p8-adv-015-compensate",
                    compensationPoolBefore.Revision,
                    compensationPoolBefore.PoolHash,
                    TokenCompensationPayload(
                        "P8-ADV-015 exact orphan compensation"));
                var committedBefore = await CaptureDatabaseSnapshotAsync(ct);
                var committedResponse = await _api.PostAsync(
                    TokenCompensationRoute(consumeId),
                    compensationEnvelope,
                    admin.Token,
                    ct: ct);
                ExpectAdvancedFailure(
                    committedResponse,
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    "$.compensatedLedgerId",
                    "TOKEN_ENTRY_NOT_ORPHAN");
                VerifyCollectionContract(
                    "P8-ADV-015/committed-compensation-rejection",
                    BuildDeltas(
                        committedBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());

                var sourceVersionId = BsonString(consume, "configId")
                                      ?? throw new InvalidOperationException(
                                          "Concurrent consume version identity missing");
                HarnessAssert.Equal(compensatedConfig.VersionId,
                    sourceVersionId,
                    "Concurrent consume does not bind the winning version row");
                HarnessAssert.Equal(compensatedConfig.ConfigHash,
                    BsonString(consume, "configHash"),
                    "Concurrent consume config hash drifted from winning version");
                var advancedVersions = _database.GetCollection<BsonDocument>(
                    AdvancedConfigsCollection);
                var exactWinningVersionFilter =
                    Builders<BsonDocument>.Filter.Eq(
                        "_id",
                        ObjectId.Parse(compensatedConfig.VersionId)) &
                    Builders<BsonDocument>.Filter.Eq(
                        "configId",
                        ObjectId.Parse(compensatedConfig.ConfigId)) &
                    Builders<BsonDocument>.Filter.Eq(
                        "versionNo",
                        compensatedConfig.VersionNo) &
                    Builders<BsonDocument>.Filter.Eq(
                        "lockTokenId",
                        ObjectId.Parse(consumeId)) &
                    Builders<BsonDocument>.Filter.Eq(
                        "configHash",
                        compensatedConfig.ConfigHash) &
                    Builders<BsonDocument>.Filter.Eq(
                        "status",
                        "LOCKED") &
                    Builders<BsonDocument>.Filter.Ne("isDeleted", true);
                HarnessAssert.Equal(1L,
                    await advancedVersions.CountDocumentsAsync(
                        exactWinningVersionFilter,
                        cancellationToken: ct),
                    "Winning Advanced version row is not exactly committed");
                var deletedOwner = await advancedVersions.DeleteOneAsync(
                    exactWinningVersionFilter,
                    ct);
                HarnessAssert.Equal(1L, deletedOwner.DeletedCount,
                    "Exact test-owned winning version row was not deleted");
                HarnessAssert.Equal(0L,
                    await advancedVersions.CountDocumentsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "_id",
                            ObjectId.Parse(compensatedConfig.VersionId)),
                        cancellationToken: ct),
                    "Winning token entry was not made a verified orphan");

                var compensationResponse = await _api.PostAsync(
                    TokenCompensationRoute(consumeId),
                    compensationEnvelope,
                    admin.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    compensationResponse,
                    HttpStatusCode.OK,
                    "P8 exact orphan quota compensation");
                var compensation = ParseTokenMutation(
                    compensationResponse.Json,
                    compensation: true);
                HarnessAssert.Equal(consumeId,
                    compensation.CompensatedLedgerId,
                    "Compensation target mismatch");
                await RequireDirectTokenEntryAsync(
                    compensation.LedgerId,
                    "COMPENSATE",
                    actor.UnitId,
                    compensation.CommandReceiptId,
                    admin.Id,
                    1,
                    ct);
                var quotaCompensated = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(quotaFull.UsedUnits - 1,
                    quotaCompensated.UsedUnits,
                    "Exact compensation did not decrement used quota once");
                HarnessAssert.Equal(1, quotaCompensated.RemainingUnits,
                    "Exact compensation did not restore one remaining unit");

                var replayBefore = await CaptureDatabaseSnapshotAsync(ct);
                var compensationReplay = await _api.PostAsync(
                    TokenCompensationRoute(consumeId),
                    compensationEnvelope,
                    admin.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    compensationReplay,
                    HttpStatusCode.OK,
                    "P8 compensation exact replay");
                HarnessAssert.Equal(Canonicalize(compensation.Raw),
                    Canonicalize(ApiHarnessClient.RequiredObject(
                        compensationReplay.Json,
                        "P8 compensation replay")),
                    "Compensation exact replay response drifted");
                VerifyCollectionContract(
                    "P8-ADV-015/compensation-replay",
                    BuildDeltas(replayBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-015/compensation-divergent",
                    () => _api.PostAsync(
                        TokenCompensationRoute(consumeId),
                        Envelope(
                            "p8-adv-015-compensate",
                            compensationPoolBefore.Revision,
                            compensationPoolBefore.PoolHash,
                            TokenCompensationPayload("divergent")),
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                    null,
                    null,
                    ct);
                return new CaseObservation(
                    "Concurrent N=2/N+1 locking produced two atomic consumes and one zero-write loser; committed compensation failed closed before exact orphan compensation restored one unit once.",
                    "remainingN=2;concurrent=2x200+1x409;loser=config+receipt+ledger0;committedCompensate=409+0W;orphan=directDeleteExactVersion;compensate=used-1;replay=0W;divergent=409+0W");
            },
            ct);
    }

    private async Task RunP805ArchiveCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-016",
            "unit_manager_a",
            ["p8-adv-016-archive"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("unit_manager_a");
                var fixture = AdvancedFixture("quota-a");
                var locked = _p805QuotaV2Locked
                             ?? throw new HarnessCaseNotRunnableException(
                                 "P8-ADV-014 did not produce locked v2");
                var quotaBefore = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                var tokenCountBefore = await CountTokenRecordsAsync(ct);
                var (_, archived) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "archive",
                    "p8-adv-016-archive",
                    locked,
                    ct);
                _p805QuotaV2Archived = archived;
                HarnessAssert.Equal("ARCHIVED", archived.Status,
                    "Advanced v2 did not archive");
                HarnessAssert.Equal(locked.VersionId, archived.VersionId,
                    "Archive replaced Advanced version identity");
                HarnessAssert.Equal(locked.ConfigHash, archived.ConfigHash,
                    "Archive changed Advanced configHash");
                var quotaAfter = await ReadTokenQuotaAsync(
                    actor,
                    actor.UnitId,
                    ct);
                HarnessAssert.Equal(quotaBefore.UsedUnits, quotaAfter.UsedUnits,
                    "Archive refunded consumed quota");
                HarnessAssert.Equal(tokenCountBefore,
                    await CountTokenRecordsAsync(ct),
                    "Archive wrote a token ledger record");
                return new CaseObservation(
                    "LOCKED→ARCHIVED preserved immutable version/hash and did not refund or mutate token quota.",
                    "status=ARCHIVED;versionStable=true;hashStable=true;usedStable=true;ledgerDelta=0");
            },
            ct);
    }

    private async Task RunP805HistoricalCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-017",
            "unit_manager_a",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("unit_manager_a");
                var fixture = AdvancedFixture("quota-a");
                var v1 = _p805QuotaV1Locked
                         ?? throw new HarnessCaseNotRunnableException(
                             "Locked Advanced v1 is unavailable");
                var v2 = _p805QuotaV2Archived
                         ?? throw new HarnessCaseNotRunnableException(
                             "Archived Advanced v2 is unavailable");
                var versionsResponse = await _api.GetAsync(
                    $"{AdvancedConfigRoute(fixture)}/versions",
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(versionsResponse,
                    HttpStatusCode.OK,
                    "P8 Advanced versions GET");
                var root = ApiHarnessClient.RequiredObject(
                    versionsResponse.Json,
                    "P8 Advanced versions response");
                HarnessAssert.Equal("ADVANCED_SUMMARY",
                    RequiredString(root, "ownerKind"),
                    "Advanced versions ownerKind mismatch");
                HarnessAssert.Equal(v2.ConfigId,
                    RequiredString(root, "configId"),
                    "Advanced versions configId mismatch");
                var versions = root["versions"] as JsonArray
                               ?? throw new InvalidOperationException(
                                   "P8 Advanced versions response lacks versions");
                HarnessAssert.Equal(2, versions.Count,
                    "Advanced historical version count mismatch");
                var readV1 = await ReadAdvancedVersionAsync(
                    actor,
                    fixture,
                    1,
                    ct);
                var readV2 = await ReadAdvancedVersionAsync(
                    actor,
                    fixture,
                    2,
                    ct);
                HarnessAssert.Equal(v1.ConfigId, readV1.ConfigId,
                    "Historical Advanced v1 configId drifted");
                HarnessAssert.Equal(v1.VersionId, readV1.VersionId,
                    "Historical Advanced v1 versionId drifted");
                HarnessAssert.Equal(v1.ConfigHash, readV1.ConfigHash,
                    "Historical Advanced v1 hash drifted");
                HarnessAssert.Equal("ARCHIVED", readV1.Status,
                    "Historical Advanced v1 status drifted");
                HarnessAssert.Equal(null, readV1.PreviousVersionId,
                    "Historical Advanced v1 previousVersionId is non-null");
                HarnessAssert.Equal(v2.ConfigId, readV2.ConfigId,
                    "Historical Advanced v2 configId drifted");
                HarnessAssert.Equal(v2.VersionId, readV2.VersionId,
                    "Historical Advanced v2 versionId drifted");
                HarnessAssert.Equal(v2.ConfigHash, readV2.ConfigHash,
                    "Historical Advanced v2 hash drifted");
                HarnessAssert.Equal("ARCHIVED", readV2.Status,
                    "Historical Advanced v2 status drifted");
                HarnessAssert.Equal(v1.VersionId, readV2.PreviousVersionId,
                    "Historical Advanced v2 lineage drifted");
                HarnessAssert.Equal(v1.ConfigId, v2.ConfigId,
                    "Advanced historical versions do not share configId");
                var versionRows = await _database
                    .GetCollection<BsonDocument>(AdvancedConfigsCollection)
                    .Find(Builders<BsonDocument>.Filter.Eq(
                              "configId",
                              ObjectId.Parse(v2.ConfigId)) &
                          Builders<BsonDocument>.Filter.Ne("isDeleted", true))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("versionNo"))
                    .ToListAsync(ct);
                HarnessAssert.Equal(2, versionRows.Count,
                    "Direct Advanced version-row count mismatch");
                var mongoV1 = versionRows.Single(
                    row => BsonInt(row, "versionNo") == 1);
                var mongoV2 = versionRows.Single(
                    row => BsonInt(row, "versionNo") == 2);
                HarnessAssert.Equal(v1.ConfigId,
                    BsonString(mongoV1, "configId"),
                    "Direct Advanced v1 configId drifted");
                HarnessAssert.Equal(v2.ConfigId,
                    BsonString(mongoV2, "configId"),
                    "Direct Advanced v2 configId drifted");
                HarnessAssert.Equal(v1.VersionId,
                    BsonString(mongoV1, "_id"),
                    "Direct Advanced v1 versionId drifted");
                HarnessAssert.Equal(v2.VersionId,
                    BsonString(mongoV2, "_id"),
                    "Direct Advanced v2 versionId drifted");
                HarnessAssert.Equal(null,
                    BsonString(mongoV1, "previousVersionId"),
                    "Direct Advanced v1 previousVersionId is non-null");
                HarnessAssert.Equal(v1.VersionId,
                    BsonString(mongoV2, "previousVersionId"),
                    "Direct Advanced v2 lineage drifted");
                HarnessAssert.Equal(v1.ConfigHash,
                    BsonString(mongoV1, "configHash"),
                    "Direct Advanced v1 hash drifted");
                HarnessAssert.Equal(v2.ConfigHash,
                    BsonString(mongoV2, "configHash"),
                    "Direct Advanced v2 hash drifted");
                HarnessAssert.Equal("ARCHIVED",
                    BsonString(mongoV1, "status"),
                    "Direct Advanced v1 status drifted");
                HarnessAssert.Equal("ARCHIVED",
                    BsonString(mongoV2, "status"),
                    "Direct Advanced v2 status drifted");
                return new CaseObservation(
                    "Version list/detail and direct Mongo preserved exactly two immutable ARCHIVED version rows with shared config identity, hashes and lineage.",
                    "versions=2;v1=ARCHIVED+hashStable;v2=ARCHIVED+hashStable;v2.previous=v1;mongoVersionRows=2;writes=0");
            },
            ct);
    }

    private async Task<BsonDocument> ReadConfigTokenEntryAsync(
        string versionId,
        int versionNo,
        string direction,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(TokenLedgersCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("recordKind", "ENTRY") &
                  Builders<BsonDocument>.Filter.Eq(
                      "configId", ObjectId.Parse(versionId)) &
                  Builders<BsonDocument>.Filter.Eq(
                      "configVersionNo", versionNo) &
                  Builders<BsonDocument>.Filter.Eq("direction", direction))
            .SingleAsync(ct);

    private async Task<long> CountConfigTokenEntriesAsync(
        string versionId,
        int versionNo,
        string direction,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(TokenLedgersCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("recordKind", "ENTRY") &
                Builders<BsonDocument>.Filter.Eq(
                    "configId", ObjectId.Parse(versionId)) &
                Builders<BsonDocument>.Filter.Eq(
                    "configVersionNo", versionNo) &
                Builders<BsonDocument>.Filter.Eq("direction", direction),
                cancellationToken: ct);

    private async Task<long> CountAdvancedReceiptsAsync(
        string ownerId,
        string commandId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("ownerId", ownerId) &
                Builders<BsonDocument>.Filter.Eq("commandId", commandId),
                cancellationToken: ct);

    private async Task<long> CountTokenRecordsAsync(CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(TokenLedgersCollection)
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);

    private async Task<P805AdvancedVersionIdentity> ReadAdvancedVersionAsync(
        P8Actor actor,
        P8AdvancedFixture fixture,
        int versionNo,
        CancellationToken ct)
    {
        var response = await _api.GetAsync(
            $"{AdvancedConfigRoute(fixture)}/versions/{versionNo}",
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            $"P8 Advanced version {versionNo} GET");
        var identity = ParseP805AdvancedVersionIdentity(response.Json);
        RequireP805AdvancedVersionIdentity(identity, fixture);
        return identity;
    }

    private static P805AdvancedVersionIdentity
        ParseP805AdvancedVersionIdentity(JsonNode? node)
    {
        var root = ApiHarnessClient.RequiredObject(
            node,
            "P8 Advanced version response");
        var identityNode = root["identity"] as JsonObject
                           ?? throw new InvalidOperationException(
                               "P8 Advanced version response lacks identity");
        var pins = identityNode["dependencyPins"] as JsonArray
                   ?? throw new InvalidOperationException(
                       "P8 Advanced version identity lacks dependencyPins");
        return new P805AdvancedVersionIdentity(
            RequiredString(identityNode, "ownerKind"),
            RequiredString(identityNode, "ownerId"),
            RequiredString(identityNode, "configId"),
            RequiredString(identityNode, "versionId"),
            RequiredInt(identityNode, "versionNo"),
            RequiredLong(identityNode, "revision"),
            RequiredString(identityNode, "status"),
            RequiredString(identityNode, "configHash"),
            pins.Select(item => item?.GetValue<string>() ?? string.Empty)
                .ToArray(),
            OptionalString(root, "previousVersionId"),
            root["validationReceipt"] as JsonObject,
            root);
    }

    private static void RequireP805AdvancedVersionIdentity(
        P805AdvancedVersionIdentity identity,
        P8AdvancedFixture fixture)
    {
        HarnessAssert.Equal("ADVANCED_SUMMARY", identity.OwnerKind,
            "Advanced historical ownerKind mismatch");
        HarnessAssert.Equal(
            $"{fixture.Assignment.Id}:{fixture.DynamicFormTemplateId}:" +
            fixture.SectionId,
            identity.OwnerId,
            "Advanced historical ownerId mismatch");
        HarnessAssert.True(ObjectId.TryParse(identity.ConfigId, out _),
            "Advanced historical configId is not an ObjectId");
        HarnessAssert.True(ObjectId.TryParse(identity.VersionId, out _),
            "Advanced historical versionId is not an ObjectId");
        HarnessAssert.True(identity.VersionNo > 0,
            "Advanced historical versionNo is not positive");
        HarnessAssert.True(identity.Revision >= 0,
            "Advanced historical revision is negative");
        HarnessAssert.True(identity.Status is "LOCKED" or "ARCHIVED",
            "Advanced historical version status is not immutable");
        RequireLowerSha256(identity.ConfigHash,
            "Advanced historical configHash");
        HarnessAssert.True(identity.DependencyPins.SequenceEqual(
                identity.DependencyPins
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Advanced historical dependency pins are not canonical");
        HarnessAssert.True(identity.ValidationReceipt is not null,
            "Advanced historical version lacks validation receipt");
    }

    private sealed record P805AdvancedVersionIdentity(
        string OwnerKind,
        string OwnerId,
        string ConfigId,
        string VersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        string? PreviousVersionId,
        JsonObject? ValidationReceipt,
        JsonObject Raw);
}
