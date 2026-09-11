using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP805AdvancedSummaryCasesAsync(CancellationToken ct)
    {
        await HideP810OnlyUsersForP805Async(ct);
        await SeedP805FixturesAsync(ct);
        await WarmP805InfrastructureAsync(ct);
        await RunP805ConfigurationCasesAsync(ct);
        await RunP805LimitCasesAsync(ct);
        await RunP805BarrierCasesAsync(ct);
        await RunP805QuotaCasesAsync(ct);
        await RunP805IsolationCaseAsync(ct);
    }

    private async Task RunP805ConfigurationCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-ADV-001",
            "system_admin",
            ["p8-adv-001-empty-draft"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("001");
                var virtualConfig = await ReadAdvancedConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: false);
                HarnessAssert.True(virtualConfig.IsVirtualEmpty,
                    "Initial Advanced config is not virtual empty");
                HarnessAssert.Equal(null, virtualConfig.ValidationReceipt,
                    "Virtual Advanced config exposed a synthetic validation receipt");

                var empty = AdvancedPayload(fixture, targetCount: 0);
                var (_, persisted) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-001-empty-draft",
                    empty,
                    ct);
                HarnessAssert.Equal(false, persisted.IsVirtualEmpty,
                    "Persisted empty Advanced draft remained virtual");
                HarnessAssert.Equal("DRAFT", persisted.Status,
                    "Persisted empty Advanced config is not DRAFT");
                HarnessAssert.Equal(1, persisted.VersionNo,
                    "Initial Advanced versionNo mismatch");
                HarnessAssert.Equal(1L, persisted.Revision,
                    "Initial Advanced revision mismatch");
                HarnessAssert.Equal(Canonicalize(empty),
                    Canonicalize(persisted.Payload),
                    "Persisted empty Advanced payload readback mismatch");
                return new CaseObservation(
                    "Virtual and persisted empty Advanced metadata stayed deterministic, DRAFT, config-only and result-free.",
                    "virtualEmpty=true;persistedEmpty=true;targets=0;draftReceipt=null;hash+readback+mongo=true;results=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-002",
            "system_admin",
            ["p8-adv-002-replay"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("002");
                var initial = await ReadAdvancedConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: false);
                var payload = AdvancedPayload(fixture, targetCount: 2);
                var envelope = Envelope(
                    "p8-adv-002-replay",
                    initial.Revision,
                    initial.ConfigHash,
                    payload);
                var first = await _api.PutAsync(
                    AdvancedConfigRoute(fixture),
                    envelope,
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK,
                    "P8 Advanced initial replay mutation");
                var firstIdentity = ParseAdvancedIdentity(
                    first.Json,
                    requireCommandReceipt: true);
                RequireAdvancedIdentityContract(firstIdentity, fixture);
                await RequireDirectAdvancedIdentityAsync(
                    firstIdentity,
                    fixture,
                    "p8-adv-002-replay",
                    "UPSERT_ADVANCED_SUMMARY_CONFIG",
                    actor.Id,
                    requirePersisted: true,
                    ct);

                var replayBefore = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await _api.PutAsync(
                    AdvancedConfigRoute(fixture),
                    envelope,
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK,
                    "P8 Advanced exact replay");
                HarnessAssert.Equal(Canonicalize(firstIdentity.Raw),
                    Canonicalize(ApiHarnessClient.RequiredObject(
                        replay.Json,
                        "P8 Advanced exact replay response")),
                    "Exact Advanced replay response drifted");
                VerifyCollectionContract(
                    "P8-ADV-002/exact-replay",
                    BuildDeltas(replayBefore,
                        await CaptureDatabaseSnapshotAsync(ct)),
                    Array.Empty<string>(),
                    Array.Empty<string>());

                var divergent = AdvancedPayload(fixture, targetCount: 3);
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-002/divergent-replay",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(fixture),
                        Envelope(
                            "p8-adv-002-replay",
                            initial.Revision,
                            initial.ConfigHash,
                            divergent),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                    null,
                    null,
                    ct);

                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-002/stale-cas",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(fixture),
                        Envelope(
                            "p8-adv-002-stale-cas",
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
                    "CAS/hash was enforced; exact replay returned byte-equivalent typed state and divergent replay/stale CAS wrote nothing.",
                    "put=1;exactReplay=0W+same;divergentReplay=409+0W;staleCas=409+0W;hashRecompute=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-003",
            "system_admin",
            [
                "p8-adv-003-draft",
                "p8-adv-003-lock-v1",
                "p8-adv-003-next-draft"
            ],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = AdvancedFixture("003");
                var (_, draft) = await PutAdvancedConfigAsync(
                    actor,
                    fixture,
                    "p8-adv-003-draft",
                    AdvancedPayload(fixture, targetCount: 3),
                    ct);
                var (_, locked) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "lock",
                    "p8-adv-003-lock-v1",
                    draft,
                    ct);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "Advanced v1 did not lock");
                HarnessAssert.Equal(draft.VersionId, locked.VersionId,
                    "Lock replaced Advanced version identity");
                HarnessAssert.Equal(draft.ConfigHash, locked.ConfigHash,
                    "Lock changed Advanced configHash");
                RequireAdvancedValidationReceipt(locked, fixture);

                var frozen = (JsonObject)locked.Versions
                    .OfType<JsonObject>()
                    .Single(version => RequiredInt(
                        ApiHarnessClient.RequiredObject(
                            version["identity"],
                            "Advanced v1 snapshot identity"),
                        "versionNo") == 1)
                    .DeepClone();
                var (_, next) = await PostAdvancedActionAsync(
                    actor,
                    fixture,
                    "next-draft",
                    "p8-adv-003-next-draft",
                    locked,
                    ct);
                HarnessAssert.Equal("DRAFT", next.Status,
                    "Advanced next version is not DRAFT");
                HarnessAssert.Equal(2, next.VersionNo,
                    "Advanced next versionNo mismatch");
                HarnessAssert.Equal(locked.VersionId, next.PreviousVersionId,
                    "Advanced next draft previousVersionId mismatch");
                HarnessAssert.True(!string.Equals(
                        locked.VersionId,
                        next.VersionId,
                        StringComparison.Ordinal),
                    "Advanced next draft reused immutable versionId");
                var afterFrozen = next.Versions.OfType<JsonObject>()
                    .Single(version => RequiredInt(
                        ApiHarnessClient.RequiredObject(
                            version["identity"],
                            "Advanced v1 snapshot identity after next draft"),
                        "versionNo") == 1);
                HarnessAssert.Equal(Canonicalize(frozen),
                    Canonicalize(afterFrozen),
                    "Advanced v1 snapshot mutated after next-draft");
                return new CaseObservation(
                    "DRAFT→LOCKED→next DRAFT preserved immutable v1 lineage and generated lock-only validation metadata.",
                    "v1=draft>locked+free;v2=draft;previous=v1;v1SnapshotImmutable=true;receipt=lockOnly");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-ADV-004",
            "outsider_b",
            ["p8-adv-004-typed-dependency"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var outsider = Actor("outsider_b");
                var admin = Actor("system_admin");
                var fixture = AdvancedFixture("004");
                var unknownAssignment = ObjectId.GenerateNewId().ToString();
                var known = await _api.GetAsync(
                    AdvancedConfigRoute(fixture),
                    outsider.Token,
                    ct: ct);
                var unknownRoute = AdvancedConfigRoute(fixture)
                    .Replace(fixture.Assignment.Id,
                        unknownAssignment,
                        StringComparison.Ordinal);
                var unknown = await _api.GetAsync(
                    unknownRoute,
                    outsider.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(known, HttpStatusCode.Forbidden,
                    "P8 Advanced foreign owner disclosure barrier");
                ApiHarnessClient.ExpectStatus(unknown, HttpStatusCode.Forbidden,
                    "P8 Advanced unknown owner disclosure barrier");
                HarnessAssert.Equal(
                    ApiHarnessClient.FindStringRecursive(known.Json, "errorCode")
                    ?? ApiHarnessClient.FindStringRecursive(known.Json, "code"),
                    ApiHarnessClient.FindStringRecursive(unknown.Json, "errorCode")
                    ?? ApiHarnessClient.FindStringRecursive(unknown.Json, "code"),
                    "Advanced auth-before-existence responses disclosed owner existence");

                var incompatible = AdvancedPayload(fixture);
                ((JsonObject)((JsonArray)((JsonObject)
                    ((JsonArray)incompatible["sections"]!)[0]!)
                    ["targets"]!)[0]!)["operation"] = "JOIN";
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-ADV-004/typed-dependency",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(fixture),
                        Envelope(
                            "p8-adv-004-typed-dependency",
                            0,
                            EmptyConfigHash,
                            incompatible),
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sections[0].targets[0].operation",
                    "OPERATION_UNSUPPORTED",
                    ct);
                return new CaseObservation(
                    "Authorization preceded owner existence and incompatible typed dependencies failed closed.",
                    "knownForeign=403;unknown=403;sameDisclosure=true;NUMBER+JOIN=400+0W");
            },
            ct);
    }
}
