using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.OpenApi;

internal static class DynamicFormTypedSchemaContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Run()
    {
        TypedSchemaSerializesToLegacyStorageAndPreservesMetadata();
        TypedSchemaRejectsEveryLegacyJsonCompanion();
        LegacyOnlyInputPassesThroughUnchanged();
        OpenApiExposesTypedSchemaAndDeprecatesLegacyJson();
    }

    private static void TypedSchemaSerializesToLegacyStorageAndPreservesMetadata()
    {
        var schema = JsonSerializer.Deserialize<DynamicFormSchemaDto>(
            """
            {
              "sections": [
                {
                  "id": "section-1",
                  "title": "Main",
                  "order": 0,
                  "layoutMetadata": { "columns": 12 }
                }
              ],
              "fields": [
                {
                  "id": "field-1",
                  "sectionId": "section-1",
                  "name": "Status",
                  "type": "singleSelect",
                  "required": true,
                  "options": [
                    { "code": "OPEN", "label": "Open", "color": "green" }
                  ],
                  "valueSource": {
                    "sourceType": "FIXED_ENUM",
                    "options": [
                      { "code": "OPEN", "label": "Open", "color": "green" }
                    ],
                    "tenantScope": "CURRENT"
                  },
                  "helpText": "Choose one"
                }
              ],
              "blocks": [
                {
                  "blockId": "block-1",
                  "sectionId": "section-1",
                  "tableMode": "FIXED_GRID",
                  "metricRules": [
                    { "metricKey": "total", "aggregateOps": ["SUM"] }
                  ],
                  "visualMetadata": { "accent": "blue" }
                },
                {
                  "blockId": "block-2",
                  "sectionId": "section-1",
                  "tableMode": "APPEND_ROWS"
                }
              ]
            }
            """,
            JsonOptions) ?? throw new InvalidOperationException("Typed Dynamic Form schema should deserialize.");

        var legacy = DynamicFormSchemaAdapter.ToLegacy(schema);
        using var sections = JsonDocument.Parse(legacy.SectionsJson!);
        using var fields = JsonDocument.Parse(legacy.FieldsJson!);
        using var blocks = JsonDocument.Parse(legacy.BlocksJson!);
        using var primaryBlock = JsonDocument.Parse(legacy.ExcelBlockJson!);

        Require(sections.RootElement.GetArrayLength() == 1, "sectionsJson should contain typed sections");
        Require(fields.RootElement.GetArrayLength() == 1, "fieldsJson should contain typed fields");
        Require(blocks.RootElement.GetArrayLength() == 2, "blocksJson should contain every typed block");
        Require(
            primaryBlock.RootElement.GetProperty("blockId").GetString() == "block-1",
            "excelBlockJson should be derived from the first typed block");
        Require(
            fields.RootElement[0].GetProperty("helpText").GetString() == "Choose one",
            "field extension metadata should survive typed-to-legacy serialization");
        Require(
            fields.RootElement[0].GetProperty("options")[0].GetProperty("color").GetString() == "green",
            "option extension metadata should survive typed-to-legacy serialization");
        Require(
            fields.RootElement[0].GetProperty("valueSource").GetProperty("tenantScope").GetString() == "CURRENT",
            "value-source extension metadata should survive typed-to-legacy serialization");
        Require(
            blocks.RootElement[0].GetProperty("visualMetadata").GetProperty("accent").GetString() == "blue",
            "block extension metadata should survive typed-to-legacy serialization");

        var responseSchema = DynamicFormSchemaAdapter.FromLegacy(
            legacy.SectionsJson,
            legacy.FieldsJson,
            legacy.ExcelBlockJson,
            legacy.BlocksJson);
        Require(
            responseSchema.Sections![0].ExtensionData!["layoutMetadata"].GetProperty("columns").GetInt32() == 12,
            "response parsing should restore section extension metadata");
        Require(
            responseSchema.Fields![0].ExtensionData!["helpText"].GetString() == "Choose one",
            "response parsing should restore field extension metadata");
        Require(
            responseSchema.Fields[0].ValueSource!.ExtensionData!["tenantScope"].GetString() == "CURRENT",
            "response parsing should restore value-source extension metadata");
        Require(
            responseSchema.Fields[0].Options![0].ExtensionData!["color"].GetString() == "green",
            "response parsing should restore option extension metadata");
        Require(
            responseSchema.Blocks![0].ExtensionData!["metricRules"].GetArrayLength() == 1,
            "response parsing should restore nested block metadata");
    }

    private static void TypedSchemaRejectsEveryLegacyJsonCompanion()
    {
        var schema = new DynamicFormSchemaDto();
        var cases = new[]
        {
            new { Name = "sectionsJson", Sections = (string?)"", Fields = (string?)null, Excel = (string?)null, Blocks = (string?)null },
            new { Name = "fieldsJson", Sections = (string?)null, Fields = (string?)"[]", Excel = (string?)null, Blocks = (string?)null },
            new { Name = "excelBlockJson", Sections = (string?)null, Fields = (string?)null, Excel = (string?)"null", Blocks = (string?)null },
            new { Name = "blocksJson", Sections = (string?)null, Fields = (string?)null, Excel = (string?)null, Blocks = (string?)"[]" }
        };

        foreach (var item in cases)
        {
            try
            {
                DynamicFormSchemaAdapter.ResolveInput(
                    schema,
                    item.Sections,
                    item.Fields,
                    item.Excel,
                    item.Blocks);
                throw new InvalidOperationException($"Typed schema plus {item.Name} should be rejected.");
            }
            catch (AppException ex)
            {
                Require(
                    ex.Code == AppErrorCode.COMMON_VALIDATION_FAILED,
                    $"Typed schema plus {item.Name} should use COMMON_VALIDATION_FAILED");
                var details = JsonSerializer.SerializeToElement(ex.Details, JsonOptions);
                Require(
                    details.GetProperty("reason").GetString() == DynamicFormSchemaAdapter.AmbiguousReason,
                    $"Typed schema plus {item.Name} should expose the stable ambiguity reason");
                Require(
                    details.GetProperty("legacyProperties")[0].GetString() == item.Name,
                    $"Typed schema plus {item.Name} should identify the conflicting legacy property");
            }
        }
    }

    private static void LegacyOnlyInputPassesThroughUnchanged()
    {
        const string sectionsJson = "[ { \"id\": \"s1\" } ]";
        const string fieldsJson = "[ { \"id\": \"f1\" } ]";
        const string excelBlockJson = "{ \"blockId\": \"b1\" }";
        const string blocksJson = "[ { \"blockId\": \"b1\" } ]";

        var legacy = DynamicFormSchemaAdapter.ResolveInput(
            schema: null,
            sectionsJson,
            fieldsJson,
            excelBlockJson,
            blocksJson);

        Require(legacy.SectionsJson == sectionsJson, "legacy sectionsJson should pass through unchanged");
        Require(legacy.FieldsJson == fieldsJson, "legacy fieldsJson should pass through unchanged");
        Require(legacy.ExcelBlockJson == excelBlockJson, "legacy excelBlockJson should pass through unchanged");
        Require(legacy.BlocksJson == blocksJson, "legacy blocksJson should pass through unchanged");
    }

    private static void OpenApiExposesTypedSchemaAndDeprecatesLegacyJson()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "OpenApiContractTest"
        });
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(DynamicFormController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Dynamic Form typed schema contract test",
                Version = "v1"
            });
            options.SchemaFilter<DeprecatedSchemaPropertyFilter>();
        });

        using var app = builder.Build();
        var document = app.Services
            .GetRequiredService<ISwaggerProvider>()
            .GetSwagger("v1");

        var createSchema = ResolveRequestSchema(document, "/api/dynamic-forms", OperationType.Post);
        var updateSchema = ResolveRequestSchema(document, "/api/dynamic-forms/{id}", OperationType.Put);
        var detailSchema = ResolveResponseSchema(document, "/api/dynamic-forms/{id}", OperationType.Get, "200");

        foreach (var schema in new[] { createSchema, updateSchema, detailSchema })
        {
            Require(schema.Properties.ContainsKey("schema"), "Dynamic Form create/update/detail should expose schema");
            AssertDeprecated(schema, "sectionsJson");
            AssertDeprecated(schema, "fieldsJson");
            AssertDeprecated(schema, "excelBlockJson");
            AssertDeprecated(schema, "blocksJson");
        }

        var typedSchema = RequireComponent<DynamicFormSchemaDto>(document);
        AssertProperties(typedSchema, "sections", "fields", "blocks");

        var fieldSchema = RequireComponent<DynamicFormFieldDto>(document);
        AssertProperties(fieldSchema, "id", "sectionId", "type", "options", "valueSource");
        Require(!fieldSchema.Properties.ContainsKey("extensionData"), "field extension data must stay flattened on the wire");

        var valueSourceSchema = RequireComponent<DynamicFormValueSourceDto>(document);
        AssertProperties(valueSourceSchema, "sourceType", "options");
        Require(
            !valueSourceSchema.Properties.ContainsKey("extensionData"),
            "value-source extension data must stay flattened on the wire");

        var optionSchema = RequireComponent<DynamicFormOptionDto>(document);
        AssertProperties(optionSchema, "code", "label");
        Require(!optionSchema.Properties.ContainsKey("extensionData"), "option extension data must stay flattened on the wire");

        var blockSchema = RequireComponent<DynamicFormBlockDto>(document);
        AssertProperties(blockSchema, "blockId", "sectionId", "tableMode");
        Require(!blockSchema.Properties.ContainsKey("extensionData"), "block extension data must stay flattened on the wire");
    }

    private static OpenApiSchema ResolveRequestSchema(
        OpenApiDocument document,
        string path,
        OperationType operationType)
    {
        var operation = RequireOperation(document, path, operationType);
        var mediaType = operation.RequestBody?.Content
            .FirstOrDefault(item => item.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Value
            ?? operation.RequestBody?.Content.Values.FirstOrDefault();
        Require(mediaType?.Schema is not null, $"OpenAPI {operationType} {path} should expose a request schema");
        return ResolveSchema(document, mediaType!.Schema);
    }

    private static OpenApiSchema ResolveResponseSchema(
        OpenApiDocument document,
        string path,
        OperationType operationType,
        string statusCode)
    {
        var operation = RequireOperation(document, path, operationType);
        Require(operation.Responses.TryGetValue(statusCode, out var response), $"OpenAPI response missing: {statusCode}");
        var mediaType = response!.Content
            .FirstOrDefault(item => item.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Value
            ?? response.Content.Values.FirstOrDefault();
        Require(mediaType?.Schema is not null, $"OpenAPI {operationType} {path} should expose a response schema");
        return ResolveSchema(document, mediaType!.Schema);
    }

    private static OpenApiOperation RequireOperation(
        OpenApiDocument document,
        string path,
        OperationType operationType)
    {
        Require(document.Paths.TryGetValue(path, out var pathItem), $"OpenAPI path missing: {path}");
        Require(
            pathItem!.Operations.TryGetValue(operationType, out var operation),
            $"OpenAPI operation missing: {operationType} {path}");
        return operation!;
    }

    private static OpenApiSchema RequireComponent<T>(OpenApiDocument document)
    {
        Require(
            document.Components.Schemas.TryGetValue(typeof(T).Name, out var schema),
            $"OpenAPI component missing: {typeof(T).Name}");
        return schema!;
    }

    private static OpenApiSchema ResolveSchema(OpenApiDocument document, OpenApiSchema schema)
    {
        if (schema.Reference is null)
            return schema;

        Require(
            document.Components.Schemas.TryGetValue(schema.Reference.Id, out var resolved),
            $"OpenAPI schema reference missing: {schema.Reference.Id}");
        return resolved!;
    }

    private static void AssertDeprecated(OpenApiSchema schema, string propertyName)
    {
        Require(schema.Properties.TryGetValue(propertyName, out var property), $"OpenAPI property missing: {propertyName}");
        Require(property!.Deprecated, $"OpenAPI property should be deprecated: {propertyName}");
        Require(
            property.Description?.Contains("schema.", StringComparison.Ordinal) == true,
            $"Deprecated OpenAPI property should direct clients to schema: {propertyName}");
    }

    private static void AssertProperties(OpenApiSchema schema, params string[] names)
    {
        foreach (var name in names)
            Require(schema.Properties.ContainsKey(name), $"OpenAPI property missing: {name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
