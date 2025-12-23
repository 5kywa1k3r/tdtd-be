//DTOs/Auth/SignUpRequest.cs
namespace tdtd_be.DTOs.Auth
{
    public record SignUpRequest(
        string Username,
        string Password,
        string FullName,
        List<string> UnitTypeId,
        string UnitId,
        string UnitName,
        List<string>? Roles,
        bool? IsActive
    );
}
