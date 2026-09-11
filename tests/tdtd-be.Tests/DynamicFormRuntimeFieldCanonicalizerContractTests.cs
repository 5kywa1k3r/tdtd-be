using System.Text.Json;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class DynamicFormRuntimeFieldCanonicalizerContractTests
{
    public static void Run()
    {
        CanonicalizesAllTenTypesDeterministically();
        RejectsWrongJsonKindForEveryCanonicalType();
        RejectsUnknownAndDuplicateContractsAndValues();
        RejectsBlankAndDuplicateListItems();
        EnforcesChoiceCodesInsteadOfLabelsOrArbitraryText();
        ValidatesFlexibleAndFullDatesStrictly();
        RequiredStagePreservesFalseAndZero();
        SanitizesRichTextDeterministically();
        RejectsDangerousAndMalformedRichText();
        AllowsOptionalNullValues();
    }

    private static void CanonicalizesAllTenTypesDeterministically()
    {
        var fields = AllFields().Reverse().ToArray();
        const string input = """
            {
              "zMetadata": { "source": "browser" },
              "values": {
                "short_key": " CODE_A ",
                "f-long": "  preserve long text  ",
                "f-rich": "<P onclick=\"evil()\" data-extra=\"drop\" style=\"color: red; text-align: CENTER\">Hello <custom>world</custom><!--drop--></P>",
                "f-list": [" first ", "second"],
                "f-number": 0,
                "f-date": "07/2026",
                "f-full-date": "29/02/2024",
                "f-single": "ONE",
                "f-multi": [" B ", "A"],
                "f-boolean": false
              },
              "dynamicFormTemplateId": "form-v1"
            }
            """;

        var result = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
            input,
            fields,
            validateRequiredFields: true);
        var repeated = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
            input,
            AllFields(),
            validateRequiredFields: true);

        Equal(result.CanonicalFieldValuesJson, repeated.CanonicalFieldValuesJson, "definition order must not affect canonical JSON");
        True(result.UsesValuesEnvelope, "values envelope should be preserved");
        SequenceEqual(new[] { "f-rich" }, result.SanitizedRichTextFieldIds, "sanitized rich-text field ids");

        using var document = JsonDocument.Parse(result.CanonicalFieldValuesJson);
        var root = document.RootElement;
        Equal("form-v1", root.GetProperty("dynamicFormTemplateId").GetString(), "envelope metadata");
        Equal("browser", root.GetProperty("zMetadata").GetProperty("source").GetString(), "nested envelope metadata");

        var values = root.GetProperty("values");
        Equal("CODE_A", values.GetProperty("f-short").GetString(), "shortText code and key alias");
        Equal("  preserve long text  ", values.GetProperty("f-long").GetString(), "longText scalar preservation");
        Equal(
            "<p style=\"text-align: center\">Hello world</p>",
            values.GetProperty("f-rich").GetString(),
            "richText allow-list canonicalization");
        Equal("first", values.GetProperty("f-list")[0].GetString(), "stringList item trim");
        Equal("second", values.GetProperty("f-list")[1].GetString(), "stringList second item");
        Equal(JsonValueKind.Number, values.GetProperty("f-number").ValueKind, "number JSON kind");
        Equal(0m, values.GetProperty("f-number").GetDecimal(), "number zero");
        Equal("07/2026", values.GetProperty("f-date").GetString(), "date value");
        Equal("29/02/2024", values.GetProperty("f-full-date").GetString(), "fullDate value");
        Equal("ONE", values.GetProperty("f-single").GetString(), "singleSelect code");
        Equal("B", values.GetProperty("f-multi")[0].GetString(), "multiSelect first code");
        Equal("A", values.GetProperty("f-multi")[1].GetString(), "multiSelect second code");
        Equal(JsonValueKind.False, values.GetProperty("f-boolean").ValueKind, "boolean false JSON kind");
    }

    private static void RejectsWrongJsonKindForEveryCanonicalType()
    {
        var cases = new (string Type, string ValueJson, IReadOnlyCollection<string>? Codes)[]
        {
            ("shortText", "1", new[] { "A" }),
            ("longText", "[]", null),
            ("richText", "[]", null),
            ("stringList", "\"text\"", null),
            ("number", "\"1\"", null),
            ("date", "2026", null),
            ("fullDate", "\"07/2026\"", null),
            ("singleSelect", "[\"A\"]", new[] { "A" }),
            ("multiSelect", "\"A\"", new[] { "A" }),
            ("boolean", "0", null)
        };

        foreach (var item in cases)
        {
            var error = Throws(() => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                $"{{\"value\":{item.ValueJson}}}",
                new[] { new ResolvedDynamicFormRuntimeFieldDefinition("value", item.Type, AllowedChoiceCodes: item.Codes) },
                validateRequiredFields: false));
            True(
                error.Reason is DynamicFormRuntimeFieldValidationReasons.ValueKindInvalid or
                    DynamicFormRuntimeFieldValidationReasons.DateInvalid,
                $"{item.Type} wrong-kind reason");
        }
    }

    private static void RejectsUnknownAndDuplicateContractsAndValues()
    {
        Reason(
            DynamicFormRuntimeFieldValidationReasons.TypeUnsupported,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{}",
                new[] { new ResolvedDynamicFormRuntimeFieldDefinition("f", "mystery") },
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.DefinitionDuplicate,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{}",
                new[]
                {
                    new ResolvedDynamicFormRuntimeFieldDefinition("same", "longText"),
                    new ResolvedDynamicFormRuntimeFieldDefinition("same", "number")
                },
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.DefinitionDuplicate,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{}",
                new[]
                {
                    new ResolvedDynamicFormRuntimeFieldDefinition("first", "longText", Key: "collision"),
                    new ResolvedDynamicFormRuntimeFieldDefinition("collision", "number")
                },
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.PropertyDuplicate,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"values\":{\"f\":\"one\",\"f\":\"two\"}}",
                new[] { new ResolvedDynamicFormRuntimeFieldDefinition("f", "longText") },
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.FieldValueDuplicate,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"field-id\":\"one\",\"field-key\":\"two\"}",
                new[] { new ResolvedDynamicFormRuntimeFieldDefinition("field-id", "longText", Key: "field-key") },
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.FieldUnknown,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"unknown\":true}",
                Array.Empty<ResolvedDynamicFormRuntimeFieldDefinition>(),
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.ChoiceOptionsInvalid,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{}",
                new[]
                {
                    new ResolvedDynamicFormRuntimeFieldDefinition(
                        "choice",
                        "singleSelect",
                        AllowedChoiceCodes: new[] { "A", " A " })
                },
                false));
    }

    private static void RejectsBlankAndDuplicateListItems()
    {
        var stringList = new[] { new ResolvedDynamicFormRuntimeFieldDefinition("list", "stringList") };
        Reason(
            DynamicFormRuntimeFieldValidationReasons.ListItemBlank,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"list\":[\"ok\",\"   \"]}", stringList, false));
        Reason(
            DynamicFormRuntimeFieldValidationReasons.ListItemDuplicate,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"list\":[\"same\",\" same \"]}", stringList, false));

        var multi = new[]
        {
            new ResolvedDynamicFormRuntimeFieldDefinition(
                "multi",
                "multiSelect",
                AllowedChoiceCodes: new[] { "A", "B" })
        };
        Reason(
            DynamicFormRuntimeFieldValidationReasons.ListItemDuplicate,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"multi\":[\"A\",\" A \"]}", multi, false));
    }

    private static void EnforcesChoiceCodesInsteadOfLabelsOrArbitraryText()
    {
        var fields = new[]
        {
            new ResolvedDynamicFormRuntimeFieldDefinition(
                "choice",
                "singleSelect",
                AllowedChoiceCodes: new[] { "CODE" })
        };

        Reason(
            DynamicFormRuntimeFieldValidationReasons.ChoiceCodeInvalid,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"choice\":\"Display label\"}", fields, false));
        Reason(
            DynamicFormRuntimeFieldValidationReasons.ChoiceCodeInvalid,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"choice\":\"code\"}", fields, false));
        Reason(
            DynamicFormRuntimeFieldValidationReasons.ChoiceCodeInvalid,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"choice\":\"\"}", fields, false));
    }

    private static void ValidatesFlexibleAndFullDatesStrictly()
    {
        var flexible = new[] { new ResolvedDynamicFormRuntimeFieldDefinition("date", "date") };
        foreach (var valid in new[] { "29/02/2024", "02/2024", "2024" })
        {
            DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                $"{{\"date\":\"{valid}\"}}",
                flexible,
                false);
        }

        foreach (var invalid in new[] { "29/02/2023", "13/2024", "0000", "2024-02-29", "" })
        {
            Reason(
                DynamicFormRuntimeFieldValidationReasons.DateInvalid,
                () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                    $"{{\"date\":\"{invalid}\"}}",
                    flexible,
                    false));
        }

        var full = new[] { new ResolvedDynamicFormRuntimeFieldDefinition("date", "fullDate") };
        Reason(
            DynamicFormRuntimeFieldValidationReasons.DateInvalid,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize("{\"date\":\"02/2024\"}", full, false));
    }

    private static void RequiredStagePreservesFalseAndZero()
    {
        var fields = new[]
        {
            new ResolvedDynamicFormRuntimeFieldDefinition("number", "number", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("boolean", "boolean", Required: true)
        };

        var result = DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
            "{\"number\":0,\"boolean\":false}",
            fields,
            validateRequiredFields: true);
        using var document = JsonDocument.Parse(result.CanonicalValuesJson);
        Equal(0m, document.RootElement.GetProperty("number").GetDecimal(), "required zero");
        False(document.RootElement.GetProperty("boolean").GetBoolean(), "required false");

        Reason(
            DynamicFormRuntimeFieldValidationReasons.ValueRequired,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"number\":0}",
                fields,
                validateRequiredFields: true));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.ValueRequired,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"text\":\"   \"}",
                new[] { new ResolvedDynamicFormRuntimeFieldDefinition("text", "longText", Required: true) },
                validateRequiredFields: true));
    }

    private static void SanitizesRichTextDeterministically()
    {
        const string source = "<div class=\"drop\" onmouseover=\"evil()\"><span style=\"padding-left: 2PX; color: red; text-decoration: UNDERLINE\">Safe</span><mark> text</mark><!--drop--></div>";
        var first = DynamicFormRuntimeRichTextSanitizer.Sanitize(source);
        var second = DynamicFormRuntimeRichTextSanitizer.Sanitize(source);

        True(first.WasSanitized, "rich text should report sanitization");
        Equal(first.Html, second.Html, "rich text sanitization must be deterministic");
        Equal(
            "<div><span style=\"padding-left: 2px; text-decoration: underline\">Safe</span> text</div>",
            first.Html,
            "rich text sanitizer output");
    }

    private static void RejectsDangerousAndMalformedRichText()
    {
        var fields = new[] { new ResolvedDynamicFormRuntimeFieldDefinition("rich", "richText") };
        foreach (var unsafeHtml in new[]
                 {
                     "<script>alert(1)</script>",
                     "<a href=\"java&#x73;cript:alert(1)\">click</a>",
                     "<span style=\"background-image: url(javascript:alert(1))\">x</span>",
                     "<svg><a>bad</a></svg>"
                 })
        {
            Reason(
                DynamicFormRuntimeFieldValidationReasons.RichTextUnsafe,
                () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                    JsonSerializer.Serialize(new { rich = unsafeHtml }),
                    fields,
                    false));
        }

        Reason(
            DynamicFormRuntimeFieldValidationReasons.RichTextInvalid,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"rich\":\"<p>unclosed\"}",
                fields,
                false));

        Reason(
            DynamicFormRuntimeFieldValidationReasons.ValueRequired,
            () => DynamicFormRuntimeFieldCanonicalizer.Canonicalize(
                "{\"rich\":\"<p><br></p>\"}",
                new[] { new ResolvedDynamicFormRuntimeFieldDefinition("rich", "richText", Required: true) },
                true));
    }

    private static void AllowsOptionalNullValues()
    {
        var fields = AllFields()
            .Select(field => field with { Required = false, AllowedChoiceCodes = Array.Empty<string>() })
            .ToArray();
        var values = string.Join(",", fields.Select(field => $"{JsonSerializer.Serialize(field.Id)}:null"));
        var result = DynamicFormRuntimeFieldCanonicalizer.Canonicalize($"{{{values}}}", fields, false);

        using var document = JsonDocument.Parse(result.CanonicalValuesJson);
        foreach (var field in fields)
            Equal(JsonValueKind.Null, document.RootElement.GetProperty(field.Id).ValueKind, $"optional null {field.Type}");
    }

    private static IReadOnlyCollection<ResolvedDynamicFormRuntimeFieldDefinition> AllFields()
        => new[]
        {
            new ResolvedDynamicFormRuntimeFieldDefinition("f-short", "shortText", Key: "short_key", Required: true, AllowedChoiceCodes: new[] { "CODE_A", "CODE_B" }),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-long", "longText", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-rich", "richText", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-list", "stringList", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-number", "number", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-date", "date", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-full-date", "fullDate", Required: true),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-single", "singleSelect", Required: true, AllowedChoiceCodes: new[] { "ONE", "TWO" }),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-multi", "multiSelect", Required: true, AllowedChoiceCodes: new[] { "A", "B", "C" }),
            new ResolvedDynamicFormRuntimeFieldDefinition("f-boolean", "boolean", Required: true)
        };

    private static void Reason(string expectedReason, Action action)
    {
        var error = Throws(action);
        Equal(expectedReason, error.Reason, "validation reason");
    }

    private static DynamicFormRuntimeFieldValidationException Throws(Action action)
    {
        try
        {
            action();
        }
        catch (DynamicFormRuntimeFieldValidationException error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected DynamicFormRuntimeFieldValidationException was not thrown.");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string context)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"{context}: sequences differ.");
    }

    private static void True(bool value, string context)
    {
        if (!value)
            throw new InvalidOperationException($"{context}: expected true.");
    }

    private static void False(bool value, string context)
    {
        if (value)
            throw new InvalidOperationException($"{context}: expected false.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }
}
