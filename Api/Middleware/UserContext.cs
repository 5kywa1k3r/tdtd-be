//Middleware/UserContext.cs
namespace tdtd_be.Middleware
{
    public sealed class UserContext
    {
        public string? UserId { get; set; }
        public string? FullName { get; set; }
        public List<String> UnitTypeId { get; set; } = [];
        public string? UnitId { get; set; }
        public string? UnitName { get; set; }
        public IReadOnlyList<string> Roles { get; set; } = [];
    }
}
