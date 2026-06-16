namespace tdtd_be.DTOs.Labels;

public sealed record LabelRow(
    string Id,
    string Code,
    string Name,
    string? Description,
    string? Color,
    string? GroupCode,
    string Usage,
    string DataType,
    string ValueSourceType,
    IReadOnlyList<LabelValueOptionDto> ValueOptions,
    string? ValueSourceCatalogId,
    string? ValueSourceCatalogCode,
    string? ValueSourceCatalogName,
    string ScopeType,
    string? ScopeId,
    bool IsSystem,
    bool IsActive,
    bool CanManage,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record LabelSearchReq(
    string? Q,
    string? Code,
    string? Name,
    string? GroupCode,
    string? Usage,
    string? ScopeType,
    string? ScopeId,
    bool? IsActive,
    int Page = 0,
    int PageSize = 20,
    string? SortField = "name",
    string? SortDirection = "asc"
);

public sealed record CreateLabelReq(
    string Code,
    string Name,
    string? Description,
    string? Color,
    string? GroupCode,
    string? Usage,
    string? DataType,
    string? ValueSourceType,
    IReadOnlyList<LabelValueOptionDto>? ValueOptions,
    string? ValueSourceCatalogId,
    string? ScopeType,
    string? ScopeId,
    bool IsActive = true
);

public sealed record UpdateLabelReq(
    string Name,
    string? Description,
    string? Color,
    string? GroupCode,
    string? Usage,
    string? DataType,
    string? ValueSourceType,
    IReadOnlyList<LabelValueOptionDto>? ValueOptions,
    string? ValueSourceCatalogId,
    bool IsActive = true
);

public sealed record LabelValueOptionDto(
    string Code,
    string Label
);

public sealed record LabelEnumOptionDto(
    string Code,
    string Label,
    int Order = 0,
    bool IsActive = true
);

public sealed record LabelEnumCatalogRow(
    string Id,
    string Code,
    string Name,
    string? Description,
    string ScopeType,
    string? ScopeId,
    string? ScopeUnitCode,
    int? ScopeLevel,
    int ActiveOptionCount,
    int TotalOptionCount,
    bool IsActive,
    bool CanManage,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record LabelEnumCatalogDetail(
    string Id,
    string Code,
    string Name,
    string? Description,
    string ScopeType,
    string? ScopeId,
    string? ScopeUnitCode,
    int? ScopeLevel,
    IReadOnlyList<LabelEnumOptionDto> Options,
    bool IsActive,
    bool CanManage,
    string CreatedByUsername,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record LabelEnumCatalogSearchReq(
    string? Q,
    string? Code,
    string? Name,
    string? ScopeType,
    string? ScopeId,
    bool? IsActive,
    int Page = 0,
    int PageSize = 20,
    string? SortField = "name",
    string? SortDirection = "asc"
);

public sealed record CreateLabelEnumCatalogReq(
    string? Code,
    string Name,
    string? Description,
    IReadOnlyList<LabelEnumOptionDto>? Options,
    string? ScopeType,
    string? ScopeId,
    bool IsActive = true
);

public sealed record UpdateLabelEnumCatalogReq(
    string Name,
    string? Description,
    IReadOnlyList<LabelEnumOptionDto>? Options,
    bool IsActive = true
);

public sealed record QuickCreateLabelEnumCatalogReq(
    string? Code,
    string Name,
    string? Description,
    string SourceFeature,
    string SourcePath,
    IReadOnlyList<LabelEnumOptionDto>? Options,
    string? ScopeType,
    string? ScopeId
);

public sealed record LabelEnumOptionPickRow(
    string Id,
    string CatalogId,
    string CatalogCode,
    string Code,
    string Label,
    int Order
);
