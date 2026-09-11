using System.Net;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP804VersionCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-BAS-011",
            "system_admin",
            [
                "p8-bas-011-create",
                "p8-bas-011-update",
                "p8-bas-011-stale"
            ],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("011");
                var initialPayload = BasicDirectPayload([
                    BasicTarget(
                        "FIELD",
                        BasicTargetKeys["NUMBER"][0],
                        "NUMBER",
                        "SUM"),
                    BasicTarget(
                        "TABLE_METRIC",
                        "basic-table:metric:number",
                        "NUMBER",
                        "SUM"),
                    BasicTarget(
                        "ROW_LABEL",
                        BasicRowLabelCode,
                        "TEXT",
                        "COUNT")
                ]);
                var (_, initial) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-011-create",
                    initialPayload,
                    ct);
                RequireP804ModernStatisticDependencyPins(initial);
                var updatedPayload = BasicDirectPayload(
                    [
                        BasicTarget(
                            "FIELD",
                            BasicTargetKeys["NUMBER"][0],
                            "NUMBER",
                            "SUM"),
                        BasicTarget(
                            "FIELD",
                            BasicTargetKeys["TEXT"][0],
                            "TEXT",
                            "JOIN"),
                        BasicTarget(
                            "TABLE_METRIC",
                            "basic-table:metric:number",
                            "NUMBER",
                            "SUM"),
                        BasicTarget(
                            "ROW_LABEL",
                            BasicRowLabelCode,
                            "TEXT",
                            "COUNT")
                    ],
                    groupingHints: ["ASSIGNMENT", "UNIT"],
                    periodRule: BasicPeriodRule(
                        "SINGLE_PERIOD",
                        periodKey: "2026-08"),
                    detailHints: BasicDetailHints(
                        includeSourceRows: true,
                        maxTextChars: 24000));
                var (_, updated) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-011-update",
                    updatedPayload,
                    ct,
                    initial.Revision,
                    initial.ConfigHash);
                HarnessAssert.Equal(initial.ConfigId, updated.ConfigId,
                    "Basic draft update replaced config identity");
                HarnessAssert.Equal(initial.VersionId, updated.VersionId,
                    "Basic draft update replaced version identity");
                HarnessAssert.Equal(1, updated.VersionNo,
                    "Basic draft update advanced version number");
                HarnessAssert.Equal(initial.Revision + 1, updated.Revision,
                    "Basic draft update did not advance revision exactly once");
                HarnessAssert.True(!string.Equals(
                        initial.ConfigHash,
                        updated.ConfigHash,
                        StringComparison.Ordinal),
                    "Basic content update did not change configHash");
                RequireBasicPayloadContract(updated, updatedPayload);
                RequireP804ModernStatisticDependencyPins(updated);

                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-011/stale-cas",
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        Envelope(
                            "p8-bas-011-stale",
                            initial.Revision,
                            initial.ConfigHash,
                            updatedPayload.DeepClone()),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    "BASIC_SUMMARY_CONFIG_CAS_MISMATCH",
                    ct);

                var versions = await ListBasicVersionsAsync(
                    actor,
                    fixture,
                    ct);
                HarnessAssert.Equal(1, versions.Count,
                    "Basic version list contains an unexpected extra version");
                RequireVersionIdentity(
                    versions[0] as System.Text.Json.Nodes.JsonObject
                    ?? throw new InvalidOperationException(
                        "Basic version list item is malformed."),
                    updated);
                var detail = await ReadBasicVersionAsync(
                    actor,
                    fixture,
                    1,
                    ct);
                HarnessAssert.Equal(
                    updated.ConfigHash,
                    detail.ConfigHash,
                    "Basic version detail hash differs from current readback");
                RequireBasicPayloadContract(detail, updatedPayload);
                return new CaseObservation(
                    "Draft revision/CAS and readback remained consistent while TABLE_METRIC/ROW_LABEL used the frozen P8-03 snapshot despite live drift and cross-scope duplication.",
                    "versionNo=1;revision=1->2;targets=FIELD+TABLE_METRIC+ROW_LABEL;statPin=exact;rowLabelPin=frozenV1;liveV2+unitDuplicate=ignored;staleCAS=409+0W;detail=exact");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-012",
            "system_admin",
            ["p8-bas-012-lock"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("011");
                var current = await ReadBasicConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: true);
                var first = await PostBasicActionAsync(
                    actor,
                    fixture,
                    "lock",
                    "p8-bas-012-lock",
                    current,
                    ct);
                var locked = first.Identity;
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "Basic lock did not set LOCKED status");
                HarnessAssert.Equal(current.Revision + 1, locked.Revision,
                    "Basic lock did not advance revision exactly once");
                HarnessAssert.Equal(current.ConfigHash, locked.ConfigHash,
                    "Basic lock changed content hash");
                HarnessAssert.Equal(current.VersionId, locked.VersionId,
                    "Basic lock changed version identity");
                var afterFirst = await CaptureDatabaseSnapshotAsync(ct);

                var replay = await _api.PostAsync(
                    $"{BasicConfigRoute(fixture)}/lock",
                    Envelope(
                        "p8-bas-012-lock",
                        current.Revision,
                        current.ConfigHash,
                        BasicActionPayload()),
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "exact Basic lock replay");
                HarnessAssert.Equal(
                    CanonicalResponse(first.Response),
                    CanonicalResponse(replay),
                    "Exact Basic lock replay response changed");
                var replayIdentity = ParseBasicIdentity(
                    replay.Json,
                    requireReceipt: true);
                HarnessAssert.Equal(locked.ConfigHash, replayIdentity.ConfigHash,
                    "Exact Basic lock replay changed result identity");
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-BAS-012/replay",
                    BuildDeltas(afterFirst, afterReplay),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                HarnessAssert.Equal(
                    1L,
                    await CountBasicReceiptsAsync(
                        locked.OwnerId,
                        "p8-bas-012-lock",
                        ct),
                    "Exact Basic lock replay wrote a second receipt");

                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-012/divergent-replay",
                    () => _api.PostAsync(
                        $"{BasicConfigRoute(fixture)}/lock",
                        Envelope(
                            "p8-bas-012-lock",
                            locked.Revision,
                            locked.ConfigHash,
                            BasicActionPayload()),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                    null,
                    "BASIC_SUMMARY_COMMAND_REPLAY_CHANGED",
                    ct);
                return new CaseObservation(
                    "Lock changed only lifecycle revision/status; exact replay reused one durable receipt and divergent replay wrote nothing.",
                    "DRAFT->LOCKED;revision+1;contentHash+versionId=stable;exactReplay=0W+sameResponse+oneReceipt;divergent=409+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-013",
            "system_admin",
            ["p8-bas-013-locked-put", "p8-bas-013-next-draft"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("011");
                var locked = await ReadBasicConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-BAS-013 fixture is not locked");
                RequireP804ModernStatisticDependencyPins(locked);

                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-013/locked-put",
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        Envelope(
                            "p8-bas-013-locked-put",
                            locked.Revision,
                            locked.ConfigHash,
                            locked.Payload.DeepClone()),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    "BASIC_SUMMARY_CONFIG_VERSION_LOCKED",
                    ct);

                var (_, draft) = await PostBasicActionAsync(
                    actor,
                    fixture,
                    "next-draft",
                    "p8-bas-013-next-draft",
                    locked,
                    ct);
                HarnessAssert.Equal(locked.VersionNo + 1, draft.VersionNo,
                    "Basic next draft version number mismatch");
                HarnessAssert.Equal(0L, draft.Revision,
                    "Basic next draft did not reset revision to zero");
                HarnessAssert.Equal("DRAFT", draft.Status,
                    "Basic next draft did not become DRAFT");
                HarnessAssert.True(!string.Equals(
                        locked.VersionId,
                        draft.VersionId,
                        StringComparison.Ordinal),
                    "Basic next draft reused locked versionId");
                HarnessAssert.Equal(locked.VersionId, draft.PreviousVersionId,
                    "Basic next draft lineage does not bind locked version");
                HarnessAssert.Equal(locked.ConfigHash, draft.ConfigHash,
                    "Basic next draft changed copied content hash");
                HarnessAssert.Equal(
                    Canonicalize(locked.Payload),
                    Canonicalize(draft.Payload),
                    "Basic next draft did not copy locked payload exactly");
                HarnessAssert.True(
                    locked.DependencyPins.SequenceEqual(
                        draft.DependencyPins,
                        StringComparer.Ordinal),
                    "Basic next draft did not copy dependency pins exactly");
                RequireP804ModernStatisticDependencyPins(draft);
                HarnessAssert.Equal(2, draft.Versions.Count,
                    "Basic next draft lineage lacks two version snapshots");

                var lockedDetail = await ReadBasicVersionAsync(
                    actor,
                    fixture,
                    1,
                    ct);
                HarnessAssert.Equal("LOCKED", lockedDetail.Status,
                    "Locked version detail was mutated by next-draft");
                HarnessAssert.Equal(locked.ConfigHash, lockedDetail.ConfigHash,
                    "Locked version hash was mutated by next-draft");
                var current = await ReadBasicConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(draft.VersionId, current.VersionId,
                    "Next draft is not the current readable version");
                return new CaseObservation(
                    "Locked content rejected PUT; next-draft copied payload and frozen statistic/ROW_LABEL pins into version N+1 with exact lineage.",
                    "lockedPut=409+0W;N=1 LOCKED;N+1=2 DRAFT revision0;previousVersionId=N;TABLE_METRIC+ROW_LABEL=preserved;frozenLabelPin=exact;lockedDetail=stable");
            },
            ct);
    }
}
