using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicForms;

/// <summary>
/// Immutable, collection-independent view of one section in an exact Dynamic Form document.
/// </summary>
public sealed record DynamicFormSectionSnapshot(
    string DynamicFormTemplateId,
    string DynamicFormTemplateCode,
    string DynamicFormTemplateName,
    string SectionId,
    string Title,
    string? Description,
    string[] TagCodes,
    int Order,
    int SchemaVersion,
    string[] FieldIds,
    string[] BlockIds,
    string FieldsJson,
    string BlocksJson,
    string ContentHash);

public sealed class DynamicFormSectionSnapshotSet
{
    private readonly IReadOnlyDictionary<string, DynamicFormSectionSnapshot> _bySectionId;

    internal DynamicFormSectionSnapshotSet(
        string dynamicFormTemplateId,
        IReadOnlyList<DynamicFormSectionSnapshot> sections)
    {
        DynamicFormTemplateId = dynamicFormTemplateId;
        Sections = sections;
        _bySectionId = sections.ToDictionary(x => x.SectionId, StringComparer.Ordinal);
    }

    public string DynamicFormTemplateId { get; }
    public IReadOnlyList<DynamicFormSectionSnapshot> Sections { get; }

    public bool TryGetSection(string? sectionId, out DynamicFormSectionSnapshot snapshot)
    {
        var normalized = sectionId?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized) && _bySectionId.TryGetValue(normalized, out var found))
        {
            snapshot = found;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public DynamicFormSectionSnapshot GetRequiredSection(string? sectionId)
    {
        if (TryGetSection(sectionId, out var snapshot))
            return snapshot;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
            new
            {
                dynamicFormTemplateId = DynamicFormTemplateId,
                sectionId,
                reason = "DYNAMIC_FORM_SECTION_NOT_FOUND"
            });
    }
}

/// <summary>
/// Builds section snapshots directly from the exact DynamicFormTemplate payload.
/// No database reads or writes are performed.
/// </summary>
public static class DynamicFormSectionSnapshotBuilder
{
    public static DynamicFormSectionSnapshotSet Build(DynamicFormTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var sections = ParseArray(template, template.SectionsJson, "SectionsJson");
        var fields = ParseArray(template, template.FieldsJson, "FieldsJson");
        var blocks = ParseArray(template, template.BlocksJson, "BlocksJson");

        var sectionSeeds = ReadSections(template, sections);
        var sectionIds = sectionSeeds
            .Select(x => x.SectionId)
            .ToHashSet(StringComparer.Ordinal);
        var fieldsBySection = ReadMembersBySection(
            template,
            fields,
            "FieldsJson",
            "id",
            "sectionId",
            sectionIds);
        var blocksBySection = ReadMembersBySection(
            template,
            blocks,
            "BlocksJson",
            "blockId",
            "sectionId",
            sectionIds);

        var schemaVersion = Math.Max(1, template.SchemaVersion);
        var snapshots = sectionSeeds
            .OrderBy(x => x.Order)
            .ThenBy(x => x.SectionId, StringComparer.Ordinal)
            .Select(seed =>
            {
                var sectionFields = fieldsBySection[seed.SectionId];
                var sectionBlocks = blocksBySection[seed.SectionId];
                var fieldIds = sectionFields.Select(x => x.Id).ToArray();
                var blockIds = sectionBlocks.Select(x => x.Id).ToArray();
                var fieldsJson = CanonicalizeArray(sectionFields.Select(x => x.Element));
                var blocksJson = CanonicalizeArray(sectionBlocks.Select(x => x.Element));
                var sectionJson = Canonicalize(seed.Element);
                var contentHash = ComputeContentHash(
                    template.Id,
                    schemaVersion,
                    sectionJson,
                    fieldIds,
                    blockIds,
                    fieldsJson,
                    blocksJson);

                return new DynamicFormSectionSnapshot(
                    template.Id,
                    template.Code,
                    template.Name,
                    seed.SectionId,
                    seed.Title,
                    seed.Description,
                    seed.TagCodes,
                    seed.Order,
                    schemaVersion,
                    fieldIds,
                    blockIds,
                    fieldsJson,
                    blocksJson,
                    contentHash);
            })
            .ToArray();

        return new DynamicFormSectionSnapshotSet(template.Id, snapshots);
    }

