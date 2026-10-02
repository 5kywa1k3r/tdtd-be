using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.DynamicExcel;
using tdtd_be.OpenApi;

namespace tdtd_be.DTOs.DynamicForms;

public sealed record DynamicFormRow(
    string Id,
    string Code,
    string Name,
    string? Description,
    string[] TagCodes,
    int SchemaVersion,
    int VersionNo,
    string FamilyId,
    string? PreviousVersionId,
    string? ClonedFromVersionId,
    string LineageStatus,
    int Revision,
    bool IsActive,
    bool IsPublished,
    string? CreatedByUserId,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    bool CanMutate,
    bool CanClone,
    bool CanViewByCloneGrant,
    string? PublishedSchemaHash,
    DynamicFormActionCapabilities Actions
);

public sealed record DynamicFormActionCapabilities(
    bool CanRead,
    bool CanUpdate,
    bool CanDelete,
    bool CanPublish,
    bool CanCreateVersion,
    bool CanViewHistory,
    bool CanClone,
    bool CanImport,
    bool CanUpdateStatistics
);

public sealed record DynamicFormDetail(
    string Id,
    string Code,
    string Name,
    string? Description,
    string[] TagCodes,
    int SchemaVersion,
    int VersionNo,
    string FamilyId,
    string? PreviousVersionId,
    string? ClonedFromVersionId,
    string LineageStatus,
    int Revision,
    bool IsActive,
    bool IsPublished,
    string? CreatedByUserId,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? PublishedAtUtc,
    [property: DeprecatedSchemaProperty("Use schema.sections instead.")] string SectionsJson,
    [property: DeprecatedSchemaProperty("Use schema.fields instead.")] string FieldsJson,
    [property: DeprecatedSchemaProperty("Use schema.blocks[0] instead.")] string? ExcelBlockJson,
    [property: DeprecatedSchemaProperty("Use schema.blocks instead.")] string BlocksJson,
    string? ExcelBlockDynamicExcelTemplateId,
    DateTime? StatisticConfigUpdatedAtUtc,
    string? StatisticConfigUpdatedByUserId,
    string? StatisticConfigUpdateMonthKey,
    bool CanMutate,
    bool CanClone,
    bool CanViewByCloneGrant,
    string? PublishedSchemaSnapshotJson,
    string? PublishedSchemaHash,
    DynamicFormActionCapabilities Actions,
    DynamicFormSchemaDto? Schema = null
);

public sealed record CreateDynamicFormVersionReq(
    int? ExpectedRevision = null,
    string? Name = null,
    string? Description = null
);

public sealed record DynamicFormVersionHistoryResp(
    string FamilyId,
    string Code,
    IReadOnlyList<DynamicFormRow> Versions
);

public sealed record CreateDynamicFormReq(
    string? Code,
    string Name,
    string? Description,
    string[]? TagCodes,
    int? SchemaVersion,
    [property: DeprecatedSchemaProperty("Use schema.sections instead.")] string? SectionsJson,
    [property: DeprecatedSchemaProperty("Use schema.fields instead.")] string? FieldsJson,
    [property: DeprecatedSchemaProperty("Use schema.blocks[0] instead.")] string? ExcelBlockJson,
    [property: DeprecatedSchemaProperty("Use schema.blocks instead.")] string? BlocksJson,
    bool IsActive = true,
    DynamicFormSchemaDto? Schema = null
);

