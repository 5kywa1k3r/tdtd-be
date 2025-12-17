//Dtos/Auth/TokenResponse.cs
namespace tdtd_be.DTOs.Auth
{
    public sealed record TokenResponse(string AccessToken, string RefreshToken);
}
