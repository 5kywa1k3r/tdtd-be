using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP807VersionCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-FLW-001",
            "system_admin",
            ["p8-flw-001-create"],
            FlowDefinitionWrites,
            FlowDefinitionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                _mainFlow = await CreateFlowDraftAsync(
                    actor,
                    "v-exclude",
                    "p8-flw-001-create",
                    BuildP807MappedFlowPayload(),
                    ct);
                var draft = RequireMainFlow().Version;
                HarnessAssert.Equal(null, draft.ContributionPolicy,
                    "Unlocked Flow draft prematurely persisted contribution policy");
                RequireNonEmptyP7MappingBaseline(draft);
                RequireEmptyStatisticProfile(draft, "V_EXCLUDE authored draft");
                return new CaseObservation(
                    "V_EXCLUDE draft was authored through real Kestrel with one non-empty structured P7 mapping rule and exact server-managed catalog/form pins.",
                    "v=1;status=DRAFT;p7MappingRules=1;policyPending=true;api+mongo+receipt+audit=true;previewApplyRun=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-002",
            "system_admin",
            ["p8-flw-002-lock-default"],
            FlowDefinitionWrites,
            FlowDefinitionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var main = RequireMainFlow();
                _excludeLockOriginalRoute = FlowLockRoute(main);
                _excludeLockOriginalRequest = FlowLockRequest(
                    main,
                    "p8-flw-002-lock-default",
                    contributionPolicy: null,
                    acknowledgeWarning: null);
                var locked = await LockFlowVersionAsync(
                    actor,
                    main,
                    "p8-flw-002-lock-default",
                    contributionPolicy: null,
                    acknowledgeWarning: null,
                    ct);
                HarnessAssert.Equal("EXCLUDE", locked.ContributionPolicy,
                    "Omitted contribution policy did not default to EXCLUDE");
                HarnessAssert.True(string.IsNullOrWhiteSpace(
                        locked.ContributionWarning),
                    "Default EXCLUDE unexpectedly emitted an INCLUDE warning");
                HarnessAssert.Equal(actor.Id, locked.LockedByUserId,
                    "V_EXCLUDE locked actor mismatch");
                RequireNonEmptyP7MappingBaseline(locked);
                RequireEmptyStatisticProfile(locked, "V_EXCLUDE locked");
                _excludeLocked = locked;
                _excludeLockedBson = (await ReadFlowVersionRowAsync(
                    locked.Id,
                    ct)).ToBson();
                var family = await ReadFlowFamilyAsync(actor, main.FamilyId, ct);
                _mainFlow = main with
                {
                    FamilyRevision = RequiredInt(family, "familyRevision")
                };
                return new CaseObservation(
                    "Omitted policy locked V_EXCLUDE with the frozen P7 payload/hash, immutable EXCLUDE policy hash and no contribution outcome.",
                    "v=1;status=LOCKED;policy=EXCLUDE(default);warning=none;p7PayloadHashPinned=true;outcomes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-003",
            "system_admin",
            ["p8-flw-003-reopen", "p8-flw-003-save-none"],
            FlowDefinitionWrites,
            FlowDefinitionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var main = RequireMainFlow();
                var exclude = RequireExcludeLocked();
                var reopened = await ReopenFlowVersionAsync(
                    actor,
                    main,
                    exclude,
                    "p8-flw-003-reopen",
                    ct);
                HarnessAssert.Equal(2, reopened.Version.VersionNo,
                    "Reopened INCLUDE draft versionNo mismatch");
                HarnessAssert.Equal(null, reopened.Version.ContributionPolicy,
                    "Reopened draft inherited a mutable policy field");
                HarnessAssert.Equal(exclude.PayloadHash,
                    reopened.Version.PayloadHash,
                    "Reopened draft crossed the exact P7 baseline");

                var nonePayload = BuildP807MappedFlowPayload();
                nonePayload["statisticProfile"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["diffMode"] = "NONE"
                };
                var saved = await SaveFlowDraftAsync(
                    actor,
                    reopened,
                    "p8-flw-003-save-none",
                    nonePayload,
                    ct);
                HarnessAssert.Equal(exclude.PayloadHash,
                    saved.Version.PayloadHash,
                    "diffMode=NONE did not normalize to the frozen empty profile");
                HarnessAssert.Equal(exclude.PayloadJson,
                    saved.Version.PayloadJson,
                    "V_INCLUDE authoring changed P7 payload bytes");
                RequireNonEmptyP7MappingBaseline(saved.Version);
                RequireEmptyStatisticProfile(
                    saved.Version,
                    "V_INCLUDE NONE-normalized draft");
                _includeDraft = saved;
                return new CaseObservation(
                    "Reopen created version 2; saving diffMode=NONE normalized to the exact V_EXCLUDE P7 payload bytes/hash while leaving contribution policy unset until lock.",
                    "v=2;status=DRAFT;profile=NONE>empty;payloadBytesEqualV1=true;p7PinsEqual=true;policyPending=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-004",
            "system_admin",
            ["p8-flw-004-lock-include"],
            FlowDefinitionWrites,
            FlowDefinitionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var includeDraft = RequireIncludeDraft();
                var include = await LockFlowVersionAsync(
                    actor,
                    includeDraft,
                    "p8-flw-004-lock-include",
                    "INCLUDE",
                    acknowledgeWarning: true,
                    ct);
                var exclude = RequireExcludeLocked();
                HarnessAssert.Equal("INCLUDE", include.ContributionPolicy,
                    "Explicit INCLUDE policy was not persisted");
                HarnessAssert.True(!string.IsNullOrWhiteSpace(
                        include.ContributionWarning),
                    "INCLUDE did not persist a contribution warning");
                HarnessAssert.Equal(exclude.PayloadJson, include.PayloadJson,
                    "V_EXCLUDE/V_INCLUDE P7 payload bytes differ");
                HarnessAssert.Equal(exclude.PayloadHash, include.PayloadHash,
                    "V_EXCLUDE/V_INCLUDE P7 payload hashes differ");
                HarnessAssert.Equal(exclude.CatalogVersion, include.CatalogVersion,
                    "Contribution variants crossed catalog version");
                HarnessAssert.Equal(exclude.CatalogSemanticHash,
                    include.CatalogSemanticHash,
                    "Contribution variants crossed catalog semantic hash");
                HarnessAssert.True(!string.Equals(
                        exclude.ContributionPolicyHash,
                        include.ContributionPolicyHash,
                        StringComparison.Ordinal),
                    "Distinct immutable contribution policies share one hash");

                var rows = await _database.GetCollection<BsonDocument>(
                        FlowVersionsCollection)
                    .Find(Builders<BsonDocument>.Filter.Eq(
                        "templateId", ObjectId.Parse(include.FamilyId)))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("versionNo"))
                    .ToListAsync(ct);
                HarnessAssert.Equal(2, rows.Count,
                    "Main Flow family does not contain exactly two versions");
                HarnessAssert.True(rows.All(row =>
                        string.Equals("LOCKED", BsonString(row, "status"),
                            StringComparison.Ordinal)),
                    "Both contribution versions are not LOCKED");
                HarnessAssert.True(_excludeLockedBson.SequenceEqual(
                        rows[0].ToBson()),
                    "Locking V_INCLUDE mutated locked V_EXCLUDE BSON");
                var listed = await ListFlowVersionsAsync(
                    actor,
                    include.FamilyId,
                    ct);
                HarnessAssert.Equal(2, listed.Count,
                    "Flow versions API did not expose exactly V_EXCLUDE/V_INCLUDE");
                _includeLocked = include;
                return new CaseObservation(
                    "Authorized explicit INCLUDE+warning acknowledgment locked version 2; both locked rows retain byte-identical P7 payload/pins while policy hashes differ and V_EXCLUDE BSON stays immutable.",
                    "v1=LOCKED:EXCLUDE;v2=LOCKED:INCLUDE;rows=2;p7PayloadBytesEqual=true;policyHashesDistinct=true;v1BsonImmutable=true");
            },
            ct);
    }

    private static void RequireNonEmptyP7MappingBaseline(
        P8FlowVersionIdentity version)
    {
        var rules = version.Payload["mappingRules"] as System.Text.Json.Nodes.JsonArray
                    ?? throw new InvalidOperationException(
                        "P7 baseline lacks mappingRules");
        HarnessAssert.Equal(1, rules.Count,
            "P7 baseline must retain exactly one structured mapping rule");
        var rule = rules[0] as System.Text.Json.Nodes.JsonObject
                   ?? throw new InvalidOperationException(
                       "P7 mapping rule is malformed");
        HarnessAssert.Equal("p7-note-to-child",
            RequiredString(rule, "mappingId"),
            "P7 mapping identity drifted");
        HarnessAssert.Equal("FIELD", RequiredString(rule, "mappingKind"),
            "P7 mapping kind drifted");
        HarnessAssert.Equal("OVERWRITE",
            RequiredString(rule, "conflictPolicy"),
            "P7 conflict policy drifted");
        HarnessAssert.Equal("INCLUDE",
            RequiredString(rule, "contributionPolicy"),
            "P7 mapping-rule contribution policy drifted");
        HarnessAssert.Equal("BLOCK_APPLY",
            RequiredString(rule, "errorPolicy"),
            "P7 error policy drifted");
        HarnessAssert.True(rule["inputs"] is System.Text.Json.Nodes.JsonArray,
            "P7 structured source inputs are absent");
        HarnessAssert.True(rule["target"] is System.Text.Json.Nodes.JsonObject,
            "P7 structured target is absent");
        HarnessAssert.True(rule["calculation"] is System.Text.Json.Nodes.JsonObject,
            "P7 typed calculation is absent");
    }

    private static void RequireEmptyStatisticProfile(
        P8FlowVersionIdentity version,
        string context)
    {
        var profile = version.Payload["statisticProfile"] as
                      System.Text.Json.Nodes.JsonObject
                      ?? throw new InvalidOperationException(
                          $"{context} lacks statisticProfile object");
        HarnessAssert.Equal(0, profile.Count,
            $"{context} is not the frozen empty profile");
    }
}
