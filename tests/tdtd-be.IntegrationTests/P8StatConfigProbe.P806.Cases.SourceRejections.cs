using System.Net;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP806SourceRejectionCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-DIF-004",
            "system_admin",
            [
                "p8-dif-004-concept-mismatch",
                "p8-dif-004-datatype-mismatch"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("004");
                var fieldSelector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);
                var metricSelector = DiffSelector(
                    "TABLE_METRIC",
                    $"{DiffBlockId}:{DiffMetricKey}",
                    _p806MetricLabel.LabelCode);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-004/concept-mismatch",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-004-concept-mismatch",
                            0,
                            EmptyConfigHash,
                            DiffPayload(
                                fieldSelector,
                                rightSelector: metricSelector)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.right.selector.conceptKind",
                    "DIFF_CONCEPT_KIND_MISMATCH",
                    ct);

                var wrongType = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode,
                    "BOOLEAN");
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-004/datatype-mismatch",
                    () => _api.PutAsync(
                        DiffConfigRoute(fixture),
                        Envelope(
                            "p8-dif-004-datatype-mismatch",
                            0,
                            EmptyConfigHash,
                            DiffPayload(wrongType)),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.left.selector.dataType",
                    "DIFF_SELECTOR_DATA_TYPE_MISMATCH",
                    ct);
                return new CaseObservation(
                    "Mixed concepts and request-vs-trusted datatype mismatches were rejected before config/receipt/job/outbox/result writes.",
                    "conceptMismatch=400+0W;datatypeMismatch=400+0W;trustedDependencies=true;receipt=0;results=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-005",
            "system_admin",
            [
                "p8-dif-005-kind",
                "p8-dif-005-inactive-row-label",
                "p8-dif-005-row-positional",
                "p8-dif-005-pin-override"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("005");

                async Task Reject(
                    string probe,
                    string commandId,
                    JsonObject payload,
                    string? path = null,
                    string? reason = null)
                    => await RequireZeroWriteDiffRejectionAsync(
                        $"P8-DIF-005/{probe}",
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
                    "kind",
                    "p8-dif-005-kind",
                    DiffPayload(DiffSelector(
                        "TABLE",
                        $"{DiffBlockId}:{DiffMetricKey}",
                        _p806MetricLabel.LabelCode)),
                    "$.payload.left.selector.conceptKind",
                    "CONCEPT_KIND_UNSUPPORTED");
                await Reject(
                    "inactive-row-label",
                    "p8-dif-005-inactive-row-label",
                    DiffPayload(DiffSelector(
                        "ROW_LABEL",
                        $"{DiffBlockId}:{_tableRowInactive.LabelCode}",
                        _tableRowInactive.LabelCode)),
                    "$.payload.left.selector.conceptKey",
                    "DIFF_ROW_LABEL_NOT_ALLOWED");
                await Reject(
                    "row-positional",
                    "p8-dif-005-row-positional",
                    DiffPayload(DiffSelector(
                        "ROW_LABEL",
                        $"{DiffBlockId}:0",
                        _p806RowLabel.LabelCode)),
                    "$.payload.left.selector.conceptKey",
                    "DIFF_ROW_LABEL_NOT_ALLOWED");

                var pinOverride = DiffPayload(DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode));
                pinOverride["dependencyPins"] = new JsonArray(
                    JsonValue.Create(
                        "DYNAMIC_FORM_SCHEMA:stale:0:invalid"));
                await Reject(
                    "pin-override",
                    "p8-dif-005-pin-override",
                    pinOverride,
                    "$.payload.dependencyPins",
                    "SCHEMA_MISMATCH");
                return new CaseObservation(
                    "Unknown kinds, a deactivated/unpinned row-label snapshot, positional row identity and client stale-pin override all failed closed.",
                    "unknownKind=400+0W;inactiveRowLabel=400+0W;positionalRow=400+0W;pinOverride=400+0W;receipt=0");
            },
            ct);
    }
}
