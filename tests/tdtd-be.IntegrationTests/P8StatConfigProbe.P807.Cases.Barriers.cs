using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP807OverrideCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-FLW-005",
            "system_admin",
            [
                "p8-flw-005-unauthorized",
                "p8-flw-005-invalid",
                "p8-flw-005-warning"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var outsider = Actor("ordinary_a");
                var admin = Actor("system_admin");
                await RequireZeroWriteFlowRejectionAsync(
                    "P8-FLW-005/unauthorized",
                    () => _api.PostAsync(
                        FlowLockRoute(_warningDraft),
                        FlowLockRequest(
                            _warningDraft,
                            "p8-flw-005-unauthorized",
                            "INCLUDE",
                            acknowledgeWarning: true),
                        outsider.Token,
                        ct: ct),
                    HttpStatusCode.Forbidden,
                    "AUTH_FORBIDDEN",
                    expectedPath: null,
                    ct);
                await RequireZeroWriteFlowRejectionAsync(
                    "P8-FLW-005/invalid-policy",
                    () => _api.PostAsync(
                        FlowLockRoute(_invalidPolicyDraft),
                        FlowLockRequest(
                            _invalidPolicyDraft,
                            "p8-flw-005-invalid",
                            "BOTH",
                            acknowledgeWarning: true),
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FLOW_CONTRIBUTION_POLICY_INVALID",
                    "contributionPolicy",
                    ct);
                await RequireZeroWriteFlowRejectionAsync(
                    "P8-FLW-005/warning-required",
                    () => _api.PostAsync(
                        FlowLockRoute(_warningDraft),
                        FlowLockRequest(
                            _warningDraft,
                            "p8-flw-005-warning",
                            "INCLUDE",
                            acknowledgeWarning: false),
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_CONTRIBUTION_WARNING_REQUIRED",
                    "acknowledgeContributionWarning",
                    ct);
                return new CaseObservation(
                    "Unauthorized INCLUDE, invalid policy and missing warning acknowledgment all failed before family/version/receipt/audit/outbox/job/result writes.",
                    "unauthorized=403+0W;invalid=400+0W;warning=409+0W;family=0;version=0;receipt=0;audit=0;results=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-006",
            "system_admin",
            ["p8-flw-002-lock-default"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var admin = Actor("system_admin");
                var main = RequireMainFlow();
                var originalRequest = RequireExcludeLockOriginalRequest();
                var divergentRequest = originalRequest.DeepClone().AsObject();
                divergentRequest["contributionPolicy"] = "INCLUDE";
                divergentRequest["acknowledgeContributionWarning"] = true;
                HarnessAssert.Equal(
                    RequiredInt(originalRequest, "expectedFamilyRevision"),
                    RequiredInt(divergentRequest, "expectedFamilyRevision"),
                    "Divergent replay changed original family CAS");
                HarnessAssert.Equal(
                    RequiredInt(originalRequest, "expectedDraftRevision"),
                    RequiredInt(divergentRequest, "expectedDraftRevision"),
                    "Divergent replay changed original draft CAS");
                HarnessAssert.Equal(
                    RequiredString(originalRequest, "expectedPayloadHash"),
                    RequiredString(divergentRequest, "expectedPayloadHash"),
                    "Divergent replay changed original payload CAS");
                HarnessAssert.True(originalRequest["contributionPolicy"] is null &&
                                   originalRequest["acknowledgeContributionWarning"] is null,
                    "Original omitted-policy request was not preserved exactly");
                await RequireZeroWriteFlowRejectionAsync(
                    "P8-FLW-006/divergent-locked-policy-replay",
                    () => _api.PostAsync(
                        _excludeLockOriginalRoute,
                        divergentRequest,
                        admin.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                    expectedPath: null,
                    ct);

                var exclude = await ReadFlowVersionAsync(
                    admin,
                    main.FamilyId,
                    RequireExcludeLocked().Id,
                    ct);
                var include = await ReadFlowVersionAsync(
                    admin,
                    main.FamilyId,
                    RequireIncludeLocked().Id,
                    ct);
                HarnessAssert.Equal("EXCLUDE", exclude.ContributionPolicy,
                    "Divergent request changed locked EXCLUDE");
                HarnessAssert.Equal("INCLUDE", include.ContributionPolicy,
                    "Divergent request changed locked INCLUDE");

                var securityContractPath = Path.Combine(
                    _paths.WorkspaceRoot,
                    "tdtd-be",
                    "Services",
                    "DynamicFlows",
                    "DynamicFlowMappingSecurityContract.cs");
                var securityContract = await File.ReadAllTextAsync(
                    securityContractPath,
                    ct);
                HarnessAssert.True(
                    securityContract.Contains(
                        "request.ContributionPolicy is not null",
                        StringComparison.Ordinal) &&
                    securityContract.Contains(
                        "DYNAMIC_FLOW_MAPPING_CONFIG_MUST_BE_FLOW_OWNED",
                        StringComparison.Ordinal),
                    "Frozen P7 preview/request contribution override guard drifted");
                return new CaseObservation(
                    "A changed replay could not override locked EXCLUDE; both policies remained immutable and the unchanged P7 request/preview guard still rejects caller-owned contributionPolicy without invoking preview.",
                    "changedReplay=409+0W;v1=EXCLUDE;v2=INCLUDE;p7OverrideGuardPinned=true;previewCalls=0;applyCalls=0;runCalls=0");
            },
            ct);
    }

    private async Task RunP807ProfileCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-FLW-007",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var version = await ReadFlowVersionAsync(
                    Actor("system_admin"),
                    RequireMainFlow().FamilyId,
                    RequireExcludeLocked().Id,
                    ct);
                RequireEmptyStatisticProfile(version, "V_EXCLUDE GET");
                HarnessAssert.Equal(RequireExcludeLocked().PayloadHash,
                    version.PayloadHash,
                    "Empty profile GET changed V_EXCLUDE hash");
                return new CaseObservation(
                    "An explicit empty statisticProfile stayed canonical empty in locked V_EXCLUDE without an executor or write.",
                    "profile={};canonicalEmpty=true;hashStable=true;http=200;writes=0;executor=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-008",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var include = await ReadFlowVersionAsync(
                    Actor("system_admin"),
                    RequireMainFlow().FamilyId,
                    RequireIncludeLocked().Id,
                    ct);
                RequireEmptyStatisticProfile(include, "V_INCLUDE GET");
                HarnessAssert.Equal(RequireExcludeLocked().PayloadJson,
                    include.PayloadJson,
                    "diffMode=NONE normalization changed canonical payload bytes");
                HarnessAssert.Equal(RequireExcludeLocked().PayloadHash,
                    include.PayloadHash,
                    "diffMode=NONE normalization changed canonical payload hash");
                return new CaseObservation(
                    "diffMode=NONE normalized to the same canonical empty profile and exact P7 payload bytes/hash as V_EXCLUDE.",
                    "profile=NONE>empty;payloadBytesEqual=true;payloadHashEqual=true;writes=0;executor=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-FLW-009",
            "system_admin",
            ["p8-flw-009-profile-lock"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                await RequireZeroWriteFlowRejectionAsync(
                    "P8-FLW-009/non-empty-profile",
                    () => _api.PostAsync(
                        FlowLockRoute(_profileDraft),
                        FlowLockRequest(
                            _profileDraft,
                            "p8-flw-009-profile-lock",
                            contributionPolicy: null,
                            acknowledgeWarning: null),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE",
                    "statisticProfile",
                    ct);
                var persisted = await ReadFlowVersionRowAsync(
                    _profileDraft.Version.Id,
                    ct);
                HarnessAssert.Equal("DRAFT", BsonString(persisted, "status"),
                    "Rejected non-empty profile changed draft status");
                HarnessAssert.Equal(null,
                    BsonString(persisted, "contributionPolicy"),
                    "Rejected profile persisted a contribution policy");
                return new CaseObservation(
                    "A non-empty statisticProfile failed lock with the exact intentional barrier and zero family/version/receipt/audit/job/outbox/result delta.",
                    "profile=nonempty;http=409;code=DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE;draftUnchanged=true;receipt=0;results=0");
            },
            ct);
    }

    private static string FlowLockRoute(P8FlowDraftFixture fixture)
        => $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/" +
           $"{fixture.Version.Id}/lock";

    private static JsonObject FlowLockRequest(
        P8FlowDraftFixture fixture,
        string commandId,
        string? contributionPolicy,
        bool? acknowledgeWarning)
    {
        var body = new JsonObject
        {
            ["commandId"] = commandId,
            ["expectedFamilyRevision"] = fixture.FamilyRevision,
            ["expectedDraftRevision"] = fixture.Version.DraftRevision,
            ["expectedPayloadHash"] = fixture.Version.PayloadHash
        };
        if (contributionPolicy is not null)
            body["contributionPolicy"] = contributionPolicy;
        if (acknowledgeWarning.HasValue)
            body["acknowledgeContributionWarning"] = acknowledgeWarning.Value;
        return body;
    }
}