public sealed record UpdateDynamicFormReq(
    string Name,
    string? Description,
    string[]? TagCodes,
    int? SchemaVersion,
    [property: DeprecatedSchemaProperty("Use schema.sections instead.")] string? SectionsJson,
    [property: DeprecatedSchemaProperty("Use schema.fields instead.")] string? FieldsJson,
    [property: DeprecatedSchemaProperty("Use schema.blocks[0] instead.")] string? ExcelBlockJson,
    [property: DeprecatedSchemaProperty("Use schema.blocks instead.")] string? BlocksJson,
    bool IsActive = true,
    DynamicFormSchemaDto? Schema = null,
    int? ExpectedRevision = null
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormSchemaDto
{
    private List<DynamicFormSectionDto>? _sections = [];
    private List<DynamicFormFieldDto>? _fields = [];
    private List<DynamicFormBlockDto>? _blocks = [];
    public List<DynamicFormSectionDto>? Sections
    {
        get => _sections;
        init { _sections = value; SectionsSpecified = true; }
    }
    public List<DynamicFormFieldDto>? Fields
    {
        get => _fields;
        init { _fields = value; FieldsSpecified = true; }
    }
    public List<DynamicFormBlockDto>? Blocks
    {
        get => _blocks;
        init { _blocks = value; BlocksSpecified = true; }
    }
    [JsonIgnore]
    public bool SectionsSpecified { get; private set; }
    [JsonIgnore]
    public bool FieldsSpecified { get; private set; }
    [JsonIgnore]
    public bool BlocksSpecified { get; private set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NativeTablesVersion { get; init; }
    private List<DynamicFormNativeTableDto>? _tables;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DynamicFormNativeTableDto>? Tables
    {
        get => _tables;
        init { _tables = value; TablesSpecified = true; }
    }
    [JsonIgnore]
    public bool TablesSpecified { get; private set; }
}

public sealed record DynamicFormSectionDto
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string[]? TagCodes { get; init; }
    public int? Order { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record DynamicFormFieldDto
{
    public string? Id { get; init; }
    public string? SectionId { get; init; }
    public string? Key { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public bool? Required { get; init; }
    public int? ColSpan { get; init; }
    public int? MinHeight { get; init; }
    public int? CanvasX { get; init; }
    public int? CanvasY { get; init; }
    public int? CanvasW { get; init; }
    public int? CanvasH { get; init; }
    public int? Order { get; init; }
    public List<DynamicFormOptionDto>? Options { get; init; }
    public DynamicFormValueSourceDto? ValueSource { get; init; }
    public string[]? StatisticLabelCodes { get; init; }
    public bool? IsStatistic { get; init; }
    public bool? ShowOnOverview { get; init; }
    public JsonElement? Statistic { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record DynamicFormValueSourceDto
{
    public string? SourceType { get; init; }
    public string? LabelCode { get; init; }
    public string? LabelName { get; init; }
    public string? CatalogId { get; init; }
    public string? CatalogCode { get; init; }
    public string? CatalogName { get; init; }
    public List<DynamicFormOptionDto>? Options { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record DynamicFormOptionDto
{
    public string? Code { get; init; }
    public string? Label { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record DynamicFormBlockDto
{
    public string? BlockId { get; init; }
    public string? SectionId { get; init; }
    public string? TableMode { get; init; }
    public string? DynamicExcelTemplateId { get; init; }
    public string? DynamicExcelCode { get; init; }
    public string? DynamicExcelName { get; init; }
    public string? ExcelSpecKind { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record PublishDynamicFormReq(
    int? ExpectedRevision = null
);

public sealed record DynamicFormSearchReq(
    string? Q,
    string? Code,
    string? Name,
    string? CreatedBy,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    string[]? TagCodes,
    bool? IsActive,
    bool? IsPublished,
    int Page = 0,
    int PageSize = 10,
    string? SortField = "createdAtUtc",
    string? SortDirection = "desc"
);

public sealed record CloneDynamicFormReq(
    string? Code,
    string? Name
);

public sealed record WrapDynamicExcelAsFormReq(
    string DynamicExcelTemplateId,
    string? Code,
    string? Name,
    string? Description,
    string[]? TagCodes
);

public sealed record ImportDynamicExcelBlockReq(
    string DynamicExcelTemplateId,
    string? SectionId = null,
    int? ExpectedRevision = null
);

public sealed record DynamicFormExcelBlockSnapshot(
    string DynamicExcelTemplateId,
    string DynamicExcelCode,
    string DynamicExcelName,
    DynamicExcelDataRectDto DataRect,
    int W,
    int H,
    string BlockId = "excel_block",
    string? SectionId = null,
    string TableMode = "FIXED_GRID",
    DynamicFormTableIndexMapItem[]? IndexMap = null,
    string? ExcelSpecKind = null,
    string? DefaultDataType = null,
    JsonElement[]? DefaultOptions = null,
    JsonElement[]? DataTypeOverrides = null,
    JsonElement[]? SpecialRanges = null,
    bool? StatisticsDisabled = null,
    int? StatisticsInputCellCount = null,
    int? StatisticsInputCellLimit = null,
    string? StatisticsDisabledReason = null
);

public sealed record DynamicFormTableIndexMapItem(
    int Index,
    string RowKey,
    string ColumnKey,
    string MetricKey
);