    public static DynamicFormSectionSnapshot GetRequiredSection(
        DynamicFormTemplate template,
        string? sectionId)
        => Build(template).GetRequiredSection(sectionId);

    private static IReadOnlyList<SectionSeed> ReadSections(
        DynamicFormTemplate template,
        IReadOnlyList<JsonElement> sections)
    {
        var result = new List<SectionSeed>(sections.Count);
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sections.Count; index++)
        {
            var path = $"SectionsJson[{index}]";
            var section = RequireObject(template, sections[index], path);
            var sectionId = RequireString(template, section, "id", path);
            if (!knownIds.Add(sectionId))
                throw Invalid(template, $"{path}.id", "DYNAMIC_FORM_SECTION_ID_DUPLICATE", sectionId);

            var title = RequireString(template, section, "title", path);
            var description = ReadOptionalString(template, section, "description", path);
            var tagCodes = ReadOptionalStringArray(template, section, "tagCodes", path);
            var order = ReadOptionalInt(template, section, "order", path) ?? index;
            result.Add(new SectionSeed(
                sectionId,
                title,
                description,
                tagCodes,
                order,
                section.Clone()));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, List<MemberSeed>> ReadMembersBySection(
        DynamicFormTemplate template,
        IReadOnlyList<JsonElement> members,
        string payloadName,
        string idProperty,
        string sectionProperty,
        IReadOnlySet<string> knownSectionIds)
    {
        var result = knownSectionIds.ToDictionary(
            x => x,
            _ => new List<MemberSeed>(),
            StringComparer.Ordinal);
        var knownMemberIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < members.Count; index++)
        {
            var path = $"{payloadName}[{index}]";
            var member = RequireObject(template, members[index], path);
            var memberId = RequireString(template, member, idProperty, path);
            if (!knownMemberIds.Add(memberId))
                throw Invalid(template, $"{path}.{idProperty}", $"DYNAMIC_FORM_{MemberKind(payloadName)}_ID_DUPLICATE", memberId);

            var sectionId = RequireString(template, member, sectionProperty, path);
            if (!knownSectionIds.Contains(sectionId))
                throw Invalid(template, $"{path}.{sectionProperty}", $"DYNAMIC_FORM_{MemberKind(payloadName)}_SECTION_ORPHAN", sectionId);

            result[sectionId].Add(new MemberSeed(memberId, member.Clone()));
        }

        return result;
    }

    private static IReadOnlyList<JsonElement> ParseArray(
        DynamicFormTemplate template,
        string? json,
        string payloadName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw Invalid(template, payloadName, "DYNAMIC_FORM_SECTION_SNAPSHOT_JSON_REQUIRED", null);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw Invalid(template, payloadName, "DYNAMIC_FORM_SECTION_SNAPSHOT_ARRAY_REQUIRED", document.RootElement.ValueKind.ToString());

            return document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        catch (JsonException ex)
        {
            throw Invalid(template, payloadName, "DYNAMIC_FORM_SECTION_SNAPSHOT_JSON_INVALID", ex.Message, ex);
        }
    }

    private static JsonElement RequireObject(
        DynamicFormTemplate template,
        JsonElement value,
        string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(template, path, "DYNAMIC_FORM_SECTION_SNAPSHOT_OBJECT_REQUIRED", value.ValueKind.ToString());
        return value;
    }

    private static string RequireString(
        DynamicFormTemplate template,
        JsonElement obj,
        string propertyName,
        string path)
    {
        var property = FindProperty(template, obj, propertyName, path);
        if (!property.HasValue || property.Value.ValueKind != JsonValueKind.String)
            throw Invalid(template, $"{path}.{propertyName}", "DYNAMIC_FORM_SECTION_SNAPSHOT_STRING_REQUIRED", null);

        var value = property.Value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(template, $"{path}.{propertyName}", "DYNAMIC_FORM_SECTION_SNAPSHOT_STRING_REQUIRED", value);
        return value;
    }

