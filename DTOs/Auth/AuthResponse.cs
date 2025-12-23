//DTOs/Auth/AuthResponse.cs
using tdtd_be.Models;

namespace tdtd_be.DTOs.Auth
{
    public record AuthResponse(
        string AccessToken,
        int ExpiresInSeconds,
        MeResponse User
    );
}
