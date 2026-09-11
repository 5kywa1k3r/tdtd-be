using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Common.Errors;

namespace tdtd_be.DTOs.DynamicForms;

internal sealed record DynamicFormLegacySchemaJson(
    string? SectionsJson,
    string? FieldsJson,
    string? ExcelBlockJson,
    string? BlocksJson);

internal static class DynamicFormSchemaAdapter
{
    internal const string AmbiguousReason = "DYNAMIC_FORM_SCHEMA_AMBIGUOUS";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static DynamicFormLegacySchemaJson ResolveInput(
        DynamicFormSchemaDto? schema,
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        string? blocksJson)
    {
        if (schema is null)
            return new DynamicFormLegacySchemaJson(sectionsJson, fieldsJson, excelBlockJson, blocksJson);

        var suppliedLegacyProperties = new List<string>(capacity: 4);
        if (sectionsJson is not null)
            suppliedLegacyProperties.Add("sectionsJson");
        if (fieldsJson is not null)
            suppliedLegacyProperties.Add("fieldsJson");
        if (excelBlockJson is not null)
            suppliedLegacyProperties.Add("excelBlockJson");
        if (blocksJson is not null)
            suppliedLegacyProperties.Add("blocksJson");

        if (suppliedLegacyProperties.Count > 0)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    reason = AmbiguousReason,
                    legacyProperties = suppliedLegacyProperties.ToArray()
                });
        }

        return ToLegacy(schema);
    }

    internal static DynamicFormLegacySchemaJson ToLegacy(DynamicFormSchemaDto schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var sections = schema.Sections ?? [];
        var fields = schema.Fields ?? [];
        var blocks = schema.Blocks ?? [];

        return new DynamicFormLegacySchemaJson(
            JsonSerializer.Serialize(sections, JsonOptions),
            JsonSerializer.Serialize(fields, JsonOptions),
            blocks.Count == 0 ? null : JsonSerializer.Serialize(blocks[0], JsonOptions),
            JsonSerializer.Serialize(blocks, JsonOptions));
    }

    internal static DynamicFormSchemaDto FromLegacy(
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        string? blocksJson)
    {
        var blocks = DeserializeArray<DynamicFormBlockDto>(blocksJson);
        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(excelBlockJson))
        {
            var firstBlock = JsonSerializer.Deserialize<DynamicFormBlockDto>(excelBlockJson, JsonOptions);
            if (firstBlock is not null)
                blocks.Add(firstBlock);
        }

        return new DynamicFormSchemaDto
        {
            Sections = DeserializeArray<DynamicFormSectionDto>(sectionsJson),
            Fields = DeserializeArray<DynamicFormFieldDto>(fieldsJson),
            Blocks = blocks
        };
    }

    private static List<T> DeserializeArray<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
    }
}
