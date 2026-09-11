using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP806CrudCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-DIF-010",
            "system_admin",
            ["p8-dif-010-create", "p8-dif-010-edit"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("010");
                var selector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);
                var (_, created) = await PutDiffConfigAsync(
                    actor,
                    fixture,
                    "p8-dif-010-create",
                    DiffPayload(selector, name: "P8 Diff create"),
                    ct);
                var (_, edited) = await PutDiffConfigAsync(
                    actor,
                    fixture,
                    "p8-dif-010-edit",
                    DiffPayload(
                        selector,
                        direction: "RIGHT_TO_LEFT",
                        missingPolicy: "AS_ZERO",
                        emptyPolicy: "INCLUDE",
                        name: "P8 Diff edited"),
                    ct);
                HarnessAssert.Equal(created.ConfigId, edited.ConfigId,
                    "Diff DRAFT edit changed configId");
                HarnessAssert.Equal(created.VersionId, edited.VersionId,
                    "Diff DRAFT edit changed versionId");
                HarnessAssert.Equal(created.VersionNo, edited.VersionNo,
                    "Diff DRAFT edit changed versionNo");
                HarnessAssert.Equal(created.Revision + 1, edited.Revision,
                    "Diff DRAFT edit did not advance revision once");
                HarnessAssert.True(!string.Equals(
                        created.ConfigHash,
                        edited.ConfigHash,
                        StringComparison.Ordinal),
                    "Diff DRAFT edit did not change configHash");
                var versions = await ListDiffVersionsAsync(actor, fixture, ct);
                HarnessAssert.Equal(1, versions.Count,
                    "Diff DRAFT edit created an unexpected version row");
                var detail = await ReadDiffVersionAsync(actor, fixture, 1, ct);
                HarnessAssert.Equal(edited.VersionId, detail.VersionId,
                    "Diff version detail differs from edited DRAFT");
                HarnessAssert.Equal(edited.ConfigHash, detail.ConfigHash,
                    "Diff version detail hash differs from edited DRAFT");
                return new CaseObservation(
                    "Create/edit/list/detail preserved stable config/version identity while CAS revision/hash advanced exactly once.",
                    "create=v1r1;edit=v1r2;configId+versionId=stable;list=1;detail=exact;receipts=2;results=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-011",
            "outsider_b",
            [
                "p8-dif-011-reader-manage",
                "p8-dif-011-reader-lock",
                "p8-dif-011-known",
                "p8-dif-011-unknown"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var outsider = Actor("outsider_b");
                var fixture = DiffFixture("011");
                var known = await _api.GetAsync(
                    DiffConfigRoute(fixture),
                    outsider.Token,
                    ct: ct);
                var unknownAssignment = ObjectId.GenerateNewId().ToString();
                var unknownTemplate = ObjectId.GenerateNewId().ToString();
                var unknownRoute =
                    $"api/work-report-statistic-diffs/assignments/" +
                    $"{unknownAssignment}/templates/{unknownTemplate}/config";
                var unknown = await _api.GetAsync(
                    unknownRoute,
                    outsider.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(known, HttpStatusCode.Forbidden,
                    "P8 Diff known foreign owner disclosure barrier");
                ApiHarnessClient.ExpectStatus(unknown, HttpStatusCode.Forbidden,
                    "P8 Diff unknown owner disclosure barrier");
                var knownCode = ApiHarnessClient.FindStringRecursive(
                                    known.Json,
                                    "errorCode")
                                ?? ApiHarnessClient.FindStringRecursive(
                                    known.Json,
                                    "code");
                var unknownCode = ApiHarnessClient.FindStringRecursive(
                                      unknown.Json,
                                      "errorCode")
                                  ?? ApiHarnessClient.FindStringRecursive(
                                      unknown.Json,
                                      "code");
                HarnessAssert.Equal(knownCode, unknownCode,
                    "Diff auth responses disclosed owner existence");
                var reader = Actor("ordinary_a");
                var readerGet = await _api.GetAsync(
                    DiffConfigRoute(fixture),
                    reader.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    readerGet,
                    HttpStatusCode.OK,
                    "P8 Diff watcher read axis");
                var readerIdentity = ParseDiffIdentity(
                    readerGet.Json,
                    requireReceipt: false);
                RequireDiffIdentityContract(readerIdentity, fixture);
                HarnessAssert.Equal(false,
                    RequiredBool(readerIdentity.Permissions, "canManageDraft"),
                    "Diff watcher unexpectedly has manage permission");
                HarnessAssert.Equal(false,
                    RequiredBool(readerIdentity.Permissions, "canLockVersion"),
                    "Diff watcher unexpectedly has lock permission");


                var selector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-011/reader-manage",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-011-reader-manage",
                            0,
                            EmptyConfigHash,
                            DiffPayload(selector)),
                        reader.Token,
                        ct: ct),
                    HttpStatusCode.Forbidden,
                    "WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN",
                    null,
                    "DIFF_CONFIG_MANAGE_FORBIDDEN",
                    ct);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-011/reader-lock",
                    () => _api.PostAsync(
                        $"{DiffConfigRoute(fixture)}/lock",
                        Envelope(
                            "p8-dif-011-reader-lock",
                            0,
                            EmptyConfigHash,
                            new JsonObject()),
                        reader.Token,
                        ct: ct),
                    HttpStatusCode.Forbidden,
                    "WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN",
                    null,
                    "DIFF_CONFIG_LOCK_FORBIDDEN",
                    ct);
                var readerAfter = await ReadDiffConfigAsync(
                    reader,
                    fixture,
                    ct,
                    requirePersisted: false);
                HarnessAssert.True(readerAfter.IsVirtualEmpty,
                    "Rejected watcher manage/lock persisted a Diff owner");

                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-011/known-mutation",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-011-known",
                            0,
                            EmptyConfigHash,
                            DiffPayload(selector)),
                        outsider.Token,
                        ct: ct),
                    HttpStatusCode.Forbidden,
                    knownCode ?? "FORBIDDEN",
                    null,
                    null,
                    ct);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-011/unknown-mutation",
                    () => _api.PutAsync(
                        unknownRoute,
                        Envelope(
                            "p8-dif-011-unknown",
                            0,
                            EmptyConfigHash,
                            DiffPayload(selector)),
                        outsider.Token,
                        ct: ct),
                    HttpStatusCode.Forbidden,
                    unknownCode ?? "FORBIDDEN",
                    null,
                    null,
                    ct);
                return new CaseObservation(
                    "Authorization preceded existence; an assigned watcher could read but separate manage/lock axes failed with distinct reasons and zero writes.",
                    "knownGET=403;unknownGET=403;sameCode=true;watcherGET=200;manage=403+0W;lock=403+0W+distinct;receipt=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-012",
            "system_admin",
            [
                "p8-dif-012-replay",
                "p8-dif-012-stale"
            ],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("012");
                var initial = await ReadDiffConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: false);
                var selector = DiffSelector(
                    "TABLE_METRIC",
                    $"{DiffBlockId}:{DiffMetricKey}",
                    _p806MetricLabel.LabelCode);
                var payload = DiffPayload(selector);
                var envelope = Envelope(
                    "p8-dif-012-replay",
                    initial.Revision,
                    initial.ConfigHash,
                    payload);
                var first = await _api.PutAsync(
                    DiffConfigRoute(fixture),
                    envelope,
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK,
                    "P8 Diff initial replay mutation");
                var firstIdentity = ParseDiffIdentity(
                    first.Json,
                    requireReceipt: true);
                RequireDiffIdentityContract(firstIdentity, fixture);
                await RequireDirectDiffIdentityAsync(
                    firstIdentity,
                    fixture,
                    "p8-dif-012-replay",
                    "UPSERT_DIFF_CONFIG",
                    actor.Id,
                    requirePersisted: true,
                    ct);

                var replayBefore = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await _api.PutAsync(
                    DiffConfigRoute(fixture),
                    envelope,
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK,
                    "P8 Diff exact replay");
                HarnessAssert.Equal(
                    Canonicalize(firstIdentity.Raw),
                    Canonicalize(ApiHarnessClient.RequiredObject(
                        replay.Json,
                        "P8 Diff exact replay response")),
                    "Diff exact replay response drifted");
                VerifyCollectionContract(
                    "P8-DIF-012/exact-replay",
                    BuildDeltas(
                        replayBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());

                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-012/divergent-replay",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-012-replay",
                            initial.Revision,
                            initial.ConfigHash,
                            DiffPayload(
                                selector,
                                direction: "RIGHT_TO_LEFT")),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                    null,
                    null,
                    ct);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-012/stale-cas",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-012-stale",
                            initial.Revision,
                            initial.ConfigHash,
                            payload),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    null,
                    ct);
                return new CaseObservation(
                    "Exact command replay returned byte-equivalent receipt response; divergent replay and stale CAS wrote nothing.",
                    "first=1;exactReplay=0W+same;divergent=409+0W;stale=409+0W;receiptUnique=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-013",
            "system_admin",
            [
                "p8-dif-013-draft",
                "p8-dif-013-lock",
                "p8-dif-013-next-draft"
            ],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("013");
                var selector = DiffSelector(
                    "ROW_LABEL",
                    $"{DiffBlockId}:{_p806RowLabel.LabelCode}",
                    _p806RowLabel.LabelCode);
                var (_, draft) = await PutDiffConfigAsync(
                    actor,
                    fixture,
                    "p8-dif-013-draft",
                    DiffPayload(selector),
                    ct);
                var (_, locked) = await PostDiffActionAsync(
                    actor,
                    fixture,
                    "lock",
                    "p8-dif-013-lock",
                    draft,
                    ct);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "Diff v1 did not lock");
                HarnessAssert.Equal(draft.ConfigId, locked.ConfigId,
                    "Diff lock changed configId");
                HarnessAssert.Equal(draft.VersionId, locked.VersionId,
                    "Diff lock replaced versionId");
                HarnessAssert.Equal(draft.ConfigHash, locked.ConfigHash,
                    "Diff lock changed content hash");
                var lockedRowBefore = await ReadDiffVersionRowAsync(
                    locked.VersionId,
                    ct);
                var lockedBytesBefore = lockedRowBefore.ToBson();

                var (_, next) = await PostDiffActionAsync(
                    actor,
                    fixture,
                    "next-draft",
                    "p8-dif-013-next-draft",
                    locked,
                    ct);
                HarnessAssert.Equal("DRAFT", next.Status,
                    "Diff next version is not DRAFT");
                HarnessAssert.Equal(2, next.VersionNo,
                    "Diff next versionNo mismatch");
                HarnessAssert.Equal(locked.ConfigId, next.ConfigId,
                    "Diff next DRAFT changed lineage configId");
                HarnessAssert.Equal(locked.VersionId, next.PreviousVersionId,
                    "Diff next DRAFT previousVersionId mismatch");
                HarnessAssert.True(!string.Equals(
                        locked.VersionId,
                        next.VersionId,
                        StringComparison.Ordinal),
                    "Diff next DRAFT reused locked versionId");
                var lockedRowAfter = await ReadDiffVersionRowAsync(
                    locked.VersionId,
                    ct);
                HarnessAssert.True(lockedBytesBefore.SequenceEqual(
                        lockedRowAfter.ToBson()),
                    "Diff locked v1 BSON changed after next-draft");
                var lineageRows = await _database
                    .GetCollection<BsonDocument>(DiffConfigsCollection)
                    .Find(Builders<BsonDocument>.Filter.Eq(
                        "configId",
                        ObjectId.Parse(next.ConfigId)))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("versionNo"))
                    .ToListAsync(ct);
                HarnessAssert.Equal(2, lineageRows.Count,
                    "Diff next-draft did not produce exactly two version rows");
                HarnessAssert.Equal(locked.VersionId,
                    BsonString(lineageRows[0], "_id"),
                    "Diff lineage v1 row changed identity");
                HarnessAssert.Equal(next.VersionId,
                    BsonString(lineageRows[1], "_id"),
                    "Diff lineage v2 row identity mismatch");
                var versions = await ListDiffVersionsAsync(actor, fixture, ct);
                HarnessAssert.Equal(2, versions.Count,
                    "Diff versions list did not expose v1+v2");
                var versionOne = await ReadDiffVersionAsync(actor, fixture, 1, ct);
                HarnessAssert.Equal("LOCKED", versionOne.Status,
                    "Diff v1 detail lost LOCKED status");
                HarnessAssert.Equal(locked.ConfigHash, versionOne.ConfigHash,
                    "Diff v1 detail hash changed after next-draft");
                return new CaseObservation(
                    "DRAFT -> LOCKED -> next DRAFT created two real Mongo version rows and preserved the locked v1 BSON byte-for-byte.",
                    "v1=draft>locked;v2=draft;rows=2;sharedConfigId=true;previous=v1;lockedBsonImmutable=true;results=0");
            },
            ct);
    }

    private async Task<BsonDocument> ReadDiffVersionRowAsync(
        string versionId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(DiffConfigsCollection)
            .Find(Builders<BsonDocument>.Filter.Eq(
                "_id",
                ObjectId.Parse(versionId)))
            .SingleAsync(ct);
}
