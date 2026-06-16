namespace tdtd_be.DTOs.DynamicExcel;

public sealed record DynamicExcelDataRectDto(int R0, int C0, int R1, int C1);

public sealed record DynamicExcelRow(
    string Id,
    string Code,
    string Name,
    string? HeaderKind,
    string TableMode,
    int ContractVersion,
    string CreatedByUsername,
    DateTime CreatedAtUtc
);

public sealed record DynamicExcelDetail(
    string Id,
    string Code,
    string Name,
    string? HeaderKind,
    string TableMode,
    int ContractVersion,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    string RawWorkbookDataJson,
    string SpecJson,
    DynamicExcelDataRectDto DataRect,
    int W,
    int H
);

public sealed record CreateDynamicExcelReq(
    string? Code,               // optional: if null => BE auto-generate
    string Name,
    string TableMode,
    int? ContractVersion,
    string RawWorkbookDataJson,
    string SpecJson,
    DynamicExcelDataRectDto? DataRect,
    int W,
    int H
);

public sealed record UpdateDynamicExcelReq(
    string Name,
    string? TableMode = null,
    int? ContractVersion = null,
    string? RawWorkbookDataJson = null,
    string? SpecJson = null,
    DynamicExcelDataRectDto? DataRect = null,
    int? W = null,
    int? H = null
);

public sealed record DynamicExcelSearchReq(
    string? Q,                   // search in code/name
    string? Code,
    string? Name,
    string? CreatedBy,           // search substring in createdByUsername
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    int Page = 0,
    int PageSize = 10,
    string? SortField = "createdAtUtc",
    string? SortDirection = "desc"
);

public sealed record NextCodeResp(string Prefix, int Year, int NextSeq, string NextCode);
