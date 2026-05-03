using tdtd_be.DTOs.DynamicExcel;

namespace tdtd_be.DTOs.DynamicForms;

public sealed record DynamicFormRow(
    string Id,
    string Code,
    string Name,
    string? Description,
    string[] Labels,
    int SchemaVersion,
    int VersionNo,
    bool IsActive,
    bool IsPublished,
    string CreatedByUsername,
    DateTime CreatedAtUtc
);

public sealed record DynamicFormDetail(
    string Id,
    string Code,
    string Name,
    string? Description,
    string[] Labels,
    int SchemaVersion,
    int VersionNo,
    bool IsActive,
    bool IsPublished,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? PublishedAtUtc,
    string SectionsJson,
    string FieldsJson,
    string? ExcelBlockJson,
    string? ExcelBlockDynamicExcelTemplateId
);

public sealed record CreateDynamicFormReq(
    string? Code,
    string Name,
    string? Description,
    string[]? Labels,
    int? SchemaVersion,
    string? SectionsJson,
    string? FieldsJson,
    string? ExcelBlockJson,
    bool IsActive = true
);

public sealed record UpdateDynamicFormReq(
    string Name,
    string? Description,
    string[]? Labels,
    int? SchemaVersion,
    string? SectionsJson,
    string? FieldsJson,
    string? ExcelBlockJson,
    bool IsActive = true
);

public sealed record DynamicFormSearchReq(
    string? Q,
    string? Code,
    string? Name,
    string? CreatedBy,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    string[]? Labels,
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
    string[]? Labels
);

public sealed record DynamicFormExcelBlockSnapshot(
    string DynamicExcelTemplateId,
    string DynamicExcelCode,
    string DynamicExcelName,
    string RawWorkbookDataJson,
    string SpecJson,
    DynamicExcelDataRectDto DataRect,
    int W,
    int H,
    string BlockId = "excel_block",
    string TableMode = "FIXED_GRID",
    DynamicFormTableIndexMapItem[]? IndexMap = null
);

public sealed record DynamicFormTableIndexMapItem(
    int Index,
    string RowKey,
    string ColumnKey,
    string MetricKey
);
