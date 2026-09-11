using System.Text.Json.Nodes;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingFieldContractTests
{
    private sealed record ContractCase(string Id, string Semantic, Action Run);

    private const string RegistryDescriptor =
        "P7-FUNC-1\nCALC_PERCENTAGE|1|NUMBER|denominator:NUMBER,numerator:NUMBER";
    private const string RegistrySha256 =
        "c85f918f982a4c556999c72242d8a16912b8d1ae40e3d532e331f86d3acf9adb";

    private static readonly IReadOnlyList<ContractCase> Cases =
    [
        new("MAP-FIELD-01", "Exact evaluator/function-registry version and deterministic SHA pins", ExactVersionAndRegistryPins),
        new("MAP-FIELD-02", "TEXT copy has Validate/Evaluate parity and preserves literal value bytes", TextCopyParity),
        new("MAP-FIELD-03", "Whitespace-only separator remains literal data", LiteralWhitespaceSeparator),
        new("MAP-FIELD-04", "Multi-input TEXT concatenation is deterministic", MultiInputText),
        new("MAP-FIELD-05", "NUMBER and registered function accept JSON numbers only", JsonNativeNumber),
        new("MAP-FIELD-06", "Numeric strings fail closed without coercion or target mutation", NumericStringRejected),
        new("MAP-FIELD-07", "BOOLEAN copy accepts a JSON boolean", JsonNativeBoolean),
        new("MAP-FIELD-08", "Boolean strings fail closed without coercion", BooleanStringRejected),
        new("MAP-FIELD-09", "DATE accepts the canonical year/month/full-date JSON string forms", CanonicalDate),
        new("MAP-FIELD-10", "FULL_DATE arithmetic preserves the canonical dd/MM/yyyy form", CanonicalFullDateArithmetic),
        new("MAP-FIELD-11", "Non-canonical date strings fail with a stable field reason", NonCanonicalDateRejected),
        new("MAP-FIELD-12", "SINGLE_SELECT writes an exact allowed choice code", SingleChoiceCode),
        new("MAP-FIELD-13", "Choice labels and unlisted codes fail closed", ChoiceLabelRejected),
        new("MAP-FIELD-14", "MULTI_SELECT requires exact unique JSON-string choice codes", MultiChoiceCodes),
        new("MAP-FIELD-15", "KEEP_NULL produces an explicit null field write", KeepNull),
        new("MAP-FIELD-16", "SKIP null policy returns a deterministic zero-write disposition", SkipNull),
        new("MAP-FIELD-17", "ERROR null policy is stable and leaves the target untouched", ErrorOnNull),
        new("MAP-FIELD-18", "OVERWRITE and TARGET_WINS conflict policies are explicit", OverwriteAndTargetWins),
        new("MAP-FIELD-19", "ERROR_ON_CONFLICT is stable and leaves the target untouched", ErrorOnConflict),
        new("MAP-FIELD-20", "Script/reflection/file/network and unknown operations are rejected before any write", UnsafeSurfaceRejected)
    ];

    public static IReadOnlyDictionary<string, string> SemanticRegistry { get; } =
        Cases.ToDictionary(item => item.Id, item => item.Semantic, StringComparer.Ordinal);

    public static void Run()
    {
        AssertEqual(20, Cases.Count, "MAP-FIELD semantic registry count");
        AssertEqual(20, SemanticRegistry.Count, "MAP-FIELD unique semantic registry count");
        for (var number = 1; number <= 20; number++)
        {
            var id = $"MAP-FIELD-{number:00}";
            AssertTrue(SemanticRegistry.ContainsKey(id), $"semantic registry must contain {id}");
        }

        foreach (var contractCase in Cases)
        {
            try
            {
                contractCase.Run();
                Console.WriteLine($"PASS {contractCase.Id} {contractCase.Semantic}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{contractCase.Id} ({contractCase.Semantic}) failed: {ex.Message}",
                    ex);
            }
        }
    }

    private static void ExactVersionAndRegistryPins()
    {
        AssertEqual("P7-EVAL-1", DynamicFlowMappingExpressionEvaluator.EvaluatorVersion, "evaluator version");
        AssertEqual("P7-FUNC-1", DynamicFlowRegisteredFunctionRegistry.RegistryVersion, "function registry version");
        AssertEqual(RegistryDescriptor, DynamicFlowRegisteredFunctionRegistry.CanonicalDescriptor, "canonical registry descriptor");
        AssertEqual(RegistrySha256, DynamicFlowRegisteredFunctionRegistry.RegistrySha256, "canonical registry SHA-256");
        AssertEqual(RegistrySha256, DynamicFlowRegisteredFunctionRegistry.RegistryHash, "runtime registry hash alias");

        DynamicFlowMappingExpressionEvaluator.EnsureVersion("P7-EVAL-1");
        DynamicFlowRegisteredFunctionRegistry.EnsurePin("P7-FUNC-1", RegistrySha256);
        ExpectReason(
            () => DynamicFlowMappingExpressionEvaluator.EnsureVersion("P7-EVAL-2"),
            "DYNAMIC_FLOW_MAPPING_EVALUATOR_VERSION_MISMATCH");
        ExpectReason(
            () => DynamicFlowRegisteredFunctionRegistry.EnsurePin("P7-FUNC-1", new string('0', 64)),
            "DYNAMIC_FLOW_MAPPING_FUNCTION_REGISTRY_PIN_MISMATCH");
    }

    private static void TextCopyParity()
    {
        var expression = Copy("text");
        var types = Types(("text", "TEXT"));
        var inputs = Inputs(("text", JsonValue.Create("  dữ liệu  ")));

        AssertEqual("TEXT", DynamicFlowMappingExpressionEvaluator.Validate(expression, types), "validated result type");
        var evaluated = DynamicFlowMappingExpressionEvaluator.Evaluate(expression, inputs, types);
        AssertEqual("  dữ liệu  ", evaluated!.GetValue<string>(), "typed evaluator result");

        var field = EvaluateField(expression, types, inputs, "TEXT");
        AssertEqual("  dữ liệu  ", field.ProposedValue!.GetValue<string>(), "field projection");
        AssertEqual("P7-EVAL-1", field.EvaluatorVersion, "exported evaluator version");
        AssertEqual("P7-FUNC-1", field.FunctionRegistryVersion, "exported registry version");
        AssertEqual(RegistrySha256, field.FunctionRegistrySha256, "exported registry SHA-256");
    }

    private static void LiteralWhitespaceSeparator()
    {
        var result = EvaluateField(
            JsonNode.Parse(
                """{"op":"concat","separator":" ","args":[{"value":"Nguyễn"},{"value":"An"}]}""")!,
            Types(),
            Inputs(),
            "TEXT");

        AssertEqual("Nguyễn An", result.ProposedValue!.GetValue<string>(), "literal separator oracle");
        AssertTrue(result.ShouldWrite, "new scalar projection should request one write");
    }

    private static void MultiInputText()
    {
        var expression = JsonNode.Parse(
            """{"op":"concat","separator":" | ","args":[{"input":"left"},{"input":"right"}]}""")!;
        var result = EvaluateField(
            expression,
            Types(("left", "TEXT"), ("right", "TEXT")),
            Inputs(
                ("left", JsonValue.Create("alpha")),
                ("right", JsonValue.Create("omega"))),
            "TEXT");

        AssertEqual("alpha | omega", result.ProposedValue!.GetValue<string>(), "multi-input text");
    }

    private static void JsonNativeNumber()
    {
        var result = EvaluateField(
            Copy("number"),
            Types(("number", "NUMBER")),
            Inputs(("number", JsonValue.Create(12.5m))),
            "NUMBER");
        AssertEqual(12.5m, result.ProposedValue!.GetValue<decimal>(), "JSON decimal copy");

        var functionArguments = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["numerator"] = Copy("numerator"),
            ["denominator"] = Copy("denominator")
        };
        var resultType = DynamicFlowRegisteredFunctionRegistry.Validate(
            "CALC_PERCENTAGE",
            1,
            functionArguments,
            Types(("numerator", "NUMBER"), ("denominator", "NUMBER")));
        AssertEqual("NUMBER", resultType, "registered function result type");

        var functionResult = DynamicFlowRegisteredFunctionRegistry.Execute(
            "CALC_PERCENTAGE",
            1,
            Inputs(
                ("numerator", JsonValue.Create(25m)),
                ("denominator", JsonValue.Create(100m))));
        AssertEqual(25m, functionResult!.GetValue<decimal>(), "registered function value");
    }

    private static void NumericStringRejected()
    {
        var current = JsonValue.Create(7m)!;
        var before = current.ToJsonString();
        ExpectReason(
            () => EvaluateField(
                Copy("number"),
                Types(("number", "NUMBER")),
                Inputs(("number", JsonValue.Create("12.5"))),
                "NUMBER",
                currentTargetValue: current),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid);
        AssertEqual(before, current.ToJsonString(), "target remains unchanged after numeric-string rejection");
    }

    private static void JsonNativeBoolean()
    {
        var result = EvaluateField(
            Copy("flag"),
            Types(("flag", "BOOLEAN")),
            Inputs(("flag", JsonValue.Create(true))),
            "BOOLEAN");
        AssertEqual(true, result.ProposedValue!.GetValue<bool>(), "JSON boolean copy");
    }

    private static void BooleanStringRejected()
    {
        ExpectReason(
            () => EvaluateField(
                Copy("flag"),
                Types(("flag", "BOOLEAN")),
                Inputs(("flag", JsonValue.Create("true"))),
                "BOOLEAN"),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid);
    }

    private static void CanonicalDate()
    {
        foreach (var value in new[] { "2026", "07/2026", "30/07/2026" })
        {
            var result = EvaluateField(
                Copy("date"),
                Types(("date", "DATE")),
                Inputs(("date", JsonValue.Create(value))),
                "DATE");
            AssertEqual(value, result.ProposedValue!.GetValue<string>(), $"canonical DATE {value}");
        }
    }

    private static void CanonicalFullDateArithmetic()
    {
        var expression = JsonNode.Parse(
            """{"op":"dateAddDays","args":[{"input":"date"},{"input":"days"}]}""")!;
        var result = EvaluateField(
            expression,
            Types(("date", "FULL_DATE"), ("days", "NUMBER")),
            Inputs(
                ("date", JsonValue.Create("30/07/2026")),
                ("days", JsonValue.Create(1m))),
            "FULL_DATE");

        AssertEqual("31/07/2026", result.ProposedValue!.GetValue<string>(), "canonical dateAddDays output");
    }

    private static void NonCanonicalDateRejected()
    {
        ExpectReason(
            () => EvaluateField(
                Copy("date"),
                Types(("date", "FULL_DATE")),
                Inputs(("date", JsonValue.Create("2026-07-30"))),
                "FULL_DATE"),
            DynamicFlowMappingFieldFailureReasons.DateInvalid);
    }

    private static void SingleChoiceCode()
    {
        var result = EvaluateField(
            Copy("choice"),
            Types(("choice", "SINGLE_SELECT")),
            Inputs(("choice", JsonValue.Create("APPROVED"))),
            "SINGLE_SELECT",
            targetChoiceCodes: ["APPROVED", "REJECTED"]);
        AssertEqual("APPROVED", result.ProposedValue!.GetValue<string>(), "exact single choice code");
    }

    private static void ChoiceLabelRejected()
    {
        ExpectReason(
            () => EvaluateField(
                Copy("choice"),
                Types(("choice", "SINGLE_SELECT")),
                Inputs(("choice", JsonValue.Create("Approved"))),
                "SINGLE_SELECT",
                targetChoiceCodes: ["APPROVED", "REJECTED"]),
            DynamicFlowMappingFieldFailureReasons.ChoiceCodeInvalid);
    }

    private static void MultiChoiceCodes()
    {
        var result = EvaluateField(
            Copy("choices"),
            Types(("choices", "MULTI_SELECT")),
            Inputs(("choices", new JsonArray("A", "B"))),
            "MULTI_SELECT",
            targetChoiceCodes: ["A", "B", "C"]);
        AssertEqual("""["A","B"]""", result.ProposedValue!.ToJsonString(), "exact multi choice codes");

        ExpectReason(
            () => EvaluateField(
                Copy("choices"),
                Types(("choices", "MULTI_SELECT")),
                Inputs(("choices", new JsonArray("A", "A"))),
                "MULTI_SELECT",
                targetChoiceCodes: ["A", "B"]),
            DynamicFlowMappingFieldFailureReasons.ChoiceItemDuplicate);
    }

    private static void KeepNull()
    {
        var result = EvaluateField(
            Copy("text"),
            Types(("text", "TEXT")),
            Inputs(("text", null)),
            "TEXT",
            nullPolicy: "KEEP_NULL",
            currentTargetValue: JsonValue.Create("old"));
        AssertTrue(result.ShouldWrite, "KEEP_NULL should explicitly clear an existing value");
        AssertEqual<JsonNode?>(null, result.ProposedValue, "KEEP_NULL proposed value");
        AssertEqual("WRITE", result.Disposition, "KEEP_NULL disposition");
    }

    private static void SkipNull()
    {
        var result = EvaluateField(
            Copy("text"),
            Types(("text", "TEXT")),
            Inputs(("text", null)),
            "TEXT",
            nullPolicy: "SKIP",
            currentTargetValue: JsonValue.Create("old"));
        AssertFalse(result.ShouldWrite, "SKIP must be zero-write");
        AssertEqual("SKIP_NULL", result.Disposition, "SKIP disposition");
    }

    private static void ErrorOnNull()
    {
        var current = JsonValue.Create("old")!;
        var before = current.ToJsonString();
        ExpectReason(
            () => EvaluateField(
                Copy("text"),
                Types(("text", "TEXT")),
                Inputs(("text", null)),
                "TEXT",
                nullPolicy: "ERROR",
                currentTargetValue: current),
            DynamicFlowMappingFieldFailureReasons.NullNotAllowed);
        AssertEqual(before, current.ToJsonString(), "target remains unchanged after null error");
    }

    private static void OverwriteAndTargetWins()
    {
        var expression = Copy("text");
        var types = Types(("text", "TEXT"));
        var inputs = Inputs(("text", JsonValue.Create("mapped")));

        var overwrite = EvaluateField(
            expression,
            types,
            inputs,
            "TEXT",
            conflictPolicy: "OVERWRITE",
            currentTargetValue: JsonValue.Create("manual"));
        AssertTrue(overwrite.ShouldWrite, "OVERWRITE should write the mapped value");
        AssertEqual("WRITE", overwrite.Disposition, "OVERWRITE disposition");

        var targetWins = EvaluateField(
            expression,
            types,
            inputs,
            "TEXT",
            conflictPolicy: "TARGET_WINS",
            currentTargetValue: JsonValue.Create("manual"));
        AssertFalse(targetWins.ShouldWrite, "TARGET_WINS must be zero-write");
        AssertEqual("TARGET_WINS", targetWins.Disposition, "TARGET_WINS disposition");
    }

    private static void ErrorOnConflict()
    {
        var current = JsonValue.Create("manual")!;
        var before = current.ToJsonString();
        ExpectReason(
            () => EvaluateField(
                Copy("text"),
                Types(("text", "TEXT")),
                Inputs(("text", JsonValue.Create("mapped"))),
                "TEXT",
                conflictPolicy: "ERROR_ON_CONFLICT",
                currentTargetValue: current),
            DynamicFlowMappingFieldFailureReasons.Conflict);
        AssertEqual(before, current.ToJsonString(), "target remains unchanged after conflict error");
    }

    private static void UnsafeSurfaceRejected()
    {
        var current = JsonValue.Create("safe")!;
        var before = current.ToJsonString();
        foreach (var property in new[] { "script", "reflection", "file", "network" })
        {
            var expression = new JsonObject
            {
                ["op"] = "copy",
                ["args"] = new JsonArray(Copy("text")),
                [property] = "forbidden"
            };
            ExpectReason(
                () => EvaluateField(
                    expression,
                    Types(("text", "TEXT")),
                    Inputs(("text", JsonValue.Create("mapped"))),
                    "TEXT",
                    currentTargetValue: current),
                "DYNAMIC_FLOW_MAPPING_EXPRESSION_PROPERTY_UNSUPPORTED");
        }

        ExpectReason(
            () => EvaluateField(
                JsonNode.Parse("""{"op":"eval","args":[{"value":"1+1"}]}""")!,
                Types(),
                Inputs(),
                "TEXT",
                currentTargetValue: current),
            "DYNAMIC_FLOW_MAPPING_EXPRESSION_OPERATION_UNSUPPORTED");
        ExpectReason(
            () => DynamicFlowRegisteredFunctionRegistry.Execute(
                "CALC_PERCENTAGE",
                1,
                Inputs(
                    ("numerator", JsonValue.Create("25")),
                    ("denominator", JsonValue.Create(100m)))),
            "DYNAMIC_FLOW_MAPPING_NUMBER_INVALID");
        AssertEqual(before, current.ToJsonString(), "unsafe evaluator failures leave target untouched");
    }

    private static DynamicFlowMappingFieldEvaluationResult EvaluateField(
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

    private static JsonNode Copy(string inputKey)
        => new JsonObject
        {
            ["op"] = "copy",
            ["args"] = new JsonArray(new JsonObject { ["input"] = inputKey })
        };

    private static IReadOnlyDictionary<string, string> Types(
        params (string Key, string DataType)[] values)
        => values.ToDictionary(item => item.Key, item => item.DataType, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonNode?> Inputs(
        params (string Key, JsonNode? Value)[] values)
        => values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static void ExpectReason(Action action, string expectedReason)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected stable reason {expectedReason}.");
        }
        catch (DynamicFlowMappingEvaluationException ex)
        {
            AssertEqual(expectedReason, ex.Reason, "stable evaluator reason");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message)
    {
        if (condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
    }
}
