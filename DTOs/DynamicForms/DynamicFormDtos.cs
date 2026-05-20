using System.Text.Json;
using tdtd_be.DTOs.DynamicExcel;

namespace tdtd_be.DTOs.DynamicForms;

public sealed record DynamicFormRow(
    string Id,
    string Code,
    string Name,
    string? Description,
    string[] TagCodes,
    int SchemaVersion,
    int VersionNo,
    bool IsActive,
    bool IsPublished,
    string? CreatedByUserId,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    bool CanMutate,
    bool CanClone,
    bool CanViewByCloneGrant
);

public sealed record DynamicFormDetail(
    string Id,
    string Code,
    string Name,
    string? Description,
    string[] TagCodes,
    int SchemaVersion,
    int VersionNo,
    bool IsActive,
    bool IsPublished,
    string? CreatedByUserId,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? PublishedAtUtc,
    string SectionsJson,
    string FieldsJson,
    string? ExcelBlockJson,
    string BlocksJson,
    string? ExcelBlockDynamicExcelTemplateId,
    DateTime? StatisticConfigUpdatedAtUtc,
    string? StatisticConfigUpdatedByUserId,
    string? StatisticConfigUpdateMonthKey,
    bool CanMutate,
    bool CanClone,
    bool CanViewByCloneGrant
);

public sealed record CreateDynamicFormReq(
    string? Code,
    string Name,
    string? Description,
    string[]? TagCodes,
    int? SchemaVersion,
    string? SectionsJson,
    string? FieldsJson,
    string? ExcelBlockJson,
    string? BlocksJson,
    bool IsActive = true
);

public sealed record UpdateDynamicFormReq(
    string Name,
    string? Description,
    string[]? TagCodes,
    int? SchemaVersion,
    string? SectionsJson,
    string? FieldsJson,
    string? ExcelBlockJson,
    string? BlocksJson,
    bool IsActive = true
);

public sealed record UpdateDynamicFormStatisticConfigReq(
    string? FieldsJson,
    string? ExcelBlockJson,
    string? BlocksJson
);

public sealed record DynamicFormStatisticConfigUpdateResp(
    DynamicFormDetail Template,
    string StatisticRebuildJobId,
    long QueuedReportCount,
    DateTime? StatisticRebuildScheduledAtUtc,
    bool StatisticRebuildRunsImmediately,
    DateTime? StatisticConfigUpdatedAtUtc,
    string? StatisticConfigUpdatedByUserId,
    string? StatisticConfigUpdateMonthKey
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
    string? SectionId = null
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
    JsonElement[]? DataTypeOverrides = null
);

public sealed record DynamicFormTableIndexMapItem(
    int Index,
    string RowKey,
    string ColumnKey,
    string MetricKey
);
