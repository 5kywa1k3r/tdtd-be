using System.Net;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP804ScopePeriodCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-BAS-007",
            "system_admin",
            [
                "p8-bas-007-flow-branch",
                "p8-bas-007-branch-required",
                "p8-bas-007-branch-step-irrelevant"
            ],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("007");
                var payload = BasicPayload(
                    BasicFlowSourceScope(fixture, "FLOW_BRANCH"),
                    BasicPeriodRule("SINGLE_PERIOD", periodKey: "2026-08"),
                    ["ASSIGNMENT", "UNIT"],
                    BasicDetailHints(includeSourceRows: true, maxTextChars: 1000),
                    [BasicTarget("FIELD", BasicTargetKeys["NUMBER"][0], "NUMBER", "SUM")]);
                var (_, identity) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-007-flow-branch",
                    payload,
                    ct);
                RequireBasicPayloadContract(identity, payload);

                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-007/branch-required",
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        Envelope(
                            "p8-bas-007-branch-required",
                            identity.Revision,
                            identity.ConfigHash,
                            BasicPayload(
                                BasicSourceScope(
                                    "FLOW_BRANCH",
                                    fixture.FlowInstanceId,
                                    flowEffectiveStatus: "EFFECTIVE"),
                                BasicPeriodRule("ALL_PERIODS"),
                                Array.Empty<string>(),
                                BasicDetailHints(),
                                Array.Empty<System.Text.Json.Nodes.JsonObject>())),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sourceScope.flowBranchId",
                    "FLOW_BRANCH_ID_REQUIRED",
                    ct);
                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-007/branch-step-irrelevant",
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        Envelope(
                            "p8-bas-007-branch-step-irrelevant",
                            identity.Revision,
                            identity.ConfigHash,
                            BasicPayload(
                                BasicSourceScope(
                                    "FLOW_BRANCH",
                                    fixture.FlowInstanceId,
                                    fixture.FlowStepId,
                                    fixture.FlowBranchId,
                                    "EFFECTIVE"),
                                BasicPeriodRule("ALL_PERIODS"),
                                Array.Empty<string>(),
                                BasicDetailHints(),
                                Array.Empty<System.Text.Json.Nodes.JsonObject>())),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sourceScope.flowStepId",
                    "SOURCE_SCOPE_FIELD_NOT_APPLICABLE",
                    ct);
                return new CaseObservation(
                    "FLOW_BRANCH was versioned/readable with SINGLE_PERIOD and typed grouping/detail hints; missing or irrelevant selector fields wrote nothing.",
                    "scope=FLOW_BRANCH+EFFECTIVE;period=SINGLE_PERIOD;groups=ASSIGNMENT,UNIT;detail=rows+1000;invalidBranchFields=0W;eligibility=BLOCKED_UNTIL_P9");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-008",
            "system_admin",
            ["p8-bas-008-flow-step", "p8-bas-008-reversed-range"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("008");
                var payload = BasicPayload(
                    BasicFlowSourceScope(fixture, "FLOW_STEP", "ANY"),
                    BasicPeriodRule(
                        "PERIOD_RANGE",
                        periodKeyFrom: "2026-01",
                        periodKeyTo: "2026-12"),
                    ["PERIOD", "UNIT"],
                    BasicDetailHints(maxTextChars: 100000),
                    [BasicTarget("FIELD", BasicTargetKeys["DATE"][0], "DATE", "MIN_DATE")]);
                var (_, identity) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-008-flow-step",
                    payload,
                    ct);
                RequireBasicPayloadContract(identity, payload);

                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-008/reversed-range",
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        Envelope(
                            "p8-bas-008-reversed-range",
                            identity.Revision,
                            identity.ConfigHash,
                            BasicPayload(
                                BasicFlowSourceScope(fixture, "FLOW_STEP", "ANY"),
                                BasicPeriodRule(
                                    "PERIOD_RANGE",
                                    periodKeyFrom: "2026-12",
                                    periodKeyTo: "2026-01"),
                                ["PERIOD"],
                                BasicDetailHints(),
                                Array.Empty<System.Text.Json.Nodes.JsonObject>())),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.periodRule.periodKeyFrom",
                    "PERIOD_RANGE_REVERSED",
                    ct);
                return new CaseObservation(
                    "FLOW_STEP preserved step identity, ANY effective-status selection, PERIOD_RANGE and maximum detail bound.",
                    "scope=FLOW_STEP+ANY;period=2026-01..2026-12;groups=PERIOD,UNIT;maxTextChars=100000;reversedRange=400+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-009",
            "system_admin",
            ["p8-bas-009-effective-path", "p8-bas-009-path-branch-irrelevant"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("009");
                var payload = BasicPayload(
                    BasicFlowSourceScope(
                        fixture,
                        "FLOW_EFFECTIVE_PATH",
                        "INVALIDATED"),
                    BasicPeriodRule("ALL_PERIODS"),
                    ["ASSIGNMENT", "PERIOD", "UNIT"],
                    BasicDetailHints(includeSourceRows: true, maxTextChars: 12000),
                    [BasicTarget("FIELD", BasicTargetKeys["BOOLEAN"][0], "BOOLEAN", "TRUE_COUNT")]);
                var (_, identity) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-009-effective-path",
                    payload,
                    ct);
                RequireBasicPayloadContract(identity, payload);

                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-009/path-branch-irrelevant",
                    () => _api.PutAsync(
                        BasicConfigRoute(fixture),
                        Envelope(
                            "p8-bas-009-path-branch-irrelevant",
                            identity.Revision,
                            identity.ConfigHash,
                            BasicPayload(
                                BasicSourceScope(
                                    "FLOW_EFFECTIVE_PATH",
                                    fixture.FlowInstanceId,
                                    flowBranchId: fixture.FlowBranchId,
                                    flowEffectiveStatus: "INVALIDATED"),
                                BasicPeriodRule("ALL_PERIODS"),
                                Array.Empty<string>(),
                                BasicDetailHints(),
                                Array.Empty<System.Text.Json.Nodes.JsonObject>())),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.sourceScope.flowBranchId",
                    "SOURCE_SCOPE_FIELD_NOT_APPLICABLE",
                    ct);
                return new CaseObservation(
                    "FLOW_EFFECTIVE_PATH and INVALIDATED status remained readable metadata with no runtime eligibility.",
                    "scope=FLOW_EFFECTIVE_PATH+INVALIDATED;period=ALL_PERIODS;groups=ASSIGNMENT,PERIOD,UNIT;irrelevantBranch=0W;runtime=blocked");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-010",
            "system_admin",
            [
                "p8-bas-010-flow-final",
                "p8-bas-010-cumulative-mode",
                "p8-bas-010-duplicate-group",
                "p8-bas-010-detail-range"
            ],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("010");
                var payload = BasicPayload(
                    BasicFlowSourceScope(fixture, "FLOW_FINAL", "TERMINATED"),
                    BasicPeriodRule("ALL_PERIODS"),
                    Array.Empty<string>(),
                    BasicDetailHints(),
                    [BasicTarget("FIELD", BasicTargetKeys["CHOICE"][0], "CHOICE", "BUCKET_COUNT")]);
                var (_, identity) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-010-flow-final",
                    payload,
                    ct);
                RequireBasicPayloadContract(identity, payload);

                async Task Reject(
                    string probe,
                    string commandId,
                    System.Text.Json.Nodes.JsonObject rejectedPayload,
                    string path,
                    string reason)
                    => await RequireZeroWriteBasicRejectionAsync(
                        $"P8-BAS-010/{probe}",
                        () => _api.PutAsync(
                            BasicConfigRoute(fixture),
                            Envelope(
                                commandId,
                                identity.Revision,
                                identity.ConfigHash,
                                rejectedPayload),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        "STAT_CONFIG_SCHEMA_INVALID",
                        path,
                        reason,
                        ct);

                await Reject(
                    "cumulative-mode",
                    "p8-bas-010-cumulative-mode",
                    BasicPayload(
                        BasicFlowSourceScope(fixture, "FLOW_FINAL", "TERMINATED"),
                        BasicPeriodRule("CUMULATIVE_TO_PERIOD", periodKey: "2026-08"),
                        Array.Empty<string>(),
                        BasicDetailHints(),
                        Array.Empty<System.Text.Json.Nodes.JsonObject>()),
                    "$.payload.periodRule.mode",
                    "PERIOD_MODE_UNSUPPORTED");
                await Reject(
                    "duplicate-group",
                    "p8-bas-010-duplicate-group",
                    BasicPayload(
                        BasicFlowSourceScope(fixture, "FLOW_FINAL", "TERMINATED"),
                        BasicPeriodRule("ALL_PERIODS"),
                        ["UNIT", "UNIT"],
                        BasicDetailHints(),
                        Array.Empty<System.Text.Json.Nodes.JsonObject>()),
                    "$.payload.groupingHints[1]",
                    "DUPLICATE_GROUPING_HINT");
                await Reject(
                    "detail-range",
                    "p8-bas-010-detail-range",
                    BasicPayload(
                        BasicFlowSourceScope(fixture, "FLOW_FINAL", "TERMINATED"),
                        BasicPeriodRule("ALL_PERIODS"),
                        Array.Empty<string>(),
                        BasicDetailHints(maxTextChars: 100001),
                        Array.Empty<System.Text.Json.Nodes.JsonObject>()),
                    "$.payload.detailHints.maxTextChars",
                    "MAX_TEXT_CHARS_OUT_OF_RANGE");
                return new CaseObservation(
                    "FLOW_FINAL was readable while unsupported period aliases, duplicate grouping and out-of-range detail hints failed closed.",
                    "scope=FLOW_FINAL+TERMINATED;runtime=blocked;cumulativeAlias=0W;duplicateGroup=0W;maxTextChars100001=0W");
            },
            ct);
    }
}