    private static string? ReadOptionalString(
        DynamicFormTemplate template,
        JsonElement obj,
        string propertyName,
        string path)
    {
        var property = FindProperty(template, obj, propertyName, path);
        if (!property.HasValue || property.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (property.Value.ValueKind != JsonValueKind.String)
            throw Invalid(template, $"{path}.{propertyName}", "DYNAMIC_FORM_SECTION_SNAPSHOT_STRING_REQUIRED", null);

        var value = property.Value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string[] ReadOptionalStringArray(
        DynamicFormTemplate template,
        JsonElement obj,
        string propertyName,
        string path)
    {
        var property = FindProperty(template, obj, propertyName, path);
        if (!property.HasValue || property.Value.ValueKind == JsonValueKind.Null)
            return [];
        if (property.Value.ValueKind != JsonValueKind.Array)
            throw Invalid(template, $"{path}.{propertyName}", "DYNAMIC_FORM_SECTION_SNAPSHOT_STRING_ARRAY_REQUIRED", null);

        var result = new List<string>();
        var index = 0;
        foreach (var item in property.Value.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(value))
                throw Invalid(template, $"{path}.{propertyName}[{index}]", "DYNAMIC_FORM_SECTION_SNAPSHOT_STRING_REQUIRED", value);
            result.Add(value);
            index++;
        }

        return result.ToArray();
    }

    private static int? ReadOptionalInt(
        DynamicFormTemplate template,
        JsonElement obj,
        string propertyName,
        string path)
    {
        var property = FindProperty(template, obj, propertyName, path);
        if (!property.HasValue || property.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
            throw Invalid(template, $"{path}.{propertyName}", "DYNAMIC_FORM_SECTION_SNAPSHOT_INTEGER_REQUIRED", null);
        return value;
    }

    private static JsonElement? FindProperty(
        DynamicFormTemplate template,
        JsonElement obj,
        string propertyName,
        string path)
    {
        var matches = obj.EnumerateObject()
            .Where(x => string.Equals(x.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
            throw Invalid(template, $"{path}.{propertyName}", "DYNAMIC_FORM_SECTION_SNAPSHOT_PROPERTY_AMBIGUOUS", propertyName);
        return matches.Length == 0 ? null : matches[0].Value;
    }

    private static string CanonicalizeArray(IEnumerable<JsonElement> items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items)
                WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind {value.ValueKind}.");
        }
    }

    private static string ComputeContentHash(
        string templateId,
        int schemaVersion,
        string sectionJson,
        IReadOnlyList<string> fieldIds,
        IReadOnlyList<string> blockIds,
        string fieldsJson,
        string blocksJson)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("dynamicFormTemplateId", templateId);
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WritePropertyName("section");
            writer.WriteRawValue(sectionJson, skipInputValidation: true);
            writer.WritePropertyName("fieldIds");
            JsonSerializer.Serialize(writer, fieldIds);
            writer.WritePropertyName("blockIds");
            JsonSerializer.Serialize(writer, blockIds);
            writer.WritePropertyName("fields");
            writer.WriteRawValue(fieldsJson, skipInputValidation: true);
            writer.WritePropertyName("blocks");
            writer.WriteRawValue(blocksJson, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static AppException Invalid(
        DynamicFormTemplate template,
        string path,
        string reason,
        object? value,
        Exception? innerException = null)
        => new(
            AppErrorCode.DYNAMIC_FORM_SECTION_CONFIG_INVALID,
            new
            {
                dynamicFormTemplateId = template.Id,
                path,
                reason,
                value
            },
            innerException: innerException);

    private static string MemberKind(string payloadName)
        => string.Equals(payloadName, "FieldsJson", StringComparison.Ordinal)
            ? "FIELD"
            : "BLOCK";

    private sealed record SectionSeed(
        string SectionId,
        string Title,
        string? Description,
        string[] TagCodes,
        int Order,
        JsonElement Element);

    private sealed record MemberSeed(string Id, JsonElement Element);
}
