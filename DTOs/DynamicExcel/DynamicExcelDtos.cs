namespace tdtd_be.DTOs.DynamicExcel;

public sealed record DynamicExcelDataRectDto(int R0, int C0, int R1, int C1);

public sealed record DynamicExcelRow(
    string Id,
    string Code,
    string Name,
    string[] Labels,
    string CreatedByUsername,
    DateTime CreatedAtUtc
);

public sealed record DynamicExcelDetail(
    string Id,
    string Code,
    string Name,
    string[] Labels,
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
    string[]? Labels,
    string RawWorkbookDataJson,
    string SpecJson,
    DynamicExcelDataRectDto DataRect,
    int W,
    int H
);

public sealed record UpdateDynamicExcelReq(
    string Name,
    string[]? Labels,
    string RawWorkbookDataJson,
    string SpecJson,
    DynamicExcelDataRectDto DataRect,
    int W,
    int H
);

public sealed record DynamicExcelSearchReq(
    string? Q,                   // search in code/name
    string? Code,
    string? Name,
    string? CreatedBy,           // search substring in createdByUsername
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    string[]? Labels,            // any match
    int Page = 0,
    int PageSize = 10,
    string? SortField = "createdAtUtc",
    string? SortDirection = "desc"
);

public sealed record NextCodeResp(string Prefix, int Year, int NextSeq, string NextCode);