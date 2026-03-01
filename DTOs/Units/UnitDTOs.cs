using System.ComponentModel.DataAnnotations;

namespace tdtd_be.DTOs.Units;

public sealed class CreateUnitRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public string FullName { get; set; } = default!;

    [MaxLength(300)]
    public string? ShortName { get; set; }
    [MaxLength(30)]
    public string? Symbol { get; set; }

    public string? ParentUnitId { get; set; } // null => create root

    // optional
    public List<string> UnitTypeCodes { get; set; } = new();
}

public sealed class UpdateUnitRequest
{
    [Required, MinLength(1), MaxLength(500)]
    public string FullName { get; set; } = default!;

    [MaxLength(300)]
    public string? ShortName { get; set; }
    [MaxLength(30)]
    public string? Symbol { get; set; }
    public List<string> UnitTypeCodes { get; set; } = new();

    public string? Note { get; set; }

    // move-subtree history option
    public bool SaveHistoryForWholeSubtree { get; set; } = false;
}

public sealed record UnitResponse(
    string Id,
    string FullName,
    string Code,
    string? ShortName,
    string? Symbol,
    int Level,
    int Version,
    string? ParentUnitId,
    List<string> UnitTypeCodes,
    string? Note,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? OldParentUnitId = null,
    string? OldCode = null
);

public sealed record UnitHistoryResponse(
    string Id,
    string UnitId,
    int Version,
    string FullName,
    string Code,
    string? ShortName,
    string? Symbol,
    int Level,
    string? ParentUnitId,
    List<string> UnitTypeCodes,
    DateTime CreatedAtUtc
);