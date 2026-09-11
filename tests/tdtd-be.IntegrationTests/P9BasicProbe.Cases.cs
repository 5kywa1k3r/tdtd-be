using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private const string BasicSummaryRoute =
        "api/work-assignment-basic-summary/summary";
    private const string BasicOnceRoute =
        "api/work-assignment-basic-summary/once";
    private JsonObject? _basFirstSummary;

    private async Task RunBasicCasesAsync(CancellationToken ct)
    {
        await RunBasicCoreCasesAsync(ct);
        await RunBasicFlowScopeCasesAsync(
            "FLOW_BRANCH",
            "P9-BAS-FLOW-BRANCH-01",
            "P9-BAS-FLOW-BRANCH-02",
            "P9-BAS-FLOW-BRANCH-03",
            "P9-BAS-FLOW-BRANCH-04",
            ct);
        await RunBasicFlowScopeCasesAsync(
            "FLOW_STEP",
            "P9-BAS-FLOW-STEP-01",
            "P9-BAS-FLOW-STEP-02",
            "P9-BAS-FLOW-STEP-03",
            "P9-BAS-FLOW-STEP-04",
            ct);
        await RunBasicFlowScopeCasesAsync(
            "FLOW_EFFECTIVE_PATH",
            "P9-BAS-FLOW-EFFECTIVE-PATH-01",
            "P9-BAS-FLOW-EFFECTIVE-PATH-02",
            "P9-BAS-FLOW-EFFECTIVE-PATH-03",
            "P9-BAS-FLOW-EFFECTIVE-PATH-04",
            ct);
        await RunBasicFlowScopeCasesAsync(
            "FLOW_FINAL",
            "P9-BAS-FLOW-FINAL-01",
            "P9-BAS-FLOW-FINAL-02",
            "P9-BAS-FLOW-FINAL-03",
            "P9-BAS-FLOW-FINAL-04",
            ct);
    }

    private async Task RunBasicCoreCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-BAS-BASIC-01",
            async () =>
            {
                await ConfigureBasicAsync("SELF", ct);
                _basFirstSummary = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(_basFirstSummary);
                HarnessAssert.Equal(
                    "SELF",
                    RequiredJsonString(meta, "sourceScopeMode"),
                    "P9-BAS exact Basic SELF scope");
                return BasicObservation(
                    "Basic result route accepted only the stage-2 locked SELF config.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-02",
            async () =>
            {
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                var identity = await ReadBasicIdentityAsync(ct);
                HarnessAssert.Equal(
                    identity.ConfigHash,
                    RequiredJsonString(meta, "configHash"),
                    "P9-BAS config hash pin");
                HarnessAssert.Equal(
                    identity.VersionId,
                    RequiredJsonString(meta, "configVersionId"),
                    "P9-BAS config version pin");
                HarnessAssert.Equal(
                    ChainId,
                    RequiredJsonString(meta, "candidateChainId"),
                    "P9-BAS candidate chain pin");
                HarnessAssert.Equal(
                    BasicPromptId,
                    RequiredJsonString(meta, "candidatePromptId"),
                    "P9-BAS candidate prompt pin");
                HarnessAssert.Equal(
                    2,
                    RequiredJsonInt(meta, "candidateStage"),
                    "P9-BAS candidate stage pin");
                HarnessAssert.Equal(
                    BasicStageLockSha256,
                    RequiredJsonString(meta, "candidateStageLockSha256"),
                    "P9-BAS stage-lock pin");
                return BasicObservation(
                    "Basic metadata pins locked config identity and candidate lineage.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-03",
            async () =>
            {
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.Equal(
                    1,
                    RequiredJsonInt(meta, "sourceAssignmentCount"),
                    "P9-BAS SELF assignment count");
                HarnessAssert.Equal(
                    1,
                    RequiredJsonInt(meta, "sourceReportCount"),
                    "P9-BAS approved-only report count");
                return BasicObservation(
                    "Draft source remained zero while the one approved current report contributed once.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-04",
            async () =>
            {
                var first = _basFirstSummary
                    ?? throw new InvalidOperationException("P9-BAS first summary unavailable.");
                var repeated = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var firstMeta = BasicMeta(first);
                var repeatedMeta = BasicMeta(repeated);
                HarnessAssert.Equal(
                    RequiredJsonString(firstMeta, "snapshotId"),
                    RequiredJsonString(repeatedMeta, "snapshotId"),
                    "P9-BAS replay snapshot identity");
                HarnessAssert.Equal(
                    RequiredJsonString(firstMeta, "sourceSignatureHash"),
                    RequiredJsonString(repeatedMeta, "sourceSignatureHash"),
                    "P9-BAS replay source signature");
                return BasicObservation(
                    "Repeated delivery converged to one logical snapshot and source signature.",
                    repeatedMeta);
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-05",
            async () =>
            {
                var root = await ReadBasicSummaryAsync(
                    BasicOnceRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.Equal(
                    "SELF",
                    RequiredJsonString(meta, "sourceScopeMode"),
                    "P9-BAS once scope");
                HarnessAssert.Equal(
                    1,
                    RequiredJsonInt(meta, "sourceReportCount"),
                    "P9-BAS once stable total");
                return BasicObservation(
                    "The /once alias preserved the locked scope and stable approved total.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-06",
            async () =>
            {
                var request = BasicRequest();
                request["sourceScopeMode"] = "FLOW_FINAL";
                request["sourceFlowInstanceId"] = Fixture().FlowInstanceId;
                request["sourceFlowEffectiveStatus"] = "EFFECTIVE";
                var response = await RequireApi().PostAsync(
                    BasicSummaryRoute,
                    request,
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.BadRequest,
                    "P9-BAS membership spoof rejection");
                HarnessAssert.Equal(
                    "COMMON_VALIDATION_FAILED",
                    ApiHarnessClient.FindStringRecursive(response.Json, "errorCode"),
                    "P9-BAS membership spoof error");
                return new CaseObservation(
                    "Client membership spoof was rejected before materialization.",
                    "http=400;reason=BASIC_SUMMARY_MEMBERSHIP_SPOOF_REJECTED");
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-07",
            async () =>
            {
                var response = await RequireApi().PostAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("outsider").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Forbidden,
                    "P9-BAS unauthorized scope");
                return new CaseObservation(
                    "Wrong-unit actor was forbidden without receiving result identity.",
                    "http=403;disclosedSnapshot=0");
            },
            ct);

        await RunCaseAsync(
            "P9-BAS-BASIC-08",
            async () =>
            {
                await TouchRootMembershipAsync(ct);
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.True(
                    meta["snapshotDirty"]?.GetValue<bool>() == true ||
                    meta["isCalculating"]?.GetValue<bool>() == true,
                    "P9-BAS stale membership must be dirty or calculating");
                return BasicObservation(
                    "Membership source drift invalidated the prior snapshot and queued refresh.",
                    meta);
            },
            ct);
    }

    private async Task RunBasicFlowScopeCasesAsync(
        string mode,
        string exactCase,
        string singleCase,
        string multiCase,
        string removedCase,
        CancellationToken ct)
    {
        await RunCaseAsync(
            exactCase,
            async () =>
            {
                await SetSiblingActiveAsync(true, ct);
                await ConfigureBasicAsync(mode, ct);
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.Equal(
                    mode,
                    RequiredJsonString(meta, "sourceScopeMode"),
                    $"P9-BAS exact {mode}");
                HarnessAssert.Equal(
                    Fixture().FlowInstanceId,
                    RequiredJsonString(meta, "sourceFlowInstanceId"),
                    $"P9-BAS {mode} flow identity");
                return BasicObservation(
                    $"{mode} was resolved from the locked server-owned Flow identity.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            singleCase,
            async () =>
            {
                await SetSiblingActiveAsync(false, ct);
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(forceRefresh: true),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.Equal(
                    1,
                    RequiredJsonInt(meta, "sourceAssignmentCount"),
                    $"P9-BAS {mode} single member");
                return BasicObservation(
                    $"{mode} returned exactly one active authorized member.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            multiCase,
            async () =>
            {
                await SetSiblingActiveAsync(true, ct);
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(forceRefresh: true),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.Equal(
                    2,
                    RequiredJsonInt(meta, "sourceAssignmentCount"),
                    $"P9-BAS {mode} multi member");
                var sources = root["sources"] as JsonArray ?? new JsonArray();
                HarnessAssert.Equal(
                    sources
                        .Select(item => item?["workAssignmentReportId"]?.GetValue<string>())
                        .Where(value => value is not null)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    sources.Count,
                    $"P9-BAS {mode} duplicate report identity");
                return BasicObservation(
                    $"{mode} included two memberships without duplicate report contribution.",
                    meta);
            },
            ct);

        await RunCaseAsync(
            removedCase,
            async () =>
            {
                await SetSiblingActiveAsync(false, ct);
                var root = await ReadBasicSummaryAsync(
                    BasicSummaryRoute,
                    BasicRequest(),
                    Actor("executor").Token,
                    ct);
                var meta = BasicMeta(root);
                HarnessAssert.Equal(
                    1,
                    RequiredJsonInt(meta, "sourceAssignmentCount"),
                    $"P9-BAS {mode} removed member");
                HarnessAssert.True(
                    meta["snapshotDirty"]?.GetValue<bool>() == true ||
                    meta["isCalculating"]?.GetValue<bool>() == true,
                    $"P9-BAS {mode} removed membership freshness");
                await SetSiblingActiveAsync(true, ct);
                return BasicObservation(
                    $"{mode} removal invalidated the old membership and excluded the member.",
                    meta);
            },
            ct);
    }

    private static CaseObservation BasicObservation(
        string detail,
        JsonObject meta)
        => new(
            detail,
            $"scope={RequiredJsonString(meta, "sourceScopeMode")};" +
            $"assignments={RequiredJsonInt(meta, "sourceAssignmentCount")};" +
            $"reports={RequiredJsonInt(meta, "sourceReportCount")};" +
            $"dirty={meta["snapshotDirty"]?.GetValue<bool>() == true};" +
            $"calculating={meta["isCalculating"]?.GetValue<bool>() == true}");
}
