using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP811CreateReplayRaceAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-RACE-001",
            "system_admin",
            [P811CreateReplayCommand],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var envelope = Envelope(
                    P811CreateReplayCommand,
                    0,
                    EmptyConfigHash,
                    LabelPayload(
                        "p8.race.create.replay",
                        "P8 race create replay",
                        "GLOBAL",
                        null,
                        usage: "STATISTIC",
                        dataType: "NUMBER"));
                var responses = await BarrierReleasedAsync(
                    4,
                    () => _api.PostAsync(
                        "api/labels/config",
                        envelope.DeepClone(),
                        actor.Token,
                        ct: ct),
                    ct);
                HarnessAssert.True(responses.All(item =>
                        item.StatusCode == HttpStatusCode.OK),
                    "Same-command create replay returned a non-200 contender.");
                HarnessAssert.Equal(1,
                    responses.Select(CanonicalResponse)
                        .Distinct(StringComparer.Ordinal).Count(),
                    "Same-command create replay responses diverged");
                var identity = ParseIdentity(
                    responses[0].Json,
                    requireReceipt: true);
                await RequireDirectIdentityAsync(
                    identity,
                    P811CreateReplayCommand,
                    actor.Id,
                    ct);
                HarnessAssert.Equal(1L,
                    await _database.GetCollection<BsonDocument>(LabelsCollection)
                        .CountDocumentsAsync(
                            Builders<BsonDocument>.Filter.Eq(
                                "code", "p8.race.create.replay"),
                            cancellationToken: ct),
                    "Same-command create replay persisted multiple labels");
                HarnessAssert.Equal(1L,
                    await CountReceiptsAsync(P811CreateReplayCommand, ct),
                    "Same-command create replay persisted multiple receipts");
                _p811CreateReplayIdentity = identity;
                return new CaseObservation(
                    "Four barrier-released real Kestrel creates used one command/CAS envelope and converged on one label, receipt and canonical response.",
                    "contenders=4;winnerState=1;responses=1;label=1;receipt=1;revision=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811BasicEditReplayRaceAsync(CancellationToken ct)
    {
        const string commandId = "p811-basic-edit-replay-002";
        await RunEvidenceCaseAsync(
            "P8-RACE-002",
            "system_admin",
            [commandId],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = P811Basic("edit-replay");
                var current = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: false);
                HarnessAssert.True(current.IsVirtualEmpty,
                    "P8-RACE-002 did not start from virtual empty Basic state.");
                var envelope = Envelope(
                    commandId,
                    current.Revision,
                    current.ConfigHash,
                    P811BasicPayload("edit-replay"));
                var responses = await BarrierReleasedAsync(
                    4,
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        envelope.DeepClone(),
                        actor.Token,
                        ct: ct),
                    ct);
                HarnessAssert.True(responses.All(item =>
                        item.StatusCode == HttpStatusCode.OK),
                    "Same-command Basic edit replay returned a loser.");
                HarnessAssert.Equal(1,
                    responses.Select(CanonicalResponse)
                        .Distinct(StringComparer.Ordinal).Count(),
                    "Same-command Basic edit replay responses diverged");
                var identity = ParseBasicIdentity(
                    responses[0].Json,
                    requireReceipt: true);
                RequireBasicIdentityContract(identity, fixture);
                await RequireDirectBasicIdentityAsync(
                    identity,
                    fixture,
                    commandId,
                    "UPSERT_BASIC_SUMMARY_CONFIG",
                    actor.Id,
                    requirePersisted: true,
                    ct);
                HarnessAssert.Equal(1L, identity.Revision,
                    "Same-command Basic edit advanced more than once");
                HarnessAssert.Equal(1L,
                    await CountP811ReceiptsAsync(
                        identity.OwnerId, commandId, ct),
                    "Same-command Basic edit duplicated its receipt");
                return new CaseObservation(
                    "Four barrier-released Basic edits returned one durable response while the owner advanced from virtual revision zero to persisted revision one exactly once.",
                    "contenders=4;responses=1;ownerWrites=1;revision=1;receipt=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811BasicLockReplayRaceAsync(CancellationToken ct)
    {
        const string commandId = "p811-basic-lock-replay-003";
        await RunEvidenceCaseAsync(
            "P8-RACE-003",
            "system_admin",
            [commandId],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = P811Basic("lock-replay");
                var current = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: true);
                HarnessAssert.Equal("DRAFT", current.Status,
                    "P8-RACE-003 fixture is not draft");
                var request = Envelope(
                    commandId,
                    current.Revision,
                    current.ConfigHash,
                    BasicActionPayload());
                var responses = await BarrierReleasedAsync(
                    4,
                    () => _api.PostAsync(
                        $"{BasicConfigRoute(fixture)}/lock",
                        request.DeepClone(),
                        actor.Token,
                        ct: ct),
                    ct);
                HarnessAssert.True(responses.All(item =>
                        item.StatusCode == HttpStatusCode.OK),
                    "Same-command Basic lock replay returned a loser.");
                HarnessAssert.Equal(1,
                    responses.Select(CanonicalResponse)
                        .Distinct(StringComparer.Ordinal).Count(),
                    "Same-command Basic lock replay responses diverged");
                var locked = ParseBasicIdentity(
                    responses[0].Json,
                    requireReceipt: true);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "Same-command Basic lock did not lock");
                HarnessAssert.Equal(current.Revision + 1, locked.Revision,
                    "Same-command Basic lock advanced revision more than once");
                HarnessAssert.Equal(current.ConfigHash, locked.ConfigHash,
                    "Basic lock changed content hash");
                await RequireDirectBasicIdentityAsync(
                    locked,
                    fixture,
                    commandId,
                    "LOCK_BASIC_SUMMARY_CONFIG",
                    actor.Id,
                    requirePersisted: true,
                    ct);
                HarnessAssert.Equal(1L,
                    await CountP811ReceiptsAsync(
                        locked.OwnerId, commandId, ct),
                    "Same-command Basic lock duplicated receipt");
                return new CaseObservation(
                    "Four barrier-released lock contenders converged on one immutable Basic lock receipt and one lifecycle revision advancement.",
                    "contenders=4;responses=1;status=LOCKED;revisionDelta=1;receipt=1;hashStable=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811DivergentAndStaleRaceAsync(CancellationToken ct)
    {
        const string staleCommand = "p811-stale-hash-004";
        await RunEvidenceCaseAsync(
            "P8-RACE-004",
            "system_admin",
            [P811CreateReplayCommand, staleCommand],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var created = _p811CreateReplayIdentity
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P8-RACE-001 create identity is absent.");
                var divergent = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        P811CreateReplayCommand,
                        0,
                        EmptyConfigHash,
                        LabelPayload(
                            created.LabelCode,
                            "P8 divergent replay",
                            "GLOBAL",
                            null,
                            usage: "STATISTIC",
                            dataType: "NUMBER")),
                    actor.Token,
                    ct: ct);
                ExpectFailure(
                    divergent,
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT");

                var fixture = P811Basic("edit-replay");
                var current = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: true);
                var stale = await _api.PutAsync(
                    BasicConfigRoute(fixture),
                    Envelope(
                        staleCommand,
                        current.Revision,
                        new string('f', 64),
                        P811BasicPayload("stale-hash")),
                    actor.Token,
                    ct: ct);
                ExpectBasicFailure(
                    stale,
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    expectedPath: null,
                    expectedReason: null);
                return new CaseObservation(
                    "Changed replay and a current-revision/stale-hash edit both returned stable 409 conflicts before any owner, receipt, job, outbox or result write.",
                    "changedReplay=409/STAT_CONFIG_COMMAND_REPLAY_CONFLICT;staleHash=409/STAT_CONFIG_CAS_CONFLICT;writes=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811EditVsLockRaceAsync(CancellationToken ct)
    {
        const string editCommand = "p811-edit-vs-lock-edit-005";
        const string lockCommand = "p811-edit-vs-lock-lock-005";
        await RunEvidenceCaseAsync(
            "P8-RACE-005",
            "system_admin",
            [editCommand, lockCommand],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = P811Basic("edit-lock");
                var before = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: true);
                var edit = Envelope(
                    editCommand,
                    before.Revision,
                    before.ConfigHash,
                    P811BasicPayload("edit-winner"));
                var lockRequest = Envelope(
                    lockCommand,
                    before.Revision,
                    before.ConfigHash,
                    BasicActionPayload());
                var release = NewP811Barrier();
                var editTask = Task.Run(async () =>
                {
                    await release.Task.WaitAsync(ct);
                    return await _api.PutAsync(
                        BasicConfigRoute(fixture),
                        edit,
                        actor.Token,
                        ct: ct);
                }, ct);
                var lockTask = Task.Run(async () =>
                {
                    await release.Task.WaitAsync(ct);
                    return await _api.PostAsync(
                        $"{BasicConfigRoute(fixture)}/lock",
                        lockRequest,
                        actor.Token,
                        ct: ct);
                }, ct);
                release.SetResult(true);
                var responses = await Task.WhenAll(editTask, lockTask);
                HarnessAssert.Equal(1,
                    responses.Count(item => item.StatusCode == HttpStatusCode.OK),
                    "Edit-vs-lock did not produce exactly one winner");
                HarnessAssert.Equal(1,
                    responses.Count(item =>
                        item.StatusCode == HttpStatusCode.Conflict),
                    "Edit-vs-lock did not produce exactly one conflict");
                var loser = responses.Single(item =>
                    item.StatusCode == HttpStatusCode.Conflict);
                var loserCode = P811ErrorCode(loser);
                HarnessAssert.True(
                    loserCode is "STAT_CONFIG_CAS_CONFLICT" or
                        "BASIC_SUMMARY_CONFIG_LOCKED" or
                        "BASIC_SUMMARY_VERSION_LOCKED",
                    $"Edit-vs-lock returned unexpected loser code {loserCode}.");
                var after = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: true);
                HarnessAssert.Equal(before.Revision + 1, after.Revision,
                    "Edit-vs-lock advanced more than one revision");
                var receiptCount =
                    await CountP811ReceiptsAsync(after.OwnerId, editCommand, ct) +
                    await CountP811ReceiptsAsync(after.OwnerId, lockCommand, ct);
                HarnessAssert.Equal(1L, receiptCount,
                    "Edit-vs-lock persisted more than the winning receipt");
                return new CaseObservation(
                    "Barrier-released Basic edit and lock sharing one CAS produced one commit, one stable conflict and one final revision with no mixed snapshot.",
                    "contenders=2;winner=1;loser=409;revisionDelta=1;receiptDelta=1;mixedRevision=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811LockVsNextDraftRaceAsync(CancellationToken ct)
    {
        const string lockCommand = "p811-lock-vs-next-lock-006";
        const string nextCommand = "p811-lock-vs-next-draft-006";
        await RunEvidenceCaseAsync(
            "P8-RACE-006",
            "system_admin",
            [lockCommand, nextCommand],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = P811Basic("lock-next");
                var before = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: true);
                var lockRequest = Envelope(
                    lockCommand,
                    before.Revision,
                    before.ConfigHash,
                    BasicActionPayload());
                var nextRequest = Envelope(
                    nextCommand,
                    before.Revision,
                    before.ConfigHash,
                    BasicActionPayload());
                var release = NewP811Barrier();
                var lockTask = Task.Run(async () =>
                {
                    await release.Task.WaitAsync(ct);
                    return await _api.PostAsync(
                        $"{BasicConfigRoute(fixture)}/lock",
                        lockRequest,
                        actor.Token,
                        ct: ct);
                }, ct);
                var nextTask = Task.Run(async () =>
                {
                    await release.Task.WaitAsync(ct);
                    return await _api.PostAsync(
                        $"{BasicConfigRoute(fixture)}/next-draft",
                        nextRequest,
                        actor.Token,
                        ct: ct);
                }, ct);
                release.SetResult(true);
                var responses = await Task.WhenAll(lockTask, nextTask);
                ApiHarnessClient.ExpectStatus(
                    responses[0], HttpStatusCode.OK,
                    "P8 lock-vs-next lock winner");
                ApiHarnessClient.ExpectStatus(
                    responses[1], HttpStatusCode.Conflict,
                    "P8 lock-vs-next next-draft loser");
                var loserCode = P811ErrorCode(responses[1]);
                HarnessAssert.True(
                    loserCode is "STAT_CONFIG_CAS_CONFLICT" or
                        "BASIC_SUMMARY_VERSION_NOT_LOCKED" or
                        "BASIC_SUMMARY_CONFIG_NOT_LOCKED",
                    $"Lock-vs-next returned unexpected loser code {loserCode}.");
                var after = await ReadBasicConfigAsync(
                    actor, fixture, ct, requirePersisted: true);
                HarnessAssert.Equal("LOCKED", after.Status,
                    "Lock-vs-next did not preserve immutable lock winner");
                HarnessAssert.Equal(before.VersionNo, after.VersionNo,
                    "Losing next-draft created a version");
                HarnessAssert.Equal(before.Revision + 1, after.Revision,
                    "Lock-vs-next revision mismatch");
                var receiptCount =
                    await CountP811ReceiptsAsync(after.OwnerId, lockCommand, ct) +
                    await CountP811ReceiptsAsync(after.OwnerId, nextCommand, ct);
                HarnessAssert.Equal(1L, receiptCount,
                    "Lock-vs-next persisted a losing receipt");
                return new CaseObservation(
                    "Concurrent lock and next-draft on a draft had one immutable lock winner; the premature/stale next-draft wrote no version or receipt.",
                    "contenders=2;lockWinner=1;nextDraftLoser=409;versionDelta=0;revisionDelta=1;receiptDelta=1;resultDelta=0");
            },
            ct);
    }

    private static TaskCompletionSource<bool> NewP811Barrier()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<T[]> BarrierReleasedAsync<T>(
        int contenderCount,
        Func<Task<T>> contender,
        CancellationToken ct)
    {
        var release = NewP811Barrier();
        var tasks = Enumerable.Range(0, contenderCount)
            .Select(_ => Task.Run(async () =>
            {
                await release.Task.WaitAsync(ct);
                return await contender();
            }, ct))
            .ToArray();
        release.SetResult(true);
        return await Task.WhenAll(tasks);
    }

    private async Task<long> CountP811ReceiptsAsync(
        string ownerId,
        string commandId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("ownerId", ownerId) &
                Builders<BsonDocument>.Filter.Eq("commandId", commandId),
                cancellationToken: ct);

    private static string P811ErrorCode(ApiHarnessResponse response)
        => ApiHarnessClient.FindStringRecursive(response.Json, "errorCode")
           ?? ApiHarnessClient.FindStringRecursive(response.Json, "code")
           ?? throw new InvalidOperationException(
               $"P8-11 conflict lacks an error code. Body={response.Body}");
}
