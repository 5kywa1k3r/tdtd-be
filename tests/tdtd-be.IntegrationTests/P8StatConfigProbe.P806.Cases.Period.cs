using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP806PeriodPolicyCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-DIF-006",
            "system_admin",
            ["p8-dif-006-exact-direction"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var fixture = DiffFixture("006");
                var selector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);
                var payload = DiffPayload(
                    selector,
                    leftPeriod: DiffExactPeriod("2026-08"),
                    rightPeriod: DiffExactPeriod("2026-07"),
                    direction: "RIGHT_TO_LEFT");
                var (_, identity) = await PutDiffConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-dif-006-exact-direction",
                    payload,
                    ct);
                HarnessAssert.Equal("RIGHT_TO_LEFT",
                    RequiredString(identity.Payload, "direction"),
                    "Diff direction readback mismatch");
                RequireDiffPeriodReadback(
                    identity,
                    "left",
                    "EXACT",
                    "2026-08",
                    null,
                    null);
                RequireDiffPeriodReadback(
                    identity,
                    "right",
                    "EXACT",
                    "2026-07",
                    null,
                    null);
                return new CaseObservation(
                    "Explicit left/right EXACT periods and RIGHT_TO_LEFT direction were versioned and read back without calculating a delta.",
                    "period=EXACT:2026-08<>2026-07;direction=RIGHT_TO_LEFT;hash+mongo=true;delta=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-007",
            "system_admin",
            ["p8-dif-007-range-policies"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var fixture = DiffFixture("007");
                var selector = DiffSelector(
                    "TABLE_METRIC",
                    $"{DiffBlockId}:{DiffMetricKey}",
                    _p806MetricLabel.LabelCode);
                var (_, identity) = await PutDiffConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-dif-007-range-policies",
                    DiffPayload(
                        selector,
                        leftPeriod: DiffRangePeriod("2026-01", "2026-06"),
                        rightPeriod: DiffRangePeriod("2025-07", "2025-12"),
                        missingPolicy: "INCLUDE",
                        emptyPolicy: "AS_MISSING"),
                    ct);
                RequireDiffPeriodReadback(
                    identity,
                    "left",
                    "RANGE",
                    null,
                    "2026-01",
                    "2026-06");
                RequireDiffPeriodReadback(
                    identity,
                    "right",
                    "RANGE",
                    null,
                    "2025-07",
                    "2025-12");
                HarnessAssert.Equal("INCLUDE",
                    RequiredString(identity.Payload, "missingPolicy"),
                    "Diff missingPolicy readback mismatch");
                HarnessAssert.Equal("AS_MISSING",
                    RequiredString(identity.Payload, "emptyPolicy"),
                    "Diff emptyPolicy readback mismatch");
                return new CaseObservation(
                    "RANGE period pairs and explicit missing/empty policies remained typed configuration metadata only.",
                    "period=RANGE;missing=INCLUDE;empty=AS_MISSING;readback+mongo+hash=true;result=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-008",
            "system_admin",
            [
                "p8-dif-008-flow-scope",
                "p8-dif-008-foreign-flow"
            ],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var fixture = DiffFixture("008");
                var selector = DiffSelector(
                    "ROW_LABEL",
                    $"{DiffBlockId}:{_p806RowLabel.LabelCode}",
                    _p806RowLabel.LabelCode);
                var flowScope = DiffSourceScope(
                    "FLOW_BRANCH",
                    fixture.FlowInstanceId,
                    flowBranchId: fixture.FlowBranchId,
                    flowEffectiveStatus: "EFFECTIVE");
                var (_, identity) = await PutDiffConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-dif-008-flow-scope",
                    DiffPayload(
                        selector,
                        leftScope: flowScope,
                        rightScope: flowScope),
                    ct);
                RequireDiffSourceScopeReadback(
                    identity,
                    "left",
                    "FLOW_BRANCH",
                    fixture.FlowInstanceId,
                    null,
                    fixture.FlowBranchId,
                    "EFFECTIVE");
                RequireDiffSourceScopeReadback(
                    identity,
                    "right",
                    "FLOW_BRANCH",
                    fixture.FlowInstanceId,
                    null,
                    fixture.FlowBranchId,
                    "EFFECTIVE");
                var foreignScope = DiffSourceScope(
                    "FLOW_BRANCH",
                    "000000000000000000000008",
                    flowBranchId: "000000000000000000000108",
                    flowEffectiveStatus: "EFFECTIVE");
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-008/foreign-flow",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-008-foreign-flow",
                            identity.Revision,
                            identity.ConfigHash,
                            DiffPayload(
                                selector,
                                leftScope: foreignScope,
                                rightScope: foreignScope)),
                        Actor("system_admin").Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.left.sourceScope.flowInstanceId",
                    "DIFF_SOURCE_SCOPE_FOREIGN",
                    ct);
                return new CaseObservation(
                    "The two sides pinned the assignment-owned FLOW_BRANCH scope; an arbitrary valid foreign flow id failed on the stable owner path with whole-database zero delta.",
                    "scope=FLOW_BRANCH+EFFECTIVE;sidesEqual=true;foreignFlow=400+0W;runtime=BLOCKED_UNTIL_P9;writes=config+receipt-only");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-009",
            "system_admin",
            [
                "p8-dif-009-range-reversed",
                "p8-dif-009-period-mode-mismatch",
                "p8-dif-009-scope-mismatch",
                "p8-dif-009-missing-policy"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("009");
                var selector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);

                async Task Reject(
                    string probe,
                    string commandId,
                    JsonObject payload,
                    string? path,
                    string? reason)
                    => await RequireZeroWriteDiffRejectionAsync(
                        $"P8-DIF-009/{probe}",
                        () => _api.PutAsync(
                            DiffConfigRoute(fixture),
                            Envelope(
                                commandId,
                                0,
                                EmptyConfigHash,
                                payload),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        "STAT_CONFIG_SCHEMA_INVALID",
                        path,
                        reason,
                        ct);

                await Reject(
                    "range-reversed",
                    "p8-dif-009-range-reversed",
                    DiffPayload(
                        selector,
                        leftPeriod: DiffRangePeriod("2026-12", "2026-01"),
                        rightPeriod: DiffRangePeriod("2025-01", "2025-12")),
                    "$.payload.left.period.periodKeyTo",
                    "PERIOD_RANGE_INVALID");
                await Reject(
                    "period-mode-mismatch",
                    "p8-dif-009-period-mode-mismatch",
                    DiffPayload(
                        selector,
                        leftPeriod: DiffExactPeriod("2026-08"),
                        rightPeriod: DiffRangePeriod("2025-01", "2025-12")),
                    "$.payload.right.period.mode",
                    "DIFF_PERIOD_MODE_MISMATCH");
                await Reject(
                    "scope-mismatch",
                    "p8-dif-009-scope-mismatch",
                    DiffPayload(
                        selector,
                        leftScope: DiffSourceScope("SELF"),
                        rightScope: DiffSourceScope("DIRECT_CHILDREN")),
                    "$.payload.right.sourceScope",
                    "DIFF_SOURCE_SCOPE_MISMATCH");
                await Reject(
                    "missing-policy",
                    "p8-dif-009-missing-policy",
                    DiffPayload(selector, missingPolicy: "DROP"),
                    "$.payload.missingPolicy",
                    "MISSING_POLICY_UNSUPPORTED");
                return new CaseObservation(
                    "Reversed/mixed periods, mismatched source scopes and unknown missing policy all failed with stable schema errors and whole-database zero delta.",
                    "rangeReversed=400+0W;periodModeMismatch=400+0W;scopeMismatch=400+0W;missingUnknown=400+0W");
            },
            ct);
    }

    private static void RequireDiffPeriodReadback(
        P8DiffConfigIdentity identity,
        string sideName,
        string mode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
    {
        var side = identity.Payload[sideName] as JsonObject
                   ?? throw new InvalidOperationException(
                       $"Diff payload lacks {sideName}");
        var period = side["period"] as JsonObject
                     ?? throw new InvalidOperationException(
                         $"Diff payload lacks {sideName}.period");
        HarnessAssert.Equal(mode, RequiredString(period, "mode"),
            $"Diff {sideName} period mode mismatch");
        HarnessAssert.Equal(periodKey, OptionalString(period, "periodKey"),
            $"Diff {sideName} periodKey mismatch");
        HarnessAssert.Equal(periodKeyFrom, OptionalString(period, "periodKeyFrom"),
            $"Diff {sideName} periodKeyFrom mismatch");
        HarnessAssert.Equal(periodKeyTo, OptionalString(period, "periodKeyTo"),
            $"Diff {sideName} periodKeyTo mismatch");
    }

    private static void RequireDiffSourceScopeReadback(
        P8DiffConfigIdentity identity,
        string sideName,
        string mode,
        string? flowInstanceId,
        string? flowStepId,
        string? flowBranchId,
        string? flowEffectiveStatus)
    {
        var side = identity.Payload[sideName] as JsonObject
                   ?? throw new InvalidOperationException(
                       $"Diff payload lacks {sideName}");
        var scope = side["sourceScope"] as JsonObject
                    ?? throw new InvalidOperationException(
                        $"Diff payload lacks {sideName}.sourceScope");
        HarnessAssert.Equal(mode, RequiredString(scope, "mode"),
            $"Diff {sideName} source scope mode mismatch");
        HarnessAssert.Equal(flowInstanceId,
            OptionalString(scope, "flowInstanceId"),
            $"Diff {sideName} flowInstanceId mismatch");
        HarnessAssert.Equal(flowStepId,
            OptionalString(scope, "flowStepId"),
            $"Diff {sideName} flowStepId mismatch");
        HarnessAssert.Equal(flowBranchId,
            OptionalString(scope, "flowBranchId"),
            $"Diff {sideName} flowBranchId mismatch");
        HarnessAssert.Equal(flowEffectiveStatus,
            OptionalString(scope, "flowEffectiveStatus"),
            $"Diff {sideName} flowEffectiveStatus mismatch");
    }
}
