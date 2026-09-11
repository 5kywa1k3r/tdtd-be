using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace tdtd_be.OpenApi;

public sealed class DeprecatedSchemaPropertyFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        foreach (var property in context.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var propertyName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                               ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!schema.Properties.TryGetValue(propertyName, out var propertySchema))
                continue;

            var schemaType = property.GetCustomAttribute<SchemaPropertyTypeAttribute>();
            if (schemaType is not null)
            {
                propertySchema = context.SchemaGenerator.GenerateSchema(
                    schemaType.SchemaType,
                    context.SchemaRepository);
                schema.Properties[propertyName] = propertySchema;
            }

            var attribute = property.GetCustomAttribute<DeprecatedSchemaPropertyAttribute>();
            if (attribute is null)
                continue;

            propertySchema.Deprecated = true;
            if (!string.IsNullOrWhiteSpace(attribute.Description))
            {
                propertySchema.Description = string.IsNullOrWhiteSpace(propertySchema.Description)
                    ? attribute.Description
                    : $"{propertySchema.Description} {attribute.Description}";
            }
        }
    }
}
