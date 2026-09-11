using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] NoCollectionWrites = [];
    private static readonly string[] LabelMutationWrites =
        [LabelsCollection, ReceiptsCollection];

    private P8ConfigIdentity _corePrimary = default!;
    private P8ConfigIdentity _coreOtherOwner = default!;
    private P8ConfigIdentity _coreCanonical = default!;
    private string _coreReplayCommand = default!;
    private JsonObject _coreReplayEnvelope = default!;
    private string _coreReplayResponseCanonical = default!;
    private JsonArray _coreLineageBeforeAuditOnlyVersion = default!;

    private async Task RunCoreCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-CORE-001",
            "anonymous",
            ["p8-core-001-noauth"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var response = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "p8-core-001-noauth",
                        0,
                        EmptyConfigHash,
                        LabelPayload("p8.core.noauth", "P8 no auth", "GLOBAL", null)),
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Unauthorized,
                    "unauthenticated config mutation");
                return new CaseObservation(
                    "Unauthenticated mutation rejected by real Kestrel authorization.",
                    "http=401;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-002",
            "ordinary_a",
            ["p8-core-002-ordinary"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var actor = Actor("ordinary_a");
                var response = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "p8-core-002-ordinary",
                        0,
                        EmptyConfigHash,
                        LabelPayload("p8.core.ordinary", "P8 ordinary", "UNIT", _unitAId)),
                    actor.Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.Forbidden, "LABEL_MANAGER_REQUIRED");
                return new CaseObservation(
                    "Authenticated ordinary actor rejected before any owner write.",
                    "http=403;code=LABEL_MANAGER_REQUIRED;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-003",
            "unit_manager_a",
            ["p8-core-003-foreign-before-existence"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var actor = Actor("unit_manager_a");
                var missingId = ObjectId.GenerateNewId().ToString();
                var response = await _api.PutAsync(
                    $"api/labels/{missingId}/config",
                    Envelope(
                        "p8-core-003-foreign-before-existence",
                        0,
                        EmptyConfigHash,
                        LabelPayload("p8.core.foreign", "P8 foreign", "UNIT", _unitBId)),
                    actor.Token,
                    ct: ct);
                ExpectFailure(
                    response,
                    HttpStatusCode.Forbidden,
                    "LABEL_SCOPE_MISMATCH",
                    "LABEL_MANAGE_FORBIDDEN");
                HarnessAssert.True(
                    !response.Body.Contains("LABEL_NOT_FOUND", StringComparison.Ordinal),
                    "Foreign-scope mutation leaked owner existence.");
                return new CaseObservation(
                    "Foreign UNIT scope denied before missing owner lookup.",
                    "authBeforeExistence=true;http=403;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-004",
            "level_manager",
            ["p8-core-004-layer-before-existence"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var actor = Actor("level_manager");
                var missingId = ObjectId.GenerateNewId().ToString();
                var response = await _api.PutAsync(
                    $"api/labels/{missingId}/config",
                    Envelope(
                        "p8-core-004-layer-before-existence",
                        0,
                        EmptyConfigHash,
                        LabelPayload("p8.core.layer", "P8 wrong layer", "GLOBAL", null)),
                    actor.Token,
                    ct: ct);
                ExpectFailure(
                    response,
                    HttpStatusCode.Forbidden,
                    "LABEL_SCOPE_MISMATCH",
                    "LABEL_MANAGE_FORBIDDEN");
                HarnessAssert.True(
                    !response.Body.Contains("LABEL_NOT_FOUND", StringComparison.Ordinal),
                    "Layer mismatch leaked owner existence.");
                return new CaseObservation(
                    "LEVEL manager wrong layer denied before owner existence.",
                    "authBeforeExistence=true;layer=GLOBAL;http=403;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-005",
            "system_admin",
            ["p8-core-005-extra-envelope"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var envelope = Envelope(
                    "p8-core-005-extra-envelope",
                    0,
                    EmptyConfigHash,
                    LabelPayload("p8.core.schema.extra", "P8 schema extra", "GLOBAL", null));
                envelope["unexpected"] = true;
                var response = await _api.PostAsync(
                    "api/labels/config",
                    envelope,
                    Actor("system_admin").Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.BadRequest, "STAT_CONFIG_SCHEMA_INVALID");

                var legacyRequests = new[]
                {
                    (HttpMethod.Post, "api/labels", (JsonNode)LabelPayload(
                        "p8.core.legacy-create",
                        "P8 legacy create",
                        "GLOBAL",
                        null)),
                    (HttpMethod.Put, $"api/labels/{ObjectId.GenerateNewId()}",
                        (JsonNode)new JsonObject { ["name"] = "P8 legacy update" }),
                    (HttpMethod.Delete, $"api/labels/{ObjectId.GenerateNewId()}",
                        (JsonNode)new JsonObject { ["hardDelete"] = true })
                };
                foreach (var legacy in legacyRequests)
                {
                    var rejected = await _api.SendAsync(
                        legacy.Item1,
                        legacy.Item2,
                        legacy.Item3,
                        Actor("system_admin").Token,
                        headers: null,
                        ct);
                    ExpectFailure(
                        rejected,
                        HttpStatusCode.BadRequest,
                        "STAT_CONFIG_SCHEMA_INVALID");
                }
                return new CaseObservation(
                    "Unknown envelope properties and all legacy mutation shapes were rejected before writes.",
                    "strictEnvelope=true;legacyMutationRoutes=3;alternateWriter=false;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-006",
            "system_admin",
            ["bad command id"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var response = await _api.PostAsync(
                    "api/labels/config",
                    Envelope(
                        "bad command id",
                        0,
                        EmptyConfigHash,
                        LabelPayload("p8.core.bad-command", "P8 bad command", "GLOBAL", null)),
                    Actor("system_admin").Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.BadRequest, "STAT_CONFIG_COMMAND_ID_INVALID");
                return new CaseObservation(
                    "Invalid commandId rejected before owner/receipt write.",
                    "commandSchema=strict;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-007",
            "system_admin",
            ["p8-core-007-null-payload"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var body = new JsonObject
                {
                    ["commandId"] = "p8-core-007-null-payload",
                    ["expectedRevision"] = 0,
                    ["expectedConfigHash"] = EmptyConfigHash,
                    ["payload"] = null
                };
                var response = await _api.PostAsync(
                    "api/labels/config",
                    body,
                    Actor("system_admin").Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.BadRequest, "STAT_CONFIG_SCHEMA_INVALID");
                return new CaseObservation(
                    "Null payload rejected by strict mutation envelope.",
                    "payloadRequired=true;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-008",
            "system_admin",
            ["p8-core-008-create-a", "p8-core-008-create-b"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                _corePrimary = (await CreateLabelAsync(
                    Actor("system_admin"),
                    "p8-core-008-create-a",
                    LabelPayload("p8.core.primary", "P8 Core Primary", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"),
                    ct)).Identity;
                _coreOtherOwner = (await CreateLabelAsync(
                    Actor("system_admin"),
                    "p8-core-008-create-b",
                    LabelPayload("p8.core.other-owner", "P8 Core Other Owner", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "BOOLEAN"),
                    ct)).Identity;
                HarnessAssert.Equal(1, _corePrimary.VersionNo, "Create A versionNo mismatch");
                HarnessAssert.Equal(1L, _corePrimary.Revision, "Create A revision mismatch");
                HarnessAssert.True(_corePrimary.OwnerId != _coreOtherOwner.OwnerId,
                    "Create fixtures did not produce distinct owners");
                return new CaseObservation(
                    "Create accepted exact empty-state CAS and wrote two distinct owner families atomically per command.",
                    "ownersCreated=2;revision=1;versionNo=1");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-009",
            "system_admin",
            ["p8-core-009-correct-edit"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var before = _corePrimary;
                _coreReplayCommand = "p8-core-009-correct-edit";
                _coreReplayEnvelope = Envelope(
                    _coreReplayCommand,
                    before.Revision,
                    before.ConfigHash,
                    LabelPayload(before.LabelCode, "P8 Core Edited", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"));
                var response = await _api.PutAsync(
                    $"api/labels/{before.LabelId}/config",
                    _coreReplayEnvelope.DeepClone(),
                    Actor("system_admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "correct CAS edit");
                _corePrimary = ParseIdentity(response.Json, requireReceipt: true);
                RequireIdentityContract(_corePrimary);
                await RequireDirectIdentityAsync(
                    _corePrimary,
                    _coreReplayCommand,
                    Actor("system_admin").Id,
                    ct);
                RequireVersionAdvanced(before, _corePrimary);
                _coreReplayResponseCanonical = CanonicalResponse(response);
                return new CaseObservation(
                    "Correct revision+hash edit advanced revision and version exactly once.",
                    "correctCas=true;revisionDelta=1;versionDelta=1");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-010",
            "system_admin",
            ["p8-core-010-stale-revision"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var response = await _api.PutAsync(
                    $"api/labels/{_corePrimary.LabelId}/config",
                    Envelope(
                        "p8-core-010-stale-revision",
                        _corePrimary.Revision - 1,
                        _corePrimary.ConfigHash,
                        LabelPayload(_corePrimary.LabelCode, "P8 stale revision", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "NUMBER")),
                    Actor("system_admin").Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.Conflict, "STAT_CONFIG_CAS_CONFLICT");
                return new CaseObservation(
                    "Stale revision CAS rejected with zero partial write.",
                    "cas=revision;http=409;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-011",
            "system_admin",
            ["p8-core-011-stale-hash"],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var response = await _api.PutAsync(
                    $"api/labels/{_corePrimary.LabelId}/config",
                    Envelope(
                        "p8-core-011-stale-hash",
                        _corePrimary.Revision,
                        new string('f', 64),
                        LabelPayload(_corePrimary.LabelCode, "P8 stale hash", "GLOBAL", null,
                            usage: "STATISTIC", dataType: "NUMBER")),
                    Actor("system_admin").Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.Conflict, "STAT_CONFIG_CAS_CONFLICT");
                return new CaseObservation(
                    "Stale configHash CAS rejected with zero partial write.",
                    "cas=configHash;http=409;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-012",
            "system_admin",
            [_coreReplayCommand],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var replay = await _api.PutAsync(
                    $"api/labels/{_corePrimary.LabelId}/config",
                    _coreReplayEnvelope.DeepClone(),
                    Actor("system_admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "exact command replay");
                HarnessAssert.Equal(_coreReplayResponseCanonical, CanonicalResponse(replay),
                    "Exact replay response changed");
                var replayIdentity = ParseIdentity(replay.Json, requireReceipt: true);
                await RequireDirectIdentityAsync(
                    replayIdentity,
                    _coreReplayCommand,
                    Actor("system_admin").Id,
                    ct);
                HarnessAssert.Equal(_corePrimary.VersionId, replayIdentity.VersionId, "Replay advanced version");
                return new CaseObservation(
                    "Exact replay returned the durable response without owner/receipt delta.",
                    "idempotentReplay=true;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-013",
            "system_admin",
            [_coreReplayCommand],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var altered = (JsonObject)_coreReplayEnvelope.DeepClone();
                ((JsonObject)altered["payload"]!)["name"] = "P8 altered replay";
                var response = await _api.PutAsync(
                    $"api/labels/{_corePrimary.LabelId}/config",
                    altered,
                    Actor("system_admin").Token,
                    ct: ct);
                ExpectFailure(response, HttpStatusCode.Conflict, "STAT_CONFIG_COMMAND_REPLAY_CONFLICT");
                return new CaseObservation(
                    "Same owner+commandId with altered request hash was rejected.",
                    "replayConflict=true;delta=zero");
            },
            ct);

        const string sharedOwnerCommand = "p8-core-014-shared-owner-command";
        await RunEvidenceCaseAsync(
            "P8-CORE-014",
            "system_admin",
            [sharedOwnerCommand],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var ownerABefore = _corePrimary;
                var ownerBBefore = _coreOtherOwner;
                _corePrimary = (await UpdateLabelAsync(
                    Actor("system_admin"),
                    ownerABefore,
                    sharedOwnerCommand,
                    LabelPayload(ownerABefore.LabelCode, "P8 Owner A shared command", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"),
                    ct)).Identity;
                _coreOtherOwner = (await UpdateLabelAsync(
                    Actor("system_admin"),
                    ownerBBefore,
                    sharedOwnerCommand,
                    LabelPayload(ownerBBefore.LabelCode, "P8 Owner B shared command", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "BOOLEAN"),
                    ct)).Identity;
                HarnessAssert.Equal(2L, await CountReceiptsAsync(sharedOwnerCommand, ct),
                    "Receipt uniqueness was not ownerKind+ownerId+commandId");
                HarnessAssert.True(_corePrimary.ReceiptId != _coreOtherOwner.ReceiptId,
                    "Distinct owners reused one receipt id");
                return new CaseObservation(
                    "Same commandId updated two pre-existing owners under independent receipt tuples.",
                    "receiptIdentity=ownerKind+ownerId+commandId;owners=2");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-015",
            "system_admin",
            [_coreReplayCommand],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var tasks = Enumerable.Range(0, 8)
                    .Select(_ => _api.PutAsync(
                        $"api/labels/{_corePrimary.LabelId}/config",
                        _coreReplayEnvelope.DeepClone(),
                        Actor("system_admin").Token,
                        ct: ct))
                    .ToArray();
                var responses = await Task.WhenAll(tasks);
                HarnessAssert.True(responses.All(response => response.StatusCode == HttpStatusCode.OK),
                    "Concurrent exact replay returned a non-success response");
                HarnessAssert.Equal(1,
                    responses.Select(CanonicalResponse).Distinct(StringComparer.Ordinal).Count(),
                    "Concurrent exact replay responses were not deterministic");
                HarnessAssert.Equal(_coreReplayResponseCanonical, CanonicalResponse(responses[0]),
                    "Concurrent replay response differs from original durable response");
                HarnessAssert.Equal(1L, await _database.GetCollection<BsonDocument>(ReceiptsCollection)
                    .CountDocumentsAsync(Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("ownerId", _corePrimary.OwnerId),
                        Builders<BsonDocument>.Filter.Eq("commandId", _coreReplayCommand)), cancellationToken: ct),
                    "Concurrent replay duplicated receipt");
                return new CaseObservation(
                    "Eight concurrent exact replays returned one durable response and zero delta.",
                    "concurrentReplays=8;distinctResponses=1;receiptCount=1;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-016",
            "system_admin",
            ["p8-core-016-race-a", "p8-core-016-race-b"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var before = _corePrimary;
                var envelopeA = Envelope(
                    "p8-core-016-race-a",
                    before.Revision,
                    before.ConfigHash,
                    LabelPayload(before.LabelCode, "P8 race A", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"));
                var envelopeB = Envelope(
                    "p8-core-016-race-b",
                    before.Revision,
                    before.ConfigHash,
                    LabelPayload(before.LabelCode, "P8 race B", "GLOBAL", null,
                        usage: "STATISTIC", dataType: "NUMBER"));
                var responses = await Task.WhenAll(
                    _api.PutAsync($"api/labels/{before.LabelId}/config", envelopeA,
                        Actor("system_admin").Token, ct: ct),
                    _api.PutAsync($"api/labels/{before.LabelId}/config", envelopeB,
                        Actor("system_admin").Token, ct: ct));
                HarnessAssert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK),
                    "Different-command CAS race must have exactly one winner");
                HarnessAssert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict),
                    "Different-command CAS race must have exactly one loser");
                var winnerIndex = responses[0].StatusCode == HttpStatusCode.OK ? 0 : 1;
                var loserIndex = 1 - winnerIndex;
                ExpectFailure(responses[loserIndex], HttpStatusCode.Conflict, "STAT_CONFIG_CAS_CONFLICT");
                var winnerCommand = winnerIndex == 0 ? "p8-core-016-race-a" : "p8-core-016-race-b";
                _corePrimary = ParseIdentity(responses[winnerIndex].Json, requireReceipt: true);
                RequireIdentityContract(_corePrimary);
                await RequireDirectIdentityAsync(
                    _corePrimary,
                    winnerCommand,
                    Actor("system_admin").Id,
                    ct);
                RequireVersionAdvanced(before, _corePrimary);
                var receiptCount = await CountReceiptsAsync("p8-core-016-race-a", ct) +
                                   await CountReceiptsAsync("p8-core-016-race-b", ct);
                HarnessAssert.Equal(1L, receiptCount, "CAS race wrote more than the winning receipt");
                return new CaseObservation(
                    "Different commands sharing one CAS produced one commit and one conflict.",
                    "raceWinners=1;raceLosers=1;receiptDelta=1");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-017",
            "system_admin",
            ["p8-core-017-key-order"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var payload = LabelPayload(
                    "p8.core.key-order",
                    "P8 Key Order",
                    "GLOBAL",
                    null,
                    usage: "STATISTIC",
                    dataType: "SHORT_TEXT");
                var firstEnvelope = Envelope(
                    "p8-core-017-key-order",
                    0,
                    EmptyConfigHash,
                    payload);
                var first = await _api.PostAsync(
                    "api/labels/config",
                    firstEnvelope,
                    Actor("system_admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK, "key-order create");
                _coreCanonical = ParseIdentity(first.Json, requireReceipt: true);
                RequireIdentityContract(_coreCanonical);
                await RequireDirectIdentityAsync(
                    _coreCanonical,
                    "p8-core-017-key-order",
                    Actor("system_admin").Id,
                    ct);

                var reordered = ReverseObject(firstEnvelope);
                var replay = await _api.PostAsync(
                    "api/labels/config",
                    reordered,
                    Actor("system_admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "key-order replay");
                HarnessAssert.Equal(CanonicalResponse(first), CanonicalResponse(replay),
                    "Property-order-only replay changed the durable response");
                HarnessAssert.Equal(_coreCanonical.ConfigHash, RecomputeConfigHash(_coreCanonical),
                    "Independent canonical hash recomputation mismatch");
                return new CaseObservation(
                    "Reversed JSON key order preserved request identity and canonical config hash.",
                    "keyOrderIgnored=true;hashRecomputed=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-018",
            "system_admin",
            [],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var read = await ReadLabelAsync(Actor("system_admin"), _coreCanonical.LabelId, ct);
                var direct = await RequireLabelDocumentAsync(_coreCanonical.LabelId, ct);
                HarnessAssert.Equal(read.ConfigHash, BsonString(direct, "configHash"),
                    "Persisted hash differs from API readback");
                HarnessAssert.Equal(read.ConfigHash, RecomputeConfigHash(read),
                    "Persisted hash differs from independent canonical recomputation");
                _coreCanonical = read;
                _coreLineageBeforeAuditOnlyVersion = (JsonArray)read.Versions.DeepClone();
                return new CaseObservation(
                    "Persisted owner hash matched API and independent canonical recomputation.",
                    "persistedHashRecomputed=true;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-019",
            "system_admin",
            ["p8-core-019-audit-excluded"],
            LabelMutationWrites,
            LabelMutationWrites,
            async () =>
            {
                var before = _coreCanonical;
                var updated = (await UpdateLabelAsync(
                    Actor("system_admin"),
                    before,
                    "p8-core-019-audit-excluded",
                    LabelPayload(
                        before.LabelCode,
                        "P8 Key Order",
                        "GLOBAL",
                        null,
                        usage: "STATISTIC",
                        dataType: "SHORT_TEXT"),
                    ct)).Identity;
                HarnessAssert.Equal(before.ConfigId, updated.ConfigId, "Audit-only version changed config family");
                HarnessAssert.Equal(before.VersionNo + 1, updated.VersionNo, "Audit-only versionNo did not advance");
                HarnessAssert.Equal(before.Revision + 1, updated.Revision, "Audit-only revision did not advance");
                HarnessAssert.True(before.VersionId != updated.VersionId, "Audit-only versionId did not advance");
                HarnessAssert.Equal(before.ConfigHash, updated.ConfigHash,
                    "Version/audit metadata contaminated semantic configHash");
                _coreCanonical = updated;
                return new CaseObservation(
                    "Semantic no-op created new audit/version metadata while configHash stayed stable.",
                    "auditMetadataExcluded=true;revisionDelta=1;hashStable=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-CORE-020",
            "system_admin",
            [],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var read = await ReadLabelAsync(Actor("system_admin"), _coreCanonical.LabelId, ct);
                HarnessAssert.Equal(
                    _coreLineageBeforeAuditOnlyVersion.Count + 1,
                    read.Versions.Count,
                    "Readback lineage did not append exactly one immutable version");
                for (var index = 0; index < _coreLineageBeforeAuditOnlyVersion.Count; index++)
                {
                    HarnessAssert.Equal(
                        Canonicalize(_coreLineageBeforeAuditOnlyVersion[index]!),
                        Canonicalize(read.Versions[index]!),
                        $"Historical version snapshot {index + 1} changed");
                }
                var versions = read.Versions.OfType<JsonObject>()
                    .OrderBy(version => RequiredInt(version, "versionNo"))
                    .ToArray();
                for (var index = 1; index < versions.Length; index++)
                {
                    HarnessAssert.Equal(
                        RequiredString(versions[index - 1], "versionId"),
                        OptionalString(versions[index], "previousVersionId"),
                        $"Version lineage link {index + 1} is broken");
                }
                var direct = await RequireLabelDocumentAsync(read.LabelId, ct);
                HarnessAssert.Equal(read.Versions.Count,
                    direct.GetValue("versionSnapshots", new BsonArray()).AsBsonArray.Count,
                    "Direct Mongo lineage count differs from readback");
                return new CaseObservation(
                    "Readback preserved every historical snapshot byte-semantically and chained lineage.",
                    "immutableReadbackLineage=true;delta=zero");
            },
            ct);
    }

    private static JsonObject ReverseObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var property in source.Reverse())
        {
            result[property.Key] = property.Value switch
            {
                JsonObject nested => ReverseObject(nested),
                JsonArray array => new JsonArray(array.Select(item => item?.DeepClone()).ToArray()),
                { } value => value.DeepClone(),
                _ => null
            };
        }
        return result;
    }
}
