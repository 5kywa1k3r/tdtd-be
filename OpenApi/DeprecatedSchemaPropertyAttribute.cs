namespace tdtd_be.OpenApi;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DeprecatedSchemaPropertyAttribute : Attribute
{
    public DeprecatedSchemaPropertyAttribute(string? description = null)
    {
        Description = description;
    }

    public string? Description { get; }
}
