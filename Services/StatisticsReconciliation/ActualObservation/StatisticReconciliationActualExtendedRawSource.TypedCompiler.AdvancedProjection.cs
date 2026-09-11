using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualExtendedRawSourceTypedCompiler
{
    private static ImmutableDictionary<string,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
        RequireAdvancedProjectionSet(
            StatisticReconciliationActualSummaryPlanBinding plan,
            ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
                projections)
    {
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == "ADVANCED")
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (descriptors.Length == 0 || projections.IsDefault ||
            projections.Length != descriptors.Length ||
            !projections.SequenceEqual(projections.OrderBy(value =>
                value.IdentitySha256, StringComparer.Ordinal)))
            throw AdvancedProjectionInvalid();
        var result = ImmutableDictionary.CreateBuilder<string,
            StatisticReconciliationActualExtendedAdvancedDescriptorProjection>(
            StringComparer.Ordinal);
        for (var index = 0; index < descriptors.Length; index++)
        {
            var descriptor = descriptors[index];
            var projection = projections[index];
            if (projection.DescriptorSemanticSha256 !=
                    descriptor.SemanticSha256 ||
                projection.IdentitySha256 != descriptor.IdentitySha256 ||
                projection.FieldId != descriptor.FieldId ||
                string.IsNullOrWhiteSpace(projection.FieldDependencyPin) ||
                projection.FieldDependencyPin !=
                    projection.FieldDependencyPin.Trim())
                throw AdvancedProjectionInvalid();
            var options = projection.Options;
            if (options.IsDefault ||
                !options.SequenceEqual(options.OrderBy(value =>
                    value.Code, StringComparer.Ordinal)) ||
                options.Select(value => value.Code)
                    .Distinct(StringComparer.Ordinal).Count() != options.Length)
                throw AdvancedProjectionInvalid();
            foreach (var option in options)
            {
                var code = StatisticReconciliationActualCanonical.Required(
                    option.Code, "EXTENDED_ADVANCED_OPTION_CODE", 512);
                var label = StatisticReconciliationActualCanonical.Required(
                    option.Label, "EXTENDED_ADVANCED_OPTION_LABEL", 512);
                if (option.OptionSemanticSha256 !=
                    StatisticReconciliationActualCanonical.Hash(
                        "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_V1",
                        code,
                        label))
                    throw AdvancedProjectionInvalid();
            }
            var optionManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_MANIFEST_V1",
                    options.Select(value => value.OptionSemanticSha256));
            if (optionManifest != projection.OptionManifestSha256)
                throw AdvancedProjectionInvalid();
            var operation = ProjectionOperation(
                descriptor.MetricId,
                projection.FieldKey);
            var actualType = ProjectionActualType(
                projection.RawFieldType);
            RequireAdvancedProjectionDescriptorShape(
                descriptor,
                projection.RawFieldType,
                operation);
            var semantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTION_V1",
                descriptor.SemanticSha256,
                descriptor.IdentitySha256,
                projection.FieldId,
                projection.FieldKey,
                projection.RawFieldType,
                actualType,
                operation,
                optionManifest,
                projection.FieldDependencyPin);
            if (semantic != projection.ProjectionSemanticSha256 ||
                !result.TryAdd(descriptor.IdentitySha256, projection))
                throw AdvancedProjectionInvalid();
        }
        return result.ToImmutable();
    }

    private static StatisticReconciliationActualRawPayloadEnvelope
        ToAdvancedProjectedRawSource(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            StatisticReconciliationActualExtendedAdvancedDescriptorProjection
                projection,
            string schemaOptionBindingSha256,
            StatisticReconciliationActualExtendedRawSourceEnvelope envelope)
    {
        var source = JsonNode.Parse(envelope.CanonicalPayloadJson)
            ?? throw AdvancedProjectionInvalid();
        var root = new JsonObject();
        if (TryAdvancedFieldNode(source, projection, out var raw) &&
            TryProjectAdvancedValue(
                raw,
                projection,
                out var projected))
            SetPointer(root, descriptor.JsonPointer, projected);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            root.ToJsonString(),
            "EXTENDED_ADVANCED_PROJECTED_SOURCE");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        var payloadSha =
            StatisticReconciliationActualJson.RawSha256(canonical);
        var stable =
            StatisticReconciliationActualSourceMembershipAdapter
                .ActualStableIdentitySha256(
                    envelope.WorkId,
                    envelope.WorkAssignmentId,
                    envelope.ReportId);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_TYPED_SOURCE_V4",
            stable,
            descriptor.SemanticSha256,
            projection.ProjectionSemanticSha256,
            schemaOptionBindingSha256,
            envelope.EnvelopeSemanticSha256,
            payloadSha);
        return new(
            stable,
            envelope.WorkId,
            envelope.WorkAssignmentId,
            envelope.ReportId,
            envelope.PayloadDocumentId,
            envelope.PayloadRevision,
            envelope.PayloadOwnerSha256,
            payloadSha,
            canonical,
            semantic);
    }

    private static bool TryAdvancedFieldNode(
        JsonNode source,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection,
        out JsonNode? value)
    {
        value = null;
        if (source is not JsonObject root ||
            root["fieldValues"] is not JsonObject fieldValues)
            return false;
        var values = fieldValues["values"] is JsonObject nested
            ? nested
            : fieldValues;
        if (values.TryGetPropertyValue(projection.FieldId, out value))
            return true;
        return projection.FieldKey != projection.FieldId &&
            values.TryGetPropertyValue(projection.FieldKey, out value);
    }

    private static bool TryProjectAdvancedValue(
        JsonNode? value,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection,
        out JsonNode? projected)
    {
        projected = null;
        if (value is null)
            return false;
        using var document = StatisticReconciliationActualJson.ParseStrict(
            value.ToJsonString(),
            "EXTENDED_ADVANCED_FIELD_VALUE");
        var raw = document.RootElement;
        if (AdvancedBlank(raw))
            return false;
        switch (projection.RawFieldType)
        {
            case "number":
                if (!AdvancedDecimal(raw, out var number))
                    return false;
                projected = JsonValue.Create(number);
                return true;
            case "date":
                return TryAdvancedDate(raw, fullDate: false, out projected);
            case "dateTime":
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .AdvancedDateTimeCanonicalUnprovable);
            case "fullDate":
                return TryAdvancedDate(raw, fullDate: true, out projected);
            case "boolean":
                if (!AdvancedBoolean(raw, out var boolean))
                    return false;
                projected = JsonValue.Create(boolean);
                return true;
            case "singleSelect":
            {
                var code = AdvancedText(raw)?.Trim();
                if (string.IsNullOrWhiteSpace(code))
                    return false;
                projected = JsonValue.Create(
                    AdvancedOptionLabel(projection, code));
                return true;
            }
            case "multiSelect":
            case "stringList":
            case "richText":
            {
                var values = new JsonArray();
                IEnumerable<JsonElement> items =
                    raw.ValueKind == JsonValueKind.Array
                        ? raw.EnumerateArray()
                        : new[] { raw };
                foreach (var item in items)
                {
                    var code = AdvancedText(item)?.Trim();
                    if (string.IsNullOrWhiteSpace(code))
                        continue;
                    values.Add(AdvancedOptionLabel(projection, code));
                }
                if (values.Count == 0)
                    return false;
                projected = values;
                return true;
            }

            case "shortText":
            case "longText":
            {
                var text = AdvancedText(raw);
                if (string.IsNullOrWhiteSpace(text))
                    return false;
                projected = JsonValue.Create(text);
                return true;
            }
            default:
                throw AdvancedProjectionInvalid();
        }
    }

    private static bool AdvancedBlank(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or
            JsonValueKind.Undefined)
            return true;
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString());
        return value.ValueKind == JsonValueKind.Array &&
            !value.EnumerateArray().Any(item => !AdvancedBlank(item));
    }

    private static string? AdvancedText(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetDecimal(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static bool AdvancedDecimal(
        JsonElement value,
        out decimal result)
    {
        result = default;
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out result))
            return true;
        return value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out result);
    }

    private static bool AdvancedBoolean(
        JsonElement value,
        out bool result)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            result = true;
            return true;
        }
        if (value.ValueKind == JsonValueKind.False)
        {
            result = false;
            return true;
        }
        result = false;
        return value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out result);
    }

    private static bool TryAdvancedDate(
        JsonElement value,
        bool fullDate,
        out JsonNode? projected)
    {
        projected = null;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw) || raw != raw.Trim())
            return false;
        var formats = fullDate
            ? new[] { "dd/MM/yyyy" }
            : new[] { "dd/MM/yyyy", "MM/yyyy", "yyyy" };
        if (!formats.Any(format => DateTime.TryParseExact(
                raw,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _)))
            return false;
        projected = JsonValue.Create(raw);
        return true;
    }

    private static string AdvancedOptionLabel(
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection,
        string code)
        => projection.Options.FirstOrDefault(value =>
               value.Code == code)?.Label ?? code;

    private static string ProjectionOperation(
        string metricId,
        string fieldKey)
    {
        fieldKey = StatisticReconciliationActualCanonical.Required(
            fieldKey,
            "EXTENDED_ADVANCED_PROJECTION_FIELD_KEY",
            512);
        var prefix = fieldKey + ":";
        if (!metricId.StartsWith(prefix, StringComparison.Ordinal))
            throw AdvancedProjectionInvalid();
        return StatisticReconciliationActualCanonical.Required(
            metricId[prefix.Length..],
            "EXTENDED_ADVANCED_PROJECTION_OPERATION",
            128);
    }

    private static string ProjectionActualType(string rawType)
        => rawType switch
        {
            "number" => "NUMBER",
            "date" or "dateTime" or "fullDate" => "DATE",
            "boolean" => "BOOLEAN",
            "singleSelect" or "multiSelect" or "stringList" or
                "richText" => "CHOICE",
            "shortText" or "longText" => "TEXT",
            _ => throw AdvancedProjectionInvalid()
        };

    private static void RequireAdvancedProjectionDescriptorShape(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string rawType,
        string operation)
    {
        var requiredOperation = operation switch
        {
            "COUNT" => "COUNT",
            "SUM" => "SUM",
            "MIN" => "MIN",
            "MAX" => "MAX",
            "MEAN" => "MEAN",
            "MIN_DATE" => "MIN",
            "MAX_DATE" => "MAX",
            "TRUE_COUNT" or "FALSE_COUNT" or "BUCKET_COUNT" or
                "JOIN" => "VALUES",
            _ => throw AdvancedProjectionInvalid()
        };
        var shape = rawType switch
        {
            "number" => descriptor.ValueType == "NUMBER" &&
                !descriptor.ExpandArray,
            "date" or "dateTime" => descriptor.ValueType == "DATE" &&
                !descriptor.ExpandArray,
            "fullDate" => descriptor.ValueType == "FULL_DATE" &&
                !descriptor.ExpandArray,
            "boolean" => descriptor.ValueType == "BOOLEAN" &&
                !descriptor.ExpandArray,
            "singleSelect" =>
                (descriptor.ValueType is "ENUM" or "TEXT") &&
                !descriptor.ExpandArray,
            "multiSelect" =>
                descriptor.ValueType == "ENUM" && descriptor.ExpandArray ||
                descriptor.ValueType == "STRING_LIST" &&
                !descriptor.ExpandArray,
            "stringList" or "richText" =>
                descriptor.ValueType == "STRING_LIST" &&
                !descriptor.ExpandArray,
            "shortText" or "longText" =>
                descriptor.ValueType == "TEXT" &&
                !descriptor.ExpandArray,
            _ => false
        };
        if (!shape || !descriptor.Operations.Contains(
                requiredOperation,
                StringComparer.Ordinal))
            throw AdvancedProjectionInvalid();
    }

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        AdvancedProjectionInvalid()
        => Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .AdvancedDescriptorMappingInvalid);
}
