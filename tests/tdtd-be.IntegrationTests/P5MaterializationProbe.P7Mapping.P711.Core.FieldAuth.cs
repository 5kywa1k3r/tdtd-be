using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Exact P7 field-evaluator and mapping-authorization core cases. Every case
/// executes its own production semantic, owns a case-tagged Kestrel preview,
/// and records one exact-case direct-Mongo query without source payload data.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P711FieldRegistryDescriptor =
        "P7-FUNC-1\nCALC_PERCENTAGE|1|NUMBER|denominator:NUMBER,numerator:NUMBER";
    private const string P711FieldRegistrySha256 =
        "c85f918f982a4c556999c72242d8a16912b8d1ae40e3d532e331f86d3acf9adb";

    private const string P711AuthorizationPolicyPayload = """
    {
      "steps": [{ "stepId": "source", "stepCode": "SOURCE" }],
      "fieldPolicies": [
        { "policyId": "all-denied", "stepId": "source", "stepCode": "*", "actorRole": "*", "fieldKey": "amount", "read": false, "write": false, "locked": true },
        { "policyId": "assignee-amount", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "amount", "read": true, "write": true, "required": true, "locked": false },
        { "policyId": "assignee-secret", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "secret", "read": true, "write": true, "required": true, "hidden": true },
        { "policyId": "review-score", "stepId": "source", "stepCode": "*", "actorRole": "REVIEWER", "fieldKey": "score", "read": true, "write": true, "required": true, "lockedAfterSubmit": true }
      ],
      "tableColumnPolicies": [
        { "policyId": "assignee-amount-col", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "blockId": "b1", "columnKey": "amount", "read": true, "write": true, "required": true },
        { "policyId": "assignee-secret-col", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "blockId": "b1", "columnKey": "secret", "read": true, "write": true, "required": true, "hidden": true },
        { "policyId": "review-score-col", "stepId": "source", "stepCode": "*", "actorRole": "REVIEWER", "blockId": "b1", "columnKey": "score", "read": true, "write": true, "required": true, "lockedAfterSubmit": true }
      ]
    }
    """;

    private static readonly P711FieldAuthSemanticCase[]
        P711FieldAndAuthSemanticCases =
    [
        new("MAP-FIELD-01", "Exact evaluator/function-registry version and deterministic SHA pins.", P711AssertMapField01),
        new("MAP-FIELD-02", "TEXT copy has Validate/Evaluate parity and preserves literal value bytes.", P711AssertMapField02),
        new("MAP-FIELD-03", "Whitespace-only separator remains literal data.", P711AssertMapField03),
        new("MAP-FIELD-04", "Multi-input TEXT concatenation is deterministic.", P711AssertMapField04),
        new("MAP-FIELD-05", "NUMBER and the registered function accept JSON numbers only.", P711AssertMapField05),
        new("MAP-FIELD-06", "Numeric strings fail closed without coercion or target mutation.", P711AssertMapField06),
        new("MAP-FIELD-07", "BOOLEAN copy accepts a JSON boolean.", P711AssertMapField07),
        new("MAP-FIELD-08", "Boolean strings fail closed without coercion.", P711AssertMapField08),
        new("MAP-FIELD-09", "DATE accepts canonical year, month, and full-date strings.", P711AssertMapField09),
        new("MAP-FIELD-10", "FULL_DATE arithmetic preserves the canonical dd/MM/yyyy form.", P711AssertMapField10),
        new("MAP-FIELD-11", "Non-canonical date strings fail with the stable field reason.", P711AssertMapField11),
        new("MAP-FIELD-12", "SINGLE_SELECT writes an exact allowed choice code.", P711AssertMapField12),
        new("MAP-FIELD-13", "Choice labels and unlisted codes fail closed.", P711AssertMapField13),
        new("MAP-FIELD-14", "MULTI_SELECT requires exact unique JSON-string choice codes.", P711AssertMapField14),
        new("MAP-FIELD-15", "KEEP_NULL produces an explicit null field write.", P711AssertMapField15),
        new("MAP-FIELD-16", "SKIP null policy returns a deterministic zero-write disposition.", P711AssertMapField16),
        new("MAP-FIELD-17", "ERROR null policy is stable and leaves the target untouched.", P711AssertMapField17),
        new("MAP-FIELD-18", "OVERWRITE and TARGET_WINS conflict policies are explicit.", P711AssertMapField18),
        new("MAP-FIELD-19", "ERROR_ON_CONFLICT is stable and leaves the target untouched.", P711AssertMapField19),
        new("MAP-FIELD-20", "Script, reflection, file, network, and unknown operations are rejected before a write.", P711AssertMapField20),
        new("MAP-AUTH-01", "Server derives assignee, issuer, and coordinator roles from canonical report state.", P711AssertMapAuth01),
        new("MAP-AUTH-02", "An outsider has no implicit ASSIGNEE fallback.", P711AssertMapAuth02),
        new("MAP-AUTH-03", "Missing policy coverage denies fields and tables by default.", P711AssertMapAuth03),
        new("MAP-AUTH-04", "Exact server role wins over a wildcard policy at higher specificity.", P711AssertMapAuth04),
        new("MAP-AUTH-05", "Hidden source and target fields are unreadable, unwritable, and non-required.", P711AssertMapAuth05),
        new("MAP-AUTH-06", "lockedAfterSubmit makes a field readonly after lifecycle submit.", P711AssertMapAuth06),
        new("MAP-AUTH-07", "Hidden table columns are unreadable, unwritable, and non-required.", P711AssertMapAuth07),
        new("MAP-AUTH-08", "lockedAfterSubmit makes a table column readonly after lifecycle submit.", P711AssertMapAuth08),
        new("MAP-AUTH-09", "Caller preview redaction removes hidden target values and all raw source values.", P711AssertMapAuth09),
        new("MAP-AUTH-10", "Required validation runs against the effective mapped draft and accepts false/zero.", P711AssertMapAuth10),
        new("MAP-AUTH-11", "Mapping-purpose access does not expose raw source identities or revision metadata.", P711AssertMapAuth11),
        new("MAP-AUTH-12", "Caller-owned source, rule, conflict, and contribution selectors are rejected.", P711AssertMapAuth12)
    ];

    private static async Task RunP711FieldAndAuthCoreCasesAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string adminToken,
        ProbeFixture baseFixture,
        P7MappingFixture fixture,
        CancellationToken ct)
    {
        Require(
            P711FieldAndAuthSemanticCases.Length == 32 &&
            P711FieldAndAuthSemanticCases
                .Select(item => item.CaseId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 32,
            "P7-11 Field/Auth semantic registry must contain 32 exact IDs.");

        var workId = await CloneP7MappingWorkAsync(
            database,
            fixture.WorkId,
            "P711-FIELD-AUTH",
            ct);
        var scenario = await PrepareP7MappingScenarioAsync(
            api,
            backend,
            database,
            adminToken,
            baseFixture,
            fixture,
            workId,
            "p711-field-auth-launch",
            "2026-07-P711-FIELD-AUTH",
            ct);
        await ReassignP7MappingSourceForRedactionAsync(
            database,
            scenario,
            fixture.OutsiderUserId,
            ct);
        var initialCaseCount = cases.Results.Count;

        foreach (var semanticCase in P711FieldAndAuthSemanticCases)
        {
            await cases.RunAsync(
                semanticCase.CaseId,
                async () =>
                {
                    Require(
                        string.Equals(
                            HarnessCaseRunner.ActiveCaseId,
                            semanticCase.CaseId,
                            StringComparison.Ordinal),
                        $"{semanticCase.CaseId} did not own ActiveCaseId.");

                    var preview = await PreviewP7MappingAsync(
                        api,
                        scenario,
                        new JsonObject(),
                        ct);
                    AssertP7MappingPreviewIdentity(
                        preview,
                        fixture,
                        scenario,
                        activationThrough: 11);
                    AssertP7MappingCallerRedaction(preview);
                    Require(
                        !preview.Body.Contains(
                            scenario.SourceReportId,
                            StringComparison.Ordinal),
                        $"{semanticCase.CaseId} Kestrel preview leaked the raw source report identity.");

                    var semanticFact = semanticCase.AssertSemantic();
                    mongoEvidence.Add(
                        await CaptureP711FieldAuthMongoObservationAsync(
                            database,
                            scenario.TargetReportId,
                            semanticCase.CaseId,
                            ct));

                    return new CaseObservation(
                        $"{semanticCase.Semantic} The production assertion, case-owned Kestrel preview, and exact direct-Mongo identity query all passed.",
                        P7MappingFingerprint(
                            semanticCase.CaseId,
                            semanticFact,
                            ApiHarnessClient.RequiredString(
                                preview.Json,
                                "sourceSignature"),
                            ApiHarnessClient.RequiredString(
                                preview.Json,
                                "resultSemanticHash")));
                });
        }

        Require(
            cases.Results.Count - initialCaseCount == 32,
            "P7-11 Field/Auth core did not emit exactly 32 case rows.");
    }

    private static async Task<object>
        CaptureP711FieldAuthMongoObservationAsync(
            IMongoDatabase database,
            string targetReportId,
            string caseId,
            CancellationToken ct)
    {
        var collection = database.GetCollection<WorkAssignmentReport>(
            "work_assignment_report");
        var filter = Builders<WorkAssignmentReport>.Filter.And(
            Builders<WorkAssignmentReport>.Filter.Eq(
                report => report.Id,
                targetReportId),
            Builders<WorkAssignmentReport>.Filter.Eq(
                report => report.IsDeleted,
                false));
        var matchedCount = await collection.CountDocumentsAsync(
            filter,
            cancellationToken: ct);
        Require(
            matchedCount == 1,
            $"{caseId} direct-Mongo target identity query matched {matchedCount} rows.");
        return new
        {
            caseId,
            collection = "work_assignment_report",
            queryKind = "EXACT_TARGET_REPORT_ID_NOT_DELETED",
            matchedCount,
            targetIdentitySha256 = P7MappingFingerprint(
                "P7-11-FIELD-AUTH-TARGET",
                targetReportId),
            directMongo = true,
            rawSourceValuesRecorded = false
        };
    }

    private static string P711AssertMapField01()
    {
        P711RequireEqual(
            "P7-EVAL-1",
            DynamicFlowMappingExpressionEvaluator.EvaluatorVersion,
            "MAP-FIELD-01 evaluator version");
        P711RequireEqual(
            "P7-FUNC-1",
            DynamicFlowRegisteredFunctionRegistry.RegistryVersion,
            "MAP-FIELD-01 registry version");
        P711RequireEqual(
            P711FieldRegistryDescriptor,
            DynamicFlowRegisteredFunctionRegistry.CanonicalDescriptor,
            "MAP-FIELD-01 registry descriptor");
        P711RequireEqual(
            P711FieldRegistrySha256,
            DynamicFlowRegisteredFunctionRegistry.RegistrySha256,
            "MAP-FIELD-01 registry SHA");
        P711RequireEqual(
            P711FieldRegistrySha256,
            DynamicFlowRegisteredFunctionRegistry.RegistryHash,
            "MAP-FIELD-01 runtime registry hash");
        DynamicFlowMappingExpressionEvaluator.EnsureVersion("P7-EVAL-1");
        DynamicFlowRegisteredFunctionRegistry.EnsurePin(
            "P7-FUNC-1",
            P711FieldRegistrySha256);
        P711ExpectEvaluationReason(
            () => DynamicFlowMappingExpressionEvaluator.EnsureVersion(
                "P7-EVAL-2"),
            "DYNAMIC_FLOW_MAPPING_EVALUATOR_VERSION_MISMATCH");
        P711ExpectEvaluationReason(
            () => DynamicFlowRegisteredFunctionRegistry.EnsurePin(
                "P7-FUNC-1",
                new string('0', 64)),
            "DYNAMIC_FLOW_MAPPING_FUNCTION_REGISTRY_PIN_MISMATCH");
        return $"P7-EVAL-1|P7-FUNC-1|{P711FieldRegistrySha256}";
    }

    private static string P711AssertMapField02()
    {
        const string literal = "  du lieu  ";
        var expression = P711Copy("text");
        var types = P711Types(("text", "TEXT"));
        var inputs = P711Inputs(("text", JsonValue.Create(literal)));
        P711RequireEqual(
            "TEXT",
            DynamicFlowMappingExpressionEvaluator.Validate(
                expression,
                types),
            "MAP-FIELD-02 validated result type");
        var evaluated = DynamicFlowMappingExpressionEvaluator.Evaluate(
            expression,
            inputs,
            types);
        P711RequireEqual(
            literal,
            evaluated!.GetValue<string>(),
            "MAP-FIELD-02 evaluator literal bytes");
        var field = P711EvaluateField(
            expression,
            types,
            inputs,
            "TEXT");
        P711RequireEqual(
            literal,
            field.ProposedValue!.GetValue<string>(),
            "MAP-FIELD-02 field projection");
        Require(
            field.EvaluatorVersion == "P7-EVAL-1" &&
            field.FunctionRegistryVersion == "P7-FUNC-1" &&
            field.FunctionRegistrySha256 ==
            P711FieldRegistrySha256,
            "MAP-FIELD-02 exported evaluator pins drifted.");
        return $"TEXT={field.ProposedValue.ToJsonString()}";
    }

    private static string P711AssertMapField03()
    {
        var result = P711EvaluateField(
            JsonNode.Parse(
                """{"op":"concat","separator":" ","args":[{"value":"Nguyen"},{"value":"An"}]}""")!,
            P711Types(),
            P711Inputs(),
            "TEXT");
        P711RequireEqual(
            "Nguyen An",
            result.ProposedValue!.GetValue<string>(),
            "MAP-FIELD-03 literal separator");
        Require(
            result.ShouldWrite,
            "MAP-FIELD-03 must request one scalar write.");
        return result.ProposedValue.ToJsonString();
    }

    private static string P711AssertMapField04()
    {
        var result = P711EvaluateField(
            JsonNode.Parse(
                """{"op":"concat","separator":" | ","args":[{"input":"left"},{"input":"right"}]}""")!,
            P711Types(("left", "TEXT"), ("right", "TEXT")),
            P711Inputs(
                ("left", JsonValue.Create("alpha")),
                ("right", JsonValue.Create("omega"))),
            "TEXT");
        P711RequireEqual(
            "alpha | omega",
            result.ProposedValue!.GetValue<string>(),
            "MAP-FIELD-04 deterministic concatenation");
        return result.ProposedValue.ToJsonString();
    }

    private static string P711AssertMapField05()
    {
        var copied = P711EvaluateField(
            P711Copy("number"),
            P711Types(("number", "NUMBER")),
            P711Inputs(("number", JsonValue.Create(12.5m))),
            "NUMBER");
        P711RequireEqual(
            12.5m,
            copied.ProposedValue!.GetValue<decimal>(),
            "MAP-FIELD-05 JSON-native decimal");
        var arguments =
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["numerator"] = P711Copy("numerator"),
                ["denominator"] = P711Copy("denominator")
            };
        P711RequireEqual(
            "NUMBER",
            DynamicFlowRegisteredFunctionRegistry.Validate(
                "CALC_PERCENTAGE",
                1,
                arguments,
                P711Types(
                    ("numerator", "NUMBER"),
                    ("denominator", "NUMBER"))),
            "MAP-FIELD-05 registered result type");
        var percentage =
            DynamicFlowRegisteredFunctionRegistry.Execute(
                "CALC_PERCENTAGE",
                1,
                P711Inputs(
                    ("numerator", JsonValue.Create(25m)),
                    ("denominator", JsonValue.Create(100m))));
        P711RequireEqual(
            25m,
            percentage!.GetValue<decimal>(),
            "MAP-FIELD-05 registered function result");
        return "COPY=12.5|PERCENTAGE=25";
    }

    private static string P711AssertMapField06()
    {
        var current = JsonValue.Create(7m)!;
        var before = current.ToJsonString();
        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                P711Copy("number"),
                P711Types(("number", "NUMBER")),
                P711Inputs(("number", JsonValue.Create("12.5"))),
                "NUMBER",
                currentTargetValue: current),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid);
        P711RequireEqual(
            before,
            current.ToJsonString(),
            "MAP-FIELD-06 unchanged target");
        return $"REASON={DynamicFlowMappingFieldFailureReasons.ValueKindInvalid}|TARGET={before}";
    }

    private static string P711AssertMapField07()
    {
        var result = P711EvaluateField(
            P711Copy("flag"),
            P711Types(("flag", "BOOLEAN")),
            P711Inputs(("flag", JsonValue.Create(true))),
            "BOOLEAN");
        P711RequireEqual(
            true,
            result.ProposedValue!.GetValue<bool>(),
            "MAP-FIELD-07 JSON boolean");
        return "BOOLEAN=true";
    }

    private static string P711AssertMapField08()
    {
        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                P711Copy("flag"),
                P711Types(("flag", "BOOLEAN")),
                P711Inputs(("flag", JsonValue.Create("true"))),
                "BOOLEAN"),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid);
        return $"REASON={DynamicFlowMappingFieldFailureReasons.ValueKindInvalid}";
    }

    private static string P711AssertMapField09()
    {
        var accepted = new List<string>();
        foreach (var value in new[]
                 {
                     "2026",
                     "07/2026",
                     "30/07/2026"
                 })
        {
            var result = P711EvaluateField(
                P711Copy("date"),
                P711Types(("date", "DATE")),
                P711Inputs(("date", JsonValue.Create(value))),
                "DATE");
            P711RequireEqual(
                value,
                result.ProposedValue!.GetValue<string>(),
                $"MAP-FIELD-09 canonical DATE {value}");
            accepted.Add(value);
        }
        return string.Join("|", accepted);
    }

    private static string P711AssertMapField10()
    {
        var result = P711EvaluateField(
            JsonNode.Parse(
                """{"op":"dateAddDays","args":[{"input":"date"},{"input":"days"}]}""")!,
            P711Types(("date", "FULL_DATE"), ("days", "NUMBER")),
            P711Inputs(
                ("date", JsonValue.Create("30/07/2026")),
                ("days", JsonValue.Create(1m))),
            "FULL_DATE");
        P711RequireEqual(
            "31/07/2026",
            result.ProposedValue!.GetValue<string>(),
            "MAP-FIELD-10 canonical date arithmetic");
        return result.ProposedValue.ToJsonString();
    }

    private static string P711AssertMapField11()
    {
        var rejected = new[]
        {
            "2026-07-30",
            "30-07-2026",
            "31/02/2026"
        };
        foreach (var value in rejected)
        {
            P711ExpectEvaluationReason(
                () => P711EvaluateField(
                    P711Copy("date"),
                    P711Types(("date", "FULL_DATE")),
                    P711Inputs(("date", JsonValue.Create(value))),
                    "FULL_DATE"),
                DynamicFlowMappingFieldFailureReasons.DateInvalid);
        }
        return $"REJECTED={rejected.Length}|REASON={DynamicFlowMappingFieldFailureReasons.DateInvalid}";
    }

    private static string P711AssertMapField12()
    {
        var result = P711EvaluateField(
            P711Copy("choice"),
            P711Types(("choice", "SINGLE_SELECT")),
            P711Inputs(("choice", JsonValue.Create("APPROVED"))),
            "SINGLE_SELECT",
            targetChoiceCodes: ["APPROVED", "REJECTED"]);
        Require(
            result.ShouldWrite &&
            result.Disposition == "WRITE" &&
            result.ProposedValue!.GetValue<string>() == "APPROVED",
            "MAP-FIELD-12 exact choice code was not written.");
        return "CHOICE=APPROVED|DISPOSITION=WRITE";
    }

    private static string P711AssertMapField13()
    {
        foreach (var value in new[] { "Approved", "UNKNOWN" })
        {
            P711ExpectEvaluationReason(
                () => P711EvaluateField(
                    P711Copy("choice"),
                    P711Types(("choice", "SINGLE_SELECT")),
                    P711Inputs(("choice", JsonValue.Create(value))),
                    "SINGLE_SELECT",
                    targetChoiceCodes: ["APPROVED", "REJECTED"]),
                DynamicFlowMappingFieldFailureReasons.ChoiceCodeInvalid);
        }
        return $"REASON={DynamicFlowMappingFieldFailureReasons.ChoiceCodeInvalid}|REJECTED=2";
    }

    private static string P711AssertMapField14()
    {
        var valid = P711EvaluateField(
            P711Copy("choices"),
            P711Types(("choices", "MULTI_SELECT")),
            P711Inputs((
                "choices",
                JsonNode.Parse("""["A","B"]"""))),
            "MULTI_SELECT",
            targetChoiceCodes: ["A", "B", "C"]);
        Require(
            valid.ShouldWrite &&
            valid.ProposedValue is JsonArray array &&
            array.Count == 2,
            "MAP-FIELD-14 exact unique choice array was not written.");
        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                P711Copy("choices"),
                P711Types(("choices", "MULTI_SELECT")),
                P711Inputs((
                    "choices",
                    JsonNode.Parse("""["A","A"]"""))),
                "MULTI_SELECT",
                targetChoiceCodes: ["A", "B", "C"]),
            DynamicFlowMappingFieldFailureReasons.ChoiceItemDuplicate);
        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                P711Copy("choices"),
                P711Types(("choices", "MULTI_SELECT")),
                P711Inputs((
                    "choices",
                    JsonNode.Parse("""["A",1]"""))),
                "MULTI_SELECT",
                targetChoiceCodes: ["A", "B", "C"]),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid);
        return "VALID=A+B|DUPLICATE=REJECTED|NON_STRING=REJECTED";
    }

    private static string P711AssertMapField15()
    {
        var result = P711EvaluateField(
            P711Copy("value"),
            P711Types(("value", "TEXT")),
            P711Inputs(("value", null)),
            "TEXT",
            nullPolicy: "KEEP_NULL",
            currentTargetValue: JsonValue.Create("old"));
        Require(
            result.ShouldWrite &&
            result.ProposedValue is null &&
            result.Disposition == "WRITE",
            "MAP-FIELD-15 KEEP_NULL did not emit an explicit null write.");
        return "VALUE=null|DISPOSITION=WRITE";
    }

    private static string P711AssertMapField16()
    {
        var result = P711EvaluateField(
            P711Copy("value"),
            P711Types(("value", "TEXT")),
            P711Inputs(("value", null)),
            "TEXT",
            nullPolicy: "SKIP",
            currentTargetValue: JsonValue.Create("old"));
        Require(
            !result.ShouldWrite &&
            result.ProposedValue is null &&
            result.Disposition == "SKIP_NULL",
            "MAP-FIELD-16 SKIP null disposition drifted.");
        return "WRITE=0|DISPOSITION=SKIP_NULL";
    }

    private static string P711AssertMapField17()
    {
        var current = JsonValue.Create("old")!;
        var before = current.ToJsonString();
        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                P711Copy("value"),
                P711Types(("value", "TEXT")),
                P711Inputs(("value", null)),
                "TEXT",
                nullPolicy: "ERROR",
                currentTargetValue: current),
            DynamicFlowMappingFieldFailureReasons.NullNotAllowed);
        P711RequireEqual(
            before,
            current.ToJsonString(),
            "MAP-FIELD-17 unchanged target");
        return $"REASON={DynamicFlowMappingFieldFailureReasons.NullNotAllowed}|TARGET={before}";
    }

    private static string P711AssertMapField18()
    {
        var expression = P711Copy("value");
        var types = P711Types(("value", "TEXT"));
        var inputs = P711Inputs(("value", JsonValue.Create("new")));
        var overwrite = P711EvaluateField(
            expression,
            types,
            inputs,
            "TEXT",
            conflictPolicy: "OVERWRITE",
            currentTargetValue: JsonValue.Create("old"));
        var targetWins = P711EvaluateField(
            expression,
            types,
            inputs,
            "TEXT",
            conflictPolicy: "TARGET_WINS",
            currentTargetValue: JsonValue.Create("old"));
        Require(
            overwrite.ShouldWrite &&
            overwrite.Disposition == "WRITE" &&
            !targetWins.ShouldWrite &&
            targetWins.Disposition == "TARGET_WINS",
            "MAP-FIELD-18 conflict dispositions drifted.");
        return "OVERWRITE=WRITE|TARGET_WINS=ZERO_WRITE";
    }

    private static string P711AssertMapField19()
    {
        var current = JsonValue.Create("old")!;
        var before = current.ToJsonString();
        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                P711Copy("value"),
                P711Types(("value", "TEXT")),
                P711Inputs(("value", JsonValue.Create("new"))),
                "TEXT",
                conflictPolicy: "ERROR_ON_CONFLICT",
                currentTargetValue: current),
            DynamicFlowMappingFieldFailureReasons.Conflict);
        P711RequireEqual(
            before,
            current.ToJsonString(),
            "MAP-FIELD-19 unchanged target");
        return $"REASON={DynamicFlowMappingFieldFailureReasons.Conflict}|TARGET={before}";
    }

    private static string P711AssertMapField20()
    {
        var current = JsonValue.Create("unchanged")!;
        var before = current.ToJsonString();
        var forbiddenProperties = new[]
        {
            "script",
            "reflection",
            "file",
            "network"
        };
        foreach (var property in forbiddenProperties)
        {
            var expression = new JsonObject
            {
                ["op"] = "copy",
                ["args"] = new JsonArray(P711Copy("text")),
                [property] = "forbidden"
            };
            P711ExpectEvaluationReason(
                () => P711EvaluateField(
                    expression,
                    P711Types(("text", "TEXT")),
                    P711Inputs(("text", JsonValue.Create("mapped"))),
                    "TEXT",
                    currentTargetValue: current),
                "DYNAMIC_FLOW_MAPPING_EXPRESSION_PROPERTY_UNSUPPORTED");
        }

        P711ExpectEvaluationReason(
            () => P711EvaluateField(
                JsonNode.Parse(
                    """{"op":"eval","args":[{"value":"1+1"}]}""")!,
                P711Types(),
                P711Inputs(),
                "TEXT",
                currentTargetValue: current),
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_UNSUPPORTED");
        P711ExpectEvaluationReason(
            () => DynamicFlowRegisteredFunctionRegistry.Execute(
                "CALC_PERCENTAGE",
                1,
                P711Inputs(
                    ("numerator", JsonValue.Create("25")),
                    ("denominator", JsonValue.Create(100m)))),
            "DYNAMIC_FLOW_MAPPING_NUMBER_INVALID");
        P711RequireEqual(
            before,
            current.ToJsonString(),
            "MAP-FIELD-20 unchanged target");
        return $"FORBIDDEN_PROPERTIES={forbiddenProperties.Length}|UNKNOWN_OP=eval|REGISTERED_NUMBER_STRING=REJECTED|TARGET={before}";
    }

    private static string P711AssertMapAuth01()
    {
        var assignment = P711AuthorizationAssignment();
        var report = P711AuthorizationReport();
        P711RequireEqual(
            "ASSIGNEE",
            WorkAssignmentReportService
                .ResolveDirectDynamicFlowActorRole(
                    assignment,
                    report,
                    "assignee"),
            "MAP-AUTH-01 assignee role");
        P711RequireEqual(
            "ISSUER",
            WorkAssignmentReportService
                .ResolveDirectDynamicFlowActorRole(
                    assignment,
                    report,
                    "issuer"),
            "MAP-AUTH-01 issuer role");
        P711RequireEqual(
            "COORDINATOR",
            WorkAssignmentReportService
                .ResolveDirectDynamicFlowActorRole(
                    assignment,
                    report,
                    "coordinator"),
            "MAP-AUTH-01 coordinator role");
        return "assignee=ASSIGNEE|issuer=ISSUER|coordinator=COORDINATOR";
    }

    private static string P711AssertMapAuth02()
    {
        var role = WorkAssignmentReportService
            .ResolveDirectDynamicFlowActorRole(
                P711AuthorizationAssignment(),
                P711AuthorizationReport(),
                "outsider");
        Require(
            role is null,
            $"MAP-AUTH-02 outsider received implicit role {role}.");
        return "OUTSIDER_ROLE=null";
    }

    private static string P711AssertMapAuth03()
    {
        const string emptyPolicyPayload = """
        {
          "steps": [{ "stepId": "source", "stepCode": "SOURCE" }],
          "fieldPolicies": [],
          "tableColumnPolicies": []
        }
        """;
        var evaluator = new DynamicFlowPolicyEvaluator();
        var permissions = evaluator.Evaluate(
            emptyPolicyPayload,
            P711AuthorizationContext("FINALIZER"));
        Require(
            permissions.DenyAllFields &&
            permissions.DenyAllTableColumns &&
            permissions.Fields.Count == 0 &&
            permissions.TableColumns.Count == 0,
            "MAP-AUTH-03 missing policy coverage did not deny by default.");

        var violations =
            DynamicFlowReportPermissionEnforcer.FindWriteViolations(
                permissions,
                """{"values":{"amount":1}}""",
                """{"values":{"amount":2}}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","amount":1}]}]}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","amount":2}]}]}""");
        Require(
            violations.Count(item =>
                item.TargetKind == "FIELD" &&
                item.TargetKey == "amount") == 1 &&
            violations.Count(item =>
                item.TargetKind == "TABLE_COLUMN" &&
                item.TargetKey == "b1:amount") == 1,
            "MAP-AUTH-03 default deny did not block actual field and table-column writes.");

        const string partialPolicyPayload = """
        {
          "steps": [{ "stepId": "source", "stepCode": "SOURCE" }],
          "fieldPolicies": [
            { "policyId": "allowed-field", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "fieldKey": "allowed", "read": true, "write": true }
          ],
          "tableColumnPolicies": [
            { "policyId": "allowed-column", "stepId": "source", "stepCode": "*", "actorRole": "ASSIGNEE", "blockId": "b1", "columnKey": "allowed", "read": true, "write": true }
          ]
        }
        """;
        var partial = evaluator.Evaluate(
            partialPolicyPayload,
            P711AuthorizationContext("ASSIGNEE"));
        var partialViolations =
            DynamicFlowReportPermissionEnforcer.FindWriteViolations(
                partial,
                """{"values":{"allowed":1,"unconfigured":1}}""",
                """{"values":{"allowed":2,"unconfigured":2}}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","allowed":1,"unconfigured":1}]}]}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","allowed":2,"unconfigured":2}]}]}""");
        Require(
            partialViolations.Count == 2 &&
            partialViolations.Count(item =>
                item.TargetKind == "FIELD" &&
                item.TargetKey == "unconfigured") == 1 &&
            partialViolations.Count(item =>
                item.TargetKind == "TABLE_COLUMN" &&
                item.TargetKey == "b1:unconfigured") == 1,
            "MAP-AUTH-03 partial coverage did not allow configured targets and deny unconfigured targets.");

        var readable =
            DynamicFlowReportPermissionEnforcer.ApplyReadRestrictions(
                partial,
                null,
                """{"values":{"allowed":"visible","unconfigured":"field-secret"}}""",
                """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","allowed":"visible","unconfigured":"table-secret"}]}]}""",
                null);
        var readableJson =
            $"{readable.FieldValuesJson}\n{readable.TableValuesJson}";
        Require(
            readableJson.Contains("visible", StringComparison.Ordinal) &&
            !readableJson.Contains("field-secret", StringComparison.Ordinal) &&
            !readableJson.Contains("table-secret", StringComparison.Ordinal),
            "MAP-AUTH-03 partial-policy reads exposed unconfigured field or table values.");
        return "EMPTY=DENY_ALL|WRITES=FIELD+TABLE_DENIED|PARTIAL=CONFIGURED_ALLOWED+UNCONFIGURED_DENIED|READS=UNCONFIGURED_REDACTED";
    }

    private static string P711AssertMapAuth04()
    {
        var permissions = new DynamicFlowPolicyEvaluator().Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext("ASSIGNEE"));
        var amount = permissions.Fields["amount"];
        Require(
            amount.Read &&
            amount.Write &&
            amount.Required &&
            !amount.Hidden &&
            !amount.Locked &&
            amount.SourcePolicyId == "assignee-amount",
            "MAP-AUTH-04 exact assignee policy did not override wildcard deny.");
        return "POLICY=assignee-amount|READ=1|WRITE=1|REQUIRED=1";
    }

    private static string P711AssertMapAuth05()
    {
        var permissions = new DynamicFlowPolicyEvaluator().Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext("ASSIGNEE"));
        var secret = permissions.Fields["secret"];
        Require(
            secret.Hidden &&
            !secret.Read &&
            !secret.Write &&
            !secret.Required,
            "MAP-AUTH-05 hidden field permissions drifted.");
        return "HIDDEN=1|READ=0|WRITE=0|REQUIRED=0";
    }

    private static string P711AssertMapAuth06()
    {
        var evaluator = new DynamicFlowPolicyEvaluator();
        var draft = evaluator.Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext("REVIEWER"));
        var afterSubmit = evaluator.Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext(
                "REVIEWER",
                isAfterSubmit: true));
        var draftScore = draft.Fields["score"];
        var submittedScore = afterSubmit.Fields["score"];
        Require(
            draftScore.Write &&
            draftScore.LockedAfterSubmit &&
            !draftScore.Locked &&
            !submittedScore.Write &&
            submittedScore.Locked &&
            submittedScore.LockedAfterSubmit,
            "MAP-AUTH-06 lockedAfterSubmit field transition drifted.");
        return "DRAFT=WRITE|AFTER_SUBMIT=READ_ONLY";
    }

    private static string P711AssertMapAuth07()
    {
        var permissions = new DynamicFlowPolicyEvaluator().Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext("ASSIGNEE"));
        var secret = permissions.TableColumns["b1:secret"];
        Require(
            secret.Hidden &&
            !secret.Read &&
            !secret.Write &&
            !secret.Required,
            "MAP-AUTH-07 hidden table-column permissions drifted.");
        return "HIDDEN=1|READ=0|WRITE=0|REQUIRED=0";
    }

    private static string P711AssertMapAuth08()
    {
        var evaluator = new DynamicFlowPolicyEvaluator();
        var draft = evaluator.Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext("REVIEWER"));
        var afterSubmit = evaluator.Evaluate(
            P711AuthorizationPolicyPayload,
            P711AuthorizationContext(
                "REVIEWER",
                isAfterSubmit: true));
        var draftScore = draft.TableColumns["b1:score"];
        var submittedScore = afterSubmit.TableColumns["b1:score"];
        Require(
            draftScore.Write &&
            draftScore.LockedAfterSubmit &&
            !draftScore.Locked &&
            !submittedScore.Write &&
            submittedScore.Locked &&
            submittedScore.LockedAfterSubmit,
            "MAP-AUTH-08 lockedAfterSubmit column transition drifted.");
        return "DRAFT=WRITE|AFTER_SUBMIT=READ_ONLY";
    }

    private static string P711AssertMapAuth09()
    {
        const string forbidden = "P7_FORBIDDEN_RAW_VALUE_9B85";
        var permissions = new DynamicFlowPolicyEvaluationResult();
        permissions.Fields["secret"] =
            new DynamicFlowFieldPermissionDto
            {
                TargetKey = "secret",
                FieldKey = "secret",
                Hidden = true
            };
        permissions.Fields["public"] =
            new DynamicFlowFieldPermissionDto
            {
                TargetKey = "public",
                FieldKey = "public",
                Read = true
            };
        var preview = new DynamicFlowMappingPreviewResponse
        {
            FieldValuesJson = JsonSerializer.Serialize(
                new
                {
                    values =
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["secret"] = forbidden,
                            ["public"] = "visible"
                        }
                }),
            SummarySourceJson = JsonSerializer.Serialize(
                new
                {
                    kind = "DYNAMIC_FLOW_MAPPING",
                    changes = new[]
                    {
                        new
                        {
                            sources = new[]
                            {
                                new
                                {
                                    valueJson = forbidden
                                }
                            }
                        }
                    }
                }),
            Changes =
            [
                new DynamicFlowMappingChangeDto
                {
                    MappingId = "hidden",
                    TargetKind = "FIELD",
                    TargetKey = "secret",
                    NextValueJson =
                        JsonSerializer.Serialize(forbidden),
                    Sources =
                    [
                        new DynamicFlowMappingInputProvenanceDto
                        {
                            ValueJson =
                                JsonSerializer.Serialize(forbidden)
                        }
                    ]
                },
                new DynamicFlowMappingChangeDto
                {
                    MappingId = "visible",
                    TargetKind = "FIELD",
                    TargetKey = "public",
                    NextValueJson = "\"visible\"",
                    Sources =
                    [
                        new DynamicFlowMappingInputProvenanceDto
                        {
                            ValueJson =
                                JsonSerializer.Serialize(forbidden)
                        }
                    ]
                }
            ]
        };
        var redacted =
            DynamicFlowReportPermissionEnforcer
                .ApplyMappingPreviewReadRestrictions(
                    permissions,
                    preview);
        var serialized = JsonSerializer.Serialize(redacted);
        var summary = JsonNode.Parse(
            redacted.SummarySourceJson
            ?? throw new InvalidOperationException(
                "MAP-AUTH-09 redacted summary envelope is missing.")) as JsonObject
            ?? throw new InvalidOperationException(
                "MAP-AUTH-09 redacted summary envelope is not an object.");
        var summarySource =
            (summary["changes"] as JsonArray)?
                .OfType<JsonObject>()
                .SelectMany(change =>
                    (change["sources"] as JsonArray)?
                        .OfType<JsonObject>()
                    ?? Enumerable.Empty<JsonObject>())
                .SingleOrDefault()
            ?? throw new InvalidOperationException(
                "MAP-AUTH-09 redacted summary source is missing.");
        Require(
            redacted.Changes.Count == 1 &&
            redacted.Changes[0].MappingId == "visible" &&
            redacted.Changes[0].Sources[0].ValueJson is null &&
            string.Equals(
                summary["kind"]?.GetValue<string>(),
                "DYNAMIC_FLOW_MAPPING",
                StringComparison.Ordinal) &&
            !summarySource.ContainsKey("valueJson") &&
            !serialized.Contains(
                forbidden,
                StringComparison.Ordinal),
            "MAP-AUTH-09 preview redaction leaked hidden/source values or corrupted the summary envelope.");
        return "HIDDEN_CHANGE=OMITTED|VISIBLE_SOURCE_VALUE=NULL|SUMMARY=PARSEABLE+REDACTED";
    }

    private static string P711AssertMapAuth10()
    {
        var permissions = new DynamicFlowPolicyEvaluationResult();
        permissions.Fields["confirmed"] =
            new DynamicFlowFieldPermissionDto
            {
                TargetKey = "confirmed",
                FieldKey = "confirmed",
                Read = false,
                Required = true
            };
        permissions.TableColumns["b1:amount"] =
            new DynamicFlowTableColumnPermissionDto
            {
                TargetKey = "b1:amount",
                BlockId = "b1",
                ColumnKey = "amount",
                Read = false,
                Required = true
            };
        var accepted =
            DynamicFlowReportPermissionEnforcer
                .FindRequiredViolations(
                    permissions,
                    """{"values":{"confirmed":false}}""",
                    """{"blocks":[{"blockId":"B1","tableMode":"APPEND_ROWS","rows":[{"rowKey":"r1","amount":0}]}]}""");
        Require(
            accepted.Count == 0,
            "MAP-AUTH-10 false/zero did not satisfy required values.");
        var missing =
            DynamicFlowReportPermissionEnforcer
                .FindRequiredViolations(
                    permissions,
                    """{"values":{"confirmed":""}}""",
                    """{"blocks":[{"blockId":"b1","tableMode":"APPEND_ROWS","rows":[{"rowKey":"r1","amount":null}]}]}""");
        Require(
            missing.Any(item =>
                item.TargetKind == "FIELD" &&
                item.Reason ==
                "DYNAMIC_FLOW_FIELD_REQUIRED") &&
            missing.Any(item =>
                item.TargetKind == "TABLE_COLUMN" &&
                item.Reason ==
                "DYNAMIC_FLOW_TABLE_COLUMN_REQUIRED"),
            "MAP-AUTH-10 blank/null required values were accepted.");
        return "false+zero=VALID|blank+null=REQUIRED";
    }

    private static string P711AssertMapAuth11()
    {
        const string reportId = "507f1f77bcf86cd799439011";
        const string forbidden = "P7_FORBIDDEN_SOURCE_VALUE_A24C";
        var internalSourceFacts = new
        {
            ReportId = reportId,
            RawValue = forbidden,
            PayloadHash = new string('a', 64)
        };
        var preview = new DynamicFlowMappingPreviewResponse
        {
            SummarySourceJson =
                JsonSerializer.Serialize(
                    new
                    {
                        value = forbidden
                    }),
            SourceReports =
            [
                new DynamicFlowMappingSourceReportDto
                {
                    ReportId = reportId,
                    WorkAssignmentId =
                        "507f1f77bcf86cd799439012",
                    FlowInstanceId =
                        "507f1f77bcf86cd799439013",
                    ExecutionEpoch = 4,
                    StepInstanceId =
                        "507f1f77bcf86cd799439014",
                    BranchId = forbidden,
                    AttemptNo = 2,
                    FlowStepId = forbidden,
                    FlowStepCode = forbidden,
                    PayloadRevision = 8,
                    PayloadHash = new string('a', 64),
                    LifecycleRevision = 3,
                    LifecycleStatus = forbidden
                }
            ],
            Changes =
            [
                new DynamicFlowMappingChangeDto
                {
                    MappingId = "m1",
                    TargetKind = "FIELD",
                    TargetKey = "public",
                    SourceReportId = reportId,
                    SourceKey = forbidden,
                    Sources =
                    [
                        new DynamicFlowMappingInputProvenanceDto
                        {
                            InputKey = forbidden,
                            SourceReportId = reportId,
                            SourceAssignmentId =
                                "507f1f77bcf86cd799439012",
                            SourcePayloadRevision = 8,
                            SourcePayloadHash =
                                new string('a', 64),
                            SourceLifecycleRevision = 3,
                            SourceKey = forbidden,
                            RowKey = forbidden,
                            ValueJson =
                                JsonSerializer.Serialize(forbidden)
                        }
                    ]
                }
            ]
        };
        DynamicFlowMappingCallerRedaction.RedactSourceIdentities(
            preview,
            new HashSet<string>(StringComparer.Ordinal)
            {
                reportId
            });
        var serialized = JsonSerializer.Serialize(preview);
        var source = preview.SourceReports.Single();
        var change = preview.Changes.Single();
        var provenance = change.Sources.Single();
        Require(
            source.IdentityRedacted &&
            source.ReportId is null &&
            source.WorkAssignmentId is null &&
            source.FlowInstanceId is null &&
            source.ExecutionEpoch is null &&
            source.PayloadRevision is null &&
            source.PayloadHash is null &&
            source.LifecycleRevision is null &&
            change.SourceReportId is null &&
            change.SourceKey is null &&
            provenance.InputKey is null &&
            provenance.SourceReportId is null &&
            provenance.SourceAssignmentId is null &&
            provenance.SourcePayloadRevision is null &&
            provenance.SourcePayloadHash is null &&
            provenance.SourceLifecycleRevision is null &&
            provenance.SourceKey is null &&
            provenance.RowKey is null &&
            provenance.ValueJson is null &&
            preview.SummarySourceJson is null &&
            !serialized.Contains(
                forbidden,
                StringComparison.Ordinal) &&
            !serialized.Contains(
                reportId,
                StringComparison.Ordinal),
            "MAP-AUTH-11 caller serialization retained raw source facts.");
        Require(
            internalSourceFacts.ReportId == reportId &&
            internalSourceFacts.RawValue == forbidden &&
            internalSourceFacts.PayloadHash ==
            new string('a', 64),
            "MAP-AUTH-11 redaction mutated server-owned facts.");
        return "CALLER_IDENTITIES=NULL|SERVER_FACTS=UNCHANGED";
    }

    private static string P711AssertMapAuth12()
    {
        var forgedInputs =
            new (string Field, Func<DynamicFlowMappingRequest> Request)[]
            {
                ("actorRole", () => new()
                {
                    ActorRole = "COORDINATOR"
                }),
                ("sourceReportIds", () => new()
                {
                    SourceReportIds = []
                }),
                ("mappingRules", () => new()
                {
                    MappingRules = []
                }),
                ("conflictPolicy", () => new()
                {
                    ConflictPolicy = string.Empty
                }),
                ("contributionPolicy", () => new()
                {
                    ContributionPolicy = string.Empty
                }),
                ("forgedFutureSelector", () =>
                    JsonSerializer.Deserialize<
                        DynamicFlowMappingRequest>(
                        """{"forgedFutureSelector":true}""")
                    ?? throw new InvalidOperationException(
                        "MAP-AUTH-12 future selector did not deserialize."))
            };
        foreach (var forged in forgedInputs)
        {
            try
            {
                DynamicFlowMappingSecurityContract
                    .ValidateFlowOwnedRequestInputs(
                        forged.Request());
                throw new InvalidOperationException(
                    $"MAP-AUTH-12 accepted forged {forged.Field}.");
            }
            catch (DynamicFlowMappingSecurityException error)
            {
                Require(
                    error.Reason ==
                    DynamicFlowMappingSecurityContract
                        .FlowOwnedConfigurationReason &&
                    error.Field == forged.Field,
                    $"MAP-AUTH-12 stable rejection drifted for {forged.Field}.");
            }
        }
        DynamicFlowMappingSecurityContract
            .ValidateFlowOwnedRequestInputs(
                new DynamicFlowMappingRequest
                {
                    FlowVersionId = "caller-assertion",
                    FlowPayloadHash = new string('a', 64),
                    ExpectedPayloadRevision = 1,
                    ExpectedLifecycleRevision = 0
                });
        return $"FORGED_FIELDS={forgedInputs.Length}|CALLER_ASSERTIONS=ALLOWED";
    }

    private static DynamicFlowMappingFieldEvaluationResult
        P711EvaluateField(
            JsonNode expression,
            IReadOnlyDictionary<string, string> inputDataTypes,
            IReadOnlyDictionary<string, JsonNode?> inputs,
            string targetDataType,
            string nullPolicy = "KEEP_NULL",
            string conflictPolicy = "OVERWRITE",
            JsonNode? currentTargetValue = null,
            IReadOnlyCollection<string>? targetChoiceCodes = null)
        => DynamicFlowMappingFieldContract.Evaluate(
            expression,
            inputDataTypes,
            inputs,
            targetDataType,
            nullPolicy,
            conflictPolicy,
            currentTargetValue,
            targetChoiceCodes);

    private static JsonNode P711Copy(string inputKey)
        => new JsonObject
        {
            ["op"] = "copy",
            ["args"] = new JsonArray(
                new JsonObject
                {
                    ["input"] = inputKey
                })
        };

    private static IReadOnlyDictionary<string, string> P711Types(
        params (string Key, string DataType)[] values)
        => values.ToDictionary(
            item => item.Key,
            item => item.DataType,
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonNode?> P711Inputs(
        params (string Key, JsonNode? Value)[] values)
        => values.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);

    private static void P711ExpectEvaluationReason(
        Action action,
        string expectedReason)
    {
        try
        {
            action();
        }
        catch (DynamicFlowMappingEvaluationException error)
        {
            P711RequireEqual(
                expectedReason,
                error.Reason,
                "P7-11 stable evaluation reason");
            return;
        }
        throw new InvalidOperationException(
            $"Expected stable evaluation reason {expectedReason}.");
    }

    private static void P711RequireEqual<T>(
        T expected,
        T actual,
        string subject)
    {
        Require(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{subject}; expected={expected}; actual={actual}.");
    }

    private static DynamicFlowPolicyEvaluationContext
        P711AuthorizationContext(
            string role,
            bool isAfterSubmit = false)
        => new()
        {
            StepId = "source",
            StepCode = "SOURCE",
            ActorRole = role,
            IsAfterSubmit = isAfterSubmit
        };

    private static WorkAssignment P711AuthorizationAssignment()
        => new()
        {
            Id = "assignment",
            CreatedByUserId = "issuer",
            FlowRole = "ASSIGNEE",
            LeaderWatcherUserIds = ["coordinator"],
            Assignees = []
        };

    private static WorkAssignmentReport P711AuthorizationReport()
        => new()
        {
            Id = "report",
            WorkAssignmentId = "assignment",
            AssigneeUserId = "assignee"
        };

    private sealed record P711FieldAuthSemanticCase(
        string CaseId,
        string Semantic,
        Func<string> AssertSemantic);
}
