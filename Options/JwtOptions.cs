//Options/JwtOptions.cs
namespace tdtd_be.Options
{
    public sealed class JwtOptions
    {
        public string Issuer { get; init; } = null!;
        public string Audience { get; init; } = null!;
        public string Key { get; init; } = null!;
        public int AccessTokenMinutes { get; init; }
        public int RefreshTokenDays { get; init; }
    }
}
