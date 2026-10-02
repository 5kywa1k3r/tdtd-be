using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Common.Errors;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.DTOs.DynamicForms;

internal sealed record DynamicFormLegacySchemaJson(
    string? SectionsJson,
    string? FieldsJson,
    string? ExcelBlockJson,
    string? BlocksJson,
    int? NativeTablesVersion = null,
    string? TablesJson = null);

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

        if ((schema.TablesSpecified || schema.NativeTablesVersion.HasValue)
            && (schema.NativeTablesVersion is not (DynamicFormNativeTableDefinition.Version or DynamicFormNativeTableDefinition.ListVersion)
                || schema.Tables is null))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "NATIVE_TABLE_VERSION_OR_COLLECTION_INVALID", path = "schema.tables" });
        if (schema.NativeTablesVersion.HasValue && (!schema.SectionsSpecified || !schema.FieldsSpecified || !schema.BlocksSpecified
            || schema.Sections is null || schema.Fields is null || schema.Blocks is null))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "NATIVE_SCHEMA_COLLECTIONS_REQUIRED", path = "schema" });

        return new DynamicFormLegacySchemaJson(
            JsonSerializer.Serialize(sections, JsonOptions),
            JsonSerializer.Serialize(fields, JsonOptions),
            blocks.Count == 0 ? null : JsonSerializer.Serialize(blocks[0], JsonOptions),
            JsonSerializer.Serialize(blocks, JsonOptions),
            schema.NativeTablesVersion,
            schema.Tables is null ? null : JsonSerializer.Serialize(schema.Tables, JsonOptions));
    }

    internal static DynamicFormSchemaDto FromLegacy(
        string? sectionsJson,
        string? fieldsJson,
        string? excelBlockJson,
        string? blocksJson,
        int? nativeTablesVersion = null,
        string? tablesJson = null)
    {
        var blocks = DeserializeArray<DynamicFormBlockDto>(blocksJson);
        if (blocks.Count == 0 && !string.IsNullOrWhiteSpace(excelBlockJson))
        {
            var firstBlock = JsonSerializer.Deserialize<DynamicFormBlockDto>(excelBlockJson, JsonOptions);
            if (firstBlock is not null)
                blocks.Add(firstBlock);
        }

        var schema = new DynamicFormSchemaDto
        {
            Sections = DeserializeArray<DynamicFormSectionDto>(sectionsJson),
            Fields = DeserializeArray<DynamicFormFieldDto>(fieldsJson),
            Blocks = blocks
        };
        var tables = DynamicFormNativeTableDefinition.ReadStored(nativeTablesVersion, tablesJson);
        if (tables is null) return schema;
        DynamicFormNativeTableDefinition.Validate(tables, sectionsJson ?? "[]", fieldsJson ?? "[]",
            JsonSerializer.Serialize(blocks, JsonOptions), nativeTablesVersion);
        return schema with { NativeTablesVersion = nativeTablesVersion, Tables = tables };
    }

    private static List<T> DeserializeArray<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
    }
}
