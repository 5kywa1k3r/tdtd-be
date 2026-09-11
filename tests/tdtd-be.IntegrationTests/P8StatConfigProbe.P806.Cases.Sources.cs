using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP806SourceCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-DIF-001",
            "system_admin",
            ["p8-dif-001-field"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var fixture = DiffFixture("001");
                var selector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);
                var (_, identity) = await PutDiffConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-dif-001-field",
                    DiffPayload(selector),
                    ct);
                RequireDiffSelectorReadback(
                    identity,
                    "left",
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode,
                    "NUMBER");
                RequireDiffSelectorReadback(
                    identity,
                    "right",
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode,
                    "NUMBER");
                return new CaseObservation(
                    "FIELD selector resolved from the trusted published schema/stat-config and persisted with stable identity, pins and hash.",
                    "kind=FIELD;typed=NUMBER;left+right=trusted;draft=1;receipt=1;mongo+hash=true;results=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-002",
            "system_admin",
            ["p8-dif-002-table-metric"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var fixture = DiffFixture("002");
                var selector = DiffSelector(
                    "TABLE_METRIC",
                    $"{DiffBlockId}:{DiffMetricKey}",
                    _p806MetricLabel.LabelCode);
                var (_, identity) = await PutDiffConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-dif-002-table-metric",
                    DiffPayload(selector),
                    ct);
                RequireDiffSelectorReadback(
                    identity,
                    "left",
                    "TABLE_METRIC",
                    $"{DiffBlockId}:{DiffMetricKey}",
                    _p806MetricLabel.LabelCode,
                    "NUMBER");
                return new CaseObservation(
                    "TABLE_METRIC used the stable blockId:metricKey identity and trusted typed statistic section without positional fallback.",
                    "kind=TABLE_METRIC;key=blockId:metricKey;typed=NUMBER;positionalFallback=false;pins+mongo+hash=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-003",
            "system_admin",
            ["p8-dif-003-row-label"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var fixture = DiffFixture("003");
                var selector = DiffSelector(
                    "ROW_LABEL",
                    $"{DiffBlockId}:{_p806RowLabel.LabelCode}",
                    _p806RowLabel.LabelCode);
                var (_, identity) = await PutDiffConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-dif-003-row-label",
                    DiffPayload(selector),
                    ct);
                RequireDiffSelectorReadback(
                    identity,
                    "left",
                    "ROW_LABEL",
                    $"{DiffBlockId}:{_p806RowLabel.LabelCode}",
                    _p806RowLabel.LabelCode,
                    "NUMBER");
                return new CaseObservation(
                    "ROW_LABEL resolved only through the trusted block allowlist and preserved its typed stable blockId:labelCode identity.",
                    "kind=ROW_LABEL;key=blockId:labelCode;allowlist=true;typed=NUMBER;pins+mongo+hash=true");
            },
            ct);
    }

    private static void RequireDiffSelectorReadback(
        P8DiffConfigIdentity identity,
        string sideName,
        string conceptKind,
        string conceptKey,
        string conceptCode,
        string dataType)
    {
        var side = identity.Payload[sideName] as JsonObject
                   ?? throw new InvalidOperationException(
                       $"Diff payload lacks {sideName}");
        var selector = side["selector"] as JsonObject
                       ?? throw new InvalidOperationException(
                           $"Diff payload lacks {sideName}.selector");
        HarnessAssert.Equal(conceptKind,
            RequiredString(selector, "conceptKind"),
            $"Diff {sideName} conceptKind mismatch");
        HarnessAssert.Equal(conceptKey,
            RequiredString(selector, "conceptKey"),
            $"Diff {sideName} conceptKey mismatch");
        HarnessAssert.Equal(conceptCode,
            RequiredString(selector, "conceptCode"),
            $"Diff {sideName} conceptCode mismatch");
        HarnessAssert.Equal(dataType,
            RequiredString(selector, "dataType"),
            $"Diff {sideName} dataType mismatch");
    }
}
