namespace tdtd_be.OpenApi;

/// <summary>
/// Keeps the public OpenAPI shape of a property when its runtime CLR type is
/// intentionally raw so strict request validation can inspect every JSON key.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SchemaPropertyTypeAttribute(Type schemaType) : Attribute
{
    public Type SchemaType { get; } = schemaType;
}
