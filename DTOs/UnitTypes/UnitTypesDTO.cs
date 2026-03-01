using System.ComponentModel.DataAnnotations;

namespace tdtd_be.DTOs.UnitTypes;

public sealed class CreateUnitTypeRequest
{
    [Required, MinLength(1), MaxLength(50)]
    public string Code { get; set; } = default!;

    [Required, MinLength(1), MaxLength(200)]
    public string Name { get; set; } = default!;
}

public sealed class UpdateUnitTypeRequest
{
    [Required, MinLength(1), MaxLength(200)]
    public string Name { get; set; } = default!;

    public bool? IsDeleted { get; set; }
    public string? Note { get; set; }
}

public sealed record UnitTypeResponse(
    string Id,
    string Code,
    string Name,
    bool IsDeleted,
    int Version,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);