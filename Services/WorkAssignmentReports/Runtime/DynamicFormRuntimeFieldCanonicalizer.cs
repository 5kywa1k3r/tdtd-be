using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

/// <summary>
/// A Dynamic Form field definition after value-source options have been resolved for the current actor.
/// Choice values are accepted only when they match an entry in <see cref="AllowedChoiceCodes"/>.
/// </summary>
public sealed record ResolvedDynamicFormRuntimeFieldDefinition(
    string Id,
    string Type,
    string? Key = null,
    bool Required = false,
    IReadOnlyCollection<string>? AllowedChoiceCodes = null);

public sealed record DynamicFormRuntimeFieldCanonicalizationResult(
    string CanonicalFieldValuesJson,
    string CanonicalValuesJson,
    bool UsesValuesEnvelope,
    IReadOnlyList<string> SanitizedRichTextFieldIds);

/// <summary>
/// Stable, storage-agnostic validation failure. The report service can map Reason and the field
/// metadata to its public AppException contract without making this helper depend on HTTP or MongoDB.
/// </summary>
public sealed class DynamicFormRuntimeFieldValidationException : InvalidOperationException
{
    public DynamicFormRuntimeFieldValidationException(
        string reason,
        string message,
        string? fieldId = null,
        string? fieldType = null,
        string? valueProperty = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        FieldId = fieldId;
        FieldType = fieldType;
        ValueProperty = valueProperty;
    }

    public string Reason { get; }
    public string? FieldId { get; }
    public string? FieldType { get; }
    public string? ValueProperty { get; }
}

public static class DynamicFormRuntimeFieldValidationReasons
{
    public const string DefinitionInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_DEFINITION_INVALID";
    public const string DefinitionDuplicate = "DYNAMIC_FORM_RUNTIME_FIELD_DEFINITION_DUPLICATE";
    public const string TypeUnsupported = "DYNAMIC_FORM_RUNTIME_FIELD_TYPE_UNSUPPORTED";
    public const string ChoiceOptionsInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_CHOICE_OPTIONS_INVALID";
    public const string ValuesJsonInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_VALUES_JSON_INVALID";
    public const string ValuesObjectRequired = "DYNAMIC_FORM_RUNTIME_FIELD_VALUES_OBJECT_REQUIRED";
    public const string PropertyDuplicate = "DYNAMIC_FORM_RUNTIME_FIELD_PROPERTY_DUPLICATE";
    public const string FieldUnknown = "DYNAMIC_FORM_RUNTIME_FIELD_UNKNOWN";
    public const string FieldValueDuplicate = "DYNAMIC_FORM_RUNTIME_FIELD_VALUE_DUPLICATE";
    public const string ValueKindInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_VALUE_KIND_INVALID";
    public const string ValueRequired = "DYNAMIC_FORM_RUNTIME_FIELD_VALUE_REQUIRED";
    public const string ChoiceCodeInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_CHOICE_CODE_INVALID";
    public const string ListItemBlank = "DYNAMIC_FORM_RUNTIME_FIELD_LIST_ITEM_BLANK";
    public const string ListItemDuplicate = "DYNAMIC_FORM_RUNTIME_FIELD_LIST_ITEM_DUPLICATE";
    public const string DateInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_DATE_INVALID";
    public const string RichTextInvalid = "DYNAMIC_FORM_RUNTIME_FIELD_RICH_TEXT_INVALID";
    public const string RichTextUnsafe = "DYNAMIC_FORM_RUNTIME_FIELD_RICH_TEXT_UNSAFE";
}

