using System.ComponentModel.DataAnnotations;

namespace tdtd_be.DTOs.Positions;

public sealed class CreatePositionRequest
{
    [Required, MinLength(1), MaxLength(80)]
    public string Code { get; set; } = default!;

    [Required, MinLength(1), MaxLength(200)]
    public string Name { get; set; } = default!;

    public int Order { get; set; }

    public int Rank { get; set; }

    [Required, MinLength(1)]
    public List<string> UnitTypeCodes { get; set; } = new();
}

public sealed class UpdatePositionRequest
{
    [Required, MinLength(1), MaxLength(200)]
    public string Name { get; set; } = default!;

    public int Order { get; set; }

    public int Rank { get; set; }

    [Required, MinLength(1)]
    public List<string> UnitTypeCodes { get; set; } = new();

    public string? Note { get; set; }
}

public sealed record PositionResponse(
    string Id,
    string Code,
    string Name,
    int Order,
    int Rank,
    List<string> UnitTypeCodes,
    bool IsDeleted,
    int Version,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);
