using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualMongoExtendedRawSourceCollectReader
{
    private async Task<
        StatisticReconciliationActualExtendedAdvancedSchemaOptionBinding>
        ReadAdvancedSchemaOptionBindingAsync(
            ActualAdvancedOwnerBoundary boundary,
            WorkAssignmentAdvancedSummaryConfigPayload payload,
            IReadOnlyList<string> dependencyPins,
            StatisticReconciliationActualSummaryPlanBinding plan,
            CancellationToken cancellationToken)
    {
        var templates = await context.DynamicFormTemplates
            .Find(value =>
                value.Id == boundary.DynamicFormTemplateId &&
                !value.IsDeleted)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (templates.Count != 1 || !templates[0].IsActive ||
            !templates[0].IsPublished)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedSelectorUnavailable,
                StatisticReconciliationActualExtendedRawSourceFields
                    .AdvancedLockedConfig);

        try
        {
            var template = templates[0];
            var published = DynamicFormPublishedSchemaSnapshotBuilder
                .ValidateAgainstTemplate(template);
            var sections = DynamicFormSectionSnapshotBuilder.Build(template);
            if (!sections.TryGetSection(boundary.SectionId, out var section) ||
                section.DynamicFormTemplateId != template.Id ||
                section.SectionId != boundary.SectionId)
                throw AdvancedSchemaInvalid();
            if (payload.Sections is null || payload.Sections.Count != 1 ||
                payload.Sections[0].SectionId != boundary.SectionId ||
                payload.Sections[0].Targets is null ||
                payload.Sections[0].Targets!.Count == 0)
                throw AdvancedSchemaInvalid();

            using var fieldsDocument =
                StatisticReconciliationActualJson.ParseStrict(
                    section.FieldsJson,
                    "EXTENDED_ADVANCED_SECTION_FIELDS");
            if (fieldsDocument.RootElement.ValueKind != JsonValueKind.Array)
                throw AdvancedSchemaInvalid();
            var fields = new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
            foreach (var field in fieldsDocument.RootElement.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object)
                    throw AdvancedSchemaInvalid();
                var fieldId = AdvancedRequiredString(field, "id");
                if (!fields.TryAdd(fieldId, field.Clone()))
                    throw AdvancedSchemaInvalid();
            }

            var targets = new Dictionary<string,
                WorkAssignmentAdvancedSummaryTargetPayload>(
                StringComparer.Ordinal);
            foreach (var target in payload.Sections[0].Targets!)
            {
                var fieldId = AdvancedRequired(target.FieldId);
                if (!targets.TryAdd(fieldId, target))
                    throw AdvancedSchemaInvalid();
            }

            var schemaSha = StatisticReconciliationActualCanonical.Sha256(
                published.Sha256,
                "EXTENDED_ADVANCED_PUBLISHED_SCHEMA_SHA");
            var sectionSha = StatisticReconciliationActualCanonical.Sha256(
                section.ContentHash,
                "EXTENDED_ADVANCED_SECTION_SHA");
            var schemaPin =
                $"DYNAMIC_FORM_SCHEMA:{template.Id}:{template.VersionNo}:{schemaSha}";
            var sectionPin =
                $"DYNAMIC_FORM_SECTION:{template.Id}:{section.SectionId}:{sectionSha}";
            var expectedPins = new List<string> { schemaPin, sectionPin };
            var fieldBindings = new Dictionary<string, AdvancedFieldBinding>(
                StringComparer.Ordinal);
            foreach (var target in targets.Values.OrderBy(
                         value => value.FieldId,
                         StringComparer.Ordinal))
            {
                var fieldId = AdvancedRequired(target.FieldId);
                if (!fields.TryGetValue(fieldId, out var field))
                    throw AdvancedSchemaInvalid();
                var fieldKey = AdvancedOptionalString(field, "key") ?? fieldId;
                var rawType = AdvancedRawFieldType(
                    AdvancedRequiredString(field, "type"));
                var actualType = AdvancedActualType(rawType);
                if (AdvancedRequired(target.DataType) != actualType)
                    throw AdvancedSchemaInvalid();
                var operation = AdvancedRequired(target.Operation);
                var fieldPin =
                    $"DYNAMIC_FORM_FIELD:{template.Id}:{section.SectionId}:{fieldId}:" +
                    $"{actualType}:" +
                    StatConfigCanonicalJson.HashUtf8(
                        StatConfigCanonicalJson.CanonicalizeElement(field));
                expectedPins.Add(fieldPin);
                var options = AdvancedOptions(field);
                var optionManifest =
                    StatisticReconciliationActualCanonical.HashSequence(
                        "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_MANIFEST_V1",
                        options.Select(value => value.OptionSemanticSha256));
                fieldBindings.Add(fieldId, new(
                    fieldId,
                    fieldKey,
                    rawType,
                    actualType,
                    operation,
                    options,
                    optionManifest,
                    fieldPin));
            }

            var canonicalPins = expectedPins
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!canonicalPins.SequenceEqual(
                    dependencyPins,
                    StringComparer.Ordinal))
                throw AdvancedSchemaInvalid();
            var descriptors = plan.IdentityDescriptors
                .Where(value => value.Family == "ADVANCED")
                .OrderBy(value => value.IdentitySha256,
                    StringComparer.Ordinal)
                .ToImmutableArray();
            if (descriptors.Length == 0 ||
                descriptors.Any(value => value.FieldId is null) ||
                targets.Keys.Any(fieldId => descriptors.All(value =>
                    value.FieldId != fieldId)))
                throw AdvancedSchemaInvalid();
            var projections = ImmutableArray.CreateBuilder<
                StatisticReconciliationActualExtendedAdvancedDescriptorProjection>(
                descriptors.Length);
            foreach (var descriptor in descriptors)
            {
                if (!fieldBindings.TryGetValue(
                        descriptor.FieldId!, out var field))
                    throw AdvancedSchemaInvalid();
                RequireAdvancedProjectionShape(descriptor, field);
                var semantic = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTION_V1",
                    descriptor.SemanticSha256,
                    descriptor.IdentitySha256,
                    field.FieldId,
                    field.FieldKey,
                    field.RawFieldType,
                    field.ActualType,
                    field.Operation,
                    field.OptionManifestSha256,
                    field.FieldDependencyPin);
                projections.Add(new(
                    descriptor.SemanticSha256,
                    descriptor.IdentitySha256,
                    field.FieldId,
                    field.FieldKey,
                    field.RawFieldType,
                    field.Options,
                    field.OptionManifestSha256,
                    field.FieldDependencyPin,
                    semantic));
            }
            var exactProjections = projections.MoveToImmutable();
            var projectionManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTION_MANIFEST_V1",
                    exactProjections.Select(value =>
                        value.ProjectionSemanticSha256));
            var pinManifest =
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_EXTENDED_ADVANCED_SCHEMA_OPTION_PINS_V1",
                    canonicalPins);
            var bindingSha = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_ADVANCED_SCHEMA_OPTION_BINDING_V1",
                template.Id,
                StatisticReconciliationActualCanonical.Integer(
                    template.VersionNo),
                schemaSha,
                section.SectionId,
                sectionSha,
                schemaPin,
                sectionPin,
                pinManifest,
                projectionManifest,
                StatisticReconciliationActualCanonical.Integer(
                    exactProjections.Length));
            return new(
                schemaSha,
                sectionSha,
                exactProjections,
                projectionManifest,
                bindingSha);
        }
        catch (StatisticReconciliationActualExtendedRawSourceIncompleteException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
                   InvalidOperationException or ArgumentException or
                   NotSupportedException or FormatException or AppException)
        {
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedDescriptorMappingInvalid,
                StatisticReconciliationActualExtendedRawSourceFields
                    .AdvancedLockedConfig);
        }
    }

    private static ImmutableArray<
        StatisticReconciliationActualExtendedAdvancedOptionProjection>
        AdvancedOptions(JsonElement field)
    {
        if (!field.TryGetProperty("options", out var options) ||
            options.ValueKind == JsonValueKind.Null)
            return ImmutableArray<
                StatisticReconciliationActualExtendedAdvancedOptionProjection>
                .Empty;
        if (options.ValueKind != JsonValueKind.Array)
            throw AdvancedSchemaInvalid();
        var byCode = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object ||
                !option.TryGetProperty("code", out var codeValue) ||
                codeValue.ValueKind != JsonValueKind.String)
                throw AdvancedSchemaInvalid();
            var code = codeValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(code))
                continue;
            code = AdvancedRequired(code);
            if (!byCode.ContainsKey(code))
                byCode.Add(code, AdvancedOptionLabel(option, code));
        }
        return byCode.Select(value => new
            StatisticReconciliationActualExtendedAdvancedOptionProjection(
                value.Key,
                value.Value,
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_V1",
                    value.Key,
                    value.Value)))
            .ToImmutableArray();
    }

    private static string AdvancedOptionLabel(
        JsonElement option,
        string code)
    {
        if (!option.TryGetProperty("label", out var label) ||
            label.ValueKind == JsonValueKind.Null)
            return code;
        if (label.ValueKind != JsonValueKind.String)
            throw AdvancedSchemaInvalid();
        var value = label.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? code :
            AdvancedRequired(value);
    }

    private static void RequireAdvancedProjectionShape(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        AdvancedFieldBinding field)
    {
        if (descriptor.FieldId != field.FieldId ||
            descriptor.MetricId != $"{field.FieldKey}:{field.Operation}")
            throw AdvancedSchemaInvalid();
        var requiredOperation = field.Operation switch
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
            _ => throw AdvancedSchemaInvalid()
        };
        var shape = field.RawFieldType switch
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
            throw AdvancedSchemaInvalid();
    }

    private static string AdvancedRawFieldType(string value)
        => value switch
        {
            "number" or "date" or "dateTime" or "fullDate" or
                "boolean" or "singleSelect" or "multiSelect" or
                "stringList" or "shortText" or "longText" or
                "richText" => value,
            _ => throw AdvancedSchemaInvalid()
        };

    private static string AdvancedActualType(string rawType)
        => rawType switch
        {
            "number" => "NUMBER",
            "date" or "dateTime" or "fullDate" => "DATE",
            "boolean" => "BOOLEAN",
            "singleSelect" or "multiSelect" or "stringList" or
                "richText" => "CHOICE",
            "shortText" or "longText" => "TEXT",
            _ => throw AdvancedSchemaInvalid()
        };

    private static string AdvancedRequiredString(
        JsonElement owner,
        string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw AdvancedSchemaInvalid();
        return AdvancedRequired(value.GetString());
    }

    private static string? AdvancedOptionalString(
        JsonElement owner,
        string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw AdvancedSchemaInvalid();
        var result = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(result) ? null :
            AdvancedRequired(result);
    }

    private static string AdvancedRequired(string? value)
        => StatisticReconciliationActualCanonical.Required(
            value,
            "EXTENDED_ADVANCED_SCHEMA_VALUE",
            512);

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        AdvancedSchemaInvalid()
        => Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .AdvancedDescriptorMappingInvalid,
            StatisticReconciliationActualExtendedRawSourceFields
                .AdvancedLockedConfig);

    private sealed record AdvancedFieldBinding(
        string FieldId,
        string FieldKey,
        string RawFieldType,
        string ActualType,
        string Operation,
        ImmutableArray<StatisticReconciliationActualExtendedAdvancedOptionProjection>
            Options,
        string OptionManifestSha256,
        string FieldDependencyPin);
}
