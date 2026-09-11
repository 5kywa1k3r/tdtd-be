using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP810ConfigurationCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-UI-009",
            "p810_owner",
            ["p810-basic-next-draft-009", "p810-basic-edit-009"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("p810_owner");
                var locked = await ReadBasicConfigAsync(
                    actor,
                    _p810BasicFixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-UI-009 source Basic version is not locked");
                var (_, draft) = await PostBasicActionAsync(
                    actor,
                    _p810BasicFixture,
                    "next-draft",
                    "p810-basic-next-draft-009",
                    locked,
                    ct);
                var (_, edited) = await PutBasicConfigAsync(
                    actor,
                    _p810BasicFixture,
                    "p810-basic-edit-009",
                    (JsonObject)draft.Payload.DeepClone(),
                    ct,
                    expectedRevision: draft.Revision,
                    expectedConfigHash: draft.ConfigHash);
                HarnessAssert.Equal("DRAFT", edited.Status,
                    "P8-UI-009 Basic edit is not DRAFT");
                HarnessAssert.Equal(locked.VersionNo + 1, edited.VersionNo,
                    "P8-UI-009 Basic version did not advance exactly once");
                HarnessAssert.Equal(draft.Revision + 1, edited.Revision,
                    "P8-UI-009 Basic edit did not advance CAS revision");
                HarnessAssert.True(!string.IsNullOrWhiteSpace(edited.ReceiptId),
                    "P8-UI-009 Basic edit lacks receipt");
                _p810BasicStale = draft;
                _p810BasicDraft = edited;
                return new CaseObservation(
                    "Basic next-draft and typed edit used exact commands, revision/hash CAS, durable receipts, direct Mongo and stable GET readback.",
                    $"version={locked.VersionNo}->{edited.VersionNo};revision={draft.Revision}->{edited.Revision};status=DRAFT;commands=2;receipt=true;hash={edited.ConfigHash}");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-010",
            "p810_owner",
            ["p810-basic-stale-010", "p810-basic-lock-010"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("p810_owner");
                var current = _p810BasicDraft
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P8-UI-009 did not produce the Basic draft.");
                var stale = _p810BasicStale
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-UI-009 did not preserve the Basic stale CAS identity.");
                await RequireZeroWriteBasicRejectionAsync(
                    "P8-UI-010/stale-cas",
                    () => _api.PutAsync(
                        BasicConfigRoute(_p810BasicFixture),
                        Envelope(
                            "p810-basic-stale-010",
                            stale.Revision,
                            stale.ConfigHash,
                            current.Payload.DeepClone()),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    null,
                    ct);
                var (_, locked) = await PostBasicActionAsync(
                    actor,
                    _p810BasicFixture,
                    "lock",
                    "p810-basic-lock-010",
                    current,
                    ct);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-UI-010 Basic version did not lock");
                HarnessAssert.Equal(current.VersionId, locked.VersionId,
                    "P8-UI-010 Basic lock changed versionId");
                HarnessAssert.Equal(current.ConfigHash, locked.ConfigHash,
                    "P8-UI-010 Basic lock changed configHash");
                HarnessAssert.Equal(current.Revision + 1, locked.Revision,
                    "P8-UI-010 Basic lock revision mismatch");
                return new CaseObservation(
                    "Basic stale CAS failed with zero writes, then exact lock preserved version/hash and advanced only lifecycle revision with receipt/readback.",
                    $"stale=409+0W;version={locked.VersionNo};status=LOCKED;revision={current.Revision}->{locked.Revision};hashStable=true;receipt={locked.ReceiptId}");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-011",
            "p810_owner",
            ["p810-advanced-next-draft-011", "p810-advanced-edit-011"],
            AdvancedConfigWrites,
            AdvancedConfigWrites,
            async () =>
            {
                var actor = Actor("p810_owner");
                var locked = await ReadAdvancedConfigAsync(
                    actor,
                    _p810AdvancedFixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-UI-011 source Advanced version is not locked");
                var (_, draft) = await PostAdvancedActionAsync(
                    actor,
                    _p810AdvancedFixture,
                    "next-draft",
                    "p810-advanced-next-draft-011",
                    locked,
                    ct);
                var (_, edited) = await PutAdvancedConfigAsync(
                    actor,
                    _p810AdvancedFixture,
                    "p810-advanced-edit-011",
                    (JsonObject)draft.Payload.DeepClone(),
                    ct,
                    expectedRevision: draft.Revision,
                    expectedConfigHash: draft.ConfigHash);
                var canonical = Canonicalize(edited.Payload);
                HarnessAssert.Equal("DRAFT", edited.Status,
                    "P8-UI-011 Advanced edit is not DRAFT");
                HarnessAssert.True(canonical.Contains("sections", StringComparison.Ordinal),
                    "P8-UI-011 Advanced section contract is absent");
                HarnessAssert.True(!string.IsNullOrWhiteSpace(edited.CommandReceiptId),
                    "P8-UI-011 Advanced edit lacks command receipt");
                _p810AdvancedStale = draft;
                _p810AdvancedDraft = edited;
                return new CaseObservation(
                    "Advanced next-draft/edit preserved typed sections and enforced exact command, CAS, budget/quota metadata and durable readback.",
                    $"version={locked.VersionNo}->{edited.VersionNo};revision={draft.Revision}->{edited.Revision};sections=typed;status=DRAFT;receipt=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-012",
            "p810_owner",
            ["p810-advanced-stale-012", "p810-advanced-lock-012"],
            AdvancedLockWrites,
            AdvancedLockWrites,
            async () =>
            {
                var actor = Actor("p810_owner");
                var current = _p810AdvancedDraft
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P8-UI-011 did not produce the Advanced draft.");
                var stale = _p810AdvancedStale
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-UI-011 did not preserve the Advanced stale CAS identity.");
                await RequireZeroWriteAdvancedRejectionAsync(
                    "P8-UI-012/stale-cas",
                    () => _api.PutAsync(
                        AdvancedConfigRoute(_p810AdvancedFixture),
                        Envelope(
                            "p810-advanced-stale-012",
                            stale.Revision,
                            stale.ConfigHash,
                            current.Payload.DeepClone()),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    null,
                    ct);
                var (_, locked) = await PostAdvancedActionAsync(
                    actor,
                    _p810AdvancedFixture,
                    "lock",
                    "p810-advanced-lock-012",
                    current,
                    ct);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-UI-012 Advanced version did not lock");
                HarnessAssert.Equal(current.VersionId, locked.VersionId,
                    "P8-UI-012 Advanced lock changed versionId");
                HarnessAssert.Equal(current.ConfigHash, locked.ConfigHash,
                    "P8-UI-012 Advanced lock changed configHash");
                RequireAdvancedValidationReceipt(locked, _p810AdvancedFixture);
                HarnessAssert.Equal("CONFIG_ONLY_NO_DATASET",
                    RequiredString(locked.ValidationReceipt!, "validationMode"),
                    "P8-UI-012 Advanced validation crossed into hierarchy execution");
                return new CaseObservation(
                    "Advanced stale CAS wrote nothing; lock produced version/hash-stable CONFIG_ONLY_NO_DATASET validation and no hierarchy output.",
                    $"stale=409+0W;version={locked.VersionNo};status=LOCKED;validation=CONFIG_ONLY_NO_DATASET;hierarchyWrites=0;receipt={locked.CommandReceiptId}");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-013",
            "p810_owner",
            ["p810-diff-next-draft-013", "p810-diff-edit-013"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var actor = Actor("p810_owner");
                var locked = await ReadDiffConfigAsync(
                    actor,
                    _p810DiffFixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-UI-013 source Diff version is not locked");
                var (_, draft) = await PostDiffActionAsync(
                    actor,
                    _p810DiffFixture,
                    "next-draft",
                    "p810-diff-next-draft-013",
                    locked,
                    ct);
                var (_, edited) = await PutDiffConfigAsync(
                    actor,
                    _p810DiffFixture,
                    "p810-diff-edit-013",
                    (JsonObject)draft.Payload.DeepClone(),
                    ct,
                    expectedRevision: draft.Revision,
                    expectedConfigHash: draft.ConfigHash);
                var canonical = Canonicalize(edited.Payload);
                foreach (var token in new[]
                         {
                             "concept", "period", "direction", "scope", "missing"
                         })
                {
                    HarnessAssert.True(canonical.Contains(token, StringComparison.OrdinalIgnoreCase),
                        $"P8-UI-013 Diff payload lacks typed {token} contract");
                }
                HarnessAssert.True(!string.IsNullOrWhiteSpace(edited.ReceiptId),
                    "P8-UI-013 Diff edit lacks receipt");
                _p810DiffStale = draft;
                _p810DiffDraft = edited;
                return new CaseObservation(
                    "Diff next-draft/edit preserved concept, period, direction, scope and missing-value policy under exact command/CAS/receipt/readback.",
                    $"version={locked.VersionNo}->{edited.VersionNo};revision={draft.Revision}->{edited.Revision};typedDimensions=5;status=DRAFT;receipt=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-014",
            "p810_owner",
            ["p810-diff-stale-014", "p810-diff-lock-014"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var actor = Actor("p810_owner");
                var current = _p810DiffDraft
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P8-UI-013 did not produce the Diff draft.");
                var stale = _p810DiffStale
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-UI-013 did not preserve the Diff stale CAS identity.");
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-UI-014/stale-cas",
                    () => _api.PutAsync(
                        DiffConfigRoute(_p810DiffFixture),
                        Envelope(
                            "p810-diff-stale-014",
                            stale.Revision,
                            stale.ConfigHash,
                            current.Payload.DeepClone()),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    null,
                    ct);
                var (_, locked) = await PostDiffActionAsync(
                    actor,
                    _p810DiffFixture,
                    "lock",
                    "p810-diff-lock-014",
                    current,
                    ct);
                HarnessAssert.Equal("LOCKED", locked.Status,
                    "P8-UI-014 Diff version did not lock");
                HarnessAssert.Equal(current.VersionId, locked.VersionId,
                    "P8-UI-014 Diff lock changed versionId");
                HarnessAssert.Equal(current.ConfigHash, locked.ConfigHash,
                    "P8-UI-014 Diff lock changed configHash");
                HarnessAssert.Equal("BLOCKED_UNTIL_P9", locked.RuntimeEligibility,
                    "P8-UI-014 Diff lock crossed into result execution");
                return new CaseObservation(
                    "Diff stale CAS failed with zero writes; lock kept version/hash stable and runtime blocked, so all Diff result/export stores stayed unchanged.",
                    $"stale=409+0W;status=LOCKED;hashStable=true;eligibility=BLOCKED_UNTIL_P9;resultDelta=0;receipt={locked.ReceiptId}");
            },
            ct);
    }
}