/// <summary>
/// Strict canonicalizer for the ten v1 Dynamic Form runtime field types.
/// It accepts either the persisted { "values": { ... } } envelope or a direct values object.
/// </summary>
public static class DynamicFormRuntimeFieldCanonicalizer
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "shortText",
        "longText",
        "richText",
        "stringList",
        "number",
        "date",
        "fullDate",
        "singleSelect",
        "multiSelect",
        "boolean"
    };

    private static readonly Regex FullDateRegex = new(
        @"^(\d{2})/(\d{2})/(\d{4})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MonthDateRegex = new(
        @"^(\d{2})/(\d{4})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex YearDateRegex = new(
        @"^(\d{4})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DynamicFormRuntimeFieldCanonicalizationResult Canonicalize(
        string? fieldValuesJson,
        IReadOnlyCollection<ResolvedDynamicFormRuntimeFieldDefinition> fields,
        bool validateRequiredFields)
    {
        var definitions = ResolveDefinitions(fields);
        var sourceJson = string.IsNullOrWhiteSpace(fieldValuesJson) ? "{}" : fieldValuesJson;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                sourceJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (JsonException ex)
        {
            throw Failure(
                DynamicFormRuntimeFieldValidationReasons.ValuesJsonInvalid,
                "Dynamic Form runtime field values must be valid JSON.",
                innerException: ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ValuesObjectRequired,
                    "Dynamic Form runtime field values root must be a JSON object.");
            }

            EnsureNoDuplicateProperties(root);

            var usesValuesEnvelope = root.TryGetProperty("values", out var valuesElement);
            if (!usesValuesEnvelope)
                valuesElement = root;
            else if (valuesElement.ValueKind != JsonValueKind.Object)
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ValuesObjectRequired,
                    "Dynamic Form runtime field values envelope must contain a JSON object at 'values'.",
                    valueProperty: "values");
            }

            var canonicalByFieldId = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var sanitizedRichTextFieldIds = new List<string>();
            foreach (var property in valuesElement.EnumerateObject())
            {
                if (!definitions.ByAlias.TryGetValue(property.Name, out var field))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.FieldUnknown,
                        $"Runtime value property '{property.Name}' does not belong to the resolved Dynamic Form fields.",
                        valueProperty: property.Name);
                }

                if (canonicalByFieldId.ContainsKey(field.Id))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.FieldValueDuplicate,
                        $"Runtime field '{field.Id}' was supplied more than once through id/key aliases.",
                        field,
                        property.Name);
                }

                var canonical = CanonicalizeValue(field, property.Value, out var richTextWasSanitized);
                canonicalByFieldId.Add(field.Id, canonical);
                if (richTextWasSanitized)
                    sanitizedRichTextFieldIds.Add(field.Id);
            }

            var canonicalValues = new JsonObject();
            foreach (var field in definitions.Ordered)
            {
                var hasValue = canonicalByFieldId.TryGetValue(field.Id, out var value);
                if (validateRequiredFields && field.Required && (!hasValue || IsBlank(value)))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.ValueRequired,
                        $"Runtime field '{field.Id}' is required for this transition.",
                        field);
                }

                if (hasValue)
                    canonicalValues.Add(field.Id, value);
            }

            var canonicalValuesJson = canonicalValues.ToJsonString();
            if (!usesValuesEnvelope)
            {
                return new DynamicFormRuntimeFieldCanonicalizationResult(
                    canonicalValuesJson,
                    canonicalValuesJson,
                    UsesValuesEnvelope: false,
                    sanitizedRichTextFieldIds.AsReadOnly());
            }

            var envelope = new JsonObject();
            var envelopeProperties = root
                .EnumerateObject()
                .Where(property => !string.Equals(property.Name, "values", StringComparison.Ordinal))
                .Select(property => new KeyValuePair<string, JsonNode?>(
                    property.Name,
                    CloneJsonNode(property.Value)))
                .Append(new KeyValuePair<string, JsonNode?>("values", canonicalValues))
                .OrderBy(property => property.Key, StringComparer.Ordinal);

            foreach (var property in envelopeProperties)
                envelope.Add(property.Key, property.Value);

            return new DynamicFormRuntimeFieldCanonicalizationResult(
                envelope.ToJsonString(),
                canonicalValuesJson,
                UsesValuesEnvelope: true,
                sanitizedRichTextFieldIds.AsReadOnly());
        }
    }

    private static ResolvedDefinitions ResolveDefinitions(
        IReadOnlyCollection<ResolvedDynamicFormRuntimeFieldDefinition>? fields)
    {
        if (fields is null)
        {
            throw Failure(
                DynamicFormRuntimeFieldValidationReasons.DefinitionInvalid,
                "Resolved Dynamic Form runtime field definitions are required.");
        }

        var ordered = new List<NormalizedFieldDefinition>(fields.Count);
        var byId = new Dictionary<string, NormalizedFieldDefinition>(StringComparer.Ordinal);
        var rawDefinitions = new List<(ResolvedDynamicFormRuntimeFieldDefinition Source, string Id, string Type, string? Key, IReadOnlyDictionary<string, string> ChoiceCodes)>();

        foreach (var field in fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Id) || string.IsNullOrWhiteSpace(field.Type))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.DefinitionInvalid,
                    "Every resolved Dynamic Form runtime field must have a non-blank id and canonical type.");
            }

            var id = field.Id.Trim();
            var type = field.Type.Trim();
            var key = string.IsNullOrWhiteSpace(field.Key) ? null : field.Key.Trim();
            if (!SupportedTypes.Contains(type))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.TypeUnsupported,
                    $"Runtime field '{id}' uses unsupported canonical type '{type}'.",
                    fieldId: id,
                    fieldType: type);
            }

            var choiceCodes = ResolveChoiceCodes(field, id, type);
            rawDefinitions.Add((field, id, type, key, choiceCodes));
        }

        foreach (var raw in rawDefinitions.OrderBy(field => field.Id, StringComparer.Ordinal))
        {
            var normalized = new NormalizedFieldDefinition(
                raw.Id,
                raw.Type,
                raw.Key,
                raw.Source.Required,
                raw.ChoiceCodes);
            if (!byId.TryAdd(normalized.Id, normalized))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.DefinitionDuplicate,
                    $"Resolved Dynamic Form runtime field id '{normalized.Id}' is duplicated.",
                    normalized);
            }

            ordered.Add(normalized);
        }

        var byAlias = new Dictionary<string, NormalizedFieldDefinition>(StringComparer.Ordinal);
        foreach (var field in ordered)
        {
            AddAlias(byAlias, field.Id, field);
            if (!string.IsNullOrWhiteSpace(field.Key) && !string.Equals(field.Key, field.Id, StringComparison.Ordinal))
                AddAlias(byAlias, field.Key, field);
        }

        return new ResolvedDefinitions(ordered, byAlias);
    }

    private static IReadOnlyDictionary<string, string> ResolveChoiceCodes(
        ResolvedDynamicFormRuntimeFieldDefinition field,
        string id,
        string type)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawCode in field.AllowedChoiceCodes ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawCode))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ChoiceOptionsInvalid,
                    $"Resolved choice options for runtime field '{id}' contain a blank code.",
                    fieldId: id,
                    fieldType: type);
            }

            var code = rawCode.Trim();
            if (!codes.TryAdd(code, code))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ChoiceOptionsInvalid,
                    $"Resolved choice options for runtime field '{id}' contain duplicate code '{code}'.",
                    fieldId: id,
                    fieldType: type);
            }
        }

        return codes;
    }

    private static void AddAlias(
        IDictionary<string, NormalizedFieldDefinition> aliases,
        string alias,
        NormalizedFieldDefinition field)
    {
        if (!aliases.TryAdd(alias, field))
        {
            throw Failure(
                DynamicFormRuntimeFieldValidationReasons.DefinitionDuplicate,
                $"Resolved Dynamic Form runtime field alias '{alias}' is ambiguous.",
                field);
        }
    }

    private static JsonNode? CanonicalizeValue(
        NormalizedFieldDefinition field,
        JsonElement value,
        out bool richTextWasSanitized)
    {
        richTextWasSanitized = false;
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        switch (field.Type)
        {
            case "shortText":
            case "singleSelect":
                return CanonicalizeChoiceCode(field, value);

            case "longText":
                RequireKind(field, value, JsonValueKind.String);
                return JsonValue.Create(value.GetString() ?? string.Empty);

            case "richText":
                RequireKind(field, value, JsonValueKind.String);
                try
                {
                    var sanitized = DynamicFormRuntimeRichTextSanitizer.Sanitize(value.GetString() ?? string.Empty);
                    richTextWasSanitized = sanitized.WasSanitized;
                    return JsonValue.Create(sanitized.Html);
                }
                catch (DynamicFormRuntimeRichTextSanitizationException ex)
                {
                    throw Failure(
                        ex.IsUnsafe
                            ? DynamicFormRuntimeFieldValidationReasons.RichTextUnsafe
                            : DynamicFormRuntimeFieldValidationReasons.RichTextInvalid,
                        $"Runtime rich-text field '{field.Id}' is not safe canonical HTML.",
                        field,
                        innerException: ex);
                }

            case "stringList":
                return CanonicalizeStringList(field, value, validateChoiceCodes: false);

            case "number":
                RequireKind(field, value, JsonValueKind.Number);
                if (!value.TryGetDecimal(out var number))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.ValueKindInvalid,
                        $"Runtime number field '{field.Id}' must be a finite JSON decimal number.",
                        field);
                }
                return JsonValue.Create(number);

            case "date":
            case "fullDate":
                RequireKind(field, value, JsonValueKind.String);
                var date = (value.GetString() ?? string.Empty).Trim();
                if (!IsDateValid(date, requireFullDate: field.Type == "fullDate"))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.DateInvalid,
                        $"Runtime field '{field.Id}' does not match its canonical date format.",
                        field);
                }
                return JsonValue.Create(date);

            case "multiSelect":
                return CanonicalizeStringList(field, value, validateChoiceCodes: true);

            case "boolean":
                if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    RequireKind(field, value, JsonValueKind.True, JsonValueKind.False);
                return JsonValue.Create(value.GetBoolean());

            default:
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.TypeUnsupported,
                    $"Runtime field '{field.Id}' uses unsupported canonical type '{field.Type}'.",
                    field);
        }
    }

    private static JsonNode CanonicalizeChoiceCode(NormalizedFieldDefinition field, JsonElement value)
    {
        RequireKind(field, value, JsonValueKind.String);
        var code = (value.GetString() ?? string.Empty).Trim();
        if (!field.ChoiceCodes.TryGetValue(code, out var canonicalCode))
        {
            throw Failure(
                DynamicFormRuntimeFieldValidationReasons.ChoiceCodeInvalid,
                $"Runtime field '{field.Id}' must contain a resolved choice code, never a label or arbitrary text.",
                field);
        }

        return JsonValue.Create(canonicalCode)!;
    }

    private static JsonNode CanonicalizeStringList(
        NormalizedFieldDefinition field,
        JsonElement value,
        bool validateChoiceCodes)
    {
        RequireKind(field, value, JsonValueKind.Array);
        var canonical = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ValueKindInvalid,
                    $"Runtime list field '{field.Id}' item {index} must be a JSON string.",
                    field,
                    valueProperty: index.ToString(CultureInfo.InvariantCulture));
            }

            var text = (item.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ListItemBlank,
                    $"Runtime list field '{field.Id}' item {index} cannot be blank.",
                    field,
                    valueProperty: index.ToString(CultureInfo.InvariantCulture));
            }

            if (validateChoiceCodes)
            {
                if (!field.ChoiceCodes.TryGetValue(text, out var canonicalCode))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.ChoiceCodeInvalid,
                        $"Runtime multi-select field '{field.Id}' item {index} is not a resolved choice code.",
                        field,
                        valueProperty: index.ToString(CultureInfo.InvariantCulture));
                }
                text = canonicalCode;
            }

            if (!seen.Add(text))
            {
                throw Failure(
                    DynamicFormRuntimeFieldValidationReasons.ListItemDuplicate,
                    $"Runtime list field '{field.Id}' contains duplicate item '{text}'.",
                    field,
                    valueProperty: index.ToString(CultureInfo.InvariantCulture));
            }

            canonical.Add(text);
            index++;
        }

        return canonical;
    }

    private static void RequireKind(
        NormalizedFieldDefinition field,
        JsonElement value,
        params JsonValueKind[] expectedKinds)
    {
        if (expectedKinds.Contains(value.ValueKind))
            return;

        throw Failure(
            DynamicFormRuntimeFieldValidationReasons.ValueKindInvalid,
            $"Runtime field '{field.Id}' of type '{field.Type}' has JSON kind '{value.ValueKind}', expected {string.Join(" or ", expectedKinds)}.",
            field);
    }

    private static bool IsBlank(JsonNode? value)
    {
        if (value is null)
            return true;
        if (value is JsonArray array)
            return array.Count == 0;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text);
        return false;
    }

    private static bool IsDateValid(string value, bool requireFullDate)
    {
        var full = FullDateRegex.Match(value);
        if (full.Success)
        {
            var day = int.Parse(full.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(full.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(full.Groups[3].Value, CultureInfo.InvariantCulture);
            return year is >= 1 and <= 9999 &&
                   month is >= 1 and <= 12 &&
                   day >= 1 &&
                   day <= DateTime.DaysInMonth(year, month);
        }

        if (requireFullDate)
            return false;

        var monthOnly = MonthDateRegex.Match(value);
        if (monthOnly.Success)
        {
            var month = int.Parse(monthOnly.Groups[1].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(monthOnly.Groups[2].Value, CultureInfo.InvariantCulture);
            return year is >= 1 and <= 9999 && month is >= 1 and <= 12;
        }

        var yearOnly = YearDateRegex.Match(value);
        return yearOnly.Success &&
               int.Parse(yearOnly.Groups[1].Value, CultureInfo.InvariantCulture) is >= 1 and <= 9999;
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path = "$")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Failure(
                        DynamicFormRuntimeFieldValidationReasons.PropertyDuplicate,
                        $"Dynamic Form runtime JSON contains duplicate property '{property.Name}' at '{path}'.",
                        valueProperty: property.Name);
                }

                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    private static JsonNode? CloneJsonNode(JsonElement element)
        => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : JsonNode.Parse(element.GetRawText());

    private static DynamicFormRuntimeFieldValidationException Failure(
        string reason,
        string message,
        NormalizedFieldDefinition? field = null,
        string? valueProperty = null,
        Exception? innerException = null,
        string? fieldId = null,
        string? fieldType = null)
        => new(
            reason,
            message,
            fieldId ?? field?.Id,
            fieldType ?? field?.Type,
            valueProperty,
            innerException);

    private sealed record NormalizedFieldDefinition(
        string Id,
        string Type,
        string? Key,
        bool Required,
        IReadOnlyDictionary<string, string> ChoiceCodes);

    private sealed record ResolvedDefinitions(
        IReadOnlyList<NormalizedFieldDefinition> Ordered,
        IReadOnlyDictionary<string, NormalizedFieldDefinition> ByAlias);
}

public sealed record DynamicFormRuntimeRichTextSanitizationResult(string Html, bool WasSanitized);

public sealed class DynamicFormRuntimeRichTextSanitizationException : InvalidOperationException
{
    public DynamicFormRuntimeRichTextSanitizationException(string message, bool isUnsafe, Exception? innerException = null)
        : base(message, innerException)
    {
        IsUnsafe = isUnsafe;
    }

    public bool IsUnsafe { get; }
}

/// <summary>
/// Conservative allow-list sanitizer for Lexical HTML. Dangerous containers and URI schemes are
/// rejected; event/unknown attributes, comments, unsupported styles and benign unknown tags are removed.
/// </summary>
public static class DynamicFormRuntimeRichTextSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "a", "b", "blockquote", "br", "div", "em", "h1", "h2", "h3", "i", "li", "ol", "p",
        "span", "strong", "table", "tbody", "td", "th", "thead", "tr", "u", "ul"
    };

    private static readonly HashSet<string> DangerousTags = new(StringComparer.Ordinal)
    {
        "base", "button", "embed", "form", "iframe", "input", "link", "math", "meta", "object",
        "option", "script", "select", "style", "svg", "textarea"
    };

    private static readonly string[] AllowedStyleOrder =
    {
        "margin-left", "padding-left", "text-align", "text-decoration", "text-indent"
    };

    private static readonly Regex DangerousTagRegex = new(
        @"<\s*/?\s*(?:base|button|embed|form|iframe|input|link|math|meta|object|option|script|select|style|svg|textarea)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex VoidTagRegex = new(
        @"<(br|hr|img)(?<attributes>\s[^<>]*?)?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UnsafeCssRegex = new(
        @"(?:expression\s*\(|url\s*\(|javascript\s*:|vbscript\s*:|data\s*:)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SafeLengthRegex = new(
        @"^-?\d{1,3}(?:\.\d{1,2})?(?:px|pt|em|rem|%)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static DynamicFormRuntimeRichTextSanitizationResult Sanitize(string? value)
    {
        var source = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
            return new DynamicFormRuntimeRichTextSanitizationResult(string.Empty, source.Length > 0);

        if (!source.Contains('<'))
        {
            var plain = source.Trim();
            return new DynamicFormRuntimeRichTextSanitizationResult(plain, !string.Equals(plain, source, StringComparison.Ordinal));
        }

        if (DangerousTagRegex.IsMatch(source))
            throw new DynamicFormRuntimeRichTextSanitizationException("Rich text contains a dangerous HTML element.", isUnsafe: true);

        var normalized = Regex.Replace(source, "&nbsp;", "&#160;", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = VoidTagRegex.Replace(normalized, match =>
        {
            var tag = match.Groups[1].Value.ToLowerInvariant();
            var attributes = match.Groups["attributes"].Value;
            return match.Value.EndsWith("/>", StringComparison.Ordinal)
                ? match.Value
                : $"<{tag}{attributes} />";
        });

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader($"<root>{normalized}</root>"),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreComments = false,
                    MaxCharactersInDocument = 1_000_000,
                    XmlResolver = null
                });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new DynamicFormRuntimeRichTextSanitizationException(
                "Rich text must be a well-formed HTML fragment.",
                isUnsafe: false,
                ex);
        }

        var context = new SanitizationContext(!string.Equals(source, normalized, StringComparison.Ordinal));
        var sanitizedNodes = document.Root!
            .Nodes()
            .SelectMany(node => SanitizeNode(node, context))
            .ToList();
        var html = string.Concat(sanitizedNodes.Select(node => node.ToString(SaveOptions.DisableFormatting))).Trim();
        if (string.IsNullOrWhiteSpace(ExtractText(sanitizedNodes)))
            html = string.Empty;

        if (!string.Equals(html, source, StringComparison.Ordinal))
            context.WasSanitized = true;

        return new DynamicFormRuntimeRichTextSanitizationResult(html, context.WasSanitized);
    }

    private static IEnumerable<XNode> SanitizeNode(XNode node, SanitizationContext context)
    {
        if (node is XText text)
            return new XNode[] { new XText(text.Value) };
        if (node is XComment or XProcessingInstruction)
        {
            context.WasSanitized = true;
            return Array.Empty<XNode>();
        }
        if (node is not XElement element)
        {
            context.WasSanitized = true;
            return Array.Empty<XNode>();
        }

        var tag = element.Name.LocalName.ToLowerInvariant();
        if (!string.IsNullOrEmpty(element.Name.NamespaceName))
        {
            throw new DynamicFormRuntimeRichTextSanitizationException(
                "Rich text cannot contain namespaced HTML elements.",
                isUnsafe: true);
        }
        if (DangerousTags.Contains(tag))
        {
            throw new DynamicFormRuntimeRichTextSanitizationException(
                $"Rich text contains dangerous element '{tag}'.",
                isUnsafe: true);
        }

        var children = element.Nodes().SelectMany(child => SanitizeNode(child, context)).ToList();
        if (!AllowedTags.Contains(tag))
        {
            context.WasSanitized = true;
            return children;
        }

        var clean = new XElement(tag);
        var attributes = element.Attributes().ToList();
        if (attributes.Any(attribute => attribute.IsNamespaceDeclaration || attribute.Name.NamespaceName.Length > 0))
        {
            throw new DynamicFormRuntimeRichTextSanitizationException(
                "Rich text cannot contain namespaced attributes.",
                isUnsafe: true);
        }

        string? href = null;
        string? colspan = null;
        string? rowspan = null;
        string? style = null;
        foreach (var attribute in attributes)
        {
            var name = attribute.Name.LocalName.ToLowerInvariant();
            if (name.StartsWith("on", StringComparison.Ordinal))
            {
                context.WasSanitized = true;
                continue;
            }

            if (name == "href" && tag == "a")
            {
                var candidate = attribute.Value.Trim();
                if (IsUnsafeUri(candidate))
                {
                    throw new DynamicFormRuntimeRichTextSanitizationException(
                        "Rich text contains an unsafe link URI.",
                        isUnsafe: true);
                }
                if (Regex.IsMatch(candidate, @"^(?:https?:|mailto:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    href = candidate;
                else
                    context.WasSanitized = true;
                continue;
            }

            if (name is "colspan" or "rowspan" &&
                int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var span) &&
                span is >= 1 and <= 50)
            {
                if (name == "colspan")
                    colspan = span.ToString(CultureInfo.InvariantCulture);
                else
                    rowspan = span.ToString(CultureInfo.InvariantCulture);
                continue;
            }

            if (name == "style")
            {
                style = SanitizeStyle(attribute.Value, context);
                continue;
            }

            context.WasSanitized = true;
        }

        if (href is not null)
            clean.SetAttributeValue("href", href);
        if (colspan is not null)
            clean.SetAttributeValue("colspan", colspan);
        if (rowspan is not null)
            clean.SetAttributeValue("rowspan", rowspan);
        if (style is not null)
            clean.SetAttributeValue("style", style);
        clean.Add(children);
        return new XNode[] { clean };
    }

    private static string? SanitizeStyle(string value, SanitizationContext context)
    {
        if (UnsafeCssRegex.IsMatch(value))
        {
            throw new DynamicFormRuntimeRichTextSanitizationException(
                "Rich text contains unsafe inline CSS.",
                isUnsafe: true);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                context.WasSanitized = true;
                continue;
            }

            var property = declaration[..separator].Trim().ToLowerInvariant();
            var rawValue = declaration[(separator + 1)..].Trim().ToLowerInvariant();
            var safeValue = property switch
            {
                "text-align" when rawValue is "left" or "right" or "center" or "justify" or "start" or "end" => rawValue,
                "text-decoration" when rawValue is "underline" or "none" => rawValue,
                "margin-left" or "padding-left" or "text-indent" when SafeLengthRegex.IsMatch(rawValue) => rawValue,
                _ => null
            };

            if (safeValue is null)
            {
                context.WasSanitized = true;
                continue;
            }
            if (!values.TryAdd(property, safeValue))
            {
                throw new DynamicFormRuntimeRichTextSanitizationException(
                    $"Rich text contains duplicate inline style '{property}'.",
                    isUnsafe: true);
            }
        }

        var canonical = string.Join(
            "; ",
            AllowedStyleOrder
                .Where(values.ContainsKey)
                .Select(property => $"{property}: {values[property]}"));
        if (!string.Equals(canonical, value.Trim().TrimEnd(';'), StringComparison.Ordinal))
            context.WasSanitized = true;
        return string.IsNullOrWhiteSpace(canonical) ? null : canonical;
    }

    private static bool IsUnsafeUri(string value)
    {
        var compact = Regex.Replace(value, @"\s+", string.Empty);
        return Regex.IsMatch(
            compact,
            @"^(?:javascript|vbscript|data):",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ExtractText(IEnumerable<XNode> nodes)
        => string.Concat(nodes.Select(ExtractText)).Replace('\u00a0', ' ').Trim();

    private static string ExtractText(XNode node)
        => node switch
        {
            XText text => text.Value,
            XElement element => string.Concat(element.Nodes().Select(ExtractText)),
            _ => string.Empty
        };

    private sealed class SanitizationContext
    {
        public SanitizationContext(bool wasSanitized)
        {
            WasSanitized = wasSanitized;
        }

        public bool WasSanitized { get; set; }
    }
}
