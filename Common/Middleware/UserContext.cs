//Middleware/UserContext.cs
namespace tdtd_be.Common.Middleware
{
    public sealed class UserContext
    {
        public string? UserId { get; set; }
        public string? FullName { get; set; }
        public List<String> UnitTypeCodes { get; set; } = [];
        public string? UnitId { get; set; }
        public string? UnitCode { get; set; }
        public string? UnitName { get; set; }
        public IReadOnlyList<string> Roles { get; set; } = [];
        public string? PositionCode { get; set; }
        public string? AccountKind { get; set; }
    }
}
