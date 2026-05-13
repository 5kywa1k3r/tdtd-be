using System.ComponentModel.DataAnnotations;

namespace tdtd_be.DTOs.Users.Admin;

public sealed class CreateUserRequest
{
    [Required, MinLength(3), MaxLength(64)]
    public string Username { get; set; } = default!;

    [Required, MinLength(6), MaxLength(128)]
    public string Password { get; set; } = default!;

    [Required, MinLength(1), MaxLength(128)]
    public string FullName { get; set; } = default!;

    public string? UnitId { get; set; }

    public List<string> Roles { get; set; } = new();

    [MaxLength(80)]
    public string? PositionCode { get; set; }
}

public sealed class UpdateUserRequest
{
    [MinLength(3), MaxLength(64)]
    public string? Username { get; set; }
    [Required, MinLength(1), MaxLength(128)]
    public string? FullName { get; set; } = default!;
    public List<string>? Roles { get; set; }
    public string? Note { get; set; }

    [MaxLength(80)]
    public string? PositionCode { get; set; }
}

public sealed class ResetPasswordRequest
{
    [Required, MinLength(6), MaxLength(128)]
    public string NewPassword { get; set; } = default!;
}

public sealed record UserResponse(
    string Id,
    string Username,
    string FullName,
    string? UnitId,
    string ? UnitSymbol,
    string? UnitName,
    string? UnitCode,
    List<string> Roles,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? PositionCode,
    string? PositionName = null,
    string? AccountKind = null
);
