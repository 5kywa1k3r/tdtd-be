namespace tdtd_be.DTOs.Labels;

public sealed record LabelRow(
    string Id,
    string Code,
    string Name,
    string? Description,
    string? Color,
    string? GroupCode,
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
    string? ScopeType,
    string? ScopeId,
    bool IsActive = true
);

public sealed record UpdateLabelReq(
    string Name,
    string? Description,
    string? Color,
    string? GroupCode,
    bool IsActive = true
);
