//Options/CacheOptions.cs
namespace tdtd_be.Options
{
    public sealed class CacheOptions
    {
        public string Provider { get; init; } = "Redis"; // Redis | Memory (nếu muốn fallback sau)
        public string? RedisConnectionString { get; init; }
        public string? InstanceName { get; init; }
    }
}
